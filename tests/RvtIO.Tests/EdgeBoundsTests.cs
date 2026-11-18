using RvtIO.Geometry;
using RvtIO.Parsing;
using RvtIO.Ifc;
using RvtIO.Internal;
using Xunit;

namespace RvtIO.Tests;

public class EdgeBoundsTests
{
    [SkippableTheory]
    [InlineData("wall")] [InlineData("nonwall")] [InlineData("duplicate_class")]
    [InlineData("unknown_class")] [InlineData("duplicate_grep")]
    [InlineData("wrong_owner")] [InlineData("wrong_grep_class")]
    public void NativeWallSamplingRequiresUniqueOwnedClassAndGrepSource(string mutation)
    {
        const uint id = 9750515;
        using var stream = TestFiles.OpenBlock($"sample-a-wall-{id}-grep.bin");
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        byte[] bytes = memory.ToArray(); uint size = BitConverter.ToUInt32(bytes, 8);
        var state = new ParsingState();
        state.OwnedElementClassRoots[id] = new(1, 361);
        state.GrepBlocks[id] = GRepParserV2.Parse(bytes, 0, size)!;
        int grepCount = 1; uint owner = id; ushort cls = 2007;
        switch (mutation)
        {
            case "nonwall": state.OwnedElementClassRoots[id] = new(1, 1794); break;
            case "duplicate_class": state.OwnedElementClassRoots[id] = new(2, 361); break;
            case "unknown_class": state.OwnedElementClassRoots.Clear(); break;
            case "duplicate_grep": grepCount = 2; break;
            case "wrong_owner": owner++; break;
            case "wrong_grep_class": cls = 2006; break;
        }
        var body = RvtFileReader.DecodeOwnNativeWallBody(state, bytes, 0, size, cls, owner, grepCount);
        // The ordinary parse samples every circular edge and carries the bounds of the sampled edge points itself, so
        // the native parse has nothing left to correct.
        Assert.Null(body);
        var ordinary = GRepParserV2.Parse(bytes, 0, size)!;
        Assert.All(ordinary.Faces, face => Assert.Equal(6, face.MeshEdgeBounds!.Length));
        Assert.False(WallExtrusion.IsStandardCaseBody(ElementBuilder.BuildGeometryData(ordinary)));
    }

    [SkippableFact]
    public void AVerticalCylinderUsesItsAnalyticAxisDespiteSkewTessellationNormals()
    {
        using var stream = TestFiles.OpenBlock("sample-a-wall-9765668-grep.bin");
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        var geometry = ElementBuilder.BuildGeometryData(
            GRepParserV2.Parse(bytes, 0, BitConverter.ToUInt32(bytes, 8), nativeWallBody: true)!)!;
        Assert.Contains(geometry.Faces, face => face.CurvedSurface && Math.Abs(face.Normal[2]) > 0.001);
        Assert.True(WallExtrusion.IsStandardCaseBody(geometry));
        var state = new ParsingState(); state.OwnedElementClassRoots[9765668] = new(1, 361);
        state.GrepBlocks[9765668] = GRepParserV2.Parse(bytes, 0, BitConverter.ToUInt32(bytes, 8))!;
        Assert.Null(RvtFileReader.DecodeOwnNativeWallBody(state, bytes, 0, BitConverter.ToUInt32(bytes, 8), 2007, 9765668, 1));
        foreach (var face in geometry.Faces.Where(face => face.CurvedSurface))
            face.AnalyticCylinderAxis = null;
        Assert.False(WallExtrusion.IsStandardCaseBody(geometry));
    }

