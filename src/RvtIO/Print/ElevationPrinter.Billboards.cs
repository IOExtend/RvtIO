using RvtIO.BuiltIns;
using RvtIO.Model;

namespace RvtIO.Print;

public static partial class ElevationPrinter
{
    private readonly record struct BillboardPlane(double X, double Y, double Offset)
    {
        internal const double Tolerance = 0.1 / FeetToMillimetres;

        internal bool Contains(double[] point) => Math.Abs(X * point[0] + Y * point[1] - Offset) <= Tolerance;
    }

    /// <summary>Recognize two perpendicular upright planes, with optional triangles at their base.</summary>
    private static List<BillboardPlane>? CrossedBillboardPlanes(List<GeoFace> faces)
    {
        const double angleTolerance = 1e-5;
        if (faces.Any(face => face.OuterVertices is not { Count: 3 } || !face.PolygonMesh
            || face.RenderStyleId != null || face.NestedRenderStyleId != null)) return null;
        double baseZ = faces.Min(face => face.OuterVertices!.Min(point => point[2]));
        var planes = new List<BillboardPlane>(2);
        foreach (var face in faces)
        {
            var vertices = face.OuterVertices!;
            if (vertices.All(point => point[2] - baseZ <= BillboardPlane.Tolerance)) continue;
            var normal = face.Normal;
            double length = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1]);
            if (length < angleTolerance || Math.Abs(normal[2]) > length * angleTolerance) return null;
            if (planes.Any(plane => vertices.All(plane.Contains))) continue;
            double x = normal[0] / length, y = normal[1] / length;
            if (planes.Count == 2 || planes.Count == 1
                && Math.Abs(planes[0].X * x + planes[0].Y * y) > angleTolerance) return null;
            var plane = new BillboardPlane(x, y, x * vertices[0][0] + y * vertices[0][1]);
            if (!vertices.All(plane.Contains)) return null;
            planes.Add(plane);
        }
        return planes.Count == 2 ? planes : null;
    }

    private static (List<List<(double X, double Y)>> Rings, List<CollectedEdge> Edges)? ProjectSectionBillboard(
        List<GeoFace> faces, List<BillboardPlane> planes, double[] horizontal, double[] vertical, double[] view,
        double planeDepth)
    {
        if (Math.Abs(vertical[2] - 1) > 1e-9 || Math.Abs(view[2]) > 1e-9) return null;
        var depths = faces.SelectMany(face => face.OuterVertices!).Select(point => Project(point, view)).ToArray();
        if (depths.Min() >= planeDepth || depths.Max() <= planeDepth) return null;

        List<List<(double X, double Y)>>[] panels = [[], []];
        foreach (var face in faces)
        {
            var vertices = face.OuterVertices!;
            var ring = vertices.Select(point => (Project(point, horizontal), Project(point, vertical))).ToList();
            double area = PlanPrinter.SignedArea(ring);
            if (Math.Abs(area) < 1e-6) continue;
            if (area < 0) ring.Reverse();
            int panel = planes.FindIndex(plane => vertices.All(plane.Contains));
            if (panel >= 0) panels[panel].Add(ring);
        }
        var rings = panels.SelectMany(panel => panel).ToList();
        if (rings.Count == 0) return null;

        const int category = (int)BuiltInCategory.OST_Entourage;
        if (panels[0].Count == 0 || panels[1].Count == 0
            || planes.Any(plane => Math.Abs(ViewComponent(plane)) < 1e-9))
            return (rings, OutlineJoinedRings(rings, category, coverOppositeEdges: true).ToList());

        var (offsetA, slopeA) = ProjectedDepth(planes[0]);
        var (offsetB, slopeB) = ProjectedDepth(planes[1]);
        // The upright panels exchange depth order at one horizontal coordinate in the section.
        var visible = VisiblePanel(0, 1, offsetA - offsetB, slopeA - slopeB).ToList();
        visible.AddRange(VisiblePanel(1, 0, offsetB - offsetA, slopeB - slopeA));
        return (rings, visible);

        (double Offset, double Slope) ProjectedDepth(BillboardPlane plane)
        {
            double toward = ViewComponent(plane);
            double across = plane.X * horizontal[0] + plane.Y * horizontal[1];
            return (plane.Offset * FeetToMillimetres / toward, -across / toward);
        }

        double ViewComponent(BillboardPlane plane) => plane.X * view[0] + plane.Y * view[1];

        IEnumerable<CollectedEdge> VisiblePanel(int current, int other, double offset, double slope)
        {
            var cover = panels[other].Select(ring => ClipHalfPlane(ring,
                point => offset + slope * point.X >= 0, point => offset + slope * point.X))
                .Where(ring => ring.Count >= 3 && Math.Abs(PlanPrinter.SignedArea(ring)) > 1e-6);
            return VisibleTerrainEdges(OutlineJoinedRings(panels[current], category, coverOppositeEdges: true),
                cover, [], coverBoundary: false, coverOppositeEdges: true);
        }
    }
}
