using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Sequential row iterator over a single table.
/// Walks the table's owned-pages usage map and yields decoded rows in physical
/// (insertion-)order, transparently following LVAL chains for Memo/OLE columns.
///
/// State machine:
///   position = (pageIndex into ownedPages, rowIndex into that page).
///   BeforeFirst → pageIndex = -1 (so the next call to GetNextRow returns the first row).
///   AfterLast   → pageIndex = ownedPages.Count (so the next call to GetPreviousRow returns the last row).
///
/// Cursors are forward/backward seekable; they're not thread-safe (one per consumer).
/// </summary>
public class Cursor : IEnumerable<Row>
{
    private   readonly PageFile        _file;
    private   readonly TableDefinition _definition;
    private   readonly Table           _table;
    private   readonly List<int>       _ownedPages;
    private   readonly RowDecoder      _decoder;

    // Members IndexCursor needs for index-driven random access.
    internal PageFile        File       => _file;
    internal TableDefinition Definition => _definition;
    internal RowDecoder      Decoder    => _decoder;

    private int  _pageIndex;
    private int  _rowIndex;
    private Row? _currentRow;

    internal Cursor(Table table, PageFile file, TableDefinition definition)
    {
        _table      = table;
        _file       = file;
        _definition = definition;

        var format = _file.Format;
        byte[] umapPage = _file.ReadPage(_definition.UmapPageNumber);
        _ownedPages = UsageMap.GetOwnedPages(umapPage, _definition.OwnedPagesRow, format, _file);

        var lvalReader = _definition.LvalColumnUmapPages.Count > 0 ? new LvalReader(_file) : null;
        _decoder = new RowDecoder(format, _definition.Columns, lvalReader);

        BeforeFirst();
    }

    /// <summary>The table this cursor iterates.</summary>
    public Table Table => _table;

    /// <summary>Repositions the cursor before the first row (next GetNextRow yields row #0).</summary>
    public void BeforeFirst()
    {
        _pageIndex  = -1;
        _rowIndex   = -1;
        _currentRow = null;
    }

    /// <summary>Repositions the cursor after the last row (next GetPreviousRow yields the final row).</summary>
    public void AfterLast()
    {
        _pageIndex  = _ownedPages.Count;
        _rowIndex   = 0;
        _currentRow = null;
    }

    /// <summary>Returns the row at the current position, or null if positioned at a boundary.</summary>
    public Row? GetCurrentRow() => _currentRow;

    /// <summary>
    /// Advances to the next live (non-deleted, non-overflow) row and returns it.
    /// Returns null when there are no more rows.
    /// </summary>
    public Row? GetNextRow()
    {
        // Re-anchor onto the next slot, skipping deleted/overflow rows transparently.
        int curPageIdx = _pageIndex;
        int curRowIdx  = _rowIndex + 1;

        if (curPageIdx < 0) { curPageIdx = 0; curRowIdx = 0; }

        while (curPageIdx < _ownedPages.Count)
        {
            byte[] page     = _file.ReadPage(_ownedPages[curPageIdx]);
            int    rowCount = ByteUtil.GetShort(page, _file.Format.OffsetDataNumRows);

            while (curRowIdx < rowCount)
            {
                Row? r = TryReadRow(page, curRowIdx);
                if (r is not null)
                {
                    _pageIndex  = curPageIdx;
                    _rowIndex   = curRowIdx;
                    _currentRow = r;
                    return r;
                }
                curRowIdx++;
            }
            curPageIdx++;
            curRowIdx = 0;
        }

        // Walked off the end.
        _pageIndex  = _ownedPages.Count;
        _rowIndex   = 0;
        _currentRow = null;
        return null;
    }

    /// <summary>
    /// Steps to the previous live row and returns it. Returns null at the beginning.
    /// </summary>
    public Row? GetPreviousRow()
    {
        int curPageIdx = _pageIndex;
        int curRowIdx  = _rowIndex - 1;

        if (curPageIdx >= _ownedPages.Count)
        {
            curPageIdx = _ownedPages.Count - 1;
            if (curPageIdx >= 0)
            {
                byte[] last = _file.ReadPage(_ownedPages[curPageIdx]);
                curRowIdx = ByteUtil.GetShort(last, _file.Format.OffsetDataNumRows) - 1;
            }
        }

        while (curPageIdx >= 0)
        {
            byte[] page = _file.ReadPage(_ownedPages[curPageIdx]);
            while (curRowIdx >= 0)
            {
                Row? r = TryReadRow(page, curRowIdx);
                if (r is not null)
                {
                    _pageIndex  = curPageIdx;
                    _rowIndex   = curRowIdx;
                    _currentRow = r;
                    return r;
                }
                curRowIdx--;
            }
            curPageIdx--;
            if (curPageIdx >= 0)
            {
                byte[] prev = _file.ReadPage(_ownedPages[curPageIdx]);
                curRowIdx = ByteUtil.GetShort(prev, _file.Format.OffsetDataNumRows) - 1;
            }
        }

        BeforeFirst();
        return null;
    }

    /// <summary>
    /// Reads the live row at <paramref name="pageNumber"/>/<paramref name="rowIndex"/>.
    /// Returns null if that slot is deleted, an overflow pointer, or malformed.
    /// Used by <see cref="IndexCursor"/> for index-driven random access.
    /// </summary>
    internal Row? ReadRowAt(int pageNumber, int rowIndex)
    {
        byte[] page = _file.ReadPage(pageNumber);
        return TryReadRow(page, rowIndex);
    }

    /// <summary>
    /// Decodes the row at <paramref name="rowIndex"/> on <paramref name="page"/>.
    /// Returns null if the slot is deleted, an overflow pointer, or malformed.
    /// </summary>
    private Row? TryReadRow(byte[] page, int rowIndex)
    {
        var    format  = _file.Format;
        int    slotOff = format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;
        ushort slotVal = ByteUtil.GetUShort(page, slotOff);
        if ((slotVal & 0x8000) != 0) return null;   // deleted
        if ((slotVal & 0x4000) != 0) return null;   // overflow pointer

        int rowStart = slotVal & JetFormat.RowOffsetMask;
        int rowEnd   = (rowIndex == 0)
            ? format.PageSize
            : (ByteUtil.GetUShort(page,
                   format.OffsetDataRowTable + (rowIndex - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);
        int rowLen = rowEnd - rowStart;
        if (rowLen <= 0) return null;

        var rowBytes = new byte[rowLen];
        Array.Copy(page, rowStart, rowBytes, 0, rowLen);

        var row = new Row();
        foreach (var col in _definition.Columns)
        {
            object? val = _decoder.Decode(rowBytes, col);
            if (val is not null) row[col.Name] = val;
        }
        return row;
    }

    // ── IEnumerable ───────────────────────────────────────────────────────────

    public IEnumerator<Row> GetEnumerator()
    {
        BeforeFirst();
        while (GetNextRow() is { } r) yield return r;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
