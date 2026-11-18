using RvtIO.Geometry;
using RvtIO.Model;
using static RvtIO.Geometry.BrepMeasures;

namespace RvtIO.Ifc;

/// <summary>The solid measures (volume, face areas) taken from the Revit solids of an
/// element, and the extrusion data found on a solid (two parallel faces of equal area, every other face parallel to their
/// normal), taken here from the decoded BRep shells in feet.</summary>
public partial class IfcExporter
{
    /// <summary>The net areas of the faces: one face per serialized Face handle, whereas the parser cuts a face
    /// into several regions (<see cref="GeoFace.SourceFace"/>); the regions of one face count as one.</summary>
    private static IEnumerable<double> FaceAreasCurrent(IEnumerable<GeoFace> faces)
    {
        var bySource = new Dictionary<(uint Face, int Node), double>();
        foreach (var face in faces)
        {
            double area = NetFaceArea(face) ?? 0;
            if (face.SourceFace == 0)
                yield return area;
            else
            {
                var key = (face.SourceFace, face.GeometryNode);
                bySource[key] = bySource.GetValueOrDefault(key) + area;
            }
        }
        foreach (double area in bySource.Values) yield return area;
    }

    /// <summary>The area of the largest planar face that stands vertically, summed over the shells of a wall
    /// (largest face of normal.z = 0 of each solid), in feet.</summary>
    internal static double LargestVerticalFaceAreaCurrent(IEnumerable<ShellInfo> shells) =>
        shells.Sum(shell => FaceAreasCurrent(shell.Faces.Where(face => !face.CurvedSurface && FaceUnitNormal(face) is { } n && Math.Abs(n[2]) < 1e-9))
            .DefaultIfEmpty(0).Max());

    /// <summary>An exact end face: a planar face that carries the area of the Revit face (<see cref="GeoFace.QuadratureArea"/>).</summary>
    private static bool IsExactCap(GeoFace face) => face.PlaneSurface && face.QuadratureArea != null && face.OuterVertices is { Count: >= 3 };

    /// <summary>Volume (m3) and surface area (m2) of the measured node whose faces are all exact end faces: a round solid lists its two
    /// planar end faces under the node that holds its integrals, while its curved side is listed as a tessellated node without integrals
    /// (which leaves <see cref="NodeSolidTotalsCurrent"/> unknown and only the open shell of the side to measure). Null unless exactly one
    /// node holds nothing but exact end faces (end faces in two nodes may each measure the whole solid) and it has a positive volume.</summary>
    internal static (double Volume, double Area)? CapNodeTotalsCurrent(GeometryData? geometry)
    {
        if (geometry?.NodeMeasures is not { Count: > 0 } measures || geometry.Faces.Count == 0) return null;
        var capNodes = geometry.Faces.GroupBy(face => face.GeometryNode).Where(group => group.All(IsExactCap)).Select(group => group.Key).ToList();
        if (capNodes is not [var node] || measures.FirstOrDefault(measure => measure.Node == node) is not { Volume: > 0 } taken) return null;
        return (taken.Volume * CubicFeetToCubicMetres, taken.Area * SquareFeetToSquareMetres);
    }

    /// <summary>Relative difference of the exact area of an end face from the area of its circle that a round column may show. Measured on
    /// the 270 end faces of the round columns of one sample: 1.4e-4 for most, 3.7e-3 at most (the file holds the exact area of some faces with a
    /// quadrature error). A regular polygon of 32 corners already differs by 6.4e-3 (24: 1.1e-2, 16: 2.5e-2, 12: 4.5e-2),
    /// so 5e-3 passes the measured faces and refuses the polygons.</summary>
    private const double RoundCapAreaTolerance = 5e-3;

