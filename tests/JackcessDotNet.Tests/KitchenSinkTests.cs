using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// End-to-end "everything together" tests that exercise multiple features in
/// one go. If these pass, the library's public API is internally consistent
/// across schema authoring, row CRUD, indexed lookup, and reopen.
/// </summary>
public sealed class KitchenSinkTests : IDisposable
{
    private readonly string _path;
    public KitchenSinkTests()
        => _path = Path.Combine(Path.GetTempPath(), $"ks_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void MultiTypeTable_AllValuesSurviveReopen()
    {
        // Single table carrying one column of each supported fixed-length type
        // plus Text + Memo. Insert one fully-populated row, reopen, scan back.
        var columns = new[]
        {
            new ColumnBuilder("Id",       DataType.Long).Build(),
            new ColumnBuilder("Title",    DataType.Text).MaxLength(50).Build(),
            new ColumnBuilder("Age",      DataType.Int).Build(),
            new ColumnBuilder("Active",   DataType.Boolean).Build(),
            new ColumnBuilder("Score",    DataType.Double).Build(),
            new ColumnBuilder("Balance",  DataType.Money).Build(),
            new ColumnBuilder("Created",  DataType.ShortDateTime).Build(),
            new ColumnBuilder("Marker",   DataType.Guid).Build(),
            new ColumnBuilder("Body",     DataType.Memo).Build(),
        };
        var marker = Guid.NewGuid();
        var when   = new DateTime(2024, 1, 2, 3, 4, 5);

        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Things", columns, primaryKey: "Id");
            t.Insert(new Row
            {
                ["Id"]      = 7,
                ["Title"]   = "Lucky",
                ["Age"]     = (short)21,
                ["Active"]  = true,
                ["Score"]   = 99.5,
                ["Balance"] = 1234.5678m,
                ["Created"] = when,
                ["Marker"]  = marker,
                ["Body"]    = "All here.",
            });
        }

        using var reopen = Database.Open(_path);
        var t2 = reopen.GetTable("Things");
        var rows = t2.ReadAllRows();
        Assert.Single(rows);
        var r = rows[0];

        Assert.Equal(7,           r["Id"]);
        Assert.Equal("Lucky",     r["Title"]);
        Assert.Equal((short)21,   r["Age"]);
        Assert.Equal(true,        r["Active"]);
        Assert.Equal(99.5,        (double)r["Score"]!);
        Assert.Equal(1234.5678m,  r["Balance"]);
        Assert.Equal(when,        r["Created"]);
        Assert.Equal(marker,      r["Marker"]);
        Assert.Equal("All here.", r["Body"]);

        // PK metadata round-tripped through TDEF.
        var pk = t2.Indexes.First(i => i.IsPrimaryKey);
        Assert.Equal("Id", pk.Columns[0].Column.Name);
    }

    [Fact]
    public void TwoTables_IndependentLookups_AfterReopen()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var customers = db.CreateTable("Customers", new[]
            {
                new ColumnBuilder("Id",   DataType.Long).Build(),
                new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
            }, primaryKey: "Id");
            for (int i = 1; i <= 5; i++)
                customers.Insert(new Row { ["Id"] = i, ["Name"] = $"Cust{i}" });

