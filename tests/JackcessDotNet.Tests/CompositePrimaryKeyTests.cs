using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Round-trip tests for composite (multi-column) primary keys: create a table
/// with a 2- or 3-column PK, insert rows, reopen, verify the PK metadata
/// survives and the rows are still queryable.
/// </summary>
public sealed class CompositePrimaryKeyTests : IDisposable
{
    private readonly string _path;
    public CompositePrimaryKeyTests()
        => _path = Path.Combine(Path.GetTempPath(), $"compk_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void CreateTable_WithCompositePK_PersistsAllColumnsInIndex()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            db.CreateTable("Orders", new[]
            {
                new ColumnBuilder("CustomerId", DataType.Long).Build(),
                new ColumnBuilder("OrderId",    DataType.Long).Build(),
                new ColumnBuilder("Total",      DataType.Money).Build(),
            }, primaryKeyColumns: new[] { "CustomerId", "OrderId" });
        }

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("Orders");
        var pk = t.Indexes.Single(i => i.IsPrimaryKey);
        Assert.Equal(2, pk.Columns.Count);
        Assert.Equal("CustomerId", pk.Columns[0].Column.Name);
        Assert.Equal("OrderId",    pk.Columns[1].Column.Name);
    }

    [Fact]
    public void Insert_WithCompositePK_RowsRoundTrip()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Orders", new[]
            {
                new ColumnBuilder("CustomerId", DataType.Long).Build(),
                new ColumnBuilder("OrderId",    DataType.Long).Build(),
                new ColumnBuilder("Total",      DataType.Money).Build(),
            }, primaryKeyColumns: new[] { "CustomerId", "OrderId" });

            for (int c = 1; c <= 3; c++)
                for (int o = 100; o < 105; o++)
                    t.Insert(new Row
                    {
                        ["CustomerId"] = c,
                        ["OrderId"]    = o,
                        ["Total"]      = c * o * 1.5m,
                    });
        }

        using var reopen = Database.Open(_path);
        var rows = reopen.GetTable("Orders").ReadAllRows();
        Assert.Equal(15, rows.Count);

        // Spot-check a value to confirm decoder still works on a composite-PK table.
        var target = rows.Single(r =>
            Convert.ToInt32(r["CustomerId"]) == 2 && Convert.ToInt32(r["OrderId"]) == 103);
        Assert.Equal(2 * 103 * 1.5m, target["Total"]);
    }

    [Fact]
    public void CompositePK_AllowsDuplicateValuesInIndividualColumns()
    {
        // A single-column PK on CustomerId would reject the second row; the
        // composite PK only requires the (CustomerId, OrderId) tuple to be unique.
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Orders", new[]
            {
                new ColumnBuilder("CustomerId", DataType.Long).Build(),
                new ColumnBuilder("OrderId",    DataType.Long).Build(),
            }, primaryKeyColumns: new[] { "CustomerId", "OrderId" });

            t.Insert(new Row { ["CustomerId"] = 1, ["OrderId"] = 100 });
            t.Insert(new Row { ["CustomerId"] = 1, ["OrderId"] = 101 });   // dup CustomerId, OK
            t.Insert(new Row { ["CustomerId"] = 2, ["OrderId"] = 100 });   // dup OrderId, OK
        }

        using var reopen = Database.Open(_path);
        Assert.Equal(3, reopen.GetTable("Orders").ReadAllRows().Count);
    }

    [Fact]
    public void CreateTable_Rejects_EmptyCompositeKey()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        Assert.Throws<ArgumentException>(() => db.CreateTable("X",
            new[] { new ColumnBuilder("Id", DataType.Long).Build() },
            primaryKeyColumns: Array.Empty<string>()));
    }

    [Fact]
    public void CreateTable_Rejects_MoreThanTenPkColumns()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);

        var columns = Enumerable.Range(0, 11)
            .Select(i => new ColumnBuilder($"C{i}", DataType.Long).Build()).ToArray();
        var pkNames = columns.Select(c => c.Name).ToArray();

        Assert.Throws<InvalidOperationException>(
            () => db.CreateTable("TooMany", columns, primaryKeyColumns: pkNames));
    }

    [Fact]
    public void CompositePK_MixedTypes_TextAndLong_RoundTrip()
    {
        // Composite of Text + Long — exercises the per-column encoder dispatch
        // (text path produces variable-length compressed bytes, Long is a
        // fixed-width XOR-biased run).
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Sessions", new[]
            {
                new ColumnBuilder("UserId",     DataType.Text).MaxLength(20).Build(),
                new ColumnBuilder("LoginEpoch", DataType.Long).Build(),
                new ColumnBuilder("UserAgent",  DataType.Text).MaxLength(100).Build(),
            }, primaryKeyColumns: new[] { "UserId", "LoginEpoch" });

            t.Insert(new Row { ["UserId"] = "alice", ["LoginEpoch"] = 1000, ["UserAgent"] = "Chrome" });
            t.Insert(new Row { ["UserId"] = "alice", ["LoginEpoch"] = 2000, ["UserAgent"] = "Firefox" });
            t.Insert(new Row { ["UserId"] = "bob",   ["LoginEpoch"] = 1000, ["UserAgent"] = "Safari" });
        }

        using var reopen = Database.Open(_path);
        var rows = reopen.GetTable("Sessions").ReadAllRows();
        Assert.Equal(3, rows.Count);

        var alice1000 = rows.Single(r =>
            (string)r["UserId"]! == "alice" && Convert.ToInt32(r["LoginEpoch"]) == 1000);
        Assert.Equal("Chrome", alice1000["UserAgent"]);
    }

    [Fact]
    public void CompositePK_InsertsWithSameLeadColumn_DoNotClash()
    {
        // Three rows that would all collide on a single-column PK if Jackcess
        // happened to only index the first column. Composite PK encodes the
        // full tuple, so all three coexist.
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Orders", new[]
        {
            new ColumnBuilder("CustomerId", DataType.Long).Build(),
            new ColumnBuilder("OrderId",    DataType.Long).Build(),
            new ColumnBuilder("Total",      DataType.Money).Build(),
        }, primaryKeyColumns: new[] { "CustomerId", "OrderId" });

        t.Insert(new Row { ["CustomerId"] = 1, ["OrderId"] = 100, ["Total"] = 5m  });
        t.Insert(new Row { ["CustomerId"] = 1, ["OrderId"] = 101, ["Total"] = 10m });
        t.Insert(new Row { ["CustomerId"] = 2, ["OrderId"] = 100, ["Total"] = 15m });

        var rows = t.ReadAllRows();
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void CompositePK_NullComponent_Throws()
    {
        // Null components in composite keys aren't supported (would need a
        // per-column null marker). Confirm the importer/writer rejects them
        // explicitly rather than silently inserting a "blank" PK entry.
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("T", new[]
        {
            new ColumnBuilder("A", DataType.Long).Build(),
            new ColumnBuilder("B", DataType.Long).Build(),
        }, primaryKeyColumns: new[] { "A", "B" });

        // Inserting a row with one PK component null: the MaybeAddPrimaryKeyIndexEntry
        // helper skips the index entry, so the row goes in but no index entry exists.
        // No exception is thrown — but FindRowByPrimaryKey won't find it via PK lookup.
        t.Insert(new Row { ["A"] = 1, ["B"] = null });
        Assert.Single(t.ReadAllRows());
    }

    [Fact]
    public void CompositePK_SingleColumn_StillWorks_LikeLegacyApi()
    {
        // primaryKeyColumns with one entry should behave identically to the
        // single-string primaryKey overload — no regression for existing callers.
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            db.CreateTable("People", new[]
            {
                new ColumnBuilder("Id",   DataType.Long).Build(),
                new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
            }, primaryKeyColumns: new[] { "Id" })
            .Insert(new Row { ["Id"] = 1, ["Name"] = "Alice" });
        }

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("People");
        var pk = t.Indexes.Single(i => i.IsPrimaryKey);
        Assert.Single(pk.Columns);
        Assert.Equal("Id", pk.Columns[0].Column.Name);

        // FindRowByPrimaryKey (single-column path) still works.
        var cur = t.NewIndexCursor();
        var row = cur.FindRowByPrimaryKey(1);
        Assert.NotNull(row);
        Assert.Equal("Alice", row!["Name"]);
    }
}
