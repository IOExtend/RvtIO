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
            {
                // The scale noted on the sheet: the most common scale of its placed views.
                var scales = new Dictionary<int, int>();
                foreach (var element in document.Elements)
                {
                    if (element.OwnerViewId != sheet.Id) continue;
                    if (element.CategoryId != (int)BuiltInCategory.OST_Viewports) continue;
                    if (element.ViewportViewId is not int viewId) continue;
                    int scale = document.GetElement(viewId)?.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value
                        is int value && value > 0 ? value : 0;
                    if (scale == 0) continue;
                    scales[scale] = scales.TryGetValue(scale, out int count) ? count + 1 : 1;
                }
                string scaleText = scales.Count == 0
                    ? ""
                    : "1:" + scales.OrderByDescending(pair => pair.Value).First().Key;
                TitleBlockPrinter.Draw(page2, document, sheet, Math.Min(180, page.WidthMillimetres * 0.22),
                    margin, scaleText, TitleLogo(document));
            }
        }

        // The drawings already put on the page: two sections the file places on top of each other are separated.
        var placedRects = new List<(double X, double Y, double Width, double Height)>();

        foreach (var element in document.Elements)
        {
            if (element.OwnerViewId != sheet.Id) continue;
            if (element.CategoryId != (int)BuiltInCategory.OST_Viewports) continue;
            if (element.ViewportViewId is not int viewId) continue;
            Element? view = document.GetElement(viewId);
            string name = view?.Name ?? ("view " + viewId);
            int scale = view?.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value is int value && value > 0
                ? value
                : element.ViewportScale ?? 100;
            double centerX = 0, centerY = 0;
            bool placed = false;
            int relocated = 0;
            if (element.ViewportOffset is { Length: >= 2 } offset)
            {
                placed = true;
                centerX = offset[0] * FeetToMillimetres - originX;
                centerY = offset[1] * FeetToMillimetres - originY;
            }
            double frameX = centerX, frameY = centerY, frameWidth = 0, frameHeight = 0;
            if (placed && view?.RootClassName == "DBView3d")
            {
                // A 3D view: an isometric projection of the model, white faces with their outlines, as the massing the
                // view would show without its rendering.
                var model = ElevationPrinter.Collect(document, "iso");
                if (model != null)
                {
                    frameWidth = (model.MaxX - model.MinX) / scale + ElevationPrinter.LabelMarginMillimetres;
                    frameHeight = (model.MaxY - model.MinY) / scale;
                    frameX = centerX - frameWidth / 2;
                    frameY = centerY - frameHeight / 2;
                    double m = options.ViewMarginMillimetres;
                    page2.BeginClip(frameX - m, frameY - m, frameWidth + 2 * m, frameHeight + 2 * m);
                    ElevationPrinter.DrawContent(page2, model, scale, frameX, frameY);
                    page2.EndClip();
                }
            }
            else if (placed && view?.RootClassName == "DBViewRendering")
            {
                // A rendering: the JPEG Revit stored for the view, placed at the offset of its viewport.
                if (RenderingImage(document, viewId) is { } jpeg)
                {
                    double height = 110, width = height * jpeg.Width / jpeg.Height;
                    if (width > 240)
                    {
                        width = 240;
                        height = width * jpeg.Height / jpeg.Width;
                    }
                    // The images keep clear of the title block strip on the right.
                    double stripLeft = page.WidthMillimetres - 5
                        - Math.Min(180, page.WidthMillimetres * 0.22) - 3;
                    double x = Math.Min(centerX - width / 2, stripLeft - width);
                    page2.DrawImage(jpeg, x, centerY - height / 2, width, height);
                }
            }
            else if (placed && plans.TryGetValue(viewId, out var plan))
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
                            sitePlan.FloorLoops,
                            new List<List<(double X, double Y)>>(),
                            new List<(List<(double X, double Y)> Polygon, string Name, string Number, double AreaSquareFeet)>(),
                            new List<List<(double X, double Y)>>(),
                            new List<(double MidX, double MidY, double Nx, double Ny, string Text, bool IsWindow)>(),
                            sitePlan.MinX, sitePlan.MinY, sitePlan.MaxX, sitePlan.MaxY);
                        // The house is filled dark on a site plan, the way the reference fills the building: the rooms
                        // of the level under its walls.
                        foreach (var room in sitePlan.Rooms)
                        {
                            var polygon = room.Polygon.Select(point => (
                                (point.X - site.MinX) / scale + frameX,
                                (point.Y - site.MinY) / scale + frameY)).ToList();
                            if (polygon.Count >= 3) page2.DrawPolyline(polygon, 0, true, 0.45);
                        }
                        PlanPrinter.DrawContent(page2, wallsOnly, scale,
                            (sitePlan.MinX - site.MinX) / scale + frameX,
                            (sitePlan.MinY - site.MinY) / scale + frameY);
                    }
                    page2.EndClip();
                    // The planting schedule sits under the view title, clear of the site plan.
                    if (pdf.Font != null)
                        DrawPlantingSchedule(page2, document, Math.Max(frameX, 8), frameY - 18);
                }
            }
            else if (placed && ElevationPrinter.Views(document).Any(view => view.ViewId == viewId)
                && (ElevationPrinter.DirectionOf(name) ?? (view?.NativeRecordClass == 1016
                    || view?.RootClassName is "DBViewSection" or "DBViewElevation" ? "section" : null))
                    is string direction)
            {
                // An elevation or a section: the model faces projected on the view's plane when the reader decoded it
                // (a section is then cut at its plane), otherwise on the plane of the direction taken from the name.
                Element? viewElement = document.GetElement(viewId);
                // A detail section (1:10 to 1:50) shows the cut near the point of its plane: a window about three by
                // two metres around it, the size a wall or roof detail covers. The detail is printed only when its
                // viewport sits on the drawable part of the page: the file places some of them outside it.
                // A stair section is printed whole like a building section, not as a windowed detail; a stair detail
                // (1:20) shows the stair in its enclosure, so it is not limited to the stairs alone. It takes the right
                // part of the sheet, the way the reference places it.
                bool stairSection = name.Contains("stair", StringComparison.OrdinalIgnoreCase);
                bool stairsOnly = stairSection && scale >= 50;
                double stripLeftForStair = page.WidthMillimetres - 5
                    - Math.Min(180, page.WidthMillimetres * 0.22) - 3;
                bool detailSection = direction == "section" && scale < 100 && !stairSection
                    && viewElement?.ViewDirection is { Length: 3 };
                double stripLeft = page.WidthMillimetres - 5
                    - Math.Min(180, page.WidthMillimetres * 0.22) - 3;
                (double Width, double Height)? detailWindow = detailSection ? (3000, 2200) : null;
                bool detailOnPage = !detailSection || (centerX > 10 && centerX < stripLeft - 10
                    && centerY > 10 && centerY < page.HeightMillimetres - 15);

                var elevation = detailOnPage
                    ? ElevationPrinter.Collect(document, direction, stairsOnly, viewElement, detailWindow)
                    : null;
                if (elevation != null)
                {
                    double drawScale = scale;
                    frameWidth = (elevation.MaxX - elevation.MinX) / drawScale
                        + ElevationPrinter.LabelMarginMillimetres;
                    frameHeight = (elevation.MaxY - elevation.MinY) / drawScale;
                    // A view whose drawing would not fit its place on the sheet (a stair detail at 1:20 is taller than
                    // the page) is drawn at a scale that fits, and one the file places off the page is put in the free
                    // space of the sheet.
                    double drawableWidth = Math.Max(60, stripLeft - 20);
                    double drawableHeight = Math.Max(60, page.HeightMillimetres - 30);
                    // A stair detail (1:20) takes the right part of the sheet, not the whole drawing area.
                    if (stairSection && scale < 50)
                    {
                        drawableWidth = Math.Min(drawableWidth, stripLeft * 0.55);
                        drawableHeight = Math.Min(drawableHeight, page.HeightMillimetres * 0.6);
                    }
                    if (frameWidth > drawableWidth || frameHeight > drawableHeight)
                    {
                        double factor = Math.Max(frameWidth / drawableWidth, frameHeight / drawableHeight);
                        frameWidth /= factor;
                        frameHeight /= factor;
                        drawScale *= factor;
                    }
                    if (stairSection && scale < 50)
                    {
                        centerX = stripLeftForStair - 20 - frameWidth / 2;
                        centerY = page.HeightMillimetres - 40 - frameHeight / 2;
                    }
                    else if (centerX < 10 || centerX > stripLeft - 10 || centerY < 10
                        || centerY > page.HeightMillimetres - 15)
                    {
                        centerX = 40 + frameWidth / 2 + relocated * Math.Min(300, drawableWidth / 2);
                        if (centerX + frameWidth / 2 > stripLeft - 10) centerX = stripLeft - 10 - frameWidth / 2;
                        centerY = page.HeightMillimetres - 40 - frameHeight / 2;
                        relocated++;
                    }
                    // The drawing stays inside the page: a view the file places against an edge is moved in.
                    frameX = centerX - frameWidth / 2;
                    frameY = centerY - frameHeight / 2;
                    if (frameX < 5) { frameX = 5; centerX = frameX + frameWidth / 2; }
                    if (frameY < 5) { frameY = 5; centerY = frameY + frameHeight / 2; }
                    if (frameX + frameWidth > stripLeft - 5)
                    {
                        frameX = stripLeft - 5 - frameWidth;
                        centerX = frameX + frameWidth / 2;
                    }
                    if (frameY + frameHeight > page.HeightMillimetres - 5)
                    {
                        frameY = page.HeightMillimetres - 5 - frameHeight;
                        centerY = frameY + frameHeight / 2;
                    }
                    // A drawing the file places on top of one already on the page goes to the next place of the top row.
                    for (int guard = 0; guard < 6; guard++)
                    {
                        bool overlaps = false;
                        foreach (var other in placedRects)
                        {
                            double overlapWidth = Math.Min(frameX + frameWidth, other.X + other.Width)
                                - Math.Max(frameX, other.X);
                            double overlapHeight = Math.Min(frameY + frameHeight, other.Y + other.Height)
                                - Math.Max(frameY, other.Y);
                            if (overlapWidth <= 0 || overlapHeight <= 0) continue;
                            double overlap = overlapWidth * overlapHeight;
                            double smaller = Math.Min(frameWidth * frameHeight, other.Width * other.Height);
                            if (overlap > smaller * 0.05) overlaps = true;
                        }
                        if (!overlaps) break;
                        relocated++;
                        // The free place of the top row, from the right of the drawing area.
                        centerX = stripLeft - 15 - frameWidth / 2;
                        centerY = page.HeightMillimetres - 40 - frameHeight / 2;
                        if (centerX - frameWidth / 2 < 15) break;
                        frameX = centerX - frameWidth / 2;
                        frameY = centerY - frameHeight / 2;
                    }
                    double m = options.ViewMarginMillimetres;
                    page2.BeginClip(frameX - m, frameY - m, frameWidth + 2 * m, frameHeight + 2 * m);
                    ElevationPrinter.DrawContent(page2, elevation, drawScale, frameX, frameY,
                        direction == "section" ? 0.82 : 1);
                    page2.EndClip();
                    placedRects.Add((frameX, frameY, frameWidth, frameHeight));
                }
            }
            // A viewport whose view prints nothing (a wall section at 1:20, a schedule) is not titled either.
            if (options.DrawViewLabels && pdf.Font != null && (!placed || frameHeight > 0))
            {
                // The view title as Revit draws it: a circled detail number, the view name beside it, the scale below and
                // a rule over the title.
                string detail = element.LookupParameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.Value as string ?? "";
                // A title stays on the page even when its drawing reaches the lower border.
                double labelY = placed && frameHeight > 0 ? Math.Max(frameY - 5, 10) : centerY;
                double textX = frameX;
                if (detail.Length > 0)
                {
                    var circle = new List<(double X, double Y)>(20);
                    for (int i = 0; i < 20; i++)
                    {
                        double angle = i * Math.PI * 2 / 20;
                        circle.Add((frameX + 3 + 3 * Math.Cos(angle), labelY + 1.2 + 3 * Math.Sin(angle)));
                    }
                    page2.DrawPolyline(circle, 0.15, true, 1);
                    page2.DrawText(detail, frameX + 3, labelY + 0.2, 2.4, PdfTextAlignment.Center);
                    textX = frameX + 8;
                }
                page2.DrawLine(frameX, labelY + 6, frameX + Math.Min(Math.Max(frameWidth, 40), 120), labelY + 6, 0.2);
                page2.DrawText(name, textX, labelY, 3.5);
                page2.DrawText($"1:{scale}", textX, labelY - 3.5, 2.5, PdfTextAlignment.Left, 0.45);
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PdfRasterImage?> Logos = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, int ViewId), PdfJpegImage?>
        Renderings = new();

    /// <summary>The JPEG a rendering view stores: the image symbol the view owns carries its type, and the type's block
    /// holds the image.</summary>
    private static PdfJpegImage? RenderingImage(RvtDocument document, int viewId)
    {
        if (document.SourcePath is not { Length: > 0 } path) return null;
        return Renderings.GetOrAdd((path, viewId), key =>
        {
            foreach (var element in document.Elements)
            {
                if (element.OwnerViewId != key.ViewId || element.RootClassName != "ImageSymbol") continue;
                if (element.TypeId <= 0) continue;
                var image = RvtRaster.FindJpeg(key.Path, element.TypeId);
                if (image != null) return image;
            }
            return null;
        });
    }

    /// <summary>The logo of the title block family: the image symbol whose name holds "logo", decoded from the file.
    /// </summary>
    private static PdfRasterImage? TitleLogo(RvtDocument document)
    {
        if (document.SourcePath is not { Length: > 0 } path) return null;
        return Logos.GetOrAdd(path, key =>
        {
            foreach (var element in document.Elements)
            {
                if (element.RootClassName != "ImageSymbol" || element.ImageWidth is not int width
                    || element.ImageHeight is not int height) continue;
                if (element.Name is not { Length: > 0 } name
                    || !name.Contains("logo", StringComparison.OrdinalIgnoreCase)) continue;
                var image = RvtRaster.FindPng(key, width, height);
                if (image != null) return image;
            }
            // The image-symbol sizes are not decoded for every file: fall back to the widest logo-shaped PNG.
            return RvtRaster.FindLogo(key);
        });
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
        // The table of the reference site plan: the type mark of the plant (T1, T2, ... as the plan tags them), its
        // common name and its count.
        page.DrawText("Planting Schedule", x, y, 3.5);
        page.DrawText("Type Mark", x, y - 5, 2.2, PdfTextAlignment.Left, 0.4);
        page.DrawText("Common Name", x + 20, y - 5, 2.2, PdfTextAlignment.Left, 0.4);
        page.DrawText("Count", x + 65, y - 5, 2.2, PdfTextAlignment.Right, 0.4);
        double row = y - 9;
        int index = 0;
        foreach (var (name, count) in groups)
        {
            index++;
            page.DrawText($"T{index}", x, row, 2.4);
            page.DrawText(name, x + 20, row, 2.4);
            page.DrawText(count.ToString(), x + 65, row, 2.4, PdfTextAlignment.Right);
            row -= 4.5;
        }
        // The table of the reference site plan has a border and a rule under the header.
        double tableTop = y + 3, tableBottom = row + 1.8;
        page.DrawRectangle(x - 2, tableBottom, 72, tableTop - tableBottom, 0.15);
        page.DrawLine(x - 2, y - 6.5, x + 70, y - 6.5, 0.12);
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
