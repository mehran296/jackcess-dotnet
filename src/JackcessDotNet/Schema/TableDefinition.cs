using System.Linq;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Holds the schema of a table plus the page-level state needed to write rows.
/// </summary>
public sealed class TableDefinition
{
    private const int     MagicTableNumber = 1625;
    private const byte    TableTypeUser    = 0x4E;

    private const byte    ColumnFlagFixedLen      = 0x01;
    private const byte    ColumnFlagUpdatable     = 0x02;
    private const byte    ColumnFlagAutoNumber    = 0x04;
    private const byte    ColumnFlagAutoNumberGuid= 0x40;

    // Per-index sort-flag bits (per Jackcess IndexData)
    private const byte    AscendingColumnFlag     = 0x01;
    // Per-index index-data-flags byte (per Jackcess IndexData)
    private const byte    UniqueIndexFlag         = 0x01;
    private const byte    UnknownIndexFlag        = 0x80;   // always set on Access 2000+ indexes
    // Index slot's index-type byte
    private const byte    PrimaryKeyIndexType     = 0x01;
    // Sentinel meaning "no related index"
    private const int     InvalidIndexNumber      = -1;

    // ── Schema ────────────────────────────────────────────────────────────────
    public string               Name    { get; }
    public IReadOnlyList<Column> Columns { get; }

    // ── Page-level state (set by Database.CreateTable / GetTable) ────────────
    /// <summary>Page number that contains this table's TDEF.</summary>
    public int TdefPageNumber   { get; set; }
    /// <summary>Page number that contains the usage-map rows for this table.</summary>
    public int UmapPageNumber   { get; set; }
    /// <summary>Row index inside the umap page that holds the owned-pages bitmap.</summary>
    public int OwnedPagesRow    { get; set; }
    /// <summary>Row index inside the umap page that holds the free-space bitmap.</summary>
    public int FreeSpaceRow     { get; set; }

    // ── Primary-key index (optional) ─────────────────────────────────────────
    /// <summary>Page number of the primary key index leaf page (0 if no primary key index).</summary>
    public int     PrimaryKeyIndexPage   { get; set; }
    /// <summary>
    /// Name of the primary key column for single-column PKs (null when there's
    /// no PK <i>or</i> the PK is composite). For composite PKs use
    /// <see cref="PrimaryKeyColumnNames"/>.
    /// </summary>
    public string? PrimaryKeyColumnName  { get; set; }
    /// <summary>
    /// Names of the PK columns for composite keys, in declaration order
    /// (max 10 entries — Jet's hard limit per index). Null/empty when the PK
    /// is single-column (see <see cref="PrimaryKeyColumnName"/>) or absent.
    /// </summary>
    public IReadOnlyList<string>? PrimaryKeyColumnNames { get; set; }

    /// <summary>
    /// Resolved list of PK column names. Empty when the table has no PK,
    /// single-element for the legacy single-column case, multi-element for
    /// composite keys.
    /// </summary>
    public IReadOnlyList<string> EffectivePrimaryKeyColumns =>
        PrimaryKeyColumnNames is { Count: > 0 } cols
            ? cols
            : PrimaryKeyColumnName is { } single
                ? new[] { single }
                : Array.Empty<string>();

