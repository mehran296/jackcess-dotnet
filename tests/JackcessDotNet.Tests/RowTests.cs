using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Coverage for the <see cref="Row"/> dictionary wrapper. Case-insensitive key
/// access is the contract relied on across the rest of the library.
/// </summary>
public sealed class RowTests
{
    [Fact]
    public void Indexer_KeyAccess_IsCaseInsensitive()
    {
        var r = new Row { ["Name"] = "Alice" };
        Assert.Equal("Alice", r["name"]);
        Assert.Equal("Alice", r["NAME"]);
        Assert.Equal("Alice", r["Name"]);
    }

    [Fact]
    public void Indexer_Set_CaseInsensitive_OverwritesPrevious()
    {
        var r = new Row { ["Name"] = "Alice" };
        r["NAME"] = "Bob";
        Assert.Single(r);
        Assert.Equal("Bob", r["name"]);
    }

    [Fact]
    public void Indexer_GetMissingKey_Throws()
    {
        var r = new Row();
        Assert.Throws<KeyNotFoundException>(() => r["missing"]);
    }

    [Fact]
    public void TryGetValue_ReturnsFalseForMissingKey()
    {
        var r = new Row { ["Id"] = 1 };
        Assert.False(r.TryGetValue("nope", out _));
        Assert.True (r.TryGetValue("Id",   out var v));
        Assert.Equal(1, v);
    }

    [Fact]
    public void Set_FluentChain_Works()
    {
        var r = new Row()
            .Set("Id",   1)
            .Set("Name", "Alice")
            .Set("Age",  (short)30);

        Assert.Equal(3, r.Count);
        Assert.Equal(1,        r["Id"]);
        Assert.Equal("Alice",  r["Name"]);
        Assert.Equal((short)30, r["Age"]);
    }

    [Fact]
    public void FromValues_PositionalAssignment()
    {
        var columns = new[]
        {
            new ColumnBuilder("Id",   DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(20).Build(),
        };

        var r = Row.FromValues(columns, 42, "Bob");

        Assert.Equal(42,    r["Id"]);
        Assert.Equal("Bob", r["Name"]);
    }

    [Fact]
    public void FromValues_TooManyValues_Throws()
    {
        var columns = new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
        };
        Assert.Throws<ArgumentException>(() => Row.FromValues(columns, 1, 2));
    }

    [Fact]
    public void FromValues_FewerValuesThanColumns_IsAllowed()
    {
        var columns = new[]
        {
            new ColumnBuilder("Id",   DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(20).Build(),
            new ColumnBuilder("Age",  DataType.Int ).Build(),
        };
        var r = Row.FromValues(columns, 1, "Alice");
        Assert.Equal(2, r.Count);
        Assert.False(r.ContainsKey("Age"));
    }

    [Fact]
    public void IEnumerableCtor_AcceptsKvpSequence()
    {
        var src = new[]
        {
            new KeyValuePair<string, object?>("Id",   1),
            new KeyValuePair<string, object?>("Name", "Alice"),
        };
        var r = new Row(src);
        Assert.Equal(2, r.Count);
        Assert.Equal("Alice", r["name"]);
    }

    [Fact]
    public void Row_AllowsNullValues()
    {
        var r = new Row { ["X"] = null };
        Assert.True(r.ContainsKey("X"));
        Assert.Null(r["X"]);
    }
}
