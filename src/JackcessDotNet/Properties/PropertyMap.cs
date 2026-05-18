namespace JackcessDotNet;

/// <summary>
/// A named collection of typed (name → value) properties attached to a
/// catalog object — typically a table or a column.
///
/// Access stores well-known property names like:
///   "Description", "Caption", "Format", "DefaultValue", "ValidationRule",
///   "InputMask", "Required", "AllowZeroLength", "DecimalPlaces"
///
/// This is the lightweight read-side model. Mutation works in memory but
/// is not yet persisted back to disk (see <see cref="Table.Properties"/>
/// for the entry point).
/// </summary>
public sealed class PropertyMap
{
    /// <summary>
    /// PropertyMap name: the empty string for "default" (table-level) properties,
    /// or the column name for per-column properties.
    /// </summary>
    public string Name { get; }

    /// <summary>Block-type byte from the on-disk stream (0x00 default, 0x01 column).</summary>
    public byte Type { get; }

    private readonly Dictionary<string, Property> _props = new(StringComparer.OrdinalIgnoreCase);

    internal PropertyMap(string name, byte type) { Name = name; Type = type; }

    /// <summary>Returns the value of <paramref name="propertyName"/>, or null if absent.</summary>
    public object? this[string propertyName]
        => _props.TryGetValue(propertyName, out var p) ? p.Value : null;

    /// <summary>Enumerates the contained properties in insertion order.</summary>
    public IReadOnlyCollection<Property> Properties => _props.Values;

    /// <summary>True when no properties are attached.</summary>
    public bool IsEmpty => _props.Count == 0;

    internal void Add(Property prop) => _props[prop.Name] = prop;

    public override string ToString()
        => $"{(string.IsNullOrEmpty(Name) ? "<default>" : Name)} {{ {string.Join(", ", _props.Values)} }}";

    /// <summary>One named, typed property within a <see cref="PropertyMap"/>.</summary>
    public sealed record Property(string Name, DataType DataType, object? Value, bool IsDdl)
    {
        public override string ToString() => $"{Name}({DataType})={Value}";
    }
}

/// <summary>
/// All property maps attached to a single catalog object (its default map plus
/// one map per column-with-properties). Keyed by map name (case-insensitive).
/// </summary>
public sealed class PropertyMaps : System.Collections.Generic.IEnumerable<PropertyMap>
{
    private readonly Dictionary<string, PropertyMap> _maps =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The unnamed (table-level) property map.</summary>
    public PropertyMap Default
        => _maps.TryGetValue(string.Empty, out var m) ? m : _empty;
    private static readonly PropertyMap _empty = new(string.Empty, 0x00);

    /// <summary>Returns the map for <paramref name="name"/> (typically a column name) or null.</summary>
    public PropertyMap? Get(string name)
        => _maps.TryGetValue(name, out var m) ? m : null;

    /// <summary>True if there are no property maps at all.</summary>
    public bool IsEmpty => _maps.Count == 0;

    /// <summary>Number of maps (default + per-column).</summary>
    public int Count => _maps.Count;

    internal void Add(PropertyMap map) => _maps[map.Name] = map;

    public System.Collections.Generic.IEnumerator<PropertyMap> GetEnumerator()
        => _maps.Values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => GetEnumerator();
}
