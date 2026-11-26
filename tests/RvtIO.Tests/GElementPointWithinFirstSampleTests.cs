using static RvtIO.Parsing.TfBlockDeserializer;
using Xunit;

namespace RvtIO.Tests;

public class GElementPointWithinFirstSampleTests
{
    [Theory]
    [InlineData(0, 6, 0, 1)] [InlineData(0, 7, 1, 0)]
    [InlineData(1, 6, 1, 0)] [InlineData(1, 7, 0, 1)]
    public void PolygonVerticesUseNativeFaceSideXorFlagOrder(int side, byte flags, int first, int last)
    {
        var loop = Loop(side, flags);
        var vertices = SelectSourceTwoPointPolygonVertices(loop, out var why);
        Assert.Null(why); Assert.NotNull(vertices); Assert.Equal(2, vertices.Count);
        Assert.Equal(first, vertices[0].PointOrdinal); Assert.Equal(last, vertices[1].PointOrdinal);
        Assert.Equal(side == 0 ? Bits(first, 2) : Bits(10 + first, 20), vertices[0].UvBits);
    }

    [Theory]
    [InlineData("open")] [InlineData("sentinel")] [InlineData("initial")]
    [InlineData("next")] [InlineData("count")] [InlineData("flags")]
    [InlineData("side")] [InlineData("interior")] [InlineData("uv-width")]
    public void UnqualifiedVertexTopologyFailsExplicitly(string change)
    {
        var loop = Loop(0, 6); var edge = loop.Edges[0];
        switch (change)
        {
            case "open": loop = loop with { Open = 1 }; break;
            case "sentinel": loop = loop with { EndOutcome = "null-next" }; break;
            case "initial": loop = loop with { InitialEdgeQueueIndex = 99 }; break;
            case "next": edge = edge with { NextQueueIndex = 99 }; break;
            case "count": edge = edge with { PointCount = 3 }; break;
            case "flags": edge = edge with { Flags = 2 }; break;
            case "side": edge = edge with { FaceSide = null }; break;
            case "interior": edge = edge with { InteriorPoints = [edge.FirstAndLastPoints[0]] }; break;
            default: edge = edge with { FirstAndLastPoints = [edge.FirstAndLastPoints[0] with { Side0Bits = [0] }, edge.FirstAndLastPoints[1]] }; break;
        }
        loop = loop with { Edges = [edge] };
        Assert.Null(SelectSourceTwoPointPolygonVertices(loop, out var why)); Assert.Equal("two-point-polygon-vertices", why);
    }

    [Fact]
    public void InsideStateOneResolvesOddNestingParityAndPreservesOuter()
    {
        var value = Read(Graph()); Assert.True(value.Complete);
        var loops = value.Faces[0].Loops;
        Assert.True(loops[0].ResolvedShouldBeCcw); Assert.False(loops[1].ResolvedShouldBeCcw);
        Assert.Equal(1, loops[1].ContainmentCount);
        var check = Assert.Single(loops[1].Checks); Assert.Equal(1, check.PointWithinState);
        Assert.Equal("polygon-inside", check.Outcome); Assert.False(check.AfterUnresolvedCheck);
        Assert.Equal(8, check.Vertices!.Count); Assert.True(check.Polygon!.IsInside);
    }

    [Fact]
    public void PolygonFalseStaysPendingRuntimeFallbackNotOutsideZero()
    {
        var value = Read(Graph(triangle: true)); Assert.False(value.Complete);
        var loop = value.Faces[0].Loops[1]; var check = Assert.Single(loop.Checks);
        Assert.Null(loop.ResolvedShouldBeCcw); Assert.Null(loop.ContainmentCount);
        Assert.Equal("polygon-outside-runtime-fallback-pending", check.Outcome);
        Assert.Null(check.PointWithinState); Assert.False(check.Polygon!.IsInside);
    }

    [Fact]
    public void BoundaryTwoNeedsRetryAndDoesNotBecomeOddContainmentCount()
    {
        var graph = Graph(); graph = Replace(graph, 4, Endpoints([8, 0], [12, 0]));
        var value = Read(graph); Assert.False(value.Complete);
        var loop = value.Faces[0].Loops[1]; var check = Assert.Single(loop.Checks);
        Assert.Equal(2, check.PointWithinState); Assert.Null(loop.ResolvedShouldBeCcw); Assert.Null(loop.ContainmentCount);
        Assert.Null(check.Polygon); Assert.Null(check.Vertices);
    }

