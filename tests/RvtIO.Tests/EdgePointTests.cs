using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The list of an Edge is the one the file stores (the dense path rewrites Start, Interior and EndP for its own tessellation),
/// and the points are evaluated in the rounding order of the surface evaluation; the older sum of the terms in file order is one or a few
/// ulps away from them.</summary>
public class EdgePointTests
{
    private static GRepParserV2.SurfaceRec Frame(double[] o, double[] x, double[] y, double[] z, double r) =>
        new() { O = o, X = x, Y = y, Z = z, R = r };

    [Fact]
    public void Edge_point_list_is_the_stored_one_after_the_dense_path_rewrote_the_edge()
    {
        var stored = new List<GRepParserV2.EdgeUV> { new() { UA = 0.5, VA = 1.5 }, new() { UA = 0.25, VA = 1.5 } };
        var edge = new GRepParserV2.EdgeRec
        {
            Start = new() { UA = 9, VA = 9 }, EndP = new() { UA = 8, VA = 8 }, Interior = { new() { UA = 7, VA = 7 }, new() { UA = 6, VA = 6 } },
            RawStart = new() { UA = 1, VA = 2 }, RawEndP = new() { UA = 3, VA = 4 }, RawInterior = stored,
        };
        var points = edge.AuthoredPoints();
        Assert.Equal(4, points.Count);
        Assert.Equal((1.0, 2.0), (points[0].UA, points[0].VA));
        Assert.Equal((0.5, 1.5), (points[1].UA, points[1].VA));
        Assert.Equal((0.25, 1.5), (points[2].UA, points[2].VA));
        Assert.Equal((3.0, 4.0), (points[3].UA, points[3].VA));
    }

    [Fact]
    public void Edge_point_list_falls_back_to_the_current_points_when_none_were_kept()
    {
        var edge = new GRepParserV2.EdgeRec { Start = new() { UA = 9 }, EndP = new() { UA = 8 }, Interior = { new() { UA = 7 } } };
        var points = edge.AuthoredPoints();
        Assert.Equal(new[] { 9.0, 7.0, 8.0 }, points.Select(p => p.UA).ToArray());
    }

    [Fact]
    public void An_edge_next_to_a_free_form_face_keeps_the_list_the_free_form_track_refined()
    {
        // a shared edge is sampled once for all its users, from the list the dense path inserted the grid lines of the free form faces into
        var edge = new GRepParserV2.EdgeRec
        {
            Start = new() { UA = 9 }, EndP = new() { UA = 8 }, Interior = { new() { UA = 7 } }, FreeformShared = true,
            RawStart = new() { UA = 1 }, RawEndP = new() { UA = 3 }, RawInterior = [new() { UA = 2 }],
        };
        Assert.Equal(new[] { 9.0, 7.0, 8.0 }, edge.AuthoredPoints().Select(p => p.UA).ToArray());
        Assert.Equal(9.0, edge.StoredStart.UA);
        Assert.Equal(8.0, edge.StoredEndP.UA);
    }

    [Fact]
    public void Edges_next_to_ruled_and_Hermite_faces_are_marked_and_the_others_are_not()
    {
        var surfaces = new Dictionary<uint, GRepParserV2.SurfaceRec>
        {
            [1] = new() { Kind = GRepParserV2.SurfKind.Plane }, [2] = new() { Kind = GRepParserV2.SurfKind.Hermite },
            [3] = new() { Kind = GRepParserV2.SurfKind.Ruled }, [4] = new() { Kind = GRepParserV2.SurfKind.Cylinder },
        };
        var plain = new GRepParserV2.EdgeRec { Handle = 10, FaceA = 1, FaceB = 4 };
        var hermite = new GRepParserV2.EdgeRec { Handle = 11, FaceA = 1, FaceB = 2 };
        var ruled = new GRepParserV2.EdgeRec { Handle = 12, FaceA = 3, FaceB = 4 };
        var edges = new Dictionary<uint, GRepParserV2.EdgeRec?> { [10] = plain, [11] = hermite, [12] = ruled, [13] = null };
        GRepParserV2.MeshMarkFreeformEdges(edges, surfaces);
        Assert.False(plain.FreeformShared);
        Assert.True(hermite.FreeformShared);
        Assert.True(ruled.FreeformShared);
    }

