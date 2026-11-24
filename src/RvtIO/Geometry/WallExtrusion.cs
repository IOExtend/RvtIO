using RvtIO.Model;

namespace RvtIO.Geometry;

/// <summary>
/// The two geometric tests the current profile wall exporter applies to a wall body before it writes an IfcWallStandardCase:
/// the "simple rectangular wall" check (the body is re-created as a thickened axis extrusion) and the search for a
/// vertical extrusion among the planar faces of the body (two parallel faces of equal area, perpendicular faces
/// without holes, and end-face frame corners that span the bounding box of the solid).
/// </summary>
internal static partial class WallExtrusion
{
    private const double AngleTolerance = 1e-9;
    private const double VerticalAreaTolerance = 1e-5;
    private const double FaceAreaTolerance = 1e-4;
    // shortcut: the polygon areas of the top and bottom of a curved wall differ by the tessellation (about 0.5%),
    // upgrade by adding the circular segments of the arc edges.
    private const double CurvedAreaRelativeTolerance = 0.01;
    private const double PlaneTolerance = 1e-9;
    // Vertex tolerance, feet.
    private const double VertexTolerance = 0.0005233832795;
    // Short curve tolerance, feet.
    internal const double ShortCurveTolerance = 0.0025602645572916664;

    /// <summary>The wall is an IfcWallStandardCase when its body is "simple rectangular" (the body is then re-created as a
    /// thickened axis extrusion) or, for a single solid, when a vertical extrusion is found.
    /// Null when there is no decoded BRep to judge.</summary>
    /// <remarks>
    /// A decoded BRep that is not a closed shell is not complete: opening reveals can be missing or collapsed, so a failing
    /// area pairing or a hole in a vertical face does not confirm that the real solid fails the same test. For such a body
    /// only the failures that do not depend on reveals or on the order of several solids make the wall an IfcWall.
    /// A single closed shell is built from the Faces' own EdgeLoops, so the test applies literally.
    /// A wall of several solids is judged by its first solid alone: it is standard-case when that solid is "simple rectangular"
    /// on its own faces and a vertical extrusion; the number of solids is not checked.
    /// shortcut: a hole in a vertical face of an open shell counts as passing the test, upgrade when the reveals of an
    /// open shell are decoded; the first solid is the first one of the decoded body, which is not validated to be the first of the exporter.
    /// </remarks>
    internal static bool? IsStandardCaseBody(GeometryData? geometry, Parsing.TfBlockDeserializer.OwnWallHelperInput? helper = null,
        double? trimmedAxisLength = null)
    {
        if (geometry?.ReadFaces is not { Count: > 0 } faces || faces.Any(face => face.OuterVertices is not { Count: >= 3 })) return null;
        var solids = geometry.BrepSolids;
        var first = solids is { Count: > 0 }
            ? solids[0].FaceIndices.Where(index => (uint)index < (uint)faces.Count).Select(index => faces[index]).ToList()
            : faces;
        if (first.Count == 0) return null;
        // The trimmed axis is thickened into a loop of four lines; a wall whose axis is shorter than
        // SHORT_CURVE_TOL gets no such loop and goes through the body route like any other wall.
        bool axisFits = trimmedAxisLength is not { } axisLength || axisLength >= ShortCurveTolerance;
        // (a body with a face that is not planar is refused; a round Face the dense shell lacks is in the face meshes)
        // (only the node of the first solid counts: a curved face of another solid of the wall does not stop its first one)
        bool roundFace = first.Select(face => face.GeometryNode).Where(node => node > 0).Distinct().ToArray() is { Length: > 0 } firstNodes
            ? geometry.HasCurvedFace(firstNodes) : geometry.HasCurvedFace();
        if (axisFits && !roundFace && IsSimpleRectangularWall(first, helper, geometry.InactiveFaces)) return true;
        if (HasTiltedFace(first)) return false;
        // Several solids: the first one decides, on its own faces only (inactive faces and other solids are not looked at).
        // shortcut: unproven asymmetry. A single-solid wall counts its inactive faces (reference data need that), a multi-solid one
        // must not (reference data need that too); the reason is not known, upgrade when the per-solid geometry of the reference exporter is explained.
        if (solids is { Count: > 1 })
            return IsSimpleRectangularWall(first, helper) && TryFindVerticalExtrusion(first, out _, exactCylinders: true);
        var edgeBounds = first.Any(IsVerticalCylinderPatch) && first.All(face => face.MeshEdgeBounds is { Length: 6 })
            ? first.Select(face => face.MeshEdgeBounds!).ToList() : null;
        if (TryFindVerticalExtrusion(first, out string reason, exactCylinders: true, edgeBounds)) return true;
        // (a Geometry node the face meshes cover is complete as well: every Face of it is decoded, the round ones that the dense
        // shell lacks included)
        bool closedShell = solids is { Count: 1 } && solids[0].IsClosed || geometry.MeshNodeCount == 1;
        return reason == "tilted-or-opening" && !closedShell;
    }

