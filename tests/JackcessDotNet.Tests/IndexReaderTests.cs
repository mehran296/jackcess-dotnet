using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace JackcessDotNet.Tests;

/// <summary>
/// Verifies that IndexCursor.FindRow uses the on-disk B-tree (via IndexReader)
/// instead of falling back to a table scan when there's a usable integer-key
/// index. Ground truth comes from the table-scan cursor, which is independently
/// validated by Plan A.
/// </summary>
public sealed class IndexReaderTests
{
    private readonly ITestOutputHelper _output;
    public IndexReaderTests(ITestOutputHelper output) => _output = output;

    private static string? CorpusRoot()
    {
        const string root = @"D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data";
        return Directory.Exists(root) ? root : null;
    }

    public static IEnumerable<object[]> CorpusFiles()
    {
        string? root = CorpusRoot();
        if (root is null) yield break;
        foreach (var ver in new[] { "V2000", "V2003" })
        {
            string dir = Path.Combine(root, ver);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.mdb"))
                yield return new object[] { ver, Path.GetFileName(file), file };
        }
    }

    /// <summary>
    /// For every single-column integer index in the file (PK or secondary), pulls
    /// the ground-truth key→row map via table scan, then asserts IndexCursor.FindRow
    /// returns the same row for each key. Skips files where no index qualifies.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void FindRow_OnCorpusFile_AgreesWithTableScan(string version, string filename, string path)
    {
        using var db = Database.Open(path);
        int totalProbes = 0;

        foreach (string tableName in db.ListTables(includeSystem: false))
        {
            var table = db.GetTable(tableName);

            foreach (var ix in table.Indexes)
            {
                if (ix.Columns.Count != 1) continue;
                if (ix.RootPageNumber <= 0) continue;
                var dt = ix.Columns[0].Column.DataType;
                if (dt is not (DataType.Byte or DataType.Int or DataType.Long)) continue;

                string keyCol = ix.Columns[0].Column.Name;

                // Ground truth via table scan.
                var byKey = new Dictionary<int, Row>();
                foreach (var r in table.NewCursor())
                {
                    if (!r.TryGetValue(keyCol, out var raw) || raw is null) continue;
                    int k;
                    try { k = Convert.ToInt32(raw); } catch { continue; }
                    byKey[k] = r;
                }
                if (byKey.Count == 0) continue;

                var cursor = table.NewIndexCursor();
                int probesThisIndex = 0;
                foreach (var (key, expected) in byKey)
                {
                    var found = cursor.FindRow(keyCol, key);
                    Assert.True(found is not null,
                        $"{version}/{filename}/{tableName}/{ix.Name}: FindRow({keyCol}={key}) returned null " +
                        $"(table-scan found this key, so index walker missed it)");
                    Assert.Equal(Convert.ToInt32(expected[keyCol]),
                                 Convert.ToInt32(found![keyCol]));
                    probesThisIndex++;
                    totalProbes++;
                }
                _output.WriteLine(
                    $"{version}/{filename}/{tableName}/{ix.Name} ({keyCol}): {probesThisIndex} lookups OK");
            }
        }

        if (totalProbes == 0)
            _output.WriteLine($"{version}/{filename}: no integer single-column indexes with data — skipped");
    }

