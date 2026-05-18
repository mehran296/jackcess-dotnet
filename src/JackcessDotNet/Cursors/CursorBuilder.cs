namespace JackcessDotNet;

/// <summary>
/// Fluent factory for <see cref="Cursor"/> and <see cref="IndexCursor"/>.
///
/// Usage:
/// <code>
///   var cursor = new CursorBuilder(table).ToCursor();
///   foreach (var row in cursor) { ... }
///
///   var pkCursor = new CursorBuilder(table)
///       .WithIndex("PrimaryKey")
///       .ToIndexCursor();
///   Row? r = pkCursor.FindRowByPrimaryKey(42);
/// </code>
/// </summary>
public sealed class CursorBuilder
{
    private readonly Table   _table;
    private          string? _indexName;

    public CursorBuilder(Table table)
        => _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <summary>Selects an index by name (case-insensitive). Pass null for primary key / no index.</summary>
    public CursorBuilder WithIndex(string? indexName)
    {
        _indexName = indexName;
        return this;
    }

    /// <summary>Builds a plain table-scan cursor.</summary>
    public Cursor ToCursor()
        => _table.NewCursorInternal();

    /// <summary>Builds an index-backed cursor (falls back to scan if no usable index).</summary>
    public IndexCursor ToIndexCursor()
        => _table.NewIndexCursorInternal(_indexName);

    // ── Static shortcuts (mirror Jackcess Java's CursorBuilder.createXxx) ────

    public static Cursor       CreateCursor(Table table)
        => new CursorBuilder(table).ToCursor();

    public static IndexCursor  CreatePrimaryKeyCursor(Table table)
        => new CursorBuilder(table).WithIndex("PrimaryKey").ToIndexCursor();

    public static Row?         FindRowByPrimaryKey(Table table, object pkValue)
        => CreatePrimaryKeyCursor(table).FindRowByPrimaryKey(pkValue);
}
