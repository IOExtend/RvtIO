using RvtIO.Model;

namespace RvtIO.Geometry;

/// <summary>The search for a vertical-style extrusion along one axis of any direction (Z for a wall, the custom axis
/// across the host wall for an opening), keeping the face the extrusion starts
/// from. <see cref="TryFindVerticalExtrusion"/> is the same search along Z without the result.</summary>
internal static partial class WallExtrusion
{
    /// <summary>The start of an extrusion found along an axis: the loops of the face at the lower parameter (the outer loop of
    /// its region first, then its holes), as the decoder holds them (feet), the distance between the two faces, and the circular
    /// edges of the start face with the spans the file stores for them.</summary>
    internal sealed record AxisExtrusion(IReadOnlyList<IReadOnlyList<double[]>> Loops, double LengthFeet, double StartParameterFeet,
        IReadOnlyList<Internal.Arc>? Arcs = null);

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static bool ParallelToAxis(double[] n, double[] axis)
    {
        double x = n[1] * axis[2] - n[2] * axis[1], y = n[2] * axis[0] - n[0] * axis[2], z = n[0] * axis[1] - n[1] * axis[0];
        return Math.Sqrt(x * x + y * y + z * z) < AngleTolerance && Math.Abs(Dot(n, axis)) > 0.5;
    }

    private static bool PerpendicularToAxis(double[] n, double[] axis) => Math.Abs(Dot(n, axis)) < AngleTolerance;

    /// <summary>The loops read from the start face when the search along <paramref name="axis"/> (a unit vector) finds exactly
    /// one pair of parallel faces and the envelopes of the pair span the bounding box of the solid. Null with a
    /// <paramref name="reason"/> otherwise; also null for a face that the decoder holds as several regions.</summary>
    internal static AxisExtrusion? FindAxisExtrusion(IReadOnlyList<GeoFace> solidFaces, double[] axis, out string reason)
    {
        var planar = MergeRegions(solidFaces).Where(face => !face.Curved).ToList();
        int count = planar.Count;
        if (count < 2) { reason = "few-faces"; return null; }

        var normals = planar.Select(face => face.Normal).ToArray();
        var areas = planar.Select(face => face.Area).ToArray();
        var hasOpenings = planar.Select(face => face.HasOpenings).ToArray();
        var skip = new bool[count];

        // The solid of a curved wall is judged as the class decision judges it: its box follows the edge samples,
        // and the planes follow their serialized envelopes.
        bool curved = solidFaces.Any(IsVerticalCylinderPatch);
        bool bounded = curved && solidFaces.All(face => face.MeshEdgeBounds is { Length: 6 });
        var body = new Extents();
        if (bounded)
            foreach (var face in solidFaces) { body.Add(face.MeshEdgeBounds![..3]); body.Add(face.MeshEdgeBounds![3..]); }
        else
        {
            foreach (var face in solidFaces)
            {
                foreach (var p in face.OuterVertices ?? []) body.Add(p);
                foreach (var ring in face.InnerVertices ?? []) foreach (var p in ring) body.Add(p);
            }
            if (curved) AddCylinderExtremes(body, solidFaces);
        }
        // (two caps whose circles are known and found in their rings are compared by their exact areas)
        var exactAreas = planar.Select(face => face.AreaIsExact && face.Regions.Any(region => region.MeshArcs is { Count: > 0 })).ToArray();
        double AreaTolerance(int i, int j) => curved && !(exactAreas[i] && exactAreas[j])
            ? Math.Max(FaceAreaTolerance, CurvedAreaRelativeTolerance * Math.Max(areas[i], areas[j])) : FaceAreaTolerance;

        var facesExtents = new Extents();
        int pairs = 0;
        double firstLength = 0;
        double[] endPlanes = [];
        MeshFace? start = null;
        double startParameter = 0;
        bool lengthsDiffer = false, stop = false;
        string failure = "no-pair";

        for (int i = 0; i < count - 1 && !stop; i++)
        {
            if (skip[i] || !ParallelToAxis(normals[i], axis)) { skip[i] = true; continue; }

            bool rejected = false;
            int j = i + 1;
            for (; j < count; j++)
            {
                if (skip[j]) continue;
                if (!Parallel(normals[i], normals[j]) || Math.Abs(areas[i] - areas[j]) > AreaTolerance(i, j)) continue;
                for (int k = 0; k < count; k++)
                {
                    if (k == i || k == j) continue;
                    if ((PerpendicularToAxis(normals[k], axis) && hasOpenings[k])
                        || (!PerpendicularToAxis(normals[k], axis) && !ParallelToAxis(normals[k], axis)))
                        rejected = true;
                }
                break;
            }
            if (rejected) { failure = "tilted-or-opening"; continue; }
            if (j == count) continue;

            skip[i] = skip[j] = true;
            double zi = Dot(planar[i].First.Origin, axis), zj = Dot(planar[j].First.Origin, axis);
            double length = Math.Abs(zj - zi);
            if (length < PlaneTolerance) continue;

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
                (start, startParameter) = zi <= zj ? (planar[i], zi) : (planar[j], zj);
            }
            pairs++;
            AddEnvelopeCorners(facesExtents, planar[i], bounded);
            AddEnvelopeCorners(facesExtents, planar[j], bounded);
        }

        if (pairs == 0) { reason = failure; return null; }
        if (!facesExtents.IsEqualTo(body, VertexTolerance)) { reason = "bbox"; return null; }
        if (pairs > 1) { reason = lengthsDiffer ? "multi-length" : "several-pairs"; return null; }
        if (start!.Regions.Count != 1) { reason = "several-regions"; return null; }

        var region = start.Regions[0];
        if (region.OuterVertices is not { Count: >= 3 } outer) { reason = "no-loop"; return null; }
        var loops = new List<IReadOnlyList<double[]>> { outer };
        foreach (var hole in region.InnerVertices ?? []) loops.Add(hole);
        reason = "ok";
        return new AxisExtrusion(loops, firstLength, startParameter, region.MeshArcs);
    }
}
