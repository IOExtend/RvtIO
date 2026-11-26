using static RvtIO.Parsing.TfBlockDeserializer;
using Xunit;

namespace RvtIO.Tests;

public class GElementLoopNestingInputTests
{
    [Theory]
    [InlineData(0, 0, 0x3f75555555555555L, 0x3f41111111111111L)]
    [InlineData(1, 0, 0x3f3c71c71c71c71cL, 0x3f06c16c16c16c16L)]
    [InlineData(2, 0, 0x3f75555555555555L, 0x3efb4e81b4e81b4eL)]
    [InlineData(0, 1, 0x3f7570124aff017bL, 0x3f41267508cc012fL)]
    [InlineData(1, 1, 0x3f3c956db95401f9L, 0x3f06ddf161100194L)]
    [InlineData(2, 1, 0x3f7570124aff017bL, 0x3efb70bb4146684bL)]
    public void LiteralNativeEpsBranchesPreserveExactToleranceAndSequentialDivisionBits(int type, int version, long tol, long eps)
    {
        var value = SelectSourceTessEpsControl(type, 1, version, out var why); Assert.Null(why);
        Assert.NotNull(value); Assert.Equal(tol, value.TessToleranceBits); Assert.Equal(eps, value.VertexEpsilonBits);
        Assert.Equal(version, value.Version); Assert.False(value.RequestedVersionRejected);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void NullVersionUsesExplicitSuppliedCurrentVersionNotAHiddenDefault(int current)
    {
        var value = SelectSourceTessEpsControl(0, current, null, out var why); Assert.Null(why);
        Assert.NotNull(value); Assert.Equal(current, value.Version); Assert.Equal(current, value.CurrentVersion);
        Assert.False(value.RequestedVersionRejected);
    }

    [Theory]
    [InlineData(-1)] [InlineData(2)] [InlineData(int.MinValue)] [InlineData(int.MaxValue)]
    public void InvalidRequestedVersionKeepsCurrentVersionAndWarningState(int requested)
    {
        var value = SelectSourceTessEpsControl(0, 1, requested, out var why); Assert.Null(why);
        Assert.NotNull(value); Assert.True(value.RequestedVersionRejected); Assert.Equal(1, value.Version);
    }

    [Fact]
    public void RequestedVersionCannotExceedTheExplicitCurrentVersion()
    {
        var value = SelectSourceTessEpsControl(0, 0, 1, out var why); Assert.Null(why);
        Assert.NotNull(value); Assert.Equal(0, value.Version); Assert.True(value.RequestedVersionRejected);
    }

    [Theory]
    [InlineData(-1, 1, "tess-eps-type")] [InlineData(3, 1, "tess-eps-type")]
    [InlineData(0, -1, "tess-eps-current-version")] [InlineData(0, 2, "tess-eps-current-version")]
    public void UnsupportedCrashOrRuntimeContextsDoNotBecomeSuccessfulDefaults(int type, int current, string expected)
    {
        Assert.Null(SelectSourceTessEpsControl(type, current, null, out var why)); Assert.Equal(expected, why);
    }

    [Fact]
    public void PlaneFirstFormUsesActualNonUnitNonOrthogonalAxes()
    {
        var metric = Metric(Surface(522, [2, 3, 4], [5, 6, 7]), [0, 0]);
        Assert.Equal(Bits(2, 3, 4), metric.DuBits); Assert.Equal(Bits(5, 6, 7), metric.DvBits);
        Assert.Equal(Bits(29, 56, 56, 110), metric.MatrixBits);
        Assert.Equal(6, metric.QueueIndex); Assert.Equal(100, metric.Start); Assert.Equal(5000, metric.End);
    }

    [Fact]
    public void PlaneDerivativesAreIndependentOfOriginUvAndOrientationFlag()
    {
        var one = Surface(522, [2, 3, 4], [5, 6, 7]);
        var two = one with { Fields = one.Fields.Append(Vector(522, "m_origin", [300, -400, 500]))
            .Append(Byte(523, "m_orientFlag", 0)).ToArray() };
        Assert.Equal(Metric(one, Bits(0, 0)).MatrixBits, Metric(two, Bits(123, -456)).MatrixBits);
    }

    [Fact]
    public void FirstFormCrossTermUsesNativeYThenXThenZAdditionOrder()
    {
        var metric = Metric(Surface(522, [1e16, 1, -1e16], [1, 1, 1]), [0, 0]);
        Assert.Equal(Bits(0)[0], metric.MatrixBits[1]); Assert.Equal(metric.MatrixBits[1], metric.MatrixBits[2]);
        // X+Z+Y would instead produce1. No generic reordered dot product is used.
    }

    [Theory]
    [InlineData(2)] [InlineData(0)] [InlineData(-2)]
    public void CylinderDerivativesUseRadiusSignedSinCosAndRawZAxis(double radius)
    {
        var metric = Metric(Surface(983, [2, 3, 4], [5, 6, 7], [8, 9, 10], radius), Bits(0, 123));
        Assert.Equal(Bits(radius * 5, radius * 6, radius * 7), metric.DuBits);
        Assert.Equal(Bits(8, 9, 10), metric.DvBits); Assert.Equal(Bits(245)[0], metric.MatrixBits[3]);
    }

    [Fact]
    public void CylinderAngleAndRadiusAreNotReplacedWithIdentityMetricOrNormalizedAxes()
    {
        var metric = Metric(Surface(983, [1, 0, 0], [0, 1, 0], [0, 0, 2], 3), Bits(0.75, -42));
        double a = -(3 * Math.Sin(0.75)), b = 3 * Math.Cos(0.75);
        Assert.Equal(Bits(a, b, 0), metric.DuBits); Assert.Equal(Bits(0, 0, 2), metric.DvBits);
        Assert.Equal(Bits((a * a + b * b), 0, 0, 4), metric.MatrixBits);
    }

    [Theory]
    [InlineData("class")] [InlineData("uv-count")] [InlineData("missing-axis")]
    [InlineData("axis-count")] [InlineData("span")] [InlineData("transcendental")]
    public void InvalidOrUnsupportedMetricInputsFailExplicitly(string mutation)
    {
        var s = Surface(522, [1, 0, 0], [0, 1, 0]); IReadOnlyList<long> uv = [0, 0];
        switch (mutation)
        {
            case "class": s = s with { ClassId = 2007 }; break;
            case "uv-count": uv = [0]; break;
            case "missing-axis": s = s with { Fields = [s.Fields[0]] }; break;
            case "axis-count": s = s with { Fields = [Vector(522, "m_xVec", [1, 0]), s.Fields[1]] }; break;
            case "span": s = s with { Fields = [s.Fields[0] with { Items = [s.Fields[0].Items[0] with { End = 109 }, s.Fields[0].Items[1], s.Fields[0].Items[2]] }, s.Fields[1]] }; break;
            default: s = Surface(983, [1, 0, 0], [0, 1, 0], [0, 0, 1], 2); uv = Bits(double.PositiveInfinity, 0); break;
        }
        Assert.Null(ComputeOwnGElementSurfaceFirstForm(s, uv, out var why)); Assert.Equal("surface-first-form", why);
    }

    [Fact]
    public void MultipleLoopsRetainPendingContainmentWithExactSampleEpsAndFirstForm()
    {
        var value = Read(Graph()); Assert.False(value.Complete);
        var loops = Assert.Single(value.Faces).Loops; Assert.Equal(2, loops.Count);
        Assert.All(loops, l => { Assert.Null(l.EarlyShouldBeCcw); Assert.Equal("pending-containment", l.Outcome); });
        Assert.Equal(Bits(2, 0), loops[0].SampleUvBits); Assert.Equal(0x3f41267508cc012fL, loops[0].Epsilon!.VertexEpsilonBits);
        Assert.Equal(Bits(29, 56, 56, 110), loops[0].SurfaceMetric!.MatrixBits);
    }

    [Fact]
    public void SingleLoopReturnsTrueBeforeUvEpsAndSurfaceEvaluation()
    {
        var graph = Replace(Graph(), 1, Owned(1255, "m_nextLoop", null));
        graph = Replace(graph, 6, Vector(522, "m_xVec", [1, 0]));
        var value = Read(graph); Assert.True(value.Complete); var loop = Assert.Single(value.Faces[0].Loops);
        Assert.Equal("single-loop-outer", loop.Outcome); Assert.True(loop.EarlyShouldBeCcw);
        Assert.Null(loop.SampleUvBits); Assert.Null(loop.Epsilon); Assert.Null(loop.SurfaceMetric);
    }

    [Fact]
    public void TwoPointSampleHalvesEachEndpointBeforeAdditionAvoidingSumOverflow()
    {
        var graph = Replace(Graph(), 2, Endpoints([double.MaxValue, -double.MaxValue], [double.MaxValue, -double.MaxValue]));
        var sample = Read(graph).Faces[0].Loops[0].SampleUvBits;
        Assert.Equal(Bits(double.MaxValue, -double.MaxValue), sample);
    }

    [Fact]
    public void MoreThanTwoPointsSelectsIntegerMiddleVertexOnChosenSide()
    {
        var graph = Replace(Graph(), 2, Interior([Point([21, 22], [31, 32]), Point([41, 42], [51, 52])]));
        var sample = Read(graph).Faces[0].Loops[0].SampleUvBits; Assert.Equal(Bits(41, 42), sample); // count4/index2
        graph = Replace(graph, 2, Weak(1244, "m_pFace", 0, 3));
        Assert.Equal(Bits(51, 52), Read(graph).Faces[0].Loops[0].SampleUvBits);
    }

    [Theory]
    [InlineData("open", "excluded-open", null)] [InlineData("empty", "excluded-empty", null)]
    [InlineData("no-face", "no-own-face", false)] [InlineData("no-edge", "no-first-edge", true)]
    [InlineData("zero-points", "insufficient-edge-points", false)] [InlineData("one-point", "insufficient-edge-points", false)]
    [InlineData("mismatch", "face-side-mismatch", false)]
    public void NativeEarlyBranchOutcomesStayDistinctAndDoNotNeedEps(string change, string expected, bool? result)
    {
        var graph = change switch
        {
            "open" => Replace(Graph(), 1, Byte(1255, "m_open", 7)),
            "empty" => Replace(Graph(), 1, Weak(1255, "m_next", 4)),
            "no-face" => Replace(Graph(), 1, Weak(1255, "m_pFace", 0)),
            "no-edge" => Replace(Graph(), 1, Weak(1255, "m_next", 0)),
            "zero-points" => Replace(Graph(), 2, Byte(1244, "m_flags", 0)),
            "one-point" => Replace(Graph(), 2, Byte(1244, "m_flags", 2)),
            _ => Replace(Graph(), 2, Weak(1244, "m_pFace", 0, 0))
        };
        var loop = Read(graph).Faces[0].Loops[0]; Assert.Equal(expected, loop.Outcome); Assert.Equal(result, loop.EarlyShouldBeCcw);
        Assert.Null(loop.Epsilon); Assert.Null(loop.SurfaceMetric); Assert.Null(loop.SampleUvBits);
    }

    [Fact]
    public void NullSurfaceKeepsPendingContainmentWithoutFabricatingAnIdentityMetric()
    {
        var loops = Read(Replace(Graph(), 0, Owned(1589, "m_pSurf", null))).Faces[0].Loops;
        Assert.All(loops, l => { Assert.Equal("pending-containment", l.Outcome); Assert.Null(l.SurfaceMetric); });
    }

    [Fact]
    public void OwnFaceWithoutFirstLoopReturnsFalseBeforeTheFirstEdgeChecks()
    {
        var graph = Replace(Graph(), 1, Weak(1255, "m_pFace", 10));
        graph = graph with { Nodes = graph.Nodes.Append(Node(7, 1589, 10,
            Owned(1590, "m_pFirstLoop", null), Owned(1589, "m_pSurf", null))).ToArray() };
        var value = Read(graph); var loop = value.Faces[0].Loops[0];
        Assert.Equal("own-face-no-first-loop", loop.Outcome); Assert.False(loop.EarlyShouldBeCcw);
        Assert.Null(loop.Epsilon); Assert.Empty(value.Faces[1].Loops);
    }

    [Fact]
    public void PlaneOrCylinderSurfaceAssociationUsesQualifiedOwnedQueueAndHandle()
    {
        var graph = Graph(); graph = graph with { Nodes = graph.Nodes.Select(n => n.QueueIndex == 6 ?
            Surface(983, [1, 0, 0], [0, 1, 0], [0, 0, 1], 2) : n).ToArray() };
        graph = Replace(graph, 0, Owned(1589, "m_pSurf", (6, (ushort)983, 9u)));
        Assert.All(Read(graph).Faces[0].Loops, l => Assert.Equal((ushort)983, l.SurfaceMetric!.ClassId));
        graph = Replace(graph, 0, Owned(1589, "m_pSurf", (6, (ushort)983, 999u)));
        Assert.Null(SelectOwnGElementLoopNestingInputs(graph, 1, out var why)); Assert.StartsWith("loop-nesting-inputs:", why);
    }

    private static OwnGElementFreshLoopNestingInputs Read(OwnGElementGeometryGraph graph)
    {
        var value = SelectOwnGElementLoopNestingInputs(graph, 1, out var why); Assert.Null(why);
        return Assert.IsType<OwnGElementFreshLoopNestingInputs>(value);
    }
    private static GElementSurfaceFirstForm Metric(GElementGraphNode surface, IReadOnlyList<long> uv)
    {
        var value = ComputeOwnGElementSurfaceFirstForm(surface, uv, out var why); Assert.Null(why);
        return Assert.IsType<GElementSurfaceFirstForm>(value);
    }
    private static long[] Bits(params double[] values) => values.Select(BitConverter.DoubleToInt64Bits).ToArray();
    private static GElementGraphNode Surface(ushort cls, double[] x, double[] y, double[]? z = null, double radius = 0)
        => Node(6, cls, 9, new[] { Vector(cls, "m_xVec", x), Vector(cls, "m_yVec", y) }
            .Concat(cls == 983 ? [Vector(cls, "m_zVec", z!), Double(cls, "m_radius", radius)] : []).ToArray());
    private static GElementGraphField Vector(int cls, string name, double[] values)
        => new(cls, name, (uint)values.Length, 100, 100 + values.Length * 8,
            values.Select((v, i) => new GElementGraphItem(100 + i * 8, 108 + i * 8, Bits(v), null, [])).ToArray());
    private static GElementGraphField Double(int cls, string name, double value) => new(cls, name, null, 100, 108, [new(100, 108, Bits(value), null, [])]);
    private static OwnGElementGeometryGraph Graph() => new(1000, 0,
        [Node(-1, 2007, 1000), Node(0, 1589, 3, Owned(1590, "m_pFirstLoop", (1, (ushort)1254, 4u)), Owned(1589, "m_pSurf", (6, (ushort)522, 9u))),
         Node(1, 1254, 4, Owned(1255, "m_nextLoop", (4, (ushort)1257, 7u)), Weak(1255, "m_pFace", 3), Weak(1255, "m_next", 5), Weak(1255, "m_prev", 6), Byte(1255, "m_open", 0)),
         Edge(2, 5, [3, 0], [6, 0]), Edge(3, 6, [0, 3], [0, 4]),
         Node(4, 1257, 7, Owned(1255, "m_nextLoop", null), Weak(1255, "m_pFace", 3), Weak(1255, "m_next", 8), Weak(1255, "m_prev", 8), Byte(1255, "m_open", 0)),
         Edge(5, 8, [3, 3], [7, 0]), Surface(522, [2, 3, 4], [5, 6, 7])], [], 10000);
    private static GElementGraphNode Node(int q, ushort cls, uint handle, params GElementGraphField[] fs) => new(q, cls, handle, 100, 5000, fs);
    private static GElementGraphNode Edge(int q, uint handle, uint[] faces, uint[] next) => Node(q, 1243, handle,
        Weak(1244, "m_pFace", faces), Weak(1244, "m_next", next), Weak(1244, "m_prev", 0, 0), Byte(1244, "m_flags", 7),
        Endpoints([0, 0], [4, 0]), Interior([]));
    private static OwnGElementGeometryGraph Replace(OwnGElementGeometryGraph g, int q, params GElementGraphField[] fs)
        => g with { Nodes = g.Nodes.Select(n => n.QueueIndex != q ? n : n with
            { Fields = n.Fields.Select(f => fs.Where(x => x.Declaring == f.Declaring && x.Name == f.Name).DefaultIfEmpty(f).Single()).ToArray() }).ToArray() };
    private static GElementGraphField Weak(int cls, string name, params uint[] keys) => new(cls, name, keys.Length == 2 ? 2u : null, 100, 100 + keys.Length * 4,
        keys.Select((k, i) => new GElementGraphItem(100 + i * 4, 104 + i * 4, [k], null, [])).ToArray());
    private static GElementGraphField Owned(int cls, string name, (int Q, ushort Class, uint Handle)? target) => new(cls, name, null, 100, 106,
        [new(100, 106, [], target is { } t ? new(t.Handle, t.Class, t.Q, null, [], 100, 106) : new(0, null, null, null, [], 100, 104), [])]);
    private static GElementGraphField Byte(int cls, string name, long value) => new(cls, name, null, 100, 101, [new(100, 101, [value], null, [])]);
    private static GElementGraphItem Point(double[] a, double[] b) => new(1000, 1032, [], null,
        [new(1248, "uv", 2, 1000, 1032, [new(1000, 1016, Bits(a), null, []), new(1016, 1032, Bits(b), null, [])])]);
    private static GElementGraphField Endpoints(double[] a, double[] b) => new(1244, "m_firstAndLastEdgePnts", 2, 1000, 1064, [Point(a, [10, 0]), Point(b, [10, 4])]);
    private static GElementGraphField Interior(GElementGraphItem[] points) => new(1244, "m_interiorEdgePnts", (uint)points.Length, 1000, 1004 + points.Length * 32, points);
}