    /// <summary>Whether the outer loop of a face has a straight edge shorter than SHORT_CURVE_TOL. Faces with circular edges
    /// are not judged: their polygons are tessellations, not the edges of the serialized loop.</summary>
    private static bool HasShortCurve(MeshFace face) =>
        face.Regions.Any(region => region.MeshArcs is not { Count: > 0 } && region.OuterVertices is { Count: >= 3 } ring
            && Enumerable.Range(0, ring.Count).Any(index =>
            {
                var a = ring[index]; var b = ring[(index + 1) % ring.Count];
                double distance = Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
                return distance > 0 && distance < ShortCurveTolerance;
            }));

    private static bool HasTiltedFace(IReadOnlyList<GeoFace> faces) =>
        // A cylinder's tessellated quads can be skew; its analytic axis determines tilt.
        faces.Any(face => { if (face.MeshEdgeBounds != null && IsVerticalCylinderPatch(face)) return false;
            var n = UnitNormal(face); return !ParallelToZ(n) && !PerpendicularToZ(n); });

    /// <summary>Planar area of a face: the outer loop (inner loops are not subtracted). Where the circular edges of the face are known and
    /// all found in its ring, the samples of each arc give way to the arc: two faces bounded by the same circles are then equal
    /// whatever way their edges were split and sampled.</summary>
    internal static double FaceArea(GeoFace face) => ExactFaceArea(face, out _);

    /// <summary><see cref="FaceArea"/>, and whether it is the area of the face itself: its ring has no arc, or every arc of it was found.</summary>
    internal static double ExactFaceArea(GeoFace face, out bool exact)
    {
        exact = face.MeshArcs is not { Count: > 0 };
        if (face.OuterVertices is { Count: >= 3 } outer && face.MeshArcs is { Count: > 0 } arcs
            && ArcRuns.FindArcRunsCurrent(outer, arcs) is var runs && runs.Count == arcs.Count)
        {
            exact = true;
            return ExactLoopArea(outer, runs);
        }
        return LoopArea(face.OuterVertices);
    }

    private static double ExactLoopArea(IReadOnlyList<double[]> ring, IReadOnlyList<ArcRuns.ArcRunCurrent> runs)
    {
        double[] total = [0, 0, 0];
        void Add(double[] a, double[] b, double sign)
        {
            total[0] += sign * 0.5 * (a[1] * b[2] - a[2] * b[1]);
            total[1] += sign * 0.5 * (a[2] * b[0] - a[0] * b[2]);
            total[2] += sign * 0.5 * (a[0] * b[1] - a[1] * b[0]);
        }
        for (int i = 0; i < ring.Count; i++) Add(ring[i], ring[(i + 1) % ring.Count], 1);
        foreach (var run in runs)
        {
            // the samples of the run give way to the arc: the area vector of the arc less that of the polyline, both as the ring walks them
            for (int step = 0; step < run.Steps; step++)
                Add(ring[(run.First + step) % ring.Count], ring[(run.First + step + 1) % ring.Count], -1);
            var arc = run.Arc;
            double t0 = run.Reversed ? arc.Last : arc.First, t1 = run.Reversed ? arc.First : arc.Last;
            double[] u0 = arc.UnitAt(t0), u1 = arc.UnitAt(t1), c = arc.Centre;
            double[] d = [u1[0] - u0[0], u1[1] - u0[1], u1[2] - u0[2]];
            double[] normal = [arc.X[1] * arc.Y[2] - arc.X[2] * arc.Y[1], arc.X[2] * arc.Y[0] - arc.X[0] * arc.Y[2], arc.X[0] * arc.Y[1] - arc.X[1] * arc.Y[0]];
            double r = arc.Radius;
            total[0] += 0.5 * r * (c[1] * d[2] - c[2] * d[1]) + 0.5 * r * r * (t1 - t0) * normal[0];
            total[1] += 0.5 * r * (c[2] * d[0] - c[0] * d[2]) + 0.5 * r * r * (t1 - t0) * normal[1];
            total[2] += 0.5 * r * (c[0] * d[1] - c[1] * d[0]) + 0.5 * r * r * (t1 - t0) * normal[2];
        }
        return Math.Sqrt(total[0] * total[0] + total[1] * total[1] + total[2] * total[2]);
    }

