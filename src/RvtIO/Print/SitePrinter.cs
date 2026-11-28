using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>
/// Prints the site terrain, property boundaries and symbols for placed planting and entourage.
/// </summary>
public static partial class SitePrinter
{
    private const double FeetToMillimetres = 304.8;

    internal sealed record SitePlant(double X, double Y, double Radius, string Type, string Mark, bool Rpc = false);

    /// <summary>The projected site: the topography outlines, the plants and the extents they span, in millimetres.</summary>
    internal sealed record SiteContent(
        List<List<(double X, double Y)>> Topography,
        List<(double X1, double Y1, double X2, double Y2, double Z)> Contours,
        List<SitePlant> Plants,
        List<List<(double X, double Y)>> Entourage,
        double MinX, double MinY, double MaxX, double MaxY,
        IReadOnlyList<List<(double X, double Y)>>? PropertyLines = null,
        IReadOnlySet<double>? NativePrimaryLevels = null);

    /// <summary>The same site seen in a rotated view: every point is projected on the view's own axes
    /// (<paramref name="right"/> across the sheet, <paramref name="up"/> up the sheet), so a site plan whose crop
    /// basis is turned prints turned the way the reference draws it. The bounds are recomputed in the new axes.
    /// Text stays upright because the tags are drawn in page space after this projection.</summary>
    internal static SiteContent InViewBasis(SiteContent site, double[] right, double[] up)
    {
        (double X, double Y) Map(double x, double y) =>
            (x * right[0] + y * right[1], x * up[0] + y * up[1]);
        var topography = site.Topography
            .Select(polygon => polygon.Select(point => Map(point.X, point.Y)).ToList()).ToList();
        var contours = site.Contours.Select(contour =>
        {
            var a = Map(contour.X1, contour.Y1);
            var b = Map(contour.X2, contour.Y2);
            return (a.X, a.Y, b.X, b.Y, contour.Z);
        }).ToList();
        var plants = site.Plants.Select(plant =>
        {
            var centre = Map(plant.X, plant.Y);
            return plant with { X = centre.X, Y = centre.Y };
        }).ToList();
        var entourage = site.Entourage
            .Select(polygon => polygon.Select(point => Map(point.X, point.Y)).ToList()).ToList();
        var propertyLines = site.PropertyLines?
            .Select(line => line.Select(point => Map(point.X, point.Y)).ToList()).ToList();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(double x, double y)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        foreach (var polygon in topography)
            foreach (var point in polygon) Grow(point.X, point.Y);
        foreach (var (x1, y1, x2, y2, _) in contours) { Grow(x1, y1); Grow(x2, y2); }
        foreach (var plant in plants) Grow(plant.X, plant.Y);
        foreach (var polygon in entourage)
            foreach (var point in polygon) Grow(point.X, point.Y);
        if (propertyLines != null)
            foreach (var line in propertyLines)
                foreach (var point in line) Grow(point.X, point.Y);
        if (minX > maxX) return site;
        return new SiteContent(topography, contours, plants, entourage, minX, minY, maxX, maxY,
            propertyLines, site.NativePrimaryLevels);
    }

