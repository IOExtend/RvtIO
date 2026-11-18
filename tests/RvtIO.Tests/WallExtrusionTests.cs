using RvtIO.Geometry;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class WallExtrusionTests
{
    /// <summary>A vertical prism over a counter-clockwise plan polygon. The horizontal faces carry the plane
    /// frame (u along <paramref name="frameAngleDegrees"/>); the vertical faces carry none.</summary>
    private static List<GeoFace> Prism(double[][] plan, double z0, double z1, double frameAngleDegrees = 0)
    {
        double a = frameAngleDegrees * Math.PI / 180;
        double[] u = [Math.Cos(a), Math.Sin(a), 0], v = [-Math.Sin(a), Math.Cos(a), 0];
        var faces = new List<GeoFace>
        {
            new()
            {
                Origin = [plan[0][0], plan[0][1], z0],
                Normal = [0, 0, -1],
                OuterVertices = plan.AsEnumerable().Reverse().Select(p => new[] { p[0], p[1], z0 }).ToList(),
                PlaneXAxis = u, PlaneYAxis = v,
            },
            new()
            {
                Origin = [plan[0][0], plan[0][1], z1],
                Normal = [0, 0, 1],
                OuterVertices = plan.Select(p => new[] { p[0], p[1], z1 }).ToList(),
                PlaneXAxis = u, PlaneYAxis = v,
            },
        };
        for (int i = 0; i < plan.Length; i++)
        {
            var p = plan[i];
            var q = plan[(i + 1) % plan.Length];
            double dx = q[0] - p[0], dy = q[1] - p[1], length = Math.Sqrt(dx * dx + dy * dy);
            faces.Add(new GeoFace
            {
                Origin = [p[0], p[1], z0],
                Normal = [dy / length, -dx / length, 0],
                OuterVertices = [[p[0], p[1], z0], [q[0], q[1], z0], [q[0], q[1], z1], [p[0], p[1], z1]],
            });
        }
        return faces;
    }

    private static readonly double[][] Rectangle = [[0, 0], [10, 0], [10, 1], [0, 1]];
    private static readonly double[][] MiteredEnds = [[0, 0], [10, 0], [11, 1], [0.5, 1]];

    [Fact]
    public void PlainBox_IsSimpleAndAVerticalExtrusion()
    {
        var faces = Prism(Rectangle, 0, 3);
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces));
        Assert.True(WallExtrusion.TryFindVerticalExtrusion(faces, out string reason));
        Assert.Equal("ok", reason);
    }

    [Fact]
    public void MiteredEnds_AreNotSimple_ButStillAnExtrusionWhenThePlaneFrameIsWorldAligned()
    {
        var faces = Prism(MiteredEnds, 0, 3);
        Assert.False(WallExtrusion.IsSimpleRectangularWall(faces));
        Assert.True(WallExtrusion.TryFindVerticalExtrusion(faces, out _));
    }

    [Fact]
    public void RotatedPlaneFrame_FailsTheBoundingBoxCheck()
    {
        // The end faces' envelope corners are two points of the rotated frame; they do not span the world box.
        var faces = Prism(MiteredEnds, 0, 3, frameAngleDegrees: 37);
        Assert.False(WallExtrusion.TryFindVerticalExtrusion(faces, out string reason));
        Assert.Equal("bbox", reason);
    }

    [Fact]
    public void SlopedTop_HasNoParallelPartnerForTheBottom()
    {
        var faces = Prism(Rectangle, 0, 3);
        faces[1].Normal = [0.1, 0, Math.Sqrt(1 - 0.01)];
        Assert.False(WallExtrusion.IsSimpleRectangularWall(faces));
        Assert.False(WallExtrusion.TryFindVerticalExtrusion(faces, out string reason));
        Assert.Equal("no-pair", reason);
    }

    [Fact]
    public void TiltedSideFace_BlocksTheExtrusion()
    {
        var faces = Prism(Rectangle, 0, 3);
        faces[2].Normal = [0, -Math.Sqrt(1 - 0.01), 0.1];
        Assert.False(WallExtrusion.IsSimpleRectangularWall(faces));
        Assert.False(WallExtrusion.TryFindVerticalExtrusion(faces, out string reason));
        Assert.Equal("tilted-or-opening", reason);
    }

    [Fact]
    public void CurvedFace_IsNotSimple_ButTheExtrusionSearchSkipsIt()
    {
        var faces = Prism(Rectangle, 0, 3);
        faces[2].CurvedSurface = true;
        Assert.False(WallExtrusion.IsSimpleRectangularWall(faces));
        Assert.True(WallExtrusion.TryFindVerticalExtrusion(faces, out _));
    }

    [Fact]
    public void OpeningInAVerticalFace_BlocksTheExtrusion()
    {
        var faces = Prism(Rectangle, 0, 3);
        faces[2].InnerVertices = [[[2, 0, 1], [3, 0, 1], [3, 0, 2], [2, 0, 2]]];
        Assert.False(WallExtrusion.TryFindVerticalExtrusion(faces, out string reason));
        Assert.Equal("tilted-or-opening", reason);
    }

    [Fact]
    public void RegionsOfOneSerializedFace_AreOneFaceForTheAreaPairing()
    {
        var faces = Prism(Rectangle, 0, 3);
        var end = faces[3];
        var lower = new GeoFace { Origin = end.Origin, Normal = end.Normal, SourceFace = 7, OuterVertices = [[10, 0, 0], [10, 1, 0], [10, 1, 1], [10, 0, 1]] };
        var upper = new GeoFace { Origin = end.Origin, Normal = end.Normal, SourceFace = 7, OuterVertices = [[10, 0, 1], [10, 1, 1], [10, 1, 3], [10, 0, 3]] };
        faces.RemoveAt(3);
        faces.AddRange([lower, upper]);

        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces));
        lower.SourceFace = 0;
        upper.SourceFace = 0;
        Assert.False(WallExtrusion.IsSimpleRectangularWall(faces));
    }

    [Fact]
    public void BodyWithoutDecodedFaces_CannotBeJudged()
    {
        Assert.Null(WallExtrusion.IsStandardCaseBody(null));
        Assert.Null(WallExtrusion.IsStandardCaseBody(new GeometryData()));
    }

    private static GeometryData Body(List<GeoFace> faces, int solids = 1)
    {
        var geometry = new GeometryData { Faces = faces, BrepSolids = [] };
        geometry.BrepSolids.Add(new GeoBrepSolid { FaceIndices = Enumerable.Range(0, faces.Count).ToList() });
        for (int i = 1; i < solids; i++) geometry.BrepSolids.Add(new GeoBrepSolid());
        return geometry;
    }

    [Fact]
    public void CurvedBodyUsesAuthoredEdgeBoundsWhenEverySourceFaceHasThem()
    {
        var faces = ArcWall([45, 135]);
        Assert.False(WallExtrusion.IsStandardCaseBody(Body(faces)));
        var points = faces.SelectMany(face => face.OuterVertices!).ToList();
        double[] bounds = [points.Min(p => p[0]), points.Min(p => p[1]), points.Min(p => p[2]),
            points.Max(p => p[0]), points.Max(p => p[1]), points.Max(p => p[2])];
        foreach (var face in faces) face.MeshEdgeBounds = bounds;
        // The exporter uses the stored edge points; it does not invent the unsampled 90-degree point.
        Assert.True(WallExtrusion.IsStandardCaseBody(Body(faces)));
        faces[0].MeshEdgeBounds = null;
        Assert.False(WallExtrusion.IsStandardCaseBody(Body(faces)));
    }

    [Fact]
    public void BodyJudgement_FollowsTheReliableFailuresOnly()
    {
        // Mitered ends in a world-aligned frame: not simple, but a vertical extrusion.
        Assert.True(WallExtrusion.IsStandardCaseBody(Body(Prism(MiteredEnds, 0, 3))));
        // The same body in a rotated plane frame misses the bounding-box check: the wall is written as an IfcWall.
        Assert.False(WallExtrusion.IsStandardCaseBody(Body(Prism(MiteredEnds, 0, 3, frameAngleDegrees: 37))));
        // A hole in a vertical face is the decoded-reveals gap: treated as passing.
        var perforated = Prism(MiteredEnds, 0, 3, frameAngleDegrees: 37);
        perforated[2].InnerVertices = [[[2, 0, 1], [3, 0, 1], [3, 0, 2], [2, 0, 2]]];
        Assert.True(WallExtrusion.IsStandardCaseBody(Body(perforated)));
        // A tilted face stays decisive, with or without a hole.
        perforated[3].Normal = [Math.Sqrt(1 - 0.01), 0, 0.1];
        Assert.False(WallExtrusion.IsStandardCaseBody(Body(perforated)));
        // The body-exporter route needs exactly one solid: with a second one only the first solid's simple test can pass.
        Assert.False(WallExtrusion.IsStandardCaseBody(Body(Prism(MiteredEnds, 0, 3, frameAngleDegrees: 37), solids: 2)));
        Assert.False(WallExtrusion.IsStandardCaseBody(Body(Prism(MiteredEnds, 0, 3), solids: 2)));
        Assert.False(WallExtrusion.IsStandardCaseBody(Body(perforated, solids: 2)));
    }

    [Fact]
    public void ClosedShell_IsJudgedByTheExactTest()
    {
        // A hole in a vertical face: an open decoded shell may only lack reveals, so the verdict stays "passes".
        var perforated = Prism(MiteredEnds, 0, 3);
        perforated[2].InnerVertices = [[[2, 0, 1], [3, 0, 1], [3, 0, 2], [2, 0, 2]]];
        var open = Body(perforated);
        Assert.True(WallExtrusion.IsStandardCaseBody(open));
        // The same faces as a closed shell are the solid that is judged: the opening is real and the wall is an IfcWall.
        var closed = Body(perforated);
        closed.BrepSolids![0].IsClosed = true;
        Assert.False(WallExtrusion.IsStandardCaseBody(closed));
        // A closed shell without openings is still a vertical extrusion.
        var plain = Body(Prism(MiteredEnds, 0, 3));
        plain.BrepSolids![0].IsClosed = true;
        Assert.True(WallExtrusion.IsStandardCaseBody(plain));
        // A second solid rules out the body-exporter route, whatever the first one is.
        var several = Body(perforated, solids: 2);
        several.BrepSolids![0].IsClosed = true;
        Assert.False(WallExtrusion.IsStandardCaseBody(several));
    }

    private static RvtIO.Parsing.TfBlockDeserializer.OwnWallHelperInput Helper(bool complete, params int[] loops)
        => new(3524, true, complete, loops);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InactiveIncompatibleFaceRejectsOnlyItsOwnSimpleWallRoute(bool curved)
    {
        var faces = Prism(Rectangle, 0, 3);
        foreach (var face in faces) face.GeometryNode = 7;
        var geometry = Body(faces);
        geometry.BrepSolids![0].IsClosed = true;
        geometry.InactiveFaces = [new() { GeometryNode = 7, Normal = [1, 1, 1], CurvedSurface = curved }];

        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces));
        Assert.False(WallExtrusion.IsSimpleRectangularWall(faces, null, geometry.InactiveFaces));
        Assert.False(IfcExporter.PassesSimpleRectangularWallCurrent(new() { Geometry = geometry }));
        // The body exporter ignores inactive faces: this active box still has a vertical extrusion.
        Assert.True(WallExtrusion.IsStandardCaseBody(geometry));

        geometry.InactiveFaces[0].GeometryNode = 8;
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces, null, geometry.InactiveFaces));
        geometry.InactiveFaces[0].GeometryNode = 7;
        foreach (var face in faces) face.GeometryNode = 0;
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces, null, geometry.InactiveFaces));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotchedWallBecomesGenericOnlyWhenItsInactiveFaceRejectsTheSimpleRoute(bool curved)
    {
        var faces = ElevationExtrusion([[0, 0], [6, 0], [6, 3], [4, 3], [4, 2], [2, 2], [2, 3], [0, 3]]);
        foreach (var face in faces) face.GeometryNode = 7;
        var geometry = Body(faces);
        geometry.BrepSolids![0].IsClosed = true;
        Assert.True(WallExtrusion.IsStandardCaseBody(geometry));
        Assert.False(WallExtrusion.TryFindVerticalExtrusion(faces, out _));

        geometry.InactiveFaces = [new() { GeometryNode = 7, Normal = [1, 1, 1], CurvedSurface = curved }];

        Assert.False(WallExtrusion.IsStandardCaseBody(geometry));
        Assert.Equal(faces.Count, geometry.Faces.Count);
    }

    [Theory]
    [InlineData(0.999, false)]
    [InlineData(1.0, false)]
    [InlineData(1.001, false)]
    [InlineData(0.999, true)]
    [InlineData(1.0, true)]
    [InlineData(1.001, true)]
    public void OrdinarySteppedAndSillWallsStaySimpleWithoutAnInactiveTilt(double step, bool bottomNotch)
    {
        double[][] profile = [[0, 0], [6, 0], [6, 3], [4, 3], [4, 3 - step], [2, 3 - step], [2, 3], [0, 3]];
        if (bottomNotch) profile = profile.Select(p => new[] { p[0], 3 - p[1] }).AsEnumerable().Reverse().ToArray();
        var faces = ElevationExtrusion(profile);
        foreach (var face in faces) face.GeometryNode = 7;
        var geometry = Body(faces);
        geometry.BrepSolids![0].IsClosed = true;
        geometry.InactiveFaces = [new() { GeometryNode = 7, Normal = [0, 0, 1] }];

        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces, null, geometry.InactiveFaces));
        Assert.True(WallExtrusion.IsStandardCaseBody(geometry));
    }

    [Fact]
    public void CompleteProfileHelperWithOtherThanFourCurves_MakesAnOtherwiseSimpleWallNotSimple()
    {
        var faces = Prism(Rectangle, 0, 3);
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces, null));
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces, new(3524, false, false, [])));
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces, Helper(true, 4)));
        // Only the first (definition) loop counts: a four-sided profile with a hole of any size stays simple.
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces, Helper(true, 4, 6)));
        Assert.False(WallExtrusion.IsSimpleRectangularWall(faces, Helper(true, 8)));
        Assert.False(WallExtrusion.IsSimpleRectangularWall(faces, Helper(true, 3)));
        // An incomplete helper and a helper without loops are not consulted.
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces, Helper(false, 8)));
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces, Helper(true)));
    }

    [Fact]
    public void VerticalFacesArePairedByNetArea_ThatIsLessTheirOpenings()
    {
        // Both long faces have the same outer loop, but the back one carries a larger opening: the face areas differ.
        double[][] Hole(double x0, double x1, double y) => [[x0, y, 1], [x1, y, 1], [x1, y, 2], [x0, y, 2]];
        var faces = Prism(Rectangle, 0, 3);
        faces[2].InnerVertices = [[.. Hole(2, 3, 0)]];
        faces[4].InnerVertices = [[.. Hole(2, 3, 1)]];
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces));
        faces[4].InnerVertices = [[.. Hole(2, 4, 1)]];
        Assert.False(WallExtrusion.IsSimpleRectangularWall(faces));
        // The decoded openings are only the net area: a body without openings is judged by the outer loops as before.
        Assert.True(WallExtrusion.IsSimpleRectangularWall(Prism(Rectangle, 0, 3)));
    }

    [Fact]
    public void RevealsOfAnOpening_PairAtTheNoiseToleranceOnlyWhenTheyTouchTheHole()
    {
        GeoFace RevealFace(double[][] ring, double nx) => new() { Normal = [nx, 0, 0], Origin = ring[0], OuterVertices = ring.ToList() };
        var faces = Prism(Rectangle, 0, 3);
        faces[2].InnerVertices = [[[2, 0, 1], [3, 0, 1], [3, 0, 2], [2, 0, 2]]];
        faces[4].InnerVertices = [[[2, 1, 1], [3, 1, 1], [3, 1, 2], [2, 1, 2]]];
        // the reveals share two corners with the hole; their areas differ by 2.5e-5 ft2 (above the 1e-5 pairing, below the noise bound)
        faces.Add(RevealFace([[2, 0, 1], [2, 1, 1], [2, 1, 2], [2, 0, 2]], 1));
        faces.Add(RevealFace([[3, 0, 1], [3, 1, 1], [3, 1.00005, 2], [3, 0, 2]], -1));
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces));
        // a reveal that is really different stays unpaired
        faces[^1] = RevealFace([[3, 0, 1], [3, 1, 1], [3, 1.01, 2], [3, 0, 2]], -1);
        Assert.False(WallExtrusion.IsSimpleRectangularWall(faces));
        // faces that do not touch the hole get no noise bound
        faces[^2] = RevealFace([[2, 0.3, 1], [2, 1.3, 1], [2, 1.3, 2], [2, 0.3, 2]], 1);
        faces[^1] = RevealFace([[3, 0.3, 1], [3, 1.3, 1], [3, 1.30005, 2], [3, 0.3, 2]], -1);
        Assert.False(WallExtrusion.IsSimpleRectangularWall(faces));
    }

    [Theory]
    [InlineData(0.002, false)]
    [InlineData(0.003, true)]
    public void ProfileLoopWithACurveShorterThanShortCurveTol_HasNoExtrusion(double length, bool extrusion)
    {
        // a strip of the wall: every vertical face pairs, yet the loop that the kernel builds from its bottom face loses the short sides
        double[][] strip = [[0, 0], [length, 0], [length, 1], [0, 1]];
        var faces = Prism(strip, 0, 3);
        Assert.True(WallExtrusion.IsSimpleRectangularWall(faces));
        Assert.Equal(extrusion, WallExtrusion.TryFindVerticalExtrusion(faces, out string reason));
        Assert.Equal(extrusion ? "ok" : "short-curve", reason);
    }

    [Theory]
    [InlineData(0.002, false)]
    [InlineData(0.003, true)]
    [InlineData(null, true)]
    public void TrimmedAxisShorterThanShortCurveTol_LeavesNoSimpleWallRoute(double? axisLength, bool standardCase)
    {
        // the thickened axis of the wall export is a loop of four lines: the axis cannot be one of them
        double length = axisLength ?? 10;
        var body = Body(Prism([[0, 0], [length, 0], [length, 1], [0, 1]], 0, 3));
        Assert.Equal(standardCase, WallExtrusion.IsStandardCaseBody(body, null, axisLength));
    }

    /// <summary>A curved wall: an annular sector around (10, 5), radii 8 and 9, the arc tessellated at the given angles; the
    /// top and bottom faces are bounded by the polygon, the cylinders are patches of one source Face each.</summary>
    private static List<GeoFace> ArcWall(double[] degrees, double z0 = 0, double z1 = 3)
    {
        const double cx = 10, cy = 5, inner = 8, outer = 9;
        double[] At(double r, double deg, double z) => [cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180), z];
        var outerRing = degrees.Select(a => At(outer, a, 0)).ToList();
        var innerRing = degrees.Select(a => At(inner, a, 0)).ToList();
        List<double[]> Ring(double z) => outerRing.Concat(Enumerable.Reverse(innerRing)).Select(p => new[] { p[0], p[1], z }).ToList();
        var faces = new List<GeoFace>
        {
            new() { Origin = [cx, cy, z0], Normal = [0, 0, -1], OuterVertices = Enumerable.Reverse(Ring(z0)).ToList() },
            new() { Origin = [cx, cy, z1], Normal = [0, 0, 1], OuterVertices = Ring(z1) },
        };
        foreach (double deg in new[] { degrees[0], degrees[^1] })
        {
            double rad = deg * Math.PI / 180;
            faces.Add(new GeoFace
            {
                Origin = At(inner, deg, z0),
                Normal = [Math.Sin(rad), -Math.Cos(rad), 0],
                OuterVertices = [At(inner, deg, z0), At(outer, deg, z0), At(outer, deg, z1), At(inner, deg, z1)],
            });
        }
        foreach (var (radius, handle) in new[] { (outer, 100u), (inner, 101u) })
            for (int i = 0; i + 1 < degrees.Length; i++)
            {
                double mid = (degrees[i] + degrees[i + 1]) / 2 * Math.PI / 180;
                faces.Add(new GeoFace
                {
                    Origin = At(radius, degrees[i], z0),
                    Normal = [Math.Cos(mid), Math.Sin(mid), 0],
                    OuterVertices = [At(radius, degrees[i], z0), At(radius, degrees[i + 1], z0), At(radius, degrees[i + 1], z1), At(radius, degrees[i], z1)],
                    CurvedSurface = true,
                    SourceFace = handle,
                    AnalyticCylinderOrigin = [cx, cy, 0],
                    AnalyticCylinderAxis = [0, 0, 1],
                    AnalyticCylinderReferenceDirection = [1, 0, 0],
                    AnalyticCylinderRadius = radius,
                });
            }
        return faces;
    }

    [Fact]
    public void CurvedWall_FailsTheBoundingBoxCheckWhenACylinderBulgesPastTheTessellationPolygon()
    {
        // The arc passes through +y (90 degrees). With a polygon vertex there the exact extreme equals the vertex.
        var vertexAtExtreme = ArcWall([45, 90, 135]);
        Assert.True(WallExtrusion.TryFindVerticalExtrusion(vertexAtExtreme, out _, exactCylinders: true));
        Assert.True(WallExtrusion.IsStandardCaseBody(Body(vertexAtExtreme)));
        // Without a vertex the cylinder reaches 9 * (1 - cos 45 degrees) past the polygon: the boxes differ, the wall is an IfcWall.
        var bulging = ArcWall([45, 135]);
        Assert.True(WallExtrusion.TryFindVerticalExtrusion(bulging, out _));
        Assert.False(WallExtrusion.TryFindVerticalExtrusion(bulging, out string reason, exactCylinders: true));
        Assert.Equal("bbox", reason);
        Assert.False(WallExtrusion.IsStandardCaseBody(Body(bulging)));
        // A bulge below the vertex tolerance (sagitta 9 * (1 - cos 0.5 degrees) = 0.0003 ft) does not matter.
        Assert.True(WallExtrusion.IsStandardCaseBody(Body(ArcWall([89.5, 90.5]))));
        // A sector that does not pass through a world axis direction has no extra extreme.
        Assert.True(WallExtrusion.IsStandardCaseBody(Body(ArcWall([20, 70]))));
    }

    [Fact]
    public void CurvedWall_PairsTopAndBottomDespiteTheirIndependentTessellation()
    {
        // The bottom polygon has its inner vertex at 90 degrees 0.012 ft further out: its area is about 0.5% smaller.
        var faces = ArcWall([45, 90, 135]);
        var vertex = faces[0].OuterVertices!.Single(p => Math.Abs(p[0] - 10) < 1e-9 && Math.Abs(p[1] - 13) < 1e-9);
        vertex[1] += 0.012;
        Assert.False(WallExtrusion.TryFindVerticalExtrusion(faces, out string reason));
        Assert.Equal("no-pair", reason);
        Assert.True(WallExtrusion.TryFindVerticalExtrusion(faces, out _, exactCylinders: true));
        Assert.True(WallExtrusion.IsStandardCaseBody(Body(faces)));
        // Without a cylinder the tolerance stays absolute: a straight wall with such a difference is not an extrusion.
        var straight = Prism(Rectangle, 0, 3);
        straight[0].OuterVertices![0][0] -= 0.005;
        Assert.False(WallExtrusion.TryFindVerticalExtrusion(straight, out _, exactCylinders: true));
    }

    [Fact]
    public void ProfileHelperRejection_TurnsAWallWithoutAnExtrusionIntoAnIfcWall()
    {
        // A top notch: the vertical faces pair up by area (simple), but the three upward faces and one bottom face have
        // no equal pair, so the body exporter finds no extrusion either.
        var notched = ElevationExtrusion([[0, 0], [6, 0], [6, 3], [4, 3], [4, 2], [2, 2], [2, 3], [0, 3]]);
        Assert.True(WallExtrusion.IsSimpleRectangularWall(notched));
        Assert.False(WallExtrusion.TryFindVerticalExtrusion(notched, out _));
        Assert.True(WallExtrusion.IsStandardCaseBody(Body(notched)));
        Assert.True(WallExtrusion.IsStandardCaseBody(Body(notched), Helper(true, 4)));
        Assert.False(WallExtrusion.IsStandardCaseBody(Body(notched), Helper(true, 8)));
        // The same helper does not stop a wall that is a vertical extrusion by itself.
        var mitered = Prism(MiteredEnds, 0, 3);
        Assert.True(WallExtrusion.IsStandardCaseBody(Body(mitered), Helper(true, 8)));
    }

    [Theory]
    [InlineData(IfcProfile.Current, "=IFCWALL(")]
    [InlineData(IfcProfile.Legacy, "=IFCWALLSTANDARDCASE(")]
    public void WallWithANotchedProfileHelper_UsesTheRuleOfTheReferenceVersion(IfcProfile version, string expected)
    {
        var geometry = new GeometryData { Faces = ElevationExtrusion([[0, 0], [6, 0], [6, 3], [4, 3], [4, 2], [2, 2], [2, 3], [0, 3]]) };
        geometry.BrepSolids = [new GeoBrepSolid { FaceIndices = Enumerable.Range(0, geometry.Faces.Count).ToList(), IsClosed = true }];
        var wall = new Element
        {
            Id = 43,
            TypeId = 44,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Name = "Wall with a notch",
            References = [99],
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.Integer, Value = 99 },
                new Parameter { Id = -1001000, StorageType = StorageType.Double, Value = 1.0 },
                new Parameter { Id = -1001105, StorageType = StorageType.Double, Value = 3.0 },
            ],
            Location = new LocationCurve { CurveType = "Line", StartPoint = [0, 0.5, 0], EndPoint = [6, 0.5, 0] },
            BoundingBox = [0, 0, 0, 6, 1, 3],
            Geometry = geometry,
            OwnWallHelper = Helper(true, 8),
        };
        var info = new RevitFileInfo();
        info.Elements.Add(wall);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-wall-helper-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            string ifc = File.ReadAllText(path);
            Assert.Contains(expected, ifc);
            Assert.Equal(version == IfcProfile.Current, ifc.Contains("=IFCWALL("));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PlainIfcWall_Current_IsTheBrepOfItsDecodedBodyNotAGrossExtrusion()
    {
        // A door cut that opens onto the end of the wall: the notch goes through the thickness, so the body is no vertical
        // extrusion and no pair of side profiles describes it. Every such IfcWall is written as the Brep of its body, the notch
        // included; the gross solid of the wall would lose the doorway.
        const double length = 3.8, thickness = 0.0656, height = 8.9, notchStart = 2.6, notchHeight = 6.9;
        double[] P(double x, double y, double z) => [x, y, z];
        double half = thickness / 2, joint = length - thickness;   // the joint with the next wall splits the back edges at 'joint'
        var faces = new List<GeoFace>
        {
            Face(P(length, -half, height), P(0, -half, height), P(0, -half, notchHeight), P(notchStart, -half, notchHeight), P(notchStart, -half, 0), P(length, -half, 0)),
            Face(P(0, half, height), P(joint, half, height), P(length, half, height), P(length, half, 0), P(joint, half, 0), P(notchStart, half, 0), P(notchStart, half, notchHeight), P(0, half, notchHeight)),
            Face(P(length, half, 0), P(length, -half, 0), P(notchStart, -half, 0), P(notchStart, half, 0), P(joint, half, 0)),
            Face(P(0, -half, height), P(length, -half, height), P(length, half, height), P(joint, half, height), P(0, half, height)),
            Face(P(length, -half, height), P(length, -half, 0), P(length, half, 0), P(length, half, height)),
            Face(P(notchStart, -half, notchHeight), P(notchStart, half, notchHeight), P(notchStart, half, 0), P(notchStart, -half, 0)),
            Face(P(notchStart, half, notchHeight), P(notchStart, -half, notchHeight), P(0, -half, notchHeight), P(0, half, notchHeight)),
            Face(P(0, -half, height), P(0, half, height), P(0, half, notchHeight), P(0, -half, notchHeight)),
        };
        var geometry = new GeometryData
        {
            Faces = faces, BoundingBox = [0, -half, 0, length, half, height],
            BrepSolids = [new GeoBrepSolid { FaceIndices = Enumerable.Range(0, faces.Count).ToList(), IsClosed = true }],
        };
        var wall = new Element
        {
            Id = 43, TypeId = 44, CategoryId = -2000011, Category = "OST_Walls", Name = "Wall with a doorway at its end", References = [99],
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.Integer, Value = 99 },
                new Parameter { Id = -1001000, StorageType = StorageType.Double, Value = thickness },
                new Parameter { Id = -1001105, StorageType = StorageType.Double, Value = height },
            ],
            Location = new LocationCurve { CurveType = "Line", StartPoint = [0, 0, 0], EndPoint = [length, 0, 0] },
            BoundingBox = [0, -half, 0, length, half, height], Geometry = geometry,
        };
        var info = new RevitFileInfo();
        info.Elements.Add(wall);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-plain-wall-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = IfcProfile.Current }.Export(info, path);
            string ifc = File.ReadAllText(path);
            Assert.Contains("=IFCWALL(", ifc);
            var brep = System.Text.RegularExpressions.Regex.Matches(ifc, @"=IFCFACETEDBREP\(");
            Assert.Single(brep);
            Assert.Contains("=IFCCLOSEDSHELL((", ifc);
            var shells = System.Text.RegularExpressions.Regex.Matches(ifc, @"=IFCCLOSEDSHELL\(\(([^)]*)\)\)")
                .Select(match => match.Groups[1].Value.Split(',').Length).ToList();
            Assert.Equal(new[] { 8 }, shells);
            Assert.DoesNotContain("=IFCEXTRUDEDAREASOLID(", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static GeoFace Face(params double[][] ring) => new() { OuterVertices = ring.Select(point => (double[])point.Clone()).ToList() };

    /// <summary>A body of two solids: <paramref name="firstFaces"/> (node 7) and a leaning box (node 8) beside it.</summary>
    private static GeometryData TwoSolids(List<GeoFace> firstFaces)
    {
        foreach (var face in firstFaces) face.GeometryNode = 7;
        var second = Prism([[20, 0], [21, 0], [21, 1], [20, 1]], 0, 1);
        foreach (var face in second) face.GeometryNode = 8;
        second[2].Normal = [Math.Sqrt(1 - 0.01), 0, 0.1];
        var geometry = new GeometryData { Faces = [.. firstFaces, .. second] };
        geometry.BrepSolids =
        [
            new() { IsClosed = true, FaceIndices = Enumerable.Range(0, firstFaces.Count).ToList() },
            new() { IsClosed = true, FaceIndices = Enumerable.Range(firstFaces.Count, second.Count).ToList() },
        ];
        return geometry;
    }

    [Fact]
    public void WallOfSeveralSolids_IsStandardCaseWhenItsFirstSolidIsSimple_WhateverTheOthersAre()
    {
        var geometry = TwoSolids(Prism(Rectangle, 0, 3));
        // A tilted inactive face of the node of the first solid: the full test fails, the first solid's own faces pass.
        geometry.InactiveFaces = [new() { GeometryNode = 7, Normal = [0, 0.11, 0.99] }];
        Assert.False(WallExtrusion.IsSimpleRectangularWall(geometry.Faces!.Take(6).ToList(), null, geometry.InactiveFaces));
        Assert.True(WallExtrusion.IsStandardCaseBody(geometry));
    }

    [Fact]
    public void WallOfSeveralSolids_IsAnIfcWallWhenItsFirstSolidIsNotSimple()
    {
        var perforated = Prism(Rectangle, 0, 3);
        perforated[2].InnerVertices = [[[2, 0, 1], [3, 0, 1], [3, 0, 2], [2, 0, 2]]];
        Assert.False(WallExtrusion.IsStandardCaseBody(TwoSolids(perforated)));
    }

    [Fact]
    public void CurvedFaceOfAnotherSolid_DoesNotStopTheFirstSolidFromBeingSimple()
    {
        var geometry = TwoSolids(Prism(Rectangle, 0, 3));
        geometry.MeshNodes =
        [
            new(7, 0, [], 1),
            new(8, 1, [new() { SurfaceKind = RvtIO.Geometry.Meshing.SurfaceKind.Cylinder, Loops = [] }], 1),
        ];
        Assert.True(WallExtrusion.IsStandardCaseBody(geometry));
    }

    [Fact]
    public void InactiveFaceStillCountsForASingleSolidWallButNotForTheFirstOfSeveral()
    {
        var single = Prism(Rectangle, 0, 3);
        foreach (var face in single) face.GeometryNode = 7;
        var inactive = new GeoFace { GeometryNode = 7, Normal = [0, 0.11, 0.99] };
        Assert.False(WallExtrusion.IsSimpleRectangularWall(single, null, [inactive]));
        var several = TwoSolids(Prism(Rectangle, 0, 3));
        several.InactiveFaces = [inactive];
        Assert.True(WallExtrusion.IsStandardCaseBody(several));
    }

    [Fact]
    public void SimpleBody_StaysStandardCaseWithSeveralSolids()
    {
        var simple = Prism(Rectangle, 0, 3);
        Assert.True(WallExtrusion.IsStandardCaseBody(Body(simple, solids: 2)));
    }

    [Theory]
    [InlineData(IfcProfile.Current, "=IFCWALL(")]
    [InlineData(IfcProfile.Legacy, "=IFCWALLSTANDARDCASE(")]
    public void WallWhoseBodyIsNotAVerticalExtrusion_UsesTheRuleOfTheReferenceVersion(IfcProfile version, string expected)
    {
        var geometry = new GeometryData { Faces = Prism(MiteredEnds, 0, 3, frameAngleDegrees: 37) };
        geometry.BrepSolids = [new GeoBrepSolid { FaceIndices = Enumerable.Range(0, geometry.Faces.Count).ToList() }];
        var wall = new Element
        {
            Id = 43,
            TypeId = 44,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Name = "Rotated wall",
            References = [99],
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.Integer, Value = 99 },
                new Parameter { Id = -1001000, StorageType = StorageType.Double, Value = 1.0 },
                new Parameter { Id = -1001105, StorageType = StorageType.Double, Value = 3.0 },
            ],
            Location = new LocationCurve { CurveType = "Line", StartPoint = [0, 0, 0], EndPoint = [10, 0, 0] },
            BoundingBox = [0, 0, 0, 11, 1, 3],
            Geometry = geometry,
        };
        var info = new RevitFileInfo();
        info.Elements.Add(wall);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-wall-class-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            string ifc = File.ReadAllText(path);
            Assert.Contains(expected, ifc);
            Assert.Equal(version == IfcProfile.Current, ifc.Contains("=IFCWALL("));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>A wall seen from its long side: the elevation profile (x, z) extruded over y in [0, 1].</summary>
    private static List<GeoFace> ElevationExtrusion(double[][] profile)
    {
        var faces = new List<GeoFace>
        {
            new() { Origin = [profile[0][0], 0, profile[0][1]], Normal = [0, -1, 0],
                OuterVertices = profile.Select(p => new[] { p[0], 0, p[1] }).ToList() },
            new() { Origin = [profile[0][0], 1, profile[0][1]], Normal = [0, 1, 0],
                OuterVertices = profile.AsEnumerable().Reverse().Select(p => new[] { p[0], 1, p[1] }).ToList() },
        };
        for (int i = 0; i < profile.Length; i++)
        {
            var p = profile[i];
            var q = profile[(i + 1) % profile.Length];
            double dx = q[0] - p[0], dz = q[1] - p[1], length = Math.Sqrt(dx * dx + dz * dz);
            faces.Add(new GeoFace
            {
                Origin = [p[0], 0, p[1]],
                Normal = [dz / length, 0, -dx / length],
                OuterVertices = [[p[0], 0, p[1]], [q[0], 0, q[1]], [q[0], 1, q[1]], [p[0], 1, p[1]]],
            });
        }
        return faces;
    }

    [Fact]
    public void StandardCaseWithANotchedBoundary_IsWrittenAsAxisAndSweptSolidForCurrent()
    {
        // A door opening that reaches the floor: the decoded side profiles are notched, but the wall is written as an
        // extrusion with an Axis, and the opening as an IfcOpeningElement.
        var geometry = new GeometryData { Faces = ElevationExtrusion([[0, 0], [2, 0], [2, 2], [3, 2], [3, 0], [6, 0], [6, 3], [0, 3]]) };
        geometry.BrepSolids = [new GeoBrepSolid { FaceIndices = Enumerable.Range(0, geometry.Faces.Count).ToList(), IsClosed = true }];
        var wall = new Element
        {
            Id = 43,
            TypeId = 44,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Name = "Wall with a doorway",
            References = [99],
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.Integer, Value = 99 },
                new Parameter { Id = -1001000, StorageType = StorageType.Double, Value = 1.0 },
                new Parameter { Id = -1001105, StorageType = StorageType.Double, Value = 3.0 },
            ],
            Location = new LocationCurve { CurveType = "Line", StartPoint = [0, 0.5, 0], EndPoint = [6, 0.5, 0] },
            BoundingBox = [0, 0, 0, 6, 1, 3],
            Geometry = geometry,
        };
        Assert.True(IfcExporter.TryBuildStraightWallBoundary(wall, out _));
        var info = new RevitFileInfo();
        info.Elements.Add(wall);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-wall-notch-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = IfcProfile.Current }.Export(info, path);
            string ifc = File.ReadAllText(path);
            Assert.Contains("=IFCWALLSTANDARDCASE(", ifc);
            Assert.Contains("'Axis'", ifc);
            Assert.Contains("'SweptSolid'", ifc);
            Assert.DoesNotContain("'Brep'", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
