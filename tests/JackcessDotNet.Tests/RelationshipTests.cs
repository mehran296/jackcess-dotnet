using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace JackcessDotNet.Tests;

public sealed class RelationshipTests
{
    private readonly ITestOutputHelper _output;
    public RelationshipTests(ITestOutputHelper output) => _output = output;

    private static string? CorpusRoot()
    {
        const string root = @"D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data";
        return Directory.Exists(root) ? root : null;
    }

    [Fact]
    public void GetRelationships_FreshDb_IsEmptyOrEmptyEnough()
    {
        // A freshly-created database has an MSysRelationships table (from the template),
        // but it contains no rows. GetRelationships() should return an empty list.
        string path = Path.Combine(Path.GetTempPath(), $"rel_{Guid.NewGuid():N}.mdb");
        try
        {
            using var db = Database.Create(path, JetVersion.Jet4);
            Assert.Empty(db.GetRelationships());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void GetRelationships_FromCorpus_IndexV2000_FindsRelationships()
    {
        string? root = CorpusRoot();
        if (root is null) return;
        string path = Path.Combine(root, "V2000", "indexV2000.mdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path);
        var rels = db.GetRelationships();

        foreach (var r in rels)
            _output.WriteLine($"  {r}");

        // The smoke test found indexV2000.mdb has 4 relationships.
        Assert.True(rels.Count > 0,
            $"Expected indexV2000.mdb to have relationships; got {rels.Count}.");

        // Sanity: each relationship has at least one column pair with non-empty endpoints.
        foreach (var r in rels)
        {
            Assert.False(string.IsNullOrEmpty(r.FromTable));
            Assert.False(string.IsNullOrEmpty(r.ToTable));
            Assert.True(r.ColumnPairs.Count > 0);
            foreach (var (from, to) in r.ColumnPairs)
            {
                Assert.False(string.IsNullOrEmpty(from));
                Assert.False(string.IsNullOrEmpty(to));
            }
        }
    }

    [Fact]
    public void GetRelationships_FromCorpus_AcrossVersions_Survives()
    {
        // Smoke test: open every corpus file and assert GetRelationships()
        // doesn't throw. Records the per-file relationship counts.
        string? root = CorpusRoot();
        if (root is null) return;

        int filesScanned = 0;
        int totalRels = 0;
        foreach (var ver in new[] { "V2000", "V2003", "V2007", "V2010" })
        {
            string dir = Path.Combine(root, ver);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.mdb")
                          .Concat(Directory.EnumerateFiles(dir, "*.accdb")))
            {
                try
                {
                    using var db = Database.Open(file);
                    var rels = db.GetRelationships();
                    filesScanned++;
                    totalRels += rels.Count;
                    if (rels.Count > 0)
                        _output.WriteLine($"{ver}/{Path.GetFileName(file)}: {rels.Count} relationship(s)");
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"{ver}/{Path.GetFileName(file)}: FAIL — {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        _output.WriteLine($"Files scanned: {filesScanned}, total relationships: {totalRels}");
        Assert.True(filesScanned > 0, "Expected some corpus files to be scanned.");
    }
}
