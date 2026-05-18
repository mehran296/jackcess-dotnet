namespace JackcessDotNet;

/// <summary>
/// Represents a row of data to be inserted into a table.
/// Values are keyed by column name (case-insensitive).
/// </summary>
public sealed class Row : Dictionary<string, object?>
{
    public Row() : base(StringComparer.OrdinalIgnoreCase) { }

    public Row(IEnumerable<KeyValuePair<string, object?>> values)
        : base(StringComparer.OrdinalIgnoreCase)
    {
        foreach (var (key, value) in values)
            Add(key, value);
    }

    /// <summary>Fluent setter for chaining.</summary>
    public Row Set(string column, object? value)
    {
        this[column] = value;
        return this;
    }

    /// <summary>Creates a Row from positional values matched to column names.</summary>
    public static Row FromValues(IReadOnlyList<Column> columns, params object?[] values)
    {
        if (values.Length > columns.Count)
            throw new ArgumentException(
                $"Too many values: got {values.Length}, table has {columns.Count} columns.");

        var row = new Row();
        for (int i = 0; i < values.Length; i++)
            row[columns[i].Name] = values[i];
        return row;
    }
}
