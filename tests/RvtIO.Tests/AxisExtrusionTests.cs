using RvtIO.Geometry;
using System.Globalization;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The extrusion search along an axis of any direction, the curve loop order and the profile the current profile writes for it.</summary>
public class AxisExtrusionTests
{
    /// <summary>The six faces of the box [x0,x1] x [y0,y1] x [z0,z1], each ring counter-clockwise about its outward normal.</summary>
    private static List<GeoFace> Box(double x0, double x1, double y0, double y1, double z0, double z1)
    {
        GeoFace Face(double[] normal, params double[][] ring) => new()
        {
            Origin = ring[0], Normal = normal, OuterVertices = ring.ToList(),
            XVector = normal[2] != 0 ? [1, 0, 0] : [0, 0, 1], YVector = normal[2] != 0 ? [0, 1, 0] : [normal[1] != 0 ? 1 : 0, normal[0] != 0 ? 1 : 0, 0],
            PlaneXAxis = normal[2] != 0 ? [1, 0, 0] : [0, 0, 1], PlaneYAxis = normal[2] != 0 ? [0, 1, 0] : [normal[1] != 0 ? 1 : 0, normal[0] != 0 ? 1 : 0, 0],
        };
        return
        [
            Face([0, 0, -1], [x0, y0, z0], [x0, y1, z0], [x1, y1, z0], [x1, y0, z0]),
            Face([0, 0, 1], [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1]),
            Face([0, -1, 0], [x0, y0, z0], [x1, y0, z0], [x1, y0, z1], [x0, y0, z1]),
            Face([0, 1, 0], [x0, y1, z0], [x0, y1, z1], [x1, y1, z1], [x1, y1, z0]),
            Face([-1, 0, 0], [x0, y0, z0], [x0, y0, z1], [x0, y1, z1], [x0, y1, z0]),
            Face([1, 0, 0], [x1, y0, z0], [x1, y1, z0], [x1, y1, z1], [x1, y0, z1]),
        ];
    }

    [Fact]
    public void Box_AcrossY_StartsAtTheLowerFaceAndTakesItsLoop()
    {
        var found = WallExtrusion.FindAxisExtrusion(Box(0, 4, -1, 1, 0, 3), [0, 1, 0], out string reason);

        Assert.Equal("ok", reason);
        Assert.NotNull(found);
        Assert.Equal(2, found.LengthFeet, 9);
        Assert.Equal(-1, found.StartParameterFeet, 9);
        var loop = Assert.Single(found.Loops);
        Assert.All(loop, point => Assert.Equal(-1, point[1], 9));
        Assert.Equal(4, loop.Count);
    }

    [Fact]
    public void Box_AlongZ_MatchesTheVerticalSearch()
    {
        var faces = Box(0, 4, -1, 1, 0, 3);
        var found = WallExtrusion.FindAxisExtrusion(faces, [0, 0, 1], out _);

        Assert.True(WallExtrusion.TryFindVerticalExtrusion(faces, out _));
        Assert.NotNull(found);
        Assert.Equal(3, found.LengthFeet, 9);
        Assert.All(found.Loops[0], point => Assert.Equal(0, point[2], 9));
    }

    [Fact]
    public void Box_WithATiltedFace_OrAWrongAxis_HasNoExtrusion()
    {
        var tilted = Box(0, 4, -1, 1, 0, 3);
        tilted[4].Normal = [-Math.Sqrt(0.99), 0.1, 0];
        Assert.Null(WallExtrusion.FindAxisExtrusion(tilted, [0, 1, 0], out string reason));
        Assert.Equal("tilted-or-opening", reason);

        // an axis along which no two faces are parallel and equal
        Assert.Null(WallExtrusion.FindAxisExtrusion(Box(0, 4, -1, 1, 0, 3), [Math.Sqrt(0.5), Math.Sqrt(0.5), 0], out _));
    }

