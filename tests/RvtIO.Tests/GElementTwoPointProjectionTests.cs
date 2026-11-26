using static RvtIO.Parsing.TfBlockDeserializer;
using Xunit;

namespace RvtIO.Tests;

public class GElementTwoPointProjectionTests
{
    [Fact]
    public void PlanePointUsesRawNonUnitNonOrthogonalAxesThenOrigin()
    {
        var value = EvaluateSourceOwnPlanePoint(Plane([10, 20, 30], [2, 3, 4], [5, 6, 7]), Bits(2, -1), out var why);
        Assert.Null(why); Assert.Equal(Bits(9, 20, 31), value);
    }

    [Fact]
    public void PlanePointDoesNotApplyOrientationFlagToPositionOutput()
    {
        var p = Plane([10, 20, 30], [2, 3, 4], [5, 6, 7]);
        p = p with { Fields = p.Fields.Append(Scalar(523, "m_orientFlag", 0)).ToArray() };
        Assert.Equal(Bits(9, 20, 31), EvaluateSourceOwnPlanePoint(p, Bits(2, -1), out _));
    }

    [Theory]
    [InlineData("class")] [InlineData("uv")] [InlineData("axis")] [InlineData("nonfinite")]
    public void UnsupportedPlanePointInputsFailExplicitly(string change)
    {
        var p = Plane([0, 0, 0], [1, 0, 0], [0, 1, 0]); var uv = Bits(1, 2);
        switch (change)
        {
            case "class": p = p with { ClassId = 983 }; break;
            case "uv": uv = [0]; break;
            case "axis": p = p with { Fields = p.Fields.Take(2).ToArray() }; break;
            default: uv = Bits(double.NaN, 0); break;
        }
        Assert.Null(EvaluateSourceOwnPlanePoint(p, uv, out var why)); Assert.Equal("plane-point", why);
    }

    [Theory]
    [InlineData(0.5, 1, 0.5, 1)] [InlineData(-1, 0, 0, 1)] [InlineData(2, 0, 1, 1)]
    [InlineData(0, 0, 0, 0)] [InlineData(1, 0, 1, 0)]
    public void SourceSegmentProjectionPreservesParameterAndDistance(double u, double v, double parameter, double distance)
    {
        var value = Project([0, 0, 0], [1, 0, 0], [u, v, 0]);
        Assert.Equal(Bits(parameter)[0], value.ParameterBits); Assert.Equal(Bits(distance)[0], value.DistanceBits);
    }

    [Fact]
    public void ParameterSnapUsesExactNativeThresholdsAndAdjacentDoubles()
    {
        double low = BitConverter.Int64BitsToDouble(0x3e112e0be826d695), high = BitConverter.Int64BitsToDouble(0x3fefffffff768fa1);
        Assert.Equal(0L, Project([0, 0, 0], [1, 0, 0], [low, 0, 0]).ParameterBits);
        Assert.Equal(Bits(Math.BitIncrement(low))[0], Project([0, 0, 0], [1, 0, 0], [Math.BitIncrement(low), 0, 0]).ParameterBits);
        Assert.Equal(Bits(1)[0], Project([0, 0, 0], [1, 0, 0], [high, 0, 0]).ParameterBits);
        Assert.Equal(Bits(Math.BitDecrement(high))[0], Project([0, 0, 0], [1, 0, 0], [Math.BitDecrement(high), 0, 0]).ParameterBits);
    }

    [Fact]
    public void DenominatorFloorIsNativeNotAnInventedDegenerateSegmentRule()
    {
        var value = Project([0, 0, 0], [1e-10, 0, 0], [1e-10, 0, 0]);
        double t = (1e-10 * 1e-10) / 1e-18;
        Assert.Equal(Bits(t)[0], value.ParameterBits); Assert.NotEqual(Bits(1)[0], value.ParameterBits);
    }

    [Fact]
    public void ZeroLengthSegmentUsesFloorAndReturnsDistanceToItsEndpoint()
    {
        var value = Project([1, 2, 3], [1, 2, 3], [4, 6, 3]);
        Assert.Equal(0L, value.ParameterBits); Assert.Equal(Bits(5)[0], value.DistanceBits); Assert.Equal(Bits(0, 0, 0), value.DirectionBits);
    }

    [Fact]
    public void DotOrderAndResidualArithmeticAreRetainedAsBits()
    {
        var value = Project([1, 2, 3], [5, 7, 9], [2, 4, 8]);
        double t = ((5.0 * 2 + 4.0 * 1) + 6.0 * 5) / ((5.0 * 5 + 4.0 * 4) + 6.0 * 6);
        Assert.Equal(Bits(t)[0], value.ParameterBits);
        Assert.Equal(Bits(1.0 + (-t) * 4.0, 2.0 + (-t) * 5.0, 5.0 + (-t) * 6.0), value.ResidualBits);
    }

