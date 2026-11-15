namespace RvtIO.Print;

using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

/// <summary>An elevation view that can be printed: its element id, name and the direction it looks at, decoded from the
/// name (North, South, East, West).</summary>
/// <param name="ViewId">The element id of the view.</param>
/// <param name="Name">The view name.</param>
/// <param name="Direction">The direction the view looks at: north, south, east or west.</param>
/// <param name="Scale">The view scale denominator.</param>
public sealed record ElevationView(int ViewId, string Name, string Direction, int Scale);

/// <summary>
/// Prints an elevation from the 3D geometry the reader decodes: every planar face of the model (walls, floors, roofs,
/// columns, windows and doors) is projected on the vertical plane of the view, painted white from the farthest to the
/// nearest and outlined when it faces the viewer. The view plane itself is not decoded yet, so the direction is taken
/// from the view name (North, South, East, West); a section is not printed (see docs/printing.md).
/// </summary>
public static class ElevationPrinter
{
    private const double FeetToMillimetres = 304.8;

    /// <summary>The elevation views of the document: named North, South, East or West, by name.</summary>
    /// <param name="document">The opened document.</param>
    public static IReadOnlyList<ElevationView> Views(RvtDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var views = new List<ElevationView>();
        foreach (var element in document.Elements)
        {
            if (element.RootClassName is not ("DBViewSection" or "DBViewElevation") && element.NativeRecordClass != 1016)
                continue;
            if (element.LookupParameter(BuiltInParameter.VIEW_NAME)?.Value is not string name || name.Length == 0)
                continue;
            string? direction = DirectionOf(name);
            if (direction == null) continue;
            int scale = element.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value is int value && value > 0 ? value : 100;
            // A detail section (1:10 to 1:50) is drawn from the same whole-model projection, which is not the detail:
            // only a building section (1:100 or wider) is printed.
            if (direction == "section" && scale < 100 && !name.Contains("stair", StringComparison.OrdinalIgnoreCase))
                continue;
            views.Add(new ElevationView(element.Id, name, direction, scale));
        }
        views.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return views;
    }

