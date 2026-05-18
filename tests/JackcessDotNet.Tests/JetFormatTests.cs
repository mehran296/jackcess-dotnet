using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Locks down the per-version <see cref="JetFormat"/> constants. The numbers
/// here come from Jackcess Java's JetFormat — every offset, size, and
/// version-specific value asserted explicitly so a regression shows up loud.
/// </summary>
public sealed class JetFormatTests
{
    [Fact]
    public void Jet3_CoreSizes()
    {
        var f = JetFormat.Jet3;
        Assert.Equal(JetVersion.Jet3, f.Version);
        Assert.Equal(2048, f.PageSize);
        Assert.False(f.HasPageChecksum);
        Assert.Equal(2012, f.MaxRowSize);
        Assert.Equal(1, f.SizeRowColumnCount);
        Assert.Equal(1, f.SizeRowVarColOffset);
        Assert.Equal(43, f.SizeTdefHeader);
        Assert.Equal(8,  f.SizeIndexDefinition);
        Assert.Equal(39, f.SizeIndexColumnBlock);
        Assert.Equal(20, f.SizeIndexInfoBlock);
        Assert.Equal(18, f.SizeColumnHeader);
        Assert.Equal(1,  f.SizeNameLength);
    }

    [Fact]
    public void Jet3_TdefOffsets()
    {
        var f = JetFormat.Jet3;
        Assert.Equal(12, f.TdefOffsetNumRows);
        Assert.Equal(23, f.TdefOffsetNumVarCols);
        Assert.Equal(25, f.TdefOffsetNumCols);
        Assert.Equal(27, f.TdefOffsetNumIndexSlots);
        Assert.Equal(31, f.TdefOffsetNumIndexes);
        Assert.Equal(35, f.TdefOffsetOwnedRow);
        Assert.Equal(36, f.TdefOffsetOwnedPage);
        Assert.Equal(39, f.TdefOffsetFreeRow);
        Assert.Equal(40, f.TdefOffsetFreePage);
    }

    [Fact]
    public void Jet3_DataPageHeader()
    {
        var f = JetFormat.Jet3;
        Assert.Equal(8,  f.OffsetDataNumRows);
        Assert.Equal(10, f.OffsetDataRowTable);
    }

    [Fact]
    public void Jet3_ColumnHeaderOffsets()
    {
        var f = JetFormat.Jet3;
        Assert.Equal(1,  f.OffsetColumnNumber);
        Assert.Equal(11, f.OffsetColumnPrecision);
        Assert.Equal(12, f.OffsetColumnScale);
        Assert.Equal(13, f.OffsetColumnFlags);
        Assert.Equal(16, f.OffsetColumnLength);
        Assert.Equal(3,  f.OffsetColumnVarTableIndex);
        Assert.Equal(14, f.OffsetColumnFixedDataOffset);
    }

    [Fact]
    public void Jet4_CoreSizes()
    {
        var f = JetFormat.Jet4;
        Assert.Equal(JetVersion.Jet4, f.Version);
        Assert.Equal(4096, f.PageSize);
        Assert.True(f.HasPageChecksum);
        Assert.Equal(4060, f.MaxRowSize);
        Assert.Equal(2, f.SizeRowColumnCount);
        Assert.Equal(2, f.SizeRowVarColOffset);
        Assert.Equal(63, f.SizeTdefHeader);
        Assert.Equal(12, f.SizeIndexDefinition);
        Assert.Equal(52, f.SizeIndexColumnBlock);
        Assert.Equal(28, f.SizeIndexInfoBlock);
        Assert.Equal(25, f.SizeColumnHeader);
        Assert.Equal(2,  f.SizeNameLength);
    }

    [Fact]
    public void Jet4_TdefOffsets()
    {
        var f = JetFormat.Jet4;
        Assert.Equal(16, f.TdefOffsetNumRows);
        Assert.Equal(43, f.TdefOffsetNumVarCols);
        Assert.Equal(45, f.TdefOffsetNumCols);
        Assert.Equal(47, f.TdefOffsetNumIndexSlots);
        Assert.Equal(51, f.TdefOffsetNumIndexes);
        Assert.Equal(55, f.TdefOffsetOwnedRow);
        Assert.Equal(56, f.TdefOffsetOwnedPage);
        Assert.Equal(59, f.TdefOffsetFreeRow);
        Assert.Equal(60, f.TdefOffsetFreePage);
    }

    [Fact]
    public void Jet4_IndexPageOffsets()
    {
        var f = JetFormat.Jet4;
        Assert.Equal(12, f.OffsetPrevIndexPage);
        Assert.Equal(16, f.OffsetNextIndexPage);
        Assert.Equal(20, f.OffsetChildTailIndexPage);
        Assert.Equal(24, f.OffsetIndexCompressedByteCount);
        Assert.Equal(27, f.OffsetIndexEntryMask);
        Assert.Equal(453, f.SizeIndexEntryMask);
    }

    [Theory]
    [InlineData(JetVersion.Jet12)]
    [InlineData(JetVersion.Jet14)]
    [InlineData(JetVersion.Jet16)]
    [InlineData(JetVersion.Jet17)]
    public void Jet12Through17_ShareJet4Layout(JetVersion ver)
    {
        JetFormat f = ver switch
        {
            JetVersion.Jet12 => JetFormat.Jet12,
            JetVersion.Jet14 => JetFormat.Jet14,
            JetVersion.Jet16 => JetFormat.Jet16,
            JetVersion.Jet17 => JetFormat.Jet17,
            _ => throw new ArgumentOutOfRangeException(nameof(ver)),
        };
        Assert.Equal(ver, f.Version);
        // Layout identical to Jet4 for the unencrypted .accdb path we currently support.
        Assert.Equal(JetFormat.Jet4.PageSize,         f.PageSize);
        Assert.Equal(JetFormat.Jet4.SizeTdefHeader,   f.SizeTdefHeader);
        Assert.Equal(JetFormat.Jet4.SizeColumnHeader, f.SizeColumnHeader);
        Assert.Equal(JetFormat.Jet4.OffsetIndexEntryMask, f.OffsetIndexEntryMask);
    }

    [Fact]
    public void StaticConstants_AreFormatIndependent()
    {
        // These don't vary across versions.
        Assert.Equal(2,    JetFormat.OffsetDataFreeSpace);
        Assert.Equal(4,    JetFormat.OffsetDataTdefPage);
        Assert.Equal(2,    JetFormat.SizeRowEntry);
        Assert.Equal(0x1FFF, JetFormat.RowOffsetMask);
        Assert.Equal(0x01, JetFormat.PageTypeData);
        Assert.Equal(0x02, JetFormat.PageTypeTableDef);
        Assert.Equal(0x03, JetFormat.PageTypeIndexNode);
        Assert.Equal(0x04, JetFormat.PageTypeIndexLeaf);
        Assert.Equal(0x05, JetFormat.PageTypeUsageMap);
        Assert.Equal(2,    JetFormat.PageSystemCatalog);
    }
}