    /// <summary>The vertical circular cylinder of a round solid, in feet: the distance of its two exact end faces and the radius of their
    /// circle. The end faces are the only exact planar faces, without holes, with at least eight corners, horizontal, the corners of each
    /// lie on a circle, both circles are the same one, the exact area of each is that of its circle to within
    /// <see cref="RoundCapAreaTolerance"/> (a polygon is no circle), no other face is a vertical plane surface of the file (a facet of a
    /// polygonal prism), and every other face has its corners on the side of that cylinder between the end faces (so no other solid is part of the geometry). The areas of the two end faces need not agree with each other.
    /// The distances are compared to a millionth of the radius, or to eight units in the last place of a 32-bit coordinate of the
    /// largest magnitude where that is more (mesh vertices are read as 32-bit numbers; the end faces of the sample are exact doubles
    /// up to 150 feet from the origin). Null for any other solid.</summary>
    internal static (double Depth, double Radius)? RoundCylinderCurrent(GeometryData? geometry)
    {
        if (geometry?.Faces is not { Count: > 0 } faces) return null;
        var caps = faces.Where(IsExactCap).ToList();
        if (caps.Count != 2 || caps.Any(cap => cap.InnerVertices is { Count: > 0 } || cap.OuterVertices!.Count < 8)) return null;
        float largest = (float)faces.SelectMany(face => face.OuterVertices ?? []).SelectMany(corner => corner).Select(Math.Abs).DefaultIfEmpty(0).Max();
        double slack = 8 * (MathF.BitIncrement(largest) - largest);
        if (CircleOfCorners(caps[0].OuterVertices!, slack) is not { } first || CircleOfCorners(caps[1].OuterVertices!, slack) is not { } second) return null;
        double tolerance = Math.Max(1e-6 * first.R, slack);
        if (Math.Abs(first.R - second.R) > tolerance || Math.Abs(first.X - second.X) > tolerance || Math.Abs(first.Y - second.Y) > tolerance) return null;
        double circleArea = Math.PI * first.R * first.R;
        if (caps.Any(cap => Math.Abs(cap.QuadratureArea!.Value / circleArea - 1) > RoundCapAreaTolerance)) return null;
        double z0 = caps[0].OuterVertices![0][2], z1 = caps[1].OuterVertices![0][2];
        if (caps.Any(cap => cap.OuterVertices!.Any(corner => Math.Abs(corner[2] - cap.OuterVertices![0][2]) > tolerance))) return null;
        double low = Math.Min(z0, z1), high = Math.Max(z0, z1);
        if (high - low < 1e-9) return null;
        // A side facet that lies on a plane surface of the file belongs to a polygonal prism, not to a cylinder: the only planar faces
        // besides the end faces are copies of them, which are horizontal.
        if (faces.Any(face => face.PlaneSurface && !IsExactCap(face) && face.OuterVertices is { Count: > 0 } ring && ring.Any(corner => Math.Abs(corner[2] - ring[0][2]) > tolerance))) return null;
        foreach (var face in faces)
            foreach (var corner in face.OuterVertices ?? [])
                if (Math.Abs(Math.Sqrt((corner[0] - first.X) * (corner[0] - first.X) + (corner[1] - first.Y) * (corner[1] - first.Y)) - first.R) > tolerance
                    || corner[2] < low - tolerance || corner[2] > high + tolerance)
                    return null;
        return (high - low, first.R);
    }

    /// <summary>The vertical circular prism of a round solid: a <see cref="RoundCylinderCurrent"/> that the extrusion calculation accepts
    /// (two parallel end faces of the same exact area). Null for any other solid.</summary>
    internal static (double Depth, double Radius)? RoundPrismCurrent(GeometryData? geometry) =>
        RoundCylinderCurrent(geometry) is { } cylinder && WallExtrusion.TryFindVerticalExtrusion(geometry!.Faces, out _, exactCylinders: true) ? cylinder : null;

    /// <summary>The circle in the XY plane through three corners of a ring, null unless every corner lies on it (to within a millionth of
    /// the radius, or <paramref name="slack"/> where that is more).</summary>
    private static (double X, double Y, double R)? CircleOfCorners(IReadOnlyList<double[]> ring, double slack)
    {
        var a = ring[0];
        var b = ring[ring.Count / 3];
        var c = ring[2 * ring.Count / 3];
        double d = 2 * (a[0] * (b[1] - c[1]) + b[0] * (c[1] - a[1]) + c[0] * (a[1] - b[1]));
        if (Math.Abs(d) < 1e-12) return null;
        double a2 = a[0] * a[0] + a[1] * a[1], b2 = b[0] * b[0] + b[1] * b[1], c2 = c[0] * c[0] + c[1] * c[1];
        double x = (a2 * (b[1] - c[1]) + b2 * (c[1] - a[1]) + c2 * (a[1] - b[1])) / d;
        double y = (a2 * (c[0] - b[0]) + b2 * (a[0] - c[0]) + c2 * (b[0] - a[0])) / d;
        double radius = Math.Sqrt((a[0] - x) * (a[0] - x) + (a[1] - y) * (a[1] - y));
        return ring.All(p => Math.Abs(Math.Sqrt((p[0] - x) * (p[0] - x) + (p[1] - y) * (p[1] - y)) - radius) <= Math.Max(1e-6 * radius, slack)) ? (x, y, radius) : null;
    }

    private static double? MiddleHeightCurrent(IEnumerable<GeoFace> faces)
    {
        var heights = faces.Where(face => face.OuterVertices is { Count: > 0 }).SelectMany(face => face.OuterVertices!).Select(point => point[2]).ToList();
        return heights.Count == 0 ? null : (heights.Min() + heights.Max()) / 2;
    }

