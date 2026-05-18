namespace JackcessDotNet;

/// <summary>
/// Cursor that supports key-based row lookup.
///
/// Lookup strategy:
///   1. If this is the primary-key cursor and the table was created by this library
///      (TableDefinition.PrimaryKeyIndexPage &gt; 0), use the existing
///      <see cref="IndexWriter.FindRowByPrimaryKey"/> single-leaf scan.
///   2. Otherwise — including for indexes that exist on disk but weren't authored by us —
///      fall back to a forward table scan that compares column values.
///
/// B-tree traversal for Access-authored indexes is not implemented yet; for those
/// the cursor still returns correct results, just at O(n).
/// </summary>
public sealed class IndexCursor : Cursor
{
    private readonly Table           _table;
    private readonly TableDefinition _definition;
    private readonly PageFile        _file;
    private readonly string?         _indexName;
    private readonly bool            _isPrimaryKey;

    internal IndexCursor(Table table, PageFile file, TableDefinition definition, string? indexName)
        : base(table, file, definition)
    {
        _table        = table;
        _file         = file;
        _definition   = definition;
        _indexName    = indexName;
        _isPrimaryKey = indexName is null
                     || string.Equals(indexName, "PrimaryKey", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(indexName, definition.PrimaryKeyColumnName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the first row whose primary key equals <paramref name="pkValue"/>.
    /// Returns null if no such row exists.
    /// </summary>
    public Row? FindRowByPrimaryKey(object pkValue)
    {
        if (_definition.PrimaryKeyColumnName is null)
            throw new InvalidOperationException(
                $"Table '{_definition.Name}' has no primary key column. " +
                "Use FindRowByEntry on a non-PK index, or use FindRow(column, value) for a column scan.");
        return FindRow(_definition.PrimaryKeyColumnName, pkValue);
    }

    /// <summary>
    /// Convenience: finds the first row where <paramref name="columnName"/> equals
    /// <paramref name="value"/>. Uses the primary-key index leaf when possible,
    /// otherwise falls back to a forward table scan from BeforeFirst.
    /// </summary>
    public Row? FindRow(string columnName, object value)
    {
        // Fast path #1: PK index leaf written by our own IndexWriter.
        // Access uses prefix-compression on most real indexes — those entries can't be
        // matched by EnumerateRowPointersForKey since the on-disk bytes have a shared
        // prefix stripped. We detect compression via the leaf's compressed-byte-count
        // field and skip this path when non-zero, deferring to IndexReader (path #2).
        bool path1Eligible =
            _isPrimaryKey
            && _definition.PrimaryKeyIndexPage > 0
            && _definition.PrimaryKeyColumnName is not null
            && string.Equals(columnName, _definition.PrimaryKeyColumnName, StringComparison.OrdinalIgnoreCase)
            && LeafIsUncompressed(_definition.PrimaryKeyIndexPage);
        if (path1Eligible)
        {
            var iw = new IndexWriter(_file, new PageAllocator(_file));
            foreach (int rowPtr in iw.EnumerateRowPointersForKey(_definition, value))
            {
                // pageNum is the upper 24 bits (3-byte LE page) and rowNum the low byte.
                int pageNum = (rowPtr >> 16) & 0xFFFFFF;
                int rowIdx  = rowPtr        & 0xFF;

                // Defensive: only attempt to materialise the row if pageNum points to
                // a real data page. Spurious "matches" against compressed Access leaves
                // can produce nonsense rowPtrs that would otherwise crash ReadRowAt.
                if (!IsLikelyDataPage(pageNum)) continue;

                Row? r;
                try { r = ReadRowAt(pageNum, rowIdx); }
                catch { continue; }
                if (r is null) continue;
                if (r.TryGetValue(columnName, out var stored) && ValuesEqual(stored, value))
                    return r;
            }
            // fall through
        }

        // Fast path #2: on a table read from disk, walk the real B-tree index whose
        // single column matches the requested column and whose key type we support.
        if (TryFindMatchingDiskIndex(columnName, out var diskIx))
        {
            var reader = new IndexReader(_file, diskIx!);
            foreach (int rowPtr in reader.FindRowPointers(value))
            {
                int pageNum = (rowPtr >> 16) & 0xFFFFFF;
                int rowIdx  = rowPtr        & 0xFF;
                Row? r = ReadRowAt(pageNum, rowIdx);
                if (r is null) continue;
                if (r.TryGetValue(columnName, out var stored) && ValuesEqual(stored, value))
                    return r;
            }
            return null;
        }

        // Slow path: forward table scan.
        BeforeFirst();
        while (GetNextRow() is { } r)
        {
            if (r.TryGetValue(columnName, out var stored) && ValuesEqual(stored, value))
                return r;
        }
        return null;
    }

    /// <summary>
    /// Picks a disk-resident index that's usable for a single-column lookup on
    /// <paramref name="columnName"/>: must have exactly one column, that column must
    /// match by name, and the data type must be one IndexReader knows how to encode.
    /// Returns false when no such index is present (caller falls back to scan).
    /// </summary>
    private bool TryFindMatchingDiskIndex(string columnName, out Index? matched)
    {
        foreach (var ix in _definition.Indexes)
        {
            if (ix.Columns.Count != 1) continue;
            if (ix.RootPageNumber <= 0) continue;
            if (!string.Equals(ix.Columns[0].Column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                continue;
            var dt = ix.Columns[0].Column.DataType;
            if (dt is DataType.Byte or DataType.Int or DataType.Long or DataType.Text)
            {
                matched = ix;
                return true;
            }
        }
        matched = null;
        return false;
    }

    /// <summary>
    /// Composite-key lookup. Matches an entry whose values, in index-column declaration
    /// order, equal <paramref name="entryValues"/>. Uses the B-tree walker when an
    /// index is selected and all its column types are encodable; otherwise scans.
    /// </summary>
    public Row? FindRowByEntry(params object[] entryValues)
    {
        if (entryValues is null || entryValues.Length == 0)
            throw new ArgumentException("Entry values are required.", nameof(entryValues));

        // Index path: prefer an explicitly-named index, else any index whose column
        // count and types match the supplied entry shape.
        Index? ix = SelectIndexForEntry(entryValues.Length);
        if (ix is not null)
        {
            var reader = new IndexReader(_file, ix);
            if (reader.CanResolveKey)
            {
                foreach (int rowPtr in reader.FindRowPointersForEntry(entryValues!))
                {
                    int pageNum = (rowPtr >> 16) & 0xFFFFFF;
                    int rowIdx  = rowPtr        & 0xFF;
                    Row? r = ReadRowAt(pageNum, rowIdx);
                    if (r is null) continue;
                    if (RowMatchesEntry(r, ix, entryValues))
                        return r;
                }
                return null;
            }
        }

        // Scan path: positionally match against the table's columns in column-number order.
        BeforeFirst();
        while (GetNextRow() is { } r)
        {
            bool match = true;
            for (int i = 0; i < entryValues.Length && i < _definition.Columns.Count; i++)
            {
                var col = _definition.Columns[i];
                if (!r.TryGetValue(col.Name, out var stored) || !ValuesEqual(stored, entryValues[i]))
                {
                    match = false; break;
                }
            }
            if (match) return r;
        }
        return null;
    }

    private Index? SelectIndexForEntry(int entryLen)
    {
        // Prefer the explicitly-named index when it fits.
        if (_indexName is not null)
        {
            foreach (var ix in _definition.Indexes)
                if (string.Equals(ix.Name, _indexName, StringComparison.OrdinalIgnoreCase)
                    && ix.Columns.Count == entryLen)
                    return ix;
        }
        // Else any index whose column count matches the supplied entry tuple.
        foreach (var ix in _definition.Indexes)
            if (ix.Columns.Count == entryLen && ix.RootPageNumber > 0)
                return ix;
        return null;
    }

    private static bool RowMatchesEntry(Row row, Index ix, object?[] entry)
    {
        for (int i = 0; i < ix.Columns.Count; i++)
        {
            string col = ix.Columns[i].Column.Name;
            if (!row.TryGetValue(col, out var stored) || !ValuesEqual(stored, entry[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// True if the index leaf at <paramref name="pageNumber"/> has zero compressed-prefix
    /// bytes — meaning entries are stored full-length and our <see cref="IndexWriter"/>
    /// reader can scan them directly. Access leaves typically have non-zero compression.
    /// </summary>
    private bool LeafIsUncompressed(int pageNumber)
    {
        try
        {
            byte[] page = _file.ReadPage(pageNumber);
            if (page[0] != JetFormat.PageTypeIndexLeaf) return false;
            int compressed = JackcessDotNet.Util.ByteUtil.GetUShort(page, _file.Format.OffsetIndexCompressedByteCount);
            return compressed == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sanity-checks that <paramref name="pageNumber"/> points at a data page (type 0x01)
    /// within file bounds. Returns false on any error so spurious rowPtrs from
    /// compressed Access leaves can't crash <see cref="ReadRowAt"/>.
    /// </summary>
    private bool IsLikelyDataPage(int pageNumber)
    {
        if (pageNumber <= 0) return false;
        try
        {
            byte[] page = _file.ReadPage(pageNumber);
            return page[0] == JetFormat.PageTypeData;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValuesEqual(object? stored, object? requested)
    {
        if (Equals(stored, requested)) return true;
        if (stored is null || requested is null) return false;
        try   { return Convert.ToDecimal(stored) == Convert.ToDecimal(requested); }
        catch { return false; }
    }
}
