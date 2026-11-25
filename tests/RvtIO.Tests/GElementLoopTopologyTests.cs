using RvtIO.Parsing;
using static RvtIO.Parsing.TfBlockDeserializer;
using Xunit;

namespace RvtIO.Tests;

public class GElementLoopTopologyTests
{
    [Fact]
    public void FaceLoopAndEdgeOrderUseLinksAndFaceSidesNotQueueOrTags()
    {
        var value = Read(Graph()); var face = Assert.Single(value.Faces);
        Assert.Equal(0, face.FaceQueueIndex); Assert.Equal(1, face.FirstLoopQueueIndex);
        Assert.Equal(new[] { 1, 4 }, face.Loops.Select(l => l.QueueIndex));
        var loop = face.Loops[0]; Assert.Equal(new[] { 2, 3 }, loop.Edges.Select(e => e.EdgeQueueIndex));
        Assert.Equal(new int?[] { 0, 1 }, loop.Edges.Select(e => e.FaceSide));
        Assert.Equal(new int?[] { 3, 1 }, loop.Edges.Select(e => e.NextQueueIndex));
        Assert.Equal("loop-sentinel", loop.EndOutcome); Assert.Empty(loop.ConstructorSkippedLoops);
        Assert.Equal((ushort)1257, face.Loops[1].ClassId); Assert.Single(face.Loops[1].Edges);
    }

