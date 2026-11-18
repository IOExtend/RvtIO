using RvtIO.Geometry;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Circular edges of an extrusion profile are sampled over the span the file stores (ceil(|span| / (pi / 16)) equal steps),
/// not from the points of the mesh the Brep is written from.</summary>
public class ArcProfileTests
{
    private static readonly double[] Z = [0, 0, 1];

    // A quarter ring about the origin, counter-clockwise: inner R=2 and outer R=3, the arcs carrying coarse mesh samples.
    private static (List<double[]> Ring, Arc Inner, Arc Outer) QuarterRing(double last)
    {
        double[] At(double r, double degrees) =>
            [r * Math.Cos(degrees * Math.PI / 180), r * Math.Sin(degrees * Math.PI / 180), 0];
        var ring = new List<double[]>
        {
            At(2, 0), At(3, 0), At(3, 30), At(3, 60), At(3, 90), At(2, 90), At(2, 60), At(2, 30),
        };
        var inner = new Arc([0, 0, 0], [1, 0, 0], [0, 1, 0], 2, 0, last);
        var outer = new Arc([0, 0, 0], [1, 0, 0], [0, 1, 0], 3, 0, last);
        return (ring, inner, outer);
    }

    [Theory]
    [InlineData(1.5707963267948974, 9)] // the stored span is a little over eight steps: nine
    [InlineData(1.5707963267948966, 8)] // exactly eight steps of pi / 16
    public void TheStoredSpanDecidesTheNumberOfSteps(double last, int steps)
    {
        var (ring, inner, outer) = QuarterRing(last);
        var loop = IfcExporter.CurveLoopCurrent(ring, Z, outer: true, [inner, outer], out var corners, shift: 0);
        Assert.Equal(4, corners.Count);
        Assert.Equal(4 + 2 * (steps - 1), loop.Count);
        // the samples of the outer arc follow its first corner, equally spaced over the stored span, on the circle
        double step = last / steps;
        for (int i = 1; i < steps; i++)
        {
            var point = loop[1 + i];
            Assert.Equal(3, Math.Sqrt(point[0] * point[0] + point[1] * point[1]), 12);
            Assert.Equal(i * step, Math.Atan2(point[1], point[0]), 12);
        }
    }

    [Fact]
    public void TheLoopStartsAtTheShiftedCornerAndTurnsCounterClockwiseAboutTheDirection()
    {
        var (ring, inner, outer) = QuarterRing(Math.PI / 2);
        var second = IfcExporter.CurveLoopCurrent(ring, Z, outer: true, [inner, outer], out _, shift: 1);
        Assert.Equal(3, second[0][0], 12); // the second corner of the ring, (3, 0)
        double area = 0;
        for (int i = 0; i < second.Count; i++)
            area += second[i][0] * second[(i + 1) % second.Count][1] - second[(i + 1) % second.Count][0] * second[i][1];
        Assert.True(area > 0);

        // clockwise about +Z: the loop is reversed keeping its first corner, the arcs sampled backwards
        var clockwise = IfcExporter.CurveLoopCurrent(ring, [0, 0, -1], outer: true, [inner, outer], out _, shift: 0);
        Assert.Equal(ring[0], clockwise[0]);
        Assert.Equal(second.Count, clockwise.Count);
        Assert.Equal(2, Math.Sqrt(clockwise[1][0] * clockwise[1][0] + clockwise[1][1] * clockwise[1][1]), 12);
    }

    [Fact]
    public void ARingWithoutTheEndsOfAnArcKeepsItsOwnPoints()
    {
        var (ring, inner, outer) = QuarterRing(Math.PI / 2);
        var elsewhere = new Arc([10, 0, 0], [1, 0, 0], [0, 1, 0], 2, 0, Math.PI / 2);
        var plain = IfcExporter.CurveLoopCurrent(ring, Z, outer: true);
        Assert.Equal(plain, IfcExporter.CurveLoopCurrent(ring, Z, outer: true, [elsewhere]));
        Assert.Equal(plain, IfcExporter.CurveLoopCurrent(ring, Z, outer: true, null));
        Assert.NotEqual(plain.Count, IfcExporter.CurveLoopCurrent(ring, Z, outer: true, [inner, outer]).Count);
    }

