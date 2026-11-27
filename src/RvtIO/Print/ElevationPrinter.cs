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
public sealed record ElevationView(int ViewId, string Name, string Direction, int Scale)
{
    /// <summary>Lossless decimal native 64-bit Revit element ID when the file renumbered it to fit <see cref="ViewId"/>; null when unchanged.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceElementId { get; init; }
}

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
        // The views the project places on a sheet: a section-class view without a compass word in its name is only
        // printed when a sheet places it (the library of the file holds hundreds of family sections otherwise).
        var placedViews = new HashSet<int>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Viewports) continue;
            if (element.ViewportViewId is int placedId) placedViews.Add(placedId);
        }
        foreach (var element in document.Elements)
        {
            if (element.RootClassName is not ("DBViewSection" or "DBViewElevation") && element.NativeRecordClass != 1016)
                continue;
            if (element.LookupParameter(BuiltInParameter.VIEW_NAME)?.Value is not string name || name.Length == 0)
                continue;
            // A view of the section class is a section even when its name carries no compass word (a stair detail).
            bool sectionClass = element.RootClassName is "DBViewSection" or "DBViewElevation"
                || element.NativeRecordClass == 1016;
            bool placed = placedViews.Contains(element.Id);
            string? direction = DirectionOf(name) ?? (sectionClass && placed ? "section" : null);
            if (direction == null) continue;
            int? storedScale = element.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value as int?;
            int scale = storedScale is int value && value > 0 ? value : direction == "section" ? 0 : 100;
            // A detail section (1:10 to 1:50) is drawn from the same whole-model projection, which is not the detail:
            // only a building section (1:100 or wider, with its scale decoded) is printed, and a stair section, which
            // projects its stairs alone.
            bool section = direction == "section";
            bool planeDecoded = element.ViewDirection is { Length: 3 };
            // A section is printed when it is a building section (1:100 or wider), a stair section, or a detail section
            // whose cutting plane the reader decoded: the detail is then cut at its plane inside a window around it.
            if (section && (scale == 0 || scale < 100) && !name.Contains("stair", StringComparison.OrdinalIgnoreCase)
                && !(planeDecoded && placed))
                continue;
            views.Add(new ElevationView(element.Id, name, direction, scale)
            {
                SourceElementId = RvtIO.Sheets.SheetCatalog.SourceIdText(document.RawData, element.Id, element),
            });
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
        List<(List<(double X, double Y)> Points, double Depth)> Context,
        double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>Projects the model geometry on the vertical plane of a direction. Null when the model has no face.</summary>
    /// <summary>The categories a stair section draws: the stairs, their railings and supports.</summary>
    private static readonly HashSet<int> StairCategories =
    [
        (int)BuiltInCategory.OST_Stairs, (int)BuiltInCategory.OST_StairsRailing,
        (int)BuiltInCategory.OST_StairsSupports, (int)BuiltInCategory.OST_Lines,
    ];

    internal static ElevationContent? Collect(RvtDocument document, string direction, bool stairsOnly = false,
        Element? planeView = null, (double Width, double Height)? window = null)
    {
        double[] planeOrigin = [];
        bool cut = false;
        double[] H, V, D;
        if (planeView is { ViewOrigin: { Length: 3 } origin, ViewDirection: { Length: 3 } dir,
            ViewVerticalDirection: { Length: 3 } vert, ViewHorizontalDirection: { Length: 3 } horiz })
        {
            (H, V, D) = (horiz, vert, dir);
            // Face vertices and the detail window use model millimetres.
            planeOrigin = [origin[0] * FeetToMillimetres, origin[1] * FeetToMillimetres,
                origin[2] * FeetToMillimetres];
            // A section is cut at its plane: only what stands behind it is drawn.
            cut = direction == "section";
        }
        else
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
        (H, V, D) = direction switch
        {
            "south" => (new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }),
            "north" => (new[] { -1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, -1, 0 }),
            "east" => (new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, new[] { -1.0, 0, 0 }),
            // The view of a 3D view: an isometric projection from above the south west corner.
            "iso" => (new[] { 0.7071068, -0.7071068, 0 }, new[] { -0.4082483, -0.4082483, 0.8164966 },
                new[] { 0.5773503, 0.5773503, -0.5773503 }),
            _ => (new[] { 0.0, -1, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }),
        };
        }
        var faces = new List<(List<(double X, double Y)> Points, double Depth, bool Outline)>();
        // The plants, the topography and the site surfaces print as the faded background of an elevation, behind the
        // building, the way the reference export fades them.
        var context = new List<(List<(double X, double Y)> Points, double Depth)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var element in document.Elements)
        {
            if (element.CategoryId is (int)BuiltInCategory.OST_Planting or (int)BuiltInCategory.OST_Topography
                or (int)BuiltInCategory.OST_Site or (int)BuiltInCategory.OST_Entourage)
            {
                if (element.Geometry is { Faces.Count: > 0 } siteGeometry)
                {
                    foreach (var siteFace in siteGeometry.Faces)
                    {
                        if (siteFace.OuterVertices is not { Count: >= 3 } siteVertices) continue;
                        var sitePoints = new List<(double X, double Y)>(siteVertices.Count);
                        double siteDepth = 0;
                        foreach (var vertex in siteVertices)
                        {
                            double x = vertex[0] * FeetToMillimetres, y = vertex[1] * FeetToMillimetres;
                            double z = vertex[2] * FeetToMillimetres;
                            sitePoints.Add((x * H[0] + y * H[1] + z * H[2],
                                x * V[0] + y * V[1] + z * V[2]));
                            siteDepth += x * D[0] + y * D[1] + z * D[2];
                        }
                        // The mesh of the ground is tessellated into tens of thousands of triangles: only the larger
                        // ones shade the background, which keeps the page light.
                        double siteArea = 0;
                        for (int i = 0; i < sitePoints.Count; i++)
                        {
                            var a = sitePoints[i];
                            var b = sitePoints[(i + 1) % sitePoints.Count];
                            siteArea += a.X * b.Y - b.X * a.Y;
                        }
                        if (Math.Abs(siteArea) / 2 < 500_000 || context.Count >= 3000) continue;
                        context.Add((sitePoints, siteDepth / siteVertices.Count));
                    }
                }
                continue;
            }
            if (element.Geometry is not { Faces.Count: > 0 } geometry) continue;
            // A stair section draws the stair, not the building around it.
            if (stairsOnly && !StairCategories.Contains(element.CategoryId ?? -1)) continue;
            foreach (var face in geometry.Faces)
            {
                if (face.OuterVertices is not { Count: >= 3 } vertices) continue;
                if (face.CurvedSurface) continue;
                var corners = new List<double[]>(vertices.Count);
                foreach (var vertex in vertices)
                    corners.Add([vertex[0] * FeetToMillimetres, vertex[1] * FeetToMillimetres,
                        vertex[2] * FeetToMillimetres]);
                if (cut && planeOrigin.Length == 3)
                {
                    corners = ClipBehind(corners, planeOrigin, D);
                    if (corners.Count < 3) continue;
                }
                var points = new List<(double X, double Y)>(corners.Count);
                double depth = 0;
                foreach (var corner in corners)
                    points.Add((corner[0] * H[0] + corner[1] * H[1] + corner[2] * H[2],
                        corner[0] * V[0] + corner[1] * V[1] + corner[2] * V[2]));
                if (window is { } detail && cut && planeOrigin.Length == 3)
                {
                    // A detail section shows the part of the cut around the point of its plane, at the height its kind
                    // stands at (a roof detail high, a foundation detail low).
                    double cx = planeOrigin[0] * H[0] + planeOrigin[1] * H[1] + planeOrigin[2] * H[2];
                    double cy = planeOrigin[0] * V[0] + planeOrigin[1] * V[1] + planeOrigin[2] * V[2];
                    points = ClipToWindow(points, cx - detail.Width / 2, cx + detail.Width / 2,
                        cy - detail.Height / 2, cy + detail.Height / 2);
                    if (points.Count < 3) continue;
                    foreach (var point in points)
                    {
                        minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                        minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
                    }
                }
                foreach (var corner in corners)
                    depth += corner[0] * D[0] + corner[1] * D[1] + corner[2] * D[2];
                // A sliver of a face adds a line to the page and nothing to the drawing.
                double projected = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    var a = points[i];
                    var b = points[(i + 1) % points.Count];
                    projected += a.X * b.Y - b.X * a.Y;
                }
                if (Math.Abs(projected) / 2 < 2000) continue;
                depth /= corners.Count;
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
        // The site around the building is drawn, but it does not decide the frame: the building does, with a little
        // air around it for the trees and the ground. Context beyond that air (a tree far down the site) is dropped,
        // so the drawing stays the size of the building.
        if (context.Count > 0)
        {
            double breadth = maxX - minX, heightExtent = maxY - minY;
            double keepLeft = minX - breadth * 0.25, keepRight = maxX + breadth * 0.25;
            double keepBottom = minY - heightExtent * 0.08, keepTop = maxY + heightExtent * 0.08;
            context.RemoveAll(shape => shape.Points.Any(point => point.X < keepLeft || point.X > keepRight
                || point.Y < keepBottom || point.Y > keepTop));
            minX -= breadth * 0.02;
            maxX += breadth * 0.02;
            minY -= heightExtent * 0.02;
            maxY += heightExtent * 0.02;
        }
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
        context.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        // A detail window carries no level lines or grid bubbles: it is a detail of the construction, not a drawing of
        // the levels.
        if (window != null)
        {
            distinctLevels.Clear();
            grids.Clear();
        }
        return new ElevationContent(faces, distinctLevels, grids, context, minX, minY, maxX, maxY);
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
        PlanPrintOptions options, Element? planeView = null)
    {
        ElevationContent content = Collect(document, view.Direction, false, planeView)
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
        DrawContent(page, content, scale, offsetX, offsetY, view.Direction == "section" ? 0.82 : 1);
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

    /// <summary>The part of a projected polygon inside a rectangle: the detail window of a section.</summary>
    private static List<(double X, double Y)> ClipToWindow(List<(double X, double Y)> polygon, double left,
        double right, double bottom, double top)
    {
        var result = ClipHalfPlane(polygon, point => point.X >= left, point => point.X - left);
        result = ClipHalfPlane(result, point => point.X <= right, point => right - point.X);
        result = ClipHalfPlane(result, point => point.Y >= bottom, point => point.Y - bottom);
        result = ClipHalfPlane(result, point => point.Y <= top, point => top - point.Y);
        return result;
    }

    private static List<(double X, double Y)> ClipHalfPlane(List<(double X, double Y)> polygon,
        Func<(double X, double Y), bool> inside, Func<(double X, double Y), double> distance)
    {
        var result = new List<(double X, double Y)>(polygon.Count + 2);
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            double da = distance(a), db = distance(b);
            if (inside(a)) result.Add(a);
            if (inside(a) != inside(b))
            {
                double t = da / (da - db);
                result.Add((a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }
        return result;
    }
    /// <summary>The part of a polygon that stands behind the cutting plane (the side the view direction points
    /// to), the plane crossing a polygon adding the points where it crosses the plane.</summary>
    private static List<double[]> ClipBehind(List<double[]> polygon, double[] origin, double[] direction)
    {
        var result = new List<double[]>(polygon.Count + 2);
        for (int i = 0; i < polygon.Count; i++)
        {
            double[] a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            double da = (a[0] - origin[0]) * direction[0] + (a[1] - origin[1]) * direction[1]
                + (a[2] - origin[2]) * direction[2];
            double db = (b[0] - origin[0]) * direction[0] + (b[1] - origin[1]) * direction[1]
                + (b[2] - origin[2]) * direction[2];
            if (da >= 0) result.Add(a);
            if (da >= 0 != db >= 0)
            {
                double t = da / (da - db);
                result.Add([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t]);
            }
        }
        return result;
    }

    /// <summary>Draws the projected elevation with (MinX, MinY) at (originX, originY), one model unit per 1/scale.</summary>
    /// <summary>The section marks of a plan: the trace of every section plane on the plan, so a plan shows where it is
    /// cut, the way the reference plans show their section marks. The point is the origin of the plane, the direction
    /// the trace runs, and the name of the view the mark belongs to, all in millimetres.
    /// </summary>
    internal static List<(double X, double Y, double Dx, double Dy, string Name)> SectionMarks(RvtDocument document)
    {
        var marks = new List<(double X, double Y, double Dx, double Dy, string Name)>();
        foreach (var view in document.Elements)
        {
            if (view.CategoryId != (int)BuiltInCategory.OST_Views) continue;
            string name = view.Name ?? "";
            if (!name.Contains("section", StringComparison.OrdinalIgnoreCase)) continue;
            if (view.ViewOrigin is not { Length: 3 } origin || view.ViewDirection is not { Length: 3 } direction
                || view.ViewVerticalDirection is not { Length: 3 } vertical) continue;
            if (Math.Abs(vertical[2]) < 0.9) continue;
            // The trace of the plane runs along the horizontal in-plane axis: the cross product of the view direction
            // and the vertical of the view.
            double dx = direction[1] * vertical[2] - direction[2] * vertical[1];
            double dy = direction[2] * vertical[0] - direction[0] * vertical[2];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.2) continue;
            double x = origin[0] * FeetToMillimetres, y = origin[1] * FeetToMillimetres;
            double ux = dx / length, uy = dy / length;
            // A detail section that shares the plane of the building section marks the same cut: one mark is enough.
            if (marks.Any(mark => Math.Abs(mark.X - x) < 50 && Math.Abs(mark.Y - y) < 50
                && Math.Abs(mark.Dx * ux + mark.Dy * uy) > 0.99)) continue;
            marks.Add((x, y, ux, uy, name));
        }
        return marks;
    }

    internal static void DrawContent(PdfPage page, ElevationContent content, double scale, double originX,
        double originY, double faceFill = 1)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        // The faded site behind the building: the plants and the ground as light shapes without outlines.
        foreach (var (contextPoints, _) in content.Context)
            page.DrawPolyline(contextPoints.Select(point => (ToX(point.X), ToY(point.Y))).ToList(), 0, true, 0.9);
        foreach (var (points, _, outline) in content.Faces)
        {
            var pagePoints = points.Select(point => (ToX(point.X), ToY(point.Y))).ToList();
            // A section fills its faces grey, so the cut mass reads as a section rather than as the white of an
            // elevation.
            page.DrawPolyline(pagePoints, outline ? 0.2 : 0, true, faceFill);
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
                if (labelY - lastLabelY < 6) labelY = lastLabelY + 6;
                if (label.Length > 0 && labelY - lastLabelY >= 6)
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