    /// <summary>Projects the topography, planting and property boundaries of the document on the plan.
    /// </summary>
    internal static SiteContent? Collect(RvtDocument document, int? viewId = null)
    {
        var hidden = viewId is int view ? document.RawData.ViewHiddenElements?.GetValueOrDefault(view) : null;
        var hiddenCategories = viewId is int categoryView
            ? document.RawData.ViewHiddenCategories?.GetValueOrDefault(categoryView) : null;
        var contours = new List<(double X1, double Y1, double X2, double Y2, double Z)>();
        HashSet<double>? nativePrimaryLevels = null;
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
            if (element.CategoryId != (int)BuiltInCategory.OST_Topography || !IsPlaced(element)) continue;
            bool hasContours = document.RawData.SiteContours?.TryGetValue(element.Id, out _) == true;
            if (hasContours)
            {
                nativePrimaryLevels ??= [];
                foreach (var contour in document.RawData.SiteContours![element.Id])
                {
                    double level = contour.Elevation * FeetToMillimetres;
                    if (contour.Primary) nativePrimaryLevels.Add(level);
                    foreach (var line in contour.Lines)
                    {
                        for (int i = 1; i < line.Count; i++)
                        {
                            var a = line[i - 1];
                            var b = line[i];
                            if (a == b) continue;
                            contours.Add((a.X * FeetToMillimetres, a.Y * FeetToMillimetres,
                                b.X * FeetToMillimetres, b.Y * FeetToMillimetres, level));
                            Grow(a.X * FeetToMillimetres, a.Y * FeetToMillimetres);
                            Grow(b.X * FeetToMillimetres, b.Y * FeetToMillimetres);
                        }
                    }
                }
            }
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
                if (!hasContours) topography3.Add(corners);
                if (topography.Count >= 20000) break;
            }
        }

        var plants = new List<SitePlant>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Planting || !IsPlaced(element)) continue;
            double[]? box = element.BoundingBox ?? element.Geometry?.BoundingBox;
            if (box is not { Length: 6 } || box[3] <= box[0] || box[4] <= box[1]) continue;
            double x = (element.InstanceOriginXY?.X ?? (box[0] + box[3]) / 2) * FeetToMillimetres;
            double y = (element.InstanceOriginXY?.Y ?? (box[1] + box[4]) / 2) * FeetToMillimetres;
            double? rpcRadius = RpcPlanRadius(document, element);
            double radius = rpcRadius ?? Math.Min(box[3] - box[0], box[4] - box[1]) / 2 * FeetToMillimetres;
            if (radius <= 0) continue;
            plants.Add(new(x, y, radius, element.Name ?? "", PlantingTypeOf(document, element).Mark, rpcRadius.HasValue));
            Grow(x - radius, y - radius);
            Grow(x + radius, y + radius);
        }

        // Visible entourage uses the placed instance bounds for its plan outline.
        var entourage = new List<List<(double X, double Y)>>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Entourage || !IsPlaced(element)) continue;
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

        var propertyLines = new List<List<(double X, double Y)>>();
        foreach (var element in document.Elements)
        {
            if (PropertyLineHidden(hiddenCategories) || !IsPlaced(element) || element.SitePropertyCurve is not { Count: >= 2 } curve)
                continue;
            var line = curve.Select(p => (X: p.X * FeetToMillimetres, Y: p.Y * FeetToMillimetres)).ToList();
            propertyLines.Add(line);
            foreach (var point in line) Grow(point.X, point.Y);
        }
        if (topography.Count == 0 && contours.Count == 0 && plants.Count == 0 && propertyLines.Count == 0) return null;
        return new SiteContent(topography, contours, plants, entourage, minX, minY, maxX, maxY,
            propertyLines, nativePrimaryLevels);

        bool IsPlaced(Element element) => !element.IsDiscovered && element.SerializedUnplacedOwnerId is not > 0
            && element.NativeIsSymbol != true && !element.RootClassIs("FamilySymbol") && hidden?.Contains(element.Id) != true
            && (element.CategoryId is not int category || hiddenCategories?.Contains(category) != true);
    }

    /// <summary>True when the view hides the property boundaries. A segment carries its own category, but a view hides
    /// the boundary through "Property Lines", the category the lines are inked with.</summary>
    internal static bool PropertyLineHidden(HashSet<int>? hiddenCategories) =>
        hiddenCategories?.Contains((int)BuiltInCategory.OST_SiteProperty) == true;

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
    internal static void DrawContent(PdfPage page, SiteContent content, double scale, double originX, double originY,
        RvtDocument? document = null, bool opaqueGround = false, SiteGraphics? graphics = null, bool drawFallbackTags = true,
        bool drawFallbackNorth = true, int? viewId = null, ViewCropFrame? basis = null)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        (double Width, PdfColor Color)? ProjectionPen(BuiltInCategory category)
        {
            if (document?.RawData.PenWidthTable is not { } table
                || !PenWidths.TryGetStyle(document, (int)category, false, out int pen, out int rgb)
                || rgb < 0 || rgb > 0xFFFFFF
                || !PenWidths.TryPenWidth(table, pen, (int)Math.Round(scale), out double width)) return null;
            return (width, new PdfColor((rgb & 255) / 255d, ((rgb >> 8) & 255) / 255d, (rgb >> 16) / 255d));
        }

        // Only the outer edge of the terrain mesh is inked, so the triangulation does not cover the plan.
        PdfColor ground = opaqueGround ? new PdfColor(1, 1, 1) : new PdfColor(0.92, 0.95, 0.9);
        var edges = new Dictionary<string, int>(StringComparer.Ordinal);
        var edgeLines = new Dictionary<string, ((double X, double Y) A, (double X, double Y) B)>(StringComparer.Ordinal);
        foreach (var polygon in content.Topography)
        {
            var points = polygon.Select(point => (ToX(point.X), ToY(point.Y))).ToList();
            page.DrawPolyline(points, 0, true, ground);
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
        if (graphics is { GroundShadows.Count: > 0 })
            page.FillPolygonUnion(graphics.GroundShadows.Select(ring => ring.Select(p => (ToX(p.X), ToY(p.Y))).ToList()),
                graphics.ShadowGray);

        var primary = content.NativePrimaryLevels == null ? null : ProjectionPen(BuiltInCategory.OST_TopographyContours);
        var secondary = content.NativePrimaryLevels == null ? null : ProjectionPen(BuiltInCategory.OST_SecondaryTopographyContours);
        var contourLevels = content.NativePrimaryLevels == null
            ? content.Contours.Select(contour => contour.Z).Distinct().OrderBy(level => level).ToList() : null;
        var contourLabels = ContourLabels(page, document, viewId, basis, ToX, ToY);
        foreach (var (x1, y1, x2, y2, level) in content.Contours)
        {
            bool index = content.NativePrimaryLevels?.Contains(level) ?? contourLevels!.IndexOf(level) % 5 == 0;
            var pen = index ? primary : secondary;
            double ax = ToX(x1), ay = ToY(y1), bx = ToX(x2), by = ToY(y2);
            foreach (var (start, end) in ContourSpans(ax, ay, bx, by, level, contourLabels))
            {
                double sx = ax + start * (bx - ax), sy = ay + start * (by - ay);
                double ex = ax + end * (bx - ax), ey = ay + end * (by - ay);
                if (pen is { } style) page.DrawLine(sx, sy, ex, ey, style.Width, style.Color);
                else page.DrawLine(sx, sy, ex, ey, index ? 0.16 : 0.07, index ? 0.5 : 0.62);
            }
        }
        if (graphics is { Equipment: not null } && document != null)
            DrawEquipment(page, document, graphics, basis, scale, ToX, ToY);
        foreach (var label in contourLabels)
            page.DrawTextRotated(label.Text, label.X + label.Sine * label.Baseline,
                label.Y - label.Cosine * label.Baseline, label.Height, label.Angle * 180 / Math.PI,
                PdfTextAlignment.Center);
        if (content.PropertyLines is { Count: > 0 } properties)
        {
            double pen = 0.18;
            int category = (int)BuiltInCategory.OST_SiteProperty;
            if (document != null && PenWidths.TryGetWidth(document, category, false, (int)Math.Round(scale), out double width))
                pen = width;
            double[]? dashes = document != null && LinePatterns.TryGetDashes(document, category, out double[] pattern)
                ? pattern : null;
            foreach (var line in properties)
            {
                var points = line.Select(p => (X: ToX(p.X), Y: ToY(p.Y))).ToList();
                if (dashes == null)
                {
                    page.DrawPolyline(points, pen);
                    continue;
                }
                double length = 0;
                for (int i = 1; i < points.Count; i++)
                {
                    double dx = points[i].X - points[i - 1].X, dy = points[i].Y - points[i - 1].Y;
                    length += Math.Sqrt(dx * dx + dy * dy);
                }
                page.DrawRoundDashedPolyline(points, pen, LinePatterns.FitEndpointDashes(length * (72 / 25.4), dashes));
            }
        }
        var plantingPen = ProjectionPen(BuiltInCategory.OST_Planting) ?? (0.1, new PdfColor(0, 0, 0));
        foreach (var plant in content.Plants)
        {
            double pageX = ToX(plant.X), pageY = ToY(plant.Y), r = plant.Radius / scale;
            if (plant.Rpc)
            {
                DrawRpcPlant(page, pageX, pageY, r, basis, plantingPen.Item1, plantingPen.Item2);
                continue;
            }
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

        // The planting of the site is tagged with the type mark of the planting schedule (T1, T2, ...),
        // each on a leader line from the plant; untagged kinds fall back to their order in the schedule.
        var types = content.Plants.Where(plant => plant.Type.Length > 0).Select(plant => plant.Type)
            .Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        var tagged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (drawFallbackTags && page.Owner.Font != null)
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

        if (!drawFallbackNorth) return;
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
