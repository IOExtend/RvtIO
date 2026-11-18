using System.Text.RegularExpressions;
using RvtIO.Geometry;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The current profile writes the faces of a planar solid one IfcFace per serialized Face:
/// a bound for every loop in the order of the file, the last loop that does not lie inside an earlier one is the outer bound.</summary>
public class IfcExporterCurrentFaceBoundsTests
{
    private static List<double[]> Square(double min, double max, bool counterClockwise = true)
    {
        var ring = new List<double[]> { new[] { min, min, 0.0 }, new[] { max, min, 0.0 }, new[] { max, max, 0.0 }, new[] { min, max, 0.0 } };
        if (!counterClockwise) ring.Reverse();
        return ring;
    }

    private static double SignedArea(IReadOnlyList<double[]> ring)
    {
        double sum = 0;
        for (int index = 0; index < ring.Count; index++)
        {
            var a = ring[index];
            var b = ring[(index + 1) % ring.Count];
            sum += a[0] * b[1] - b[0] * a[1];
        }
        return sum / 2;
    }

    [Fact]
    public void FaceWithAHole_TheLoopListedFirstIsTheOuterBound()
    {
        var outer = Square(0, 4);
        var hole = Square(1, 3, counterClockwise: false);
        var bounds = IfcExporter.FaceBoundsCurrent([new GeoFace { OuterVertices = outer, InnerVertices = [hole] }], [outer, hole]);

        Assert.NotNull(bounds);
        Assert.Equal([true, false], bounds.Select(bound => bound.IsOuter));
        Assert.Equal(16.0, SignedArea(bounds[0].Ring));
        Assert.Equal(-4.0, SignedArea(bounds[1].Ring));
    }

    [Fact]
    public void FaceWithAHole_TheHoleListedFirstStaysFirstAndTheOuterLoopIsTheOuterBound()
    {
        var outer = Square(0, 4);
        var hole = Square(1, 3, counterClockwise: false);
        var bounds = IfcExporter.FaceBoundsCurrent([new GeoFace { OuterVertices = outer, InnerVertices = [hole] }], [hole, outer]);

        Assert.NotNull(bounds);
        Assert.Equal([false, true], bounds.Select(bound => bound.IsOuter));   // the file order is kept, not the outer loop first
        Assert.Equal(-4.0, SignedArea(bounds[0].Ring));
        Assert.Equal(16.0, SignedArea(bounds[1].Ring));
    }

    [Fact]
    public void DisjointCoplanarRegionsOfOneFace_BecomeLoopsOfOneFaceWithTheSameWinding()
    {
        var first = Square(0, 1);
        var second = Square(3, 4, counterClockwise: false);   // the other region was oriented the other way by the shell
        var bounds = IfcExporter.FaceBoundsCurrent(
            [new GeoFace { OuterVertices = first }, new GeoFace { OuterVertices = second }], [first, second]);

        Assert.NotNull(bounds);
        Assert.Equal([false, true], bounds.Select(bound => bound.IsOuter));   // neither loop is inside the other: the last one is the outer
        Assert.All(bounds, bound => Assert.True(SignedArea(bound.Ring) > 0));
    }

    [Fact]
    public void RegionsThatAreNotTheLoopsOfTheSerializedFace_GiveNoBounds()
    {
        var region = Square(0, 1);
        Assert.Null(IfcExporter.FaceBoundsCurrent([new GeoFace { OuterVertices = region }], [Square(5, 6)]));
        Assert.Null(IfcExporter.FaceBoundsCurrent([new GeoFace { OuterVertices = region }], [region, Square(5, 6)]));   // a loop no region has
        Assert.Null(IfcExporter.FaceBoundsCurrent([new GeoFace { OuterVertices = region }], []));
    }

    [Fact]
    public void AllVerticesStrictlyInside_ABoundaryVertexIsNotInside()
    {
        var outer = Square(0, 4);
        Assert.True(GeometryValidation.AllVerticesStrictlyInside(Square(1, 3), outer));
        Assert.False(GeometryValidation.AllVerticesStrictlyInside(Square(5, 6), outer));
        Assert.False(GeometryValidation.AllVerticesStrictlyInside(Square(0, 2), outer));   // shares the corner with the outer loop
        Assert.False(GeometryValidation.AllVerticesStrictlyInside(Square(2, 6), outer));   // one vertex inside, the others out
        // a polygon of a vertical plane: the projection drops the axis along which the plane is flat
        var wall = new List<double[]> { new[] { 0.0, 0, 0 }, new[] { 0.0, 4, 0 }, new[] { 0.0, 4, 4 }, new[] { 0.0, 0, 4 } };
        var window = new List<double[]> { new[] { 0.0, 1, 1 }, new[] { 0.0, 3, 1 }, new[] { 0.0, 3, 3 }, new[] { 0.0, 1, 3 } };
        Assert.True(GeometryValidation.AllVerticesStrictlyInside(window, wall));
        Assert.False(GeometryValidation.AllVerticesStrictlyInside(wall, window));
    }


    private static readonly double[][] Corners = [[0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0], [0, 0, 2], [4, 0, 2], [4, 3, 3], [0, 3, 3]];

    private static GeoFace Face(IEnumerable<double[]> ring, int node, uint source = 0, IReadOnlyList<List<double[]>>? loops = null)
    {
        var vertices = ring.Select(point => (double[])point.Clone()).ToList();
        return new GeoFace { OuterVertices = vertices, GeometryNode = node, SourceFace = source, SourceLoops = loops ?? (source == 0 ? null : [vertices]) };
    }