    internal static double LoopArea(IReadOnlyList<double[]>? points)
    {
        if (points is not { Count: >= 3 }) return 0;
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            x += a[1] * b[2] - a[2] * b[1];
            y += a[2] * b[0] - a[0] * b[2];
            z += a[0] * b[1] - a[1] * b[0];
        }
        return 0.5 * Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>One face: a serialized Face, which may have been decoded into several coplanar regions.</summary>
    private sealed class MeshFace
    {
        public required List<GeoFace> Regions { get; init; }
        public GeoFace First => Regions[0];
        public double[] Normal => UnitNormal(First);
        public bool Curved => Regions.Any(region => region.CurvedSurface);
        public bool HasOpenings => Regions.Any(region => region.InnerVertices is { Count: > 0 });
        /// <summary>The area given by the quadrature of the Face when it has a curved Edge (see <see cref="Geometry.Meshing.FaceArea"/>).</summary>
        public double? QuadratureArea => Regions.Select(region => region.QuadratureArea).FirstOrDefault(area => area != null);
        public double Area => QuadratureArea ?? Regions.Sum(FaceArea);
        /// <summary>True when <see cref="Area"/> is the area of the face itself (see <see cref="ExactFaceArea"/>).</summary>
        public bool AreaIsExact => QuadratureArea != null || Regions.All(region => { ExactFaceArea(region, out bool exact); return exact; });
        /// <summary>The net area of the face: the outer loops less the openings (inner loops).</summary>
        public double NetArea => QuadratureArea ?? Area - Regions.Sum(region => (region.InnerVertices ?? []).Sum(LoopArea));
    }

    private static List<MeshFace> MergeRegions(IEnumerable<GeoFace> faces)
    {
        var result = new List<MeshFace>();
        var bySource = new Dictionary<uint, MeshFace>();
        foreach (var face in faces)
        {
            if (face.SourceFace != 0 && bySource.TryGetValue(face.SourceFace, out var merged))
            {
                merged.Regions.Add(face);
                continue;
            }
            var meshFace = new MeshFace { Regions = [face] };
            if (face.SourceFace != 0) bySource[face.SourceFace] = meshFace;
            result.Add(meshFace);
        }
        return result;
    }

    private static double[] UnitNormal(GeoFace face)
    {
        var n = face.Normal;
        double length = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        return length == 0 ? [0, 0, 0] : [n[0] / length, n[1] / length, n[2] / length];
    }

    private static bool ParallelToZ(double[] n) => Math.Sqrt(n[0] * n[0] + n[1] * n[1]) < AngleTolerance && Math.Abs(n[2]) > 0.5;
    private static bool PerpendicularToZ(double[] n) => Math.Abs(n[2]) < AngleTolerance;

    private static bool Parallel(double[] a, double[] b)
    {
        double x = a[1] * b[2] - a[2] * b[1], y = a[2] * b[0] - a[0] * b[2], z = a[0] * b[1] - a[1] * b[0];
        return Math.Sqrt(x * x + y * y + z * z) < AngleTolerance;
    }

    /// <summary>The simple rectangular wall test: the extrusion helper of the wall, when it is complete, has a
    /// profile loop of four curves, every face of the first solid is planar with a vertical or horizontal normal, and the
    /// vertical faces cancel pairwise by area.</summary>
    internal static bool IsSimpleRectangularWall(IReadOnlyList<GeoFace> faces, Parsing.TfBlockDeserializer.OwnWallHelperInput? helper,
        IReadOnlyList<GeoFace>? inactiveFaces = null)
    {
        if (!HelperProfileIsQuadrilateral(helper)) return false;
        // Inactive faces belong to their serialized Geometry node, not to another solid or a display branch.
        if (inactiveFaces is { Count: > 0 } && faces.Select(face => face.GeometryNode).Distinct().ToArray() is [var node] && node > 0)
            return IsSimpleRectangularWall(faces.Concat(inactiveFaces.Where(face => face.GeometryNode == node)).ToList());
        return IsSimpleRectangularWall(faces);
    }

