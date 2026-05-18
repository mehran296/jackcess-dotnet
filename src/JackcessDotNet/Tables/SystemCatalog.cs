using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Reads from and writes to the MSysObjects system catalog (TDEF always at page 2).
///
/// On table creation the following row is appended to the MSysObjects data page:
///   Id         = TDEF page number of the new table
///   Name       = table name
///   Type       = 1  (CatalogTypeTable)
///   DateCreate = now
///   DateUpdate = now
///   ParentId   = 0
///   Flags      = 0
///   (all other columns remain NULL)
/// </summary>
public sealed class SystemCatalog
{
    private readonly PageFile       _file;
    private readonly JetFormat      _format;
    private readonly DataPageWriter _writer;

    // Well-known MSysObjects column names
    private const string ColId         = "Id";
    private const string ColName       = "Name";
    private const string ColType       = "Type";
    private const string ColDateCreate = "DateCreate";
    private const string ColDateUpdate = "DateUpdate";
    private const string ColParentId   = "ParentId";
    private const string ColFlags      = "Flags";
    private const string ColLvProp     = "LvProp";

    public SystemCatalog(PageFile file)
    {
        _file   = file   ?? throw new ArgumentNullException(nameof(file));
        _format = file.Format;
        _writer = new DataPageWriter(file, new PageAllocator(file));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void CreateBaseCatalog()
    {
        // No-op: the embedded empty-database template already contains MSysObjects.
    }

    /// <summary>
    /// Appends a user-table entry to MSysObjects.
    /// </summary>
    public void InsertTableEntry(string tableName, int tdefPageNumber)
    {
        var catalogDef = BuildCatalogTableDef();

        var row = new Row
        {
            [ColId]         = tdefPageNumber,
            [ColName]       = tableName,
            [ColType]       = (short)JetFormat.CatalogTypeTable,
            [ColDateCreate] = DateTime.Now,
            [ColDateUpdate] = DateTime.Now,
            [ColParentId]   = 0,
            [ColFlags]      = 0
        };

        _writer.InsertRow(catalogDef, row);
        _writer.IncrementTdefRowCount(JetFormat.PageSystemCatalog);
    }

    // Flags stored on each MSysObjects row; matches Jackcess constants.
    private const int SystemObjectFlag    = unchecked((int)0x80000000);
    private const int AltSystemObjectFlag = 0x02;

    /// <summary>
    /// Loads the property bytes ("LvProp" column) for the MSysObjects row whose
    /// Id equals <paramref name="objectTdefPage"/>. Returns null for any row that
    /// has a null/empty LvProp value (most tables have none). The result is the
    /// raw blob; <see cref="PropertyMapReader"/> turns it into a <see cref="PropertyMaps"/>.
    /// </summary>
    public byte[]? GetPropertyBytesForObject(int objectTdefPage)
    {
        var catalogDef = BuildCatalogTableDef();
        var columns    = catalogDef.Columns;
        Column? colId    = columns.FirstOrDefault(c => c.Name.Equals(ColId,    StringComparison.OrdinalIgnoreCase));
        Column? colLvProp= columns.FirstOrDefault(c => c.Name.Equals(ColLvProp,StringComparison.OrdinalIgnoreCase));
        if (colId is null || colLvProp is null) return null;

        var decoder    = new RowDecoder(_format, columns);   // no LvalReader → we read raw LvRef ourselves
        var lvalReader = new LvalReader(_file);

        byte[] umapPage  = _file.ReadPage(catalogDef.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, catalogDef.OwnedPagesRow, _format, _file);

        foreach (int pageNum in ownedList)
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, _format.OffsetDataNumRows);

            for (int r = 0; r < rowCount; r++)
            {
                byte[]? rowBytes = ReadRowBytes(dp, r, _format);
                if (rowBytes is null) continue;

                object? idVal = decoder.Decode(rowBytes, colId);
                if (idVal is not int id || id != objectTdefPage) continue;

                // Find the LvProp column's variable-length payload manually so we
                // can pull the raw bytes (property bytes are arbitrary, not text).
                return ReadVariableColumnRawBytes(rowBytes, colLvProp, columns, lvalReader);
            }
        }
        return null;
    }

    /// <summary>
    /// Locates <paramref name="targetCol"/>'s slice within <paramref name="rowBytes"/>'
    /// variable-length area and, if it's a long-value (Memo/OLE) column with an
    /// OTHER_PAGE LvRef, follows the chain to assemble the full byte array.
    /// </summary>
    private byte[]? ReadVariableColumnRawBytes(
        byte[] rowBytes, Column targetCol,
        IReadOnlyList<Column> allColumns, LvalReader lvalReader)
    {
        if (!targetCol.DataType.IsVariableLength()) return null;
        if (rowBytes.Length < _format.SizeRowColumnCount) return null;

        int colCount = _format.SizeRowColumnCount == 2
            ? ByteUtil.GetShort(rowBytes, 0)
            : rowBytes[0];

        int maskSize   = (colCount + 7) / 8;
        int maskOffset = rowBytes.Length - maskSize;
        if (maskOffset < _format.SizeRowColumnCount) return null;

        bool notNull = (rowBytes[maskOffset + targetCol.ColumnNumber / 8]
                        & (1 << (targetCol.ColumnNumber % 8))) != 0;
        if (!notNull) return null;

        // Determine this column's var-index. Either trust the on-disk TDEF value
        // or compute by enumerating variable-length columns in column-number order.
        int varIdx = targetCol.VarLenTableIndex >= 0
            ? targetCol.VarLenTableIndex
            : CountPrecedingVarColumns(targetCol, allColumns);

        int sz = _format.SizeRowVarColOffset;
        int varCountOffset = maskOffset - sz;
        if (varCountOffset < 0) return null;
        int storedVarCount = sz == 2
            ? ByteUtil.GetShort(rowBytes, varCountOffset)
            : rowBytes[varCountOffset];
        if (varIdx >= storedVarCount) return null;

        int varStartOffset = maskOffset - (varIdx + 2) * sz;
        if (varStartOffset < 0) return null;
        int varStart = sz == 2 ? ByteUtil.GetShort(rowBytes, varStartOffset) : rowBytes[varStartOffset];
        int varEnd;
        if (varIdx + 1 < storedVarCount)
        {
            int nextOffset = maskOffset - (varIdx + 1 + 2) * sz;
            varEnd = sz == 2 ? ByteUtil.GetShort(rowBytes, nextOffset) : rowBytes[nextOffset];
        }
        else
        {
            int eodOffset = maskOffset - (storedVarCount + 2) * sz;
            if (eodOffset < 0) return null;
            varEnd = sz == 2 ? ByteUtil.GetShort(rowBytes, eodOffset) : rowBytes[eodOffset];
        }

        int varLen = varEnd - varStart;
        if (varLen < 4 || varStart < 0 || varEnd > maskOffset) return null;

        // Access LvRef header (12 bytes total in the row's var area, per Jackcess):
        //   bytes 0..3 = lengthWithFlags (LE int): high byte = type, low 24 bits = length
        //   THIS_PAGE   (type 0x80): bytes 4..11 reserved, then inline data at byte 12
        //   OTHER_PAGE  (type 0x40): byte 4 = rowNum, bytes 5..7 = page (LE 3-byte)
        //   OTHER_PAGES (type 0x00): same first 4 bytes, but chunks chain across pages
        if (varLen < 4) return null;
        int lenWithFlags = ByteUtil.GetInt(rowBytes, varStart);
        byte typeFlag   = (byte)((uint)lenWithFlags >> 24);
        int  dataLen    = lenWithFlags & 0x00FFFFFF;
        if (typeFlag == 0x80)
        {
            // Inline data starts 12 bytes into the LvRef.
            int avail = varLen - 12;
            if (avail <= 0) return Array.Empty<byte>();
            int actualLen = Math.Min(dataLen, avail);
            var inline = new byte[actualLen];
            Array.Copy(rowBytes, varStart + 12, inline, 0, actualLen);
            return inline;
        }
        if ((typeFlag == 0x40 || typeFlag == 0x00) && varLen >= 8)
        {
            int lvalRow  = rowBytes[varStart + 4];
            int lvalPage = rowBytes[varStart + 5]
                         | (rowBytes[varStart + 6] << 8)
                         | (rowBytes[varStart + 7] << 16);
            return typeFlag == 0x40
                ? ReadAccessLvalSingleChunk(lvalPage, lvalRow, dataLen)
                : ReadAccessLvalChain      (lvalPage, lvalRow, dataLen);
        }
        return null;
    }

    /// <summary>
    /// Single-chunk OTHER_PAGE read: the entire value lives in one row on an LVAL page.
    /// Row layout: chunk data starts at the row's first byte and runs to rowEnd.
    /// </summary>
    private byte[] ReadAccessLvalSingleChunk(int pageNum, int rowNum, int dataLen)
    {
        byte[] page = _file.ReadPage(pageNum);
        int rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);
        if (rowNum >= rowCount) return Array.Empty<byte>();

        int slotOff  = _format.OffsetDataRowTable + rowNum * JetFormat.SizeRowEntry;
        int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
        int rowEnd   = (rowNum == 0)
            ? _format.PageSize
            : (ByteUtil.GetUShort(page,
                   _format.OffsetDataRowTable + (rowNum - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);
        int rowLen = rowEnd - rowStart;
        if (rowLen <= 0) return Array.Empty<byte>();

        int copyLen = Math.Min(dataLen, rowLen);
        var result = new byte[copyLen];
        Array.Copy(page, rowStart, result, 0, copyLen);
        return result;
    }

    /// <summary>
    /// Chunk-chain OTHER_PAGES read: each chunk row begins with a 4-byte
    /// (1-byte nextRow + 3-byte LE nextPage) header, followed by data bytes to rowEnd.
    /// </summary>
    private byte[] ReadAccessLvalChain(int pageNum, int rowNum, int dataLen)
    {
        var result = new byte[dataLen];
        int written = 0;

        while (written < dataLen)
        {
            byte[] page = _file.ReadPage(pageNum);
            int rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);
            if (rowNum >= rowCount) break;

            int slotOff  = _format.OffsetDataRowTable + rowNum * JetFormat.SizeRowEntry;
            int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
            int rowEnd   = (rowNum == 0)
                ? _format.PageSize
                : (ByteUtil.GetUShort(page,
                       _format.OffsetDataRowTable + (rowNum - 1) * JetFormat.SizeRowEntry)
                   & JetFormat.RowOffsetMask);
            int rowLen = rowEnd - rowStart;
            if (rowLen < 4) break;

            // Header at the start of the chunk row: nextRow + nextPage(3LE)
            int nextRow  = page[rowStart];
            int nextPage = page[rowStart + 1]
                         | (page[rowStart + 2] << 8)
                         | (page[rowStart + 3] << 16);

            int chunkLen = rowLen - 4;
            int copyLen  = Math.Min(chunkLen, dataLen - written);
            Array.Copy(page, rowStart + 4, result, written, copyLen);
            written += copyLen;

            if (nextPage == 0 || (nextPage == 0 && nextRow == 0)) break;
            pageNum = nextPage;
            rowNum  = nextRow;
        }
        return result;
    }

    private static int CountPrecedingVarColumns(Column target, IReadOnlyList<Column> all)
    {
        int idx = 0;
        foreach (var c in all)
        {
            if (!c.DataType.IsVariableLength()) continue;
            if (c.ColumnNumber == target.ColumnNumber) return idx;
            idx++;
        }
        return idx;
    }

    /// <summary>
    /// Returns the names of all user tables (or, with <paramref name="includeSystem"/>,
    /// all tables including MSys*). Linked tables are not yet distinguished.
    /// </summary>
    public IReadOnlyList<string> GetTableNames(bool includeSystem)
    {
        var result = new List<string>();
        ForEachTableEntry((name, _, flags) =>
        {
            bool isSystem = (flags & (SystemObjectFlag | AltSystemObjectFlag)) != 0;
            if (isSystem && !includeSystem) return;
            result.Add(name);
        });
        return result;
    }

    /// <summary>
    /// Scans MSysObjects data pages and returns the TDEF page number for
    /// <paramref name="tableName"/>, or -1 if not found.
    /// </summary>
    public int FindTableTdefPage(string tableName)
    {
        int found = -1;
        ForEachTableEntry((name, tdefPage, _) =>
        {
            if (found < 0 && string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase))
                found = tdefPage;
        });
        return found;
    }

    /// <summary>
    /// Iterates every Type=1 row in MSysObjects and invokes <paramref name="visit"/>
    /// with (name, tdefPageNumber, flags). Used by both FindTableTdefPage and GetTableNames.
    /// </summary>
    private void ForEachTableEntry(Action<string, int, int> visit)
    {
        var catalogDef = BuildCatalogTableDef();
        var columns    = catalogDef.Columns;
        var decoder    = new RowDecoder(_format, columns);

        Column? colId    = columns.FirstOrDefault(c => c.Name.Equals(ColId,    StringComparison.OrdinalIgnoreCase));
        Column? colName  = columns.FirstOrDefault(c => c.Name.Equals(ColName,  StringComparison.OrdinalIgnoreCase));
        Column? colType  = columns.FirstOrDefault(c => c.Name.Equals(ColType,  StringComparison.OrdinalIgnoreCase));
        Column? colFlags = columns.FirstOrDefault(c => c.Name.Equals(ColFlags, StringComparison.OrdinalIgnoreCase));

        if (colId is null || colName is null || colType is null)
            throw new InvalidOperationException(
                "Could not locate required columns (Id, Name, Type) in MSysObjects TDEF.");

        byte[] umapPage  = _file.ReadPage(catalogDef.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, catalogDef.OwnedPagesRow, _format, _file);

        foreach (int pageNum in ownedList)
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, _format.OffsetDataNumRows);

            for (int r = 0; r < rowCount; r++)
            {
                byte[]? rowBytes = ReadRowBytes(dp, r, _format);
                if (rowBytes is null) continue;

                object? typeVal = decoder.Decode(rowBytes, colType);
                if (typeVal is not short typeShort || typeShort != JetFormat.CatalogTypeTable)
                    continue;

                object? nameVal = decoder.Decode(rowBytes, colName);
                if (nameVal is not string name) continue;

                object? idVal = decoder.Decode(rowBytes, colId);
                if (idVal is not int id) continue;

                int flags = 0;
                if (colFlags is not null && decoder.Decode(rowBytes, colFlags) is int f)
                    flags = f;

                visit(name, id, flags);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private TableDefinition BuildCatalogTableDef()
    {
        byte[] tdefPage = _file.ReadPage(JetFormat.PageSystemCatalog);
        var    info     = TdefReader.Read(tdefPage, _format);

        return new TableDefinition("MSysObjects", info.Columns)
        {
            TdefPageNumber = JetFormat.PageSystemCatalog,
            UmapPageNumber = info.OwnedPagesUmapPage,
            OwnedPagesRow  = info.OwnedPagesUmapRow,
            FreeSpaceRow   = info.FreeSpaceUmapRow,
        };
    }

    /// <summary>
    /// Returns the raw bytes of row <paramref name="rowNum"/> from a data page,
    /// or <c>null</c> if the row is deleted or an overflow pointer.
    /// </summary>
    private static byte[]? ReadRowBytes(byte[] page, int rowNum, JetFormat format)
    {
        int slotValue = ByteUtil.GetUShort(page,
            format.OffsetDataRowTable + rowNum * JetFormat.SizeRowEntry);

        if ((slotValue & 0x8000) != 0) return null;   // deleted row
        if ((slotValue & 0x4000) != 0) return null;   // overflow pointer (not supported)

        int rowStart = slotValue & JetFormat.RowOffsetMask;

        // Row length: distance from this row's start to the previous row's start
        // (or end of page for row 0, which has the highest slot value = last appended).
        int rowEnd = (rowNum == 0)
            ? format.PageSize
            : (ByteUtil.GetUShort(page,
                   format.OffsetDataRowTable + (rowNum - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);

        int rowLen = rowEnd - rowStart;
        if (rowLen <= 0) return null;

        var bytes = new byte[rowLen];
        Array.Copy(page, rowStart, bytes, 0, rowLen);
        return bytes;
    }

    // ── Obsolete stubs kept for API compat ────────────────────────────────────

    public void InsertTableEntry(string name)
        => throw new NotSupportedException(
               "Use InsertTableEntry(string tableName, int tdefPageNumber) instead.");

    public void InsertColumnEntry(string tableName, Column column)
        => throw new NotSupportedException(
               "Column metadata is stored directly in the table's TDEF page, " +
               "not in a separate system catalog table.");

    public void InsertIndexEntry(string tableName, string indexName, bool isPrimaryKey)
        => throw new NotSupportedException(
               "Index metadata is stored directly in the table's TDEF page, " +
               "not in a separate system catalog table.");
}
