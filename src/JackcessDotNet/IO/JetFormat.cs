using System.Text;

namespace JackcessDotNet;

public sealed class JetFormat
{
    // ── Per-version row-encoding properties ───────────────────────────────────
    public JetVersion Version { get; }
    public int PageSize { get; }
    public bool HasPageChecksum { get; }
    public Encoding TextEncoding { get; }
    public int MaxRowSize { get; }
    public int SizeRowColumnCount { get; }
    public int SizeRowVarColOffset { get; }
    public int SizeSortOrder { get; }

    // ── Per-version TDEF layout properties ───────────────────────────────────
    /// <summary>Size in bytes of the TDEF page header (before index-row-count blocks).</summary>
    public int SizeTdefHeader { get; }
    /// <summary>Bytes per "real" index row-count block that precedes column definitions.</summary>
    public int SizeIndexDefinition { get; }
    /// <summary>Bytes per "real" index column block that follows column definitions.</summary>
    public int SizeIndexColumnBlock { get; }
    /// <summary>Bytes per logical-index info block that follows the index column blocks.</summary>
    public int SizeIndexInfoBlock { get; }
    /// <summary>Bytes per column header in the TDEF column-definition area (Jet3=18, Jet4=25).</summary>
    public int SizeColumnHeader { get; }
    /// <summary>Bytes of the name-length prefix on column/index names (Jet3=1, Jet4=2).</summary>
    public int SizeNameLength { get; }
    /// <summary>Byte size of the inline usage-map bitmap written per map row.</summary>
    public int UmapInlineBitmapSize { get; }

    // ── TDEF field byte-offsets (per-version) ────────────────────────────────
    public int TdefOffsetNumRows       { get; }   // int  – total row count in table
    public int TdefOffsetNumVarCols    { get; }   // short – variable-length column count
    public int TdefOffsetNumCols       { get; }   // short – total column count
    public int TdefOffsetNumIndexSlots { get; }   // int  – logical (slot) index count
    public int TdefOffsetNumIndexes    { get; }   // int  – real index count
    public int TdefOffsetOwnedRow      { get; }   // byte – row# in umap page for owned-pages map
    public int TdefOffsetOwnedPage     { get; }   // 3-byte int – umap page# for owned-pages map
    public int TdefOffsetFreeRow       { get; }   // byte – row# in umap page for free-space map
    public int TdefOffsetFreePage      { get; }   // 3-byte int – umap page# for free-space map

    // ── Per-column-header field byte-offsets (within one SizeColumnHeader block) ──
    public int OffsetColumnNumber          { get; }
    public int OffsetColumnPrecision       { get; }
    public int OffsetColumnScale           { get; }
    public int OffsetColumnFlags           { get; }
    public int OffsetColumnLength          { get; }
    public int OffsetColumnVarTableIndex   { get; }
    public int OffsetColumnFixedDataOffset { get; }

    // ── Padding bytes inside an index column block / logical-index info block ────
    /// <summary>Bytes to skip before the per-index magic-number / column array (Jet3=0, Jet4=4).</summary>
    public int SkipBeforeIndex      { get; }
    /// <summary>Bytes to skip between root-page-number and index flags (Jet3=0, Jet4=4).</summary>
    public int SkipBeforeIndexFlags { get; }
    /// <summary>Bytes to skip after the index-flags byte (Jet3=0, Jet4=5).</summary>
    public int SkipAfterIndexFlags  { get; }
    /// <summary>Bytes to skip before the per-slot index-number (Jet3=0, Jet4=4).</summary>
    public int SkipBeforeIndexSlot  { get; }
    /// <summary>Bytes to skip after each slot's index-type byte (Jet3=0, Jet4=4).</summary>
    public int SkipAfterIndexSlot   { get; }

    // ── Index page (type 0x03/0x04) layout offsets ────────────────────────────
    /// <summary>Byte offset of prev-page link in an index page (Jet3=8, Jet4=12).</summary>
    public int OffsetPrevIndexPage              { get; }
    /// <summary>Byte offset of next-page link in an index page (Jet3=12, Jet4=16).</summary>
    public int OffsetNextIndexPage              { get; }
    /// <summary>Byte offset of child-tail-page link in an index page (Jet3=16, Jet4=20).</summary>
    public int OffsetChildTailIndexPage         { get; }
    /// <summary>Byte offset of compressed-prefix-byte-count field (Jet3=20, Jet4=24).</summary>
    public int OffsetIndexCompressedByteCount   { get; }
    /// <summary>Byte offset where the entry mask bitmap begins (Jet3=22, Jet4=27).</summary>
    public int OffsetIndexEntryMask             { get; }
    /// <summary>Size of the entry mask bitmap in bytes (Jet3=226, Jet4=453).</summary>
    public int SizeIndexEntryMask               { get; }

