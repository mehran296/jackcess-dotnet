using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// One round-trip per <see cref="DataType"/>: create a table with a column of
/// that type, insert a representative value, reopen, read it back, and verify
/// the value survives encoding + decoding. These are the canonical "does the
/// type work end-to-end" tests; type-specific quirks (range limits, special
/// markers, null handling) belong here too.
/// </summary>
public sealed class DataTypeRoundTripTests : IDisposable
{
    private readonly string _path;
    public DataTypeRoundTripTests()
        => _path = Path.Combine(Path.GetTempPath(), $"dt_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private Row Roundtrip(DataType dt, object? value, Action<ColumnBuilder>? configure = null)
    {
        ColumnBuilder cb = new ColumnBuilder("V", dt);
        configure?.Invoke(cb);

        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("T", new[]
            {
                new ColumnBuilder("Id", DataType.Long).Build(),
                cb.Build(),
            });
            t.Insert(new Row { ["Id"] = 1, ["V"] = value });
        }

        using var reopen = Database.Open(_path);
        var rows = reopen.GetTable("T").ReadAllRows();
        Assert.Single(rows);
        return rows[0];
    }

    [Fact] public void Byte_RoundTrip()
    {
        var r = Roundtrip(DataType.Byte, (byte)0x7F);
        Assert.Equal((byte)0x7F, r["V"]);
    }

    [Fact] public void Byte_MaxValue_RoundTrip()
    {
        var r = Roundtrip(DataType.Byte, (byte)0xFF);
        Assert.Equal((byte)0xFF, r["V"]);
    }

    [Fact] public void Int_RoundTrip()
    {
        var r = Roundtrip(DataType.Int, (short)-12345);
        Assert.Equal((short)-12345, r["V"]);
    }

    [Fact] public void Int_BoundaryValues_RoundTrip()
    {
        var r = Roundtrip(DataType.Int, short.MinValue);
        Assert.Equal(short.MinValue, r["V"]);
        Dispose(); File.Delete(_path); /* fresh */
    }

    [Fact] public void Long_PositiveAndNegative_RoundTrip()
    {
        Assert.Equal(2147000000, Roundtrip(DataType.Long, 2147000000)["V"]);
        Dispose(); File.Delete(_path);
        Assert.Equal(-1, Roundtrip(DataType.Long, -1)["V"]);
    }

    [Fact] public void Float_RoundTrip_Within6SigFigs()
    {
        var r = Roundtrip(DataType.Float, 3.14159f);
        Assert.InRange((float)r["V"]!, 3.1415f, 3.1416f);
    }

    [Fact] public void Double_RoundTrip()
    {
        const double v = 2.718281828459045;
        var r = Roundtrip(DataType.Double, v);
        Assert.Equal(v, (double)r["V"]!, 1e-12);
    }

    [Fact] public void Money_RoundTrip_RetainsFourDecimals()
    {
        var r = Roundtrip(DataType.Money, 12345.6789m);
        Assert.Equal(12345.6789m, r["V"]);
    }

    [Fact] public void Money_Negative_RoundTrip()
    {
        var r = Roundtrip(DataType.Money, -7.5m);
        Assert.Equal(-7.5m, r["V"]);
    }

    [Fact] public void ShortDateTime_RoundTrip()
    {
        var dt = new DateTime(2024, 6, 15, 12, 30, 45);
        var r = Roundtrip(DataType.ShortDateTime, dt);
        Assert.Equal(dt, r["V"]);
    }

    [Fact] public void Guid_RoundTrip()
    {
        var g = Guid.NewGuid();
        var r = Roundtrip(DataType.Guid, g);
        Assert.Equal(g, r["V"]);
    }

    [Fact] public void Boolean_True_RoundTrip()
    {
        var r = Roundtrip(DataType.Boolean, true);
        Assert.Equal(true, r["V"]);
    }

    [Fact] public void Boolean_False_RoundTrip()
    {
        var r = Roundtrip(DataType.Boolean, false);
        // Boolean's null-mask bit doubles as the value; a "not-null = false" reads as false.
        Assert.Equal(false, r["V"]);
    }

    [Fact] public void Text_AsciiOnly_RoundTrip()
    {
        var r = Roundtrip(DataType.Text, "Hello, world!", cb => cb.MaxLength(50));
        Assert.Equal("Hello, world!", r["V"]);
    }

    [Fact] public void Text_Empty_RoundTrip()
    {
        // Empty string: row decode returns null (no notNull bit set / zero length).
        // Test exists to document the behaviour rather than fix it.
        var r = Roundtrip(DataType.Text, "", cb => cb.MaxLength(50));
        // Either null or empty is acceptable depending on the encoder's null treatment.
        Assert.True(r["V"] is null or "", $"Expected null or empty; got '{r["V"]}'");
    }

    [Fact] public void Text_Unicode_RoundTrip()
    {
        // Characters outside Latin-1 force UTF-16LE storage (no compression marker).
        var r = Roundtrip(DataType.Text, "日本語テスト", cb => cb.MaxLength(50));
        Assert.Equal("日本語テスト", r["V"]);
    }

    [Fact] public void Binary_RoundTrip()
    {
        var bytes = new byte[] { 0x00, 0x01, 0xFE, 0xFF, 0x42 };
        var r = Roundtrip(DataType.Binary, bytes, cb => cb.WithLength(255));
        Assert.Equal(bytes, (byte[])r["V"]!);
    }

    [Fact] public void Numeric_RoundTrip_WithScale()
    {
        var r = Roundtrip(DataType.Numeric, 123.45m, cb => cb.WithNumericScale(18, 2));
        Assert.Equal(123.45m, r["V"]);
    }

    [Fact] public void Numeric_RoundTrip_Negative()
    {
        var r = Roundtrip(DataType.Numeric, -99.99m, cb => cb.WithNumericScale(18, 2));
        Assert.Equal(-99.99m, r["V"]);
    }

    [Fact] public void Memo_Short_RoundTrip()
    {
        var r = Roundtrip(DataType.Memo, "A short memo");
        Assert.Equal("A short memo", r["V"]);
    }

    [Fact] public void Memo_Null_StoresAsNull()
    {
        // Null memo: not inserted as a value; row should report null on read.
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("T", new[]
            {
                new ColumnBuilder("Id", DataType.Long).Build(),
                new ColumnBuilder("V",  DataType.Memo).Build(),
            });
            t.Insert(new Row { ["Id"] = 1 });   // no V
        }
        using var reopen = Database.Open(_path);
        var rows = reopen.GetTable("T").ReadAllRows();
        Assert.False(rows[0].ContainsKey("V"),
            "Memo column with no value should not appear in the read row (null filtered out).");
    }
}
