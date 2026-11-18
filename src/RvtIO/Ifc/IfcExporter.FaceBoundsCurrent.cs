using RvtIO.Model;
using static RvtIO.Geometry.FacetedShell;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>A closed shell the faceted writer found: the faces of <c>GeometryData.Faces</c> it comes from, its oriented faces, the
    /// faces it uses up and the face of <c>GeometryData.Faces</c> each oriented face was made of (null when that is not one to one).</summary>
    internal sealed record ClosedComponentCurrent(List<int> SourceIndices, List<GeoFace> OrientedFaces, List<int> ConsumedIndices,
        List<int>? OrientedSources);

    /// <summary>An oriented face with the serialized Face it is a region of (0 and null when it is not known to be one).</summary>
    private sealed record PreparedFaceCurrent(GeoFace Face, uint SourceFace, IReadOnlyList<List<double[]>>? SourceLoops);

    /// <summary>
    /// The bounds of one IfcFace the way the current profile writes the face of a planar solid: one IfcFace per serialized
    /// Face, a bound for every loop of the Face in the order the file lists them, and the one IfcFaceOuterBound is the last
    /// loop that does not lie inside an earlier one.
    /// </summary>
    internal static List<(List<double[]> Ring, bool IsOuter)>? FaceBoundsCurrent(
        IReadOnlyList<GeoFace> regions, IReadOnlyList<IReadOnlyList<double[]>> sourceLoops)
    {
        if (regions.Count == 0 || sourceLoops.Count == 0 || regions.Any(region => region.OuterVertices is not { Count: >= 3 }))
            return null;
        double[]? reference = Newell(regions[0].OuterVertices!);
        if (reference == null) return null;

        var rings = new List<List<double[]>>();
        foreach (var region in regions)
        {
            double[]? normal = Newell(region.OuterVertices!);
            if (normal == null) return null;
            bool reverse = Dot(normal, reference) < 0;
            foreach (var ring in new[] { region.OuterVertices! }.Concat(region.InnerVertices ?? []))
            {
                if (ring.Count < 3) return null;
                var copy = ring.Select(point => (double[])point.Clone()).ToList();
                if (reverse) copy.Reverse();
                rings.Add(copy);
            }
        }
        if (rings.Count != sourceLoops.Count) return null;

        // each ring is a cleaned copy of exactly one serialized loop: the loop that shares most of its vertices
        var loopKeys = sourceLoops.Select(loop => loop.Select(point => IfcPrecisionKeyFeet(point)).ToHashSet()).ToList();
        var ordered = new List<double[]>?[sourceLoops.Count];
        foreach (var ring in rings)
        {
            var keys = ring.Select(point => IfcPrecisionKeyFeet(point)).ToList();
            int best = -1, bestShared = 0;
            for (int index = 0; index < loopKeys.Count; index++)
            {
                int shared = keys.Count(key => loopKeys[index].Contains(key));
                if (shared > bestShared) { best = index; bestShared = shared; }
            }
            if (best < 0 || bestShared * 2 < keys.Count || ordered[best] != null) return null;
            ordered[best] = ring;
        }
        if (ordered.Any(ring => ring == null)) return null;

        int outer = 0;
        for (int index = 0; index < ordered.Length; index++)
        {
            bool inner = false;
            for (int earlier = 0; earlier < index && !inner; earlier++)
                inner = AllVerticesStrictlyInside(ordered[index]!, ordered[earlier]!);
            if (!inner) outer = index;
        }
        return ordered.Select((ring, index) => (ring!, index == outer)).ToList();

        static double[]? Newell(IReadOnlyList<double[]> ring)
        {
            double x = 0, y = 0, z = 0;
            for (int index = 0; index < ring.Count; index++)
            {
                var a = ring[index];
                var b = ring[(index + 1) % ring.Count];
                x += (a[1] - b[1]) * (a[2] + b[2]);
                y += (a[2] - b[2]) * (a[0] + b[0]);
                z += (a[0] - b[0]) * (a[1] + b[1]);
            }
            return double.IsFinite(x + y + z) && x * x + y * y + z * z > 0 ? [x, y, z] : null;
        }

        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    }

    internal static bool AllVerticesStrictlyInside(IReadOnlyList<double[]> inner, IReadOnlyList<double[]> outer)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int index = 0; index < outer.Count; index++)
        {
            var a = outer[index];
            var b = outer[(index + 1) % outer.Count];
            nx += (a[1] - b[1]) * (a[2] + b[2]);
            ny += (a[2] - b[2]) * (a[0] + b[0]);
            nz += (a[0] - b[0]) * (a[1] + b[1]);
        }
        int drop = Math.Abs(nx) >= Math.Abs(ny) && Math.Abs(nx) >= Math.Abs(nz) ? 0 : Math.Abs(ny) >= Math.Abs(nz) ? 1 : 2;
        (double U, double V) Project(double[] point) => drop switch
        {
            0 => (point[1], point[2]),
            1 => (point[0], point[2]),
            _ => (point[0], point[1]),
        };
        var polygon = outer.Select(Project).ToArray();
        const double tolerance = 1e-9;
        foreach (var vertex in inner)
        {
            var (u, v) = Project(vertex);
            bool inside = false;
            for (int index = 0; index < polygon.Length; index++)
            {
                var (u1, v1) = polygon[index];
                var (u2, v2) = polygon[(index + 1) % polygon.Length];
                double dx = u2 - u1, dy = v2 - v1, length = Math.Sqrt(dx * dx + dy * dy);
                if (length > 0)
                {
                    double along = ((u - u1) * dx + (v - v1) * dy) / length;
                    double across = Math.Abs((u - u1) * dy - (v - v1) * dx) / length;
                    if (across <= tolerance && along >= -tolerance && along <= length + tolerance) return false;
                }
                if ((v1 > v) != (v2 > v) && u < (u2 - u1) * (v - v1) / (v2 - v1) + u1) inside = !inside;
            }
            if (!inside) return false;
        }
        return true;
    }
}
