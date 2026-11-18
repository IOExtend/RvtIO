using RvtIO.Geometry;
using RvtIO.Geometry.Meshing;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class AnalyticSolidMeasureTests
{
    private static List<FaceArea.Coedge> Rectangle(double width, double bottom, double top)
    {
        (double, double)[] points = [(0, bottom), (width, bottom), (width, top), (0, top)];
        return Enumerable.Range(0, 4).Select(i => new FaceArea.Coedge(
            [points[i], points[(i + 1) % 4]], 6, false)).ToList();
    }

    private static GRepParserV2.SurfaceRec Surface(GRepParserV2.SurfKind kind, double radius = 0) => new()
    {
        Kind = kind, O = [0, 0, 0], X = [1, 0, 0], Y = [0, 1, 0], Z = [0, 0, 1], R = radius, Orient = 1,
    };

    [Fact]
    public void PlaneIntegral_PreservesSignedVolumeAndSurfaceOrientation()
    {
        var surface = Surface(GRepParserV2.SurfKind.Plane);
        surface.O = [0, 0, 3];
        var edges = Rectangle(3, 0, 2);
        var measure = GRepParserV2.MeasureAnalyticFace(surface, [0, 0, 0], edges)!.Value;
        Assert.Equal(6, measure.Area, 12);
        Assert.Equal(6, measure.Volume, 12);

        surface.Orient = 0;
        var reversed = GRepParserV2.MeasureAnalyticFace(surface, [0, 0, 0], edges)!.Value;
        Assert.Equal(measure.Area, reversed.Area, 12);
        Assert.Equal(-measure.Volume, reversed.Volume, 12);
    }

    [Fact]
    public void CylinderIntegral_UsesTheSurfaceInsteadOfFacetedChordsAndIsTranslationInvariant()
    {
        var surface = Surface(GRepParserV2.SurfKind.Cylinder, 2);
        var edges = Rectangle(Math.PI, 0, 3);
        var measure = GRepParserV2.MeasureAnalyticFace(surface, [0, 0, 0], edges)!.Value;
        Assert.Equal(6 * Math.PI, measure.Area, 11);
        Assert.Equal(4 * Math.PI, measure.Volume, 11);

        surface.O = [10, -7, 9];
        var moved = GRepParserV2.MeasureAnalyticFace(surface, surface.O, edges)!.Value;
        Assert.Equal(measure.Area, moved.Area, 12);
        Assert.Equal(measure.Volume, moved.Volume, 12);
    }

    [Fact]
    public void ConeIntegral_UsesSlantDistanceAndHasZeroFluxAboutItsApex()
    {
        double angle = Math.Atan(0.2);
        var measure = GRepParserV2.MeasureAnalyticFace(Surface(GRepParserV2.SurfKind.Cone, angle),
            [0, 0, 0], Rectangle(Math.PI, 1, 3))!.Value;
        Assert.Equal(4 * Math.PI * Math.Sin(angle), measure.Area, 12);
        Assert.Equal(0, measure.Volume, 12);
    }

    [Fact]
    public void SurfaceIntegral_RejectsIncompleteOrUnsupportedInputs()
    {
        var edges = Rectangle(2, 0, 1);
        Assert.Null(GRepParserV2.MeasureAnalyticFace(Surface(GRepParserV2.SurfKind.Hermite), [0, 0, 0], edges));
        Assert.Null(GRepParserV2.MeasureAnalyticFace(Surface(GRepParserV2.SurfKind.Plane), [double.NaN, 0, 0], edges));
        Assert.Null(GRepParserV2.MeasureAnalyticFace(Surface(GRepParserV2.SurfKind.Plane), [0, 0, 0],
            [new FaceArea.Coedge([(0, 0)], 6, false)]));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void ShellMeasures_RequireAClosedShellAndEverySourceFace(bool complete, bool closed, bool expected)
    {
        var geometry = new GeometryData
        {
            Faces = Enumerable.Range(1, 4).Select(i => new GeoFace
            {
                SourceFace = (uint)i, GeometryNode = 7,
                OuterVertices = [[0, 0, i], [1, 0, i], [1, 1, i], [0, 1, i]],
            }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = closed, FaceIndices = [0, 1, 2, 3] }],
            MeshNodes = [new GeometryNode(7, 0,
                Enumerable.Range(1, complete ? 4 : 5).Select(i => new BrFace { Marker = i, SurfaceKind = SurfaceKind.Plane, Loops = [] }).ToList(), 1)
                { SourceMeasures = (12, 5) }],
        };

        var shell = Assert.Single(BrepMeasures.ShellsOfCurrent(geometry));
        Assert.Equal(expected, shell.SourceMeasured);
        Assert.Equal(expected ? 12 : 4, shell.Area, 12);
        if (expected)
        {
            Assert.Equal(5, shell.Volume, 12);
            Assert.Equal(12, BrepMeasures.SurfaceAreaCurrent(geometry), 12);
        }
    }

    [Fact]
    public void ClosedNodes_AreTheNodesWhoseEdgesJoinTheirOwnFacesOnly()
    {
        var nodeByFace = new Dictionary<uint, (int Node, int Style)>
        {
            [1] = (7, 0), [2] = (7, 0), [3] = (7, 0), [4] = (7, 0),      // closed: every edge joins two faces of node 7
            [11] = (9, 0), [12] = (9, 0), [13] = (-9, 0),                // an edge into a face of another node
            [21] = (12, 0), [22] = (12, 0),                              // an edge with a missing face
        };
        var edges = new (uint, uint)[]
        {
            (1, 2), (2, 3), (3, 4), (4, 1), (1, 3), (2, 4),
            (11, 12), (12, 13),
            (21, 22), (22, 0), (21, 0xFFFFFFFF),
        };

        var closed = GRepParserV2.ClosedNodes(edges, nodeByFace);

        Assert.Equal([7], closed.OrderBy(node => node));
    }
}