    /// <summary>The first test of the simple rectangular check: a complete ElementExtrusionHelper whose definition (outer)
    /// loop has a number of curves other than four makes the wall not simple, whatever its faces are. The outer loop is the
    /// first one of the helper. A wall without a helper, or one that cannot be read, is not rejected here.
    /// shortcut: the loop type is not decoded, so the loops after the first count as holes; upgrade when the type is read.</summary>
    internal static bool HelperProfileIsQuadrilateral(Parsing.TfBlockDeserializer.OwnWallHelperInput? helper) =>
        helper is not { Present: true, Complete: true, LoopCurveCounts: { Count: > 0 } counts } || counts[0] == 4;

    /// <summary>The face tests of the simple rectangular check: every face of the first solid is planar with a vertical or
    /// horizontal normal, and the vertical faces cancel pairwise by area.</summary>
    internal static bool IsSimpleRectangularWall(IReadOnlyList<GeoFace> faces)
    {
        var merged = MergeRegions(faces);
        var holeCorners = HoleCorners(merged);
        var unpaired = new List<(double Area, bool TouchesHole)>();
        foreach (var face in merged)
        {
            if (face.Curved) return false;
            var n = face.Normal;
            if (!(ParallelToZ(n) || PerpendicularToZ(n))) return false;
            if (!PerpendicularToZ(n)) continue;
            double area = face.NetArea;
            if (Math.Abs(area) < 1e-10) continue;
            int match = unpaired.FindIndex(other => Math.Abs(other.Area - area) <= VerticalAreaTolerance);
            if (match >= 0) unpaired.RemoveAt(match); else unpaired.Add((area, TouchesHole(face, holeCorners)));
        }
        // shortcut: exact areas are paired at 1e-5, but the decoded reveals of an opening carry float-level noise
        // (two reveals of 5.7156 ft2 can differ by 1.04e-5), so faces that touch a hole pair at FaceAreaTolerance;
        // upgrade by reading the double coordinates of the serialized vertices.
        for (int index = 0; index < unpaired.Count; index++)
        {
            if (!unpaired[index].TouchesHole) continue;
            int partner = unpaired.FindIndex(index + 1, other => Math.Abs(other.Area - unpaired[index].Area) <= FaceAreaTolerance);
            if (partner < 0) continue;
            unpaired.RemoveAt(partner);
            unpaired.RemoveAt(index--);
        }
        return unpaired.Count == 0;
    }

    private static (long, long, long) CornerKey(double[] p) =>
        ((long)Math.Round(p[0] * 1e4), (long)Math.Round(p[1] * 1e4), (long)Math.Round(p[2] * 1e4));

    private static HashSet<(long, long, long)> HoleCorners(List<MeshFace> faces)
    {
        var corners = new HashSet<(long, long, long)>();
        foreach (var face in faces)
            foreach (var region in face.Regions)
                foreach (var ring in region.InnerVertices ?? [])
                    foreach (var p in ring)
                        corners.Add(CornerKey(p));
        return corners;
    }

    /// <summary>A face other than the host of the hole that shares at least two corners with a hole loop.</summary>
    private static bool TouchesHole(MeshFace face, HashSet<(long, long, long)> holeCorners)
    {
        if (holeCorners.Count == 0 || face.HasOpenings) return false;
        int shared = 0;
        foreach (var region in face.Regions)
            foreach (var p in region.OuterVertices ?? [])
                if (holeCorners.Contains(CornerKey(p)) && ++shared >= 2) return true;
        return false;
    }

