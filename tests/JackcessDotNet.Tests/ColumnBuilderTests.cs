using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Coverage for every <see cref="ColumnBuilder"/> public method and static factory.
/// Verifies the resulting <see cref="Column"/> carries the expected metadata so
/// downstream serialization sees the right values.
/// </summary>
public sealed class ColumnBuilderTests
{
    [Fact]
    public void Ctor_RequiresName()
    {
        Assert.Throws<ArgumentNullException>(() => new ColumnBuilder(null!, DataType.Long));
    }

    [Fact]
    public void Build_EmptyName_Throws()
    {
        var cb = new ColumnBuilder("", DataType.Long);
        Assert.Throws<ArgumentException>(() => cb.Build());
    }

    [Fact]
    public void Build_NameLongerThan64_Throws()
    {
        var cb = new ColumnBuilder(new string('x', 65), DataType.Long);
        Assert.Throws<ArgumentException>(() => cb.Build());
    }

    [Fact]
    public void Build_NameExactly64Chars_Succeeds()
    {
        var cb = new ColumnBuilder(new string('x', 64), DataType.Long);
        var col = cb.Build();
        Assert.Equal(64, col.Name.Length);
    }

    [Fact]
    public void WithLength_SetsExplicitStorageLength()
    {
        var col = new ColumnBuilder("X", DataType.Binary).WithLength(200).Build();
        Assert.Equal(200, col.Length);
    }

    [Fact]
    public void WithMaxChars_OnText_ConvertsToByteLength()
    {
        var col = new ColumnBuilder("X", DataType.Text).WithMaxChars(50).Build();
        Assert.Equal(100, col.Length); // 50 chars × 2 bytes UTF-16
    }

    [Fact]
    public void WithMaxChars_OnNonText_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => new ColumnBuilder("X", DataType.Long).WithMaxChars(50).Build());
    }

    [Fact]
    public void MaxLength_AliasForWithMaxChars()
    {
        var col = new ColumnBuilder("X", DataType.Text).MaxLength(20).Build();
        Assert.Equal(40, col.Length);
    }

    [Fact]
    public void WithRequired_SetsFlag()
    {
        var col = new ColumnBuilder("X", DataType.Long).WithRequired().Build();
        Assert.True(col.IsRequired);
    }

    [Fact]
    public void WithAutoNumber_RequiresLong()
    {
        Assert.Throws<InvalidOperationException>(
            () => new ColumnBuilder("X", DataType.Text).WithAutoNumber().Build());
    }

    [Fact]
    public void WithAutoNumber_OnLong_SetsFlag()
    {
        var col = new ColumnBuilder("X", DataType.Long).WithAutoNumber().Build();
        Assert.True(col.IsAutoNumber);
    }

    [Fact]
    public void AutoNumber_InstanceAlias()
    {
        var col = new ColumnBuilder("X", DataType.Long).AutoNumber().Build();
        Assert.True(col.IsAutoNumber);
    }

    [Fact]
    public void WithAllowZeroLength_DefaultsTrue_CanBeDisabled()
    {
        var def = new ColumnBuilder("X", DataType.Text).Build();
        Assert.True(def.AllowZeroLength);

        var off = new ColumnBuilder("X", DataType.Text).WithAllowZeroLength(false).Build();
        Assert.False(off.AllowZeroLength);
    }

    [Fact]
    public void WithNumericScale_PropagatesPrecisionAndScale()
    {
        var col = new ColumnBuilder("X", DataType.Numeric)
            .WithNumericScale(precision: 10, scale: 3)
            .Build();
        Assert.Equal((byte)10, col.Precision);
        Assert.Equal((byte)3,  col.Scale);
    }

    [Fact]
    public void Build_LengthExceedingMax_Throws()
    {
        // Text max is 510 bytes (255 chars × 2).
        var cb = new ColumnBuilder("X", DataType.Text).WithLength(1024);
        Assert.Throws<InvalidOperationException>(() => cb.Build());
    }

    [Fact]
    public void Build_FixedLength_ForcesCanonicalSize()
    {
        // Fixed types ignore user-set length and use GetFixedSize().
        var col = new ColumnBuilder("X", DataType.Long).WithLength(99).Build();
        Assert.Equal(4, col.Length);
    }

    [Fact] public void StaticFactory_Text()
    {
        var col = ColumnBuilder.Text("X", 30).Build();
        Assert.Equal(DataType.Text, col.DataType);
        Assert.Equal(60, col.Length);
    }

    [Fact] public void StaticFactory_Long()
    {
        var col = ColumnBuilder.Long("X").Build();
        Assert.Equal(DataType.Long, col.DataType);
        Assert.False(col.IsAutoNumber);
    }

    [Fact] public void StaticFactory_AutoNumber_SetsAutoFlag()
    {
        var col = ColumnBuilder.AutoNumber("X").Build();
        Assert.Equal(DataType.Long, col.DataType);
        Assert.True(col.IsAutoNumber);
    }

    [Fact] public void StaticFactory_Int_Double_DateTime_Boolean_Memo()
    {
        Assert.Equal(DataType.Int,           ColumnBuilder.Int("a").Build().DataType);
        Assert.Equal(DataType.Double,        ColumnBuilder.Double("a").Build().DataType);
        Assert.Equal(DataType.ShortDateTime, ColumnBuilder.DateTime("a").Build().DataType);
        Assert.Equal(DataType.Boolean,       ColumnBuilder.Boolean("a").Build().DataType);
        Assert.Equal(DataType.Memo,          ColumnBuilder.Memo("a").Build().DataType);
    }

    [Fact] public void StaticFactory_Money_Guid()
    {
        Assert.Equal(DataType.Money, ColumnBuilder.Money("a").Build().DataType);
        Assert.Equal(DataType.Guid,  ColumnBuilder.Guid("a").Build().DataType);
    }

    [Fact] public void StaticFactory_Numeric_DefaultsPrecision18Scale0()
    {
        var col = ColumnBuilder.Numeric("a").Build();
        Assert.Equal(DataType.Numeric, col.DataType);
        Assert.Equal((byte)18, col.Precision);
        Assert.Equal((byte)0,  col.Scale);
    }

    [Fact] public void StaticFactory_Numeric_OverridesPrecisionScale()
    {
        var col = ColumnBuilder.Numeric("a", precision: 5, scale: 2).Build();
        Assert.Equal((byte)5, col.Precision);
        Assert.Equal((byte)2, col.Scale);
    }

    [Fact] public void ToColumn_IsBuildAlias()
    {
        var b = new ColumnBuilder("X", DataType.Long);
        var c1 = b.Build();
        var c2 = b.ToColumn();
        // Identity not guaranteed (ColumnBuilder builds a new Column each call),
        // but the schema should match.
        Assert.Equal(c1.Name,     c2.Name);
        Assert.Equal(c1.DataType, c2.DataType);
        Assert.Equal(c1.Length,   c2.Length);
    }

    [Fact] public void Column_ToString_HasNameAndType()
    {
        var col = ColumnBuilder.Text("Title", 50).Build();
        string s = col.ToString();
        Assert.Contains("Title", s);
        Assert.Contains("Text",  s);
    }
}
