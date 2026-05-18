using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace JackcessDotNet.Tests;

public sealed class PropertyMapTests
{
    private readonly ITestOutputHelper _output;
    public PropertyMapTests(ITestOutputHelper output) => _output = output;

    private static string? CorpusRoot()
    {
        const string root = @"D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data";
        return Directory.Exists(root) ? root : null;
    }

    [Fact]
    public void PropertyMapReader_DecodesHandCraftedBlob()
    {
        // Minimal property blob: signature "MR2\0", one-entry name list ("Desc"),
        // one default-map value list with Desc = "Hi" (Text).
        var bytes = new List<byte>();
        bytes.AddRange(new byte[] { (byte)'M', (byte)'R', (byte)'2', 0x00 });

        var nameBlock = new List<byte>();
        nameBlock.Add(0x08); nameBlock.Add(0x00);
        nameBlock.AddRange(System.Text.Encoding.Unicode.GetBytes("Desc"));
        bytes.AddRange(BitConverter.GetBytes(4 + 2 + nameBlock.Count));
        bytes.AddRange(BitConverter.GetBytes((short)0x0080));
        bytes.AddRange(nameBlock);

        var valueBlock = new List<byte>();
        valueBlock.AddRange(BitConverter.GetBytes(4));
        byte[] hi = System.Text.Encoding.Unicode.GetBytes("Hi");
        valueBlock.AddRange(BitConverter.GetBytes((short)(2 + 1 + 1 + 2 + 2 + hi.Length)));
        valueBlock.Add(0x00);
        valueBlock.Add(0x0A);   // Text
        valueBlock.AddRange(BitConverter.GetBytes((short)0));
        valueBlock.AddRange(BitConverter.GetBytes((short)hi.Length));
        valueBlock.AddRange(hi);
        bytes.AddRange(BitConverter.GetBytes(4 + 2 + valueBlock.Count));
        bytes.AddRange(BitConverter.GetBytes((short)0x0000));
        bytes.AddRange(valueBlock);

        var maps = PropertyMapReader.Read(bytes.ToArray(), JetFormat.Jet4);
        Assert.False(maps.IsEmpty);
        Assert.Equal("Hi", maps.Default["Desc"]);
    }

    [Fact]
    public void Properties_RoundTripFromCorpus_DecodeTableAndColumnProperties()
    {
        // IndexPropertiesV2003.mdb's TableIgnoreNulls1 has property maps for the
        // table itself and for the "row" and "data" columns.
        string? root = CorpusRoot();
        if (root is null) return;
        string path = Path.Combine(root, "V2003", "IndexPropertiesV2003.mdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path);
        var t = db.GetTable("TableIgnoreNulls1");
        var props = t.Properties;

        Assert.False(props.IsEmpty);

        var tableProps = props.Default;
        Assert.NotNull(tableProps);
        Assert.True(tableProps.Properties.Count > 0, "table-level map should expose properties");

        // Check at least one well-known per-column property is decoded.
        var rowMap = props.Get("row");
        Assert.NotNull(rowMap);
        Assert.NotNull(rowMap!["AllowZeroLength"]);
    }

    [Fact]
    public void Properties_FreshTable_AreEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"prop_{Guid.NewGuid():N}.mdb");
        try
        {
            using var db = Database.Create(path, JetVersion.Jet4);
            var t = db.CreateTable("Foo", new[] { new ColumnBuilder("X", DataType.Long).Build() });
            Assert.True(t.Properties.IsEmpty);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Properties_DumpForObservability()
    {
        // Loose observability check — print what we find across many corpus tables.
        string? root = CorpusRoot();
        if (root is null) return;
        string path = Path.Combine(root, "V2003", "IndexPropertiesV2003.mdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path);
        foreach (var name in db.ListTables(includeSystem: false))
        {
            var t = db.GetTable(name);
            if (t.Properties.IsEmpty) continue;
            _output.WriteLine($"=== {name} ===");
            foreach (var map in t.Properties)
            {
                _output.WriteLine($"  [{(string.IsNullOrEmpty(map.Name) ? "<table>" : map.Name)}] " +
                                  $"{map.Properties.Count} prop(s)");
                foreach (var p in map.Properties)
                    _output.WriteLine($"    {p.Name} ({p.DataType}) = {(p.Value is byte[] b ? $"byte[{b.Length}]" : p.Value)}");
            }
        }
    }
}
