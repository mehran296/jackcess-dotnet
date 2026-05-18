using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class CursorTests : IDisposable
{
    private readonly string _path;

    public CursorTests()
        => _path = Path.Combine(Path.GetTempPath(), $"cursor_test_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static IReadOnlyList<Column> PeopleColumns() => new[]
    {
        new ColumnBuilder("Id",   DataType.Long).Build(),
        new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
        new ColumnBuilder("Age",  DataType.Int ).Build(),
    };

    /// <summary>
    /// Creates a fresh db, builds "People" with 5 rows, returns both the live Database
    /// (caller must dispose) and the original Table reference (which retains in-memory
    /// state like PrimaryKeyColumnName that isn't yet persisted in the TDEF).
    /// </summary>
    private (Database db, Table table) OpenWithPeople(string? primaryKey = null)
    {
        var db = Database.Create(_path, JetVersion.Jet4);
        var t  = db.CreateTable("People", PeopleColumns(), primaryKey: primaryKey);
        for (int i = 1; i <= 5; i++)
            t.Insert(new Row { ["Id"] = i, ["Name"] = $"P{i}", ["Age"] = (short)(20 + i) });
        return (db, t);
    }

    [Fact]
    public void Cursor_IteratesAllRowsInOrder()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.NewCursor();

        var ids = new List<int>();
        foreach (var row in c)
            ids.Add(Convert.ToInt32(row["Id"]));

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ids);
    }

    [Fact]
    public void Cursor_GetNextRow_ReturnsNullAfterLast()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.NewCursor();

        for (int i = 0; i < 5; i++) Assert.NotNull(c.GetNextRow());
        Assert.Null(c.GetNextRow());
        Assert.Null(c.GetNextRow());   // idempotent
    }

    [Fact]
    public void Cursor_GetPreviousRow_WalksBackwards()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.NewCursor();

        c.AfterLast();
        var ids = new List<int>();
        while (c.GetPreviousRow() is { } r)
            ids.Add(Convert.ToInt32(r["Id"]));

        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, ids);
    }

    [Fact]
    public void Cursor_BeforeFirst_ResetsIteration()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.NewCursor();

        Assert.NotNull(c.GetNextRow());
        Assert.NotNull(c.GetNextRow());
        c.BeforeFirst();
        Assert.Equal(1, Convert.ToInt32(c.GetNextRow()!["Id"]));
    }

    [Fact]
    public void IndexCursor_FindRow_ByPrimaryKey()
    {
        var (db, t) = OpenWithPeople(primaryKey: "Id");
        using var _ = db;
        var c = t.NewIndexCursor();

        Row? r = c.FindRowByPrimaryKey(3);

        Assert.NotNull(r);
        Assert.Equal("P3", r!["Name"]);
    }

    [Fact]
    public void IndexCursor_FindRow_ByArbitraryColumn_FallsBackToScan()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.NewIndexCursor();

        Row? r = c.FindRow("Name", "P4");

        Assert.NotNull(r);
        Assert.Equal(4, Convert.ToInt32(r!["Id"]));
    }

    [Fact]
    public void IndexCursor_FindRow_NoMatch_ReturnsNull()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        var c = t.NewIndexCursor();

        Assert.Null(c.FindRow("Name", "DoesNotExist"));
    }

    [Fact]
    public void CursorBuilder_StaticShortcuts_Work()
    {
        var (db, t) = OpenWithPeople(primaryKey: "Id");
        using var _ = db;

        var cursor = CursorBuilder.CreateCursor(t);
        Assert.Equal(5, System.Linq.Enumerable.Count(cursor));

        var pkCursor = CursorBuilder.CreatePrimaryKeyCursor(t);
        Assert.NotNull(pkCursor.FindRowByPrimaryKey(2));

        var found = CursorBuilder.FindRowByPrimaryKey(t, 5);
        Assert.Equal("P5", found!["Name"]);
    }

    [Fact]
    public void Cursor_SkipsDeletedRows()
    {
        var (db, t) = OpenWithPeople();
        using var _ = db;
        t.DeleteRow("Id", 3);

        var ids = new List<int>();
        foreach (var row in t.NewCursor())
            ids.Add(Convert.ToInt32(row["Id"]));

        Assert.Equal(new[] { 1, 2, 4, 5 }, ids);
    }

    // ── Corpus-driven cursor smoke test (V2000 + V2003) ───────────────────────

    public static IEnumerable<object[]> CorpusFiles()
    {
        // Reuse CorpusTests' resolution logic by trying the same path heuristic.
        string? root = ResolveCorpusRoot();
        if (root is null) yield break;
        foreach (var ver in new[] { "V2000", "V2003" })
        {
            string dir = Path.Combine(root, ver);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "common1*.mdb"))
                yield return new object[] { ver, Path.GetFileName(file), file };
        }
    }

    private static string? ResolveCorpusRoot()
    {
        const string hardcoded = @"D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data";
        return Directory.Exists(hardcoded) ? hardcoded : null;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Cursor_OnCorpusFile_IteratesAllRows(string version, string filename, string path)
    {
        using var db = Database.Open(path);
        foreach (var name in db.ListTables(includeSystem: false))
        {
            var t = db.GetTable(name);
            var viaScan   = t.ReadAllRows();
            var viaCursor = new List<Row>();
            foreach (var r in t.NewCursor()) viaCursor.Add(r);

            Assert.True(viaScan.Count == viaCursor.Count,
                $"{version}/{filename} table '{name}': scan={viaScan.Count} cursor={viaCursor.Count}");
        }
    }
}