    // ── Data-page byte-offsets ────────────────────────────────────────────────
    /// <summary>Byte offset of the free-space short inside a data/umap page header (same in all versions).</summary>
    public const int OffsetDataFreeSpace = 2;
    /// <summary>Byte offset of the owning TDEF page-number int inside a data page header (same in all versions).</summary>
    public const int OffsetDataTdefPage  = 4;
    /// <summary>Bytes per row-slot entry in the row-slot table (same in all versions).</summary>
    public const int SizeRowEntry        = 2;
    /// <summary>Bitmask applied to a slot value to strip the DELETED / OVERFLOW flag bits.</summary>
    public const int RowOffsetMask       = 0x1FFF;

    /// <summary>Byte offset of the row-count short inside a data/umap page header (Jet3=8, Jet4=12).</summary>
    public int OffsetDataNumRows  { get; }
    /// <summary>Byte offset where the row-slot table begins inside a data/umap page (Jet3=10, Jet4=14).</summary>
    public int OffsetDataRowTable { get; }
    /// <summary>Number of header bytes that precede the row-slot table on a data page (Jet3=10, Jet4=14).</summary>
    public int DataPageHeaderSize => OffsetDataRowTable;

    // ── Page-type sentinel bytes ──────────────────────────────────────────────
    public const byte PageTypeData     = 0x01;
    public const byte PageTypeTableDef = 0x02;
    public const byte PageTypeIndexNode= 0x03;
    public const byte PageTypeIndexLeaf= 0x04;
    public const byte PageTypeUsageMap = 0x05;

    // ── System page numbers ───────────────────────────────────────────────────
    public const int PageSystemCatalog = 2;   // MSysObjects TDEF page

    // ── MSysObjects type codes ────────────────────────────────────────────────
    public const short CatalogTypeTable = 1;

    /// <summary>Free space in a freshly-initialised data or umap page.</summary>
    public int DataPageInitialFreeSpace => PageSize - DataPageHeaderSize;

