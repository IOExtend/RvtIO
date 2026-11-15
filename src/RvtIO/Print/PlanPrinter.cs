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

/// <summary>
/// Prints a plan of one level to a PDF page from the data the reader decodes: walls as their location line with the
/// stored thickness, columns as their stored footprint, grids as their location lines. This is the first view renderer
/// of the sheet printing work: it draws the model geometry, not Revit's generated linework, so openings, annotations,
/// room fills, dimensions, tags and the title block are not on the page yet (see the notes in docs/printing.md).
/// </summary>
public static class PlanPrinter
{
    private const double FeetToMillimetres = 304.8;

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

    /// <summary>Prints the plan of a level to a one-page PDF document.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="levelId">The level to print; null prints the level with the most walls and columns (the first of
    /// <see cref="Levels"/> when several levels tie).</param>
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
        string? levelName = document.GetElement(levelId.Value)?.Name;

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
                double width = WidthOf(element) ?? options.FallbackWallWidthMillimetres;
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

        if (walls.Count == 0 && columns.Count == 0)
            throw new InvalidOperationException("The level has no wall or column with a location line to draw.");

        // Openings: the cut loop of a hosted door or window, transformed to the model, gives the stretch of its wall to
        // leave out. The plan then draws the wall in the pieces between its openings, with a jamb at every cut.
        var wallById = new Dictionary<int, (int Id, double X1, double Y1, double X2, double Y2, double Width)>();
        foreach (var wall in walls) wallById[wall.Id] = wall;
        var gaps = new Dictionary<int, List<(double From, double To)>>();
        var windowGaps = new List<(int HostId, double From, double To)>();
        int debugOpenings = 0, debugCutouts = 0, debugHosted = 0;
        foreach (var insert in document.Elements)
        {
            if (insert.CategoryId is not ((int)BuiltInCategory.OST_Doors or (int)BuiltInCategory.OST_Windows)) continue;
            debugOpenings++;
            if (insert.OwnSingleFamilyCutout is not { } cutout || cutout.Transform is not { Length: 12 } matrix) continue;
            debugCutouts++;
            if (!wallById.TryGetValue(cutout.HostId, out var wall)) continue;
            debugHosted++;
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

        // Grids are clipped to the extent of the level: a grid line can run far beyond the building, and an unclipped
        // one would put the whole plan into the corner of an oversized page.
        var grids = new List<(double X1, double Y1, double X2, double Y2)>();
        double gridMargin = Math.Max((maxX - minX + maxY - minY) * 0.06, 1000);
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Grids) continue;
            if (element.Location is not { CurveType: "Line" } line) continue;
            double gx1 = line.StartPoint[0] * FeetToMillimetres, gy1 = line.StartPoint[1] * FeetToMillimetres;
            double gx2 = line.EndPoint[0] * FeetToMillimetres, gy2 = line.EndPoint[1] * FeetToMillimetres;
            if (ClipToBox(ref gx1, ref gy1, ref gx2, ref gy2,
                    minX - gridMargin, minY - gridMargin, maxX + gridMargin, maxY + gridMargin))
                grids.Add((gx1, gy1, gx2, gy2));
        }

        if (Environment.GetEnvironmentVariable("RVTIO_PRINT_DEBUG") is { Length: > 0 } debugPath)
            File.WriteAllText(debugPath, $"walls={walls.Count} doorsWindows={debugOpenings} cutouts={debugCutouts} "
                + $"hosted={debugHosted} gaps={gaps.Count} windows={windowGaps.Count}");

        double contentWidth = (maxX - minX) / scale;
        double contentHeight = (maxY - minY) / scale;
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
        double offsetX = (paper.WidthMillimetres - (maxX - minX) / effectiveScale) / 2;
        double offsetY = (paper.HeightMillimetres - (maxY - minY) / effectiveScale) / 2;

        PdfTrueTypeFont? font = PdfFonts.TryFind(out _);
        var pdf = new PdfDocument { Font = font };
        PdfPage page = pdf.AddPage(paper);
        if (options.DrawFrame)
        {
            double m = options.MarginMillimetres;
            page.DrawRectangle(m, m, paper.WidthMillimetres - 2 * m, paper.HeightMillimetres - 2 * m, 0.25);
        }

        // Grids first and light, then walls, then the filled column footprints on top of both.
        foreach (var grid in grids)
            page.DrawLine(ToX(grid.X1), ToY(grid.Y1), ToX(grid.X2), ToY(grid.Y2), 0.18, 0.45);

        foreach (var wall in walls)
        {
            double dx = wall.X2 - wall.X1, dy = wall.Y2 - wall.Y1;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) continue;
            double ux = dx / length, uy = dy / length;
            double nx = -uy * wall.Width / 2, ny = ux * wall.Width / 2;
            foreach (var (from, to) in SplitWall(length, gaps.TryGetValue(wall.Id, out var list) ? list : null))
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
        foreach (var (hostId, from, to) in windowGaps)
        {
            if (!wallById.TryGetValue(hostId, out var wall)) continue;
            double dx = wall.X2 - wall.X1, dy = wall.Y2 - wall.Y1;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6) continue;
            double ux = dx / length, uy = dy / length;
            page.DrawLine(ToX(wall.X1 + ux * from), ToY(wall.Y1 + uy * from),
                ToX(wall.X1 + ux * to), ToY(wall.Y1 + uy * to), 0.15, 0.35);
        }

        foreach (var column in columns)
            page.DrawRectangle(ToX(column.X), ToY(column.Y), column.Width / effectiveScale, column.Height / effectiveScale, 0.2, 1);

        // A title inside the frame: the level and the scale, in the level's own name. Drawn only when a font with the
        // name's characters was found; the standard font cannot write Cyrillic.
        if (font != null && levelName != null)
        {
            double margin = options.MarginMillimetres;
            page.DrawText(levelName, margin + 3, paper.HeightMillimetres - margin - 8, 4.5);
            page.DrawText($"1:{scale}", margin + 3, paper.HeightMillimetres - margin - 15, 3.5, PdfTextAlignment.Left, 0.35);
        }

        return pdf;

        double ToX(double x) => (x - minX) / effectiveScale + offsetX;
        double ToY(double y) => (y - minY) / effectiveScale + offsetY;
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