    [Theory]
    [InlineData("shape")] [InlineData("nonfinite")] [InlineData("overflow")]
    public void UnsupportedProjectionArithmeticFailsNotZeroSuccess(string change)
    {
        var a = Bits(0, 0, 0); var z = Bits(1, 0, 0); var p = Bits(0, 1, 0);
        if (change == "shape") a = [0]; else if (change == "nonfinite") p = Bits(double.PositiveInfinity, 0, 0);
        else z = Bits(double.MaxValue, 0, 0);
        Assert.Null(ProjectSourceTwoPointSegment(a, z, p, out var why)); Assert.Equal("two-point-projection", why);
    }

    [Fact]
    public void ProjectionPhaseKeepsPolygonPendingWhenAllDistancesExceedEps()
    {
        var value = Read(Graph()); Assert.False(value.Complete);
        var check = Assert.Single(value.Faces[0].Loops[1].Checks);
        Assert.Equal("pending-polygon", check.Outcome); Assert.Null(check.PointWithinState);
        var edge = Assert.Single(check.Edges); Assert.Equal("projected", edge.Outcome);
        Assert.Equal(Bits(12, 10, 0), check.QueryXyzBits); Assert.Equal(Bits(0, 0, 0), edge.FirstXyzBits);
        Assert.NotNull(edge.Projection); Assert.Null(value.Faces[0].Loops[1].ResolvedShouldBeCcw);
    }

    [Fact]
    public void ExactDistanceWithinEpsReturnsBoundaryTwoButNotFinalLoopOrientation()
    {
        var graph = Replace(Graph(), 4, Endpoints([10, 0], [14, 0]));
        var check = Assert.Single(Read(graph).Faces[0].Loops[1].Checks);
        Assert.Equal("boundary-first-sample", check.Outcome); Assert.Equal(2, check.PointWithinState);
        Assert.Equal("boundary", Assert.Single(check.Edges).Outcome);
    }

    [Fact]
    public void FirstNonNullEdgeFaceControlsEndpointsEvenWhenLoopBelongsToSecondFace()
    {
        var graph = Graph(); graph = Replace(graph, 2, Weak(1244, "m_pFace", 9, 3), Weak(1244, "m_next", 0, 4));
        graph = graph with { Nodes = graph.Nodes.Concat([
            Node(6, 1589, 9, Owned(1590, "m_pFirstLoop", null), Owned(1589, "m_pSurf", (7, (ushort)522, 10u))),
            Plane([0, 0, 100], [1, 0, 0], [0, 1, 0]) with { QueueIndex = 7, Handle = 10 }]).ToArray() };
        var edge = Assert.Single(Read(graph).Faces[0].Loops[1].Checks[0].Edges);
        Assert.Equal(6, edge.DefaultFaceQueueIndex); Assert.Equal(Bits(0, 0, 100), edge.FirstXyzBits);
    }

    [Fact]
    public void NullFirstFaceSelectsSecondSideAndEndpointFlagFlipsCancel()
    {
        var graph = Replace(Graph(), 2, Weak(1244, "m_pFace", 0, 3), Weak(1244, "m_next", 0, 4), Scalar(1244, "m_flags", 6));
        var edge = Assert.Single(Read(graph).Faces[0].Loops[1].Checks[0].Edges);
        Assert.Equal(0, edge.DefaultFaceQueueIndex); Assert.Equal(Bits(0, 0), edge.FirstUvBits); Assert.Equal(Bits(20, 0), edge.LastUvBits);
    }

    [Fact]
    public void MoreThanTwoPointsRemainUnsupportedNotAChordApproximation()
    {
        var graph = Replace(Graph(), 2, new GElementGraphField(1244, "m_interiorEdgePnts", 1, 100, 136, [Point([10, 2])]));
        var check = Assert.Single(Read(graph).Faces[0].Loops[1].Checks);
        Assert.Equal("unsupported-point-count", check.Outcome); Assert.Null(check.PointWithinState);
        Assert.Null(Assert.Single(check.Edges).Projection);
    }

    [Fact]
    public void ExistingEnvelopeOutsideDecisionDoesNotNeedProjection()
    {
        var value = Read(Graph()); var check = Assert.Single(value.Faces[0].Loops[0].Checks);
        Assert.Equal("rejected-envelope-outside", check.Outcome); Assert.Equal(0, check.PointWithinState); Assert.Empty(check.Edges);
    }

