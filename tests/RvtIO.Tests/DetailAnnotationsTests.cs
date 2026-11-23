using System.Buffers.Binary;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public sealed class DetailAnnotationsTests
{
    [Theory]
    [InlineData(BuiltInCategory.OST_Floors, 0, 2)]
    [InlineData(BuiltInCategory.OST_Floors, 10, 0)]
    [InlineData(BuiltInCategory.OST_Ceilings, 0, 2)]
    [InlineData(BuiltInCategory.OST_Ceilings, 10, 0)]
    public void ClosedHostCutHatches_UseStoredHorizontalLayersInARectangularCut(BuiltInCategory category,
        double skew, int expectedCount)
    {
        var host = new Element { Id = 1, TypeId = 2, CategoryId = (int)category };
        var type = new Element
        {
            Id = 2,
            SerializedHostTypeLayers =
            [
                new(BitConverter.DoubleToInt64Bits(0.25), 1, -1, 10, -1, 0, 1),
                new(BitConverter.DoubleToInt64Bits(0.75), 1, -1, 20, -1, 1, 1),
            ],
        };
        var doc = RvtDocument.FromInfo(new RevitFileInfo { Elements = { host, type } });
        var surface = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.01, [0.004, 0.002], 0);
        var core = surface with { Angle = Math.PI / 4 };
        doc.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)>
            { [10] = (100, -1, null), [20] = (200, -1, null) };
        doc.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
            { [100] = ("Surface", [surface]), [200] = ("Core", [core]) };
        var fills = new List<DetailAnnotations.LayerHatch>();

        var boundaries = new List<DetailAnnotations.LayerBoundary>();

        DetailAnnotations.AddClosedHostCutHatches(fills, doc, host,
            [(0, 0), (1000, 0), (1000, 304.8 + skew), (0, 304.8)],
            [1, 0, 0], [0, 0, 1], [5, 0, 2], boundaries);

        Assert.Equal(expectedCount, fills.Count);
        if (expectedCount == 0)
        {
            Assert.Empty(boundaries);
            return;
        }
        var boundary = Assert.Single(boundaries);
        Assert.Equal((int)category, boundary.CategoryId);
        Assert.Equal(0, boundary.Start.X, 6);
        Assert.Equal(1000, boundary.End.X, 6);
        Assert.Equal(228.6, boundary.Start.Y, 6);
        Assert.Equal(228.6, boundary.End.Y, 6);
        Assert.Same(surface, Assert.Single(fills[0].Grids));
        Assert.Equal(228.6, fills[0].Polygon.Min(p => p.Y), 6);
        Assert.Same(core, Assert.Single(fills[1].Grids));
        Assert.Equal(228.6, fills[1].Polygon.Max(p => p.Y), 6);
        Assert.All(fills, fill => Assert.Equal((1524.0, 609.6), (fill.AnchorX, fill.AnchorY)));
    }

    [Fact]
    public void WallMaterialBandsUseTheClosedCutAtAnAngledJunction()
    {
        var wall = new Element
        {
            Id = 1, TypeId = 2, CategoryId = (int)BuiltInCategory.OST_Walls, SerializedWallFlipped = true,
            Location = new LocationCurve { StartPoint = [0, 0, 0], EndPoint = [1, 0, 0], Direction = [1, 0, 0] },
        };
        var type = new Element
        {
            Id = 2,
            SerializedHostTypeLayers =
            [
                new(BitConverter.DoubleToInt64Bits(0.25), 1, -1, 10, -1, 0, 1),
                new(BitConverter.DoubleToInt64Bits(0.75), 1, -1, 20, -1, 1, 1),
            ],
        };
        var doc = RvtDocument.FromInfo(new RevitFileInfo { Elements = { wall, type } });
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.01, [], 0);
        doc.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [10] = (100, -1, null) };
        doc.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
            { [100] = ("Finish", [grid]) };
        var fills = new List<DetailAnnotations.LayerHatch>();
        var boundaries = new List<DetailAnnotations.LayerBoundary>();

        DetailAnnotations.AddLayerHatches(fills, doc, wall, [(0, 0, 0, 600), (304.8, 0, 304.8, 800)],
            [0, 1, 0], [0, 0, 1], [0, 0, 0], boundaries, [[(0, 0), (304.8, 0), (304.8, 800), (0, 600)]]);

        var boundary = Assert.Single(boundaries);
        Assert.Equal(76.2, boundary.Start.X, 6);
        Assert.Equal(76.2, boundary.End.X, 6);
        Assert.Equal(0, Math.Min(boundary.Start.Y, boundary.End.Y), 6);
        Assert.Equal(650, Math.Max(boundary.Start.Y, boundary.End.Y), 6);
        Assert.Equal(650, Assert.Single(fills).Polygon.Max(p => p.Y), 6);
    }

    [Fact]
    public void ContinuousDraftingGrid_InksOnlyTheUnionOfClippedSpans()
    {
        List<(double X, double Y)> first = [(0, 0.5), (4, 0.5), (4, 1.5), (0, 1.5)];
        List<(double X, double Y)> second = [(2, 0.5), (6, 0.5), (6, 1.5), (2, 1.5)];
        TfBlockDeserializer.PatternGrid[] grids = [new(0, 0, 0, 0, 1 / 304.8, [], 0)];

        Assert.Empty(FillHatch.Strokes([first, second], grids, 0, 0));
        var stroke = Assert.Single(FillHatch.Strokes([first, second], grids, 0, 0, continuousLines: true));
        Assert.Equal((0d, 1d, 6d, 1d), stroke);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BattInsulation_UsesStoredWidthAndStaysWithinAuthoredEnds(bool mirrored)
    {
        var strokes = DetailAnnotations.BattInsulation(10, 20, 45, 20, 12, mirrored).ToArray();
        var points = strokes.SelectMany(s => new[] { (s.X1, s.Y1), (s.X2, s.Y2) }).ToArray();
        Assert.Equal(10, points.Min(p => p.Item1), 6);
        Assert.Equal(45, points.Max(p => p.Item1), 6);
        Assert.Equal(14, points.Min(p => p.Item2), 6);
        Assert.Equal(26, points.Max(p => p.Item2), 6);
        Assert.Contains(points, p => p.Item1 == 12 && p.Item2 == (mirrored ? 26 : 14));
        Assert.Empty(DetailAnnotations.BattInsulation(0, 0, 0, 0, 12, mirrored));
    }

    [Theory]
    [InlineData(-0.5, -0.1)]
    [InlineData(0.7, 0.1)]
    public void BreakReferencePlanes_UseTheStretchedOutlineAndMaskSide(double maskY, double tipY)
    {
        const int stride = 105;
        var data = new byte[20 * stride];
        for (int plane = 0; plane < 20; plane++) data[plane * stride + 32] = 1;
        Set(0, -.85, 0); Set(2, .64, 0);
        Set(14, -.2, 0); Set(15, .2, 0);
        Set(19, -.1, 0); Set(18, .1, 0);
        Set(13, 0, maskY); Set(16, 0, tipY); Set(17, 0, -tipY);

        var profile = TfBlockDeserializer.ReadBreakReferencePlanes(data);
        Assert.NotNull(profile);
        Assert.Equal(maskY, profile.MaskY);
        Assert.Equal(new[] { (-.85, 0d), (-.2, 0d), (-.1, tipY), (.1, -tipY), (.2, 0d), (.64, 0d) }, profile.Line);
        Assert.Null(TfBlockDeserializer.ReadBreakReferencePlanes(data.AsSpan(0, data.Length - 1)));

        void Set(int plane, double x, double y)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(plane * stride + 33), x);
            BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(plane * stride + 41), y);
        }
    }
}
