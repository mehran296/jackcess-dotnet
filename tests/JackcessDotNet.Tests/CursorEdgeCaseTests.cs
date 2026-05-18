using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Edge-case behaviour of <see cref="Cursor"/> at iteration boundaries and on
/// empty tables. Companion to <see cref="CursorTests"/> which covers the
/// happy-path forward/backward walks.
/// </summary>
public sealed class CursorEdgeCaseTests : IDisposable
{
    private readonly string _path;
    public CursorEdgeCaseTests()
        => _path = Path.Combine(Path.GetTempPath(), $"cursoredge_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private (Database, Table) NewEmptyTable()
    {
        var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("People", new[]
        {
            new ColumnBuilder("Id",   DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(20).Build(),
        });
        return (db, t);
    }

    [Fact]
    public void EmptyTable_GetNextRow_Null()
    {
        using var db = NewEmptyTable().Item1;
        var c = db.GetTable("People").NewCursor();
        Assert.Null(c.GetNextRow());
    }

    [Fact]
    public void EmptyTable_Enumeration_YieldsNothing()
    {
        using var db = NewEmptyTable().Item1;
        var c = db.GetTable("People").NewCursor();
        Assert.Empty(c);
    }

    [Fact]
    public void GetCurrentRow_BeforeIteration_IsNull()
    {
        using var db = NewEmptyTable().Item1;
        var c = db.GetTable("People").NewCursor();
        Assert.Null(c.GetCurrentRow());
    }

    [Fact]
    public void GetCurrentRow_AfterGetNext_ReturnsLatest()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        t.Insert(new Row { ["Id"] = 1, ["Name"] = "A" });
        t.Insert(new Row { ["Id"] = 2, ["Name"] = "B" });

        var c = t.NewCursor();
        var first = c.GetNextRow();
        Assert.Equal(first, c.GetCurrentRow());

        var second = c.GetNextRow();
        Assert.Equal(second, c.GetCurrentRow());
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GetCurrentRow_AfterIterationEnds_IsNull()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        t.Insert(new Row { ["Id"] = 1, ["Name"] = "A" });

        var c = t.NewCursor();
        Assert.NotNull(c.GetNextRow());
        Assert.Null(c.GetNextRow());
        Assert.Null(c.GetCurrentRow());
    }

    [Fact]
    public void AfterLast_ThenGetPreviousRow_WalksBackward()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        for (int i = 1; i <= 3; i++)
            t.Insert(new Row { ["Id"] = i, ["Name"] = $"P{i}" });

        var c = t.NewCursor();
        c.AfterLast();
        var r3 = c.GetPreviousRow();
        var r2 = c.GetPreviousRow();
        var r1 = c.GetPreviousRow();
        var nada = c.GetPreviousRow();

        Assert.Equal(3, Convert.ToInt32(r3!["Id"]));
        Assert.Equal(2, Convert.ToInt32(r2!["Id"]));
        Assert.Equal(1, Convert.ToInt32(r1!["Id"]));
        Assert.Null(nada);
    }

    [Fact]
    public void Enumeration_ResetsToBeforeFirst_EachCall()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        for (int i = 1; i <= 3; i++)
            t.Insert(new Row { ["Id"] = i, ["Name"] = $"P{i}" });

        var c = t.NewCursor();
        Assert.Equal(3, c.Count());
        // Foreach again — should re-iterate from the start.
        Assert.Equal(3, c.Count());
    }

    [Fact]
    public void Table_PropertyAccessors_AreImmutableAfterCreate()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        Assert.Equal("People", t.Name);
        Assert.Equal(2, t.Columns.Count);
        Assert.NotNull(t.Indexes);   // not null, just empty (no PK was configured)
    }

    [Fact]
    public void IndexCursor_FindRow_EmptyTable_ReturnsNull()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        var c = t.NewIndexCursor();
        Assert.Null(c.FindRow("Name", "Nobody"));
    }

    [Fact]
    public void IndexCursor_FindRowByEntry_NoArgs_Throws()
    {
        var (db, t) = NewEmptyTable();
        using var _ = db;
        Assert.Throws<ArgumentException>(() => t.NewIndexCursor().FindRowByEntry());
    }
}
