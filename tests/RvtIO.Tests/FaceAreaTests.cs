using RvtIO.Geometry;
using RvtIO.Geometry.Meshing;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// The two end faces of an extrusion are paired by the area <c>computeAreaOfFace</c> gives them, within 1e-4. For a Face with a curved Edge that is
/// the Romberg quadrature of <see cref="FaceArea"/> over the Hermite spline of the stored Edge points, not the polygon area: the caps of a
/// cylinder have separate Edges with separate point lists and differ by its noise.
/// </summary>
public class FaceAreaTests
{
    private static byte[] Block(string name)
    {
        using var stream = TestFiles.OpenBlock($"{name}.bin");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static FaceArea.Coedge Line((double, double) a, (double, double) b, bool second = false) =>
        new([a, b], 6, second);

    [Fact]
    public void AStraightLoop_IsItsExactArea()
    {
        var square = new[]
        {
            Line((0, 0), (3, 0)), Line((3, 0), (3, 2)), Line((3, 2), (0, 2)), Line((0, 2), (0, 0)),
        };
        Assert.Equal(6.0, FaceArea.Compute(square)!.Value, 12);
    }

    [Fact]
    public void ALoopWalkedTheOtherWay_HasTheSameArea()
    {
        // the second Face of an Edge walks it against its stored direction: the stored list is the one of the first Face
        var square = new[]
        {
            Line((0, 0), (0, 2), second: true), Line((0, 2), (3, 2), second: true), Line((3, 2), (3, 0), second: true), Line((3, 0), (0, 0), second: true),
        };
        Assert.Equal(6.0, FaceArea.Compute(square)!.Value, 12);
    }

    [Fact]
    public void AnEdgeWithoutTwoPoints_HasNoArea()
    {
        Assert.Null(FaceArea.Compute([new FaceArea.Coedge([(0, 0)], 6, false)]));
    }

    [Fact]
    public void AHalfDiscWithAFewArcPoints_IsNotItsExactArea()
    {
        // the half circle of radius 1 through 5 stored points, closed by its diameter: the spline through the points is not the circle
        var arc = Enumerable.Range(0, 5).Select(i => (Math.Cos(Math.PI * i / 4), Math.Sin(Math.PI * i / 4))).ToList();
        var area = FaceArea.Compute([new FaceArea.Coedge(arc, 14, false), Line((-1, 0), (1, 0))])!.Value;
        Assert.InRange(area, Math.PI / 2 * 0.98, Math.PI / 2 * 1.02);
        Assert.NotEqual(Math.PI / 2, area, 9);
    }

    [SkippableFact]
    public void TheCapsOfRoom8964053_HaveTheAreasReferenceComputes_AndAreNotPaired()
    {
        byte[] data = Block("sample-a-room-8964053-curved");
        var body = GRepParserV2.ParseCurvedSpaceBoundary(data, 0, (uint)(data.Length - 16), 8964053)!;
        var geometry = ElementBuilder.BuildGeometryData(body)!;
        var caps = geometry.Faces.Where(face => face.QuadratureArea != null).ToList();
        Assert.Equal(2, caps.Count);
        Assert.Equal([58.129188, 58.130268], caps.Select(face => face.QuadratureArea!.Value).Order().Select(area => Math.Round(area, 6)));
        Assert.Null(IfcExporter.CalculateOthersCurrentExtrusion(geometry.Faces, IfcExporter.OthersCurrentAxesZ));
    }

    [SkippableFact]
    public void TheCapsOfArcWall8961372_HaveTheAreasReferenceComputes_AndTheWallIsNoStandardCaseWall()
    {
        byte[] data = Block("sample-a-wall-8961372-arc");
        var geometry = ElementBuilder.BuildGeometryData(GRepParserV2.Parse(data, 0, BitConverter.ToUInt32(data, 8))!)!;
        var caps = geometry.Faces.Where(face => face.QuadratureArea != null && Math.Abs(face.Normal[2]) > 0.99).ToList();
        Assert.Equal([2.094069707, 2.094191829], caps.Select(face => face.QuadratureArea!.Value).Order().Select(area => Math.Round(area, 9)));
        Assert.False(WallExtrusion.IsStandardCaseBody(geometry));
    }
}
