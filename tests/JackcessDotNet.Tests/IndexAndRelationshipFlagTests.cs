using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Bit-pattern coverage for <see cref="Index"/> and <see cref="Relationship"/>
/// flag accessors. Verifies that constructing one of these with specific raw
/// flags exposes the expected boolean properties.
/// </summary>
public sealed class IndexAndRelationshipFlagTests
{
    // ── Index flag bits ───────────────────────────────────────────────────────

    [Fact]
    public void Index_PrimaryKeyTypeByte_FlipsIsPrimaryKey()
    {
        var idx = NewIndex(flags: 0, indexType: 1);   // 1 = primary key
        Assert.True(idx.IsPrimaryKey);
        Assert.False(idx.IsForeignKey);
        Assert.True(idx.IsUnique);   // PK is always unique
    }

    [Fact]
    public void Index_ForeignKeyTypeByte_FlipsIsForeignKey()
    {
        var idx = NewIndex(flags: 0, indexType: 2);
        Assert.True(idx.IsForeignKey);
        Assert.False(idx.IsPrimaryKey);
    }

    [Fact]
    public void Index_UniqueFlag_ExposedAsIsUnique()
    {
        var idx = NewIndex(flags: 0x01, indexType: 0);
        Assert.True(idx.IsUnique);
        Assert.False(idx.IsPrimaryKey);
    }

    [Fact]
    public void Index_IgnoreNullsFlag_Bit2()
    {
        Assert.True (NewIndex(flags: 0x02).IgnoresNulls);
        Assert.False(NewIndex(flags: 0x01).IgnoresNulls);
    }

    [Fact]
    public void Index_RequiredFlag_Bit4()
    {
        Assert.True (NewIndex(flags: 0x08).IsRequired);
        Assert.False(NewIndex(flags: 0x00).IsRequired);
    }

    [Fact]
    public void Index_ToString_DescribesShape()
    {
        var col = new ColumnBuilder("A", DataType.Long).Build();
        col.ColumnNumber = 0;
        var idx = new Index(
            name: "PK", columns: new[] { new IndexColumn(col, 0x01) },
            rootPageNumber: 42, indexNumber: 0, flags: 0x80, indexType: 1);
        string s = idx.ToString();
        Assert.Contains("PK", s);
        Assert.Contains("p42", s);
        Assert.Contains("A",  s);
    }

    [Fact]
    public void IndexColumn_AscendingBit0()
    {
        var col = new ColumnBuilder("A", DataType.Long).Build();
        Assert.True (new IndexColumn(col, 0x01).IsAscending);
        Assert.False(new IndexColumn(col, 0x00).IsAscending);
    }

    // ── Relationship flag bits ───────────────────────────────────────────────

    [Fact] public void Relationship_OneToOne_Bit0()
        => Assert.True (NewRel(flags: 0x00000001).IsOneToOne);

    [Fact] public void Relationship_CascadeUpdates_Bit8()
    {
        Assert.True (NewRel(flags: 0x00000100).CascadeUpdates);
        Assert.False(NewRel(flags: 0x00000001).CascadeUpdates);
    }

    [Fact] public void Relationship_CascadeDeletes_Bit12()
        => Assert.True(NewRel(flags: 0x00001000).CascadeDeletes);

    [Fact] public void Relationship_CascadeNull_Bit13()
        => Assert.True(NewRel(flags: 0x00002000).CascadeNull);

    [Fact] public void Relationship_LeftOuterJoin_Bit24()
        => Assert.True(NewRel(flags: 0x01000000).LeftOuterJoin);

    [Fact] public void Relationship_RightOuterJoin_Bit25()
        => Assert.True(NewRel(flags: 0x02000000).RightOuterJoin);

    [Fact] public void Relationship_MultipleFlagsCombine()
    {
        var r = NewRel(flags: 0x00001101);   // 1-to-1 + cascade updates + cascade deletes
        Assert.True(r.IsOneToOne);
        Assert.True(r.CascadeUpdates);
        Assert.True(r.CascadeDeletes);
        Assert.False(r.CascadeNull);
        Assert.False(r.LeftOuterJoin);
    }

    [Fact] public void Relationship_ToString_DescribesShape()
    {
        var r = NewRel(flags: 0, pairs: new[]
        {
            ("ParentId", "ChildId"),
            ("ParentId2", "ChildId2"),
        });
        string s = r.ToString();
        Assert.Contains("Parents",  s);
        Assert.Contains("Children", s);
        Assert.Contains("ParentId", s);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Index NewIndex(byte flags = 0, byte indexType = 0)
    {
        var col = new ColumnBuilder("A", DataType.Long).Build();
        col.ColumnNumber = 0;
        return new Index(
            name: "I", columns: new[] { new IndexColumn(col, 0x01) },
            rootPageNumber: 1, indexNumber: 0, flags: flags, indexType: indexType);
    }

    private static Relationship NewRel(int flags, (string From, string To)[]? pairs = null)
        => new Relationship(
            name: "R",
            fromTable: "Parents", toTable: "Children",
            flags: flags,
            columnPairs: (pairs ?? new[] { ("PId", "CId") }).ToList());
}
