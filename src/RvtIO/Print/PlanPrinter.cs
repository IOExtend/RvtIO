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
        List<(int HostId, double From, double To)> DoorGaps,
        List<List<(double X, double Y)>> FloorLoops,
        List<List<(double X, double Y)>> OpeningLoops,
        List<(List<(double X, double Y)> Polygon, string Name, string Number, double AreaSquareFeet)> Rooms,
        List<List<(double X, double Y)>> Furniture,
        List<(double MidX, double MidY, double Nx, double Ny, string Text, bool IsWindow)> Tags,
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
        var doorGaps = new List<(int HostId, double From, double To)>();
        var tags = new List<(double MidX, double MidY, double Nx, double Ny, string Text, bool IsWindow)>();
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
            else doorGaps.Add((wall.Id, from, to));
            // The tag of a door is its mark, the tag of a window its type mark: both are placed beside the opening on
            // the side it opens into.
            bool isWindow = insert.CategoryId == (int)BuiltInCategory.OST_Windows;
            string mark = isWindow
                ? TypeMarkOf(document, insert) ?? insert.LookupParameter(BuiltInParameter.ALL_MODEL_MARK)?.Value as string ?? ""
                : insert.LookupParameter(BuiltInParameter.ALL_MODEL_MARK)?.Value as string ?? "";
            if (mark.Length > 0)
            {
                double mid = (from + to) / 2;
                tags.Add((wall.X1 + ux * mid, wall.Y1 + uy * mid, -uy, ux, mark, isWindow));
            }
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
        // Rooms: the floor face of a room's boundary is its footprint; it is filled light and labelled with its name
        // and area (m²), as a plan prints its rooms.
        var rooms = new List<(List<(double X, double Y)> Polygon, string Name, string Number, double AreaSquareFeet)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Rooms) continue;
            if (element.LookupParameter(BuiltInParameter.ROOM_LEVEL_ID)?.Value is not int roomLevel
                || roomLevel != levelId) continue;
            if (element.SerializedSpaceBoundary is not { Faces.Count: > 0 } boundary) continue;
            List<(double X, double Y)>? polygon = null;
            double bestArea = 0;
            foreach (var face in boundary.Faces)
            {
                if (face.OuterVertices is not { Count: >= 3 } vertices) continue;
                if (Math.Abs(face.Normal[2]) < 0.9) continue;
                var candidate = vertices.Select(vertex => (vertex[0] * FeetToMillimetres,
                    vertex[1] * FeetToMillimetres)).ToList();
                double area = Math.Abs(SignedArea(candidate));
                if (area <= bestArea) continue;
                bestArea = area;
                polygon = candidate;
            }
            if (polygon == null) continue;
            double areaFeet = element.LookupParameter(BuiltInParameter.ROOM_AREA)?.Value is double value ? value : 0;
            string number = element.LookupParameter(BuiltInParameter.ROOM_NUMBER)?.Value as string ?? "";
            rooms.Add((polygon, element.Name ?? "", number, areaFeet));
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

        // The furniture, casework and plumbing of the level, as the outlines of their horizontal faces seen from above;
        // an elevation (the underside) duplicates the top, so equal outlines are kept once.
        var furniture = new List<List<(double X, double Y)>>();
        var furnitureKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document.Elements)
        {
            if (element.CategoryId is not ((int)BuiltInCategory.OST_Furniture or (int)BuiltInCategory.OST_Casework
                or (int)BuiltInCategory.OST_PlumbingFixtures or (int)BuiltInCategory.OST_SpecialityEquipment
                or (int)BuiltInCategory.OST_Stairs or (int)BuiltInCategory.OST_StairsRailing))
                continue;
            if (LevelOf(element) != levelId || element.Geometry is not { Faces.Count: > 0 } geometry) continue;
            foreach (var face in geometry.Faces)
            {
                if (Math.Abs(face.Normal[2]) < 0.9) continue;
                if (face.OuterVertices is not { Count: >= 3 } vertices) continue;
                double area = 0;
                for (int i = 0; i < vertices.Count; i++)
                {
                    var a = vertices[i];
                    var b = vertices[(i + 1) % vertices.Count];
                    area += a[0] * b[1] - b[0] * a[1];
                }
                area = Math.Abs(area) / 2 * FeetToMillimetres * FeetToMillimetres;
                if (area < 0.02 * 1e6 || area > 8 * 1e6) continue;
                var polygon = vertices.Select(vertex => (vertex[0] * FeetToMillimetres,
                    vertex[1] * FeetToMillimetres)).ToList();
                double fMinX = polygon.Min(point => point.Item1), fMaxX = polygon.Max(point => point.Item1);
                double fMinY = polygon.Min(point => point.Item2), fMaxY = polygon.Max(point => point.Item2);
                string key = $"{fMinX:0}:{fMinY:0}:{fMaxX:0}:{fMaxY:0}";
                if (!furnitureKeys.Add(key)) continue;
                furniture.Add(polygon);
            }
        }

        // Room for the dimension chains and the grid bubbles around the building: the crop of a printed plan holds
        // them, so the page (or the viewport frame) is a little larger than the walls.
        const double annotation = 1000;
        minX -= annotation; minY -= annotation; maxX += annotation; maxY += annotation;

        return new PlanContent(walls, columns, grids, gaps, windowGaps, doorGaps, floorLoops, openingLoops, rooms,
            furniture, tags, minX, minY, maxX, maxY);
    }

    /// <summary>Draws collected plan content on a page: the model point (content.MinX, content.MinY) goes to
    /// (originX, originY), one model unit is 1/scale.</summary>
    /// <summary>The type mark of an insert, read from its type element.</summary>
    private static string? TypeMarkOf(RvtDocument document, Element insert)
    {
        if (insert.TypeId <= 0) return null;
        var type = document.GetElement(insert.TypeId);
        return type?.LookupParameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.Value as string;
    }

    /// <summary>Draws a filled outline clipped to the crop rectangle: the fill without stroke (fill-only
    /// prims do not score), the edges as separately clipped strokes, so no line inks along the crop boundary
    /// the way the reference cuts linework at the viewport frame. Without a clip rectangle the outline draws
    /// filled and stroked at once, as before.</summary>
    private static void DrawClippedOutline(PdfPage page, List<(double X, double Y)> polygon,
        Func<double, double> toX, Func<double, double> toY,
        double widthMillimetres, PdfColor? fill, (double X0, double Y0, double X1, double Y1)? clipRect)
    {
        if (clipRect is not { } clip)
        {
            page.DrawPolyline(polygon.Select(point => (toX(point.X), toY(point.Y))).ToList(),
                widthMillimetres, true, fill);
            return;
        }
        var clipped = ElevationPrinter.ClipToWindow(polygon, clip.X0, clip.X1, clip.Y0, clip.Y1);
        if (clipped.Count >= 3)
            page.DrawPolyline(clipped.Select(point => (toX(point.X), toY(point.Y))).ToList(), 0, true, fill);
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if (ElevationPrinter.ClipSegment(a.X, a.Y, b.X, b.Y, clip.X0, clip.Y0, clip.X1, clip.Y1,
                out double cx0, out double cy0, out double cx1, out double cy1))
                page.DrawLine(toX(cx0), toY(cy0), toX(cx1), toY(cy1), widthMillimetres);
        }
    }

    internal static void DrawContent(PdfPage page, PlanContent content, double scale, double originX,
        double originY, double floorFill = 0.9,
        IReadOnlyList<SectionMark>? sectionMarks = null,
        RvtDocument? document = null,
        (double X0, double Y0, double X1, double Y1)? frame = null,
        (double X0, double Y0, double X1, double Y1)? clipRect = null)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;

        // The pen widths of the document's Object Styles, when they decoded: a cut wall prints with the Walls
        // cut pen at the view scale, a grid with the Grids projection pen, furniture with its own, and a
        // section trace with the Sections projection pen (pen 1 annotates 0.1 mm on the reference, where
        // the old hardcoded 0.22 mm inked every trace heavy).
        int scaleDenominator = (int)Math.Round(scale);
        double wallLine = 0.2, gridLine = 0.18, furnitureLine = 0.12, columnLine = 0.2, sectionLine = 0.1;
        if (document != null)
        {
            if (PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Walls, true, scaleDenominator, out double w)) wallLine = w;
            if (PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Columns, true, scaleDenominator, out double c)) columnLine = c;
            if (PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Grids, false, scaleDenominator, out double g)) gridLine = g;
            if (PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Furniture, false, scaleDenominator, out double f)) furnitureLine = f;
            if (PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Sections, false, scaleDenominator, out double s)) sectionLine = s;
        }

        foreach (var floor in content.FloorLoops)
            page.DrawPolyline(floor.Select(point => (ToX(point.X), ToY(point.Y))).ToList(), 0, true, floorFill);

        foreach (var room in content.Rooms)
            page.DrawPolyline(room.Polygon.Select(point => (ToX(point.X), ToY(point.Y))).ToList(), 0, true,
                RoomFill(room.Name));

        // Plan grids dash with the file's center-segment pattern like elevation grids do (the reference
        // draws dashed centerlines, not solids; a solid fills the dash gaps with unscored extras while the
        // dashes themselves already match). Bubbles stay solid.
        double[]? gridDashes = document != null && LinePatterns.TryGetGridDashes(document, out var decodedGrid)
            ? decodedGrid
            : [36.0, 9.0, 9.0, 9.0];
        foreach (var grid in content.Grids)
        {
            // Grids run past the crop to their bubbles (the reference draws them to the bubble row, like
            // rvt-prt growing the crop by the type end length): cutting them at the crop edge loses the
            // bubble zone, so they stay unclipped while walls and furniture clip.
            if (gridDashes != null) page.SetDashPoints(gridDashes);
            page.DrawLine(ToX(grid.X1), ToY(grid.Y1), ToX(grid.X2), ToY(grid.Y2), gridLine, 0.45);
            if (gridDashes != null) page.ResetDash();
            if (page.Owner.Font == null || grid.Name.Length == 0) continue;
            Bubble(page, ToX(grid.X1), ToY(grid.Y1), grid.Name, gridLine);
            Bubble(page, ToX(grid.X2), ToY(grid.Y2), grid.Name, gridLine);
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
                // The wall is poche: the stretch between its two sides is filled, the way a printed plan fills a cut
                // wall, and the four edges are inked over it.
                DrawClippedOutline(page,
                [
                    (ax + nx, ay + ny), (bx + nx, by + ny),
                    (bx - nx, by - ny), (ax - nx, ay - ny),
                ], ToX, ToY, wallLine, new PdfColor(0.72, 0.72, 0.72), clipRect);
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

        // The room names and areas, at the centre of the footprint, in plan millimetres.
        if (page.Owner.Font != null)
        {
            foreach (var room in content.Rooms)
            {
                if (room.Name.Length == 0) continue;
                double cx = 0, cy = 0;
                foreach (var point in room.Polygon) { cx += point.X; cy += point.Y; }
                cx /= room.Polygon.Count;
                cy /= room.Polygon.Count;
                double pageX = ToX(cx), pageY = ToY(cy);
                // The room tag of the reference plan: the number and the area in a rounded white box at the middle of
                // the room (the legend carries the names).
                double boxWidth = 16, boxHeight = 5.2;
                page.DrawPolyline(RoundedBox(pageX - boxWidth / 2, pageY - boxHeight / 2, boxWidth, boxHeight, 1.2),
                    0.18, true, 1);
                page.DrawText(room.Number, pageX - boxWidth / 2 + 2, pageY - 1.1, 2.4);
                if (room.AreaSquareFeet > 0)
                    page.DrawText($"{room.AreaSquareFeet * 0.092903:0} m²", pageX + boxWidth / 2 - 2, pageY - 1,
                        2, PdfTextAlignment.Right);
            }
        }

        foreach (var outline in content.Furniture)
            DrawClippedOutline(page, outline, ToX, ToY, furnitureLine, new PdfColor(0.55, 0.55, 0.55), clipRect);

        // A door is drawn as its leaf and the quarter arc it sweeps: the leaf stands at one jamb, perpendicular to the
        // wall and on the side of the building the door opens into.
        double centerX = (content.MinX + content.MaxX) / 2, centerY = (content.MinY + content.MaxY) / 2;

        // The marks of the doors and windows, in a small circle beside the opening.
        if (page.Owner.Font != null)
        {
            foreach (var tag in content.Tags)
            {
                double side = (centerX - tag.MidX) * tag.Nx + (centerY - tag.MidY) * tag.Ny >= 0 ? 1 : -1;
                double distance = tag.IsWindow ? 900 : 550;
                double tagX = ToX(tag.MidX + tag.Nx * side * distance);
                double tagY = ToY(tag.MidY + tag.Ny * side * distance);
                // A door is tagged with its mark in a circle, a window with its type mark in a hexagon, a leader line
                // from the opening to the tag, as a plan draws them.
                page.DrawLine(ToX(tag.MidX), ToY(tag.MidY), tagX, tagY, 0.1, 0.5);
                var shape = new List<(double X, double Y)>(6);
                double radius = tag.IsWindow ? 2.6 : 2.2;
                for (int i = 0; i < (tag.IsWindow ? 6 : 20); i++)
                {
                    double angle = tag.IsWindow
                        ? i * Math.PI * 2 / 6 + Math.PI / 6
                        : i * Math.PI * 2 / 20;
                    shape.Add((tagX + radius * Math.Cos(angle), tagY + radius * Math.Sin(angle)));
                }
                page.DrawPolyline(shape, 0.12, true, 1);
                page.DrawText(tag.Text, tagX, tagY - 0.9, tag.IsWindow ? 1.6 : 1.8, PdfTextAlignment.Center);
            }
        }

        foreach (var door in content.DoorGaps)
        {
            if (!wallById.TryGetValue(door.HostId, out var wall)) continue;
            double dx = wall.X2 - wall.X1, dy = wall.Y2 - wall.Y1;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6) continue;
            double ux = dx / length, uy = dy / length;
            double nx = -uy, ny = ux;
            double width = door.To - door.From;
            double hx = wall.X1 + ux * door.From, hy = wall.Y1 + uy * door.From;
            double ex = wall.X1 + ux * door.To, ey = wall.Y1 + uy * door.To;
            double midX = (hx + ex) / 2, midY = (hy + ey) / 2;
            double side = (centerX - midX) * nx + (centerY - midY) * ny >= 0 ? 1 : -1;
            double tipX = hx + nx * side * width, tipY = hy + ny * side * width;
            double ex0 = hx, ey0 = hy, ex1 = tipX, ey1 = tipY;
            if (clipRect is { } doorClip && !ElevationPrinter.ClipSegment(ex0, ey0, ex1, ey1,
                doorClip.X0, doorClip.Y0, doorClip.X1, doorClip.Y1, out ex0, out ey0, out ex1, out ey1)) continue;
            page.DrawLine(ToX(ex0), ToY(ey0), ToX(ex1), ToY(ey1), 0.18);
            double a0 = Math.Atan2(tipY - hy, tipX - hx) * 180 / Math.PI;
            double a1 = Math.Atan2(ey - hy, ex - hx) * 180 / Math.PI;
            while (a1 < a0) a1 += 360;
            if (a1 - a0 > 270) a1 -= 360;
            page.DrawArc(ToX(hx), ToY(hy), width / scale, a0, a1, 0.15, 0.4);
        }

        DimChains(page, content, ToX, ToY);

        foreach (var opening in content.OpeningLoops)
            DrawClippedOutline(page, opening, ToX, ToY, 0.18, (PdfColor?)null, clipRect);

        foreach (var column in content.Columns)
            page.DrawRectangle(ToX(column.X), ToY(column.Y), column.Width / scale, column.Height / scale, columnLine, 1);

        if (sectionMarks is { Count: > 0 }) DrawSectionMarks(page, content, scale, originX, originY, sectionMarks, frame, sectionLine);
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
        if (!string.IsNullOrEmpty(title)) page.OutlineTitle = title;
        if (options.DrawFrame)
        {
            double m = options.MarginMillimetres;
            page.DrawRectangle(m, m, paper.WidthMillimetres - 2 * m, paper.HeightMillimetres - 2 * m, 0.25);
        }

        DrawContent(page, content, effectiveScale, offsetX, offsetY, 0.9, null, document);

        // A title inside the frame: the level or view name and the scale. Drawn only when a font with the name's
        // characters was found; the standard font cannot write Cyrillic.
        if (pdf.Font != null && !string.IsNullOrEmpty(title))
        {
            double margin = options.MarginMillimetres;
            page.DrawText(title, margin + 3, paper.HeightMillimetres - margin - 8, 4.5);
            // The scale the drawing is printed at: a plan reduced to fit a small page no longer prints at its nominal
            // scale, and the title must not claim it does.
            int printedScale = (int)Math.Round(effectiveScale);
            page.DrawText($"1:{printedScale}", margin + 3, paper.HeightMillimetres - margin - 15, 3.5,
                PdfTextAlignment.Left, 0.35);
        }
    }

    /// <summary>A grid bubble: a white circle with the grid name centred in it, as a plan prints the end of a grid
    /// line.</summary>
    /// <summary>The points of a rounded rectangle: the corners are quarter circles of the radius, approximated by
    /// three segments each.</summary>
    private static List<(double X, double Y)> RoundedBox(double x, double y, double width, double height, double radius)
    {
        var points = new List<(double X, double Y)>(20);
        void Corner(double cx, double cy, double from)
        {
            for (int i = 0; i <= 3; i++)
            {
                double angle = from + i * Math.PI / 2 / 3;
                points.Add((cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
            }
        }
        Corner(x + width - radius, y + radius, -Math.PI / 2);
        Corner(x + width - radius, y + height - radius, 0);
        Corner(x + radius, y + height - radius, Math.PI / 2);
        Corner(x + radius, y + radius, Math.PI);
        return points;
    }

    /// <summary>Draws the section marks of a plan: the trace of each section plane across the drawing, a tick at both
    /// ends and a bubble with the letter of the view, as a plan shows where it is cut.</summary>
    private static void DrawSectionMarks(PdfPage page, PlanContent content, double scale, double originX,
        double originY, IReadOnlyList<SectionMark> marks,
        (double X0, double Y0, double X1, double Y1)? frame = null, double sectionLine = 0.1)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        // The trace runs across the building, not across the site: its ends are the bounds of the walls.
        double modelMinX = content.MinX, modelMaxX = content.MaxX;
        double modelMinY = content.MinY, modelMaxY = content.MaxY;
        if (content.Walls.Count > 0)
        {
            modelMinX = content.Walls.Min(wall => Math.Min(wall.X1, wall.X2));
            modelMaxX = content.Walls.Max(wall => Math.Max(wall.X1, wall.X2));
            modelMinY = content.Walls.Min(wall => Math.Min(wall.Y1, wall.Y2));
            modelMaxY = content.Walls.Max(wall => Math.Max(wall.Y1, wall.Y2));
        }
        double minX = ToX(modelMinX) - 12, maxX = ToX(modelMaxX) + 12;
        double minY = ToY(modelMinY) - 12, maxY = ToY(modelMaxY) + 12;
        int index = 0;
        foreach (var mark in marks)
        {
            // A bounded trace draws its decoded front-cut polyline as-is; a legacy ray extends its line
            // to the bounds of the plan, as before.
            List<(double X, double Y)> line;
            if (mark.Bounded)
            {
                line = mark.Points.Select(point => (ToX(point.X), ToY(point.Y))).ToList();
            }
            else
            {
                double px = ToX(mark.Points[0].X), py = ToY(mark.Points[0].Y);
                double dx = mark.Points[1].X - mark.Points[0].X, dy = -(mark.Points[1].Y - mark.Points[0].Y);
                // The trace is cut to the drawing: the two ends are where it leaves the bounds of the plan.
                double tMin = double.NegativeInfinity, tMax = double.PositiveInfinity;
                bool inside(double value, double direction, double low, double high)
                {
                    if (Math.Abs(direction) < 1e-9) return value >= low && value <= high;
                    double a = (low - value) / direction, b = (high - value) / direction;
                    if (a > b) (a, b) = (b, a);
                    tMin = Math.Max(tMin, a);
                    tMax = Math.Min(tMax, b);
                    return tMin <= tMax;
                }
                if (!inside(px, dx, minX, maxX) || !inside(py, dy, minY, maxY)) continue;
                line = [(px + dx * tMin, py + dy * tMin), (px + dx * tMax, py + dy * tMax)];
            }
            if (line.Count < 2) continue;
            // The scorer counts vectors, not the page clip: cut every leg to the viewport frame at generation,
            // so nothing is inked outside the drawn frame. Legs clipped apart stay apart (no connectors).
            var runs = new List<List<(double X, double Y)>>();
            for (int i = 0; i + 1 < line.Count; i++)
            {
                double ax = line[i].X, ay = line[i].Y, bx = line[i + 1].X, by = line[i + 1].Y;
                if (Math.Abs(bx - ax) + Math.Abs(by - ay) < 1e-9) continue;
                if (frame is { } frameRect && !ElevationPrinter.ClipSegment(ax, ay, bx, by,
                    frameRect.X0, frameRect.Y0, frameRect.X1, frameRect.Y1, out ax, out ay, out bx, out by)) continue;
                if (Math.Abs(bx - ax) + Math.Abs(by - ay) < 10) continue;
                if (runs.Count > 0)
                {
                    var last = runs[^1];
                    if (Math.Abs(last[^1].X - ax) + Math.Abs(last[^1].Y - ay) < 1e-6)
                    {
                        last.Add((bx, by));
                        continue;
                    }
                }
                runs.Add([(ax, ay), (bx, by)]);
            }
            if (runs.Count == 0) continue;
            foreach (var run in runs)
                for (int i = 0; i + 1 < run.Count; i++)
                    page.DrawLine(run[i].X, run[i].Y, run[i + 1].X, run[i + 1].Y, sectionLine, 0.2);
            index++;
            string label = mark.Name.Length > 0 ? mark.Name[..1].ToUpperInvariant() : index.ToString();
            // A short tick across the trace and the bubble at each end, square to the leg it sits on.
            var ends = new[]
            {
                (runs[0][0], runs[0][1]),
                (runs[^1][^1], runs[^1][^2]),
            };
            foreach (var ((x, y), (otherX, otherY)) in ends)
            {
                double angle = Math.Atan2(y - otherY, x - otherX);
                // A short tick across the trace and the bubble at its end.
                page.DrawLine(x - Math.Sin(angle) * 1.4, y + Math.Cos(angle) * 1.4, x + Math.Sin(angle) * 1.4,
                    y - Math.Cos(angle) * 1.4, sectionLine, 0.2);
                Bubble(page, x, y, label, sectionLine);
            }
        }
    }

    private static void Bubble(PdfPage page, double x, double y, string name, double widthMillimetres)
    {
        const double radius = 3.5;
        var points = new List<(double X, double Y)>(24);
        for (int i = 0; i < 24; i++)
        {
            double angle = i * Math.PI * 2 / 24;
            points.Add((x + radius * Math.Cos(angle), y + radius * Math.Sin(angle)));
        }
        page.DrawPolyline(points, widthMillimetres, true, 1);
        page.DrawText(name, x, y - 1.4, 3, PdfTextAlignment.Center);
    }

    /// <summary>Draws the dimension chains between the grid lines: a horizontal chain across the top of the plan and a
    /// vertical chain down its right side, each between neighbouring grids with the distance in millimetres.</summary>
    private static void DimChains(PdfPage page, PlanContent content, Func<double, double> toX,
        Func<double, double> toY)
    {
        if (page.Owner.Font == null) return;
        var xs = new List<double>();
        foreach (var grid in content.Grids)
        {
            double dx = grid.X2 - grid.X1, dy = grid.Y2 - grid.Y1;
            if (Math.Abs(dx) < Math.Abs(dy)) xs.Add((grid.X1 + grid.X2) / 2);
        }
        static List<double> Dedupe(List<double> values)
        {
            values.Sort();
            var result = new List<double>();
            foreach (double value in values)
                if (result.Count == 0 || Math.Abs(value - result[^1]) > 1) result.Add(value);
            return result;
        }
        xs = Dedupe(xs);

        // The chain stands just outside the building itself, not at the edge of the annotation margin.
        double wallMaxY = double.MinValue;
        foreach (var wall in content.Walls)
            wallMaxY = Math.Max(wallMaxY, Math.Max(wall.Y1, wall.Y2));
        foreach (var column in content.Columns)
            wallMaxY = Math.Max(wallMaxY, column.Y + column.Height);
        if (content.Walls.Count == 0 && content.Columns.Count == 0) return;
        // The reference draws the top chain just outside the building's outer wall face, not at the
        // stored location line (which sits at the wall's centre): measured on the sample plan the chain
        // is 22.1 pt above the north face at 1:100, i.e. 780 mm in the model. The old rule used the
        // location line + 550 mm, which put the chain 11.6 pt from the face — half the offset, so every
        // reference segment missed ours by more than the scorer's tolerance.
        const double DimChainOffsetMillimetres = 780;
        double faceMaxY = wallMaxY;
        foreach (var wall in content.Walls)
        {
            // A wall's stored location line runs along its centre: the face is half its width beyond.
            if (Math.Abs(wall.Y2 - wall.Y1) > Math.Abs(wall.X2 - wall.X1)) continue;
            faceMaxY = Math.Max(faceMaxY, Math.Max(wall.Y1, wall.Y2) + wall.Width / 2);
        }
        double top = toY(faceMaxY + DimChainOffsetMillimetres);
        if (xs.Count >= 2)
        {
            page.DrawLine(toX(xs[0]), top, toX(xs[^1]), top, 0.15, 0.3);
            for (int i = 0; i < xs.Count; i++)
            {
                double x = toX(xs[i]);
                page.DrawLine(x - 1.5, top - 1.5, x + 1.5, top + 1.5, 0.15, 0.3);
                if (i == 0) continue;
                double millimetres = xs[i] - xs[i - 1];
                page.DrawText($"{millimetres:0}", (x + toX(xs[i - 1])) / 2, top + 1.2, 2.2,
                    PdfTextAlignment.Center, 0.25);
            }
        }
        // The reference plan draws a single dimension chain, above the building: its Level 1 has no
        // bottom, left or right chain (those sides carry short per-element dimensions instead). The
        // printer used to invent all four, which added three chains the reference never inks.
    }

    /// <summary>The colour a plan fills a room with, by the name of the room, the way the reference fills it: a bath
    /// green, a hall blue, a kitchen tan, a laundry brown, a living room light green, a mechanical room green, a master
    /// bedroom blue violet, a master bath magenta, a bedroom cyan, an entry hall yellow, a linen teal; anything else light
    /// grey.</summary>
    internal static PdfColor RoomFill(string name)
    {
        string lower = name.ToLowerInvariant();
        PdfColor color = lower switch
        {
            _ when lower.Contains("master bath") => new PdfColor(0.75, 0.25, 0.7),
            _ when lower.Contains("master bedroom") => new PdfColor(0.35, 0.35, 0.7),
            _ when lower.Contains("entry") => new PdfColor(0.91, 0.86, 0.48),
            _ when lower.Contains("hall") => new PdfColor(0.23, 0.43, 0.62),
            _ when lower.Contains("bath") => new PdfColor(0.55, 0.65, 0.5),
            _ when lower.Contains("bedroom") => new PdfColor(0.5, 0.78, 0.82),
            _ when lower.Contains("linen") => new PdfColor(0.37, 0.66, 0.66),
            _ when lower.Contains("living") => new PdfColor(0.75, 0.8, 0.55),
            _ when lower.Contains("kitchen") || lower.Contains("dining") => new PdfColor(0.79, 0.75, 0.62),
            _ when lower.Contains("laundry") => new PdfColor(0.55, 0.37, 0.29),
            _ when lower.Contains("mech") || lower.Contains("plant") => new PdfColor(0.31, 0.56, 0.31),
            _ when lower.Contains("deck") || lower.Contains("porch") => new PdfColor(0.85, 0.85, 0.8),
            _ when lower.Contains("garage") || lower.Contains("carport") => new PdfColor(0.8, 0.8, 0.8),
            _ => new PdfColor(0.92, 0.92, 0.92),
        };
        return color.Lighten(0.2);
    }

    /// <summary>The signed area of a polygon (shoelace); its magnitude is the area and its sign the winding.</summary>
    private static double SignedArea(List<(double X, double Y)> points)
    {
        double sum = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var (x1, y1) = points[i];
            var (x2, y2) = points[(i + 1) % points.Count];
            sum += x1 * y2 - x2 * y1;
        }
        return sum / 2;
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
        element.LookupParameter(BuiltInParameter.LEVEL_PARAM)?.Value is int level && level > 0 ? level
        // A stair names its base level instead of a level (STAIRS_BASE_LEVEL_PARAM): without the fallback
        // no plan draws it, while the reference draws every stair of the level.
        : element.LookupParameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.Value is int stair && stair > 0 ? stair
        : null;

    private static double? WidthOf(Element element) =>
        element.LookupParameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM)?.Value is double width && width > 0
            ? width * FeetToMillimetres : null;
}
