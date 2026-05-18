using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Writes rows into Jet data pages (page type 0x01).
///
/// Data-page slot-array layout:
///   Bytes  0..13  – header (page type, free-space, tdef-page#, row-count)
///   Bytes 14..    – row slot table, 2 bytes per slot, grows forward
///   … free space …
///   …             – row data, packed from end of page, grows backward
///
/// Slot N value = absolute byte-offset of row N's data inside the page.
/// Row 0 is the first row inserted and is placed at the highest address.
/// Row N (N>0) is placed immediately before row N-1.
///
/// Free-space formula:
///   freeSpace = row1Start − ( OffsetDataRowTable + rowCount × SizeRowEntry )
///             = minSlotValue − headerUsed
/// Equivalently:
///   cursor (next write position) = OffsetDataRowTable + rowCount×2 + freeSpace
///   newRowStart = cursor − rowSize
/// </summary>
public sealed class DataPageWriter
{
    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;

    public DataPageWriter(PageFile file, PageAllocator allocator)
    {
        _file      = file      ?? throw new ArgumentNullException(nameof(file));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes <paramref name="row"/> and appends it to the best available data page
    /// for <paramref name="tableDef"/>.  Allocates a new data page if needed and
    /// registers it in the table's owned-pages usage-map.
    /// </summary>
    /// <returns>
    /// A packed row-pointer: <c>pageNumber &lt;&lt; 16 | rowIndexOnPage</c>.
    /// </returns>
    public int InsertRow(TableDefinition tableDef, Row row)
    {
        if (tableDef is null) throw new ArgumentNullException(nameof(tableDef));
        if (row      is null) throw new ArgumentNullException(nameof(row));

        var format = _file.Format;

        // Build per-column LvalWriters for any Memo/OLE columns that have a umap page.
        Dictionary<string, LvalWriter>? lvalWriters = null;
        foreach (var col in tableDef.Columns)
        {
            if (col.DataType.IsLongValue() &&
                tableDef.LvalColumnUmapPages.TryGetValue(col.Name, out int umapPage))
            {
                lvalWriters ??= new Dictionary<string, LvalWriter>(StringComparer.OrdinalIgnoreCase);
                lvalWriters[col.Name] = new LvalWriter(_file, _allocator, umapPage);
            }
        }

        var    encoder = new RowEncoder(format, tableDef.Columns, lvalWriters);
        byte[] rowData = encoder.Encode(row);

        int dataPage   = FindOrAllocateDataPage(tableDef, rowData.Length, format);
        int rowNum     = WriteRowOnPage(dataPage, rowData, tableDef.TdefPageNumber, format);

        return (dataPage << 16) | rowNum;
    }

    /// <summary>
    /// Updates the total-row-count field inside the table's TDEF page by adding
    /// <paramref name="delta"/> to the existing value.
    /// </summary>
    public void IncrementTdefRowCount(int tdefPageNumber, int delta = 1)
    {
        var format    = _file.Format;
        var tdefPage  = _file.ReadPage(tdefPageNumber);
        int current   = ByteUtil.GetInt(tdefPage, format.TdefOffsetNumRows);
        ByteUtil.PutInt(tdefPage, format.TdefOffsetNumRows, current + delta);
        _file.WritePage(tdefPageNumber, tdefPage);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private int FindOrAllocateDataPage(TableDefinition tableDef, int rowDataSize, JetFormat format)
    {
        int needed = rowDataSize + JetFormat.SizeRowEntry;   // data bytes + one slot

        // Consult the owned-pages usage-map for existing pages with enough room.
        byte[] umapPage  = _file.ReadPage(tableDef.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, tableDef.OwnedPagesRow, format, _file);

        foreach (int pageNum in ownedList)
        {
            var   dp        = _file.ReadPage(pageNum);
            short freeSpace = ByteUtil.GetShort(dp, JetFormat.OffsetDataFreeSpace);
            if (freeSpace >= needed)
                return pageNum;
        }

        // Allocate a fresh data page and register it in the usage-map.
        int newPage = _allocator.AllocateDataPage(tableDef.TdefPageNumber);
        umapPage = _file.ReadPage(tableDef.UmapPageNumber);  // re-read (may have been evicted)
        UsageMap.AddPage(umapPage, tableDef.OwnedPagesRow, newPage, format);
        _file.WritePage(tableDef.UmapPageNumber, umapPage);

        return newPage;
    }

    private int WriteRowOnPage(int pageNumber, byte[] rowData, int tdefPageNumber, JetFormat format)
    {
        byte[] page      = _file.ReadPage(pageNumber);
        int    rowCount  = ByteUtil.GetShort(page, format.OffsetDataNumRows);
        int    freeSpace = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
        int    needed    = rowData.Length + JetFormat.SizeRowEntry;

        if (freeSpace < needed)
            throw new InvalidOperationException(
                $"Data page {pageNumber} has only {freeSpace} free bytes; row needs {needed}.");

        // cursor = first byte of the free gap (grows up), which also equals the
        // left-edge of the already-written data area when rearranged:
        //   cursor = OffsetDataRowTable + rowCount×2 + freeSpace
        int cursor      = format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry + freeSpace;
        int rowStart    = cursor - rowData.Length;

        // Copy row data into the page
        Array.Copy(rowData, 0, page, rowStart, rowData.Length);

        // Write the slot entry for this row
        int slotOffset  = format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry;
        ByteUtil.PutShort(page, slotOffset, (short)rowStart);

        // Update header
        ByteUtil.PutShort(page, format.OffsetDataNumRows,   (short)(rowCount  + 1));
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)(freeSpace - needed));

        _file.WritePage(pageNumber, page);
        return rowCount;   // 0-based row index on this page
    }

