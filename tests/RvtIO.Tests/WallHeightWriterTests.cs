using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

public class WallHeightWriterTests
{
    [Theory]
    [InlineData(1.1, 0.7, 15.91, 0, 14.110000000000001)]
    [InlineData(7.7, 1.18, 15.91, 0.03, 7.06)]
    [InlineData(7.7, 1.18, 15.91, -0.76, 6.2700000000000005)]
    [InlineData(7.7, 1.18, 15.91, 1.89, 8.920000000000002)]
    [InlineData(7.7, 0, 15.91, -1.25, 6.96)]
    public void ComputesHeightBetweenOffsetLevelsInRevitOrder(double baseElevation, double baseOffset,
        double topElevation, double topOffset, double height) =>
        Assert.Equal(height, WallHeightWriter.ConstrainedHeight(baseElevation, baseOffset, topElevation, topOffset));

    [Fact]
    public void ClassifiesARectangularOpeningCrossingTheWallBase()
    {
        Assert.Equal(WallHeightWriter.OpeningCut.Interior, WallHeightWriter.CutAt(0, 8, 2, 5));
        Assert.Equal(WallHeightWriter.OpeningCut.ThroughBottom, WallHeightWriter.CutAt(2.25, 10.25, 2, 5));
        Assert.Equal(WallHeightWriter.OpeningCut.ThroughBottom, WallHeightWriter.CutAt(2.0006103515625, 10.0006103515625, 2, 5));
        Assert.Equal(WallHeightWriter.OpeningCut.AtBaseSill, WallHeightWriter.CutAt(2, 10, 2, 5));
        Assert.Equal(WallHeightWriter.OpeningCut.Unverified, WallHeightWriter.CutAt(2, 5, 2, 5));
        Assert.Equal(WallHeightWriter.OpeningCut.Unverified, WallHeightWriter.CutAt(2.00048828125, 10.00048828125, 2, 5));
        Assert.Equal(WallHeightWriter.OpeningCut.Unverified, WallHeightWriter.CutAt(2.25, 4, 2, 5));
        Assert.Equal(WallHeightWriter.OpeningCut.AtBaseHead, WallHeightWriter.CutAt(5, 13, 2, 5));
        Assert.Equal(WallHeightWriter.OpeningCut.Unverified, WallHeightWriter.CutAt(Math.BitDecrement(5), 13, 2, 5));
        Assert.Equal(WallHeightWriter.OpeningCut.Unverified, WallHeightWriter.CutAt(Math.BitIncrement(5), 13, 2, 5));
        Assert.Equal(WallHeightWriter.OpeningCut.BelowWall, WallHeightWriter.CutAt(5.25, 13.25, 2, 5));
    }

    [Fact]
    public void ReplaysCutsAtAnExactBaseContactInEitherDirection()
    {
        var opening = new WallHeightWriter.OpeningInterval(0, 4, 8, 2, 5,
            WallHeightWriter.OpeningCut.Interior, WallHeightWriter.OpeningCut.AtBaseSill);
        Assert.True(WallHeightWriter.CanReplayBaseBoundaryCuts([opening]));
        Assert.True(WallHeightWriter.CanReplayBaseBoundaryCuts(
            [opening with { Cut = WallHeightWriter.OpeningCut.AtBaseHead, Destination = WallHeightWriter.OpeningCut.ThroughBottom }]));
        Assert.True(WallHeightWriter.CanReplayBaseBoundaryCuts(
            [opening with { Cut = WallHeightWriter.OpeningCut.AtBaseSill, Destination = WallHeightWriter.OpeningCut.BelowWall }]));
        Assert.False(WallHeightWriter.CanReplayBaseBoundaryCuts(
            [opening with { Cut = WallHeightWriter.OpeningCut.ThroughTop }]));
    }