    [Fact]
    public void Box_WithAHoleInTheStartFace_KeepsTheHoleLoop()
    {
        var faces = Box(0, 4, -1, 1, 0, 3);
        faces[2].InnerVertices = [[[1, -1, 1], [1, -1, 2], [3, -1, 2], [3, -1, 1]]];
        faces[3].InnerVertices = [[[1, 1, 1], [3, 1, 1], [3, 1, 2], [1, 1, 2]]];
        // the side faces perpendicular to the axis must have no opening; the two end faces may
        var found = WallExtrusion.FindAxisExtrusion(faces, [0, 1, 0], out _);

        Assert.NotNull(found);
        Assert.Equal(2, found.Loops.Count);
        Assert.Equal(4, found.Loops[1].Count);
    }


    private static readonly double[][] Square = [[0, 0, 0], [0, 1, 0], [1, 1, 0], [1, 0, 0]];   // clockwise about +Z

    [Fact]
    public void CurveLoop_ClockwiseRing_IsFlippedKeepingTheSecondVertexFirst()
    {
        var loop = IfcExporter.CurveLoopCurrent(Square, [0, 0, 1], outer: true);

        // ring r0..r3 -> the chain from r1 is r1 r2 r3 r0 (clockwise), flipped: r1 r0 r3 r2
        Assert.Equal([Square[1], Square[0], Square[3], Square[2]], loop);
    }

    [Fact]
    public void CurveLoop_CounterClockwiseRing_KeepsTheChainFromTheSecondVertex()
    {
        var loop = IfcExporter.CurveLoopCurrent(Square.AsEnumerable().Reverse().ToArray(), [0, 0, 1], outer: true);

        // the reversed ring is r3 r2 r1 r0; the chain from its second vertex is r2 r1 r0 r3
        Assert.Equal([Square[2], Square[1], Square[0], Square[3]], loop);
    }

    [Fact]
    public void CurveLoop_Hole_IsClockwise()
    {
        var hole = IfcExporter.CurveLoopCurrent(Square.AsEnumerable().Reverse().ToArray(), [0, 0, 1], outer: false);

        // the counter-clockwise reversed ring is flipped for a hole: r2 r3 r0 r1
        Assert.Equal([Square[2], Square[3], Square[0], Square[1]], hole);
    }


    private static double[] Numbers(StepWriter w, string reference)
    {
        int id = int.Parse(reference.TrimStart('#'));
        for (int i = 0; i < w.EntityCount; i++)
            if (w.EntityAt(i).Id == id)
                return w.EntityAt(i).Args.Trim('(', ')').Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        throw new KeyNotFoundException(reference);
    }

    private static (string Type, string[] Attributes) Entity(StepWriter w, int id)
    {
        for (int i = 0; i < w.EntityCount; i++)
            if (w.EntityAt(i).Id == id) return (w.EntityAt(i).Type, IfcExporter.SplitStepArguments(w.EntityAt(i).Args));
        throw new KeyNotFoundException($"#{id}");
    }

