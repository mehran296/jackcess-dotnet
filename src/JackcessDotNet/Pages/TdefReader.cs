using System.Text;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Parses the binary content of a Table-Definition (TDEF) page and extracts the
/// column list plus the Usage-Map references.
///
/// TDEF page layout (Jet4 / Access 2000+):
///   [0..62]  TDEF header   (SizeTdefHeader = 63 bytes)
///   [63..]   Index row-count blocks  (numIndexes × SizeIndexDefinition)  ← before columns
///   [..]     Column definitions      (numCols    × 25 bytes)
///   [..]     Index column blocks     (numIndexes × SizeIndexColumnBlock)  ← after columns
///   [..]     Logical-index info      (numLogicalIndexes × SizeIndexInfoBlock)
///   [..]     Logical-index names     (numLogicalIndexes × (2 + name_bytes))
///   [..]     Column names            (numCols            × (2 + name_bytes))
///   [..]     TDEF trailer            (0xFF 0xFF)
/// </summary>
internal static class TdefReader
{
    public readonly record struct TdefInfo(
        IReadOnlyList<Column> Columns,
        int TotalRowCount,
        int OwnedPagesUmapPage,
        int OwnedPagesUmapRow,
        int FreeSpaceUmapPage,
        int FreeSpaceUmapRow,
        IReadOnlyDictionary<string, int> LvalColumnUmapPages,
        IReadOnlyList<Index> Indexes);

    /// <summary>Number of slots in an index column block's column array (always 10 in real Access).</summary>
    private const int MaxIndexColumns = 10;
    /// <summary>Sentinel column-number value meaning "this slot is unused".</summary>
    private const short ColumnUnused = unchecked((short)0xFFFF);

