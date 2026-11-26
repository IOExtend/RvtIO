using System.Globalization;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>IFC placement, body and height checks for straight walls with one or more solids.</summary>
public class IfcMultiSolidWallExportTests
{
    // A box on [x0,x1] x [0,0.5] and [baseZ, baseZ + height] in feet, faces with normals.
    private static List<GeoFace> Box(double x0, double x1, double height, double baseZ = 0)
    {
        double topZ = baseZ + height;
        double[][] p = [[x0, 0, baseZ], [x1, 0, baseZ], [x1, 0.5, baseZ], [x0, 0.5, baseZ],
            [x0, 0, topZ], [x1, 0, topZ], [x1, 0.5, topZ], [x0, 0.5, topZ]];
        int[][] f = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return f.Select(ix =>
        {
            var ring = ix.Select(i => p[i]).ToList();
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                nx += (a[1] - b[1]) * (a[2] + b[2]); ny += (a[2] - b[2]) * (a[0] + b[0]); nz += (a[0] - b[0]) * (a[1] + b[1]);
            }
            double area = Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
            return new GeoFace { OuterVertices = ring, Normal = [nx / (2 * area), ny / (2 * area), nz / (2 * area)], Area = area };
        }).ToList();
    }

    private static Element Wall(params List<GeoFace>[] solids)
    {
        var shells = new List<GeoBrepSolid>();
        int next = 0;
        foreach (var solid in solids)
        {
            shells.Add(new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(next, solid.Count).ToList() });
            next += solid.Count;
        }
        double[] box = [0, 0, 0, 22, 0.5, solids.Max(solid => solid.Max(face => face.OuterVertices!.Max(point => point[2])))];
        return new Element
        {
            Id = 100, TypeId = 500, CategoryId = -2000011, Category = "OST_Walls", Name = "Wall", IsDiscovered = true,
            Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }],
            Location = new LocationCurve { CurveType = "Line", StartPoint = [0, 0.25, 0], EndPoint = [10, 0.25, 0], Length = 10 },
            BoundingBox = box,
            Geometry = new GeometryData { BoundingBox = box, Faces = solids.SelectMany(solid => solid).ToList(), BrepSolids = shells },
        };
    }

    private static string Export(Element wall, IfcUnitFamily? unit = null)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange([wall, new Element { Id = 500, TypeId = -1, CategoryId = -2000011, Name = "Wall type" }]);
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-multi-solid-wall-{Guid.NewGuid():N}.ifc");
        try { new IfcExporter { Profile = IfcProfile.Current, LengthUnit = unit }.Export(info, path); return File.ReadAllText(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static List<string> Entities(string ifc, string type) => Regex.Matches(ifc, $@"^#\d+={type}\(.*\);\s*$", RegexOptions.Multiline)
        .Select(match => match.Value.TrimEnd()).ToList();

    private static string[] Args(string entity) => IfcExporter.SplitStepArguments(entity[(entity.IndexOf('(') + 1)..entity.LastIndexOf(')')]);

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    private static Dictionary<string, string> ByReference(string ifc) =>
        ifc.Split('\n').Where(line => line.StartsWith('#')).ToDictionary(line => line[..line.IndexOf('=')]);

    [Theory]
    [InlineData(-12.0, null, 304.8)]
    [InlineData(-9.0, null, 304.8)]
    [InlineData(-12.0, IfcUnitFamily.Centimetre, 30.48)]
    public void ConstrainedWall_BaseOffsetIsAppliedOnce(double locationZ, IfcUnitFamily? unit, double unitsPerFoot)
    {
        var wall = Wall(Box(0, 10, 9, baseZ: -12));
        wall.Location!.StartPoint = [0, 0.25, locationZ];
        wall.Location.EndPoint = [10, 0.25, locationZ];
        wall.BoundingBox = wall.Geometry!.BoundingBox = [0, 0, -12, 10, 0.5, -3];
        wall.Parameters.AddRange(
        [
            new Parameter { Id = -1001108, StorageType = StorageType.Double, Value = -3.0 },
            new Parameter { Id = -1001103, StorageType = StorageType.ElementId, Value = 20 },
            new Parameter { Id = -1001105, StorageType = StorageType.Double, Value = 9.0 },
        ]);

        string ifc = Export(wall, unit);
        var entities = ByReference(ifc);
        var occurrence = Args(Assert.Single(Entities(ifc, "IFCWALLSTANDARDCASE")));
        var placement = Args(entities[occurrence[5]]);
        var axis = Args(entities[placement[1]]);
        var origin = Args(entities[axis[0]])[0].Trim('(', ')').Split(',').Select(Number).ToArray();
        double depth = Number(Args(Assert.Single(Entities(ifc, "IFCEXTRUDEDAREASOLID")))[3]);
        Assert.Equal(-12 * unitsPerFoot, origin[2], 6);
        Assert.Equal(-3 * unitsPerFoot, origin[2] + depth, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullHeightGap_ExportsBothWallPieces(bool reverseSolidOrder)
    {
        var end = Box(0, 0.2, 14);
        var other = Box(8, 20, 14);
        var wall = reverseSolidOrder ? Wall(other, end) : Wall(end, other);
        wall.Location!.EndPoint = [20, 0.25, 0];
        wall.Location.Length = 20;
        wall.BoundingBox = wall.Geometry!.BoundingBox = [0, 0, 0, 20, 0.5, 14];
        wall.OwnWallTrimmedAxis = reverseSolidOrder ? [[8, 0.25, 0], [20, 0.25, 0]] : [[0, 0.25, 0], [0.2, 0.25, 0]];

        string ifc = Export(wall);
        var entities = ByReference(ifc);
        var occurrence = Args(Assert.Single(Entities(ifc, "IFCWALL")));
        var reps = Args(entities[occurrence[6]])[2].Trim('(', ')').Split(',').Select(id => Args(entities[id])).ToList();
        var body = Assert.Single(reps, rep => rep[1] == "'Body'");
        Assert.Equal("'Brep'", body[2]);
        var axis = Assert.Single(reps, rep => rep[1] == "'Axis'");
        var trim = Args(entities[axis[3].Trim('(', ')')]);
        Assert.Equal(20 * 304.8, Number(Args(entities[trim[2].Trim('(', ')')])[0].Trim('(', ')').Split(',')[0]), 6);
        var pieces = body[3].Trim('(', ')').Split(',').Select(AlongExtent).OrderBy(bounds => bounds.Min).ToArray();
        Assert.Equal(2, pieces.Length);
        Assert.Equal(0, pieces[0].Min, 6);
        Assert.Equal(0.2 * 304.8, pieces[0].Max, 6);
        Assert.Equal(8 * 304.8, pieces[1].Min, 6);
        Assert.Equal(20 * 304.8, pieces[1].Max, 6);

        (double Min, double Max) AlongExtent(string brep)
        {
            var xCoordinates = Args(entities[Args(entities[brep])[0]])[0].Trim('(', ')').Split(',')
                .Select(face => Args(entities[face])[0].Trim('(', ')'))
                .Select(bound => Args(entities[bound])[0])
                .SelectMany(loop => Args(entities[loop])[0].Trim('(', ')').Split(','))
                .Select(point => Number(Args(entities[point])[0].Trim('(', ')').Split(',')[0])).ToArray();
            return (xCoordinates.Min(), xCoordinates.Max());
        }
    }

    [Fact]
    public void ExtrusionDepthAndHeight_AreThoseOfTheFirstSolid()
    {
        // two rectangular solids, 3 ft and 6 ft high: the wall is the extrusion of the first one
        string ifc = Export(Wall(Box(0, 10, 3), Box(12, 22, 6)));

        var wall = Assert.Single(Entities(ifc, "IFCWALLSTANDARDCASE"));
        Assert.NotNull(wall);
        var solid = Assert.Single(Entities(ifc, "IFCEXTRUDEDAREASOLID"));
        Assert.Equal(3 * 304.8, Number(Args(solid)[3]), 6);
        var height = Assert.Single(Entities(ifc, "IFCQUANTITYLENGTH"), quantity => Args(quantity)[0] == "'Height'");
        Assert.Equal(3 * 304.8, Number(Args(height)[3]), 6);
    }

    [Fact]
    public void OtherSolidsOfTheWall_ClosedOrNot_DoNotChangeTheDepthOrTheHeight()
    {
        // the second solid, 6 ft high, has no top face: it is open, and it is not the solid the wall is extruded from
        var tall = Box(12, 22, 6);
        tall.RemoveAt(1);
        var wall = Wall(Box(0, 10, 3), tall);
        wall.Geometry!.BrepSolids![1].IsClosed = false;

        string ifc = Export(wall);

        Assert.Single(Entities(ifc, "IFCWALLSTANDARDCASE"));
        var solid = Assert.Single(Entities(ifc, "IFCEXTRUDEDAREASOLID"));
        Assert.Equal(3 * 304.8, Number(Args(solid)[3]), 6);
        var height = Assert.Single(Entities(ifc, "IFCQUANTITYLENGTH"), quantity => Args(quantity)[0] == "'Height'");
        Assert.Equal(3 * 304.8, Number(Args(height)[3]), 6);
    }

    [Fact]
    public void WallOfOneSolid_KeepsItsHeightAsDepthAndHeight()
    {
        string ifc = Export(Wall(Box(0, 10, 6)));

        var solid = Assert.Single(Entities(ifc, "IFCEXTRUDEDAREASOLID"));
        Assert.Equal(6 * 304.8, Number(Args(solid)[3]), 6);
        var height = Assert.Single(Entities(ifc, "IFCQUANTITYLENGTH"), quantity => Args(quantity)[0] == "'Height'");
        Assert.Equal(6 * 304.8, Number(Args(height)[3]), 6);
    }
}
