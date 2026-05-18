using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// API-shape tests for <see cref="PropertyMap"/> and <see cref="PropertyMaps"/>:
/// indexer access, enumeration, IsEmpty semantics. The end-to-end decode is
/// covered by <see cref="PropertyMapTests"/>; this file pokes the public surface
/// directly against synthetic data.
/// </summary>
public sealed class PropertyMapAccessTests
{
    [Fact]
    public void EmptyMaps_IsEmpty_True_AndDefaultIsNotNull()
    {
        var maps = new PropertyMaps();
        Assert.True(maps.IsEmpty);
        Assert.Equal(0, maps.Count);
        Assert.NotNull(maps.Default);
        Assert.True(maps.Default.IsEmpty);
    }

    [Fact]
    public void Get_MissingMap_ReturnsNull()
    {
        var maps = new PropertyMaps();
        Assert.Null(maps.Get("nope"));
    }

    [Fact]
    public void Enumeration_VisitsAllAddedMaps()
    {
        // Build through the binary path so the data model matches what we'd see
        // from a real Access file (no public ctor on PropertyMap).
        byte[] blob = HandCraftedTwoMapBlob();
        var maps = PropertyMapReader.Read(blob, JetFormat.Jet4);

        var visited = maps.Select(m => m.Name).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "", "ColX" }, visited);
    }

    [Fact]
    public void Indexer_MissingProperty_ReturnsNull()
    {
        byte[] blob = HandCraftedTwoMapBlob();
        var maps = PropertyMapReader.Read(blob, JetFormat.Jet4);
        Assert.Null(maps.Default["nope"]);
    }

    [Fact]
    public void Indexer_CaseInsensitivePropertyAccess()
    {
        byte[] blob = HandCraftedTwoMapBlob();
        var maps = PropertyMapReader.Read(blob, JetFormat.Jet4);
        var colMap = maps.Get("ColX");
        Assert.NotNull(colMap);
        // Same value via differently-cased key.
        Assert.Equal("hi", colMap!["caption"]);
        Assert.Equal("hi", colMap!["CAPTION"]);
    }

    [Fact]
    public void Property_RecordEquality_BasedOnFieldValues()
    {
        var p1 = new PropertyMap.Property("X", DataType.Text, "abc", false);
        var p2 = new PropertyMap.Property("X", DataType.Text, "abc", false);
        var p3 = new PropertyMap.Property("X", DataType.Text, "abd", false);
        Assert.Equal(p1, p2);
        Assert.NotEqual(p1, p3);
    }

    [Fact]
    public void Property_ToString_HasNameTypeValue()
    {
        var p = new PropertyMap.Property("Description", DataType.Text, "Hello", false);
        string s = p.ToString();
        Assert.Contains("Description", s);
        Assert.Contains("Text",        s);
        Assert.Contains("Hello",       s);
    }

    // ── Helper: synthesise a property-map blob with two maps ─────────────────

    private static byte[] HandCraftedTwoMapBlob()
    {
        // Property names list with one entry: "Caption" (UTF-16LE, 14 bytes)
        var bytes = new List<byte>();
        bytes.AddRange(new byte[] { (byte)'M', (byte)'R', (byte)'2', 0x00 });

        var nameBlock = new List<byte>();
        byte[] capName = System.Text.Encoding.Unicode.GetBytes("Caption");
        nameBlock.Add((byte)capName.Length); nameBlock.Add(0x00);
        nameBlock.AddRange(capName);
        bytes.AddRange(BitConverter.GetBytes(4 + 2 + nameBlock.Count));
        bytes.AddRange(BitConverter.GetBytes((short)0x0080));
        bytes.AddRange(nameBlock);

        // Map 1: default (no name), Caption = "hi"
        AddValueBlock(bytes, mapName: "",     blockType: 0x0000, propName: "hi");
        // Map 2: named "ColX", Caption = "hi"
        AddValueBlock(bytes, mapName: "ColX", blockType: 0x0001, propName: "hi");
        return bytes.ToArray();
    }

    private static void AddValueBlock(List<byte> bytes, string mapName, short blockType, string propName)
    {
        var block = new List<byte>();

        // Map name sub-block.
        byte[] mapNameBytes = mapName.Length == 0
            ? Array.Empty<byte>()
            : System.Text.Encoding.Unicode.GetBytes(mapName);
        if (mapName.Length == 0)
        {
            // sub-len = 4 (header only — no name)
            block.AddRange(BitConverter.GetBytes(4));
        }
        else
        {
            // 4-byte sub-len + 2-byte name-len + name bytes
            int subLen = 4 + 2 + mapNameBytes.Length;
            block.AddRange(BitConverter.GetBytes(subLen));
            block.AddRange(BitConverter.GetBytes((short)mapNameBytes.Length));
            block.AddRange(mapNameBytes);
        }

        // Single property entry: Caption (idx 0) = propName (Text).
        byte[] valBytes = System.Text.Encoding.Unicode.GetBytes(propName);
        int entryLen   = 2 + 1 + 1 + 2 + 2 + valBytes.Length;
        block.AddRange(BitConverter.GetBytes((short)entryLen));
        block.Add(0x00);           // isDdl
        block.Add(0x0A);           // Text
        block.AddRange(BitConverter.GetBytes((short)0));            // nameIdx → 0 = "Caption"
        block.AddRange(BitConverter.GetBytes((short)valBytes.Length));
        block.AddRange(valBytes);

        bytes.AddRange(BitConverter.GetBytes(4 + 2 + block.Count));
        bytes.AddRange(BitConverter.GetBytes(blockType));
        bytes.AddRange(block);
    }
}