    /// <summary>
    /// Reads the TDEF binary and returns the column list and umap references.
    /// Only Jet4 is fully supported; Jet3 is a best-effort read.
    /// </summary>
    public static TdefInfo Read(byte[] page, JetFormat format)
    {
        if (page is null)   throw new ArgumentNullException(nameof(page));
        if (format is null) throw new ArgumentNullException(nameof(format));

        // ── Header fields ─────────────────────────────────────────────────────
        int totalRowCount      = ByteUtil.GetInt  (page, format.TdefOffsetNumRows);
        int numVarCols         = ByteUtil.GetShort(page, format.TdefOffsetNumVarCols);
        int numCols            = ByteUtil.GetShort(page, format.TdefOffsetNumCols);
        int numIndexSlots      = ByteUtil.GetInt  (page, format.TdefOffsetNumIndexSlots);
        int numIndexes         = ByteUtil.GetInt  (page, format.TdefOffsetNumIndexes);

        int ownedRow           = page[format.TdefOffsetOwnedRow];
        int ownedPage          = ByteUtil.Get3ByteInt(page, format.TdefOffsetOwnedPage);
        int freeRow            = page[format.TdefOffsetFreeRow];
        int freePage           = ByteUtil.Get3ByteInt(page, format.TdefOffsetFreePage);

        // ── Column definition area ────────────────────────────────────────────
        // Column defs start after the header + numIndexes index-row-count blocks.
        int colDefStart = format.SizeTdefHeader + numIndexes * format.SizeIndexDefinition;
        int colHdrSize  = format.SizeColumnHeader;   // Jet3 = 18, Jet4 = 25

        var columns = new List<Column>(numCols);

        // Read raw column metadata (names are stored separately, added below).
        for (int i = 0; i < numCols; i++)
        {
            int p = colDefStart + i * colHdrSize;

            var   dataType    = (DataType)page[p];
            short colNum      = ByteUtil.GetShort(page, p + format.OffsetColumnNumber);
            byte  flags       = page[p + format.OffsetColumnFlags];
            bool  isAutoNum   = (flags & 0x04) != 0;
            bool  isAutoGuid  = (flags & 0x40) != 0;
            short length      = ByteUtil.GetShort(page, p + format.OffsetColumnLength);
            short fixedOffset = ByteUtil.GetShort(page, p + format.OffsetColumnFixedDataOffset);
            short varIndex    = ByteUtil.GetShort(page, p + format.OffsetColumnVarTableIndex);

            // Build a temporary Column; name will be patched below.
            var col = new Column(
                name:           $"__col{i}__",   // placeholder
                dataType:       dataType,
                length:         length <= 0 ? dataType.GetDefaultSize() : length,
                isRequired:     false,
                isAutoNumber:   isAutoNum || isAutoGuid,
                allowZeroLength:false,
                precision:      page[p + format.OffsetColumnPrecision],
                scale:          page[p + format.OffsetColumnScale]);

            col.ColumnNumber = colNum;
            // Honour the on-disk offsets so post-deletion tables decode correctly.
            col.FixedDataOffset  = dataType.IsVariableLength() ? (short)-1 : fixedOffset;
            col.VarLenTableIndex = dataType.IsVariableLength() ? varIndex     : (short)-1;
            columns.Add(col);
        }

        // ── Locate column names ───────────────────────────────────────────────
        // Column names come IMMEDIATELY after column defs (before index blocks).
        // Layout (Jackcess Java confirmed):
        //   [colDefStart]                         numCols × SizeColumnHeader  column defs
        //   [colDefStart + numCols*colHdrSize]    numCols name entries  ← HERE
        //   [+ colNamesSize]                      numIndexes × SizeIndexColumnBlock
        //   [+ idx col blocks]                    numIndexSlots × SizeIndexInfoBlock
        //   [+ idx info blocks]                   numIndexSlots index name entries
        //   [+ idx names]                         long-value col usage maps
        //   [end]                                 0xFF 0xFF trailer
        int pos = colDefStart + numCols * colHdrSize;

        // Read column names and patch them in
        for (int i = 0; i < numCols && pos + format.SizeNameLength <= page.Length; i++)
        {
            (string name, int consumed) = ReadName(page, pos, format);
            pos += consumed;
            columns[i] = RenameColumn(columns[i], name);
        }

        // ── Index column blocks + slots + names ──────────────────────────────
        // Wrapped in try/fallback: a TDEF whose index sections spill past the
        // current page (via the next-TDEF-page pointer at bytes 4-7) would cause
        // out-of-bounds reads. Multi-page TDEF traversal is a future slice — for
        // now, bail to the old skip-with-arithmetic behaviour so plan A's reads
        // continue working.
        IReadOnlyList<Index> indexes;
        int indexBlocksEnd = pos
            + numIndexes     * format.SizeIndexColumnBlock
            + numIndexSlots  * format.SizeIndexInfoBlock;

        if (indexBlocksEnd > page.Length)
        {
            // Multi-page TDEF: skip past the index sections without parsing them,
            // matching the pre-step1 behaviour.
            indexes = Array.Empty<Index>();
            pos = indexBlocksEnd;
            for (int i = 0; i < numIndexSlots && pos + format.SizeNameLength <= page.Length; i++)
            {
                (_, int consumed) = ReadName(page, pos, format);
                pos += consumed;
            }
        }
        else
        {
            indexes = ParseIndexes(page, format, ref pos, numIndexes, numIndexSlots, columns);
        }

        // Read LVAL usage-map refs (4 bytes each: 1-byte row + 3-byte page),
        // one entry per long-value (Memo/OLE) column in column-number order.
        var lvalUmapPages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lvalCols = columns.Where(c => c.DataType.IsLongValue()).ToList();
        for (int i = 0; i < lvalCols.Count && pos + 4 <= page.Length; i++)
        {
            // byte 0 = row number (always 0 for owned-pages bitmap, ignored here)
            int lvalPage = ByteUtil.Get3ByteInt(page, pos + 1);
            pos += 4;
            if (lvalPage > 0)
                lvalUmapPages[lvalCols[i].Name] = lvalPage;
        }

        return new TdefInfo(
            Columns:              columns,
            TotalRowCount:        totalRowCount,
            OwnedPagesUmapPage:   ownedPage,
            OwnedPagesUmapRow:    ownedRow,
            FreeSpaceUmapPage:    freePage,
            FreeSpaceUmapRow:     freeRow,
            LvalColumnUmapPages:  lvalUmapPages,
            Indexes:              indexes);
    }

    /// <summary>Intermediate record: one decoded index column block.</summary>
    private readonly record struct IndexDataRecord(
        IReadOnlyList<IndexColumn> Columns, int RootPageNumber, byte Flags);

    /// <summary>Intermediate record: one decoded logical-index slot.</summary>
    private readonly record struct IndexSlotRecord(int IndexNumber, int IndexDataNumber, byte IndexType);

