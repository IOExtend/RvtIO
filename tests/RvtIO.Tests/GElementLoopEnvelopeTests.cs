using static RvtIO.Parsing.TfBlockDeserializer;
using Xunit;

namespace RvtIO.Tests;

public class GElementLoopEnvelopeTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(1, 1)] [InlineData(0.5, 1)]
    public void InsideAndBoundaryUseNativeMarginBeforeReadingMetric(double u, double v)
    {
        var value = Evaluate([0, 0, 1, 1], [u, v], -1, [double.NaN, 0, 0, double.NaN]);
        Assert.True(value.IsInside); Assert.Equal("inside-margin", value.Outcome);
        Assert.Null(value.GrowthBits); Assert.Null(value.DistanceBits); Assert.Equal(Bits(u, v), value.ClosestBits);
    }

    [Fact]
    public void ExactTinyMarginBoundaryAndNextRepresentableValueFollowStrictComparisons()
    {
        double tiny = BitConverter.Int64BitsToDouble(0x3e112e0be826d695);
        Assert.True(Evaluate([0, 0, 1, 1], [-tiny, 0], 0).IsInside);
        var outside = Evaluate([0, 0, 1, 1], [-Math.BitIncrement(tiny), 0], 0);
        Assert.False(outside.IsInside); Assert.Equal("axis-reject", outside.Outcome);
    }

    [Theory]
    [InlineData(2, 0, 1, 1)] [InlineData(0, 2, 1, 1)]
    public void InvalidFiniteEnvelopeIsNativeWarningFalseNotAnEmptySuccess(double a, double b, double c, double d)
    {
        var value = Evaluate([a, b, c, d], [0, 0], 1);
        Assert.False(value.IsInside); Assert.Equal("invalid-envelope", value.Outcome); Assert.Null(value.ClosestBits);
    }

    [Fact]
    public void DegenerateSetEnvelopeCanContainItsPoint()
    {
        Assert.True(Evaluate([2, 3, 2, 3], [2, 3], 0).IsInside);
    }

    [Fact]
    public void AxisGrowthRejectAndFinalDistanceRejectAreDistinct()
    {
        Assert.Equal("axis-reject", Evaluate([0, 0, 1, 1], [-2, 0], 0.25).Outcome);
        var value = Evaluate([0, 0, 1, 1], [-0.2, -0.2], 0.25);
        Assert.Equal("distance-reject", value.Outcome); Assert.False(value.IsInside);
        Assert.NotNull(value.DistanceBits); Assert.Equal(Bits(0, 0), value.ClosestBits);
        Assert.Equal(Bits(-0.2, -0.2), value.DeltaBits);
    }

    [Fact]
    public void DistanceEqualToEpsilonIsAccepted()
    {
        var value = Evaluate([0, 0, 1, 1], [-0.25, 0.5], 0.25);
        Assert.True(value.IsInside); Assert.Equal("within-distance", value.Outcome);
        Assert.Equal(Bits(0.25)[0], value.DistanceBits);
    }

    [Fact]
    public void NativeMetricAxisGrowthAndDistanceBothMatter()
    {
        var yes = Evaluate([0, 0, 1, 1], [-0.5, 0.5], 1, [4, 0, 0, 1]);
        Assert.True(yes.IsInside); Assert.Equal(Bits(1)[0], yes.DistanceBits);
        Assert.Equal(Bits(2.5 + 1e-9, 1 + 1e-9), yes.GrowthBits);
        Assert.False(Evaluate([0, 0, 1, 1], [-0.5, 0.5], 0.99, [4, 0, 0, 1]).IsInside);
    }

    [Fact]
    public void NativeDistanceScalesAbsoluteComponentsEvenWithMetricCrossTerms()
    {
        var value = Evaluate([0, 0, 0, 0], [-1, 1], 2, [1, 0.5, 0.5, 1]);
        Assert.Equal(Bits(Math.Sqrt(3))[0], value.DistanceBits); Assert.True(value.IsInside);
        // A signed quadratic form would give sqrt1 instead; that is not this native helper.
    }

    [Fact]
    public void BothOrderedCrossTermsAreRetainedForNonsymmetricSuppliedMetric()
    {
        var value = Evaluate([0, 0, 0, 0], [-1, -1], 2, [1, 0.5, 0.25, 1]);
        Assert.Equal(Bits(Math.Sqrt(2.75))[0], value.DistanceBits);
    }

    [Fact]
    public void MaxComponentScalingAvoidsNaiveSquareOverflow()
    {
        var value = Evaluate([0, 0, 0, 0], [1e200, 1e200], 2e200);
        Assert.Equal(Bits(Math.Sqrt(2) * 1e200)[0], value.DistanceBits); Assert.True(value.IsInside);
    }

    [Fact]
    public void TinyNegativeQuadraticUsesNativeZeroClamp()
    {
        var value = Evaluate([0, 0, 0, 0], [-1, 0], 2, [-5e-10, 0, 0, 1]);
        Assert.True(value.IsInside); Assert.Equal(0L, value.DistanceBits);
    }

    [Theory]
    [InlineData("corners")] [InlineData("point")] [InlineData("matrix")]
    [InlineData("nonfinite-corners")] [InlineData("nonfinite-point")] [InlineData("nonfinite-eps")]
    [InlineData("nonfinite-metric")] [InlineData("math-problem")]
    public void UnsupportedInputsAndUnclosedMathWarningPolicyFailExplicitly(string change)
    {
        long[] corners = Bits(0, 0, 0, 0), p = Bits(-1, 0), m = Bits(1, 0, 0, 1); long eps = Bits(4)[0];
        switch (change)
        {
            case "corners": corners = [0]; break; case "point": p = [0]; break; case "matrix": m = [0]; break;
            case "nonfinite-corners": corners = Bits(double.PositiveInfinity, 0, 0, 0); break;
            case "nonfinite-point": p = Bits(double.NaN, 0); break;
            case "nonfinite-eps": eps = Bits(double.PositiveInfinity)[0]; break;
            case "nonfinite-metric": m = Bits(double.NaN, 0, 0, 1); break;
            default: m = Bits(-2e-9, 0, 0, 1); break;
        }
        Assert.Null(EvaluateSourceEnvelopeContainment(corners, p, eps, m, out var failure)); Assert.NotNull(failure);
    }

    [Fact]
    public void AllOtherEnvelopeRejectsResolveNativeZeroParityWithoutCallingPolygon()
    {
        var result = Read(Graph()); Assert.True(result.Complete);
        Assert.All(result.Faces[0].Loops, l =>
        {
            Assert.True(l.ResolvedShouldBeCcw); Assert.Equal("all-other-loops-outside", l.Outcome);
            var check = Assert.Single(l.Checks); Assert.Equal(0, check.PointWithinState);
            Assert.Equal("rejected-envelope-outside", check.Outcome); Assert.False(check.EnvelopeResult!.IsInside);
        });
    }

    [Fact]
    public void AcceptedEnvelopeRemainsPendingNotInsideOrBoundary()
    {
        var graph = Replace(Graph(), 1, Envelope(0, 0, 20, 20));
        var result = Read(graph); Assert.False(result.Complete);
        Assert.True(result.Faces[0].Loops[0].ResolvedShouldBeCcw);
        var child = result.Faces[0].Loops[1]; Assert.Null(child.ResolvedShouldBeCcw);
        var check = Assert.Single(child.Checks); Assert.Equal("accepted-envelope-pending", check.Outcome);
        Assert.Null(check.PointWithinState); Assert.True(check.EnvelopeResult!.IsInside);
    }

    [Fact]
    public void ChecksAfterUnresolvedContainmentAreMarkedNotValidatedNativeExecutionTrace()
    {
        var graph = Replace(Graph(), 3, Envelope(0, 0, 20, 20), Owned(1255, "m_nextLoop", (6, (ushort)1254, 9u)));
        graph = graph with { Nodes = graph.Nodes.Concat([
            Node(6, 1254, 9, Owned(1255, "m_nextLoop", null), Weak(1255, "m_pFace", 3), Weak(1255, "m_next", 10),
                Weak(1255, "m_prev", 10), Scalar(1255, "m_open", 0), Envelope(30, 30, 34, 34)),
            Edge(7, 10, 9, [30, 30], [34, 30])]).ToArray() };
        var current = Read(graph).Faces[0].Loops[0]; Assert.Equal(2, current.Checks.Count);
        Assert.False(current.Checks[0].AfterUnresolvedCheck); Assert.Null(current.Checks[0].PointWithinState);
        Assert.True(current.Checks[1].AfterUnresolvedCheck); Assert.Equal(0, current.Checks[1].PointWithinState);
        Assert.Null(current.ResolvedShouldBeCcw);
    }

    [Fact]
    public void UnsetOtherEnvelopeIsNotConvertedToPointWithinOutside()
    {
        var result = Read(Replace(Graph(), 3, Envelope(14, 10, 10, 14)));
        var check = Assert.Single(result.Faces[0].Loops[0].Checks);
        Assert.Equal("unset-envelope-pending", check.Outcome); Assert.Null(check.PointWithinState);
        Assert.Null(check.EnvelopeResult); Assert.False(result.Complete);
    }

    [Theory]
    [InlineData("open", "skipped-open")] [InlineData("empty", "empty-outside")] [InlineData("no-face", "no-face-outside")]
    public void NativeOtherLoopEarlyPathsPrecedeEnvelopeReading(string mutation, string expected)
    {
        var graph = mutation switch
        {
            "open" => Replace(Graph(), 3, Scalar(1255, "m_open", 1)),
            "empty" => Replace(Graph(), 3, Weak(1255, "m_next", 6)),
            _ => Replace(Graph(), 3, Weak(1255, "m_pFace", 0))
        };
        graph = Replace(graph, 3, Envelope(14, 10, 10, 14));
        var current = Read(graph).Faces[0].Loops[0]; Assert.True(current.ResolvedShouldBeCcw);
        var check = Assert.Single(current.Checks); Assert.Equal(expected, check.Outcome); Assert.Null(check.EnvelopeBits);
        Assert.Equal(mutation == "open" ? (int?)null : 0, check.PointWithinState);
    }

    [Fact]
    public void SingleLoopKeepsExactEarlyDecisionWithoutOtherLoopChecks()
    {
        var value = Read(Replace(Graph(), 1, Owned(1255, "m_nextLoop", null)));
        var loop = Assert.Single(value.Faces[0].Loops); Assert.True(value.Complete);
        Assert.True(loop.ResolvedShouldBeCcw); Assert.Equal("single-loop-outer", loop.Outcome); Assert.Empty(loop.Checks);
    }

    [Fact]
    public void OtherLoopMetricUsesItsOwnFaceSurfaceNotTheSourceSampleFace()
    {
        var graph = Replace(Graph(), 3, Weak(1255, "m_pFace", 9));
        graph = Replace(graph, 4, Weak(1244, "m_pFace", 9, 0));
        graph = graph with { Nodes = graph.Nodes.Concat([
            Node(6, 1589, 9, Owned(1590, "m_pFirstLoop", (3, (ushort)1254, 6u)), Owned(1589, "m_pSurf", (7, (ushort)522, 10u))),
            Node(7, 522, 10, Vector("m_xVec", [2, 0, 0]), Vector("m_yVec", [0, 2, 0]))]).ToArray() };
        var check = Assert.Single(Read(graph).Faces[0].Loops[0].Checks);
        Assert.Equal(7, check.SurfaceMetric!.QueueIndex); Assert.Equal(Bits(4, 0, 0, 4), check.SurfaceMetric.MatrixBits);
    }

    [Theory]
    [InlineData("count")] [InlineData("width")] [InlineData("inline-value")]
    public void MalformedEnvelopePayloadFailsInsteadOfGuessingBounds(string change)
    {
        var f = Envelope(0, 0, 1, 1); var item = f.Items[0]; var corners = item.Fields[0];
        item = change switch
        {
            "count" => item with { Fields = [corners with { Count = 1 }] },
            "width" => item with { End = item.End + 1 }, _ => item with { Values = [7] }
        };
        Assert.Null(SelectOwnGElementLoopEnvelopeFirstPass(Replace(Graph(), 3, f with { Items = [item] }), 1, out var failure));
        Assert.StartsWith("loop-envelope-target:", failure);
    }

    private static SourceEnvelopeContainment Evaluate(double[] corners, double[] point, double eps, double[]? matrix = null)
    {
        var result = EvaluateSourceEnvelopeContainment(Bits(corners), Bits(point), Bits(eps)[0], matrix == null ? null : Bits(matrix), out var failure);
        Assert.Null(failure); return Assert.IsType<SourceEnvelopeContainment>(result);
    }
    private static OwnGElementFreshLoopEnvelopeFirstPass Read(OwnGElementGeometryGraph graph)
    {
        var result = SelectOwnGElementLoopEnvelopeFirstPass(graph, 1, out var failure); Assert.Null(failure);
        return Assert.IsType<OwnGElementFreshLoopEnvelopeFirstPass>(result);
    }
    private static long[] Bits(params double[] values) => values.Select(BitConverter.DoubleToInt64Bits).ToArray();
    private static OwnGElementGeometryGraph Graph() => new(1000, 0,
        [Node(-1, 2007, 1000), Node(0, 1589, 3, Owned(1590, "m_pFirstLoop", (1, (ushort)1254, 4u)), Owned(1589, "m_pSurf", (5, (ushort)522, 8u))),
         Node(1, 1254, 4, Owned(1255, "m_nextLoop", (3, (ushort)1254, 6u)), Weak(1255, "m_pFace", 3), Weak(1255, "m_next", 5), Weak(1255, "m_prev", 5), Scalar(1255, "m_open", 0), Envelope(0, 0, 4, 4)),
         Edge(2, 5, 4, [0, 0], [4, 0]),
         Node(3, 1254, 6, Owned(1255, "m_nextLoop", null), Weak(1255, "m_pFace", 3), Weak(1255, "m_next", 7), Weak(1255, "m_prev", 7), Scalar(1255, "m_open", 0), Envelope(10, 10, 14, 14)),
         Edge(4, 7, 6, [10, 10], [14, 10]), Node(5, 522, 8, Vector("m_xVec", [1, 0, 0]), Vector("m_yVec", [0, 1, 0]))], [], 10000);
    private static GElementGraphNode Node(int q, ushort cls, uint handle, params GElementGraphField[] fs) => new(q, cls, handle, 100, 5000, fs);
    private static GElementGraphNode Edge(int q, uint handle, uint next, double[] a, double[] b) => Node(q, 1243, handle,
        Weak(1244, "m_pFace", 3, 0), Weak(1244, "m_next", next, 0), Weak(1244, "m_prev", 0, 0), Scalar(1244, "m_flags", 7),
        new(1244, "m_firstAndLastEdgePnts", 2, 1000, 1064, [Point(a), Point(b)]), new(1244, "m_interiorEdgePnts", 0, 1000, 1004, []));
    private static GElementGraphItem Point(double[] p) => new(1000, 1032, [], null,
        [new(1248, "uv", 2, 1000, 1032, [new(1000, 1016, Bits(p), null, []), new(1016, 1032, Bits(p), null, [])])]);
    private static GElementGraphField Envelope(double a, double b, double c, double d) => new(1255, "m_Envelope", null, 100, 132,
        [new(100, 132, [], null, [new(524, "m_corners", 2, 100, 132, [new(100, 116, Bits(a, b), null, []), new(116, 132, Bits(c, d), null, [])])])]);
    private static GElementGraphField Vector(string name, double[] v) => new(522, name, 3, 100, 124,
        v.Select((x, i) => new GElementGraphItem(100 + i * 8, 108 + i * 8, Bits(x), null, [])).ToArray());
    private static GElementGraphField Weak(int cls, string name, params uint[] keys) => new(cls, name, keys.Length == 2 ? 2u : null, 100, 100 + keys.Length * 4,
        keys.Select((k, i) => new GElementGraphItem(100 + i * 4, 104 + i * 4, [k], null, [])).ToArray());
    private static GElementGraphField Owned(int cls, string name, (int Q, ushort Class, uint Handle)? target) => new(cls, name, null, 100, 106,
        [new(100, 106, [], target is { } t ? new(t.Handle, t.Class, t.Q, null, [], 100, 106) : new(0, null, null, null, [], 100, 104), [])]);
    private static GElementGraphField Scalar(int cls, string name, long value) => new(cls, name, null, 100, 101, [new(100, 101, [value], null, [])]);
    private static OwnGElementGeometryGraph Replace(OwnGElementGeometryGraph g, int q, params GElementGraphField[] fs) => g with
        { Nodes = g.Nodes.Select(n => n.QueueIndex != q ? n : n with { Fields = n.Fields.Select(f => fs.Where(x => x.Declaring == f.Declaring && x.Name == f.Name).DefaultIfEmpty(f).Single()).ToArray() }).ToArray() };
}
