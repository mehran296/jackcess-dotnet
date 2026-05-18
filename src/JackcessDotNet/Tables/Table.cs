using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Represents an open user table and exposes row-insertion operations.
/// Instances are created by <see cref="Database.CreateTable"/> or
/// <see cref="Database.GetTable"/>.
/// </summary>
public sealed class Table
{
    private readonly PageFile       _file;
    private readonly PageAllocator  _allocator;
    private readonly TableDefinition _definition;
    private readonly DataPageWriter  _dataWriter;
    private readonly Database?       _owningDb;

    internal Table(PageFile file, PageAllocator allocator, TableDefinition definition,
                   Database? owningDb = null)
    {
        _file        = file        ?? throw new ArgumentNullException(nameof(file));
        _allocator   = allocator   ?? throw new ArgumentNullException(nameof(allocator));
        _definition  = definition  ?? throw new ArgumentNullException(nameof(definition));
        _dataWriter  = new DataPageWriter(file, allocator);
        _owningDb    = owningDb;
    }

    // ── Public properties ─────────────────────────────────────────────────────

    public string                Name    => _definition.Name;
    public IReadOnlyList<Column> Columns => _definition.Columns;
    /// <summary>Indexes defined on this table (read from the on-disk TDEF).</summary>
    public IReadOnlyList<Index>  Indexes => _definition.Indexes;

    private PropertyMaps? _propertiesCache;
    /// <summary>
    /// Property maps attached to this table in MSysObjects.LvProp. The
    /// <c>Default</c> map holds table-level properties (Description, Caption, etc.);
    /// per-column properties live in named maps keyed by column name.
    /// Lazily decoded on first access; null bytes → empty <see cref="PropertyMaps"/>.
    /// </summary>
    public PropertyMaps Properties
    {
        get
        {
            if (_propertiesCache is not null) return _propertiesCache;
            var catalog = new SystemCatalog(_file);
            byte[]? bytes = catalog.GetPropertyBytesForObject(_definition.TdefPageNumber);
            _propertiesCache = PropertyMapReader.Read(bytes, _file.Format);
            return _propertiesCache;
        }
    }

    // ── Cursor factories ──────────────────────────────────────────────────────

    /// <summary>Creates a sequential table-scan cursor (Jackcess-shape API).</summary>
    public Cursor      NewCursor()                               => NewCursorInternal();

    /// <summary>Creates an index cursor that supports FindRow / FindRowByPrimaryKey.</summary>
    public IndexCursor NewIndexCursor(string? indexName = null)  => NewIndexCursorInternal(indexName);

    internal Cursor      NewCursorInternal()                       => new Cursor(this, _file, _definition);
    internal IndexCursor NewIndexCursorInternal(string? indexName) => new IndexCursor(this, _file, _definition, indexName);

    // ── Row operations ────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes and inserts <paramref name="row"/> into the table.
    /// A new data page is allocated automatically if the current one is full.
    /// The total row count in the table's TDEF is updated on every insert.
    /// When the table has a primary-key column configured, a leaf entry is also
    /// appended to the PK index page so <see cref="IndexCursor.FindRow"/> can use it.
    /// </summary>
    public void Insert(Row row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));

        _owningDb?.ValidateForeignKeysForInsert(this, row);

        int rowPtr = _dataWriter.InsertRow(_definition, row);
        _dataWriter.IncrementTdefRowCount(_definition.TdefPageNumber);
        MaybeAddPrimaryKeyIndexEntry(row, rowPtr);
    }

    /// <summary>
    /// Updates the row with the given primary-key value.
    /// Old LVAL chunks are freed and their space is reclaimed before the new row is written.
    /// A new PK index entry is appended pointing at the rewritten row; the old entry is left
    /// behind as a stale pointer that <see cref="IndexCursor"/> filters out via slot/PK checks.
    /// </summary>
    public void UpdateByPrimaryKey(object primaryKeyValue, Row newValues)
    {
        int newRowPtr = _dataWriter.UpdateRowByPrimaryKey(_definition, primaryKeyValue, newValues);

        // The merged row that was actually written carries the (unchanged) PK value, so we can
        // index it under primaryKeyValue without inspecting newValues for the PK column.
        if (_definition.PrimaryKeyColumnName is not null && _definition.PrimaryKeyIndexPage > 0)
            new IndexWriter(_file, _allocator)
                .InsertPrimaryKey(_definition, primaryKeyValue, newRowPtr);
    }

    private void MaybeAddPrimaryKeyIndexEntry(Row row, int rowPtr)
    {
        if (_definition.PrimaryKeyIndexPage == 0) return;

        var pkCols = _definition.EffectivePrimaryKeyColumns;
        if (pkCols.Count == 0) return;

        var writer = new IndexWriter(_file, _allocator);
        if (pkCols.Count == 1)
        {
            // Single-column path: missing-or-null skips the index entry, matching
            // the existing (pre-composite) behaviour exactly.
            if (!row.TryGetValue(pkCols[0], out var pkValue) || pkValue is null) return;
            writer.InsertPrimaryKey(_definition, pkValue, rowPtr);
        }
        else
        {
            // Composite path: every component must be present + non-null.
            var values = new object?[pkCols.Count];
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (!row.TryGetValue(pkCols[i], out var v) || v is null) return;
                values[i] = v;
            }
            writer.InsertPrimaryKey(_definition, values, rowPtr);
        }
    }

    /// <summary>
    /// Deletes the first row where <paramref name="columnName"/> equals <paramref name="value"/>.
    /// Any LVAL page chains held by the deleted row are freed and their space is reclaimed.
    /// The TDEF row count is decremented by one.
    /// </summary>
    public void DeleteRow(string columnName, object value)
    {
        _dataWriter.DeleteRow(_definition, columnName, value);
        _dataWriter.IncrementTdefRowCount(_definition.TdefPageNumber, -1);
    }

    /// <summary>
    /// Reads and decodes every non-deleted row in the table and returns them in
    /// insertion order.  Follows LVAL page chains for Memo/OLE columns.
    /// </summary>
    public IReadOnlyList<Row> ReadAllRows()
    {
        var format     = _file.Format;
        var lvalReader = _definition.LvalColumnUmapPages.Count > 0
            ? new LvalReader(_file)
            : null;
        var decoder = new RowDecoder(format, _definition.Columns, lvalReader);

        byte[] umapPage  = _file.ReadPage(_definition.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, _definition.OwnedPagesRow, format, _file);

        var result = new List<Row>();
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
                    : (ByteUtil.GetUShort(dp,
                           format.OffsetDataRowTable + (r - 1) * JetFormat.SizeRowEntry)
                       & JetFormat.RowOffsetMask);
                int rowLen = rowEnd - rowStart;
                if (rowLen <= 0) continue;

                var rowBytes = new byte[rowLen];
                Array.Copy(dp, rowStart, rowBytes, 0, rowLen);

                var row = new Row();
                foreach (var col in _definition.Columns)
                {
                    object? val = decoder.Decode(rowBytes, col);
                    if (val is not null)
                        row[col.Name] = val;
                }
                result.Add(row);
            }
        }
        return result;
    }
}