    /// <summary>
    /// Parses the index column blocks, the logical-index slot blocks, and the per-slot
    /// name entries that follow them. Caller must ensure the deterministic-size sections
    /// (blocks + slots) all fit on the current page.
    /// </summary>
    private static IReadOnlyList<Index> ParseIndexes(
        byte[] page, JetFormat format, ref int pos,
        int numIndexes, int numIndexSlots, List<Column> columns)
    {
        // Per index column block: SkipBeforeIndex + MaxIndexColumns × (short col + byte flags)
        //   + 4-byte umap ref + 4-byte root page + SkipBeforeIndexFlags + 1-byte flags + SkipAfterIndexFlags
        // Total = SizeIndexColumnBlock (39 Jet3 / 52 Jet4).
        var indexData = new IndexDataRecord[numIndexes];
        for (int i = 0; i < numIndexes; i++)
        {
            int blockStart = pos;
            int p          = pos + format.SkipBeforeIndex;

            var idxCols = new List<IndexColumn>(MaxIndexColumns);
            for (int c = 0; c < MaxIndexColumns; c++)
            {
                short colNum = ByteUtil.GetShort(page, p);
                byte  cFlags = page[p + 2];
                p += 3;
                if (colNum == ColumnUnused) continue;
                var col = columns.FirstOrDefault(x => x.ColumnNumber == colNum);
                if (col is null) continue;   // tolerant of unknown column numbers
                idxCols.Add(new IndexColumn(col, cFlags));
            }

            p += 4;   // UsageMap ref (1-byte row + 3-byte page)
            int rootPage = ByteUtil.GetInt(page, p); p += 4;
            p += format.SkipBeforeIndexFlags;
            byte indexFlags = page[p]; p += 1;
            // SkipAfterIndexFlags absorbed by the deterministic block-size advance below.

            indexData[i] = new IndexDataRecord(idxCols, rootPage, indexFlags);
            pos = blockStart + format.SizeIndexColumnBlock;
        }

        // Per slot: SkipBeforeIndexSlot + indexNumber(int) + indexDataNumber(int)
        //   + relIndexType(byte) + relIndexNumber(int) + relTablePage(int)
        //   + cascadeUpdates(byte) + cascadeDeletes(byte) + indexType(byte) + SkipAfterIndexSlot
        // Total = SizeIndexInfoBlock (20 Jet3 / 28 Jet4).
        var slots = new IndexSlotRecord[numIndexSlots];
        for (int i = 0; i < numIndexSlots; i++)
        {
            int slotStart = pos;
            int p = pos + format.SkipBeforeIndexSlot;
            int indexNumber     = ByteUtil.GetInt(page, p); p += 4;
            int indexDataNumber = ByteUtil.GetInt(page, p); p += 4;
            p += 11;   // relIndexType + relIndexNumber + relTablePage + cascade flags
            byte indexType = page[p];
            slots[i] = new IndexSlotRecord(indexNumber, indexDataNumber, indexType);
            pos = slotStart + format.SizeIndexInfoBlock;
        }

        // One length-prefixed name per slot.
        var slotNames = new string[numIndexSlots];
        for (int i = 0; i < numIndexSlots && pos + format.SizeNameLength <= page.Length; i++)
        {
            (string name, int consumed) = ReadName(page, pos, format);
            slotNames[i] = name;
            pos += consumed;
        }

        var indexes = new List<Index>(numIndexSlots);
        for (int i = 0; i < numIndexSlots; i++)
        {
            var slot = slots[i];
            if (slot.IndexDataNumber < 0 || slot.IndexDataNumber >= indexData.Length) continue;
            var data = indexData[slot.IndexDataNumber];
            indexes.Add(new Index(
                name:           slotNames[i] ?? $"__index_{i}__",
                columns:        data.Columns,
                rootPageNumber: data.RootPageNumber,
                indexNumber:    slot.IndexNumber,
                flags:          data.Flags,
                indexType:      slot.IndexType));
        }
        return indexes;
    }

    /// <summary>
    /// Reads a length-prefixed name from the TDEF buffer at <paramref name="pos"/>:
    ///   Jet3 → 1-byte length + cp1252 bytes
    ///   Jet4 → 2-byte length + UTF-16LE bytes
    /// Returns (decoded name, total bytes consumed including the length prefix).
    /// </summary>
    private static (string name, int consumed) ReadName(byte[] page, int pos, JetFormat format)
    {
        int nameLen = format.SizeNameLength == 2
            ? ByteUtil.GetShort(page, pos)
            : page[pos];
        int nameStart = pos + format.SizeNameLength;
        if (nameLen < 0 || nameStart + nameLen > page.Length)
            return ($"__bad_name_at_{pos}__", format.SizeNameLength);

        string name = format.SizeNameLength == 2
            ? Encoding.Unicode.GetString(page, nameStart, nameLen)
            : format.TextEncoding.GetString(page, nameStart, nameLen);
        return (name, format.SizeNameLength + nameLen);
    }

    // Column is a sealed class with internal ctor, so we rebuild it with the real name.
    private static Column RenameColumn(Column src, string name) =>
        new Column(
            name:           name,
            dataType:       src.DataType,
            length:         src.Length,
            isRequired:     src.IsRequired,
            isAutoNumber:   src.IsAutoNumber,
            allowZeroLength:src.AllowZeroLength,
            precision:      src.Precision,
            scale:          src.Scale)
        {
            ColumnNumber     = src.ColumnNumber,
            FixedDataOffset  = src.FixedDataOffset,
            VarLenTableIndex = src.VarLenTableIndex,
        };
}