    // The box of Corners (its top slopes, so the solid stays a Brep) whose bottom is two regions of ONE serialized Face
    // (handle 11, two loops): the halves x<2 and x>2, the front and back faces carry the point between them.
    private static GeometryData BoxWithASplitBottom()
    {
        double[] p(int index) => Corners[index];
        double[] middleFront = [2, 0, 0], middleBack = [2, 3, 0];
        var left = new List<double[]> { p(0), middleFront, middleBack, p(3) };
        var right = new List<double[]> { middleFront, p(1), p(2), middleBack };
        List<List<double[]>> bottomLoops = [left, right];
        var faces = new List<GeoFace>
        {
            Face(left, 1, 11, bottomLoops),
            Face(right, 1, 11, bottomLoops),
            Face([p(4), p(5), p(6), p(7)], 1, 12),
            Face([p(0), middleFront, p(1), p(5), p(4)], 1, 13),
            Face([p(2), middleBack, p(3), p(7), p(6)], 1, 14),
            Face([p(1), p(2), p(6), p(5)], 1, 15),
            Face([p(3), p(0), p(4), p(7)], 1, 16),
        };
        return new GeometryData
        {
            BoundingBox = [0, 0, 0, 4, 3, 3], Faces = faces,
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(0, faces.Count).ToList() }],
        };
    }

    private static Element Product(GeometryData geometry) => new()
    {
        Id = 100, TypeId = 0, SuperInstanceId = 999, IsDiscovered = true, CategoryId = -2000151, Category = "OST_GenericModel",
        Name = "Product 100", References = [3],
        Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }],
        BoundingBox = geometry.BoundingBox, Geometry = geometry,
    };

    private static string Export(IfcProfile version, GeometryData geometry)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Product(geometry));
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-bounds-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static List<int> FacesPerClosedShell(string ifc, out List<int> boundsPerFace)
    {
        var shellFaces = Regex.Matches(ifc, @"=IFCCLOSEDSHELL\(\(([^)]*)\)\);").Select(match => match.Groups[1].Value.Split(',').Length).ToList();
        boundsPerFace = Regex.Matches(ifc, @"=IFCFACE\(\(([^)]*)\)\);").Select(match => match.Groups[1].Value.Split(',').Length).ToList();
        return shellFaces;
    }

    [Fact]
    public void ExportCurrent_TwoRegionsOfOneSerializedFace_AreOneIfcFaceWithTwoBounds()
    {
        string ifc = Export(IfcProfile.Current, BoxWithASplitBottom());

        Assert.Equal([6], FacesPerClosedShell(ifc, out var bounds));
        Assert.Equal([1, 1, 1, 1, 1, 2], bounds.Order());
        Assert.Equal(6, Regex.Matches(ifc, @"=IFCFACEOUTERBOUND\(").Count);   // one outer bound per face
        Assert.Single(Regex.Matches(ifc, @"=IFCFACEBOUND\("));        // the second loop of the merged face
    }

    [Fact]
    public void ExportLegacy_KeepsEveryRegionAsAFaceOfItsOwn()
    {
        string ifc = Export(IfcProfile.Legacy, BoxWithASplitBottom());

        Assert.Equal([7], FacesPerClosedShell(ifc, out var bounds));
        Assert.All(bounds, count => Assert.Equal(1, count));
        Assert.DoesNotContain("=IFCFACEBOUND(", ifc);
    }

    // Two disjoint boxes, one serialized solid or two; the faces carry the given Geometry node (0: they know none).
    private static GeometryData TwoDisjointBoxes(int node, bool oneSolid = true)
    {
        int[][] rings = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        var faces = new List<GeoFace>();
        for (int box = 0; box < 2; box++)
            faces.AddRange(rings.Select(ring => Face(ring.Select(index => new[] { Corners[index][0] + 10 * box, Corners[index][1], Corners[index][2] }), node)));
        return new GeometryData
        {
            BoundingBox = [0, 0, 0, 14, 3, 3], Faces = faces,
            BrepSolids = oneSolid
                ? [new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(0, faces.Count).ToList() }]
                : [new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(0, 6).ToList() },
                   new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(6, 6).ToList() }],
        };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExportCurrent_ShellsOfAGeometryWithoutNodes_AreOneItem(bool oneSolid)
    {
        // a wall has one Geometry node however many solids its GRep holds
        string ifc = Export(IfcProfile.Current, TwoDisjointBoxes(node: 0, oneSolid));

        Assert.Single(Regex.Matches(ifc, @"=IFCFACETEDBREP\("));
        Assert.Equal([12], FacesPerClosedShell(ifc, out _));
    }

    [Fact]
    public void ExportCurrent_ShellsOfOneNode_AreOneItemAsBefore()
    {
        string ifc = Export(IfcProfile.Current, TwoDisjointBoxes(node: 1));

        Assert.Single(Regex.Matches(ifc, @"=IFCFACETEDBREP\("));
        Assert.Equal([12], FacesPerClosedShell(ifc, out _));
    }

    [Fact]
    public void ExportLegacy_ShellsOfOneSerializedSolid_AreItemsOfTheirOwn()
    {
        string ifc = Export(IfcProfile.Legacy, TwoDisjointBoxes(node: 0));

        Assert.Equal(2, Regex.Matches(ifc, @"=IFCFACETEDBREP\(").Count);
    }
}