    /// <summary>The extrusion search for the Z axis on one solid: true when the body is
    /// a vertical extrusion; otherwise <paramref name="reason"/> names the first failing step. With
    /// <paramref name="exactCylinders"/> the bounding box of the solid follows the exact vertical cylinders of the body,
    /// instead of the polygons that tessellate them (see <see cref="AddCylinderExtremes"/>).</summary>
    internal static bool TryFindVerticalExtrusion(IReadOnlyList<GeoFace> solidFaces, out string reason, bool exactCylinders = false,
        IReadOnlyList<double[]>? sourceEdgeBounds = null)
    {
        var planar = MergeRegions(solidFaces).Where(face => !face.Curved).ToList();
        int count = planar.Count;
        if (count < 2) { reason = "few-faces"; return false; }

        var normals = planar.Select(face => face.Normal).ToArray();
        var areas = planar.Select(face => face.Area).ToArray();
        var hasOpenings = planar.Select(face => face.HasOpenings).ToArray();
        var skip = new bool[count];

        var body = new Extents();
        if (sourceEdgeBounds != null)
            foreach (var box in sourceEdgeBounds) { body.Add(box[..3]); body.Add(box[3..]); }
        else
        {
            foreach (var face in solidFaces)
            {
                foreach (var p in face.OuterVertices ?? []) body.Add(p);
                foreach (var ring in face.InnerVertices ?? []) foreach (var p in ring) body.Add(p);
            }
            if (exactCylinders) AddCylinderExtremes(body, solidFaces);
        }
        // The top and the bottom of a curved wall are tessellated independently, so their exact areas are compared.
        bool curved = exactCylinders && solidFaces.Any(IsVerticalCylinderPatch);
        // (two caps whose circles are known and found in their rings are compared by their exact areas)
        var exactAreas = planar.Select(face => face.QuadratureArea != null || face.AreaIsExact && face.Regions.Any(region => region.MeshArcs is { Count: > 0 })).ToArray();
        double AreaTolerance(int i, int j) => curved && !(exactAreas[i] && exactAreas[j])
            ? Math.Max(FaceAreaTolerance, CurvedAreaRelativeTolerance * Math.Max(areas[i], areas[j])) : FaceAreaTolerance;

        var facesExtents = new Extents();
        var endPlanes = new List<double>();
        int pairs = 0;
        double firstLength = 0;
        string failure = "no-pair";
        bool lengthsDiffer = false, stop = false;
        MeshFace? profileFace = null;

        for (int i = 0; i < count - 1 && !stop; i++)
        {
            if (skip[i] || !ParallelToZ(normals[i])) { skip[i] = true; continue; }

            bool rejected = false;
            int j = i + 1;
            for (; j < count; j++)
            {
                if (skip[j]) continue;
                if (!Parallel(normals[i], normals[j]) || Math.Abs(areas[i] - areas[j]) > AreaTolerance(i, j)) continue;
                for (int k = 0; k < count; k++)
                {
                    if (k == i || k == j) continue;
                    if ((PerpendicularToZ(normals[k]) && hasOpenings[k])
                        || (!PerpendicularToZ(normals[k]) && !ParallelToZ(normals[k])))
                        rejected = true;
                }
                break;
            }
            if (rejected) { failure = "tilted-or-opening"; continue; }
            if (j == count) continue;

            skip[i] = skip[j] = true;
            double zi = planar[i].First.Origin[2], zj = planar[j].First.Origin[2];
            double length = Math.Abs(zj - zi);
            if (length < PlaneTolerance) continue;
            // The loops of the lower face of the pair are the profile of the extrusion.
            if (pairs == 0) profileFace = zi <= zj ? planar[i] : planar[j];

            if (pairs > 0)
            {
                bool Known(double z) => Math.Abs(z - endPlanes[0]) < PlaneTolerance || Math.Abs(z - endPlanes[1]) < PlaneTolerance;
                if (!(Known(zi) && Known(zj))) { failure = "plane-mismatch"; pairs = 0; stop = true; continue; }
                if (Math.Abs(length - firstLength) > PlaneTolerance) lengthsDiffer = true;
            }
            else
            {
                endPlanes = [zi, zj];
                firstLength = length;
            }
            pairs++;
            AddEnvelopeCorners(facesExtents, planar[i], sourceEdgeBounds != null);
            AddEnvelopeCorners(facesExtents, planar[j], sourceEdgeBounds != null);
        }

        if (pairs == 0) { reason = failure; return false; }
        if (!facesExtents.IsEqualTo(body, VertexTolerance)) { reason = "bbox"; return false; }
        if (pairs > 1 && lengthsDiffer) { reason = "multi-length"; return false; }
        // The profile loop built from the face leaves out every curve shorter than SHORT_CURVE_TOL, the broken loop has no
        // orientation and no plane, and no solid is written.
        if (profileFace != null && HasShortCurve(profileFace)) { reason = "short-curve"; return false; }
        reason = "ok";
        return true;
    }