    [Fact]
    public void NonemptyDerivedChainRemainsUnsupportedNotSimplePolygon()
    {
        var graph = Graph(); graph = graph with { Nodes = graph.Nodes.Select(n => n.QueueIndex != 1 ? n : n with
        { ClassId = 1257, Fields = n.Fields.Append(new GElementGraphField(1257, "m_chainsWithEnvelopes", 1, 100, 104,
            [new(100, 104, [], null, [])])).ToArray() }).ToArray() };
        graph = Replace(graph, 0, Owned(1590, "m_pFirstLoop", (1, (ushort)1257, 4u)));
        var check = Assert.Single(Read(graph).Faces[0].Loops[1].Checks);
        Assert.Equal("unsupported-projection-topology", check.Outcome); Assert.Null(check.Polygon); Assert.Null(check.PointWithinState);
    }

    [Fact]
    public void InvalidCurrentEpsContextFailsNotCompletedGraph()
    {
        Assert.Null(SelectOwnGElementPointWithinFirstSample(Graph(), 2, out var why));
        Assert.Equal("tess-eps-current-version", why);
    }

    [Fact]
    public void LaterInsideCandidateAfterUnknownCannotResolveWholeParity()
    {
        var graph = Graph();
        graph = Replace(graph, 0, Owned(1590, "m_pFirstLoop", (9, (ushort)1254, 12u)));
        graph = graph with { Nodes = graph.Nodes.Concat([
            Node(9, 1254, 12, Owned(1255, "m_nextLoop", (1, (ushort)1254, 4u)), Weak(1255, "m_pFace", 3),
                Weak(1255, "m_next", 13), Weak(1255, "m_prev", 13), Scalar(1255, "m_open", 0), Envelope(0, 0, 20, 20)),
            Edge(10, 13, 12, [6, 6], [8, 6])]).ToArray() };
        var loop = Read(graph).Faces[0].Loops.Single(l => l.LoopQueueIndex == 3);
        Assert.Null(loop.ResolvedShouldBeCcw); Assert.Null(loop.ContainmentCount);
        Assert.Null(loop.Checks[0].PointWithinState); Assert.False(loop.Checks[0].AfterUnresolvedCheck);
        Assert.Equal(1, loop.Checks[1].PointWithinState); Assert.True(loop.Checks[1].AfterUnresolvedCheck);
    }

    [Fact]
    public void TwoKnownContainingLoopsResolveEvenParity()
    {
        var graph = Graph();
        graph = Replace(graph, 0, Owned(1590, "m_pFirstLoop", (9, (ushort)1254, 12u)));
        double[][] points = [[-1, -1], [21, -1], [21, 21], [-1, 21]];
        graph = graph with { Nodes = graph.Nodes.Concat([
            Node(9, 1254, 12, Owned(1255, "m_nextLoop", (1, (ushort)1254, 4u)), Weak(1255, "m_pFace", 3),
                Weak(1255, "m_next", 13), Weak(1255, "m_prev", 16), Scalar(1255, "m_open", 0), Envelope(-1, -1, 21, 21))
        ]).Concat(points.Select((p, i) => Edge(10 + i, (uint)(13 + i), i == 3 ? 12u : (uint)(14 + i), p, points[(i + 1) % 4]))).ToArray() };
        var loop = Read(graph).Faces[0].Loops.Single(l => l.LoopQueueIndex == 3);
        Assert.True(loop.ResolvedShouldBeCcw); Assert.Equal(2, loop.ContainmentCount);
        Assert.All(loop.Checks, c => { Assert.Equal(1, c.PointWithinState); Assert.False(c.AfterUnresolvedCheck); });
    }

    [Fact]
    public void EmptyDerivedChainCanUseTheQualifiedSimplePath()
    {
        var graph = Graph(); graph = graph with { Nodes = graph.Nodes.Select(n => n.QueueIndex != 1 ? n : n with
        { ClassId = 1257, Fields = n.Fields.Append(new GElementGraphField(1257, "m_chainsWithEnvelopes", 0, 100, 104, [])).ToArray() }).ToArray() };
        graph = Replace(graph, 0, Owned(1590, "m_pFirstLoop", (1, (ushort)1257, 4u)));
        var loop = Read(graph).Faces[0].Loops[1];
        Assert.False(loop.ResolvedShouldBeCcw); Assert.Equal(1, Assert.Single(loop.Checks).PointWithinState);
    }

