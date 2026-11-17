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
    /// Each plant carries its schedule key: the type mark (T1, T2, ...) for its tag, empty when unknown.</summary>
    internal sealed record SiteContent(
        List<List<(double X, double Y)>> Topography,
        List<(double X1, double Y1, double X2, double Y2, double Z)> Contours,
        List<(double X, double Y, double Radius, string Type, string Mark)> Plants,
        List<List<(double X, double Y)>> Entourage,
        double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>Projects the topography and the planting of the document on the plan. Null when the model has neither.
    /// </summary>
    internal static SiteContent? Collect(RvtDocument document)
    {
        var topography = new List<List<(double X, double Y)>>();
        var topography3 = new List<List<double[]>>();
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
                var corners = new List<double[]>(vertices.Count);
                foreach (var vertex in vertices)
                {
                    double x = vertex[0] * FeetToMillimetres, y = vertex[1] * FeetToMillimetres;
                    double z = vertex[2] * FeetToMillimetres;
                    polygon.Add((x, y));
                    corners.Add([x, y, z]);
                    Grow(x, y);
                }
                topography.Add(polygon);
                topography3.Add(corners);
                if (topography.Count >= 20000) break;
            }
        }

        var plants = new List<(double X, double Y, double Radius, string Type, string Mark)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Planting) continue;
            double[]? box = element.BoundingBox ?? element.Geometry?.BoundingBox;
            if (box is not { Length: 6 } || box[3] <= box[0] || box[4] <= box[1]) continue;
            double x = (box[0] + box[3]) / 2 * FeetToMillimetres;
            double y = (box[1] + box[4]) / 2 * FeetToMillimetres;
            double radius = Math.Min(box[3] - box[0], box[4] - box[1]) / 2 * FeetToMillimetres;
            if (radius <= 0) continue;
            plants.Add((x, y, radius, element.Name ?? "", PlantingTypeOf(document, element).Mark));
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

        // The contours of the terrain: the mesh is sliced at every half metre (a metre on a high site) and each slice
        // is drawn as the lines a site plan prints, the way the reference shows the fall of the ground.
        var contours = new List<(double X1, double Y1, double X2, double Y2, double Z)>();
        if (topography3.Count > 0)
        {
            double zMin = topography3.Min(corners => corners.Min(corner => corner[2]));
            double zMax = topography3.Max(corners => corners.Max(corner => corner[2]));
            double step = zMax - zMin > 3000 ? 1000 : 500;
            for (double level = Math.Ceiling(zMin / step) * step; level <= zMax && contours.Count < 20000; level += step)
            {
                foreach (var corners in topography3)
                {
                    if (corners.Count < 3) continue;
                    var crossings = new List<(double X, double Y)>(2);
                    for (int i = 0; i < corners.Count && crossings.Count < 2; i++)
                    {
                        double[] a = corners[i], b = corners[(i + 1) % corners.Count];
                        if (a[2] == level || b[2] == level
                            || (a[2] < level) == (b[2] < level)) continue;
                        double t = (level - a[2]) / (b[2] - a[2]);
                        crossings.Add((a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t));
                    }
                    if (crossings.Count == 2)
                        contours.Add((crossings[0].X, crossings[0].Y, crossings[1].X, crossings[1].Y, level));
                    if (contours.Count >= 20000) break;
                }
            }
        }

        if (topography.Count == 0 && plants.Count == 0) return null;
        return new SiteContent(topography, contours, plants, entourage, minX, minY, maxX, maxY);
    }

    /// <summary>The schedule key of a planting instance: the type mark (its ALL_MODEL_TYPE_MARK) and the
    /// common name (its ALL_MODEL_DESCRIPTION, the "Common Name" the planting schedule lists), read from its
    /// type when the type carries them, else from the instance itself. Empty when the file names neither.</summary>
    internal static (string Mark, string CommonName) PlantingTypeOf(RvtDocument document, Element element)
    {
        var type = element.TypeId > 0 ? document.GetElement(element.TypeId) : null;
        if (type == element) type = null;
        string mark = (type?.LookupParameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.Value as string
            ?? element.LookupParameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.Value as string) ?? "";
        string common = (type?.LookupParameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.Value as string
            ?? element.LookupParameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.Value as string) ?? "";
        return (mark, common);
    }

    /// <summary>Draws the site with (MinX, MinY) at (originX, originY), one model unit per 1/scale.</summary>
    internal static void DrawContent(PdfPage page, SiteContent content, double scale, double originX, double originY)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        // The reference site plan draws no terrain, contours, planting graphics or entourage over the paper:
        // only the planting tags and the north arrow are inked (the fall of the ground reads from its spot
        // elevations, the planting from its schedule). Each tag stands on a leader line from its plant;
        // untagged kinds fall back to their order in the schedule.
        var types = content.Plants.Where(plant => plant.Type.Length > 0).Select(plant => plant.Type)
            .Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        var tagged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (page.Owner.Font != null)
        {
            foreach (var plant in content.Plants)
            {
                int index = types.IndexOf(plant.Type);
                if (index < 0 && plant.Mark.Length == 0) continue;
                string tag = plant.Mark.Length > 0 ? plant.Mark : $"T{index + 1}";
                tagged.TryGetValue(tag, out int count);
                if (count >= 2) continue;
                tagged[tag] = count + 1;
                double pageX = ToX(plant.X), pageY = ToY(plant.Y), r = plant.Radius / scale;
                double endX = pageX + r + 3, endY = pageY + r * 0.5 + 3 + count * 4;
                page.DrawLine(pageX + r * 0.7, pageY + r * 0.7, endX, endY, 0.08, 0.45);
                page.DrawLine(endX, endY, endX + 3, endY, 0.08, 0.45);
                page.DrawText(tag, endX + 3.6, endY - 0.9, 2, PdfTextAlignment.Left, 0.4);
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