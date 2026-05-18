using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Reflection;

namespace JackcessDotNet;

/// <summary>
/// High-level read helpers that materialise an Access table into a
/// <see cref="DataTable"/>, every user table into a <see cref="DataSet"/>,
/// or an <c>IEnumerable&lt;T&gt;</c> of POCO instances. Counterpart to
/// <see cref="DatabaseImporter"/>.
///
/// <para>Type mapping (Jet → CLR) mirrors what <see cref="Table.ReadAllRows"/>
/// already produces — Boolean→bool, Int→short, Long→int, Money/Numeric→decimal,
/// ShortDateTime→DateTime, Text/Memo→string, Binary/OLE→byte[], Guid→Guid.</para>
///
/// <para>The <c>IEnumerable&lt;T&gt;</c> path matches Access columns to
/// public writable properties on <c>T</c> by name (case-insensitive).
/// <see cref="ColumnAttribute"/> renames; <see cref="NotMappedAttribute"/> skips;
/// Nullable / enum / <see cref="DateTimeOffset"/> properties are coerced
/// from the underlying boxed value.  Columns with no matching property are
/// silently ignored, and properties whose column is missing keep their default.</para>
/// </summary>
public static class DatabaseExporter
{
    // ── DataTable / DataSet ──────────────────────────────────────────────────

    /// <summary>
    /// Reads <paramref name="tableName"/> in full and returns it as an
    /// in-memory <see cref="DataTable"/>. The PK (when present and single-column)
    /// is set on <see cref="DataTable.PrimaryKey"/>; column <see cref="DataColumn.MaxLength"/>
    /// is filled in for Text columns.
    /// </summary>
    public static DataTable ExportToDataTable(this Database db, string tableName)
    {
        if (db is null)        throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name must not be empty.", nameof(tableName));

        return BuildDataTable(db.GetTable(tableName));
    }

    /// <summary>
    /// Reads every user table (and optionally system tables) into a single
    /// <see cref="DataSet"/>. Tables are named after the Access table name.
    /// </summary>
    public static DataSet ExportToDataSet(this Database db, bool includeSystem = false)
    {
        if (db is null) throw new ArgumentNullException(nameof(db));

        var ds = new DataSet();
        foreach (var name in db.ListTables(includeSystem))
            ds.Tables.Add(BuildDataTable(db.GetTable(name)));
        return ds;
    }

    private static DataTable BuildDataTable(Table table)
    {
        var dt = new DataTable(table.Name);

        // Columns first — order matches the on-disk TDEF.
        // AllowDBNull stays true even when the Access column is marked Required:
        // ReadAllRows skips null values from the dict, so a row missing the key
        // would otherwise be rejected by the DataTable on Add. The exported
        // DataTable is a data view, not a schema mirror.
        foreach (var col in table.Columns)
        {
            Type clr = MapToClrType(col.DataType);
            var dc = new DataColumn(col.Name, clr);
            if (col.DataType == DataType.Text && col.Length > 0)
                dc.MaxLength = col.Length / 2;     // Length is bytes (UTF-16); expose chars
            dt.Columns.Add(dc);
        }

        // Materialise rows first so we can decide whether the PK is safe to set:
        // assigning DataTable.PrimaryKey flips the column's AllowDBNull back to
        // false, which then rejects rows whose PK value is missing (e.g. some
        // MSysObjects rows that come back with a null Id from ReadAllRows).
        var sourceRows = table.ReadAllRows();

        // Single-column PK round-trip — multi-column PKs are skipped (DataTable
        // supports them, but Jet only round-trips one PK per table cleanly).
        var pkIndex = table.Indexes.FirstOrDefault(i => i.IsPrimaryKey);
        if (pkIndex is not null && pkIndex.Columns.Count == 1)
        {
            string pkName = pkIndex.Columns[0].Column.Name;
            bool allHavePk = sourceRows.All(r =>
                r.TryGetValue(pkName, out var v) && v is not null);
            if (allHavePk && dt.Columns[pkName] is DataColumn pkCol)
                dt.PrimaryKey = new[] { pkCol };
        }

        foreach (var row in sourceRows)
        {
            var dr = dt.NewRow();
            foreach (DataColumn dc in dt.Columns)
            {
                row.TryGetValue(dc.ColumnName, out var v);
                dr[dc] = v ?? (object)DBNull.Value;
            }
            dt.Rows.Add(dr);
        }
        dt.AcceptChanges();   // freshly loaded rows aren't dirty
        return dt;
    }

