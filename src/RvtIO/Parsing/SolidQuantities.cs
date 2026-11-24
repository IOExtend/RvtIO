using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>Volume and face areas of an element whose geometry is one closed polyhedral shell.
/// The faces of a decoded shell do not share one winding, so each face is oriented from the edges it shares with its
/// neighbours: every edge has to belong to exactly two faces and be walked once in each direction after the flips.
/// A shell for which that does not hold (open, shared edges of more than two faces, not orientable) gives no value.
/// The volume is the exact one only when every face is a true plane; a shell with tessellated curved faces gives the
/// volume of its facets, so callers use it for categories whose shells are planar.</summary>
internal static class SolidQuantities
{
    internal readonly record struct Shell(double Volume, double SurfaceArea, double LargestFaceArea);

    private const double VertexTolerance = 1e-6;

    /// <summary>The shells of all solids of a geometry, when every face of the geometry belongs to a closed solid, lies on a
    /// plane surface of the file and every solid is an orientable closed polyhedron (<see cref="OfSingleClosedShell"/>);
    /// volumes and surfaces add up, the largest face is the largest of any solid. Null otherwise.</summary>
    internal static Shell? OfAllSolids(GeometryData? geometry)
    {
        if (geometry?.BrepSolids is not { Count: > 0 } solids || geometry.ReadFaces is not { Count: > 0 } faces) return null;
        var covered = new HashSet<int>();
        double volume = 0, surface = 0, largest = 0;
        foreach (var solid in solids)
        {
            if (!solid.IsClosed || OfClosedShell(faces, solid.FaceIndices, planarOnly: true) is not { } shell)
                return null;
            volume += shell.Volume; surface += shell.SurfaceArea; largest = Math.Max(largest, shell.LargestFaceArea);
            covered.UnionWith(solid.FaceIndices);
        }
        return covered.Count == faces.Count ? new Shell(volume, surface, largest) : null;
    }

    internal static Shell? OfSingleClosedShell(GeometryData? geometry)
    {
        if (geometry?.BrepSolids is not { Count: 1 } solids || !solids[0].IsClosed) return null;
        return OfClosedShell(geometry.ReadFaces, solids[0].FaceIndices, planarOnly: false);
    }

    private static Shell? OfClosedShell(IReadOnlyList<GeoFace> source, List<int> indices, bool planarOnly)
    {
        var faces = new List<List<List<double[]>>>(); // face -> loops (outer first) -> points
        foreach (int index in indices)
        {
            if ((uint)index >= (uint)source.Count) return null;
            var face = source[index];
            if ((planarOnly && !face.PlaneSurface) || face.OuterVertices is not { Count: >= 3 } outer) return null;
            var loops = new List<List<double[]>> { outer };
            double[] outerArea = VectorArea(outer);
            foreach (var inner in face.InnerVertices ?? [])
            {
                if (inner.Count < 3) return null;
                // holes run against the outer boundary
                loops.Add(Dot(outerArea, VectorArea(inner)) < 0 ? inner : Enumerable.Reverse(inner).ToList());
            }
            faces.Add(loops);
        }
        if (faces.Count < 4) return null;

        var vertices = new VertexIndex();
        var edgeUses = new Dictionary<(int, int), List<(int Face, int Direction)>>();
        for (int face = 0; face < faces.Count; face++)
            foreach (var loop in faces[face])
                for (int i = 0; i < loop.Count; i++)
                {
                    int a = vertices.Id(loop[i]), b = vertices.Id(loop[(i + 1) % loop.Count]);
                    if (a == b) continue;
                    var key = a < b ? (a, b) : (b, a);
                    if (!edgeUses.TryGetValue(key, out var uses)) edgeUses[key] = uses = new();
                    uses.Add((face, a < b ? 1 : -1));
                }

        var neighbours = new List<(int Face, int Product)>[faces.Count];
        for (int i = 0; i < neighbours.Length; i++) neighbours[i] = new();
        foreach (var uses in edgeUses.Values)
        {
            if (uses.Count != 2 || uses[0].Face == uses[1].Face) return null;
            // after the flips the two faces must walk the edge in opposite directions
            neighbours[uses[0].Face].Add((uses[1].Face, uses[0].Direction * uses[1].Direction));
            neighbours[uses[1].Face].Add((uses[0].Face, uses[0].Direction * uses[1].Direction));
        }
        var flip = new int[faces.Count];
        flip[0] = 1;
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count > 0)
        {
            int face = pending.Pop();
            foreach (var (other, product) in neighbours[face])
            {
                int wanted = -flip[face] * product;
                if (flip[other] == 0) { flip[other] = wanted; pending.Push(other); }
                else if (flip[other] != wanted) return null;
            }
        }
        if (flip.Any(value => value == 0)) return null;

        double volume = 0, surface = 0, largest = 0;
        for (int face = 0; face < faces.Count; face++)
        {
            double net = 0;
            for (int l = 0; l < faces[face].Count; l++)
            {
                double[] area = VectorArea(faces[face][l]);
                volume += flip[face] * Dot(area, faces[face][l][0]) / 3;
                double magnitude = Math.Sqrt(Dot(area, area));
                net += l == 0 ? magnitude : -magnitude;
            }
            surface += net;
            largest = Math.Max(largest, net);
        }
        return new Shell(Math.Abs(volume), surface, largest);
    }

    /// <summary>Plan area of a room's boundary shell: half the sum of the projections of its faces on the plan, a hole of a face counted
    /// against it. It is the net area of the floor of a room with one floor level and the area of the whole plan outline where the
    /// floor steps.</summary>
    internal static double? RoomPlanArea(GeometryData? boundary)
    {
        if (boundary?.ReadFaces is not { Count: > 0 } faces) return null;
        double projected = 0;
        foreach (var face in faces)
        {
            if (face.OuterVertices is not { Count: >= 3 } outer) continue;
            double area = Math.Abs(VectorArea(outer)[2]);
            foreach (var hole in face.InnerVertices ?? [])
                if (hole.Count >= 3) area -= Math.Abs(VectorArea(hole)[2]);
            projected += area;
        }
        return projected / 2;
    }

    private static double[] VectorArea(List<double[]> points)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < points.Count; i++)
        {
            double[] p = points[i], q = points[(i + 1) % points.Count];
            x += p[1] * q[2] - p[2] * q[1];
            y += p[2] * q[0] - p[0] * q[2];
            z += p[0] * q[1] - p[1] * q[0];
        }
        return [x / 2, y / 2, z / 2];
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    /// <summary>Numbers the points of a shell, one number per position within <see cref="VertexTolerance"/>.</summary>
    private sealed class VertexIndex
    {
        private readonly Dictionary<(long, long, long), List<(double[] Point, int Id)>> _cells = new();
        private int _count;

        public int Id(double[] point)
        {
            long cx = (long)Math.Floor(point[0] / VertexTolerance), cy = (long)Math.Floor(point[1] / VertexTolerance), cz = (long)Math.Floor(point[2] / VertexTolerance);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var known))
                            foreach (var (other, id) in known)
                                if (Math.Abs(other[0] - point[0]) <= VertexTolerance && Math.Abs(other[1] - point[1]) <= VertexTolerance
                                    && Math.Abs(other[2] - point[2]) <= VertexTolerance)
                                    return id;
            if (!_cells.TryGetValue((cx, cy, cz), out var cell)) _cells[(cx, cy, cz)] = cell = new();
            cell.Add((point, _count));
            return _count++;
        }
    }
}
