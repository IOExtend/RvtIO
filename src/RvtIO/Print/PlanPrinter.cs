using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>Options of a plan page printing: the paper, the margin, the width a wall without a stored width is drawn
/// with, and whether the page frame is drawn.</summary>
public sealed class PlanPrintOptions
{
    /// <summary>The paper size; null chooses the smallest of A4 to A0 that holds the drawing at the view scale.</summary>
    public PdfPageSize? PaperSize { get; set; }

    /// <summary>The margin around the drawing, in millimetres.</summary>
    public double MarginMillimetres { get; set; } = 10;

    /// <summary>The width in millimetres a wall is drawn with when neither the wall nor its type stores one.</summary>
    public double FallbackWallWidthMillimetres { get; set; } = 100;

    /// <summary>True (the default) draws the page frame at the margin.</summary>
    public bool DrawFrame { get; set; } = true;
}

/// <summary>A level a plan can be printed for: the element id, its name and the elevation the file stores for it.</summary>
/// <param name="LevelId">The element id of the level.</param>
/// <param name="Name">The level name, when the reader decoded one.</param>
/// <param name="ElevationFeet">The stored elevation in feet, null when the reader did not decode it.</param>
public sealed record PlanLevel(int LevelId, string? Name, double? ElevationFeet);

/// <summary>A view a plan can be printed for: a named plan view whose name or stored elevation matches a printable
/// level. The scale is the view's own view scale.</summary>
/// <param name="ViewId">The element id of the view.</param>
/// <param name="Name">The view name.</param>
/// <param name="LevelId">The level the plan of the view is drawn from.</param>
/// <param name="Scale">The view scale denominator, 100 for 1:100.</param>
public sealed record PlanView(int ViewId, string Name, int LevelId, int Scale);

/// <summary>
/// Prints plans to PDF pages from the data the reader decodes: walls as their location line with the stored thickness,
/// walls cut at the openings of their hosted doors and windows, columns as their stored footprint, grids as their
/// location lines, with a frame and a title. This is the view renderer of the sheet printing work: it draws the model
/// geometry, not Revit's generated linework, so floors, stairs, annotations, room fills, dimensions, tags and the title
/// block are not on the page yet (see the notes in docs/printing.md).
/// </summary>
public static class PlanPrinter
{
    private const double FeetToMillimetres = 304.8;