    [Theory]
    [InlineData(true, (byte)6, true)]
    [InlineData(true, (byte)7, false)]
    [InlineData(false, (byte)6, false)]
    [InlineData(false, (byte)7, true)]
    public void A_coedge_runs_along_its_edge_by_the_side_and_bit_0_of_the_edge_flags(bool sideA, byte flags, bool forward)
    {
        // At the last end of an edge the next edge of side A and the previous edge of side B follow, bit 0 swaps the ends.
        // A few loops of two edges that share both ends cannot be decided by the continuity of the end points.
        Assert.Equal(forward, GRepParserV2.MeshCoedgeForward(new GRepParserV2.EdgeRec { Flags = flags }, sideA));
    }

    [Fact]
    public void Plane_point_preserves_rounding_order_with_only_the_result_allocation()
    {
        var surface = Frame([6e15, 8e15, 2], [0.6, 0.8, 0], [-0.8, 0.6, 0], [0, 0, 1], 0);
        Assert.Equal([-0.8, 0.6, 2], GRepParserV2.MeshPlanePoint(surface, -1e16, 1));

        var points = new double[256][];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < points.Length; i++) points[i] = GRepParserV2.MeshPlanePoint(surface, -1e16, i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(points);
        Assert.InRange(allocated, 0, 64L * points.Length);
    }

    [Theory]
    [InlineData(4.712388980384685, 0.04152515010747385, 0.21247801399732427, -0.06250799034779941, 0.2056640577410033)]
    [InlineData(2.8955361930263255, 0.08314499133618838, 0.17085817276860957, 0.015225765113994889, 0.14503877478090532)]
    public void Cylinder_point_is_the_unit_radial_direction_scaled_then_moved(double u, double v, double x, double y, double z)
    {
        var s = Frame([0.2540031641047981, -1.411238799618231e-13, 0.20566405774100432], [3.16576718602825e-15, -1.382799582304306e-14, 1],
            [0, 1, 1.382799582304306e-14], [-1, 0, 3.16576718602825e-15], 0.06250799034765829);
        Assert.Equal(new[] { x, y, z }, GRepParserV2.MeshCylinderPoint(s, u, v));
    }

    [Theory]
    [InlineData(-2.984513020910301, 8.364533322821886, 1.6202236558319936, -0.25661821692968695, 8.1364829396325)]
    [InlineData(-2.3561944901923426, 8.364533322821886, 1.1599520688756404, -1.1599520688757399, 8.1364829396325)]
    public void Cone_point_is_the_unit_generator_direction_times_the_slant_distance_from_the_apex(double u, double v, double x, double y, double z)
    {
        var s = Frame([7.849081524068347e-14, -1.6535357535541482e-13, 16.338582677164368], [-1, 3.231089148865161e-15, 0],
            [3.231089148865161e-15, 1, 3.4580515856032e-17], [4.262017452955111e-17, 3.458051585603186e-17, -1], 0.1973955598499025);
        Assert.Equal(new[] { x, y, z }, GRepParserV2.MeshConePoint(s, u, v));
    }