    /// <summary>The area of the planar faces that are not vertical and lie above the middle of the height of their shell (the top
    /// surface of a floor, sloped or not), in feet: the area of the surface of the solid, which is what Revit calls the computed host
    /// area of a floor. An arc of the outline is a run of chords here (the edges of the facets of a vertical cylinder); the circular
    /// segment over each chord is added where the arc bulges out of the face and taken off where it bulges into it
    /// (<see cref="ArcSegmentCorrectionCurrent"/>).</summary>
    internal static double TopSurfaceAreaCurrent(IEnumerable<ShellInfo> shells)
    {
        var all = shells as IReadOnlyList<ShellInfo> ?? shells.ToList();
        var arcs = VerticalCylinderChordsCurrent(all.SelectMany(shell => shell.Faces));
        return all.Sum(shell => MiddleHeightCurrent(shell.Faces) is not { } middle ? 0 : shell.Faces
            .Where(face => !face.CurvedSurface && FaceUnitNormal(face) is { } n && Math.Abs(n[2]) > 1e-9 && MeanHeightCurrent(face) >= middle)
            .Sum(face => (NetFaceArea(face) ?? 0) + ArcSegmentCorrectionCurrent(face, arcs) / Math.Abs(FaceUnitNormal(face)![2])));
    }

    private static (long, long, long, long) ChordKeyCurrent(double[] a, double[] b)
    {
        static long Cell(double value) => (long)Math.Round(value * 1e9);
        var (p, q) = (Cell(a[0]), Cell(a[1])).CompareTo((Cell(b[0]), Cell(b[1]))) <= 0 ? (a, b) : (b, a);
        return (Cell(p[0]), Cell(p[1]), Cell(q[0]), Cell(q[1]));
    }