    /// <summary>The plan content of a level in model millimetres: the walls, columns and grids to draw and the
    /// extents they span.</summary>
    internal sealed record PlanContent(
        List<(int Id, double X1, double Y1, double X2, double Y2, double Width)> Walls,
        List<(double X, double Y, double Width, double Height)> Columns,
        List<(double X1, double Y1, double X2, double Y2, string Name)> Grids,
        Dictionary<int, List<(double From, double To)>> Gaps,
        List<(int HostId, double From, double To)> WindowGaps,
        List<List<(double X, double Y)>> FloorLoops,
        List<List<(double X, double Y)>> OpeningLoops,
        double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>The levels a plan can be printed for: those that own at least one wall or column with a location line,
    /// by elevation and then name.</summary>
    /// <param name="document">The opened document.</param>
    public static IReadOnlyList<PlanLevel> Levels(RvtDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var counts = new Dictionary<int, int>();
        foreach (var element in document.Elements)
        {
            if (!IsWallOrColumn(element) || !HasDrawableGeometry(element)) continue;
            if (LevelOf(element) is not int level) continue;
            counts[level] = counts.TryGetValue(level, out int count) ? count + 1 : 1;
        }
        var byId = new Dictionary<int, Element>();
        foreach (var element in document.Elements) byId.TryAdd(element.Id, element);

        var levels = new List<PlanLevel>();
        foreach (var (levelId, _) in counts)
        {
            if (!byId.TryGetValue(levelId, out var level)) continue;
            double? elevation = level.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value is double value ? value : null;
            levels.Add(new PlanLevel(levelId, level.Name, elevation));
        }
        levels.Sort((left, right) =>
        {
            int order = (left.ElevationFeet ?? double.MaxValue).CompareTo(right.ElevationFeet ?? double.MaxValue);
            return order != 0 ? order : string.CompareOrdinal(left.Name, right.Name);
        });
        return levels;
    }

    /// <summary>The views a plan can be printed for: a named plan view whose name matches a printable level (or whose
    /// stored elevation equals the level's). A section or an elevation is left out until its renderer exists.</summary>
    /// <param name="document">The opened document.</param>
    public static IReadOnlyList<PlanView> Views(RvtDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<PlanLevel> levels = Levels(document);
        var views = new List<PlanView>();
        foreach (var element in document.Elements)
        {
            // Only a plan view draws as a plan: a section or an elevation named like a level must not be printed as one.
            if (element.RootClassName != "DBViewPlan" && element.NativeRecordClass != 1005) continue;
            if (element.LookupParameter(BuiltInParameter.VIEW_NAME)?.Value is not string name || name.Length == 0)
                continue;
            int? levelId = null;
            foreach (var level in levels)
            {
                if (string.Equals(level.Name, name, StringComparison.OrdinalIgnoreCase)) { levelId = level.LevelId; break; }
            }
            // The elevation fallback is for a level plan named differently from its level ("Level 1 - Plan"); a site
            // view, a section or a 3D view shares the level elevation but is not the plan of that level.
            if (levelId == null && name.Contains("level", StringComparison.OrdinalIgnoreCase)
                && element.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value is double elevation)
            {
                foreach (var level in levels)
                {
                    if (level.ElevationFeet is double levelElevation && Math.Abs(levelElevation - elevation) < 1e-6)
                    {
                        levelId = level.LevelId;
                        break;
                    }
                }
            }
            if (levelId == null) continue;
            int scale = element.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value is int value && value > 0 ? value : 100;
            // A detail view of a plan is also a DBViewPlan and carries the plan's name; it is drawn at 1:2 to 1:24 and
            // is not the plan of the level.
            if (scale < 50) continue;
            views.Add(new PlanView(element.Id, name, levelId.Value, scale));
        }
        views.Sort((left, right) =>
        {
            int order = string.CompareOrdinal(left.Name, right.Name);
            return order != 0 ? order : left.ViewId.CompareTo(right.ViewId);
        });
        return views;
    }

    /// <summary>Prints the plan of a level to a one-page PDF document.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="levelId">The level to print; null prints the level with the most walls and columns.</param>
    /// <param name="scale">The view scale denominator: 100 prints 1:100, one model metre to ten millimetres of paper.
    /// </param>
    /// <param name="options">The paper and margin; null uses the defaults.</param>
    /// <exception cref="InvalidOperationException">The document has no level with a wall or column, or the level has
    /// nothing with a location line to draw.</exception>
    public static PdfDocument Render(RvtDocument document, int? levelId = null, int scale = 100,
        PlanPrintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        options ??= new PlanPrintOptions();

        if (levelId == null)
        {
            int? best = null;
            int bestCount = 0;
            var counts = new Dictionary<int, int>();
            foreach (var element in document.Elements)
            {
                if (!IsWallOrColumn(element) || !HasDrawableGeometry(element)) continue;
                if (LevelOf(element) is not int level) continue;
                counts[level] = counts.TryGetValue(level, out int count) ? count + 1 : 1;
                if (counts[level] > bestCount) { best = level; bestCount = counts[level]; }
            }
            if (best == null) throw new InvalidOperationException("The document has no level with a wall or column.");
            levelId = best;
        }

        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _), Compress = true };
        AddPlanPage(pdf, document, levelId.Value, scale, document.GetElement(levelId.Value)?.Name, options);
        return pdf;
    }

    /// <summary>Prints one view to a one-page PDF document, at the view's own scale.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="viewId">The element id of the view; it must be one of <see cref="Views"/>.</param>
    /// <param name="options">The paper and margin; null uses the defaults.</param>
    /// <exception cref="InvalidOperationException">No printable plan view has that id.</exception>
    public static PdfDocument RenderView(RvtDocument document, int viewId, PlanPrintOptions? options = null) =>
        RenderViews(document, [viewId], options);

    /// <summary>Prints several views, one page each, to one PDF document at every view's own scale.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="viewIds">The element ids of the views; each must be one of <see cref="Views"/>.</param>
    /// <param name="options">The paper and margin; null uses the defaults.</param>
    /// <exception cref="InvalidOperationException">A view id is not a printable plan view.</exception>
    public static PdfDocument RenderViews(RvtDocument document, IReadOnlyList<int> viewIds, PlanPrintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(viewIds);
        options ??= new PlanPrintOptions();
        var views = new Dictionary<int, PlanView>();
        foreach (var view in Views(document)) views[view.ViewId] = view;
        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _), Compress = true };
        foreach (int viewId in viewIds)
        {
            if (!views.TryGetValue(viewId, out var view))
                throw new InvalidOperationException($"View {viewId} is not a printable plan view (see PlanPrinter.Views).");
            AddPlanPage(pdf, document, view.LevelId, view.Scale, view.Name, options);
        }
        if (pdf.Pages.Count == 0) throw new InvalidOperationException("No view was given to print.");
        return pdf;
    }

    /// <summary>Collects what the plan of a level draws: its walls (cut at the openings of their doors and windows),
    /// columns and grids, and the extents they span in model millimetres. Null when the level has nothing to draw.</summary>
    internal static PlanContent? CollectLevel(RvtDocument document, int levelId)
    {
        var walls = new List<(int Id, double X1, double Y1, double X2, double Y2, double Width)>();
        var columns = new List<(double X, double Y, double Width, double Height)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(double x, double y)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        foreach (var element in document.Elements)
        {
            if (LevelOf(element) != levelId) continue;

            if (element.CategoryId == (int)BuiltInCategory.OST_Walls)
            {
                if (element.Location is not { CurveType: "Line" } line) continue;
                double x1 = line.StartPoint[0] * FeetToMillimetres, y1 = line.StartPoint[1] * FeetToMillimetres;
                double x2 = line.EndPoint[0] * FeetToMillimetres, y2 = line.EndPoint[1] * FeetToMillimetres;
                double width = WidthOf(element) ?? 100;
                walls.Add((element.Id, x1, y1, x2, y2, width));
                Grow(x1, y1); Grow(x2, y2);
                continue;
            }

            if (IsColumn(element))
            {
                double[]? box = element.BoundingBox;
                if (box is not { Length: 6 } || box[3] <= box[0] || box[4] <= box[1]) continue;
                double x = box[0] * FeetToMillimetres, y = box[1] * FeetToMillimetres;
                double width = (box[3] - box[0]) * FeetToMillimetres, height = (box[4] - box[1]) * FeetToMillimetres;
                columns.Add((x, y, width, height));
                Grow(x, y); Grow(x + width, y + height);
            }
        }

        if (walls.Count == 0 && columns.Count == 0) return null;

        // Openings: the cut loop of a hosted door or window, transformed to the model, gives the stretch of its wall to
        // leave out.
        var wallById = new Dictionary<int, (int Id, double X1, double Y1, double X2, double Y2, double Width)>();
        foreach (var wall in walls) wallById[wall.Id] = wall;
        var gaps = new Dictionary<int, List<(double From, double To)>>();
        var windowGaps = new List<(int HostId, double From, double To)>();
        foreach (var insert in document.Elements)
        {
            if (insert.CategoryId is not ((int)BuiltInCategory.OST_Doors or (int)BuiltInCategory.OST_Windows)) continue;
            if (insert.OwnSingleFamilyCutout is not { } cutout || cutout.Transform is not { Length: 12 } matrix) continue;
            if (!wallById.TryGetValue(cutout.HostId, out var wall)) continue;
            double dx = wall.X2 - wall.X1, dy = wall.Y2 - wall.Y1;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6) continue;
            double ux = dx / length, uy = dy / length;
            double from = double.MaxValue, to = double.MinValue;
            foreach (var vertex in cutout.Loop.Vertices)
            {
                // The loop and its transform are in feet; the wall line is in millimetres.
                double wx = (matrix[0] * vertex[0] + matrix[1] * vertex[1] + matrix[2] * vertex[2] + matrix[9])
                    * FeetToMillimetres;
                double wy = (matrix[3] * vertex[0] + matrix[4] * vertex[1] + matrix[5] * vertex[2] + matrix[10])
                    * FeetToMillimetres;
                double t = (wx - wall.X1) * ux + (wy - wall.Y1) * uy;
                double d = -(wx - wall.X1) * uy + (wy - wall.Y1) * ux;
                if (Math.Abs(d) > wall.Width * 1.5 + 50) continue;
                from = Math.Min(from, t);
                to = Math.Max(to, t);
            }
            if (to <= from) continue;
            from = Math.Max(0, from);
            to = Math.Min(length, to);
            if (to - from < 1) continue;
            if (!gaps.TryGetValue(wall.Id, out var list)) gaps[wall.Id] = list = new List<(double From, double To)>();
            list.Add((from, to));
            if (insert.CategoryId == (int)BuiltInCategory.OST_Windows) windowGaps.Add((wall.Id, from, to));
        }

        // The floors of the level are drawn as their sketch loops, light under the walls, and every door or window cut
        // loop as its frame polygon, so an opening reads as a break in the wall with a jamb on each side.
        var floorLoops = new List<List<(double X, double Y)>>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Floors) continue;
            if (LevelOf(element) != levelId || element.SketchLoops is not { Count: > 0 } loops) continue;
            foreach (var loop in loops)
            {
                var points = new List<(double X, double Y)>(loop.Count);
                foreach (var vertex in loop)
                    points.Add((vertex[0] * FeetToMillimetres, vertex[1] * FeetToMillimetres));
                if (points.Count >= 3) floorLoops.Add(points);
            }
        }
        var openingLoops = new List<List<(double X, double Y)>>();
        foreach (var insert in document.Elements)
        {
            if (insert.CategoryId is not ((int)BuiltInCategory.OST_Doors or (int)BuiltInCategory.OST_Windows)) continue;
            if (insert.OwnSingleFamilyCutout is not { } cutout || cutout.Transform is not { Length: 12 } matrix) continue;
            var points = new List<(double X, double Y)>(cutout.Loop.Vertices.Count);
            foreach (var vertex in cutout.Loop.Vertices)
            {
                double wx = (matrix[0] * vertex[0] + matrix[1] * vertex[1] + matrix[2] * vertex[2] + matrix[9])
                    * FeetToMillimetres;
                double wy = (matrix[3] * vertex[0] + matrix[4] * vertex[1] + matrix[5] * vertex[2] + matrix[10])
                    * FeetToMillimetres;
                points.Add((wx, wy));
            }
            if (points.Count >= 3) openingLoops.Add(points);
        }

        var grids = new List<(double X1, double Y1, double X2, double Y2, string Name)>();
        double gridMargin = Math.Max((maxX - minX + maxY - minY) * 0.06, 1000);
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Grids) continue;
            if (element.Location is not { CurveType: "Line" } line) continue;
            double gx1 = line.StartPoint[0] * FeetToMillimetres, gy1 = line.StartPoint[1] * FeetToMillimetres;
            double gx2 = line.EndPoint[0] * FeetToMillimetres, gy2 = line.EndPoint[1] * FeetToMillimetres;
            if (ClipToBox(ref gx1, ref gy1, ref gx2, ref gy2,
                    minX - gridMargin, minY - gridMargin, maxX + gridMargin, maxY + gridMargin))
                grids.Add((gx1, gy1, gx2, gy2, element.Name ?? ""));
        }

        return new PlanContent(walls, columns, grids, gaps, windowGaps, floorLoops, openingLoops, minX, minY, maxX, maxY);
    }

    /// <summary>Draws collected plan content on a page: the model point (content.MinX, content.MinY) goes to
    /// (originX, originY), one model unit is 1/scale.</summary>
    internal static void DrawContent(PdfPage page, PlanContent content, double scale, double originX, double originY)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;

        foreach (var floor in content.FloorLoops)
            page.DrawPolyline(floor.Select(point => (ToX(point.X), ToY(point.Y))).ToList(), 0, true, 0.9);

        foreach (var grid in content.Grids)
        {
            page.DrawLine(ToX(grid.X1), ToY(grid.Y1), ToX(grid.X2), ToY(grid.Y2), 0.18, 0.45);
            if (page.Owner.Font == null || grid.Name.Length == 0) continue;
            Bubble(page, ToX(grid.X1), ToY(grid.Y1), grid.Name);
            Bubble(page, ToX(grid.X2), ToY(grid.Y2), grid.Name);
        }

        var wallById = new Dictionary<int, (int Id, double X1, double Y1, double X2, double Y2, double Width)>();
        foreach (var wall in content.Walls) wallById[wall.Id] = wall;

        foreach (var wall in content.Walls)
        {
            double dx = wall.X2 - wall.X1, dy = wall.Y2 - wall.Y1;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) continue;
            double ux = dx / length, uy = dy / length;
            double nx = -uy * wall.Width / 2, ny = ux * wall.Width / 2;
            foreach (var (from, to) in SplitWall(length, content.Gaps.TryGetValue(wall.Id, out var list) ? list : null))
            {
                double ax = wall.X1 + ux * from, ay = wall.Y1 + uy * from;
                double bx = wall.X1 + ux * to, by = wall.Y1 + uy * to;
                page.DrawLine(ToX(ax + nx), ToY(ay + ny), ToX(bx + nx), ToY(by + ny), 0.2);
                page.DrawLine(ToX(ax - nx), ToY(ay - ny), ToX(bx - nx), ToY(by - ny), 0.2);
                page.DrawLine(ToX(ax + nx), ToY(ay + ny), ToX(ax - nx), ToY(ay - ny), 0.2);
                page.DrawLine(ToX(bx + nx), ToY(by + ny), ToX(bx - nx), ToY(by - ny), 0.2);
            }
        }

        // A window is a light line across its opening on the wall axis, the glass of the plan.
        foreach (var (hostId, from, to) in content.WindowGaps)
        {
            if (!wallById.TryGetValue(hostId, out var wall)) continue;
            double dx = wall.X2 - wall.X1, dy = wall.Y2 - wall.Y1;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6) continue;
            double ux = dx / length, uy = dy / length;
            page.DrawLine(ToX(wall.X1 + ux * from), ToY(wall.Y1 + uy * from),
                ToX(wall.X1 + ux * to), ToY(wall.Y1 + uy * to), 0.15, 0.35);
        }

        foreach (var opening in content.OpeningLoops)
            page.DrawPolyline(opening.Select(point => (ToX(point.X), ToY(point.Y))).ToList(), 0.18, true);

        foreach (var column in content.Columns)
            page.DrawRectangle(ToX(column.X), ToY(column.Y), column.Width / scale, column.Height / scale, 0.2, 1);
    }

    /// <summary>Draws the plan of a level as one page of the document.</summary>
    internal static void AddPlanPage(PdfDocument pdf, RvtDocument document, int levelId, int scale, string? title,
        PlanPrintOptions options)
    {
        PlanContent content = CollectLevel(document, levelId)
            ?? throw new InvalidOperationException("The level has no wall or column with a location line to draw.");
        double contentWidth = (content.MaxX - content.MinX) / scale;
        double contentHeight = (content.MaxY - content.MinY) / scale;
        PdfPageSize paper = options.PaperSize ?? SmallestPaper(contentWidth + 2 * options.MarginMillimetres,
            contentHeight + 2 * options.MarginMillimetres);
        double usableWidth = paper.WidthMillimetres - 2 * options.MarginMillimetres;
        double usableHeight = paper.HeightMillimetres - 2 * options.MarginMillimetres;
        double effectiveScale = scale;
        if (contentWidth > usableWidth || contentHeight > usableHeight)
        {
            double factor = Math.Min(usableWidth / Math.Max(contentWidth, 1e-9),
                                     usableHeight / Math.Max(contentHeight, 1e-9));
            effectiveScale = scale / factor;
        }
        double offsetX = (paper.WidthMillimetres - (content.MaxX - content.MinX) / effectiveScale) / 2;
        double offsetY = (paper.HeightMillimetres - (content.MaxY - content.MinY) / effectiveScale) / 2;

        PdfPage page = pdf.AddPage(paper);
        if (options.DrawFrame)
        {
            double m = options.MarginMillimetres;
            page.DrawRectangle(m, m, paper.WidthMillimetres - 2 * m, paper.HeightMillimetres - 2 * m, 0.25);
        }

        DrawContent(page, content, effectiveScale, offsetX, offsetY);

        // A title inside the frame: the level or view name and the scale. Drawn only when a font with the name's
        // characters was found; the standard font cannot write Cyrillic.
        if (pdf.Font != null && !string.IsNullOrEmpty(title))
        {
            double margin = options.MarginMillimetres;
            page.DrawText(title, margin + 3, paper.HeightMillimetres - margin - 8, 4.5);
            page.DrawText($"1:{scale}", margin + 3, paper.HeightMillimetres - margin - 15, 3.5,
                PdfTextAlignment.Left, 0.35);
        }
    }

    /// <summary>A grid bubble: a white circle with the grid name centred in it, as a plan prints the end of a grid
    /// line.</summary>
    private static void Bubble(PdfPage page, double x, double y, string name)
    {
        const double radius = 3.5;
        var points = new List<(double X, double Y)>(24);
        for (int i = 0; i < 24; i++)
        {
            double angle = i * Math.PI * 2 / 24;
            points.Add((x + radius * Math.Cos(angle), y + radius * Math.Sin(angle)));
        }
        page.DrawPolyline(points, 0.18, true, 1);
        page.DrawText(name, x, y - 1.4, 3, PdfTextAlignment.Center);
    }

    /// <summary>The stretches of a wall that are not an opening: [0, length] minus the gaps, in order.</summary>
    private static List<(double From, double To)> SplitWall(double length, List<(double From, double To)>? gaps)
    {
        var result = new List<(double From, double To)>();
        double cursor = 0;
        if (gaps != null)
        {
            gaps.Sort((left, right) => left.From.CompareTo(right.From));
            foreach (var gap in gaps)
            {
                if (gap.From > cursor) result.Add((cursor, Math.Min(gap.From, length)));
                cursor = Math.Max(cursor, gap.To);
                if (cursor >= length) break;
            }
        }
        if (cursor < length) result.Add((cursor, length));
        return result;
    }

    private static bool ClipToBox(ref double x1, ref double y1, ref double x2, ref double y2,
        double left, double bottom, double right, double top)
    {
        // Liang-Barsky parametrisation of the segment against the box.
        double dx = x2 - x1, dy = y2 - y1;
        double t0 = 0, t1 = 1;
        foreach (var (p, q) in new[] { (-dx, x1 - left), (dx, right - x1), (-dy, y1 - bottom), (dy, top - y1) })
        {
            if (Math.Abs(p) < 1e-12)
            {
                if (q < 0) return false;
                continue;
            }
            double t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
        }
        double nx1 = x1 + t0 * dx, ny1 = y1 + t0 * dy, nx2 = x1 + t1 * dx, ny2 = y1 + t1 * dy;
        x1 = nx1; y1 = ny1; x2 = nx2; y2 = ny2;
        return true;
    }

    private static PdfPageSize SmallestPaper(double width, double height)
    {
        double w = Math.Max(width, height), h = Math.Min(width, height);
        if (w <= PdfPageSize.A4.Landscape.WidthMillimetres && h <= PdfPageSize.A4.Landscape.HeightMillimetres)
            return PdfPageSize.A4.Landscape;
        if (w <= PdfPageSize.A3.Landscape.WidthMillimetres && h <= PdfPageSize.A3.Landscape.HeightMillimetres)
            return PdfPageSize.A3.Landscape;
        if (w <= PdfPageSize.A2.Landscape.WidthMillimetres && h <= PdfPageSize.A2.Landscape.HeightMillimetres)
            return PdfPageSize.A2.Landscape;
        if (w <= PdfPageSize.A1.Landscape.WidthMillimetres && h <= PdfPageSize.A1.Landscape.HeightMillimetres)
            return PdfPageSize.A1.Landscape;
        return PdfPageSize.A0.Landscape;
    }

    private static bool IsWallOrColumn(Element element) =>
        element.CategoryId is (int)BuiltInCategory.OST_Walls or (int)BuiltInCategory.OST_Columns
            or (int)BuiltInCategory.OST_StructuralColumns;

    /// <summary>True when the element has what the plan draws: a straight location line for a wall, a footprint box
    /// for a column.</summary>
    private static bool HasDrawableGeometry(Element element) =>
        element.CategoryId == (int)BuiltInCategory.OST_Walls
            ? element.Location is { CurveType: "Line" }
            : element.BoundingBox is { Length: 6 } box && box[3] > box[0] && box[4] > box[1];

    private static bool IsColumn(Element element) =>
        element.CategoryId is (int)BuiltInCategory.OST_Columns or (int)BuiltInCategory.OST_StructuralColumns;

    private static int? LevelOf(Element element) =>
        element.LookupParameter(BuiltInParameter.LEVEL_PARAM)?.Value is int level && level > 0 ? level : null;

    private static double? WidthOf(Element element) =>
        element.LookupParameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM)?.Value is double width && width > 0
            ? width * FeetToMillimetres : null;
}
