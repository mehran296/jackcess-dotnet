namespace JackcessDotNet;

/// <summary>
/// Read-only metadata for a real (B-tree) index defined on a table.
///
/// Each entry in <see cref="Columns"/> participates in the composite sort key,
/// in declaration order. Index trees rooted at <see cref="RootPageNumber"/> are
/// not yet traversed by this library for Access-authored files (see B-step2/3);
/// the metadata is exposed today so callers can introspect schemas and so the
/// future B-tree walker has the entry points it needs.
/// </summary>
public sealed class Index
{
    /// <summary>Index name (e.g. "PrimaryKey", "ByLastName"); never null.</summary>
    public string Name { get; }

    /// <summary>Columns participating in the sort key, in declaration order.</summary>
    public IReadOnlyList<IndexColumn> Columns { get; }

    /// <summary>Page number of the index's root page (node or leaf).</summary>
    public int RootPageNumber { get; }

    /// <summary>
    /// Ordinal of this index inside the TDEF (matches the index slot's
    /// <c>indexDataNumber</c>, which the foreign-key reference points at).
    /// </summary>
    public int IndexNumber { get; }

    /// <summary>Raw index-flags byte from the index column block.</summary>
    public byte Flags { get; }

    /// <summary>Index type byte from the logical-index slot (1 = primary key, 2 = foreign key, 0 = regular).</summary>
    public byte IndexType { get; }

    public bool IsPrimaryKey => IndexType == 1;
    public bool IsForeignKey => IndexType == 2;
    public bool IsUnique     => IsPrimaryKey || (Flags & 0x01) != 0;
    public bool IgnoresNulls => (Flags & 0x02) != 0;
    public bool IsRequired   => (Flags & 0x08) != 0;

    internal Index(string name, IReadOnlyList<IndexColumn> columns, int rootPageNumber,
                   int indexNumber, byte flags, byte indexType)
    {
        Name           = name;
        Columns        = columns;
        RootPageNumber = rootPageNumber;
        IndexNumber    = indexNumber;
        Flags          = flags;
        IndexType      = indexType;
    }

    public override string ToString()
        => $"{Name} [{IndexType switch { 1 => "PK", 2 => "FK", _ => "IDX" }}] " +
           $"on ({string.Join(", ", Columns)}) root=p{RootPageNumber}";
}

/// <summary>
/// One column participating in an index's composite sort key.
/// </summary>
public sealed class IndexColumn
{
    /// <summary>The table column being indexed.</summary>
    public Column Column { get; }

    /// <summary>Raw flags byte from the index column block.</summary>
    public byte Flags { get; }

    /// <summary>True when the column is sorted ascending in this index (bit 0 of <see cref="Flags"/>).</summary>
    public bool IsAscending => (Flags & 0x01) != 0;

    internal IndexColumn(Column column, byte flags)
    {
        Column = column;
        Flags  = flags;
    }

    public override string ToString()
        => $"{Column.Name}{(IsAscending ? " ASC" : " DESC")}";
}