    [SkippableTheory]
    [InlineData(8954843)] [InlineData(8959579)] [InlineData(9750515)]
    [InlineData(9750549)] [InlineData(9765007)] [InlineData(9765665)] [InlineData(9766112)]
    public void AuthoredArcWallsCompareTheStoredPlaneEnvelopeWithEdgePointBounds(int id)
    {
        using var stream = TestFiles.OpenBlock($"sample-a-wall-{id}-grep.bin");
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        var grep = GRepParserV2.Parse(bytes, 0, BitConverter.ToUInt32(bytes, 8), nativeWallBody: true)!;
        var geometry = ElementBuilder.BuildGeometryData(grep)!;
        Assert.All(geometry.Faces, face => Assert.Equal(6, face.MeshEdgeBounds!.Length));
        Assert.All(geometry.Faces.Where(face => !face.CurvedSurface),
            face => Assert.Equal(6, face.MeshPlaneEnvelopeCorners!.Length));
        Assert.False(WallExtrusion.IsStandardCaseBody(geometry));
        var ordinary = ElementBuilder.BuildGeometryData(GRepParserV2.Parse(bytes, 0, BitConverter.ToUInt32(bytes, 8))!)!;
        Assert.False(WallExtrusion.IsStandardCaseBody(ordinary));
        // Native Low mesh bounds, in source feet.
        double[]? expected = id switch
        {
            8959579 => [-42.115887196606899, -66.028923392066289, 28.707349081364828, -41.301445309961508, -63.675250604555764, 38.024934383202094],
            9750515 => [-35.20580361328507, -73.727384853049045, 29.035433070866141, -29.881781132610971, -72.371048433650785, 38.024934383202094],
            9750549 => [-42.317310817213666, -66.087229906794391, 29.035433070866141, -41.645789652335374, -63.550277083438502, 38.024934383202094],
            9765007 => [-37.124942506063014, -58.910833987209692, 34.940944881889763, -34.16638830821239, -58.109296533505074, 38.024934383202094],
            9765665 => [-41.722110470041436, -65.752350700035052, 29.035433070866141, -41.231397176524631, -63.922739874952661, 34.940944881889763],
            9766112 => [-41.722110470041436, -65.752350700035052, 34.940944881889763, -41.231397176524631, -63.922739874952661, 38.024934383202094],
            _ => null,
        };
        if (expected != null)
        {
            var points = geometry.Faces.SelectMany(face => face.OuterVertices!).ToList();
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.InRange(Math.Abs(points.Min(p => p[axis]) - expected[axis]), 0, 0.0001 / 0.3048);
                Assert.InRange(Math.Abs(points.Max(p => p[axis]) - expected[axis + 3]), 0, 0.0001 / 0.3048);
            }
        }
    }

    [Fact]
    public void AuthoredSamplesUseTheChosenFacesUvFrameWithoutAddingCurveExtrema()
    {
        var surface = new GRepParserV2.SurfaceRec { Kind = GRepParserV2.SurfKind.Cylinder,
            O = [10, 20, 30], X = [0, 1, 0], Y = [-1, 0, 0], Z = [0, 0, 1], R = 2 };
        var edge = new GRepParserV2.EdgeRec { HasEndpoints = true, Flags = 6,
            Start = new() { UA = 0, VA = 3, UB = Math.PI / 4, VB = 4 },
            EndP = new() { UA = Math.PI, VA = 5, UB = 3 * Math.PI / 4, VB = 6 } };
        var a = GRepParserV2.EvaluateEdgePoints(edge, true, surface)!;
        Assert.Equal(2, a.Count);
        Assert.Equal(new double[] { 10, 22, 33 }, a[0]);
        Assert.Equal(20 - 2, a[1][1], 12);
        var b = GRepParserV2.EvaluateEdgePoints(edge, false, surface)!;
        Assert.Equal(2, b.Count);
        Assert.All(b, point => Assert.Equal(10 - Math.Sqrt(2), point[0], 12));
        Assert.Equal(34, b[0][2]); Assert.Equal(36, b[1][2]);
        edge.Interior.Add(new() { UB = Math.PI / 2, VB = 5 });
        var interior = GRepParserV2.EvaluateEdgePoints(edge, false, surface)!;
        Assert.Equal(3, interior.Count);
        Assert.Equal(new double[] { 8, 20, 35 }, interior[1]);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(2, 2)] [InlineData(4, 2)] [InlineData(6, 3)]
    public void EndpointFlagsAreRespectedAndInteriorSamplesAreAlwaysUsed(byte flags, int count)
    {
        var plane = new GRepParserV2.SurfaceRec { Kind = GRepParserV2.SurfKind.Plane,
            O = [1, 2, 3], X = [1, 0, 0], Y = [0, 1, 0] };
        var edge = new GRepParserV2.EdgeRec { HasEndpoints = true, Flags = flags,
            Start = new() { UA = 10 }, EndP = new() { UA = 20 }, Interior = [new() { UA = 15, VA = 25 }] };
        var points = GRepParserV2.EvaluateEdgePoints(edge, true, plane)!;
        Assert.Equal(count, points.Count);
        Assert.Contains(points, p => p.SequenceEqual(new double[] { 16, 27, 3 }));
    }

    [Fact]
    public void UnsupportedSurfaceMissingEndpointsOrNonfiniteInputDoesNotConfirmBounds()
    {
        var edge = new GRepParserV2.EdgeRec { HasEndpoints = true, Flags = 6 };
        var surface = new GRepParserV2.SurfaceRec { Kind = GRepParserV2.SurfKind.Cone };
        Assert.Null(GRepParserV2.EvaluateEdgePoints(edge, true, surface));
        surface.Kind = GRepParserV2.SurfKind.Plane;
        edge.HasEndpoints = false;
        Assert.Null(GRepParserV2.EvaluateEdgePoints(edge, true, surface));
        edge.HasEndpoints = true; edge.Start.UA = double.NaN;
        Assert.Null(GRepParserV2.EvaluateEdgePoints(edge, true, surface));
    }
}
