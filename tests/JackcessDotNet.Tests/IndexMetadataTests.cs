using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace JackcessDotNet.Tests;

/// <summary>
/// Verifies that Table.Indexes is populated from real Access-authored TDEFs.
/// Multi-page TDEFs are silently empty (see TdefReader fallback) — these
/// tests target single-page TDEFs in the V2000/V2003 corpus where parsing succeeds.
/// </summary>
public sealed class IndexMetadataTests
{
    private readonly ITestOutputHelper _output;
    public IndexMetadataTests(ITestOutputHelper output) => _output = output;

    private static string? CorpusRoot()
    {
        const string root = @"D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data";
        return Directory.Exists(root) ? root : null;
    }

    [Fact]
    public void Indexes_ParsedForCommon1V2000_AtLeastOneTableHasPrimaryKey()
    {
        string? root = CorpusRoot();
        if (root is null) return;
        string path = Path.Combine(root, "V2000", "common1V2000.mdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path);

        bool sawPk = false;
        foreach (var name in db.ListTables(includeSystem: false))
        {
            var t = db.GetTable(name);
            _output.WriteLine($"{name}: {t.Indexes.Count} index(es)");
            foreach (var ix in t.Indexes)
            {
                _output.WriteLine($"  - {ix}");
                if (ix.IsPrimaryKey) sawPk = true;
            }
        }

        Assert.True(sawPk, "Expected at least one user table in common1V2000.mdb to expose a primary-key index.");
    }

    [Fact]
    public void Indexes_ParsedForCommon1V2003_AtLeastOneTableHasPrimaryKey()
    {
        string? root = CorpusRoot();
        if (root is null) return;
        string path = Path.Combine(root, "V2003", "common1V2003.mdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path);

        bool sawPk = false;
        foreach (var name in db.ListTables(includeSystem: false))
        {
            var t = db.GetTable(name);
            _output.WriteLine($"{name}: {t.Indexes.Count} index(es)");
            foreach (var ix in t.Indexes)
            {
                _output.WriteLine($"  - {ix}");
                if (ix.IsPrimaryKey) sawPk = true;
            }
        }

        Assert.True(sawPk, "Expected at least one user table in common1V2003.mdb to expose a primary-key index.");
    }

    [Fact]
    public void Indexes_RootPagesArePositive()
    {
        string? root = CorpusRoot();
        if (root is null) return;
        string path = Path.Combine(root, "V2000", "indexV2000.mdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path);
        foreach (var name in db.ListTables(includeSystem: false))
        {
            var t = db.GetTable(name);
            foreach (var ix in t.Indexes)
            {
                Assert.True(ix.RootPageNumber > 0,
                    $"Index {t.Name}.{ix.Name} has non-positive root page {ix.RootPageNumber}");
                Assert.True(ix.Columns.Count > 0,
                    $"Index {t.Name}.{ix.Name} reports zero columns");
            }
        }
    }

    [Fact]
    public void Indexes_FreshlyCreatedTable_ExposesPersistedPrimaryKey()
    {
        // Step 1 of the post-B-tree roadmap: PK info is now persisted in the TDEF.
        // A freshly-authored table with primaryKey: "Id" must round-trip through
        // Database.Open and expose a PK index in its Indexes list.
        string path = Path.Combine(Path.GetTempPath(), $"idx_meta_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(path, JetVersion.Jet4))
            {
                db.CreateTable("Foo", new[]
                {
                    new ColumnBuilder("Id",   DataType.Long).Build(),
                    new ColumnBuilder("Name", DataType.Text).MaxLength(20).Build(),
                }, primaryKey: "Id");
            }

            using var reopen = Database.Open(path);
            var t = reopen.GetTable("Foo");

            Assert.Single(t.Indexes);
            var pk = t.Indexes[0];
            Assert.Equal("PrimaryKey", pk.Name);
            Assert.True(pk.IsPrimaryKey);
            Assert.Single(pk.Columns);
            Assert.Equal("Id", pk.Columns[0].Column.Name);
            Assert.True(pk.RootPageNumber > 0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Indexes_FreshlyCreatedTable_WithoutPrimaryKey_HasEmptyIndexList()
    {
        // When no primaryKey is specified to CreateTable, no index block is written.
        string path = Path.Combine(Path.GetTempPath(), $"idx_meta_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(path, JetVersion.Jet4))
            {
                db.CreateTable("Foo", new[]
                {
                    new ColumnBuilder("Id",   DataType.Long).Build(),
                    new ColumnBuilder("Name", DataType.Text).MaxLength(20).Build(),
                });
            }

            using var reopen = Database.Open(path);
            var t = reopen.GetTable("Foo");
            Assert.Empty(t.Indexes);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
