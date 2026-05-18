using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Writes long-value (Memo / OLE) data to dedicated LVAL data pages,
/// splitting large values into a chain of chunks when they exceed one page's capacity.
///
/// Each chunk row stored on a LVAL page contains:
///   [0 .. chunkDataLen-1]   actual data bytes
///   [chunkDataLen .. +3]    4-byte chain suffix:
///                             0xFF × 4                              → last (or only) chunk
///                             [3-byte LE nextPage][1-byte nextRow]  → link to next chunk
///
/// The 9-byte OTHER_PAGE LvRef written into the parent row's variable-length area:
///   [0..3]  4-byte LE total data byte count
///   [4]     0x40  (OTHER_PAGE marker)
///   [5..7]  3-byte LE page number of the first chunk
///   [8]     1-byte row index of the first chunk on that page
/// </summary>
internal sealed class LvalWriter
{
    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;
    private readonly int           _umapPageNumber;   // LVAL column's umap page (row 0 = owned pages)
    private readonly JetFormat     _format;

    public LvalWriter(PageFile file, PageAllocator allocator, int umapPageNumber)
    {
        _file           = file      ?? throw new ArgumentNullException(nameof(file));
        _allocator      = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _umapPageNumber = umapPageNumber;
        _format         = file.Format;
    }

    /// <summary>
    /// Writes <paramref name="data"/> across one or more LVAL pages (in reverse-chunk order)
    /// and returns the 9-byte OTHER_PAGE LvRef to store in the parent row.
    /// </summary>
    public byte[] Write(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        // Max bytes of payload data per chunk row.
        // A fresh LVAL page has DataPageInitialFreeSpace bytes free.
        // Inserting a row costs: rowBytes + SizeRowEntry (slot header).
        // The row itself is: chunkDataCapacity + 4 (chain suffix).
        int maxChunkRowSize   = _format.DataPageInitialFreeSpace - JetFormat.SizeRowEntry;
        int chunkDataCapacity = maxChunkRowSize - 4;

        int totalLen  = data.Length;
        int numChunks = totalLen == 0 ? 1
                                      : (totalLen + chunkDataCapacity - 1) / chunkDataCapacity;

        // Compute per-chunk slice descriptors.
        var chunkStart = new int[numChunks];
        var chunkLen   = new int[numChunks];
        for (int i = 0; i < numChunks; i++)
        {
            chunkStart[i] = i * chunkDataCapacity;
            chunkLen[i]   = Math.Min(chunkDataCapacity, totalLen - chunkStart[i]);
        }

        // Write chunks in REVERSE order so each chunk can embed its successor's address.
        int nextPage = 0, nextRow = 0;
        for (int i = numChunks - 1; i >= 0; i--)
        {
            bool isLast = (i == numChunks - 1);
            int  dLen   = chunkLen[i];
            var  chunk  = new byte[dLen + 4];

            Array.Copy(data, chunkStart[i], chunk, 0, dLen);

            if (isLast)
            {
                chunk[dLen    ] = 0xFF;
                chunk[dLen + 1] = 0xFF;
                chunk[dLen + 2] = 0xFF;
                chunk[dLen + 3] = 0xFF;
            }
            else
            {
                chunk[dLen    ] = (byte) nextPage;
                chunk[dLen + 1] = (byte)(nextPage >>  8);
                chunk[dLen + 2] = (byte)(nextPage >> 16);
                chunk[dLen + 3] = (byte) nextRow;
            }

            (nextPage, nextRow) = WriteChunkRow(chunk);
        }

        return BuildOtherPageLvRef(totalLen, nextPage, nextRow);
    }

