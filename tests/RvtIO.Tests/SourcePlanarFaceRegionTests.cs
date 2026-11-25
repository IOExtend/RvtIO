using RvtIO.Parsing;
using static RvtIO.Parsing.TfBlockDeserializer;
using F = RvtIO.Tests.GElementPointWithinFirstSampleTests;
using Xunit;

namespace RvtIO.Tests;

public class SourcePlanarFaceRegionTests
{
    [Fact]
    public void FirstSourceLoopSurvivesDescendingAreaClassification()
    {
        var graph = Graph();
        double[][] small = [[0,0],[1,0],[1,1],[0,1]];
        int[] indices = [2,6,7,8];
        for (int i = 0; i < 4; i++)
        {
            var field = F.Edge(0, 0, 0, small[i], small[(i + 1) % 4]).Fields
                .Single(f => f.Name == "m_firstAndLastEdgePnts");
            graph = F.Replace(graph, indices[i], field);
        }
        graph = F.Replace(graph, 1, new GElementGraphField(1255, "m_Envelope", null, 100, 132,
            [new(100, 132, [], null, [new(524, "m_corners", 2, 100, 132,
                [new(100,116,[0,0],null,[]), new(116,132,
                    [BitConverter.DoubleToInt64Bits(1),BitConverter.DoubleToInt64Bits(1)],null,[])])])]));
        var regions = GRepParserV2.SelectOwnPlanarFaceRegions(graph, out var why);
        Assert.Null(why); Assert.NotNull(regions);
        Assert.Equal(2, regions[0].Count);
        Assert.Equal(12, regions[0][0].Vertices!.Max(p => p[0]));
        Assert.Equal(1, regions[0][1].Vertices!.Max(p => p[0]));
        Assert.Equal(regions[0][1].Vertices, regions[0][0].FirstSourceLoop);
        Assert.Equal(regions[0][1].Vertices, regions[0][1].FirstSourceLoop);
    }
    [Fact]
    public void OwnSentinelPartitionPreservesAnOuterAndItsHole()
    {
        var regions = GRepParserV2.SelectOwnPlanarFaceRegions(Graph(), out var why);
        Assert.Null(why); Assert.NotNull(regions);
        var face = Assert.Single(regions[0]);
        Assert.Equal(4, face.Vertices!.Count);
        Assert.Equal(4, Assert.Single(face.InnerVertices!).Count);
        Assert.Equal(new double[] { 0, 0, 0 }, face.Vertices[0]);
    }

    [Fact]
    public void SingleOwnLoopAgreesWithBothEpsBranches()
    {
        var graph = F.Replace(Graph(), 1, F.Owned(1255, "m_nextLoop", null));
        var winding = SelectOwnGElementEpsIndependentSamples(graph, out var windingFailure);
        Assert.Null(windingFailure);
        Assert.True(Assert.Single(winding!.Loops).IndependentShouldBeCcw);

        var regions = GRepParserV2.SelectOwnPlanarFaceRegions(graph, out var failure);

        Assert.Null(failure);
        var face = Assert.Single(regions![0]);
        Assert.Null(face.InnerVertices);
        Assert.Equal(4, face.Vertices!.Count);
        Assert.Equal(face.Vertices, Assert.Single(face.SourceLoops!));
        Assert.NotSame(face.Vertices[0], face.SourceLoops![0][0]);
    }