    // ── IEnumerable<T> ───────────────────────────────────────────────────────

    /// <summary>
    /// Streams <paramref name="tableName"/> as <c>T</c> instances.
    /// Table name defaults to <c>typeof(T).Name</c>.
    /// Rows are yielded lazily — keeps memory bounded for large tables and
    /// lets the caller stop early via <c>Take</c>/<c>FirstOrDefault</c>.
    /// </summary>
    public static IEnumerable<T> ExportToCollection<T>(this Database db, string? tableName = null)
        where T : new()
    {
        if (db is null) throw new ArgumentNullException(nameof(db));

        var table = db.GetTable(tableName ?? typeof(T).Name);
        return EnumerateAsCollection<T>(table);
    }

    private static IEnumerable<T> EnumerateAsCollection<T>(Table table) where T : new()
    {
        var bindings = BuildPropertyBindings<T>(table);

        foreach (var row in table.NewCursor())
        {
            var instance = new T();
            foreach (var (colName, prop) in bindings)
            {
                if (!row.TryGetValue(colName, out var value) || value is null) continue;
                prop.SetValue(instance, ConvertForProperty(value, prop.PropertyType));
            }
            yield return instance;
        }
    }

    private static List<(string ColumnName, PropertyInfo Property)> BuildPropertyBindings<T>(Table table)
    {
        var columnNames = new HashSet<string>(
            table.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var bindings = new List<(string, PropertyInfo)>();

        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            if (p.GetCustomAttribute<NotMappedAttribute>() is not null) continue;

            string colName = p.GetCustomAttribute<ColumnAttribute>()?.Name ?? p.Name;
            if (columnNames.Contains(colName))
                bindings.Add((colName, p));
        }
        return bindings;
    }

    // ── CLR / Jet bridging ───────────────────────────────────────────────────

    /// <summary>
    /// Reverse of <see cref="TypeMapper.MapClrType"/>. Picks the CLR type that
    /// the row decoder actually produces for each Jet <see cref="DataType"/>.
    /// Unknown / complex types fall back to <c>object</c>.
    /// </summary>
    internal static Type MapToClrType(DataType dt) => dt switch
    {
        DataType.Boolean       => typeof(bool),
        DataType.Byte          => typeof(byte),
        DataType.Int           => typeof(short),
        DataType.Long          => typeof(int),
        DataType.Money         => typeof(decimal),
        DataType.Float         => typeof(float),
        DataType.Double        => typeof(double),
        DataType.ShortDateTime => typeof(DateTime),
        DataType.Binary        => typeof(byte[]),
        DataType.Text          => typeof(string),
        DataType.Memo          => typeof(string),
        DataType.Ole           => typeof(byte[]),
        DataType.Guid          => typeof(Guid),
        DataType.Numeric       => typeof(decimal),
        DataType.Complex       => typeof(int),
        _                      => typeof(object),
    };

    /// <summary>
    /// Coerces a boxed value coming back from the row decoder into whatever
    /// <paramref name="targetType"/> the receiving property declares. Handles
    /// Nullable&lt;T&gt;, enums (via underlying type), DateTimeOffset (assumed
    /// UTC for naked DateTime), Guid parsed from string, and the usual
    /// widening/narrowing via <see cref="Convert.ChangeType(object?,Type)"/>.
    /// </summary>
    private static object? ConvertForProperty(object value, Type targetType)
    {
        Type t = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (t.IsInstanceOfType(value)) return value;

        if (t.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(t);
            return Enum.ToObject(t, Convert.ChangeType(value, underlying));
        }

        if (t == typeof(DateTimeOffset))
            return value is DateTime dt
                ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))
                : DateTimeOffset.Parse(value.ToString()!);

        if (t == typeof(Guid))
            return value is Guid g ? g : Guid.Parse(value.ToString()!);

        if (t == typeof(string))
            return value as string ?? value.ToString();

        return Convert.ChangeType(value, t);
    }
}
