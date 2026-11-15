using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>Options of a composed sheet page: the paper (null takes the size the title block spans), and whether the
/// view labels are written.</summary>
public sealed class SheetComposeOptions
{
    /// <summary>The paper; null reads the extent of the title block instance's type (the page the sheet is set up
    /// for) and snaps it to A4 to A0.</summary>
    public PdfPageSize? PaperSize { get; set; }

    /// <summary>True (the default) writes the name and scale of every placed view under its frame.</summary>
    public bool DrawViewLabels { get; set; } = true;

    /// <summary>True (the default) writes the room legend of a plan under its frame: a colour swatch and the room name
    /// for every kind of room on the plan.</summary>
    public bool DrawRoomLegend { get; set; } = true;

    /// <summary>The margin around a view frame that its content may still be drawn in, in millimetres.</summary>
    public double ViewMarginMillimetres { get; set; } = 3;

    /// <summary>True (the default) draws the page border and, at its lower-right corner, the sheet number and name.
    /// This stands in for the title block while the linework of its family is not decoded.</summary>
    public bool DrawPageFrame { get; set; } = true;
}

/// <summary>A sheet that can be composed: the element id, its number and name, the page its title block spans and the
/// number of placed viewports and text notes.</summary>
/// <param name="SheetId">The element id of the sheet.</param>
/// <param name="Number">The sheet number, null when empty.</param>
/// <param name="Name">The sheet name, null when empty.</param>
/// <param name="Page">The page the title block spans.</param>
/// <param name="ViewportCount">The number of placed viewports.</param>
/// <param name="TextCount">The number of text notes the sheet owns.</param>
public sealed record ComposedSheet(int SheetId, string? Number, string? Name, PdfPageSize Page, int ViewportCount,
    int TextCount);

/// <summary>
/// Composes a sheet page the way Revit prints it: the page is the size the title block spans, every placed viewport
/// draws its view at the offset the file stores for it (the plan of the view's level at the view scale), each view is
/// labelled with its name and scale, and the sheet's own text notes are written at their positions. The linework of the
/// title block family and the fills of rooms are not decoded yet (see docs/printing.md).
/// </summary>
public static class SheetComposer
{
    private const double FeetToMillimetres = 304.8;

    /// <summary>The sheets of the document: those with a sheet number, by number and then id.</summary>
    /// <param name="document">The opened document.</param>
    public static IReadOnlyList<ComposedSheet> Sheets(RvtDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var viewports = new Dictionary<int, int>();
        var texts = new Dictionary<int, int>();
        foreach (var element in document.Elements)
        {
            if (element.OwnerViewId is not int owner) continue;
            if (element.CategoryId == (int)BuiltInCategory.OST_Viewports && element.ViewportViewId != null)
                viewports[owner] = viewports.TryGetValue(owner, out int count) ? count + 1 : 1;
            if (element.Text != null) texts[owner] = texts.TryGetValue(owner, out int textCount) ? textCount + 1 : 1;
        }
        var sheets = new List<ComposedSheet>();
        foreach (var sheet in document.Elements)
        {
            if (sheet.LookupParameter(BuiltInParameter.SHEET_NUMBER)?.Value is not string number || number.Length == 0)
                continue;
            var (page, _, _) = PageOf(document, sheet, null);
            sheets.Add(new ComposedSheet(sheet.Id, number,
                sheet.LookupParameter(BuiltInParameter.SHEET_NAME)?.Value as string, page,
                viewports.TryGetValue(sheet.Id, out int vp) ? vp : 0,
                texts.TryGetValue(sheet.Id, out int tx) ? tx : 0));
        }
        sheets.Sort((left, right) =>
        {
            int order = string.CompareOrdinal(left.Number, right.Number);
            return order != 0 ? order : left.SheetId.CompareTo(right.SheetId);
        });
        return sheets;
    }

