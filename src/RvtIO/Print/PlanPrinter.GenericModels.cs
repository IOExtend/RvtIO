using RvtIO.BuiltIns;
using RvtIO.Model;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    private static void CollectSectionedGenericModel(GeometryData geometry, double bottom, double cut,
        List<List<(double X, double Y)>> masks,
        ref List<(double X1, double Y1, double X2, double Y2, bool Cut)>? lines)
    {
        if (geometry is not { BrepSolids.Count: > 0, UnresolvedSymbol: false, PlacedSymbolBox: false,
                PlacedSymbolClipped: false, UntriangulatedFaces: 0 }) return;
        var faces = geometry.Faces;
        var members = new HashSet<int>();
        var projection = new List<List<(double X, double Y)>>();
        var sections = new List<List<(double X, double Y)>>();
        double[] lower = [0, 0, bottom], upper = [0, 0, cut];
        double[] up = [0, 0, 1], down = [0, 0, -1];
        foreach (var solid in geometry.BrepSolids)
        {
            if (!solid.IsClosed || solid.FaceIndices.Count == 0
                || solid.FaceIndices.Any(index => (uint)index >= (uint)faces.Count)) return;

            var crossing = new List<GeoFace>();
            foreach (int index in solid.FaceIndices)
            {
                var face = faces[index];
                if (face.ExcludedFromTopPlan || face.CurvedSurface
                    || face.InnerVertices is { Count: > 0 } || face.InnerLoops is { Count: > 0 }
                    || face.OuterVertices is not { Count: >= 3 } vertices
                    || vertices.Any(point => point.Length != 3 || !point.All(double.IsFinite))) return;
                double min = vertices.Min(point => point[2]), max = vertices.Max(point => point[2]);
                if (min < cut && max > cut) crossing.Add(face);
                if (!members.Add(index) || max < bottom || min > cut) continue;
                var clipped = min < bottom ? ElevationPrinter.ClipBehind(vertices, lower, up) : vertices;
                if (max > cut) clipped = ElevationPrinter.ClipBehind(clipped, upper, down);
                if (clipped.Count < 3) continue;

                var ring = clipped.Select(InPlan).ToList();
                double area = SignedArea(ring);
                if (Math.Abs(area) < 1e-8) continue;
                if (area < 0) ring.Reverse();
                projection.Add(ring);
            }

            if (crossing.Count == 0) continue;
            var rings = ElevationPrinter.ClosedSectionRings(crossing, [0, 0, cut * FeetToMillimetres], up,
                [1, 0, 0], [0, 1, 0], requireClosed: true).ToList();
            // shortcut: nested cut rings need a mask with holes.
            if (rings.Count == 0 || rings.Any(ring => rings.Any(other => other != ring
                    && PointInPolygon(ring[0].X, ring[0].Y, other)))) return;
            foreach (var ring in rings)
            {
                if (SignedArea(ring) < 0) ring.Reverse();
                sections.Add(ring);
            }
        }
        if (members.Count != faces.Count || sections.Count == 0) return;

        projection.AddRange(sections);
        // A thin projected facet can have distinct boundaries within the same snapped coordinate.
        var outline = ElevationPrinter.OutlineJoinedRings(projection, (int)BuiltInCategory.OST_GenericModel,
            coverOppositeEdges: true, snapEndpoints: false).ToList();
        if (outline.Count == 0) return;
        masks.AddRange(projection);
        lines ??= [];
        // Reverse cut rings so their coincident spans cover the projection edges as well as their interiors.
        var cutMasks = sections.Select(ring => ring.AsEnumerable().Reverse().ToList());
        lines.AddRange(ElevationPrinter.VisibleTerrainEdges(outline, cutMasks, [], coverOppositeEdges: true)
            .Select(edge => (edge.X1, edge.Y1, edge.X2, edge.Y2, false)));
        lines.AddRange(ElevationPrinter.OutlineJoinedRings(sections, (int)BuiltInCategory.OST_GenericModel,
            coverOppositeEdges: true).Select(edge => (edge.X1, edge.Y1, edge.X2, edge.Y2, true)));
    }
}
