using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class SmokeTests : IDisposable
{
    private readonly string _path;

    public SmokeTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"jackcess_test_{Guid.NewGuid():N}.mdb");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<Column> PeopleColumns() => new[]
    {
        new ColumnBuilder("Id",   DataType.Long).Build(),
        new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
        new ColumnBuilder("Age",  DataType.Int ).Build(),
    };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_WritesFile()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        Assert.True(File.Exists(_path));
        Assert.True(new FileInfo(_path).Length > 0);
    }

    [Fact]
    public void CreateTable_DoesNotThrow()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("People", PeopleColumns());
        Assert.Equal("People", table.Name);
        Assert.Equal(3, table.Columns.Count);
    }

    [Fact]
    public void Insert_SingleRow_DoesNotThrow()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("People", PeopleColumns());

        table.Insert(new Row
        {
            ["Id"]   = 1,
            ["Name"] = "Alice",
            ["Age"]  = (short)30,
        });
    }

    [Fact]
    public void Insert_MultipleRows_DoesNotThrow()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("People", PeopleColumns());

        for (int i = 1; i <= 10; i++)
            table.Insert(new Row { ["Id"] = i, ["Name"] = $"Person{i}", ["Age"] = (short)(20 + i) });
    }

    [Fact]
    public void GetTable_AfterReopenDb_ReturnsCorrectColumns()
    {
        // Create and close
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            db.CreateTable("People", PeopleColumns());
        }

        // Re-open and look up
        using var db2   = Database.Open(_path, JetVersion.Jet4);
        var       table = db2.GetTable("People");

        Assert.Equal("People", table.Name);
        Assert.Equal(3, table.Columns.Count);
        Assert.Contains(table.Columns, c => c.Name == "Id"   && c.DataType == DataType.Long);
        Assert.Contains(table.Columns, c => c.Name == "Name" && c.DataType == DataType.Text);
        Assert.Contains(table.Columns, c => c.Name == "Age"  && c.DataType == DataType.Int);
    }

    [Fact]
    public void GetTable_UnknownName_Throws()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        Assert.Throws<InvalidOperationException>(() => db.GetTable("DoesNotExist"));
    }

    [Fact]
    public void Insert_ThenGetTable_CanInsertMore()
    {
        // First session: create + 5 rows
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Items", new[]
            {
                new ColumnBuilder("Id",    DataType.Long).Build(),
                new ColumnBuilder("Label", DataType.Text).MaxLength(80).Build(),
            });
            for (int i = 1; i <= 5; i++)
                t.Insert(new Row { ["Id"] = i, ["Label"] = $"Item {i}" });
        }

        // Second session: open + 5 more rows
        using var db2 = Database.Open(_path, JetVersion.Jet4);
        var t2 = db2.GetTable("Items");
        for (int i = 6; i <= 10; i++)
            t2.Insert(new Row { ["Id"] = i, ["Label"] = $"Item {i}" });
    }

    [Fact]
    public void CreateMultipleTables_AllRegisteredInCatalog()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);

        db.CreateTable("Alpha", new[] { new ColumnBuilder("X", DataType.Long).Build() });
        db.CreateTable("Beta",  new[] { new ColumnBuilder("Y", DataType.Text).MaxLength(20).Build() });

        var alpha = db.GetTable("Alpha");
        var beta  = db.GetTable("Beta");

        Assert.Equal("Alpha", alpha.Name);
        Assert.Equal("Beta",  beta.Name);
    }

    // ── Memo tests ────────────────────────────────────────────────────────────

    private static IReadOnlyList<Column> ArticleColumns() => new[]
    {
        new ColumnBuilder("Id",    DataType.Long).Build(),
        new ColumnBuilder("Title", DataType.Text).MaxLength(100).Build(),
        new ColumnBuilder("Body",  DataType.Memo).Build(),
    };

    [Fact]
    public void CreateTable_WithMemoColumn_DoesNotThrow()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Articles", ArticleColumns());
        Assert.Equal(3, table.Columns.Count);
        Assert.Contains(table.Columns, c => c.Name == "Body" && c.DataType == DataType.Memo);
    }

    [Fact]
    public void Insert_MemoColumn_DoesNotThrow()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Articles", ArticleColumns());
        table.Insert(new Row
        {
            ["Id"]    = 1,
            ["Title"] = "Hello",
            ["Body"]  = "This is a long memo value stored inline.",
        });
    }

    [Fact]
    public void GetTable_WithMemoColumn_ReturnsCorrectSchema()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("Articles", ArticleColumns());

        using var db2  = Database.Open(_path, JetVersion.Jet4);
        var table = db2.GetTable("Articles");

        Assert.Equal(3, table.Columns.Count);
        Assert.Contains(table.Columns, c => c.Name == "Body" && c.DataType == DataType.Memo);
    }

    [Fact]
    public void Insert_LargeMemoValue_DoesNotThrow()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Articles", ArticleColumns());
        string longText = new string('A', 1500);   // 1500 chars = 3000 UTF-16 bytes inline
        table.Insert(new Row { ["Id"] = 1, ["Title"] = "Long", ["Body"] = longText });
    }

    [Fact]
    public void Insert_NullMemoValue_DoesNotThrow()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Articles", ArticleColumns());
        table.Insert(new Row { ["Id"] = 2, ["Title"] = "No body" });
    }

    // ── LVAL round-trip tests ─────────────────────────────────────────────────

    [Fact]
    public void Insert_MemoColumn_RoundTrip_Short()
    {
        const string body = "Hello, world!";

        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Articles", ArticleColumns());
            t.Insert(new Row { ["Id"] = 1, ["Title"] = "Test", ["Body"] = body });
        }

        using var db2  = Database.Open(_path, JetVersion.Jet4);
        var       rows = db2.GetTable("Articles").ReadAllRows();

        Assert.Single(rows);
        Assert.Equal(body, rows[0]["Body"]);
    }

    [Fact]
    public void Insert_MemoColumn_RoundTrip_SingleChunk()
    {
        // 2000 chars × 2 bytes (UTF-16LE) = 4000 bytes < chunkDataCapacity (4076) → one LVAL chunk
        string body = new string('A', 2000);

        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Articles", ArticleColumns());
            t.Insert(new Row { ["Id"] = 1, ["Title"] = "Big", ["Body"] = body });
        }

        using var db2  = Database.Open(_path, JetVersion.Jet4);
        var       rows = db2.GetTable("Articles").ReadAllRows();

        Assert.Single(rows);
        Assert.Equal(body, rows[0]["Body"]);
    }

    [Fact]
    public void Insert_MemoColumn_RoundTrip_MultiChunk()
    {
        // 3000 chars × 2 bytes = 6000 bytes > 4076 → requires 2 LVAL chunks
        string body = new string('Z', 3000);

        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Articles", ArticleColumns());
            t.Insert(new Row { ["Id"] = 1, ["Title"] = "Multi", ["Body"] = body });
        }

        using var db2  = Database.Open(_path, JetVersion.Jet4);
        var       rows = db2.GetTable("Articles").ReadAllRows();

        Assert.Single(rows);
        Assert.Equal(body, rows[0]["Body"]);
    }

    [Fact]
    public void Insert_MemoColumn_RoundTrip_ManyChunks()
    {
        // 10 000 chars × 2 bytes = 20 000 bytes → ceil(20000/4076) = 5 LVAL chunks
        string body = new string('X', 10_000);

        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Articles", ArticleColumns());
            t.Insert(new Row { ["Id"] = 1, ["Title"] = "Many", ["Body"] = body });
        }

        using var db2  = Database.Open(_path, JetVersion.Jet4);
        var       rows = db2.GetTable("Articles").ReadAllRows();

        Assert.Single(rows);
        Assert.Equal(body, rows[0]["Body"]);
    }

    // ── Delete tests ──────────────────────────────────────────────────────────

    [Fact]
    public void DeleteRow_RowIsGone()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("People", PeopleColumns());
        for (int i = 1; i <= 3; i++)
            table.Insert(new Row { ["Id"] = i, ["Name"] = $"Person{i}", ["Age"] = (short)(20 + i) });

        table.DeleteRow("Id", 2);

        var rows = table.ReadAllRows();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.NotEqual(2, Convert.ToInt32(r["Id"])));
    }

    [Fact]
    public void DeleteRow_AllRows_TableIsEmpty()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("People", PeopleColumns());
        for (int i = 1; i <= 3; i++)
            table.Insert(new Row { ["Id"] = i, ["Name"] = $"P{i}", ["Age"] = (short)i });

        table.DeleteRow("Id", 1);
        table.DeleteRow("Id", 2);
        table.DeleteRow("Id", 3);

        Assert.Empty(table.ReadAllRows());
    }

    [Fact]
    public void DeleteRow_WithMemo_RowIsGone()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Articles", ArticleColumns());
        table.Insert(new Row { ["Id"] = 1, ["Title"] = "Keep",   ["Body"] = "stay" });
        table.Insert(new Row { ["Id"] = 2, ["Title"] = "Delete", ["Body"] = "gone" });

        table.DeleteRow("Id", 2);

        var rows = table.ReadAllRows();
        Assert.Single(rows);
        Assert.Equal("stay", rows[0]["Body"]);
    }

    [Fact]
    public void DeleteRow_WithLargeMemo_LvalSpaceReclaimed()
    {
        // Large enough to need multiple LVAL chunks (10 000 chars × 2 bytes = 20 000 bytes → 5 chunks).
        string bigText = new string('G', 10_000);

        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Articles", ArticleColumns());

        table.Insert(new Row { ["Id"] = 1, ["Title"] = "First", ["Body"] = bigText });
        long sizeAfterFirst = new FileInfo(_path).Length;

        // Delete the row; LVAL GC should compact the 5 LVAL pages.
        table.DeleteRow("Id", 1);

        // Insert a second row of the same size.  With GC the LVAL pages are fully reused
        // and the file must not grow (beyond one possible new main data page = 4096 bytes).
        table.Insert(new Row { ["Id"] = 2, ["Title"] = "Second", ["Body"] = bigText });
        long sizeAfterSecond = new FileInfo(_path).Length;

        Assert.True(sizeAfterSecond <= sizeAfterFirst + 4096,
            $"File grew by {sizeAfterSecond - sizeAfterFirst} bytes — LVAL space was not reclaimed.");

        // Verify the second row round-trips correctly.
        var rows = table.ReadAllRows();
        Assert.Single(rows);
        Assert.Equal(bigText, rows[0]["Body"]);
    }

    [Fact]
    public void UpdateByPrimaryKey_LvalChains_OldSpaceReclaimed()
    {
        string body1 = new string('A', 8_000);   // ~16 000 bytes → 4 LVAL chunks
        string body2 = new string('B', 8_000);

        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Articles", ArticleColumns(), primaryKey: "Id");

        table.Insert(new Row { ["Id"] = 1, ["Title"] = "T", ["Body"] = body1 });
        long sizeAfterFirst = new FileInfo(_path).Length;

        table.UpdateByPrimaryKey(1, new Row { ["Body"] = body2 });
        long sizeAfterUpdate = new FileInfo(_path).Length;

        // Old LVAL chunks freed → update must not allocate extra LVAL pages.
        Assert.True(sizeAfterUpdate <= sizeAfterFirst + 4096,
            $"File grew by {sizeAfterUpdate - sizeAfterFirst} bytes after update — old LVAL was not freed.");

        // Verify the updated value round-trips correctly.
        var rows = table.ReadAllRows();
        Assert.Single(rows);
        Assert.Equal(body2, rows[0]["Body"]);
    }

    [Fact]
    public void Insert_MultipleRows_WithMemo_RoundTrip()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Articles", ArticleColumns());
            for (int i = 1; i <= 5; i++)
                t.Insert(new Row
                {
                    ["Id"]    = i,
                    ["Title"] = $"Title {i}",
                    ["Body"]  = new string((char)('A' + i - 1), 500 * i),
                });
        }

        using var db2  = Database.Open(_path, JetVersion.Jet4);
        var       rows = db2.GetTable("Articles").ReadAllRows();

        Assert.Equal(5, rows.Count);
        for (int i = 1; i <= 5; i++)
        {
            var row = rows[i - 1];
            Assert.Equal(new string((char)('A' + i - 1), 500 * i), row["Body"]);
        }
    }
}
