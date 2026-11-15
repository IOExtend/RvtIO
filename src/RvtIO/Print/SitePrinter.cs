using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>
/// Prints a site plan: the topography of the model (the edges of its faces seen from above) and the planting of the
/// site (a circle with four spokes at each plant). A site view is not the plan of a level, so it is drawn from the site
/// entities rather than from walls and rooms.
/// </summary>
public static class SitePrinter
{
    private const double FeetToMillimetres = 304.8;

    /// <summary>The projected site: the topography outlines, the plants and the extents they span, in millimetres.
    /// </summary>
    internal sealed record SiteContent(
        List<List<(double X, double Y)>> Topography,
        List<(double X, double Y, double Radius)> Plants,
        double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>Projects the topography and the planting of the document on the plan. Null when the model has neither.
    /// </summary>
    internal static SiteContent? Collect(RvtDocument document)
    {
        var topography = new List<List<(double X, double Y)>>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(double x, double y)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Topography) continue;
            if (element.Geometry is not { Faces.Count: > 0 } geometry) continue;
            foreach (var face in geometry.Faces)
            {
                if (face.OuterVertices is not { Count: >= 3 } vertices) continue;
                var polygon = new List<(double X, double Y)>(vertices.Count);
                foreach (var vertex in vertices)
                {
                    double x = vertex[0] * FeetToMillimetres, y = vertex[1] * FeetToMillimetres;
                    polygon.Add((x, y));
                    Grow(x, y);
                }
                topography.Add(polygon);
                if (topography.Count >= 20000) break;
            }
        }

        var plants = new List<(double X, double Y, double Radius)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Planting) continue;
            double[]? box = element.BoundingBox ?? element.Geometry?.BoundingBox;
            if (box is not { Length: 6 } || box[3] <= box[0] || box[4] <= box[1]) continue;
            double x = (box[0] + box[3]) / 2 * FeetToMillimetres;
            double y = (box[1] + box[4]) / 2 * FeetToMillimetres;
            double radius = Math.Min(box[3] - box[0], box[4] - box[1]) / 2 * FeetToMillimetres;
            if (radius <= 0) continue;
            plants.Add((x, y, radius));
            Grow(x - radius, y - radius);
            Grow(x + radius, y + radius);
        }

        if (topography.Count == 0 && plants.Count == 0) return null;
        return new SiteContent(topography, plants, minX, minY, maxX, maxY);
    }

    /// <summary>Draws the site with (MinX, MinY) at (originX, originY), one model unit per 1/scale.</summary>
    internal static void DrawContent(PdfPage page, SiteContent content, double scale, double originX, double originY)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        // A light green wash under the site; only the outer edge of the terrain mesh is inked, so the triangulation does
        // not cover the plan.
        var edges = new Dictionary<string, int>(StringComparer.Ordinal);
        var edgeLines = new Dictionary<string, ((double X, double Y) A, (double X, double Y) B)>(StringComparer.Ordinal);
        foreach (var polygon in content.Topography)
        {
            var points = polygon.Select(point => (ToX(point.X), ToY(point.Y))).ToList();
            page.DrawPolyline(points, 0, true, new PdfColor(0.92, 0.95, 0.9));
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                string ka = $"{Math.Round(a.Item1, 1)}:{Math.Round(a.Item2, 1)}";
                string kb = $"{Math.Round(b.Item1, 1)}:{Math.Round(b.Item2, 1)}";
                // The two triangles that share an edge walk it in opposite directions: the key ignores the direction,
                // so a shared edge counts twice and only the outer edge of the terrain stays at one.
                string key = string.CompareOrdinal(ka, kb) <= 0 ? ka + "|" + kb : kb + "|" + ka;
                edges[key] = edges.TryGetValue(key, out int count) ? count + 1 : 1;
                edgeLines[key] = (a, b);
            }
        }
        foreach (var (key, count) in edges)
        {
            if (count != 1 || !edgeLines.TryGetValue(key, out var line)) continue;
            page.DrawLine(line.A.Item1, line.A.Item2, line.B.Item1, line.B.Item2, 0.15, 0.45);
        }
        foreach (var (x, y, radius) in content.Plants)
        {
            double pageX = ToX(x), pageY = ToY(y), r = radius / scale;
            var circle = new List<(double X, double Y)>(24);
            for (int i = 0; i < 24; i++)
            {
                double angle = i * Math.PI * 2 / 24;
                circle.Add((pageX + r * Math.Cos(angle), pageY + r * Math.Sin(angle)));
            }
            page.DrawPolyline(circle, 0.12, true, (PdfColor?)null);
            page.DrawLine(pageX - r, pageY, pageX + r, pageY, 0.08, 0.45);
            page.DrawLine(pageX, pageY - r, pageX, pageY + r, 0.08, 0.45);
        }
    }
}
