namespace JackcessDotNet;

/// <summary>
/// A foreign-key / join relationship between two tables. Parsed from
/// MSysRelationships; one row per column pair (a composite relationship is
/// reassembled from rows that share <see cref="Name"/>).
///
/// Read-only today; the on-disk row format is supported but write-side
/// (creating/dropping relationships, FK enforcement on Insert) is not yet
/// hooked in.
/// </summary>
public sealed class Relationship
{
    /// <summary>Relationship name (e.g. "{guid}", "Customers_Orders").</summary>
    public string Name { get; }

    /// <summary>Parent (referenced) table — szReferencedObject in MSysRelationships.</summary>
    public string FromTable { get; }

    /// <summary>Child (referencing) table — szObject in MSysRelationships.</summary>
    public string ToTable { get; }

    /// <summary>
    /// Column pairs in this relationship, in icolumn order. For a single-column
    /// FK this list has one entry; composites have multiple.
    /// </summary>
    public IReadOnlyList<(string FromColumn, string ToColumn)> ColumnPairs { get; }

    /// <summary>Raw grbit flags from MSysRelationships.</summary>
    public int Flags { get; }

    public bool IsOneToOne     => (Flags & 0x00000001) != 0;
    public bool CascadeUpdates => (Flags & 0x00000100) != 0;
    public bool CascadeDeletes => (Flags & 0x00001000) != 0;
    public bool CascadeNull    => (Flags & 0x00002000) != 0;
    public bool LeftOuterJoin  => (Flags & 0x01000000) != 0;
    public bool RightOuterJoin => (Flags & 0x02000000) != 0;

    internal Relationship(
        string name, string fromTable, string toTable, int flags,
        IReadOnlyList<(string From, string To)> columnPairs)
    {
        Name        = name;
        FromTable   = fromTable;
        ToTable     = toTable;
        Flags       = flags;
        ColumnPairs = columnPairs;
    }

    public override string ToString()
        => $"{Name}: {FromTable}({string.Join(",", ColumnPairs.Select(p => p.FromColumn))})" +
           $" → {ToTable}({string.Join(",", ColumnPairs.Select(p => p.ToColumn))})";
}