    [Theory]
    [InlineData("missing-surface")] [InlineData("curved-surface")]
    [InlineData("open")] [InlineData("wrong-sentinel")] [InlineData("zero-world-area")]
    [InlineData("duplicate-surface")] [InlineData("nonfinite-plane")] [InlineData("loop-owner")]
    public void UnsupportedOwnGeometryDoesNotLeavePartialRegions(string mode)
    {
        var graph = Graph();
        if (mode == "missing-surface") graph = graph with { FaceSurfaces = [] };
        if (mode == "duplicate-surface") graph = graph with { FaceSurfaces = [new(0, 5), new(0, 5)] };
        if (mode == "curved-surface") graph = graph with { Nodes = graph.Nodes.Select(n =>
            n.QueueIndex == 5 ? n with { ClassId = 983 } : n).ToArray() };
        if (mode == "open") graph = F.Replace(graph, 1, Scalar(1255, "m_open", 1));
        if (mode == "wrong-sentinel") graph = F.Replace(graph, 11, F.Weak(1244, "m_next", 4, 0));
        if (mode == "loop-owner") graph = F.Replace(graph, 1, F.Weak(1255, "m_pFace", 0));
        if (mode == "nonfinite-plane") graph = graph with { Nodes = graph.Nodes.Select(n =>
            n.QueueIndex == 5 ? n with { Fields = n.Fields.Select(f => f.Name == "m_origin"
                ? f with { Items = f.Items.Select(i => i with { Values = [BitConverter.DoubleToInt64Bits(double.NaN)] }).ToArray() }
                : f).ToArray() } : n).ToArray() };
        if (mode == "zero-world-area") graph = graph with { Nodes = graph.Nodes.Select(n =>
            n.QueueIndex == 5 ? n with { Fields = n.Fields.Select(f => f.Name == "m_xVec" || f.Name == "m_yVec"
                ? f with { Items = f.Items.Select(i => i with { Values = [0] }).ToArray() } : f).ToArray() } : n).ToArray() };
        Assert.Null(GRepParserV2.SelectOwnPlanarFaceRegions(graph, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void PlaneEvaluationRetainsNonUnitBasisWithoutNormalization()
    {
        var graph = Graph();
        graph = graph with { Nodes = graph.Nodes.Select(n => n.QueueIndex == 5 ? n with
        { Fields = n.Fields.Select(f => f.Name == "m_xVec" ? f with
            { Items = f.Items.Select((i,k) => k == 0 ? i with { Values = [BitConverter.DoubleToInt64Bits(2)] } : i).ToArray() } : f).ToArray() } : n).ToArray() };
        var regions = GRepParserV2.SelectOwnPlanarFaceRegions(graph, out var why);
        Assert.Null(why); Assert.NotNull(regions);
        Assert.Equal(40, Assert.Single(regions[0]).Vertices!.Max(p => p[0]));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void DerivedChainPartitionDoesNotChangeTheBaseBoundary(int chainCount)
    {
        var graph = Graph();
        var loop = graph.Nodes.Single(n => n.QueueIndex == 1);
        var envelope = loop.Fields.Single(f => f.Name == "m_Envelope") with
            { Declaring = 1253, Name = "m_envelope" };
        var chains = new GElementGraphField(1257, "m_chainsWithEnvelopes", (uint)chainCount, 1500, 1700,
            Enumerable.Range(0, chainCount).Select(i => new GElementGraphItem(1500 + i * 100, 1600 + i * 100,
                [], null, [new GElementGraphField(1253, "m_pStartEdge", null, 1500 + i * 100, 1504 + i * 100,
                    [new(1500 + i * 100, 1504 + i * 100, [i == 0 ? 5u : 10u], null, [])]), envelope])).ToArray());
        graph = graph with { Nodes = graph.Nodes.Select(n => n.QueueIndex == 1
            ? n with { ClassId = 1257, Fields = n.Fields.Append(chains).ToArray() } : n).ToArray() };
        graph = F.Replace(graph, 0, F.Owned(1590, "m_pFirstLoop", (1, (ushort)1257, 4u)));
        foreach (var (q, previous) in new[] { (2, 4u), (6, 5u), (7, 9u), (8, 10u) })
            graph = F.Replace(graph, q, F.Weak(1244, "m_prev", previous, 0));
        var regions = GRepParserV2.SelectOwnPlanarFaceRegions(graph, out var why);
        Assert.Null(why); Assert.NotNull(regions);
        var expected = GRepParserV2.SelectOwnPlanarFaceRegions(Graph(), out _)!;
        var face = Assert.Single(regions[0]);
        Assert.Equal(expected[0][0].Vertices, face.Vertices);
        Assert.Equal(expected[0][0].InnerVertices![0], face.InnerVertices![0]);
    }

    private static OwnGElementGeometryGraph Graph()
    {
        var graph = F.Graph() with { FaceSurfaces = [new(0, 5)] };
        graph = F.Replace(graph, 3, F.Weak(1255, "m_prev", 14));
        graph = F.Replace(graph, 4, F.Weak(1244, "m_next", 12, 0));
        return graph with { Nodes = graph.Nodes.Concat([
            F.Edge(9, 12, 13, [12, 10], [12, 12]),
            F.Edge(10, 13, 14, [12, 12], [8, 12]),
            F.Edge(11, 14, 6, [8, 12], [8, 10])]).ToArray() };
    }

    private static GElementGraphField Scalar(int cls, string name, long value) =>
        new(cls, name, null, 100, 101, [new(100, 101, [value], null, [])]);
}