    // Appends one chunk row to an available LVAL data page; returns (pageNum, rowIndex).
    private (int page, int row) WriteChunkRow(byte[] chunk)
    {
        int needed = chunk.Length + JetFormat.SizeRowEntry;

        // Find an existing LVAL page with enough room.
        byte[] umapPage  = _file.ReadPage(_umapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, 0 /* OwnedPagesRow */, _format, _file);
        int    lvalPage  = -1;

        foreach (int pn in ownedList)
        {
            byte[] dp   = _file.ReadPage(pn);
            short  free = ByteUtil.GetShort(dp, JetFormat.OffsetDataFreeSpace);
            if (free >= needed) { lvalPage = pn; break; }
        }

        if (lvalPage < 0)
        {
            // No page has room — allocate a fresh LVAL data page.
            // tdefPageNumber = 0: LVAL pages are not owned by a user TDEF.
            lvalPage = _allocator.AllocateDataPage(0);
            umapPage = _file.ReadPage(_umapPageNumber);   // re-read after alloc
            UsageMap.AddPage(umapPage, 0 /* OwnedPagesRow */, lvalPage, _format);
            _file.WritePage(_umapPageNumber, umapPage);
        }

        // Append the chunk row (rows are packed from the end of the page).
        byte[] page      = _file.ReadPage(lvalPage);
        int    rowCount  = ByteUtil.GetShort(page, _format.OffsetDataNumRows);
        int    freeSpace = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
        int    cursor    = _format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry + freeSpace;
        int    rowStart  = cursor - chunk.Length;

        Array.Copy(chunk, 0, page, rowStart, chunk.Length);

        ByteUtil.PutShort(page,
            _format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry,
            (short)rowStart);
        ByteUtil.PutShort(page, _format.OffsetDataNumRows,   (short)(rowCount + 1));
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)(freeSpace - needed));

        _file.WritePage(lvalPage, page);
        return (lvalPage, rowCount);   // rowCount was the 0-based index before increment
    }

    private static byte[] BuildOtherPageLvRef(int totalLen, int page, int row)
    {
        var lvRef = new byte[9];
        ByteUtil.PutInt(lvRef, 0, totalLen);   // 4-byte LE total data length
        lvRef[4] = 0x40;                        // OTHER_PAGE marker
        lvRef[5] = (byte) page;
        lvRef[6] = (byte)(page >>  8);
        lvRef[7] = (byte)(page >> 16);
        lvRef[8] = (byte) row;
        return lvRef;
    }
}

/// <summary>
/// Reads long-value data from LVAL data pages by following the chunk chain forward.
/// </summary>
internal sealed class LvalReader
{
    private readonly PageFile  _file;
    private readonly JetFormat _format;

    public LvalReader(PageFile file)
    {
        _file   = file ?? throw new ArgumentNullException(nameof(file));
        _format = file.Format;
    }

    /// <summary>
    /// Reconstructs the full byte array for the LVAL value whose first chunk starts at
    /// (<paramref name="lvalPage"/>, <paramref name="lvalRow"/>).
    /// <paramref name="totalLen"/> is the expected byte count from the parent-row LvRef.
    /// </summary>
    public byte[] Read(int lvalPage, int lvalRow, int totalLen)
    {
        var result  = new byte[totalLen];
        int written = 0;
        int curPage = lvalPage;
        int curRow  = lvalRow;

        while (written < totalLen)
        {
            byte[] page     = _file.ReadPage(curPage);
            int    rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);

            if (curRow >= rowCount)
                throw new InvalidOperationException(
                    $"LVAL page {curPage} has {rowCount} rows; requested row {curRow}.");

            (int rowStart, int rowEnd) = GetRowBounds(page, curRow, rowCount);
            int rowLen = rowEnd - rowStart;

            if (rowLen < 4)
                throw new InvalidOperationException(
                    $"LVAL row [{curPage},{curRow}] is only {rowLen} bytes (minimum 4 required).");

            int dataLen = rowLen - 4;
            int copyLen = Math.Min(dataLen, totalLen - written);
            Array.Copy(page, rowStart, result, written, copyLen);
            written += copyLen;

            // Inspect the 4-byte chain suffix at rowStart + dataLen.
            int sfx = rowStart + dataLen;
            bool isLast = page[sfx    ] == 0xFF
                       && page[sfx + 1] == 0xFF
                       && page[sfx + 2] == 0xFF
                       && page[sfx + 3] == 0xFF;
            if (isLast) break;

            curPage = page[sfx    ] | (page[sfx + 1] << 8) | (page[sfx + 2] << 16);
            curRow  = page[sfx + 3];
        }