    // Must be declared before Jet3/Jet4 so the provider is registered before
    // Encoding.GetEncoding(1252) is called during static field initialisation.
    private static readonly bool _encodingsRegistered = InitEncodings();
    private static bool InitEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    }

    private sealed record FormatSpec(
        int PageSize, bool HasPageChecksum, Encoding TextEncoding,
        int MaxRowSize, int SizeRowColumnCount, int SizeRowVarColOffset, int SizeSortOrder,
        int SizeTdefHeader, int SizeIndexDefinition, int SizeIndexColumnBlock,
        int SizeIndexInfoBlock, int SizeColumnHeader, int SizeNameLength, int UmapInlineBitmapSize,
        int OffsetDataNumRows, int OffsetDataRowTable,
        // TDEF offsets
        int TdefOffsetNumRows, int TdefOffsetNumVarCols, int TdefOffsetNumCols,
        int TdefOffsetNumIndexSlots, int TdefOffsetNumIndexes,
        int TdefOffsetOwnedRow, int TdefOffsetOwnedPage,
        int TdefOffsetFreeRow, int TdefOffsetFreePage,
        // Column-header offsets
        int OffsetColumnNumber, int OffsetColumnPrecision, int OffsetColumnScale,
        int OffsetColumnFlags, int OffsetColumnLength,
        int OffsetColumnVarTableIndex, int OffsetColumnFixedDataOffset,
        // Index padding
        int SkipBeforeIndex, int SkipBeforeIndexFlags, int SkipAfterIndexFlags,
        int SkipBeforeIndexSlot, int SkipAfterIndexSlot,
        // Index page header
        int OffsetPrevIndexPage, int OffsetNextIndexPage, int OffsetChildTailIndexPage,
        int OffsetIndexCompressedByteCount, int OffsetIndexEntryMask, int SizeIndexEntryMask);

    private JetFormat(JetVersion version, FormatSpec s)
    {
        Version                     = version;
        PageSize                    = s.PageSize;
        HasPageChecksum             = s.HasPageChecksum;
        TextEncoding                = s.TextEncoding;
        MaxRowSize                  = s.MaxRowSize;
        SizeRowColumnCount          = s.SizeRowColumnCount;
        SizeRowVarColOffset         = s.SizeRowVarColOffset;
        SizeSortOrder               = s.SizeSortOrder;
        SizeTdefHeader              = s.SizeTdefHeader;
        SizeIndexDefinition         = s.SizeIndexDefinition;
        SizeIndexColumnBlock        = s.SizeIndexColumnBlock;
        SizeIndexInfoBlock          = s.SizeIndexInfoBlock;
        SizeColumnHeader            = s.SizeColumnHeader;
        SizeNameLength              = s.SizeNameLength;
        UmapInlineBitmapSize        = s.UmapInlineBitmapSize;
        OffsetDataNumRows           = s.OffsetDataNumRows;
        OffsetDataRowTable          = s.OffsetDataRowTable;
        TdefOffsetNumRows           = s.TdefOffsetNumRows;
        TdefOffsetNumVarCols        = s.TdefOffsetNumVarCols;
        TdefOffsetNumCols           = s.TdefOffsetNumCols;
        TdefOffsetNumIndexSlots     = s.TdefOffsetNumIndexSlots;
        TdefOffsetNumIndexes        = s.TdefOffsetNumIndexes;
        TdefOffsetOwnedRow          = s.TdefOffsetOwnedRow;
        TdefOffsetOwnedPage         = s.TdefOffsetOwnedPage;
        TdefOffsetFreeRow           = s.TdefOffsetFreeRow;
        TdefOffsetFreePage          = s.TdefOffsetFreePage;
        OffsetColumnNumber          = s.OffsetColumnNumber;
        OffsetColumnPrecision       = s.OffsetColumnPrecision;
        OffsetColumnScale           = s.OffsetColumnScale;
        OffsetColumnFlags           = s.OffsetColumnFlags;
        OffsetColumnLength          = s.OffsetColumnLength;
        OffsetColumnVarTableIndex   = s.OffsetColumnVarTableIndex;
        OffsetColumnFixedDataOffset = s.OffsetColumnFixedDataOffset;
        SkipBeforeIndex             = s.SkipBeforeIndex;
        SkipBeforeIndexFlags        = s.SkipBeforeIndexFlags;
        SkipAfterIndexFlags         = s.SkipAfterIndexFlags;
        SkipBeforeIndexSlot         = s.SkipBeforeIndexSlot;
        SkipAfterIndexSlot          = s.SkipAfterIndexSlot;
        OffsetPrevIndexPage              = s.OffsetPrevIndexPage;
        OffsetNextIndexPage              = s.OffsetNextIndexPage;
        OffsetChildTailIndexPage         = s.OffsetChildTailIndexPage;
        OffsetIndexCompressedByteCount   = s.OffsetIndexCompressedByteCount;
        OffsetIndexEntryMask             = s.OffsetIndexEntryMask;
        SizeIndexEntryMask               = s.SizeIndexEntryMask;
    }

    public static JetFormat Jet3 { get; } = new(JetVersion.Jet3, new FormatSpec(
        PageSize:              2048,
        HasPageChecksum:       false,
        TextEncoding:          Encoding.GetEncoding(1252),
        MaxRowSize:            2012,
        SizeRowColumnCount:    1,
        SizeRowVarColOffset:   1,
        SizeSortOrder:         2,
        SizeTdefHeader:        43,
        SizeIndexDefinition:   8,
        SizeIndexColumnBlock:  39,
        SizeIndexInfoBlock:    20,
        SizeColumnHeader:      18,
        SizeNameLength:        1,
        UmapInlineBitmapSize:  100,   // 800 pages per inline bitmap
        OffsetDataNumRows:     8,
        OffsetDataRowTable:    10,
        TdefOffsetNumRows:     12,
        TdefOffsetNumVarCols:  23,
        TdefOffsetNumCols:     25,
        TdefOffsetNumIndexSlots: 27,
        TdefOffsetNumIndexes:  31,
        TdefOffsetOwnedRow:    35,
        TdefOffsetOwnedPage:   36,
        TdefOffsetFreeRow:     39,
        TdefOffsetFreePage:    40,
        OffsetColumnNumber:    1,
        OffsetColumnPrecision: 11,
        OffsetColumnScale:     12,
        OffsetColumnFlags:     13,
        OffsetColumnLength:    16,
        OffsetColumnVarTableIndex:   3,
        OffsetColumnFixedDataOffset: 14,
        SkipBeforeIndex:             0,
        SkipBeforeIndexFlags:        0,
        SkipAfterIndexFlags:         0,
        SkipBeforeIndexSlot:         0,
        SkipAfterIndexSlot:          0,
        OffsetPrevIndexPage:             8,
        OffsetNextIndexPage:             12,
        OffsetChildTailIndexPage:        16,
        OffsetIndexCompressedByteCount:  20,
        OffsetIndexEntryMask:            22,
        SizeIndexEntryMask:              226));

    // Jet4 spec extracted so the ACE 12-17 (.accdb) instances can share it.
    // On-disk layout for unencrypted .accdb is identical to Jet4; what differs
    // (codec for encrypted files, sort-order default, V14+ calculated types) is
    // either irrelevant for read-only opens or deferred to a future slice.
    private static readonly FormatSpec _jet4Spec = new(
        PageSize:              4096,
        HasPageChecksum:       true,
        TextEncoding:          Encoding.Unicode,
        MaxRowSize:            4060,
        SizeRowColumnCount:    2,
        SizeRowVarColOffset:   2,
        SizeSortOrder:         4,
        SizeTdefHeader:        63,
        SizeIndexDefinition:   12,
        SizeIndexColumnBlock:  52,
        SizeIndexInfoBlock:    28,
        SizeColumnHeader:      25,
        SizeNameLength:        2,
        UmapInlineBitmapSize:  200,   // 1600 pages per inline bitmap
        OffsetDataNumRows:     12,
        OffsetDataRowTable:    14,
        TdefOffsetNumRows:     16,
        TdefOffsetNumVarCols:  43,
        TdefOffsetNumCols:     45,
        TdefOffsetNumIndexSlots: 47,
        TdefOffsetNumIndexes:  51,
        TdefOffsetOwnedRow:    55,
        TdefOffsetOwnedPage:   56,
        TdefOffsetFreeRow:     59,
        TdefOffsetFreePage:    60,
        OffsetColumnNumber:    5,
        OffsetColumnPrecision: 11,
        OffsetColumnScale:     12,
        OffsetColumnFlags:     15,
        OffsetColumnLength:    23,
        OffsetColumnVarTableIndex:   7,
        OffsetColumnFixedDataOffset: 21,
        SkipBeforeIndex:             4,
        SkipBeforeIndexFlags:        4,
        SkipAfterIndexFlags:         5,
        SkipBeforeIndexSlot:         4,
        SkipAfterIndexSlot:          4,
        OffsetPrevIndexPage:             12,
        OffsetNextIndexPage:             16,
        OffsetChildTailIndexPage:        20,
        OffsetIndexCompressedByteCount:  24,
        OffsetIndexEntryMask:            27,
        SizeIndexEntryMask:              453);

    public static JetFormat Jet4 { get; } = new(JetVersion.Jet4, _jet4Spec);

    /// <summary>
    /// ACE 12 (Access 2007) format. Almost identical to Jet4 — same page size,
    /// same layout offsets, same column-header size. Unencrypted .accdb files
    /// open straight through Jet4-shaped parsing; encrypted files need a codec
    /// that isn't yet implemented and will fail at first read of an encrypted page.
    /// </summary>
    public static JetFormat Jet12 { get; } = new(JetVersion.Jet12, _jet4Spec);
    /// <summary>ACE 14 — same on-disk layout as Jet12 in practice (sort-order differs).</summary>
    public static JetFormat Jet14 { get; } = new(JetVersion.Jet14, _jet4Spec);
    /// <summary>ACE 16 — same on-disk layout as Jet12/14.</summary>
    public static JetFormat Jet16 { get; } = new(JetVersion.Jet16, _jet4Spec);
    /// <summary>ACE 17 — same on-disk layout as Jet12/14/16.</summary>
    public static JetFormat Jet17 { get; } = new(JetVersion.Jet17, _jet4Spec);
}