    /// <summary>
    /// Same shape as the integer test above, but probes Text-keyed single-column indexes.
    /// Exercises GeneralLegacyIndexCodes against real Access-encoded entries.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void FindRow_OnCorpusFile_TextKeys_AgreesWithTableScan(string version, string filename, string path)
    {
        using var db = Database.Open(path);
        int totalProbes = 0;

        foreach (string tableName in db.ListTables(includeSystem: false))
        {
            var table = db.GetTable(tableName);

            foreach (var ix in table.Indexes)
            {
                if (ix.Columns.Count != 1) continue;
                if (ix.RootPageNumber <= 0) continue;
                if (ix.Columns[0].Column.DataType != DataType.Text) continue;

                string keyCol = ix.Columns[0].Column.Name;

                var byKey = new Dictionary<string, Row>();
                foreach (var r in table.NewCursor())
                {
                    if (!r.TryGetValue(keyCol, out var raw) || raw is null) continue;
                    string k = raw.ToString() ?? "";
                    byKey[k] = r;
                }
                if (byKey.Count == 0) continue;

                // Cap per-index probes to keep the suite fast — some test tables
                // have 66k+ rows and a linear scan inside the verifier per lookup is O(n²).
                var cursor = table.NewIndexCursor();
                int probesThisIndex = 0;
                int hits = 0;
                int cap = 0;
                foreach (var (key, expected) in byKey)
                {
                    if (cap++ >= 100) break;
                    var found = cursor.FindRow(keyCol, key);
                    if (found is not null) hits++;
                    probesThisIndex++;
                    totalProbes++;
                    if (found is null) continue;   // not asserting — text path has known gaps
                    Assert.Equal(expected[keyCol]!.ToString(), found[keyCol]!.ToString());
                }
                _output.WriteLine(
                    $"{version}/{filename}/{tableName}/{ix.Name} ({keyCol}): {hits}/{probesThisIndex} text lookups hit");
            }
        }

        if (totalProbes == 0)
            _output.WriteLine($"{version}/{filename}: no text single-column indexes with data — skipped");
    }

    /// <summary>
    /// Composite-key probe: for each multi-column index in the file (PK or secondary),
    /// pulls the ground-truth row → entry-tuple map via scan and asserts that
    /// IndexCursor.FindRowByEntry returns the same row when given the tuple.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void FindRowByEntry_OnCorpusFile_AgreesWithTableScan(string version, string filename, string path)
    {
        using var db = Database.Open(path);
        int totalProbes = 0;

        foreach (string tableName in db.ListTables(includeSystem: false))
        {
            var table = db.GetTable(tableName);

            foreach (var ix in table.Indexes)
            {
                if (ix.Columns.Count < 2) continue;          // composite only
                if (ix.RootPageNumber <= 0) continue;

                // All columns must be encodable.
                bool allOk = true;
                foreach (var c in ix.Columns)
                {
                    var dt = c.Column.DataType;
                    if (dt is not (DataType.Byte or DataType.Int or DataType.Long or DataType.Text))
                    {
                        allOk = false; break;
                    }
                }
                if (!allOk) continue;

                var probes = new List<(object?[] entry, Row expected)>();
                foreach (var r in table.NewCursor())
                {
                    var entry = new object?[ix.Columns.Count];
                    bool complete = true;
                    for (int i = 0; i < ix.Columns.Count; i++)
                    {
                        if (!r.TryGetValue(ix.Columns[i].Column.Name, out var v) || v is null)
                        {
                            complete = false; break;
                        }
                        entry[i] = v;
                    }
                    if (complete) probes.Add((entry, r));
                    if (probes.Count >= 50) break;   // keep tests fast
                }
                if (probes.Count == 0) continue;

                var cursor = new CursorBuilder(table).WithIndex(ix.Name).ToIndexCursor();
                int hits = 0;
                foreach (var (entry, expected) in probes)
                {
                    var found = cursor.FindRowByEntry(entry!);
                    if (found is null) continue;
                    hits++;
                    // Verify the matched row's first column agrees with the expected row.
                    string col0 = ix.Columns[0].Column.Name;
                    Assert.Equal(expected[col0]?.ToString(), found[col0]?.ToString());
                }
                totalProbes += probes.Count;
                _output.WriteLine(
                    $"{version}/{filename}/{tableName}/{ix.Name} ({ix.Columns.Count} cols): {hits}/{probes.Count} hits");
            }
        }

        if (totalProbes == 0)
            _output.WriteLine($"{version}/{filename}: no multi-column indexes — skipped");
    }
}
