using RvtIO.Geometry.Meshing;
using RvtIO.Model;

namespace RvtIO.Print;

public static partial class ElevationPrinter
{
    private static List<CollectedEdge>? ProjectSectionMeshSilhouette(GeometryData geometry,
        double[] horizontal, double[] vertical, double[] direction, double planeDepth, int elementId, int category,
        bool multipleRevolvedNodes)
    {
        if (geometry is not { BrepSolids.Count: > 1, UnresolvedSymbol: false, PlacedSymbolBox: false,
                PlacedSymbolClipped: false, UntriangulatedFaces: 0, MeshNodes: { Count: > 0 } nodes }
            || geometry.BrepSolids.Any(solid => !solid.IsClosed)
            || nodes.Any(node => node.Ghost || node.Faces.Any(face =>
                face.SurfaceKind is not (SurfaceKind.Plane or SurfaceKind.Cylinder or SurfaceKind.Revolved)))
            || (multipleRevolvedNodes
                ? nodes.Count < 2 || nodes.Any(node => !node.Faces.Any(face => face.SurfaceKind == SurfaceKind.Revolved))
                : nodes.Count != 1))
            return null;

        var revolvedMarkers = nodes.SelectMany(node => node.Faces.Where(face => face.SurfaceKind == SurfaceKind.Revolved)
            .Select(face => (node.Node, (uint)face.Marker))).ToHashSet();
        if (!geometry.Faces.Any(face => !face.PlaneSurface
                && revolvedMarkers.Contains((face.GeometryNode, face.MeshMarker))))
            return null;

        var assigned = geometry.BrepSolids.SelectMany(solid => solid.FaceIndices).ToHashSet();
        if ((!multipleRevolvedNodes && assigned.Count != geometry.Faces.Count)
            || assigned.Any(index => (uint)index >= geometry.Faces.Count))
            return null;

        var rings = new List<List<(double X, double Y)>>();
        var depths = new List<double[]>();
        for (int i = 0; i < geometry.Faces.Count; i++)
        {
            var face = geometry.Faces[i];
            if (face.CurvedSurface || face.InnerVertices is { Count: > 0 } || face.InnerLoops is { Count: > 0 }
                || face.OuterVertices is not { Count: >= 3 } vertices
                || vertices.Any(point => point.Length != 3 || !point.All(double.IsFinite)))
                return null;

            var vertexDepths = vertices.Select(point => Project(point, direction)).ToArray();
            if (vertexDepths.Any(depth => depth <= planeDepth + 1e-6)) return null;
            var ring = vertices.Select(point => (Project(point, horizontal), Project(point, vertical))).ToList();
            double area = PlanPrinter.SignedArea(ring);
            if (Math.Abs(area) < 1e-6) continue;
            // Unassigned display faces may be edge-on in this view. A visible open face uses the fallback.
            if (!assigned.Contains(i)) return null;
            if (area < 0)
            {
                ring.Reverse();
                Array.Reverse(vertexDepths);
            }
            rings.Add(ring);
            depths.Add(vertexDepths);
        }

        var strokes = new List<CollectedEdge>();
        foreach (var edge in OutlineJoinedRings(rings, category, coverOppositeEdges: true, retainSource: true))
        {
            var (ringIndex, first) = edge.SourceRingEdge!.Value;
            var ring = rings[ringIndex];
            int second = (first + 1) % ring.Count;
            var a = ring[first];
            var b = ring[second];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;
            double DepthAt(double x, double y) => depths[ringIndex][first]
                + (depths[ringIndex][second] - depths[ringIndex][first])
                    * ((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared;
            double start = DepthAt(edge.X1, edge.Y1), end = DepthAt(edge.X2, edge.Y2);
            double mid = (start + end) / 2;
            strokes.Add(edge with
            {
                Depth = mid, MidDepth = mid, NearDepth = Math.Min(start, end),
                SourceRingEdge = null, SilhouetteOwnerId = elementId,
            });
        }
        return strokes.Count > 0 ? strokes : null;
    }
}
