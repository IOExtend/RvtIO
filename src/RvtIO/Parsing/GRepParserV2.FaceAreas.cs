using RvtIO.Geometry.Meshing;

namespace RvtIO.Parsing;

internal static partial class GRepParserV2
{
    private static double? MeasureFilledFaces(Cur c, Dictionary<uint, SurfaceRec> surfaces, Dictionary<uint, List<int>> loopsByFace)
    {
        var edges = EdgesByHandle(c);
        double area = 0;
        for (int i = 0; i < c.Queue.Count; i++)
        {
            if (c.Queue[i].Cls != 2014 || c.QueueLinks[i] is not (int parent and >= 0, "m_pGFilling")
                || c.Queue[parent].Cls != GFaceClass) continue;
            uint face = (uint)c.Queue[parent].V;
            if (!surfaces.TryGetValue(face, out var surface) || surface.Kind != SurfKind.Plane
                || !loopsByFace.TryGetValue(face, out var loops) || SurfaceCoedges(c, face, loops, edges) is not { } coedges
                || FaceArea.Compute(coedges) is not double measure) return null;
            area += measure;
        }
        return area > 0 && double.IsFinite(area) ? area : null;
    }

    /// <summary>The area <c>computeAreaOfFace</c> gives each planar Face with curved Edges, and each cylindrical Face: the
    /// quadrature of <see cref="FaceArea"/> over the coedges of its loops in file order, with the Edge points as the file stores them.
    /// A planar Face of straight Edges has no entry: the quadrature of a line is its exact value, the polygon area. A Face with an open loop or
    /// an Edge that is not known on both sides has none either.</summary>
    private static Dictionary<uint, double> ComputeFaceAreas(Cur c, Dictionary<uint, SurfaceRec> surfaces, IEnumerable<uint> faces,
        Dictionary<uint, List<int>> loopsByFace, Dictionary<uint, EdgeRec?> edges)
    {
        var result = new Dictionary<uint, double>();
        foreach (uint face in faces)
        {
            if (!surfaces.TryGetValue(face, out var surface) || surface.Kind is not (SurfKind.Plane or SurfKind.Cylinder)
                || surface.Kind == SurfKind.Cylinder && !c.IncludeNonVisible
                || !loopsByFace.TryGetValue(face, out var indices) || indices.Count == 0)
                continue;
            var coedges = SurfaceCoedges(c, face, indices, edges);
            bool curved = coedges?.Any(edge => edge.Points.Count > 2) == true;
            if (coedges != null && (curved || surface.Kind == SurfKind.Cylinder)
                && FaceArea.Compute(coedges) is { } area)
                result[face] = surface.Kind == SurfKind.Cylinder ? area * Math.Abs(surface.R) : area;
        }
        return result;
    }

    private static List<FaceArea.Coedge>? SurfaceCoedges(Cur c, uint face, List<int> indices,
        Dictionary<uint, EdgeRec?> edges, bool rawOnly = false, bool closeEndPole = false)
    {
        var coedges = new List<FaceArea.Coedge>();
        foreach (int index in indices)
        {
            var loop = c.SpaceEdgeLoops[index];
            uint sentinel = (uint)c.Queue[index].V, current = loop.Next;
            int guard = 0;
            int first = coedges.Count;
            if (loop.Open != 0) return null;
            while (current != sentinel)
            {
                if (guard++ > 100000 || !edges.TryGetValue(current, out var edge) || edge == null || !edge.HasEndpoints
                    || edge.FaceA == edge.FaceB || edge.FaceA != face && edge.FaceB != face)
                    return null;
                bool firstFace = edge.FaceA == face;
                // The original coedges precede the mesh refinements shared with neighbouring faces.
                if (rawOnly && edge.RawInterior == null) return null;
                var samples = rawOnly ? edge.RawInterior!.Prepend(edge.RawStart).Append(edge.RawEndP) : edge.AuthoredPoints();
                var points = samples.Select(point => firstFace ? (point.UA, point.VA) : (point.UB, point.VB)).ToList();
                coedges.Add(new FaceArea.Coedge(points, edge.Flags, !firstFace, edge.Handle));
                current = firstFace ? edge.NextA : edge.NextB;
            }
            if (closeEndPole && !CloseRuledEndPole(coedges, first)) return null;
        }
        return coedges;
    }

    internal static bool CloseRuledEndPole(List<FaceArea.Coedge> coedges, int first)
    {
        const double tolerance = 1e-9;
        int end = coedges.Count;
        for (int i = first; i < end; i++)
        {
            var edge = coedges[i];
            var next = coedges[i + 1 == end ? first : i + 1];
            var a = edge.Reversed ? edge.Points[0] : edge.Points[^1];
            var b = next.Reversed ? next.Points[^1] : next.Points[0];
            if (Math.Abs(a.U - b.U) <= tolerance && Math.Abs(a.V - b.V) <= tolerance) continue;
            if (Math.Abs(a.V - 1) > tolerance || Math.Abs(b.V - 1) > tolerance) return false;
            // The two UV endpoints at the end profile are one 3D point and have no saved edge.
            coedges.Add(new FaceArea.Coedge([a, b], 0, false));
        }
        return true;
    }
}