    [Fact]
    public void RemovesAndReactivatesACutWhenTheOpeningFallsBelowTheWall()
    {
        var removal = new WallHeightWriter.OpeningInterval(0, 4, 8, 2, 5,
            WallHeightWriter.OpeningCut.Interior, WallHeightWriter.OpeningCut.BelowWall);
        Assert.True(WallHeightWriter.CanRemoveOpeningCuts([removal]));
        Assert.True(WallHeightWriter.CanRemoveOpeningCuts([removal with { Cut = WallHeightWriter.OpeningCut.ThroughBottom }]));
        Assert.True(WallHeightWriter.CanActivateOpeningCuts(
            [removal with { Cut = WallHeightWriter.OpeningCut.BelowWall, Destination = WallHeightWriter.OpeningCut.Interior }]));
        var notch = removal with { Cut = WallHeightWriter.OpeningCut.BelowWall, Destination = WallHeightWriter.OpeningCut.ThroughBottom };
        Assert.True(WallHeightWriter.CanActivateOpeningCuts([notch]));
        Assert.False(WallHeightWriter.CanActivateOpeningCuts([notch, notch with { Left = 12, Right = 16 }]));
        Assert.False(WallHeightWriter.CanRemoveOpeningCuts([removal with { Cut = WallHeightWriter.OpeningCut.AboveWall }]));
    }

    [Theory]
    [InlineData(0, 0.5, 0x00000002u)]
    [InlineData(0, 3.999999998, 0x00000002u)]
    [InlineData(0, 3.999999999, 0x01000002u)]
    [InlineData(0, 4, 0x01000002u)]
    [InlineData(0, 8, 0x01000002u)]
    [InlineData(-2, -1.5, 0x04000002u)]
    [InlineData(5.875, 6.125, 0x08000002u)]
    [InlineData(-6.125, -5.875, 0x0c000002u)]
    [InlineData(13.125, 17.125, 0x20000002u)]
    [InlineData(122, 123.999999998, 0xf0000002u)]
    [InlineData(122, 124, 0x41000002u)]
    [InlineData(0, 255.999999999, 0x03000002u)]
    [InlineData(0, 256, null)]
    [InlineData(-24576, -24575.75, null)]
    public void EncodesTheHeightIntervalOfTheGeometry(double minimum, double maximum, uint? expected)
    {
        Assert.Equal(expected, WallHeightWriter.EncodeGeometryControl((0, 20), (-0.25, 0.25), (minimum, maximum)));
    }

    [Fact]
    public void KeepsEachSolidInItsOwnHorizontalCells()
    {
        Assert.Equal(0u, WallHeightWriter.EncodeGeometryControl((0, 1), (0, 1), (0, 0.5)));
        Assert.Equal(0x01008002u, WallHeightWriter.EncodeGeometryControl((0, 20), (-0.25, -0.25), (0, 8)));
        Assert.Equal(0x014d027cu, WallHeightWriter.EncodeGeometryControl(
            (-40000, -39500), (309.6719160104905, 310.32808398950215), (0, 8)));
    }

    [Theory]
    [InlineData("PlanTopologies", 0, false, true)]
    [InlineData("PlanTopologies", 1, false, false)]
    [InlineData("PlanTopologies", 0, true, false)]
    [InlineData("PlanTopologies", null, false, false)]
    [InlineData("UnverifiedPlanTopologies", 0, false, false)]
    [InlineData(null, null, false, false)]
    public void RequiresACompleteEmptyLevelSet(string? setClass, int? levelCount, bool trailingByte, bool empty)
    {
        var schema = new SchemaDocument
        {
            Classes =
            [
                new(1, "AreaPlanTopologiesInterface", 0, 1, [new("m_pSet", 14, 1, 0, null, null, null, null)], []),
                new(2, setClass ?? "PlanTopologies", 0, 1,
                    levelCount.HasValue ? [new("m_set", 4, 80, 0, null, null, null, null)] : [], []),
            ],
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)1);
        writer.Write(setClass is null ? 0u : uint.MaxValue);
        if (setClass is not null)
            writer.Write((ushort)2);
        if (levelCount is int count)
        {
            writer.Write(count);
            for (int i = 0; i < count; i++)
                writer.Write(i + 10);
        }
        if (trailingByte)
            writer.Write((byte)0);
        byte[] body = stream.ToArray();
        var walker = new WideBlockWalker(schema, (declaring, field) =>
            WideBlockWalker.AllItemsKeyPrefix + declaring.Name + "." + field.Name, []);

        Assert.Equal(empty, WallHeightWriter.HasEmptyPlanTopologies(walker.Run(body, 0, body.Length, 1), walker));
    }
}
