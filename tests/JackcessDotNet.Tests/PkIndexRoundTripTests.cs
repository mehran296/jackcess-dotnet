using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Exercises the PK-index population path: every Insert appends a leaf entry,
/// every UpdateByPrimaryKey appends a new entry pointing at the rewritten row,
/// and IndexCursor.FindRow consumes the index but filters stale entries.
///
/// These tests verify behaviour through the public API (FindRow returns the
/// correct, current row), since the single-leaf B-tree is intentionally
/// append-only and stale-pointer-tolerant.
/// </summary>
public sealed class PkIndexRoundTripTests : IDisposable
{
    private readonly string _path;

    public PkIndexRoundTripTests()
        => _path = Path.Combine(Path.GetTempPath(), $"pk_index_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static IReadOnlyList<Column> ArticlesCols() => new[]
    {
        new ColumnBuilder("Id",    DataType.Long).Build(),
        new ColumnBuilder("Title", DataType.Text).MaxLength(80).Build(),
        new ColumnBuilder("Body",  DataType.Memo).Build(),
    };

    [Fact]
    public void FindByPk_ReturnsRowInsertedViaInsert()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
        for (int i = 1; i <= 10; i++)
            t.Insert(new Row { ["Id"] = i, ["Title"] = $"T{i}", ["Body"] = $"body {i}" });

        var c = t.NewIndexCursor();

        for (int i = 1; i <= 10; i++)
        {
            var r = c.FindRowByPrimaryKey(i);
            Assert.NotNull(r);
            Assert.Equal($"T{i}", r!["Title"]);
        }
    }

    [Fact]
    public void FindByPk_MissingKey_ReturnsNull()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
        t.Insert(new Row { ["Id"] = 1, ["Title"] = "A" });
        t.Insert(new Row { ["Id"] = 3, ["Title"] = "C" });

        var c = t.NewIndexCursor();