    [Fact]
    public void TheEdgeSamplerReturnsTheStoredSpanAndSamplesAtLeastFourPoints()
    {
        // a vertical cylinder R = 5 ft cut by a horizontal plane, an edge over 0.2 rad stored with a single segment
        var cylinder = new GRepParserV2.SurfaceRec { Kind = GRepParserV2.SurfKind.Cylinder,
            O = [0, 0, 0], X = [1, 0, 0], Y = [0, 1, 0], Z = [0, 0, 1], R = 5 };
        var plane = new GRepParserV2.SurfaceRec { Kind = GRepParserV2.SurfKind.Plane,
            O = [0, 0, 2], X = [1, 0, 0], Y = [0, 1, 0], Z = [0, 0, 1] };
        var edge = new GRepParserV2.EdgeRec { HasEndpoints = true, Flags = 6,
            Start = new() { UA = 0, VA = 2, UB = 5, VB = 0 }, EndP = new() { UA = 0.2, VA = 2, UB = 5 * Math.Cos(0.2), VB = 5 * Math.Sin(0.2) } };
        var arc = GRepParserV2.ResampleCircularEdge(edge, cylinder, plane);
        Assert.NotNull(arc);
        Assert.Equal(0, arc!.First);
        Assert.Equal(0.2, arc.Last);
        Assert.Equal(new double[] { 0, 0, 2 }, arc.Centre);
        // the mesh keeps at least four points of an edge, equally spaced over the span
        Assert.Equal(2, edge.Interior.Count);
        Assert.Equal(0.2 / 3, edge.Interior[0].UA, 12);

        // an edge that is not circular is no arc, and the arc of a body that keeps its points leaves them as they are
        Assert.Null(GRepParserV2.ResampleCircularEdge(new GRepParserV2.EdgeRec { HasEndpoints = true, Flags = 6 }, plane, plane));
        var kept = new GRepParserV2.EdgeRec { HasEndpoints = true, Flags = 6,
            Start = new() { UA = 0, VA = 2, UB = 5, VB = 0 }, EndP = new() { UA = 0.2, VA = 2, UB = 5 * Math.Cos(0.2), VB = 5 * Math.Sin(0.2) } };
        Assert.NotNull(GRepParserV2.ResampleCircularEdge(kept, cylinder, plane, resample: false));
        Assert.Empty(kept.Interior);
    }

    [Fact]
    public void AnArcThatIsOnlyPartlyFoundLeavesTheWholeRingAsItIs()
    {
        // two arcs are known but the ring carries the samples of one only (the file holds an edge that is no part of this loop)
        var (ring, inner, outer) = QuarterRing(Math.PI / 2);
        var lost = new Arc([0, 0, 0], [1, 0, 0], [0, 1, 0], 2.5, 0, Math.PI / 2);
        var plain = IfcExporter.CurveLoopCurrent(ring, Z, outer: true);
        Assert.Equal(plain, IfcExporter.CurveLoopCurrent(ring, Z, outer: true, [inner, outer, lost], out var corners, shift: 1));
        Assert.Equal(plain, corners);
    }

    [Fact]
    public void TheAreaOfAFaceWithCircularEdgesIsTheAreaOfTheCircles()
    {
        var (ring, inner, outer) = QuarterRing(Math.PI / 2);
        var face = new RvtIO.Model.GeoFace { OuterVertices = ring, MeshArcs = [inner, outer] };
        double exact = 0.5 * (Math.PI / 2) * (3 * 3 - 2 * 2);
        Assert.Equal(exact, WallExtrusion.ExactFaceArea(face, out bool isExact), 12);
        Assert.True(isExact);
        Assert.True(WallExtrusion.LoopArea(ring) < exact - 0.01); // the samples are coarse: the polygon is smaller

        // the same face without the circle of one edge: the polygon, and not an exact area
        face.MeshArcs = [inner, outer, new Arc([0, 0, 0], [1, 0, 0], [0, 1, 0], 2.5, 0, Math.PI / 2)];
        Assert.Equal(WallExtrusion.LoopArea(ring), WallExtrusion.ExactFaceArea(face, out isExact), 12);
        Assert.False(isExact);

        // a ring walked the other way gives the same area
        var reversed = new RvtIO.Model.GeoFace { OuterVertices = Enumerable.Reverse(ring).ToList(), MeshArcs = [inner, outer] };
        Assert.Equal(exact, WallExtrusion.ExactFaceArea(reversed, out _), 12);
    }

    [SkippableFact]
    public void TheEndFaceOfAnArcWallOfSampleGivesTheProfileWrites()
    {
        // a wall profile of 21 points: 10 + 9 steps over five arcs (the inner one cut in three, the outer in two) and two straight sides;
        // the ring of the face carries chord points between the samples of the arcs
        using var stream = TestFiles.OpenBlock("sample-a-wall-9765668-grep.bin");
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        var geometry = ElementBuilder.BuildGeometryData(GRepParserV2.Parse(bytes, 0, BitConverter.ToUInt32(bytes, 8))!)!;
        var solidFaces = geometry.BrepSolids![0].FaceIndices.Select(index => geometry.Faces[index]).ToList();
        var extrusion = WallExtrusion.FindAxisExtrusion(solidFaces, Z, out string reason);
        Assert.True(extrusion != null, reason + string.Join(" | ", solidFaces.Where(f => !f.CurvedSurface).Select(f => { double a = WallExtrusion.ExactFaceArea(f, out bool e); return $"n.z={f.Normal[2]:F1} exact={a:F6} ({e}) polygon={WallExtrusion.LoopArea(f.OuterVertices):F6} arcs={f.MeshArcs?.Count}"; })));
        Assert.Equal(5, extrusion!.Arcs!.Count);
        var ring = extrusion.Loops[0];
        Assert.Equal(5, ArcRuns.FindArcRunsCurrent(ring, extrusion.Arcs).Count);
        double area = 0;
        for (int i = 0; i < ring.Count; i++)
            area += ring[i][0] * ring[(i + 1) % ring.Count][1] - ring[(i + 1) % ring.Count][0] * ring[i][1];
        var loop = IfcExporter.CurveLoopCurrent(ring, [0, 0, area > 0 ? 1 : -1], outer: true, extrusion.Arcs, out var corners, shift: 0);
        Assert.Equal(7, corners.Count);
        Assert.Equal(21, loop.Count);
    }
}