    [Theory]
    [InlineData(1.0471975511965967, 0.23591551399732436, 0.016710990921999756, 0.21531215284832814)]
    [InlineData(5.2359877559829835, 0.23591551399732436, -0.01671099092228233, 0.21531215284832758)]
    public void Revolved_point_is_the_unit_radial_direction_scaled_by_the_profile_radius(double u, double x, double y, double z)
    {
        var s = Frame([0.2540031641047981, -1.411238799618231e-13, 0.20566405774100432], [3.16576718602825e-15, -1.382799582304306e-14, 1],
            [0, 1, 1.382799582304306e-14], [-1, 0, 3.16576718602825e-15], 0);
        var profile = new GRepParserV2.ArcRec
        {
            C = [0.040129523547980356, -8.67746995743113e-30, 0.01808765010747382], X = [3.16576718602825e-15, -1.3132864627537627e-28, -1],
            Y = [-1, 0, -3.16576718602825e-15], R = 0.020833333333333332, RY = 0, T0 = 1.5707963267948992, T1 = 3.1415926535897882,
        };
        Assert.Equal(new[] { x, y, z }, GRepParserV2.MeshRevolvedPoint(s, profile, u, 1.5707963267948992));
    }

    private static List<double[]> HalfCircle(int segments) =>
        Enumerable.Range(0, segments + 1).Select(i => new[] { 4 + 1.5 * Math.Cos(Math.PI * i / segments), 2 + 1.5 * Math.Sin(Math.PI * i / segments), 1.25 }).ToList();

    [Fact]
    public void Round_samples_of_an_arc_edge_are_fitted_by_a_circle()
    {
        Assert.True(GRepParserV2.TryFittedArc(new GRepParserV2.EdgeRec { Flags = 8 }, HalfCircle(32), out var arc));
        Assert.Equal(1.5, arc.Radius, 12);
    }

    [Fact]
    public void Round_samples_of_an_edge_without_the_arc_flag_stay_the_interpolating_spline()
    {
        // a half circle between a plane and a ruled face (flags 6) is a kNurbCurve3d in the expected export, divided by the chord recursion and not into equal steps
        Assert.False(GRepParserV2.TryFittedArc(new GRepParserV2.EdgeRec { Flags = 6 }, HalfCircle(32), out _));
    }

    [Fact]
    public void The_arc_edge_of_a_cone_beside_a_cylinder_is_the_circle_of_the_cylinder_frame()
    {
        // sample project A, symbol 119160, edge 23 between the cone (face 35) and the cylinder (face 32): the circle the expected export reports for it is the cylinder's own
        // (exact axis and reference), not the cone circle, whose frame differs in the last bits and decides the interval count of the half circle
        var cylinder = Frame([-1.8962062022989088e-18, 0.29527559055118, -7.193315973768625e-16], [1, 0, 0], [0, 0, 1], [0, -1, 0], 0.09022309711286088);
        cylinder.Kind = GRepParserV2.SurfKind.Cylinder;
        var edge = new GRepParserV2.EdgeRec
        {
            FaceA = 35, FaceB = 32,
            Start = new() { UB = 3.14159265358979, VB = 0 }, EndP = new() { UB = 6.283185307179575, VB = 0 },
            Interior = { new() { UB = 3.490660407763654, VB = 2.589050205961555e-15 }, new() { UB = 4.71238898038469, VB = 0 } },
        };
        var surfaces = new Dictionary<uint, GRepParserV2.SurfaceRec> { [32] = cylinder };
        Assert.True(GRepParserV2.TryCylinderNeighbourArc(edge, true, surfaces, new HashSet<uint> { 32u }, out var arc));
        Assert.Equal(new[] { -1.8962062022989088e-18, 0.29527559055118, -7.193315973768625e-16 }, arc.Center);
        Assert.Equal(new[] { 0.0, -1.0, 0.0 }, arc.Normal);
        Assert.Equal(new[] { 1.0, 0.0, 0.0 }, arc.Ref);
        Assert.Equal(0.09022309711286088, arc.Radius);
        Assert.Equal((3.14159265358979, 6.283185307179575), (arc.Start, arc.End));
        // the beside cylinder must be an active face
        Assert.False(GRepParserV2.TryCylinderNeighbourArc(edge, true, surfaces, new HashSet<uint> { 35u }, out _));
    }
}