    internal static bool IsVerticalCylinderPatch(GeoFace face) =>
        face.CurvedSurface && face.SourceFace != 0
        && face.AnalyticCylinderOrigin is { Length: >= 3 } && face.AnalyticCylinderAxis is { Length: >= 3 } axis
        && Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1]) < AngleTolerance
        && Math.Abs(Math.Abs(axis[2]) - 1) < AngleTolerance && face.AnalyticCylinderRadius > 0;

    /// <summary>The bounding box of a solid follows its surfaces, while the planar faces of a curved wall are bounded
    /// by the polygon that tessellates the arc: where a vertical cylinder passes through the direction of a world axis
    /// between two polygon vertices, the exact extreme lies outside every vertex by the sagitta of that facet.
    /// The extremes of each cylinder (its patches share one source Face) are added to <paramref name="body"/> at both ends
    /// of its height.
    /// shortcut: the arc of a cylinder is taken as the complement of the largest gap between the angles of its patch
    /// vertices, so a single patch spanning more than 180 degrees is read as its complement; upgrade by reading the
    /// angular range of the serialized Face.</summary>
    private static void AddCylinderExtremes(Extents body, IReadOnlyList<GeoFace> solidFaces)
    {
        var cylinders = solidFaces.Where(IsVerticalCylinderPatch).GroupBy(face => face.SourceFace);
        foreach (var cylinder in cylinders)
        {
            var origin = cylinder.First().AnalyticCylinderOrigin!;
            double radius = cylinder.First().AnalyticCylinderRadius;
            var points = cylinder.SelectMany(face => face.OuterVertices ?? []).ToList();
            if (points.Count < 2) continue;
            var angles = points.Select(p => Math.Atan2(p[1] - origin[1], p[0] - origin[0])).OrderBy(a => a).ToList();
            // The patches cover the arc that is left when the largest gap between consecutive angles is removed.
            int gapAfter = 0;
            double gap = -1;
            for (int i = 0; i < angles.Count; i++)
            {
                double next = i + 1 < angles.Count ? angles[i + 1] : angles[0] + 2 * Math.PI;
                if (next - angles[i] > gap) { gap = next - angles[i]; gapAfter = i; }
            }
            double start = angles[(gapAfter + 1) % angles.Count];
            double span = 2 * Math.PI - gap;
            double zMin = points.Min(p => p[2]), zMax = points.Max(p => p[2]);
            for (int quarter = 0; quarter < 4; quarter++)
            {
                double direction = quarter * Math.PI / 2;
                double offset = ((direction - start) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
                if (offset > span) continue;
                foreach (double z in new[] { zMin, zMax })
                    body.Add([origin[0] + radius * Math.Cos(direction), origin[1] + radius * Math.Sin(direction), z]);
            }
        }
    }

    /// <summary>Only the two opposite corners of the face's (u, v) envelope in the serialized plane frame are added; for a
    /// plane whose axes are not aligned with the world axes they do not span the face's world extents.</summary>
    private static void AddEnvelopeCorners(Extents extents, MeshFace face, bool useSourceEnvelope = false)
    {
        if (useSourceEnvelope && face.First.MeshPlaneEnvelopeCorners is { Length: 6 } corners)
        {
            extents.Add(corners[..3]); extents.Add(corners[3..]);
            return;
        }
        var outline = face.Regions.SelectMany(region => region.OuterVertices ?? []).ToList();
        if (outline.Count == 0) return;
        if (face.First.PlaneXAxis is not { } x || face.First.PlaneYAxis is not { } y)
        {
            foreach (var p in outline) extents.Add(p);
            return;
        }
        var origin = outline[0];
        double minU = double.MaxValue, minV = double.MaxValue, maxU = double.MinValue, maxV = double.MinValue;
        foreach (var p in outline)
        {
            double dx = p[0] - origin[0], dy = p[1] - origin[1], dz = p[2] - origin[2];
            double u = dx * x[0] + dy * x[1] + dz * x[2];
            double v = dx * y[0] + dy * y[1] + dz * y[2];
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
        }
        extents.Add([origin[0] + minU * x[0] + minV * y[0], origin[1] + minU * x[1] + minV * y[1], origin[2] + minU * x[2] + minV * y[2]]);
        extents.Add([origin[0] + maxU * x[0] + maxV * y[0], origin[1] + maxU * x[1] + maxV * y[1], origin[2] + maxU * x[2] + maxV * y[2]]);
    }

    private sealed class Extents
    {
        private readonly double[] _min = [double.MaxValue, double.MaxValue, double.MaxValue];
        private readonly double[] _max = [double.MinValue, double.MinValue, double.MinValue];
        private bool _any;

        public void Add(double[] p)
        {
            _any = true;
            for (int axis = 0; axis < 3; axis++)
            {
                _min[axis] = Math.Min(_min[axis], p[axis]);
                _max[axis] = Math.Max(_max[axis], p[axis]);
            }
        }

        public bool IsEqualTo(Extents other, double tolerance)
        {
            if (!_any || !other._any) return false;
            return Distance(_min, other._min) <= tolerance && Distance(_max, other._max) <= tolerance;
        }

        private static double Distance(double[] a, double[] b) =>
            Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
    }
}