    static GElementLoopTopology Loop(int side, byte flags) => new(1, 1254, 0, null, 2, 2, 0, true, [], 2,
        [new(2, side, flags, 2, [new(100, 132, Bits(0, 2), Bits(10, 20)), new(132, 164, Bits(1, 2), Bits(11, 20))], [], 1, "loop-sentinel")], "loop-sentinel");
    static OwnGElementFreshPointWithinFirstSample Read(OwnGElementGeometryGraph graph)
    {
        var value = SelectOwnGElementPointWithinFirstSample(graph, 1, out var why);
        Assert.Null(why); Assert.NotNull(value); return value;
    }
    internal static OwnGElementGeometryGraph Graph(bool triangle = false)
    {
        double[][] points = triangle ? [[0, 0], [20, 0], [0, 20]] : [[0, 0], [20, 0], [20, 20], [0, 20]];
        var edges = points.Select((p, i) => Edge(i == 0 ? 2 : 5 + i, i == 0 ? 5u : (uint)(8 + i),
            i + 1 == points.Length ? 4u : (uint)(9 + i), p, points[(i + 1) % points.Length])).ToArray();
        return new(1000, 0, new[] {
            Node(-1, 2007, 1000), Node(0, 1589, 3, Owned(1590, "m_pFirstLoop", (1, (ushort)1254, 4u)), Owned(1589, "m_pSurf", (5, (ushort)522, 8u))),
            Node(1, 1254, 4, Owned(1255, "m_nextLoop", (3, (ushort)1254, 6u)), Weak(1255, "m_pFace", 3), Weak(1255, "m_next", 5), Weak(1255, "m_prev", (uint)(7 + points.Length)), Scalar(1255, "m_open", 0), Envelope(0, 0, 20, 20)),
            Node(3, 1254, 6, Owned(1255, "m_nextLoop", null), Weak(1255, "m_pFace", 3), Weak(1255, "m_next", 7), Weak(1255, "m_prev", 7), Scalar(1255, "m_open", 0), Envelope(8, 8, 18, 18)),
            Edge(4, 7, 6, triangle ? [14, 15] : [8, 10], triangle ? [16, 15] : [12, 10]),
            Node(5, 522, 8, Vector("m_origin", [0, 0, 0]), Vector("m_xVec", [1, 0, 0]), Vector("m_yVec", [0, 1, 0]))
        }.Concat(edges).ToArray(), [], 10000);
    }
    internal static GElementGraphNode Edge(int q, uint h, uint next, double[] a, double[] b) => Node(q, 1243, h,
        Weak(1244, "m_pFace", 3, 0), Weak(1244, "m_next", next, 0), Weak(1244, "m_prev", 0, 0), Scalar(1244, "m_flags", 6),
        Endpoints(a, b), new(1244, "m_interiorEdgePnts", 0, 100, 104, []));
    static GElementGraphNode Node(int q, ushort c, uint h, params GElementGraphField[] f) => new(q, c, h, 100, 5000, f);
    static GElementGraphField Endpoints(double[] a, double[] b) => new(1244, "m_firstAndLastEdgePnts", 2, 100, 164, [Point(a), Point(b)]);
    static GElementGraphItem Point(double[] p) => new(100, 132, [], null, [new(1248, "uv", 2, 100, 132,
        [new(100, 116, Bits(p), null, []), new(116, 132, Bits(p), null, [])])]);
    static GElementGraphField Envelope(double a, double b, double c, double d) => new(1255, "m_Envelope", null, 100, 132,
        [new(100, 132, [], null, [new(524, "m_corners", 2, 100, 132, [new(100, 116, Bits(a, b), null, []), new(116, 132, Bits(c, d), null, [])])])]);
    static GElementGraphField Vector(string name, double[] values) => new(522, name, 3, 100, 124,
        values.Select((v, i) => new GElementGraphItem(100 + i * 8, 108 + i * 8, Bits(v), null, [])).ToArray());
    internal static GElementGraphField Weak(int c, string name, params uint[] keys) => new(c, name, keys.Length == 2 ? 2u : null, 100, 100 + 4 * keys.Length,
        keys.Select((k, i) => new GElementGraphItem(100 + 4 * i, 104 + 4 * i, [k], null, [])).ToArray());
    internal static GElementGraphField Owned(int c, string name, (int Q, ushort C, uint H)? t) => new(c, name, null, 100, 106,
        [new(100, 106, [], t is { } v ? new(v.H, v.C, v.Q, null, [], 100, 106) : new(0, null, null, null, [], 100, 104), [])]);
    static GElementGraphField Scalar(int c, string name, long v) => new(c, name, null, 100, 101, [new(100, 101, [v], null, [])]);
    static long[] Bits(params double[] values) => values.Select(BitConverter.DoubleToInt64Bits).ToArray();
    internal static OwnGElementGeometryGraph Replace(OwnGElementGeometryGraph g, int q, params GElementGraphField[] fields) => g with
    { Nodes = g.Nodes.Select(n => n.QueueIndex != q ? n : n with { Fields = n.Fields.Select(f => fields.Where(x => x.Declaring == f.Declaring && x.Name == f.Name).DefaultIfEmpty(f).Single()).ToArray() }).ToArray() };
}