    // ── Long-value column usage maps (one per Memo/OLE column) ───────────────
    /// <summary>
    /// Usage-map page numbers for long-value (Memo/OLE) columns, keyed by column name.
    /// Populated by <see cref="Database.CreateTable"/> before <see cref="Serialize"/> is called.
    /// </summary>
    public Dictionary<string, int> LvalColumnUmapPages { get; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Real B-tree indexes parsed from the on-disk TDEF (empty for tables this
    /// library creates, since we don't yet serialize real index column blocks).
    /// </summary>
    public IReadOnlyList<Index> Indexes { get; internal set; } = Array.Empty<Index>();

    public TableDefinition(string name, IReadOnlyList<Column> columns)
    {
        Name    = name    ?? throw new ArgumentNullException(nameof(name));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
    }

    // ── Serialisation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Serialises the table definition to a byte array that is ready to be written
    /// as a TDEF page.  The page is exactly <see cref="JetFormat.PageSize"/> bytes;
    /// unused trailing space is left as zero.
    ///
    /// Layout produced (Jet4):
    ///   [0..62]   TDEF header  (63 bytes)
    ///   [63..]    Column definitions  (numCols × 25 bytes)   ← no index blocks (0 indexes)
    ///   [..]      Column names        (2 + UTF-16LE per name)
    ///   [..]      Trailer             (0xFF 0xFF)
    /// </summary>
    public byte[] Serialize(JetFormat format)
    {
        if (format is null) throw new ArgumentNullException(nameof(format));
        if (Columns.Count == 0)
            throw new InvalidOperationException("Table must have at least one column.");
        if (Columns.Count > 255)
            throw new InvalidOperationException("Tables with more than 255 columns are not supported.");
        if (format.Version == JetVersion.Jet3)
            throw new NotSupportedException(
                "Writing Jet3 (Access 97) tables is not yet supported. " +
                "Jet3 column headers are 18 bytes with a different field layout than Jet4 (25 bytes).");

        // Compute column-name bytes (always UTF-16LE in TDEF)
        byte[][] nameBytes = Columns.Select(c => System.Text.Encoding.Unicode.GetBytes(c.Name)).ToArray();

        var pkColumns = EffectivePrimaryKeyColumns;
        if (pkColumns.Count > 10)
            throw new InvalidOperationException(
                $"Composite primary keys are limited to 10 columns; got {pkColumns.Count}.");

        bool hasPrimaryKey = pkColumns.Count > 0 && PrimaryKeyIndexPage > 0;
        int  numIndexes    = hasPrimaryKey ? 1 : 0;
        int  numIndexSlots = hasPrimaryKey ? 1 : 0;

        // Per-index sections sum to SizeIndexDefinition + SizeIndexColumnBlock + SizeIndexInfoBlock
        // for one index, plus a length-prefixed "PrimaryKey" name.
        byte[] pkNameBytes = hasPrimaryKey
            ? System.Text.Encoding.Unicode.GetBytes("PrimaryKey")
            : Array.Empty<byte>();

        int headerSize     = format.SizeTdefHeader;                  // 63 (Jet4) or 43 (Jet3)
        int idxDefSection  = numIndexes * format.SizeIndexDefinition;
        int colDefsSize    = format.SizeColumnHeader * Columns.Count;
        int nameSection    = nameBytes.Sum(b => 2 + b.Length);
        int idxColBlocks   = numIndexes * format.SizeIndexColumnBlock;
        int idxInfoBlocks  = numIndexSlots * format.SizeIndexInfoBlock;
        int idxNameSection = numIndexSlots * (2 + pkNameBytes.Length);
        int lvalSection    = Columns.Count(c => c.DataType.IsLongValue()) * 4;
        int trailerSize    = 2;

        int contentSize = headerSize + idxDefSection + colDefsSize + nameSection
                        + idxColBlocks + idxInfoBlocks + idxNameSection
                        + lvalSection + trailerSize;

        // The page buffer is always a full page; content starts at byte 0.
        var page = new byte[format.PageSize];

        // ── 8-byte page prefix ────────────────────────────────────────────────
        page[0] = JetFormat.PageTypeTableDef;
        page[1] = 0x01;
        page[2] = 0x00;
        page[3] = 0x00;
        ByteUtil.PutInt(page, 4, 0);   // next-TDEF-page = 0 (single-page table def)

        int pos = 8;

        // ── TDEF header (varies by format) ────────────────────────────────────
        ByteUtil.PutInt  (page, pos, contentSize); pos += 4;  // total content size
        ByteUtil.PutInt  (page, pos, MagicTableNumber); pos += 4;
        ByteUtil.PutInt  (page, pos, 0); pos += 4;   // num rows (initially 0)
        ByteUtil.PutInt  (page, pos, 0); pos += 4;   // last autonumber
        page[pos++] = 0x01;                           // autonumber flag

        // SizeTdefHeader is measured from byte 0 of the page, so we subtract
        // the current absolute write position (pos) plus the size of all
        // remaining fixed fields that follow the unknown padding.
        int unknownPad = format.SizeTdefHeader - pos
                         - 1  /* table type */
                         - 2  /* max cols */
                         - 2  /* var cols */
                         - 2  /* num cols */
                         - 4  /* logical index count */
                         - 4  /* real index count */
                         - 4  /* owned-pages umap ref  (1 row byte + 3 page bytes) */
                         - 4  /* free-space umap ref   (1 row byte + 3 page bytes) */;
        for (int i = 0; i < unknownPad; i++) page[pos++] = 0x00;

        page[pos++] = TableTypeUser;
        ByteUtil.PutShort(page, pos, (short)Columns.Count); pos += 2;  // max cols
        ByteUtil.PutShort(page, pos, (short)Columns.Count(c => c.DataType.IsVariableLength())); pos += 2;
        ByteUtil.PutShort(page, pos, (short)Columns.Count); pos += 2;  // col count
        ByteUtil.PutInt  (page, pos, numIndexSlots); pos += 4;   // logical index count
        ByteUtil.PutInt  (page, pos, numIndexes);    pos += 4;   // real index count

        // Owned-pages umap reference: row 0 of UmapPageNumber
        page[pos++] = (byte)OwnedPagesRow;
        ByteUtil.Put3ByteInt(page, pos, UmapPageNumber); pos += 3;

        // Free-space umap reference: row 1 of UmapPageNumber
        page[pos++] = (byte)FreeSpaceRow;
        ByteUtil.Put3ByteInt(page, pos, UmapPageNumber); pos += 3;

        // pos should now == SizeTdefHeader (the header size includes the 8-byte page prefix)
        pos = headerSize;

        // ── Index row-count blocks (zeros — Jackcess does the same) ──────────
        pos += idxDefSection;

        // ── Assign column layout ──────────────────────────────────────────────
        var fixedOffsets = new Dictionary<Column, short>();
        var varIndexes   = new Dictionary<Column, short>();
        short fixedOff = 0;
        short varIdx   = 0;

        for (short i = 0; i < Columns.Count; i++)
        {
            Columns[i].ColumnNumber = i;
            if (Columns[i].DataType.IsVariableLength())
                varIndexes[Columns[i]] = varIdx++;
            else
            {
                fixedOffsets[Columns[i]] = fixedOff;
                fixedOff += (short)Columns[i].Length;
            }
        }

        // ── Column definitions (25 bytes each) ───────────────────────────────
        foreach (var col in Columns)
        {
            page[pos++] = (byte)col.DataType;
            ByteUtil.PutInt  (page, pos, MagicTableNumber); pos += 4;
            ByteUtil.PutShort(page, pos, (short)col.ColumnNumber); pos += 2;
            ByteUtil.PutShort(page, pos, col.DataType.IsVariableLength() ? varIndexes[col] : (short)0); pos += 2;
            ByteUtil.PutShort(page, pos, (short)col.ColumnNumber); pos += 2;

            if (col.DataType == DataType.Text)
            {
                for (int i = 0; i < format.SizeSortOrder; i++) page[pos++] = 0x00;
            }
            else
            {
                page[pos++] = col.DataType == DataType.Numeric ? col.Precision : (byte)0x00;
                page[pos++] = col.DataType == DataType.Numeric ? col.Scale     : (byte)0x00;
                ByteUtil.PutShort(page, pos, 0); pos += 2;
            }

            byte flags = ColumnFlagUpdatable;
            if (!col.DataType.IsVariableLength()) flags |= ColumnFlagFixedLen;
            if (col.IsAutoNumber)
                flags |= (col.DataType == DataType.Guid ? ColumnFlagAutoNumberGuid : ColumnFlagAutoNumber);

            page[pos++] = flags;
            page[pos++] = 0x00;   // ext flags
            ByteUtil.PutInt  (page, pos, 0); pos += 4;
            ByteUtil.PutShort(page, pos, col.DataType.IsVariableLength() ? (short)0 : fixedOffsets[col]); pos += 2;
            ByteUtil.PutShort(page, pos, col.DataType.IsLongValue()      ? (short)0 : (short)col.Length); pos += 2;
        }

        // ── Column names (length-prefixed UTF-16LE) ───────────────────────────
        for (int i = 0; i < Columns.Count; i++)
        {
            ByteUtil.PutShort(page, pos, (short)nameBytes[i].Length); pos += 2;
            Array.Copy(nameBytes[i], 0, page, pos, nameBytes[i].Length);
            pos += nameBytes[i].Length;
        }

        // ── Index column block(s) + slot info + index name ────────────────────
        // Only emitted when PrimaryKey is configured. Layout per Jackcess Java:
        //   index column block: SkipBeforeIndex + 10×(short colNum + byte flags)
        //                     + 4-byte umap ref + 4-byte root page
        //                     + SkipBeforeIndexFlags + 1-byte flags + SkipAfterIndexFlags
        //   slot block:        SkipBeforeIndexSlot + 4-byte indexNumber + 4-byte indexDataNumber
        //                     + 1-byte relIndexType + 4-byte relIndexNumber + 4-byte relTablePage
        //                     + 1-byte cascadeUpdates + 1-byte cascadeDeletes + 1-byte indexType
        //                     + SkipAfterIndexSlot
        //   slot name:         length-prefixed UTF-16LE
        if (hasPrimaryKey)
        {
            // Map every PK column name → its column number; throw if any is missing.
            short[] pkColumnNumbers = pkColumns.Select(name =>
            {
                var col = Columns.FirstOrDefault(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (col is null)
                    throw new InvalidOperationException(
                        $"Primary key column '{name}' not found in table '{Name}'.");
                return (short)col.ColumnNumber;
            }).ToArray();

            int blockStart = pos;
            int p = pos + format.SkipBeforeIndex;

            // 10 column slots: populated PK columns ASC, remaining slots = COLUMN_UNUSED (-1).
            for (int c = 0; c < 10; c++)
            {
                if (c < pkColumnNumbers.Length)
                {
                    ByteUtil.PutShort(page, p, pkColumnNumbers[c]); p += 2;
                    page[p++] = AscendingColumnFlag;
                }
                else
                {
                    ByteUtil.PutShort(page, p, unchecked((short)0xFFFF)); p += 2;
                    page[p++] = 0x00;
                }
            }
            // UsageMap ref (4 bytes: row + 3-byte page) — empty for us.
            p += 4;
            // Root page = PK index leaf page.
            ByteUtil.PutInt(page, p, PrimaryKeyIndexPage); p += 4;
            p += format.SkipBeforeIndexFlags;
            page[p++] = (byte)(UnknownIndexFlag | UniqueIndexFlag);
            // SkipAfterIndexFlags absorbed by the block-size advance below.
            pos = blockStart + format.SizeIndexColumnBlock;

            // Logical-index slot block.
            int slotStart = pos;
            p = pos + format.SkipBeforeIndexSlot;
            ByteUtil.PutInt(page, p, 0);                   p += 4;   // indexNumber
            ByteUtil.PutInt(page, p, 0);                   p += 4;   // indexDataNumber
            p += 1;                                                 // relIndexType
            ByteUtil.PutInt(page, p, InvalidIndexNumber);  p += 4;   // relIndexNumber
            p += 4;                                                 // relTablePage
            p += 2;                                                 // cascadeUpdates + cascadeDeletes
            page[p] = PrimaryKeyIndexType;
            pos = slotStart + format.SizeIndexInfoBlock;

            // Length-prefixed "PrimaryKey" name.
            ByteUtil.PutShort(page, pos, (short)pkNameBytes.Length); pos += 2;
            Array.Copy(pkNameBytes, 0, page, pos, pkNameBytes.Length);
            pos += pkNameBytes.Length;
        }

        // ── LVAL column usage-map references (4 bytes each: 1-byte row + 3-byte page) ──
        // One entry per long-value (Memo/OLE) column, in column-number order.
        foreach (var col in Columns.Where(c => c.DataType.IsLongValue()))
        {
            LvalColumnUmapPages.TryGetValue(col.Name, out int lvalPage);
            page[pos++] = 0;                                  // row 0 = owned-pages bitmap
            ByteUtil.Put3ByteInt(page, pos, lvalPage); pos += 3;
        }

        // ── Trailer ───────────────────────────────────────────────────────────
        page[pos++] = 0xFF;
        page[pos]   = 0xFF;

        return page;
    }
}