    /// <summary>The chords (plan edges, by their end points) of the facets of vertical cylinders in a set of faces, with the centre
    /// and the radius of the circle (feet): the axis has to be vertical, the facet has to come with the serialized analytic
    /// cylinder.</summary>
    private static Dictionary<(long, long, long, long), (double Cx, double Cy, double R)> VerticalCylinderChordsCurrent(IEnumerable<GeoFace> faces)
    {
        var chords = new Dictionary<(long, long, long, long), (double, double, double)>();
        foreach (var face in faces)
        {
            if (!face.CurvedSurface || face.AnalyticCylinderOrigin is not { Length: >= 3 } origin || face.AnalyticCylinderAxis is not { Length: >= 3 } axis
                || !(face.AnalyticCylinderRadius > 0) || Math.Abs(Math.Abs(axis[2]) - 1) > 1e-9 || face.OuterVertices is not { Count: >= 4 } ring)
                continue;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                if (Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) > 1e-9)
                    chords[ChordKeyCurrent(a, b)] = (origin[0], origin[1], face.AnalyticCylinderRadius);
            }
        }
        return chords;
    }

    /// <summary>What the arcs add to the plan area of a face: for every edge of its rings that is the chord of a vertical cylinder, the
    /// circular segment over it, added where the arc bulges away from the face and taken off where it bulges into it; the segments
    /// of the holes count the other way round. Divided by the cosine of the slope it is the correction of the area of the face.</summary>
    private static double ArcSegmentCorrectionCurrent(GeoFace face, Dictionary<(long, long, long, long), (double Cx, double Cy, double R)> arcs)
    {
        if (arcs.Count == 0) return 0;
        double Ring(IReadOnlyList<double[]> ring)
        {
            double shoelace = 0, segments = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                shoelace += a[0] * b[1] - b[0] * a[1];
                if (!arcs.TryGetValue(ChordKeyCurrent(a, b), out var circle)) continue;
                double dx = b[0] - a[0], dy = b[1] - a[1], chord = Math.Sqrt(dx * dx + dy * dy);
                if (!(chord < 2 * circle.R)) continue;
                double angle = 2 * Math.Asin(chord / (2 * circle.R));
                double segment = circle.R * circle.R / 2 * (angle - Math.Sin(angle));
                // the arc bulges away from the centre: to the right of the chord when the middle of the chord is on the right of the centre
                double rightward = ((a[0] + b[0]) / 2 - circle.Cx) * dy - ((a[1] + b[1]) / 2 - circle.Cy) * dx;
                segments += rightward > 0 ? segment : -segment;
            }
            // a segment on the right of a counter-clockwise ring lies outside it, on the right of a clockwise ring inside it
            return shoelace >= 0 ? segments : -segments;
        }
        double correction = Ring(face.OuterVertices!);
        foreach (var hole in face.InnerVertices ?? []) correction -= Ring(hole);
        return correction;
    }

    /// <summary>The plan area of a room solid, in feet: the area of its horizontal faces above the middle of its height or of
    /// those below it, whichever is larger (a stepped floor or ceiling splits one of the two).</summary>
    internal static double? RoomPlanAreaCurrent(GeometryData boundary)
    {
        if (MiddleHeightCurrent(boundary.Faces) is not { } middle) return null;
        var horizontal = boundary.Faces.Where(face => !face.CurvedSurface && face.OuterVertices is { Count: >= 3 }
            && FaceUnitNormal(face) is { } n && Math.Abs(n[2]) > 1 - 1e-9 && NetFaceArea(face) is not null).ToList();
        if (horizontal.Count == 0) return null;
        return Math.Max(
            horizontal.Where(face => MeanHeightCurrent(face) > middle).Sum(face => NetFaceArea(face)!.Value),
            horizontal.Where(face => MeanHeightCurrent(face) < middle).Sum(face => NetFaceArea(face)!.Value));
    }

    /// <summary>The outer perimeter (metres) of the end face found for a room solid along the Z axis, null when the room is not
    /// an extrusion.
    /// Only the planar faces count (a curved wall is no obstacle). The calculation needs two horizontal faces facing each other
    /// with the same area (1e-4 ft2), and no other planar face that is neither vertical nor horizontal, or vertical with a hole.
    /// Shortcut: a room with more than two horizontal faces (a stepped floor or ceiling) is taken as no extrusion, since
    /// coplanar pairs are joined only with an export option.</summary>
    internal static double? RoomExtrusionPerimeterMetresCurrent(GeometryData boundary)
    {
        var planar = new List<(GeoFace Face, double[] Normal, double Area)>();
        foreach (var face in boundary.Faces)
            if (!face.CurvedSurface && FaceUnitNormal(face) is { } normal && NetFaceArea(face) is { } area)
                planar.Add((face, normal, area));
        var horizontal = planar.Where(item => Math.Abs(item.Normal[2]) > 1 - 1e-9).ToList();
        if (horizontal.Count != 2 || Math.Abs(MeanHeightCurrent(horizontal[0].Face) - MeanHeightCurrent(horizontal[1].Face)) < 1e-9
            || Math.Abs(horizontal[0].Area - horizontal[1].Area) > 1e-4)
            return null;
        bool framesKnown = horizontal.All(item => item.Face.PlaneXAxis is { Length: 3 } && item.Face.PlaneYAxis is { Length: 3 });
        foreach (var (face, normal, _) in planar)
        {
            bool perpendicular = Math.Abs(normal[2]) < 1e-9, parallel = Math.Abs(normal[2]) > 1 - 1e-9;
            if (perpendicular && face.InnerVertices is { Count: > 0 } || !perpendicular && !parallel) return null;
            // Shortcut (frames of the end planes unknown): the calculation also compares the extents of the two end faces, taken
            // from the parametric envelopes of their planes, with the extents of the solid; for a room turned against the model
            // axes that agrees only when the axes of the plane happen to be the model axes.
            if (!framesKnown && perpendicular && Math.Min(Math.Abs(normal[0]), Math.Abs(normal[1])) > 1e-6) return null;
        }
        // With the frames of the end planes the whole calculation applies: its end faces add only two corners of their (u, v) envelopes to
        // the extents that must span the solid, which a plane turned against the model axes fails.
        if (framesKnown && !WallExtrusion.TryFindVerticalExtrusion(boundary.Faces, out _)) return null;
        var bottom = horizontal[0].Face.OuterVertices![0][2] <= horizontal[1].Face.OuterVertices![0][2]
            ? horizontal[0].Face : horizontal[1].Face;
        // The exact length of the loop: an arc of the outline (the chords of the facets
        // of a vertical cylinder) counts as the arc, not as its chords.
        var ring = bottom.OuterVertices!;
        return (RingPerimeter(ring) + ArcLengthOverChordsCurrent(ring, VerticalCylinderChordsCurrent(boundary.Faces))) * FeetToMetres;
    }

    /// <summary>What the arcs add to the perimeter of a ring: for every edge that is the chord of a vertical cylinder, the arc over it less the chord.</summary>
    private static double ArcLengthOverChordsCurrent(IReadOnlyList<double[]> ring, Dictionary<(long, long, long, long), (double Cx, double Cy, double R)> arcs)
    {
        double extra = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var (a, b) = (ring[i], ring[(i + 1) % ring.Count]);
            if (!arcs.TryGetValue(ChordKeyCurrent(a, b), out var circle)) continue;
            double chord = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]));
            if (chord < 2 * circle.R) extra += circle.R * 2 * Math.Asin(chord / (2 * circle.R)) - chord;
        }
        return extra;
    }

    /// <summary>The area of the largest planar face that lies horizontally, summed over the shells (the top face of a wall
    /// or slab), in feet.</summary>
    internal static double LargestHorizontalFaceAreaCurrent(IEnumerable<ShellInfo> shells) =>
        shells.Sum(shell => FaceAreasCurrent(shell.Faces.Where(face => !face.CurvedSurface && FaceUnitNormal(face) is { } n && Math.Abs(n[2]) > 1 - 1e-9))
            .DefaultIfEmpty(0).Max());
}