    // ── UpdateRowByPrimaryKey ─────────────────────────────────────────────────

    /// <summary>
    /// Finds the row whose primary-key column equals <paramref name="primaryKeyValue"/>
    /// (linear scan of all owned data pages), merges <paramref name="newValues"/> into it,
    /// marks the old row as deleted, and re-inserts the merged row.
    /// Returns the packed rowPtr of the new row: <c>pageNumber &lt;&lt; 16 | rowIndex</c>.
    /// </summary>
    public int UpdateRowByPrimaryKey(TableDefinition table, object primaryKeyValue, Row newValues)
    {
        if (table    is null) throw new ArgumentNullException(nameof(table));
        if (newValues is null) throw new ArgumentNullException(nameof(newValues));

        if (table.PrimaryKeyColumnName is null)
            throw new InvalidOperationException(
                "PrimaryKeyColumnName is not set on this TableDefinition. " +
                "Specify a primary key column when calling Database.CreateTable.");

        var format  = _file.Format;
        var columns = table.Columns;
        var lvalReader = table.LvalColumnUmapPages.Count > 0
            ? new LvalReader(_file)
            : null;
        var decoder = new RowDecoder(format, columns, lvalReader);

        // Locate the primary-key column descriptor.
        Column? pkCol = null;
        foreach (var col in columns)
        {
            if (col.Name.Equals(table.PrimaryKeyColumnName, StringComparison.OrdinalIgnoreCase))
            {
                pkCol = col;
                break;
            }
        }
        if (pkCol is null)
            throw new InvalidOperationException(
                $"Primary key column '{table.PrimaryKeyColumnName}' not found in table '{table.Name}'.");

        byte[] umapPage  = _file.ReadPage(table.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, table.OwnedPagesRow, format, _file);

        foreach (int pageNum in ownedList)
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, format.OffsetDataNumRows);