        Assert.Null(c.FindRowByPrimaryKey(2));   // never inserted
        Assert.Null(c.FindRowByPrimaryKey(99));  // way out of range
    }

    [Fact]
    public void FindByPk_AfterUpdate_ReturnsUpdatedRow()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
        t.Insert(new Row { ["Id"] = 1, ["Title"] = "Original", ["Body"] = "v1" });

        t.UpdateByPrimaryKey(1, new Row { ["Title"] = "Revised", ["Body"] = "v2" });

        var c = t.NewIndexCursor();
        var r = c.FindRowByPrimaryKey(1);

        Assert.NotNull(r);
        Assert.Equal("Revised", r!["Title"]);
        Assert.Equal("v2",      r["Body"]);
    }

    [Fact]
    public void FindByPk_AfterDelete_ReturnsNull()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
        t.Insert(new Row { ["Id"] = 1, ["Title"] = "Keep" });
        t.Insert(new Row { ["Id"] = 2, ["Title"] = "Delete" });

        t.DeleteRow("Id", 2);

        var c = t.NewIndexCursor();
        Assert.Null   (c.FindRowByPrimaryKey(2));
        Assert.NotNull(c.FindRowByPrimaryKey(1));
    }

    [Fact]
    public void FindByPk_RepeatedUpdate_StaleEntriesAreFilteredOut()
    {
        // After N updates of the same row, the PK index leaf holds N+1 entries
        // all keyed to the same PK (1 original + N from updates). Lookup must
        // still return the single live row — verifies the verify-the-hit filter.
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
        t.Insert(new Row { ["Id"] = 1, ["Title"] = "v0" });

        for (int i = 1; i <= 5; i++)
            t.UpdateByPrimaryKey(1, new Row { ["Title"] = $"v{i}" });

        var c = t.NewIndexCursor();
        var r = c.FindRowByPrimaryKey(1);

        Assert.NotNull(r);
        Assert.Equal("v5", r!["Title"]);
    }

    [Fact]
    public void FindByPk_AfterDeleteThenReinsertWithSameKey_ReturnsNewRow()
    {
        // Stale entry for the deleted row sits in the leaf. A subsequent insert
        // with the same PK appends a fresh entry. Lookup must skip the stale one
        // and return the new row.
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
        t.Insert(new Row { ["Id"] = 42, ["Title"] = "First" });
        t.DeleteRow("Id", 42);
        t.Insert(new Row { ["Id"] = 42, ["Title"] = "Second" });

        var c = t.NewIndexCursor();
        var r = c.FindRowByPrimaryKey(42);

        Assert.NotNull(r);
        Assert.Equal("Second", r!["Title"]);
    }

    [Fact]
    public void FindByPk_TableWithoutPrimaryKey_FindRowByPrimaryKeyThrows()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Articles", ArticlesCols());   // no primaryKey
        t.Insert(new Row { ["Id"] = 1, ["Title"] = "A" });

        var c = t.NewIndexCursor();

        Assert.Throws<InvalidOperationException>(() => c.FindRowByPrimaryKey(1));
    }

    [Fact]
    public void FindByPk_NonPrimaryKeyColumn_FallsBackToScan()
    {
        // Calling FindRow against a non-PK column should still work via scan,
        // even when the PK index is present.
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
        t.Insert(new Row { ["Id"] = 1, ["Title"] = "First"  });
        t.Insert(new Row { ["Id"] = 2, ["Title"] = "Second" });

        var c = t.NewIndexCursor();
        var r = c.FindRow("Title", "Second");

        Assert.NotNull(r);
        Assert.Equal(2, Convert.ToInt32(r!["Id"]));
    }

    [Fact]
    public void FindByPk_ManyRows_ReturnsCorrectRowForEach()
    {
        // 200 rows of small payloads to fit comfortably in the single-leaf
        // (entry size 8 bytes × 200 = 1600 bytes, well under 4076 of leaf space).
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
        for (int i = 1; i <= 200; i++)
            t.Insert(new Row { ["Id"] = i, ["Title"] = $"T{i}" });

        var c = t.NewIndexCursor();
        foreach (int probe in new[] { 1, 17, 99, 100, 150, 200 })
        {
            var r = c.FindRowByPrimaryKey(probe);
            Assert.NotNull(r);
            Assert.Equal($"T{probe}", r!["Title"]);
        }
    }

    [Fact]
    public void FindByPk_AfterReopen_ViaAccessFormatIndexReader()
    {
        // After Step D, IndexWriter writes Access-format leaves (entry mask at byte 27+,
        // entries at byte 480+). Verify by reopening and looking up keys via
        // IndexReader — the disk-format walker, not our IndexWriter scan.
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
            for (int i = 1; i <= 10; i++)
                t.Insert(new Row { ["Id"] = i, ["Title"] = $"T{i}" });
        }

        using var reopen = Database.Open(_path);
        var table = reopen.GetTable("Articles");
        var pkIx  = table.Indexes.First(ix => ix.IsPrimaryKey);

        var reader = new IndexReader(
            (PageFile)typeof(Database).GetField("_file",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(reopen)!,
            pkIx);
        if (!reader.CanResolveKey)
            throw new InvalidOperationException(
                $"CanResolveKey=false; pkIx={pkIx.Columns.Count} col(s); " +
                $"col[0]={pkIx.Columns[0].Column.Name}({pkIx.Columns[0].Column.DataType}) " +
                $"flags=0x{pkIx.Columns[0].Flags:X2} asc={pkIx.Columns[0].IsAscending}");

        // Dump leaf bytes (header + first 64 entry bytes) for diagnostics
        var pageFile = (PageFile)typeof(Database).GetField("_file",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(reopen)!;
        byte[] leafBytes = pageFile.ReadPage(pkIx.RootPageNumber);
        var hdr = new System.Text.StringBuilder();
        for (int b = 0; b < 32; b++) hdr.Append(leafBytes[b].ToString("X2")).Append(' ');
        var mask = new System.Text.StringBuilder();
        for (int b = 0; b < 16; b++) mask.Append(leafBytes[27 + b].ToString("X2")).Append(' ');
        var entries = new System.Text.StringBuilder();
        for (int b = 0; b < 32; b++) entries.Append(leafBytes[480 + b].ToString("X2")).Append(' ');

        int hits = 0;
        foreach (int probe in new[] { 1, 5, 10 })
        {
            foreach (var rowPtr in reader.FindRowPointers(probe))
            {
                hits++;
                int pageNum = (rowPtr >> 16) & 0xFFFFFF;
                int rowIdx  = rowPtr        & 0xFF;
                Assert.True(pageNum > 0);
                Assert.True(rowIdx >= 0 && rowIdx < 256);
            }
        }
        if (hits == 0)
            throw new InvalidOperationException(
                $"IndexReader found 0 entries on rootPage={pkIx.RootPageNumber}\n" +
                $"  header[0..32]: {hdr}\n" +
                $"  mask[27..43]:  {mask}\n" +
                $"  entries[0..32]:{entries}");
    }

    [Fact]
    public void FindByPk_AfterReopen_StillResolvesPrimaryKey()
    {
        // PK metadata (column name + leaf root page) is now persisted into the TDEF
        // via a real index column block + slot + name. After Dispose + reopen,
        // Database.GetTable should reattach PrimaryKeyColumnName/IndexPage from the
        // on-disk metadata so FindRowByPrimaryKey works without rebuilding state.
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
            for (int i = 1; i <= 10; i++)
                t.Insert(new Row { ["Id"] = i, ["Title"] = $"T{i}" });
        }

        using var reopen = Database.Open(_path);
        var table = reopen.GetTable("Articles");

        // Index metadata survived the round-trip.
        var pkIx = table.Indexes.FirstOrDefault(ix => ix.IsPrimaryKey);
        Assert.True(pkIx is not null,
            $"Expected a persisted PK index after reopen; got {table.Indexes.Count} index(es): " +
            string.Join(", ", table.Indexes.Select(i => $"{i.Name}({i.Columns.Count} cols, type {i.IndexType})")));
        Assert.Equal("PrimaryKey", pkIx!.Name);
        Assert.Single(pkIx.Columns);
        Assert.Equal("Id", pkIx.Columns[0].Column.Name);

        // Lookups via the persisted index work end-to-end.
        var c = table.NewIndexCursor();
        foreach (int probe in new[] { 1, 4, 7, 10 })
        {
            var r = c.FindRowByPrimaryKey(probe);
            Assert.NotNull(r);
            Assert.Equal($"T{probe}", r!["Title"]);
        }
    }

    [Fact]
    public void FindByPk_AfterLeafSplit_RootBecomesNode_AndIndexReaderWalksIt()
    {
        // Roughly 800 rows of (Id, Title="Tn") at ~9 bytes/key + 4-byte trailer = 13 bytes/entry.
        // The leaf can hold ~280 such entries before splitting, so this exercises both
        // (a) the leaf-split path and (b) the leaf-to-node-root promotion.
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
            for (int i = 1; i <= 800; i++)
                t.Insert(new Row { ["Id"] = i, ["Title"] = $"T{i}" });
        }

        // Reopen + verify via IndexReader (which descends node → leaf, not via our
        // own chain walker). If the root didn't get promoted to a node, IndexReader
        // would only see the original leaf's entries — the test would fail for
        // any Id beyond the first leaf.
        using var reopen = Database.Open(_path);
        var table = reopen.GetTable("Articles");
        var pkIx  = table.Indexes.First(ix => ix.IsPrimaryKey);

        var pageFile = (PageFile)typeof(Database).GetField("_file",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(reopen)!;
        byte[] rootPage = pageFile.ReadPage(pkIx.RootPageNumber);
        Assert.True(rootPage[0] == 0x03,
            $"Expected root to be a node (0x03) after splits; got 0x{rootPage[0]:X2}");

        var reader = new IndexReader(pageFile, pkIx);
        foreach (int probe in new[] { 1, 50, 200, 500, 750, 800 })
        {
            var ptrs = reader.FindRowPointers(probe).ToList();
            Assert.True(ptrs.Count >= 1, $"IndexReader missed Id={probe}");
        }
    }

    [Fact]
    public void FindByPk_LotsOfRows_LeavesChainCorrectly()
    {
        // 2000 PK rows with 8-byte entries = 16000 bytes of index entries.
        // A single leaf only holds ~4076/8 ≈ 509 entries, so this exercises
        // the linked-leaf path (3+ leaves chained via next-pointer).
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("Articles", ArticlesCols(), primaryKey: "Id");
        for (int i = 1; i <= 2000; i++)
            t.Insert(new Row { ["Id"] = i, ["Title"] = $"T{i}" });

        var c = t.NewIndexCursor();
        // Probe values that fall into different leaves of the chain.
        foreach (int probe in new[] { 1, 100, 500, 600, 1000, 1500, 1999, 2000 })
        {
            var r = c.FindRowByPrimaryKey(probe);
            Assert.NotNull(r);
            Assert.Equal($"T{probe}", r!["Title"]);
        }
    }
}