    /// <summary>Composes one sheet to a one-page PDF document.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="sheetId">The element id of the sheet.</param>
    /// <param name="options">The paper and labels; null uses the defaults.</param>
    public static PdfDocument Render(RvtDocument document, int sheetId, SheetComposeOptions? options = null) =>
        RenderSheets(document, [sheetId], options);

    /// <summary>Composes several sheets, one page each, to one PDF document.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="sheetIds">The element ids of the sheets.</param>
    /// <param name="options">The paper and labels; null uses the defaults.</param>
    /// <exception cref="InvalidOperationException">An element id is not a sheet with a number.</exception>
    public static PdfDocument RenderSheets(RvtDocument document, IReadOnlyList<int> sheetIds,
        SheetComposeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sheetIds);
        options ??= new SheetComposeOptions();
        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _), Compress = true };
        var plans = new Dictionary<int, PlanView>();
        foreach (var view in PlanPrinter.Views(document)) plans[view.ViewId] = view;
        foreach (int sheetId in sheetIds)
        {
            Element? sheet = document.GetElement(sheetId);
            if (sheet == null || sheet.LookupParameter(BuiltInParameter.SHEET_NUMBER)?.Value is not string number
                || number.Length == 0)
                throw new InvalidOperationException($"Element {sheetId} is not a numbered sheet.");
            AddSheetPage(pdf, document, sheet, plans, options);
        }
        if (pdf.Pages.Count == 0) throw new InvalidOperationException("No sheet was given to compose.");
        return pdf;
    }

    private static void AddSheetPage(PdfDocument pdf, RvtDocument document, Element sheet,
        Dictionary<int, PlanView> plans, SheetComposeOptions options)
    {
        var (page, originX, originY) = PageOf(document, sheet, options);
        PdfPage page2 = pdf.AddPage(page);

        // The title block family's linework is not decoded: the page border and a title block strip on the right, built
        // from the sheet's own fields, stand in for it, so a printed sheet reads as a framed page of a set.
        if (options.DrawPageFrame)
        {
            double margin = 5;
            page2.DrawRectangle(margin, margin, page.WidthMillimetres - 2 * margin,
                page.HeightMillimetres - 2 * margin, 0.35);
            if (pdf.Font != null)
                TitleBlockPrinter.Draw(page2, document, sheet,
                    Math.Min(180, page.WidthMillimetres * 0.22), margin);
        }

        foreach (var element in document.Elements)
        {
            if (element.OwnerViewId != sheet.Id) continue;
            if (element.CategoryId != (int)BuiltInCategory.OST_Viewports) continue;
            if (element.ViewportViewId is not int viewId) continue;
            Element? view = document.GetElement(viewId);
            string name = view?.Name ?? ("view " + viewId);
            int scale = view?.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value is int value && value > 0 ? value : 100;
            double centerX = 0, centerY = 0;
            bool placed = false;
            if (element.ViewportOffset is { Length: >= 2 } offset)
            {
                placed = true;
                centerX = offset[0] * FeetToMillimetres - originX;
                centerY = offset[1] * FeetToMillimetres - originY;
            }
            double frameX = centerX, frameY = centerY, frameWidth = 0, frameHeight = 0;
            if (placed && plans.TryGetValue(viewId, out var plan))
            {
                int viewScale = plan.Scale;
                var content = PlanPrinter.CollectLevel(document, plan.LevelId);
                if (content != null)
                {
                    frameWidth = (content.MaxX - content.MinX) / viewScale;
                    frameHeight = (content.MaxY - content.MinY) / viewScale;
                    frameX = centerX - frameWidth / 2;
                    frameY = centerY - frameHeight / 2;
                    double m = options.ViewMarginMillimetres;
                    page2.BeginClip(frameX - m, frameY - m, frameWidth + 2 * m, frameHeight + 2 * m);
                    PlanPrinter.DrawContent(page2, content, viewScale, frameX, frameY);
                    page2.EndClip();
                    if (options.DrawRoomLegend && pdf.Font != null && content.Rooms.Count > 0)
                        DrawRoomLegend(page2, content.Rooms.Select(room => room.Name), Math.Max(frameX, 8),
                            frameY - 16);
                }
            }
            else if (placed && name.Contains("site", StringComparison.OrdinalIgnoreCase))
            {
                // A site view: the topography and the planting, not the plan of a level.
                var site = SitePrinter.Collect(document);
                if (site != null)
                {
                    frameWidth = (site.MaxX - site.MinX) / scale;
                    frameHeight = (site.MaxY - site.MinY) / scale;
                    frameX = centerX - frameWidth / 2;
                    frameY = centerY - frameHeight / 2;
                    double m = options.ViewMarginMillimetres;
                    page2.BeginClip(frameX - m, frameY - m, frameWidth + 2 * m, frameHeight + 2 * m);
                    SitePrinter.DrawContent(page2, site, scale, frameX, frameY);
                    // The building stands on the site: the walls of the level with most walls go on the same plan,
                    // without its rooms, furniture and dimensions, which a site plan does not show.
                    int? bestLevel = null;
                    int bestCount = 0;
                    foreach (var level in PlanPrinter.Levels(document))
                    {
                        var content = PlanPrinter.CollectLevel(document, level.LevelId);
                        if (content == null || content.Walls.Count <= bestCount) continue;
                        bestLevel = level.LevelId;
                        bestCount = content.Walls.Count;
                    }
                    if (bestLevel is int levelId && PlanPrinter.CollectLevel(document, levelId) is { } sitePlan)
                    {
                        var wallsOnly = new PlanPrinter.PlanContent(sitePlan.Walls, sitePlan.Columns,
                            new List<(double X1, double Y1, double X2, double Y2, string Name)>(),
                            sitePlan.Gaps, sitePlan.WindowGaps, sitePlan.DoorGaps,
                            new List<List<(double X, double Y)>>(),
                            new List<List<(double X, double Y)>>(),
                            new List<(List<(double X, double Y)> Polygon, string Name, string Number, double AreaSquareFeet)>(),
                            new List<List<(double X, double Y)>>(),
                            sitePlan.MinX, sitePlan.MinY, sitePlan.MaxX, sitePlan.MaxY);
                        PlanPrinter.DrawContent(page2, wallsOnly, scale,
                            (sitePlan.MinX - site.MinX) / scale + frameX,
                            (sitePlan.MinY - site.MinY) / scale + frameY);
                    }
                    page2.EndClip();
                    if (pdf.Font != null) DrawPlantingSchedule(page2, document, Math.Max(frameX, 8), frameY - 6);
                }
            }
            else if (placed && ElevationPrinter.DirectionOf(name) is string direction)
            {
                // An elevation: the model faces projected on the vertical plane of the direction. The view plane itself
                // is not decoded, so the direction comes from the view name.
                var elevation = ElevationPrinter.Collect(document, direction);
                if (elevation != null)
                {
                    frameWidth = (elevation.MaxX - elevation.MinX) / scale;
                    frameHeight = (elevation.MaxY - elevation.MinY) / scale;
                    frameX = centerX - frameWidth / 2;
                    frameY = centerY - frameHeight / 2;
                    double m = options.ViewMarginMillimetres;
                    page2.BeginClip(frameX - m, frameY - m, frameWidth + 2 * m, frameHeight + 2 * m);
                    ElevationPrinter.DrawContent(page2, elevation, scale, frameX, frameY);
                    page2.EndClip();
                }
            }
            if (options.DrawViewLabels && pdf.Font != null)
            {
                double labelY = placed && frameHeight > 0 ? frameY - 5 : centerY;
                page2.DrawText(name, frameX, labelY, 3.5);
                page2.DrawText($"1:{scale}", frameX, labelY - 3.5, 2.5, PdfTextAlignment.Left, 0.45);
            }
        }

        foreach (var element in document.Elements)
        {
            if (element.OwnerViewId != sheet.Id) continue;
            if (element.Text is not { Length: > 0 } text || element.TextPosition is not { Length: >= 2 } position)
                continue;
            double x = position[0] * FeetToMillimetres - originX;
            double y = position[1] * FeetToMillimetres - originY;
            double height = element.TextFontSizePoints is double points && points > 0 ? points * 25.4 / 72 : 2.5;
            string[] lines = text.Split('\n');
            for (int line = 0; line < lines.Length; line++)
                page2.DrawText(lines[line], x, y - line * height * 1.25, height);
        }
    }

    /// <summary>Writes the planting schedule of the site: one row per kind of plant with its count, under the plan.
    /// </summary>
    private static void DrawPlantingSchedule(PdfPage page, RvtDocument document, double x, double y)
    {
        var groups = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Planting) continue;
            string name = element.Name ?? "";
            if (name.Length == 0) continue;
            groups[name] = groups.TryGetValue(name, out int count) ? count + 1 : 1;
        }
        if (groups.Count == 0) return;
        page.DrawText("Planting Schedule", x, y, 3.5);
        page.DrawText("Type", x, y - 5, 2.4, PdfTextAlignment.Left, 0.4);
        page.DrawText("Count", x + 40, y - 5, 2.4, PdfTextAlignment.Right, 0.4);
        double row = y - 9;
        foreach (var (name, count) in groups)
        {
            page.DrawText(name, x, row, 2.4);
            page.DrawText(count.ToString(), x + 40, row, 2.4, PdfTextAlignment.Right);
            row -= 4.5;
        }
    }

    /// <summary>Writes the room legend: a title and one row per kind of room, a colour swatch and the name. The rows
    /// start at (x, y) and go down.</summary>
    private static void DrawRoomLegend(PdfPage page, IEnumerable<string> names, double x, double y)
    {
        var seen = new List<string>();
        foreach (string name in names)
        {
            if (name.Length == 0) continue;
            if (!seen.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                seen.Add(name);
        }
        if (seen.Count == 0) return;
        page.DrawText("Room Legend", x, y, 3.5);
        double row = y - 6;
        foreach (string name in seen)
        {
            page.DrawPolyline([(x, row), (x + 4, row), (x + 4, row + 4), (x, row + 4)], 0.15, true,
                PlanPrinter.RoomFill(name));
            page.DrawText(name, x + 6, row + 0.8, 2.8);
            row -= 5.5;
        }
    }

    /// <summary>The page a sheet is set up for: the extent of the type of its title block instance, snapped to a
    /// standard paper when it is close, and the lower-left corner of that extent.</summary>
    private static (PdfPageSize Page, double OriginX, double OriginY) PageOf(RvtDocument document, Element sheet,
        SheetComposeOptions? options)
    {
        if (options?.PaperSize is { } chosen) return (chosen, 0, 0);
        foreach (var element in document.Elements)
        {
            if (element.OwnerViewId != sheet.Id || element.CategoryId != (int)BuiltInCategory.OST_TitleBlocks) continue;
            Element? type = document.GetElement(element.TypeId);
            if (type?.BoundingBox is not { Length: 6 } box || box[3] <= box[0] || box[4] <= box[1]) continue;
            double width = (box[3] - box[0]) * FeetToMillimetres;
            double height = (box[4] - box[1]) * FeetToMillimetres;
            return (Snap(width, height), box[0] * FeetToMillimetres, box[1] * FeetToMillimetres);
        }
        return (PdfPageSize.A1.Landscape, 0, 0);
    }

    /// <summary>The standard paper the extent matches within ten millimetres, in the orientation of the extent.</summary>
    internal static PdfPageSize Snap(double width, double height)
    {
        PdfPageSize[] standard = [PdfPageSize.A4, PdfPageSize.A3, PdfPageSize.A2, PdfPageSize.A1, PdfPageSize.A0];
        foreach (var size in standard)
        {
            foreach (var candidate in new[] { size, size.Landscape })
            {
                if (Math.Abs(candidate.WidthMillimetres - width) <= 10
                    && Math.Abs(candidate.HeightMillimetres - height) <= 10)
                    return candidate;
            }
        }
        return new PdfPageSize(width, height);
    }
}