    /// <summary>The direction a view name looks at, or null when the name is not a compass elevation.</summary>
    internal static string? DirectionOf(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("north")) return "north";
        if (lower.Contains("south")) return "south";
        if (lower.Contains("east")) return "east";
        if (lower.Contains("west")) return "west";
        if (lower.Contains("section")) return "section";
        return null;
    }

    /// <summary>The projected faces of an elevation: the polygons in view millimetres, the frame extents and the
    /// faces that get an outline.</summary>
    internal sealed record ElevationContent(
        List<(List<(double X, double Y)> Points, double Depth, bool Outline)> Faces,
        List<(double Y, string Label)> Levels,
        List<(double X, string Name)> Grids,
        double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>Projects the model geometry on the vertical plane of a direction. Null when the model has no face.</summary>
    /// <summary>The categories a stair section draws: the stairs, their railings and supports.</summary>
    private static readonly HashSet<int> StairCategories =
    [
        (int)BuiltInCategory.OST_Stairs, (int)BuiltInCategory.OST_StairsRailing,
        (int)BuiltInCategory.OST_StairsSupports, (int)BuiltInCategory.OST_Lines,
    ];

    internal static ElevationContent? Collect(RvtDocument document, string direction, bool stairsOnly = false)
    {
        if (direction == "section")
        {
            // A section is not decoded to its cutting plane yet: it is projected along the short axis of the building,
            // the way a longitudinal section looks.
            double wallMinX = double.MaxValue, wallMinY = double.MaxValue;
            double wallMaxX = double.MinValue, wallMaxY = double.MinValue;
            foreach (var element in document.Elements)
            {
                if (element.CategoryId != (int)BuiltInCategory.OST_Walls) continue;
                if (element.Location is not { CurveType: "Line" } line) continue;
                foreach (var point in new[] { line.StartPoint, line.EndPoint })
                {
                    double x = point[0] * FeetToMillimetres, y = point[1] * FeetToMillimetres;
                    wallMinX = Math.Min(wallMinX, x); wallMaxX = Math.Max(wallMaxX, x);
                    wallMinY = Math.Min(wallMinY, y); wallMaxY = Math.Max(wallMaxY, y);
                }
            }
            direction = wallMaxX - wallMinX >= wallMaxY - wallMinY ? "south" : "east";
        }
        (double[] H, double[] V, double[] D) = direction switch
        {
            "south" => (new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }),
            "north" => (new[] { -1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, -1, 0 }),
            "east" => (new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, new[] { -1.0, 0, 0 }),
            _ => (new[] { 0.0, -1, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }),
        };
        var faces = new List<(List<(double X, double Y)> Points, double Depth, bool Outline)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var element in document.Elements)
        {
            // The plants and the site surfaces are drawn in an elevation as symbols, not as their tessellated faces:
            // projecting them buries the building under a mesh of leaves and ground.
            if (element.CategoryId is (int)BuiltInCategory.OST_Planting or (int)BuiltInCategory.OST_Topography
                or (int)BuiltInCategory.OST_Site) continue;
            if (element.Geometry is not { Faces.Count: > 0 } geometry) continue;
            // A stair section draws the stair, not the building around it.
            if (stairsOnly && !StairCategories.Contains(element.CategoryId ?? -1)) continue;
            foreach (var face in geometry.Faces)
            {
                if (face.OuterVertices is not { Count: >= 3 } vertices) continue;
                if (face.CurvedSurface) continue;
                var points = new List<(double X, double Y)>(vertices.Count);
                double depth = 0;
                foreach (var vertex in vertices)
                {
                    double x = vertex[0] * FeetToMillimetres, y = vertex[1] * FeetToMillimetres;
                    double z = vertex[2] * FeetToMillimetres;
                    points.Add((x * H[0] + y * H[1] + z * H[2], z));
                    depth += x * D[0] + y * D[1] + z * D[2];
                }
                // A sliver of a face adds a line to the page and nothing to the drawing.
                double projected = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    var a = points[i];
                    var b = points[(i + 1) % points.Count];
                    projected += a.X * b.Y - b.X * a.Y;
                }
                if (Math.Abs(projected) / 2 < 2000) continue;
                depth /= vertices.Count;
                double facing = Math.Abs(face.Normal[0] * D[0] + face.Normal[1] * D[1] + face.Normal[2] * D[2]);
                foreach (var (x, y) in points)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
                faces.Add((points, depth, facing > 0.5));
            }
        }
        if (faces.Count == 0) return null;
        // Far to near: painter's algorithm, so a nearer wall hides the one behind it.
        faces.Sort((left, right) => right.Depth.CompareTo(left.Depth));

        // The levels of the model and the grids that stand across the view are drawn dashed, as an elevation prints
        // them, with the level name and the grid bubble.
        var levels = new List<(double Y, string Label)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Levels) continue;
            if (element.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value is not double elevation) continue;
            levels.Add((elevation * FeetToMillimetres, element.Name ?? ""));
        }
        levels.Sort((left, right) => left.Y.CompareTo(right.Y));
        // The project holds many level elements at the same height (one per family); an elevation notes each height
        // once, with the first name it finds.
        var distinctLevels = new List<(double Y, string Label)>();
        foreach (var level in levels)
        {
            if (distinctLevels.Count > 0 && Math.Abs(distinctLevels[^1].Y - level.Y) < 1.0)
            {
                if (distinctLevels[^1].Label.Length == 0)
                    distinctLevels[^1] = (distinctLevels[^1].Y, level.Label);
                continue;
            }
            distinctLevels.Add(level);
        }
        var grids = new List<(double X, string Name)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Grids) continue;
            if (element.Location is not { CurveType: "Line" } line) continue;
            double dx = line.EndPoint[0] - line.StartPoint[0], dy = line.EndPoint[1] - line.StartPoint[1];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) continue;
            if (Math.Abs(dx / length * H[0] + dy / length * H[1]) > 0.2) continue;
            double x = (line.StartPoint[0] * H[0] + line.StartPoint[1] * H[1]) * FeetToMillimetres;
            grids.Add((x, element.Name ?? ""));
        }
        grids.Sort((left, right) => left.X.CompareTo(right.X));
        if (grids.Count > 0) maxY += 4500;
        return new ElevationContent(faces, distinctLevels, grids, minX, minY, maxX, maxY);
    }

    /// <summary>Prints one elevation view to a one-page PDF document at the view's scale.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="viewId">The element id of the view; it must be one of <see cref="Views"/>.</param>
    /// <param name="options">The paper and margin; null uses the defaults.</param>
    public static PdfDocument Render(RvtDocument document, int viewId, PlanPrintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PlanPrintOptions();
        var views = new Dictionary<int, ElevationView>();
        foreach (var view in Views(document)) views[view.ViewId] = view;
        if (!views.TryGetValue(viewId, out var elevation))
            throw new InvalidOperationException($"View {viewId} is not a compass elevation (see ElevationPrinter.Views).");
        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _), Compress = true };
        AddElevationPage(pdf, document, elevation, options);
        return pdf;
    }

    /// <summary>Draws one elevation as a page of the document.</summary>
    internal static void AddElevationPage(PdfDocument pdf, RvtDocument document, ElevationView view,
        PlanPrintOptions options)
    {
        ElevationContent content = Collect(document, view.Direction)
            ?? throw new InvalidOperationException($"The model has no face to project for the {view.Name} elevation.");
        double width = (content.MaxX - content.MinX) / view.Scale + LabelMarginMillimetres;
        double height = (content.MaxY - content.MinY) / view.Scale;
        PdfPageSize paper = options.PaperSize ?? (width + 2 * options.MarginMillimetres <= PdfPageSize.A4.Landscape.WidthMillimetres
            && height + 2 * options.MarginMillimetres <= PdfPageSize.A4.Landscape.HeightMillimetres
            ? PdfPageSize.A4.Landscape
            : width + 2 * options.MarginMillimetres <= PdfPageSize.A3.Landscape.WidthMillimetres
                && height + 2 * options.MarginMillimetres <= PdfPageSize.A3.Landscape.HeightMillimetres
                ? PdfPageSize.A3.Landscape
                : width + 2 * options.MarginMillimetres <= PdfPageSize.A2.Landscape.WidthMillimetres
                    && height + 2 * options.MarginMillimetres <= PdfPageSize.A2.Landscape.HeightMillimetres
                    ? PdfPageSize.A2.Landscape
                    : PdfPageSize.A1.Landscape);
        double usableWidth = paper.WidthMillimetres - 2 * options.MarginMillimetres;
        double usableHeight = paper.HeightMillimetres - 2 * options.MarginMillimetres;
        double scale = view.Scale;
        if (width > usableWidth || height > usableHeight)
            scale = view.Scale * Math.Max(width / usableWidth, height / usableHeight);
        double offsetX = (paper.WidthMillimetres - (content.MaxX - content.MinX) / scale - LabelMarginMillimetres) / 2;
        double offsetY = (paper.HeightMillimetres - (content.MaxY - content.MinY) / scale) / 2;
        PdfPage page = pdf.AddPage(paper);
        if (options.DrawFrame)
        {
            double m = options.MarginMillimetres;
            page.DrawRectangle(m, m, paper.WidthMillimetres - 2 * m, paper.HeightMillimetres - 2 * m, 0.25);
        }
        DrawContent(page, content, scale, offsetX, offsetY);
        if (pdf.Font != null)
        {
            page.DrawText(view.Name, options.MarginMillimetres + 2, paper.HeightMillimetres - options.MarginMillimetres - 6, 4);
            page.DrawText($"1:{view.Scale}", options.MarginMillimetres + 2,
                paper.HeightMillimetres - options.MarginMillimetres - 11, 3, PdfTextAlignment.Left, 0.35);
        }
    }

    /// <summary>The width reserved to the right of an elevation for the level markers and their labels, in
    /// millimetres of the page.</summary>
    internal const double LabelMarginMillimetres = 42;

    /// <summary>Draws the projected elevation with (MinX, MinY) at (originX, originY), one model unit per 1/scale.</summary>
    internal static void DrawContent(PdfPage page, ElevationContent content, double scale, double originX,
        double originY)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        foreach (var (points, _, outline) in content.Faces)
        {
            var pagePoints = points.Select(point => (ToX(point.X), ToY(point.Y))).ToList();
            page.DrawPolyline(pagePoints, outline ? 0.2 : 0, true, 1);
        }

        // Grids first, then the levels: both dashed and light, over the faces the way an elevation prints them.
        if (content.Grids.Count > 0)
        {
            page.SetDash(2, 1.5);
            double top = ToY(content.MaxY) - 8;
            foreach (var (x, name) in content.Grids)
            {
                double pageX = ToX(x);
                page.DrawLine(pageX, ToY(content.MinY), pageX, top, 0.18, 0.55);
                var circle = new List<(double X, double Y)>(24);
                for (int i = 0; i < 24; i++)
                {
                    double angle = i * Math.PI * 2 / 24;
                    circle.Add((pageX + 3.5 * Math.Cos(angle), top + 3.5 + 3.5 * Math.Sin(angle)));
                }
                page.DrawPolyline(circle, 0.18, true, 1);
                if (name.Length > 0) page.DrawText(name, pageX, top + 2.4, 2.8, PdfTextAlignment.Center);
            }
            page.ResetDash();
        }
        if (content.Levels.Count > 0)
        {
            page.SetDash(4, 1.5);
            double lastLabelY = double.NegativeInfinity;
            foreach (var (y, label) in content.Levels)
            {
                double pageY = ToY(y);
                if (pageY < ToY(content.MinY) - 1 || pageY > ToY(content.MaxY) + 1) continue;
                double left = ToX(content.MinX), right = ToX(content.MaxX);
                page.DrawLine(left, pageY, right, pageY, 0.15, 0.55);
                // The level marker at the right end of the line: a circle with an arrow head, the level name above its
                // height in millimetres, as an elevation notes them.
                double markerX = right + 4;
                page.DrawLine(right, pageY, markerX - 2.5, pageY, 0.12, 0.4);
                var marker = new List<(double X, double Y)>(20);
                for (int i = 0; i < 20; i++)
                {
                    double angle = i * Math.PI * 2 / 20;
                    marker.Add((markerX + 2.5 * Math.Cos(angle), pageY + 2.5 * Math.Sin(angle)));
                }
                page.DrawPolyline(marker, 0.12, true, 1);
                page.DrawLine(markerX - 3.5, pageY, markerX, pageY, 0.15);
                page.DrawLine(markerX, pageY, markerX - 1.5, pageY + 1.1, 0.15);
                page.DrawLine(markerX, pageY, markerX - 1.5, pageY - 1.1, 0.15);
                // Two levels a few millimetres apart (Ceiling over Level 1) would print their labels on top of each
                // other; the label is pushed down instead.
                double labelY = pageY;
                if (labelY - lastLabelY < 5) labelY = lastLabelY + 5;
                if (label.Length > 0 && labelY - lastLabelY >= 5)
                {
                    page.DrawText(label, markerX + 4, labelY + 0.6, 2.2, PdfTextAlignment.Left, 0.4);
                    page.DrawText($"{y:0}", markerX + 4, labelY - 3.6, 2, PdfTextAlignment.Left, 0.4);
                    page.DrawLine(markerX + 2.5, pageY, markerX + 3.5, labelY + 0.4, 0.12, 0.4);
                    lastLabelY = labelY;
                }
            }
            page.ResetDash();
        }
    }
}