            var orders = db.CreateTable("Orders", new[]
            {
                new ColumnBuilder("OrderId",    DataType.Long).Build(),
                new ColumnBuilder("CustomerId", DataType.Long).Build(),
                new ColumnBuilder("Total",      DataType.Money).Build(),
            }, primaryKey: "OrderId");
            for (int i = 100; i <= 110; i++)
                orders.Insert(new Row
                {
                    ["OrderId"]    = i,
                    ["CustomerId"] = (i % 5) + 1,
                    ["Total"]      = i * 10.5m,
                });
        }

        using var reopen = Database.Open(_path);
        Assert.Contains("Customers", reopen.ListTables());
        Assert.Contains("Orders",    reopen.ListTables());

        var custRows  = reopen.GetTable("Customers").ReadAllRows();
        var orderRows = reopen.GetTable("Orders").ReadAllRows();
        Assert.Equal(5,  custRows.Count);
        Assert.Equal(11, orderRows.Count);
    }

    [Fact]
    public void Builder_DrivenCreation_ProducesSameResultAsDirectAPI()
    {
        // Two paths to building the same table — must yield same schema.
        string viaBuilders = Path.ChangeExtension(_path, ".builder.mdb");
        try
        {
            using (var db = new DatabaseBuilder()
                   .WithFile(viaBuilders).WithVersion(JetVersion.Jet4).Create())
            {
                new TableBuilder("Items")
                    .AddColumn("Id",    DataType.Long)
                    .AddColumn("Label", DataType.Text, cb => cb.MaxLength(30))
                    .WithPrimaryKey("Id")
                    .ToTable(db);
            }

            using (var db = Database.Create(_path, JetVersion.Jet4))
            {
                db.CreateTable("Items", new[]
                {
                    new ColumnBuilder("Id",    DataType.Long).Build(),
                    new ColumnBuilder("Label", DataType.Text).MaxLength(30).Build(),
                }, primaryKey: "Id");
            }

            using var a = Database.Open(viaBuilders);
            using var b = Database.Open(_path);
            var ta = a.GetTable("Items");
            var tb = b.GetTable("Items");

            Assert.Equal(ta.Columns.Count, tb.Columns.Count);
            for (int i = 0; i < ta.Columns.Count; i++)
            {
                Assert.Equal(ta.Columns[i].Name,     tb.Columns[i].Name);
                Assert.Equal(ta.Columns[i].DataType, tb.Columns[i].DataType);
                Assert.Equal(ta.Columns[i].Length,   tb.Columns[i].Length);
            }
            Assert.Equal(ta.Indexes.Count, tb.Indexes.Count);
        }
        finally
        {
            if (File.Exists(viaBuilders)) File.Delete(viaBuilders);
        }
    }

    [Fact]
    public void UpdateDelete_BothPaths_LeaveTableConsistent()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Things", new[]
        {
            new ColumnBuilder("Id",    DataType.Long).Build(),
            new ColumnBuilder("Title", DataType.Text).MaxLength(50).Build(),
        }, primaryKey: "Id");

        for (int i = 1; i <= 5; i++)
            t.Insert(new Row { ["Id"] = i, ["Title"] = $"T{i}" });

        t.UpdateByPrimaryKey(3, new Row { ["Title"] = "Updated" });
        t.DeleteRow("Id", 4);

        var rows = t.ReadAllRows().OrderBy(r => Convert.ToInt32(r["Id"])).ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal("Updated", rows.Single(r => Convert.ToInt32(r["Id"]) == 3)["Title"]);
        Assert.DoesNotContain(rows, r => Convert.ToInt32(r["Id"]) == 4);

        // Cursor sees the consistent state.
        var c = t.NewIndexCursor();
        Assert.Equal("Updated", c.FindRowByPrimaryKey(3)!["Title"]);
        Assert.Null(c.FindRowByPrimaryKey(4));
    }

    [Fact]
    public void Database_OpenAfterFullPipeline_HasIndexAndDataConsistent()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Demo", new[]
            {
                new ColumnBuilder("Id",   DataType.Long).Build(),
                new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
            }, primaryKey: "Id");
            for (int i = 1; i <= 100; i++)
                t.Insert(new Row { ["Id"] = i, ["Name"] = $"N{i:000}" });
        }

        using var reopen = Database.Open(_path);
        var t2 = reopen.GetTable("Demo");
        Assert.Equal(100, t2.ReadAllRows().Count);

        var pkIx = t2.Indexes.First(ix => ix.IsPrimaryKey);
        Assert.Equal("Id", pkIx.Columns[0].Column.Name);
        Assert.True(pkIx.RootPageNumber > 0);
    }
}
