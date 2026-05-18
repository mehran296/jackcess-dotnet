using System.IO;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace JackcessDotNet.Tests;

/// <summary>
/// Reality-check tests that open .mdb files from the Jackcess Java test corpus
/// (V1997 / V2000 / V2003 — V2007+ .accdb files are skipped: codec not yet implemented).
///
/// Corpus root is resolved in this order:
///   1. env var JACKCESS_CORPUS_PATH
///   2. ../../jackcess-jackcess-5.0.0/src/test/resources/data (relative to repo root)
///   3. D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data
///
/// If no corpus is found, MemberData returns empty and the theory simply runs zero cases.
/// Each file is its own theory invocation so the failure list maps 1:1 to broken files.
/// </summary>
public sealed class CorpusTests
{
    private readonly ITestOutputHelper _output;

    public CorpusTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> CorpusFiles()
    {
        string? root = ResolveCorpusRoot();
        if (root is null) yield break;

        foreach (var version in new[] { "V1997", "V2000", "V2003" })
        {
            string dir = Path.Combine(root, version);
            if (!Directory.Exists(dir)) continue;

            foreach (string file in Directory.EnumerateFiles(dir, "*.mdb"))
                yield return new object[] { version, Path.GetFileName(file), file };
        }

        foreach (var version in new[] { "V2007", "V2010", "V2019" })
        {
            string dir = Path.Combine(root, version);
            if (!Directory.Exists(dir)) continue;

            foreach (string file in Directory.EnumerateFiles(dir, "*.accdb"))
                yield return new object[] { version, Path.GetFileName(file), file };
        }
    }

    private static string? ResolveCorpusRoot()
    {
        string? env = Environment.GetEnvironmentVariable("JACKCESS_CORPUS_PATH");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        // Repo-relative fallback (lets the test work on machines other than the author's).
        string here = AppContext.BaseDirectory;
        for (int up = 0; up < 8; up++)
        {
            string candidate = Path.GetFullPath(Path.Combine(here, "..",
                "jackcess-jackcess-5.0.0", "src", "test", "resources", "data"));
            if (Directory.Exists(candidate)) return candidate;
            here = Path.GetFullPath(Path.Combine(here, ".."));
        }

        const string hardcoded = @"D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data";
        return Directory.Exists(hardcoded) ? hardcoded : null;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Open_ListTables_ReadFirstRows(string version, string filename, string path)
    {
        var report = new StringBuilder();
        report.AppendLine($"=== {version}/{filename} ===");

        Database? db = null;
        try
        {
            db = Database.Open(path);
            report.AppendLine("  open: OK");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  open: FAIL — {ex.GetType().Name}: {ex.Message}");
            _output.WriteLine(report.ToString());
            Assert.Fail(report.ToString());
            return;
        }

        var failures = new List<string>();
        try
        {
            var tableNames = db.ListTables(includeSystem: false);
            report.AppendLine($"  user tables ({tableNames.Count}): {string.Join(", ", tableNames)}");

            var sysTables = db.ListTables(includeSystem: true);
            report.AppendLine($"  total tables (incl. system): {sysTables.Count}");

            foreach (string name in tableNames)
            {
                try
                {
                    var table = db.GetTable(name);
                    var rows  = table.ReadAllRows();
                    int sample = Math.Min(rows.Count, 10);
                    report.AppendLine($"  table '{name}': {table.Columns.Count} cols, {rows.Count} rows read (sample {sample})");
                }
                catch (Exception ex)
                {
                    string line = $"  table '{name}': FAIL — {ex.GetType().Name}: {ex.Message}";
                    report.AppendLine(line);
                    failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            db.Dispose();
        }

        _output.WriteLine(report.ToString());
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} table(s) failed in {version}/{filename}:\n  - "
                        + string.Join("\n  - ", failures));
    }
}