    [Fact]
    public void BothRawUvSidesAndUnusedInteriorPayloadArePreservedWithoutReversalOrRounding()
    {
        var graph = Replace(Graph(), 2, Interior(2), Scalar(1244, "m_flags", 0));
        var edge = Read(graph).Faces[0].Loops[0].Edges[0]; Assert.Equal(0, edge.PointCount);
        Assert.Equal(2, edge.InteriorPoints.Count); Assert.Equal(2, edge.FirstAndLastPoints.Count);
        Assert.Equal(new long[] { long.MinValue, 0x7ff8000000000001 }, edge.FirstAndLastPoints[0].Side0Bits);
        Assert.Equal(new long[] { 33, -44 }, edge.FirstAndLastPoints[0].Side1Bits);
        Assert.Equal(1000, edge.FirstAndLastPoints[0].Start); Assert.Equal(1032, edge.FirstAndLastPoints[0].End);
        Assert.Equal(edge.FirstAndLastPoints[0].Side1Bits, edge.FirstAndLastPoints[1].Side1Bits);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 1)] [InlineData(3, 1)]
    [InlineData(4, 0)] [InlineData(6, 4)] [InlineData(7, 4)] [InlineData(130, 1)] [InlineData(255, 4)]
    public void NativePointCountUsesFlagBitsBeforeArrayLength(int flags, int count)
    {
        var edge = Read(Replace(Graph(), 2, Interior(2), Scalar(1244, "m_flags", flags))).Faces[0].Loops[0].Edges[0];
        Assert.Equal(count, edge.PointCount);
    }

    [Fact]
    public void NullInteriorArrayProducesTwoPointsWhenFlagsEnableEndpoints()
    {
        var edge = Read(Replace(Graph(), 2, new GElementGraphField(1244, "m_interiorEdgePnts", uint.MaxValue, 1000, 1004, [])))
            .Faces[0].Loops[0].Edges[0]; Assert.Equal(2, edge.PointCount);
    }

    [Fact]
    public void FirstMatchingFaceSideWinsWhenBothSidesPointToSameFace()
    {
        var edge = Read(Replace(Graph(), 2, Weak(1244, "m_pFace", 3, 3))).Faces[0].Loops[0].Edges[0];
        Assert.Equal(0, edge.FaceSide); Assert.Equal(3, edge.NextQueueIndex);
    }

    [Fact]
    public void NullLoopFaceCanMatchNullEdgeSideExactlyLikeNativePointerComparison()
    {
        var graph = Replace(Replace(Graph(), 1, Weak(1255, "m_pFace", 0)), 2, Weak(1244, "m_pFace", 0, 0));
        var edge = Read(graph).Faces[0].Loops[0].Edges[0]; Assert.Equal(0, edge.FaceSide);
    }

    [Fact]
    public void MissingMatchingFaceStopsWithNativeWarningOutcomeNotAnInventedSide()
    {
        var loop = Read(Replace(Graph(), 2, Weak(1244, "m_pFace", 0, 0))).Faces[0].Loops[0];
        var edge = Assert.Single(loop.Edges); Assert.Null(edge.FaceSide); Assert.Null(edge.NextQueueIndex);
        Assert.Equal("face-mismatch", loop.EndOutcome);
    }

    [Fact]
    public void IteratorIgnoresOpenFlagButGetOuterLoopsCandidateDoesNot()
    {
        var loops = Read(Replace(Replace(Graph(), 1, Scalar(1255, "m_open", 7)), 4, Scalar(1255, "m_open", 1))).Faces[0].Loops;
        Assert.All(loops, l => Assert.False(l.GetOuterLoopsCandidate)); Assert.Equal(2, loops[0].Edges.Count);
        Assert.Equal("loop-sentinel", loops[0].EndOutcome);
    }

    [Fact]
    public void ConstructorSkipsSelfEmptyLoopsAndKeepsOriginalLoopFace()
    {
        var graph = Replace(Replace(Graph(), 1, Weak(1255, "m_next", 4)), 4, Weak(1255, "m_pFace", 0));
        var first = Read(graph).Faces[0].Loops[0]; Assert.Equal(new[] { 1 }, first.ConstructorSkippedLoops);
        Assert.Equal(5, first.InitialEdgeQueueIndex); Assert.Equal(0, first.Edges[0].FaceSide);
        Assert.Equal(0, first.FaceQueueIndex); Assert.False(first.GetOuterLoopsCandidate);
    }

    [Fact]
    public void AllSelfEmptyLoopsEndWithoutEmittingSentinelAsAnEdge()
    {
        var graph = Replace(Replace(Graph(), 1, Weak(1255, "m_next", 4)), 4, Weak(1255, "m_next", 7));
        var first = Read(graph).Faces[0].Loops[0]; Assert.Equal(new[] { 1, 4 }, first.ConstructorSkippedLoops);
        Assert.Null(first.InitialEdgeQueueIndex); Assert.Empty(first.Edges); Assert.Equal("no-initial-edge", first.EndOutcome);
    }

    [Fact]
    public void NullFirstEdgeIsNotAnEmptySelfSentinelOrAutomaticallyExcludedCandidate()
    {
        var loop = Read(Replace(Graph(), 1, Weak(1255, "m_next", 0))).Faces[0].Loops[0];
        Assert.True(loop.GetOuterLoopsCandidate); Assert.Empty(loop.ConstructorSkippedLoops); Assert.Empty(loop.Edges);
    }

    [Fact]
    public void NullNextEdgeStopsInsteadOfGuessingClosingLink()
    {
        var loop = Read(Replace(Graph(), 2, Weak(1244, "m_next", 0, 0))).Faces[0].Loops[0];
        Assert.Single(loop.Edges); Assert.Equal("null-next", loop.EndOutcome);
    }

    [Fact]
    public void RepeatedEdgesReachNativeIncrementLimitWithoutDeduplication()
    {
        var loop = Read(Replace(Graph(), 2, Weak(1244, "m_next", 5, 0))).Faces[0].Loops[0];
        Assert.Equal(100001, loop.Edges.Count); Assert.All(loop.Edges, e => Assert.Equal(2, e.EdgeQueueIndex));
        Assert.Equal("increment-limit", loop.EndOutcome); Assert.Equal("advanced", loop.Edges[99999].Outcome);
        Assert.Same(loop.Edges[0].FirstAndLastPoints, loop.Edges[^1].FirstAndLastPoints);
        Assert.Same(loop.Edges[0].InteriorPoints, loop.Edges[^1].InteriorPoints);
    }

    [Fact]
    public void FacesWithNullFirstLoopAreRetained()
    {
        var face = Assert.Single(Read(Replace(Graph(), 0, Owned(1590, "m_pFirstLoop", null))).Faces);
        Assert.Null(face.FirstLoopQueueIndex); Assert.Empty(face.Loops);
    }

    [Fact]
    public void RepeatedInlineWeakFieldOrdinalsRemainDistinctByTheirRawSpans()
    {
        var nested = new GElementGraphField(1257, "m_chainsWithEnvelopes", 2, 1500, 1604,
            [new(1500, 1504, [], null, [Weak(1253, "m_pStartEdge", 5)]),
             new(1600, 1604, [], null, [new(1253, "m_pStartEdge", null, 1600, 1604, [new(1600, 1604, [8], null, [])])])]);
        var graph = Graph(); graph = graph with { Nodes = graph.Nodes.Select(n => n.QueueIndex != 4 ? n :
            n with { Fields = n.Fields.Append(nested).ToArray() }).ToArray() };
        Assert.Equal(2, Read(graph).Faces[0].Loops.Count);
    }

    [Fact]
    public void UnboundedFaceLoopCycleFailsExplicitly()
    {
        var graph = Replace(Graph(), 4, Owned(1255, "m_nextLoop", (1, (ushort)1254, 4u)));
        Assert.Null(SelectOwnGElementLoopTopology(graph, out var failure)); Assert.StartsWith("loop-topology-chain:", failure);
    }

    [Theory]
    [InlineData("missing-key")] [InlineData("external-document")] [InlineData("wrong-class")]
    [InlineData("owned-handle")] [InlineData("owned-hand")] [InlineData("byte-range")]
    [InlineData("weak-array-shape")] [InlineData("uv-shape")] [InlineData("uv-span")]
    [InlineData("interior-count")]
    public void MalformedOrUnsupportedTopologyFailsExplicitly(string mutation)
    {
        var graph = Graph();
        graph = mutation switch
        {
            "missing-key" => Replace(graph, 1, Weak(1255, "m_next", 999)),
            "external-document" => Replace(graph, 1, Weak(1255, "m_pFace", 1)),
            "wrong-class" => Replace(graph, 1, Weak(1255, "m_pFace", 5)),
            "owned-handle" => Replace(graph, 0, Owned(1590, "m_pFirstLoop", (1, (ushort)1254, 55u))),
            "owned-hand" => Replace(graph, 0, new GElementGraphField(1590, "m_pFirstLoop", null, 100, 108,
                [new(100, 108, [], new(1, 4, null, 8, [], 100, 108), [])])),
            "byte-range" => Replace(graph, 1, Scalar(1255, "m_open", 256)),
            "weak-array-shape" => Replace(graph, 2, Weak(1244, "m_pFace", 3)),
            "uv-shape" => Replace(graph, 2, Endpoints() with { Items = [Point() with { Fields = [] }, Point()] }),
            "uv-span" => Replace(graph, 2, Endpoints() with { Items = [Point() with { End = 1033 }, Point()] }),
            _ => Replace(graph, 2, Interior(1) with { Count = 2 })
        };
        Assert.Null(SelectOwnGElementLoopTopology(graph, out var failure)); Assert.NotNull(failure);
    }

    private static OwnGElementFreshLoopTopology Read(OwnGElementGeometryGraph graph)
    {
        var value = SelectOwnGElementLoopTopology(graph, out var failure); Assert.Null(failure);
        return Assert.IsType<OwnGElementFreshLoopTopology>(value);
    }

    [Fact]
    public void ReusesReadOnlyTopologyForEachGraphInstance()
    {
        var graph = Graph();
        var topology = Read(graph);
        Assert.Same(topology, Read(graph));
        var invalid = graph with { OwnerId = 0 };
        Assert.Null(SelectOwnGElementLoopTopology(invalid, out var firstFailure));
        Assert.Equal("archive-reference-graph", firstFailure);
        Assert.Null(SelectOwnGElementLoopTopology(invalid, out var repeatedFailure));
        Assert.Equal(firstFailure, repeatedFailure);
        Assert.Same(topology, Read(graph));
    }
    private static OwnGElementGeometryGraph Graph() => new(1000, 0,
        [Node(-1, 2007, 1000), Node(0, 1589, 3, Owned(1590, "m_pFirstLoop", (1, (ushort)1254, 4u))),
         Node(1, 1254, 4, Owned(1255, "m_nextLoop", (4, (ushort)1257, 7u)), Weak(1255, "m_pFace", 3),
            Weak(1255, "m_next", 5), Weak(1255, "m_prev", 6), Scalar(1255, "m_open", 0)),
         Edge(2, 5, [3, 0], [6, 0]), Edge(3, 6, [0, 3], [0, 4]),
         Node(4, 1257, 7, Owned(1255, "m_nextLoop", null), Weak(1255, "m_pFace", 3),
            Weak(1255, "m_next", 8), Weak(1255, "m_prev", 8), Scalar(1255, "m_open", 0)),
         Edge(5, 8, [3, 3], [7, 0])], [], 10000);
    private static GElementGraphNode Node(int q, ushort cls, uint handle, params GElementGraphField[] fields)
        => new(q, cls, handle, 100, 5000, fields);
    private static GElementGraphNode Edge(int q, uint handle, uint[] faces, uint[] next) => Node(q, 1243, handle,
        Weak(1244, "m_pFace", faces), Weak(1244, "m_next", next), Weak(1244, "m_prev", 0, 0),
        Scalar(1244, "m_flags", 7), Endpoints(), Interior(0));
    private static OwnGElementGeometryGraph Replace(OwnGElementGeometryGraph graph, int q, params GElementGraphField[] fields)
        => graph with { Nodes = graph.Nodes.Select(n => n.QueueIndex != q ? n : n with
            { Fields = n.Fields.Select(f => fields.SingleOrDefault(x => x.Declaring == f.Declaring && x.Name == f.Name) ?? f).ToArray() }).ToArray() };
    private static GElementGraphField Weak(int cls, string name, params uint[] keys)
        => new(cls, name, keys.Length == 2 ? 2u : null, 100, 100 + keys.Length * 4,
            keys.Select((k, i) => new GElementGraphItem(100 + i * 4, 104 + i * 4, [k], null, [])).ToArray());
    private static GElementGraphField Owned(int cls, string name, (int Q, ushort Class, uint Handle)? target)
        => new(cls, name, null, 100, 106, [new(100, 106, [],
            target is { } t ? new(t.Handle, t.Class, t.Q, null, [], 100, 106) : new(0, null, null, null, [], 100, 104), [])]);
    private static GElementGraphField Scalar(int cls, string name, long value)
        => new(cls, name, null, 100, 101, [new(100, 101, [value], null, [])]);
    private static GElementGraphItem Point() => new(1000, 1032, [], null,
        [new(1248, "uv", 2, 1000, 1032, [new(1000, 1016, [long.MinValue, 0x7ff8000000000001], null, []), new(1016, 1032, [33, -44], null, [])])]);
    private static GElementGraphField Endpoints() => new(1244, "m_firstAndLastEdgePnts", 2, 1000, 1064, [Point(), Point()]);
    private static GElementGraphField Interior(int count) => new(1244, "m_interiorEdgePnts", (uint)count, 1000, 1004 + count * 32,
        Enumerable.Range(0, count).Select(_ => Point()).ToArray());
}
