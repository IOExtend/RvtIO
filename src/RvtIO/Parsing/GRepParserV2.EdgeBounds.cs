using RvtIO.Internal;

namespace RvtIO.Parsing;

internal static partial class GRepParserV2
{
    /// <summary>
    /// Samples a circular edge (cylinder meets a plane square to its axis) the way Revit's
    /// renderer does and returns the edge as the file stores it; null when the edge is not of that
    /// kind.
    /// </summary>
    internal static Arc? ResampleCircularEdge(EdgeRec edge, SurfaceRec a, SurfaceRec b, bool resample = true)
    {
        if (!edge.HasEndpoints || (edge.Flags & 6) != 6) return null;
        bool cylinderA = a.Kind == SurfKind.Cylinder;
        var cylinder = cylinderA ? a : b;
        var plane = cylinderA ? b : a;
        if (cylinder.Kind != SurfKind.Cylinder || plane.Kind != SurfKind.Plane
            || Math.Abs(Dot(plane.X, cylinder.Z)) > 1e-10
            || Math.Abs(Dot(plane.Y, cylinder.Z)) > 1e-10) return null;
        var source = new List<EdgeUV> { edge.Start }; source.AddRange(edge.Interior); source.Add(edge.EndP);
        double U(EdgeUV p) => cylinderA ? p.UA : p.UB;
        double V(EdgeUV p) => cylinderA ? p.VA : p.VB;
        double first = U(source[0]), last = U(source[^1]), height = V(source[0]);
        if (!double.IsFinite(first) || !double.IsFinite(last) || !double.IsFinite(height)
            || Math.Abs(last - first) > 2 * Math.PI || first == last
            || source.Any(p => !double.IsFinite(U(p)) || !double.IsFinite(V(p)) || Math.Abs(V(p) - height) > 1e-10)) return null;
        for (int i = 1; i < source.Count; i++)
            if ((U(source[i]) - U(source[i - 1])) * (last - first) < 0) return null;
        double radius = Math.Abs(cylinder.R);
        if (!double.IsFinite(radius) || radius <= 0) return null;
        var circle = new Arc(
            Enumerable.Range(0, 3).Select(k => cylinder.O[k] + height * cylinder.Z[k]).ToArray(),
            (double[])cylinder.X.Clone(), (double[])cylinder.Y.Clone(), cylinder.R, first, last, edge.Handle);
        if (!resample) return circle;
        // Native Low LOD surfaceTolerance is halved before the arc sample points are appended.
        double ratio = (0.09998 - 0.2 * 0.1998) * 0.5 / radius;
        double angle = ratio < 0.000005 ? 0.006283185307179587
            : ratio >= 2 ? Math.PI / 2 : Math.Min(Math.PI / 4, 2 * Math.Acos(1 - ratio));
        double count = Math.Ceiling(Math.Abs(last - first) / angle);
        // The renderer takes at least four points of an edge (six for a nearly complete circle, a
        // span above 1.8 pi) and spaces them evenly over the edge when the arc sampler returned
        // fewer: two half circles of a thin rod are a hexagon.
        double minimumPoints = Math.Abs(last - first) > 5.654866776461628 ? 6 : 4;
        if (count + 1 < minimumPoints) count = minimumPoints - 1;
        if (!double.IsFinite(count) || count < 1 || count > 10000) return circle;
        double xx = Dot(plane.X, plane.X), yy = Dot(plane.Y, plane.Y), xy = Dot(plane.X, plane.Y);
        double determinant = xx * yy - xy * xy;
        if (!double.IsFinite(determinant) || determinant <= 0) return circle;
        var sampled = new List<EdgeUV>();
        for (int i = 0; i <= (int)count; i++)
        {
            double u = first + (last - first) * i / count;
            var world = Enumerable.Range(0, 3).Select(k => cylinder.O[k]
                + cylinder.R * (Math.Cos(u) * cylinder.X[k] + Math.Sin(u) * cylinder.Y[k])
                + height * cylinder.Z[k] - plane.O[k]).ToArray();
            double px = Dot(world, plane.X), py = Dot(world, plane.Y);
            double pu = (px * yy - py * xy) / determinant, pv = (py * xx - px * xy) / determinant;
            if (!double.IsFinite(pu) || !double.IsFinite(pv)) return circle;
            sampled.Add(cylinderA ? new() { UA = u, VA = height, UB = pu, VB = pv }
                : new() { UA = pu, VA = pv, UB = u, VB = height });
        }
        edge.AuthoredStart ??= edge.Start; edge.AuthoredEndP ??= edge.EndP; edge.AuthoredInterior ??= edge.Interior;
        edge.Start = sampled[0]; edge.EndP = sampled[^1]; edge.Interior = sampled.Skip(1).SkipLast(1).ToList();
        return circle;
        static double Dot(double[] x, double[] y) => x[0] * y[0] + x[1] * y[1] + x[2] * y[2];
    }