        return result;
    }

    private (int start, int end) GetRowBounds(byte[] page, int rowIndex, int rowCount)
    {
        int slotOff  = _format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;
        int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
        int rowEnd   = rowIndex == 0
            ? _format.PageSize
            : (ByteUtil.GetUShort(page,
                   _format.OffsetDataRowTable + (rowIndex - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);
        return (rowStart, rowEnd);
    }
}

/// <summary>
/// Frees (marks as deleted) a chain of LVAL chunks, then compacts each affected LVAL page
/// by reclaiming space from any deleted tail rows.
///
/// Tail compaction: rows are packed from the end of the page (row 0 = highest address,
/// row rowCount-1 = lowest address = most recently inserted).  If the most recently
/// inserted row is deleted we can shrink rowCount and increase freeSpace, making that
/// space immediately available to the next write.  We repeat until the last row is live.
///
/// Because LvalWriter writes chunks in REVERSE order, chunk 0 (first in the chain) is
/// always the LAST row written to its LVAL page and therefore the most eligible for tail
/// compaction.  Following the forward chain (chunk 0 → 1 → … → last) and compacting each
/// page in turn fully reclaims the space when all chunks were the sole occupants of their
/// respective pages.
/// </summary>
internal sealed class LvalFree
{
    private readonly PageFile  _file;
    private readonly JetFormat _format;

    public LvalFree(PageFile file)
    {
        _file   = file ?? throw new ArgumentNullException(nameof(file));
        _format = file.Format;
    }

    /// <summary>
    /// Marks every chunk in the LVAL chain starting at
    /// (<paramref name="lvalPage"/>, <paramref name="lvalRow"/>) as deleted,
    /// and attempts tail-compaction on each affected page.
    /// </summary>
    public void FreeChain(int lvalPage, int lvalRow)
    {
        int curPage = lvalPage;
        int curRow  = lvalRow;

        while (true)
        {
            byte[] page     = _file.ReadPage(curPage);
            int    rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);

            if (curRow >= rowCount) break;   // safety: malformed chain

            (int rowStart, int rowEnd) = GetRowBounds(page, curRow, rowCount);
            int rowLen = rowEnd - rowStart;
            if (rowLen < 4) break;

            // Read chain suffix BEFORE modifying the page.
            int  dataLen = rowLen - 4;
            int  sfx     = rowStart + dataLen;
            bool isLast  = page[sfx    ] == 0xFF && page[sfx + 1] == 0xFF
                        && page[sfx + 2] == 0xFF && page[sfx + 3] == 0xFF;
            int nextPage = 0, nextRow = 0;
            if (!isLast)
            {
                nextPage = page[sfx    ] | (page[sfx + 1] << 8) | (page[sfx + 2] << 16);
                nextRow  = page[sfx + 3];
            }

            // Mark this chunk's slot as deleted.
            int slotOff = _format.OffsetDataRowTable + curRow * JetFormat.SizeRowEntry;
            ByteUtil.PutUShort(page, slotOff,
                (ushort)(ByteUtil.GetUShort(page, slotOff) | 0x8000u));

            // Compact the tail: pop any deleted rows from the end of the slot table,
            // returning their space to the free area.
            TrimDeletedTail(page);

            _file.WritePage(curPage, page);

            if (isLast) break;

            curPage = nextPage;
            curRow  = nextRow;
        }
    }

    // Pops deleted rows from the high-slot-index end of the page (the "bottom" of the
    // data area), increasing freeSpace for each reclaimed row.
    private void TrimDeletedTail(byte[] page)
    {
        int rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);

        while (rowCount > 0)
        {
            int    slotOff = _format.OffsetDataRowTable + (rowCount - 1) * JetFormat.SizeRowEntry;
            ushort slotVal = ByteUtil.GetUShort(page, slotOff);
            if ((slotVal & 0x8000) == 0) break;   // last row is live — stop

            // Row (rowCount-1):
            //   start = slotVal & 0x1FFF
            //   end   = slot[rowCount-2] (or pageSize for the only row)
            int rowStart = slotVal & JetFormat.RowOffsetMask;
            int rowEnd   = rowCount == 1
                ? _format.PageSize
                : (ByteUtil.GetUShort(page,
                       _format.OffsetDataRowTable + (rowCount - 2) * JetFormat.SizeRowEntry)
                   & JetFormat.RowOffsetMask);
            int rowLen = rowEnd - rowStart;

            rowCount--;
            ByteUtil.PutShort(page, _format.OffsetDataNumRows, (short)rowCount);

            short freeSpace = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
            ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace,
                (short)(freeSpace + rowLen + JetFormat.SizeRowEntry));
        }
    }

    private (int start, int end) GetRowBounds(byte[] page, int rowIndex, int rowCount)
    {
        int slotOff  = _format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;
        int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
        int rowEnd   = rowIndex == 0
            ? _format.PageSize
            : (ByteUtil.GetUShort(page,
                   _format.OffsetDataRowTable + (rowIndex - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);
        return (rowStart, rowEnd);
    }
}
