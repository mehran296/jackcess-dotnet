using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Reads and writes inline usage-map rows stored inside a Usage-Map page (type 0x05).
///
/// Each inline-map row layout:
///   Byte 0     : MAP_TYPE  (0x00 = inline)
///   Bytes 1-4  : start-page number (int, little-endian) – pages are numbered relative to this
///   Bytes 5-N  : bitmap   (1 bit per page, starting from start-page)
///
/// The row is stored inside the page using the standard slot-array layout shared by all
/// Jet page types: slot N lives at byte offset  OffsetDataRowTable + N * SizeRowEntry
/// and its value is the absolute byte-position of that row's data inside the page.
/// </summary>
internal static class UsageMap
{
    private const byte MapTypeInline    = 0x00;
    private const byte MapTypeReference = 0x01;

    // ── Page initialization ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a fresh Usage-Map page that holds two empty inline maps:
    ///   row 0 = owned-pages map
    ///   row 1 = free-space map
    /// </summary>
    public static byte[] CreateUmapPage(JetFormat format)
    {
        int bitmapSize = format.UmapInlineBitmapSize;
        int rowDataSize = 1 + 4 + bitmapSize;   // MAP_TYPE + startPage + bitmap
        var page = new byte[format.PageSize];

        // Page header
        page[0] = JetFormat.PageTypeUsageMap;
        page[1] = 0x01;
        // bytes 4-7 are 0 (no owning TDEF)

        // Write two rows, packed from the end of the page
        int cursor = format.PageSize;

        // Row 0 (owned pages) – written first = at higher address
        cursor -= rowDataSize;
        int row0Start = cursor;
        page[row0Start] = MapTypeInline;   // MAP_TYPE
        // start-page = 0, bitmap = all zeros (already zeroed)

        // Row 1 (free-space pages) – written second = at lower address
        cursor -= rowDataSize;
        int row1Start = cursor;
        page[row1Start] = MapTypeInline;

        // Slot table at OffsetDataRowTable (Jet3=10, Jet4=14)
        ByteUtil.PutShort(page, format.OffsetDataRowTable,                   (short)row0Start);
        ByteUtil.PutShort(page, format.OffsetDataRowTable + JetFormat.SizeRowEntry, (short)row1Start);

        // Row count and free space
        ByteUtil.PutShort(page, format.OffsetDataNumRows, 2);
        int freeSpace = row1Start - format.OffsetDataRowTable - 2 * JetFormat.SizeRowEntry;
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)freeSpace);

        return page;
    }

    // ── Row reading helpers ───────────────────────────────────────────────────

    /// <summary>Returns the byte-offset within <paramref name="page"/> at which row
    /// <paramref name="rowNum"/> starts, after stripping DELETED/OVERFLOW flag bits.</summary>
    public static int GetRowStart(byte[] page, int rowNum, JetFormat format)
        => ByteUtil.GetUShort(page, format.OffsetDataRowTable + rowNum * JetFormat.SizeRowEntry)
           & JetFormat.RowOffsetMask;

    /// <summary>Returns the length in bytes of row <paramref name="rowNum"/> in
    /// <paramref name="page"/>, using the slot-array convention that rows are packed
    /// from the end of the page (row 0 is last = highest address).</summary>
    public static int GetRowLength(byte[] page, int rowNum, JetFormat format)
    {
        int rowStart = GetRowStart(page, rowNum, format);
        int end = (rowNum == 0) ? format.PageSize
                                : GetRowStart(page, rowNum - 1, format);
        return end - rowStart;
    }

    // ── Bitmap operations ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the list of page numbers whose bits are set in the inline map stored
    /// at <paramref name="mapRow"/> of <paramref name="page"/>.
    /// Inline-only — throws for reference-style maps (use the overload that takes a PageFile).
    /// </summary>
    public static List<int> GetOwnedPages(byte[] page, int mapRow, JetFormat format)
        => GetOwnedPages(page, mapRow, format, file: null);

    /// <summary>
    /// Returns the list of page numbers covered by the usage map at <paramref name="mapRow"/>.
    /// Resolves both inline (single bitmap) and reference (list-of-bitmap-page-pointers) styles.
    /// </summary>
    public static List<int> GetOwnedPages(byte[] page, int mapRow, JetFormat format, PageFile? file)
    {
        int rowStart = GetRowStart(page, mapRow, format);
        int rowLen   = GetRowLength(page, mapRow, format);

        if (rowLen < 1) return new List<int>();

        byte mapType = page[rowStart];

        if (mapType == MapTypeInline)
        {
            if (rowLen < 5) return new List<int>();
            int startPage = ByteUtil.GetInt(page, rowStart + 1);
            return ReadBitmap(page, rowStart + 5, rowLen - 5, startPage);
        }

        if (mapType == MapTypeReference)
        {
            if (file is null)
                throw new InvalidOperationException(
                    "Reference-style usage map encountered but no PageFile was provided to resolve it.");

            // Row layout: 0x01 followed by an array of 4-byte LE page numbers.
            // Each pointer references a dedicated UsageMap page (type 0x05) whose
            // bitmap starts at byte 4 (OFFSET_USAGE_MAP_PAGE_DATA).
            const int RefMapBitmapStart = 4;
            int maxPagesPerRefPage = (format.PageSize - RefMapBitmapStart) * 8;
            int numRefPages = (rowLen - 1) / 4;
            var result = new List<int>();

            for (int i = 0; i < numRefPages; i++)
            {
                int refPageNum = ByteUtil.GetInt(page, rowStart + 1 + i * 4);
                if (refPageNum <= 0) continue;

                byte[] refPage = file.ReadPage(refPageNum);
                if (refPage[0] != JetFormat.PageTypeUsageMap)
                    throw new InvalidDataException(
                        $"Expected usage-map page (type 0x05) at page {refPageNum}, found 0x{refPage[0]:X2}.");

                int bitmapLen = format.PageSize - RefMapBitmapStart;
                result.AddRange(ReadBitmap(refPage, RefMapBitmapStart, bitmapLen, i * maxPagesPerRefPage));
            }
            return result;
        }

        throw new NotSupportedException($"Unknown usage-map type 0x{mapType:X2}.");
    }

    private static List<int> ReadBitmap(byte[] page, int bitmapStart, int bitmapLen, int basePage)
    {
        var result = new List<int>();
        for (int byteIdx = 0; byteIdx < bitmapLen; byteIdx++)
        {
            byte b = page[bitmapStart + byteIdx];
            if (b == 0) continue;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (1 << bit)) != 0)
                    result.Add(basePage + byteIdx * 8 + bit);
            }
        }
        return result;
    }

    /// <summary>
    /// Sets the bit for <paramref name="pageNumber"/> in the inline map stored at
    /// <paramref name="mapRow"/> of <paramref name="page"/> (in-place, then caller
    /// must write the page back).
    /// </summary>
    public static void AddPage(byte[] page, int mapRow, int pageNumber, JetFormat format)
    {
        int rowStart = GetRowStart(page, mapRow, format);
        int rowLen   = GetRowLength(page, mapRow, format);

        if (rowLen < 5)
            throw new InvalidOperationException("Usage-map row is too small.");

        byte mapType = page[rowStart];
        if (mapType != MapTypeInline)
            throw new NotSupportedException("Reference-style usage maps are not supported yet.");

        int startPage = ByteUtil.GetInt(page, rowStart + 1);
        int relative  = pageNumber - startPage;

        if (relative < 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber),
                $"Page {pageNumber} is before the start page {startPage} of the usage map.");

        int byteIdx = relative / 8;
        int bitIdx  = relative % 8;
        int bitmapLen = rowLen - 5;

        if (byteIdx >= bitmapLen)
            throw new NotSupportedException(
                $"Page {pageNumber} is beyond the inline bitmap capacity " +
                $"(bitmap covers {bitmapLen * 8} pages from page {startPage}).");

        page[rowStart + 5 + byteIdx] |= (byte)(1 << bitIdx);
    }
}
