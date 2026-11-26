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
    public void HermiteFacets_MeasureAreaAndSignedFluxOfFullKnotDomain()
    {
        var surface = Surface(GRepParserV2.SurfKind.Hermite);
        surface.Env = [0, 0, 1, 1];
        surface.UParams = [0, 1];
        surface.VParams = [0, 1];
        surface.HermiteNodes =
        [
            Node(0, 0), Node(3, 0), Node(0, 2), Node(3, 2),
        ];
        var edges = Rectangle(1, 0, 1);
        var measure = GRepParserV2.MeasureHermiteFace(surface, [0, 0, 0], edges)!.Value;
        // The facet vertices are rounded to float before accumulating the quantities.
        Assert.Equal(6, measure.Area, tolerance: 1e-6);
        Assert.Equal(measure.Area, measure.Volume, 12);
        Assert.Null(GRepParserV2.MeasureHermiteFace(surface, [0, 0, 0], Rectangle(0.5, 0, 1)));

        static GRepParserV2.HermiteSurfaceNode Node(double x, double y) => new()
        {
            Point = [x, y, 3], Du = [3, 0, 0], Dv = [0, 2, 0],
        };
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
    public void RevolvedArcIntegral_MeasuresHalfATorus()
    {
        var surface = Surface(GRepParserV2.SurfKind.Rev);
        surface.Env = [0, 0, Math.PI, 2 * Math.PI];
        var profile = new GRepParserV2.ArcRec { C = [3, 0, 0], X = [1, 0, 0], Y = [0, 0, 1], R = 1 };
        var edges = Rectangle(Math.PI, 0, 2 * Math.PI);
        var measure = GRepParserV2.MeasureRevolvedFace(surface, profile, [0, 0, 0], edges)!.Value;
        Assert.InRange(measure.Area / (6 * Math.PI * Math.PI), 1 - 1e-6, 1 + 1e-6);
        Assert.InRange(measure.Volume / (3 * Math.PI * Math.PI), 1 - 1e-6, 1 + 1e-6);
        Assert.Null(GRepParserV2.MeasureRevolvedFace(surface, profile with { C = [0, 0, 0] }, [0, 0, 0], edges));
    }

    [Fact]
    public void RevolvedLineIntegral_UsesTheRadiusAtTheTrimmedEndpoints()
    {
        var surface = Surface(GRepParserV2.SurfKind.Rev);
        surface.Env = [0, 1, Math.PI, 3];
        var profile = new GRepParserV2.ArcRec { C = [0, 0, 0], X = [0.6, 0, 0.8] };
        var measure = GRepParserV2.MeasureRevolvedFace(surface, profile, [0, 0, 0], Rectangle(Math.PI, 1, 3))!.Value;
        Assert.Equal(2.4 * Math.PI, measure.Area, 10);
        Assert.Equal(0, measure.Volume, 12);
    }

    [Fact]
    public void RevolvedArcIntegral_AllowsTrimEndpointsOnTheAxis()
    {
        var surface = Surface(GRepParserV2.SurfKind.Rev);
        surface.Env = [0, 0, 2 * Math.PI, Math.PI];
        var profile = new GRepParserV2.ArcRec { X = [0, 0, -1], Y = [1, 0, 0], R = 1 };
        var measure = GRepParserV2.MeasureRevolvedFace(surface, profile, [0, 0, 0], Rectangle(2 * Math.PI, 0, Math.PI))!.Value;
        Assert.InRange(measure.Area / (4 * Math.PI), 1 - 1e-6, 1 + 1e-6);
        Assert.InRange(measure.Volume / (4 * Math.PI / 3), 1 - 1e-6, 1 + 1e-6);
    }

    [Fact]
    public void RevolvedEllipseIntegral_MeasuresAnOblateSpheroid()
    {
        var surface = Surface(GRepParserV2.SurfKind.Rev);
        surface.Env = [0, 0, 2 * Math.PI, Math.PI];
        var profile = new GRepParserV2.ArcRec { X = [0, 0, -1], Y = [1, 0, 0], R = 1, RY = 2 };
        var measure = GRepParserV2.MeasureRevolvedFace(surface, profile, [0, 0, 0], Rectangle(2 * Math.PI, 0, Math.PI))!.Value;
        double eccentricity = Math.Sqrt(0.75);
        double area = 8 * Math.PI * (1 + 0.25 / eccentricity * Math.Atanh(eccentricity));

        Assert.InRange(measure.Area / area, 1 - 1e-6, 1 + 1e-6);
        Assert.InRange(measure.Volume / (16 * Math.PI / 3), 1 - 1e-6, 1 + 1e-6);
    }

    [Fact]
    public void RevolvedHermiteIntegral_UsesTheStoredProfileTangents()
    {
        var surface = Surface(GRepParserV2.SurfKind.Rev);
        surface.Env = [0, 0, Math.PI, 1];
        var profile = new GRepParserV2.ArcRec
        {
            HermiteNodes =
            [
                new() { T = 0, Point = [1, 0, 0], Tangent = [1, 0, 3] },
                new() { T = 1, Point = [2, 0, 3], Tangent = [1, 0, 3] },
            ],
        };
        var measure = GRepParserV2.MeasureRevolvedFace(surface, profile, [0, 0, 0], Rectangle(Math.PI, 0, 1))!.Value;

        Assert.Equal(1.5 * Math.PI * Math.Sqrt(10), measure.Area, 11);
        Assert.Equal(1.5 * Math.PI, measure.Volume, 11);
        profile.HermiteNodes[0].Tangent[0] = -4;
        Assert.Null(GRepParserV2.MeasureRevolvedFace(surface, profile, [0, 0, 0], Rectangle(Math.PI, 0, 1)));
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, 1)]
    public void RuledApexIntegral_DoesNotRequireAnEdgeAtTheCollapsedEnd(bool apexAtEnd, double volume)
    {
        var profile = new GRepParserV2.ArcRec { C = [0, 0, 3], X = [1, 0, 0], T1 = 2 };
        var edges = Rectangle(1, 0, 1);
        edges.RemoveAt(apexAtEnd ? 2 : 0);
        var face = GRepParserV2.MeasureRuledApex(profile, new Pt3(0, 1, 3), apexAtEnd, default, edges)!.Value;
        Assert.Equal(1, face.Area, 12);
        Assert.Equal(volume, face.Volume, 12);
    }

    [Fact]
    public void RawRuledLoopRestoresOnlyTheImplicitEdgeAtThePole()
    {
        var pole = Rectangle(1, 0, 1);
        pole.RemoveAt(2);
        Assert.True(GRepParserV2.CloseRuledEndPole(pole, 0));
        Assert.Equal(new[] { (1d, 1d), (0d, 1d) }, pole[^1].Points);

        var open = Rectangle(1, 0, 0.75);
        open.RemoveAt(2);
        Assert.False(GRepParserV2.CloseRuledEndPole(open, 0));
    }

    [Fact]
    public void RuledArcPairIntegral_MeasuresAConicalFrustumSide()
    {
        var bottom = new GRepParserV2.ArcRec { X = [1, 0, 0], Y = [0, 1, 0], R = 1, T1 = Math.PI };
        var top = bottom with { C = [0, 0, 3], R = 2 };
        var edges = Rectangle(1, 0, 1);
        var measure = GRepParserV2.MeasureRuledProfiles(bottom, top, default, edges)!.Value;

        Assert.Equal(1.5 * Math.PI * Math.Sqrt(10), measure.Area, 11);
        Assert.Equal(1.5 * Math.PI, measure.Volume, 11);
        Assert.Null(GRepParserV2.MeasureRuledProfiles(bottom, top, default, Rectangle(1, 0, 0.9)));
        Assert.Null(GRepParserV2.MeasureRuledProfiles(bottom, top with { T0 = 0.1 }, default, edges));
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
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public void ShellMeasures_RequireACompleteClosedNonCuttingNode(bool complete, bool closed, bool cutting, bool expected)
    {
        var geometry = new GeometryData
        {
            Faces = Enumerable.Range(1, 4).Select(i => new GeoFace
            {
                SourceFace = (uint)i, GeometryNode = 7, QuadratureArea = 2,
                OuterVertices = [[0, 0, i], [1, 0, i], [1, 1, i], [0, 1, i]],
            }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = closed, FaceIndices = [0, 1, 2, 3] }],
            NodeMeasures = cutting ? [new MeshNodeMeasure(7, 12, -5)] : null,
            MeshNodes = [new GeometryNode(7, 0,
                Enumerable.Range(1, complete ? 4 : 5).Select(i => new BrFace { Marker = i, SurfaceKind = SurfaceKind.Plane, Loops = [] }).ToList(), 1)
                { SourceMeasures = (12, 5) }],
        };

        var shell = Assert.Single(BrepMeasures.ShellsOfCurrent(geometry));
        Assert.Equal(expected, shell.SourceMeasured);
        Assert.Equal(expected ? 12 : 4, shell.Area, 12);
        Assert.Equal(expected ? 12 : 8, BrepMeasures.SurfaceAreaCurrent(geometry), 12);
        if (expected) Assert.Equal(5, shell.Volume, 12);
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