    private static SourceTwoPointProjection Project(double[] a, double[] z, double[] p)
    {
        var value = ProjectSourceTwoPointSegment(Bits(a), Bits(z), Bits(p), out var why); Assert.Null(why);
        return Assert.IsType<SourceTwoPointProjection>(value);
    }
    private static OwnGElementFreshTwoPointProjections Read(OwnGElementGeometryGraph g)
    {
        var value = SelectOwnGElementLoopTwoPointProjections(g, 1, out var why); Assert.Null(why);
        return Assert.IsType<OwnGElementFreshTwoPointProjections>(value);
    }
    private static long[] Bits(params double[] v) => v.Select(BitConverter.DoubleToInt64Bits).ToArray();
    private static GElementGraphNode Plane(double[] origin, double[] x, double[] y) => Node(5, 522, 8, Vector("m_origin", origin), Vector("m_xVec", x), Vector("m_yVec", y));
    private static OwnGElementGeometryGraph Graph() => new(1000, 0,
        [Node(-1, 2007, 1000), Node(0, 1589, 3, Owned(1590, "m_pFirstLoop", (1, (ushort)1254, 4u)), Owned(1589, "m_pSurf", (5, (ushort)522, 8u))),
         Node(1, 1254, 4, Owned(1255, "m_nextLoop", (3, (ushort)1254, 6u)), Weak(1255, "m_pFace", 3), Weak(1255, "m_next", 5), Weak(1255, "m_prev", 5), Scalar(1255, "m_open", 0), Envelope(0, 0, 20, 20)),
         Edge(2, 5, 4, [0, 0], [20, 0]),
         Node(3, 1254, 6, Owned(1255, "m_nextLoop", null), Weak(1255, "m_pFace", 3), Weak(1255, "m_next", 7), Weak(1255, "m_prev", 7), Scalar(1255, "m_open", 0), Envelope(10, 10, 14, 14)),
         Edge(4, 7, 6, [10, 10], [14, 10]), Plane([0, 0, 0], [1, 0, 0], [0, 1, 0])], [], 10000);
    private static GElementGraphNode Node(int q, ushort cls, uint h, params GElementGraphField[] f) => new(q, cls, h, 100, 5000, f);
    private static GElementGraphNode Edge(int q, uint h, uint next, double[] a, double[] z) => Node(q, 1243, h,
        Weak(1244, "m_pFace", 3, 0), Weak(1244, "m_next", next, 0), Weak(1244, "m_prev", 0, 0), Scalar(1244, "m_flags", 7),
        Endpoints(a, z), new(1244, "m_interiorEdgePnts", 0, 100, 104, []));
    private static GElementGraphField Endpoints(double[] a, double[] z) => new(1244, "m_firstAndLastEdgePnts", 2, 100, 164, [Point(a), Point(z)]);
    private static GElementGraphItem Point(double[] p) => new(100, 132, [], null,
        [new(1248, "uv", 2, 100, 132, [new(100, 116, Bits(p), null, []), new(116, 132, Bits(p), null, [])])]);
    private static GElementGraphField Envelope(double a, double b, double c, double d) => new(1255, "m_Envelope", null, 100, 132,
        [new(100, 132, [], null, [new(524, "m_corners", 2, 100, 132, [new(100, 116, Bits(a, b), null, []), new(116, 132, Bits(c, d), null, [])])])]);
    private static GElementGraphField Vector(string name, double[] v) => new(522, name, 3, 100, 124,
        v.Select((x, i) => new GElementGraphItem(100 + i * 8, 108 + i * 8, Bits(x), null, [])).ToArray());
    private static GElementGraphField Weak(int cls, string name, params uint[] keys) => new(cls, name, keys.Length == 2 ? 2u : null, 100, 100 + keys.Length * 4,
        keys.Select((k, i) => new GElementGraphItem(100 + i * 4, 104 + i * 4, [k], null, [])).ToArray());
    private static GElementGraphField Owned(int cls, string name, (int Q, ushort Class, uint H)? target) => new(cls, name, null, 100, 106,
        [new(100, 106, [], target is { } t ? new(t.H, t.Class, t.Q, null, [], 100, 106) : new(0, null, null, null, [], 100, 104), [])]);
    private static GElementGraphField Scalar(int cls, string name, long v) => new(cls, name, null, 100, 101, [new(100, 101, [v], null, [])]);
    private static OwnGElementGeometryGraph Replace(OwnGElementGeometryGraph g, int q, params GElementGraphField[] fs) => g with
        { Nodes = g.Nodes.Select(n => n.QueueIndex != q ? n : n with { Fields = n.Fields.Select(f => fs.Where(x => x.Declaring == f.Declaring && x.Name == f.Name).DefaultIfEmpty(f).Single()).ToArray() }).ToArray() };
}
