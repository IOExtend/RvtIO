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
        List<(double X, double Y, double Radius, string Type)> Plants,
        List<List<(double X, double Y)>> Entourage,
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

        var plants = new List<(double X, double Y, double Radius, string Type)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Planting) continue;
            double[]? box = element.BoundingBox ?? element.Geometry?.BoundingBox;
            if (box is not { Length: 6 } || box[3] <= box[0] || box[4] <= box[1]) continue;
            double x = (box[0] + box[3]) / 2 * FeetToMillimetres;
            double y = (box[1] + box[4]) / 2 * FeetToMillimetres;
            double radius = Math.Min(box[3] - box[0], box[4] - box[1]) / 2 * FeetToMillimetres;
            if (radius <= 0) continue;
            plants.Add((x, y, radius, element.Name ?? ""));
            Grow(x - radius, y - radius);
            Grow(x + radius, y + radius);
        }

        // The cars and the people of the site (the entourage) print as their outlines seen from above, as the
        // reference site plan shows a car on the drive.
        var entourage = new List<List<(double X, double Y)>>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Entourage) continue;
            double[]? box = element.BoundingBox ?? element.Geometry?.BoundingBox;
            if (box is not { Length: 6 }) continue;
            double x0 = box[0] * FeetToMillimetres, y0 = box[1] * FeetToMillimetres;
            double x1 = box[3] * FeetToMillimetres, y1 = box[4] * FeetToMillimetres;
            double width = x1 - x0, height = y1 - y0;
            if (width <= 100 || height <= 100) continue;
            var polygon = new List<(double X, double Y)>();
            if (width * height >= 1_000_000)
            {
                // A car: its footprint as a rounded rectangle would be, a rectangle on the plan.
                polygon.Add((x0, y0)); polygon.Add((x1, y0)); polygon.Add((x1, y1)); polygon.Add((x0, y1));
            }
            else
            {
                double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, radius = Math.Max(width, height) / 2;
                for (int i = 0; i < 12; i++)
                {
                    double angle = i * Math.PI * 2 / 12;
                    polygon.Add((cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
                }
            }
            foreach (var point in polygon) Grow(point.X, point.Y);
            entourage.Add(polygon);
        }

        if (topography.Count == 0 && plants.Count == 0) return null;
        return new SiteContent(topography, plants, entourage, minX, minY, maxX, maxY);
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
        foreach (var (x, y, radius, _) in content.Plants)
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

        foreach (var polygon in content.Entourage)
            page.DrawPolyline(polygon.Select(point => (ToX(point.X), ToY(point.Y))).ToList(), 0.15, true, 0.75);

        // The planting of the site is tagged the way the planting schedule tags it: T1 for the first kind of plant, T2
        // for the next, each on a leader line from the plant.
        var types = content.Plants.Where(plant => plant.Type.Length > 0).Select(plant => plant.Type)
            .Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        var tagged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (page.Owner.Font != null)
        {
            foreach (var plant in content.Plants)
            {
                int index = types.IndexOf(plant.Type);
                if (index < 0) continue;
                tagged.TryGetValue(plant.Type, out int count);
                if (count >= 2) continue;
                tagged[plant.Type] = count + 1;
                double pageX = ToX(plant.X), pageY = ToY(plant.Y), r = plant.Radius / scale;
                double endX = pageX + r + 3, endY = pageY + r * 0.5 + 3 + count * 4;
                page.DrawLine(pageX + r * 0.7, pageY + r * 0.7, endX, endY, 0.08, 0.45);
                page.DrawLine(endX, endY, endX + 3, endY, 0.08, 0.45);
                page.DrawText($"T{index + 1}", endX + 3.6, endY - 0.9, 2, PdfTextAlignment.Left, 0.4);
            }
        }

        // The north arrow at the lower right of the site.
        double boxRight = (content.MaxX - content.MinX) / scale + originX;
        double boxBottom = originY;
        double northX = boxRight - 12, northY = boxBottom + 14;
        var northCircle = new List<(double X, double Y)>(24);
        for (int i = 0; i < 24; i++)
        {
            double angle = i * Math.PI * 2 / 24;
            northCircle.Add((northX + 5 * Math.Cos(angle), northY + 5 * Math.Sin(angle)));
        }
        page.DrawPolyline(northCircle, 0.12, true, (PdfColor?)null);
        page.DrawPolyline([(northX, northY - 5), (northX + 2.4, northY), (northX, northY + 5), (northX - 2.4, northY)],
            0.12, true, 0.2);
        page.DrawLine(northX, northY, northX, northY + 5, 0.18);
        page.DrawText("N", northX, northY + 6, 2.6, PdfTextAlignment.Center);
    }
}