    [Fact]
    public void Rectangle_InAVerticalPlane_TakesTheVerticalSideAsXDimAndTheSecondSideBackAsReference()
    {
        var w = new StepWriter();
        var globals = new IfcExporter.GlobalsCurrent(w);
        // an opening 0.8 wide (along X) and 2.1 high at y = -0.1, extruded along +Y: the loop in wall coordinates
        // (x 1..1.8, z 0..2.1), counter-clockwise seen from +Y means x runs backwards: start at the top (x = 1.8)
        double[][] loop = [[1.8, -0.1, 2.1], [1.8, -0.1, 0], [1.0, -0.1, 0], [1.0, -0.1, 2.1]];
        var parts = IfcExporter.WriteExtrusionProfileCurrent(w, globals, "''", [loop], [0, 1, 0], [0, 1, 0]);

        var (type, profile) = Entity(w, parts.ProfileId);
        Assert.Equal("IFCRECTANGLEPROFILEDEF", type);
        // plane: X = Z x Y = (-1, 0, 0), Y = Y x X = (0, 0, 1); the origin is the centre (1.4, -0.1, 1.05)
        // projected corners: v0 (-0.4, 1.05), v1 (-0.4, -1.05), v2 (0.4, -1.05), v3 (0.4, 1.05)
        // flipX (the plane's X axis is horizontal): X length = v2 - v3 = (0, -2.1), Y length = v0 - v3 = (-0.8, 0)
        Assert.Equal("2.1", profile[3]);
        Assert.Equal("0.8", profile[4]);
        var placement = Entity(w, int.Parse(profile[2][1..]));
        Assert.Equal(new[] { 0.0, 0.0 }, Numbers(w, placement.Attributes[0]).Select(Math.Abs).ToArray());
        Assert.Equal(new[] { 0.0, -1.0 }, Numbers(w, placement.Attributes[1]));
        var position = Entity(w, parts.PositionId);
        Assert.Equal(new[] { 1.4, -0.1, 1.05 }, Numbers(w, position.Attributes[0]).Select(v => Math.Round(v, 9)).ToArray());
        Assert.Equal(new[] { 0.0, 1.0, 0.0 }, Numbers(w, position.Attributes[1]));
        Assert.Equal(new[] { -1.0, 0.0, 0.0 }, Numbers(w, position.Attributes[2]));
        Assert.Equal(new[] { 0.0, 0.0, 1.0 }, Numbers(w, "#" + parts.DirectionId));
    }

    [Fact]
    public void Polygon_InAHorizontalPlane_IsAnArbitraryProfileInTheFrameOfTheInternalXAxis()
    {
        var w = new StepWriter();
        var globals = new IfcExporter.GlobalsCurrent(w);
        // a mitred wall footprint, counter-clockwise, in a frame whose internal Y axis is (0, 1, 0): the plane X is the frame X
        double[][] loop = [[0, -0.2, 0], [3, -0.2, 0], [3.4, 0.2, 0], [0, 0.2, 0]];
        var parts = IfcExporter.WriteExtrusionProfileCurrent(w, globals, "'Type'", [loop], [0, 0, 1], [0, 1, 0]);

        var (type, profile) = Entity(w, parts.ProfileId);
        Assert.Equal("IFCARBITRARYCLOSEDPROFILEDEF", type);
        Assert.Equal(".AREA.", profile[0]);
        Assert.Equal("'Type'", profile[1]);
        var curve = Entity(w, int.Parse(profile[2][1..]));
        Assert.Equal("IFCPOLYLINE", curve.Type);
        var pointIds = curve.Attributes[0].Trim('(', ')').Split(',');
        Assert.Equal(5, pointIds.Length);
        Assert.Equal(pointIds[0], pointIds[4]);
        // the origin is the mean of the vertices: (1.6, 0, 0); the points are relative to it
        Assert.Equal(new[] { -1.6, -0.2 }, Numbers(w, pointIds[0]).Select(v => Math.Round(v, 9)).ToArray());
        var position = Entity(w, parts.PositionId);
        Assert.Equal(new[] { 1.6, 0.0, 0.0 }, Numbers(w, position.Attributes[0]).Select(v => Math.Round(v, 9)).ToArray());
        Assert.Equal("$,$", $"{position.Attributes[1]},{position.Attributes[2]}");     // both axes are the defaults
    }

    [Fact]
    public void Polygon_WithAHole_IsAnArbitraryProfileWithVoids()
    {
        var w = new StepWriter();
        var globals = new IfcExporter.GlobalsCurrent(w);
        double[][] outer = [[0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0]];
        double[][] hole = [[1, 1, 0], [1, 2, 0], [2, 2, 0], [2, 1, 0]];     // clockwise
        var parts = IfcExporter.WriteExtrusionProfileCurrent(w, globals, "''", [outer, hole], [0, 0, 1], [0, 1, 0]);

        var (type, profile) = Entity(w, parts.ProfileId);
        Assert.Equal("IFCARBITRARYPROFILEDEFWITHVOIDS", type);
        Assert.Equal(4, profile.Length);
    }
}