            for (int r = 0; r < rowCount; r++)
            {
                int    slotOff = format.OffsetDataRowTable + r * JetFormat.SizeRowEntry;
                ushort slotVal = ByteUtil.GetUShort(dp, slotOff);

                if ((slotVal & 0x8000) != 0) continue;   // deleted
                if ((slotVal & 0x4000) != 0) continue;   // overflow pointer

                int rowStart = slotVal & JetFormat.RowOffsetMask;
                int rowEnd   = (r == 0)
                    ? format.PageSize
                    : (ByteUtil.GetUShort(dp, format.OffsetDataRowTable + (r - 1) * JetFormat.SizeRowEntry)
                       & JetFormat.RowOffsetMask);
                int rowLen = rowEnd - rowStart;
                if (rowLen <= 0) continue;

                var rowBytes = new byte[rowLen];
                Array.Copy(dp, rowStart, rowBytes, 0, rowLen);

                object? pkVal = decoder.Decode(rowBytes, pkCol);
                if (pkVal is null || !PrimaryKeysEqual(pkVal, primaryKeyValue)) continue;

                // Build the merged row: existing values overlaid by newValues.
                var merged = new Row();
                foreach (var col in columns)
                {
                    object? existing = decoder.Decode(rowBytes, col);
                    if (existing is not null)
                        merged[col.Name] = existing;
                }
                foreach (var kvp in newValues)
                    merged[kvp.Key] = kvp.Value;

                // Free any LVAL chains referenced by the old row before overwriting.
                FreeRowLvalChains(rowBytes, decoder);

                // Mark the original slot as deleted (bit 15).
                ByteUtil.PutUShort(dp, slotOff, (ushort)(slotVal | 0x8000u));
                _file.WritePage(pageNum, dp);

                // Insert the merged row and propagate its rowPtr so the caller can
                // update any PK index that points at the old (now-deleted) row.
                return InsertRow(table, merged);
            }
        }

        throw new InvalidOperationException(
            $"No row with primary key '{primaryKeyValue}' found in table '{table.Name}'.");
    }

    // ── DeleteRow ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the first row where <paramref name="columnName"/> equals <paramref name="value"/>
    /// (linear scan), frees its LVAL chains, and marks the slot as deleted.
    /// The caller is responsible for decrementing the TDEF row count.
    /// </summary>
    public void DeleteRow(TableDefinition table, string columnName, object value)
    {
        if (table is null)      throw new ArgumentNullException(nameof(table));
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));

        Column? targetCol = null;
        foreach (var col in table.Columns)
        {
            if (col.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                targetCol = col;
                break;
            }
        }
        if (targetCol is null)
            throw new InvalidOperationException(
                $"Column '{columnName}' not found in table '{table.Name}'.");

        var format     = _file.Format;
        var lvalReader = table.LvalColumnUmapPages.Count > 0 ? new LvalReader(_file) : null;
        var decoder    = new RowDecoder(format, table.Columns, lvalReader);

        byte[] umapPage  = _file.ReadPage(table.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, table.OwnedPagesRow, format, _file);

        foreach (int pageNum in ownedList)
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, format.OffsetDataNumRows);

            for (int r = 0; r < rowCount; r++)
            {
                int    slotOff = format.OffsetDataRowTable + r * JetFormat.SizeRowEntry;
                ushort slotVal = ByteUtil.GetUShort(dp, slotOff);

                if ((slotVal & 0x8000) != 0) continue;   // already deleted
                if ((slotVal & 0x4000) != 0) continue;   // overflow pointer

                int rowStart = slotVal & JetFormat.RowOffsetMask;
                int rowEnd   = (r == 0)
                    ? format.PageSize
                    : (ByteUtil.GetUShort(dp,
                           format.OffsetDataRowTable + (r - 1) * JetFormat.SizeRowEntry)
                       & JetFormat.RowOffsetMask);
                int rowLen = rowEnd - rowStart;
                if (rowLen <= 0) continue;

                var rowBytes = new byte[rowLen];
                Array.Copy(dp, rowStart, rowBytes, 0, rowLen);

                object? colVal = decoder.Decode(rowBytes, targetCol);
                if (!PrimaryKeysEqual(colVal, value)) continue;

                // Free any LVAL chains referenced by this row.
                FreeRowLvalChains(rowBytes, decoder);

                // Mark the slot as deleted.
                ByteUtil.PutUShort(dp, slotOff, (ushort)(slotVal | 0x8000u));
                _file.WritePage(pageNum, dp);
                return;
            }
        }

        throw new InvalidOperationException(
            $"No row with {columnName} = '{value}' found in table '{table.Name}'.");
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    // Frees every OTHER_PAGE LVAL chain referenced by the given row bytes.
    private void FreeRowLvalChains(byte[] rowBytes, RowDecoder decoder)
    {
        var lvalFree = new LvalFree(_file);
        foreach (var (lvalPage, lvalRow) in decoder.GetOtherPageLvRefs(rowBytes))
            lvalFree.FreeChain(lvalPage, lvalRow);
    }

    private static bool PrimaryKeysEqual(object? stored, object? requested)
    {
        if (Equals(stored, requested)) return true;
        if (stored is null || requested is null) return false;
        try { return Convert.ToDecimal(stored) == Convert.ToDecimal(requested); }
        catch { return false; }
    }
}