    // The bounding box of a geometry is computed per face: only authored Edge samples,
    // evaluated on the Edge's first active Face; neither a surface envelope nor analytic extrema.
    private static Dictionary<uint, double[]> ReadFaceEdgeBounds(Cur c, Dictionary<uint, List<int>> loops,
        Dictionary<uint, EdgeRec?> edges)
    {
        var result = new Dictionary<uint, double[]>();
        foreach (var (face, indices) in loops)
        {
            var faceEdges = TryGetFaceLoopEdges(c, face, indices, edges);
            if (faceEdges == null) continue;
            var points = new List<double[]>();
            bool complete = true;
            foreach (var edge in faceEdges)
            {
                if (TryGetEdgePoints(c, edge) is not { } samples)
                { complete = false; break; }
                points.AddRange(samples);
            }
            if (complete && points.Count > 0)
                result[face] = [points.Min(p => p[0]), points.Min(p => p[1]), points.Min(p => p[2]),
                    points.Max(p => p[0]), points.Max(p => p[1]), points.Max(p => p[2])];
        }
        return result;
    }

    private static List<double[]>? TryGetEdgePoints(Cur c, EdgeRec edge)
    {
        if (!TryIsActive(edge.FaceA, out bool activeA) || !TryIsActive(edge.FaceB, out bool activeB)) return null;
        bool useA = edge.FaceA != 0 && (activeA || edge.FaceB == 0 || !activeB);
        return c.DirectSurfaceByFace.TryGetValue(useA ? edge.FaceA : edge.FaceB, out var surface)
            ? EvaluateEdgePoints(edge, useA, surface) : null;
        bool TryIsActive(uint handle, out bool active)
        {
            active = false;
            if (handle == 0) return true;
            if (!c.ObjectGInfo.TryGetValue((int)handle, out var info)) return false;
            active = (info.Flags & 0x80000) != 0;
            return true;
        }
    }

    private static List<EdgeRec>? TryGetFaceLoopEdges(Cur c, uint face, List<int> indices,
        Dictionary<uint, EdgeRec?> edges)
    {
        var result = new List<EdgeRec>();
        var used = new HashSet<uint>();
        foreach (int index in indices)
        {
            var loop = c.SpaceEdgeLoops[index];
            uint sentinel = (uint)c.Queue[index].V, previous = sentinel, current = loop.Next;
            while (current != sentinel)
            {
                if (!edges.TryGetValue(current, out var edge) || edge == null || !used.Add(current)
                    || edge.FaceA == edge.FaceB || edge.FaceA != face && edge.FaceB != face
                    || (edge.FaceA == face ? edge.PrevA : edge.PrevB) != previous) return null;
                result.Add(edge);
                previous = current;
                current = edge.FaceA == face ? edge.NextA : edge.NextB;
            }
            if (previous != loop.Prev) return null;
        }
        return result;
    }

    internal static List<double[]>? EvaluateEdgePoints(EdgeRec edge, bool useA, SurfaceRec surface)
    {
        if (surface.Kind is not (SurfKind.Plane or SurfKind.Cylinder) || !edge.HasEndpoints) return null;
        var points = new List<double[]>();
        if ((edge.Flags & 2) != 0 && !Add(edge.Start)) return null;
        foreach (var point in edge.Interior) if (!Add(point)) return null;
        if ((edge.Flags & 4) != 0 && !Add(edge.EndP)) return null;
        return points;

        bool Add(EdgeUV point)
        {
            double u = useA ? point.UA : point.UB, v = useA ? point.VA : point.VB;
            double x = surface.Kind == SurfKind.Cylinder ? surface.R * Math.Cos(u) : u;
            double y = surface.Kind == SurfKind.Cylinder ? surface.R * Math.Sin(u) : v;
            double z = surface.Kind == SurfKind.Cylinder ? v : 0;
            var world = Enumerable.Range(0, 3).Select(k => surface.O[k]
                + x * surface.X[k] + y * surface.Y[k] + z * surface.Z[k]).ToArray();
            if (world.Any(value => !double.IsFinite(value))) return false;
            points.Add(world);
            return true;
        }
    }
}
