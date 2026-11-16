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
        // A composed sheet keeps the paper of its title block: --paper applies to plans and views, not to a sheet.
        var (page, originX, originY) = PageOf(document, sheet, null);
        PdfPageSize layout = page;
        PdfPage page2 = pdf.AddPage(page);
        // The bookmark of the page, as Revit names separate files: "<number> - <name>".
        if (sheet.LookupParameter(BuiltInParameter.SHEET_NUMBER)?.Value is string sheetNumber
            && sheetNumber.Length > 0)
            page2.OutlineTitle = sheet.LookupParameter(BuiltInParameter.SHEET_NAME)?.Value is string sheetName
                && sheetName.Length > 0 ? sheetNumber + " - " + sheetName : sheetNumber;
        // The title block family's linework is not decoded: the page border and a title block strip on the right, built
        // from the sheet's own fields, stand in for it, so a printed sheet reads as a framed page of a set.
        double margin = 5;
        if (options.DrawPageFrame)
            page2.DrawRectangle(margin, margin, layout.WidthMillimetres - 2 * margin,
                layout.HeightMillimetres - 2 * margin, 0.35);

        // The drawings already put on the page: two sections the file places on top of each other are separated.
        var placedRects = new List<(double X, double Y, double Width, double Height)>();
        // The scale each drawing was finally printed at: a view the layout had to reduce no longer shows its nominal
        // scale, and the title of the view and the sheet tell the scale the geometry is printed at.
        var effectiveScales = new Dictionary<int, int>();
        double areaLeft = 5, areaBottom = 5;
        double areaRight = layout.WidthMillimetres - 5 - Math.Min(180, layout.WidthMillimetres * 0.22) - 3;
        double areaTop = layout.HeightMillimetres - 5;

        // A drawing larger than its place on the sheet is reduced until it fits, and every drawing is kept inside the
        // page frame and left of the title block strip: no line of a view may run into the frame or the strip.
        void FitFrame(ref double width, ref double height, ref double drawScale)
        {
            double drawableWidth = Math.Max(40, areaRight - areaLeft);
            double drawableHeight = Math.Max(40, areaTop - areaBottom);
            if (width <= drawableWidth && height <= drawableHeight) return;
            double factor = Math.Max(width / drawableWidth, height / drawableHeight);
            width /= factor;
            height /= factor;
            drawScale *= factor;
        }

        void ClampFrame(ref double x, ref double y, double width, double height)
        {
            // The margin around a drawing holds its annotations (dimension chains, view titles): it stays inside the
            // page frame too, so no annotation is cut by the frame or runs under the title block strip.
            double inset = options.ViewMarginMillimetres;
            if (x < areaLeft + inset) x = areaLeft + inset;
            if (y < areaBottom + inset) y = areaBottom + inset;
            if (x + width > areaRight - inset) x = areaRight - inset - width;
            if (y + height > areaTop - inset) y = areaTop - inset - height;
        }

        void ClipFrame(double frameX, double frameY, double frameWidth, double frameHeight)
        {
            double m = options.ViewMarginMillimetres;
            double x0 = Math.Max(areaLeft, frameX - m), y0 = Math.Max(areaBottom, frameY - m);
            double x1 = Math.Min(areaRight, frameX + frameWidth + m);
            double y1 = Math.Min(areaTop, frameY + frameHeight + m);
            page2.BeginClip(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }


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
            int effectiveScale = scale;
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
                    double drawScale = scale;
                    frameWidth = (model.MaxX - model.MinX) / drawScale + ElevationPrinter.LabelMarginMillimetres;
                    frameHeight = (model.MaxY - model.MinY) / drawScale;
                    FitFrame(ref frameWidth, ref frameHeight, ref drawScale);
                    frameX = centerX - frameWidth / 2;
                    frameY = centerY - frameHeight / 2;
                    ClampFrame(ref frameX, ref frameY, frameWidth, frameHeight);
                    ClipFrame(frameX, frameY, frameWidth, frameHeight);
                    ElevationPrinter.DrawContent(page2, model, drawScale, frameX, frameY);
                    page2.EndClip();
                    effectiveScale = (int)Math.Round(drawScale);
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
                    double stripLeft = layout.WidthMillimetres - 5
                        - Math.Min(180, layout.WidthMillimetres * 0.22) - 3;
                    double x = Math.Min(centerX - width / 2, stripLeft - width);
                    page2.DrawImage(jpeg, x, centerY - height / 2, width, height);
                }
            }
            else if (placed && plans.TryGetValue(viewId, out var plan))
            {
                double viewScale = plan.Scale;
                var content = PlanPrinter.CollectLevel(document, plan.LevelId);
                if (content != null)
                {
                    double drawScale = viewScale;
                    frameWidth = (content.MaxX - content.MinX) / drawScale;
                    frameHeight = (content.MaxY - content.MinY) / drawScale;
                    FitFrame(ref frameWidth, ref frameHeight, ref drawScale);
                    frameX = centerX - frameWidth / 2;
                    frameY = centerY - frameHeight / 2;
                    ClampFrame(ref frameX, ref frameY, frameWidth, frameHeight);
                    double drawOriginX = frameX, drawOriginY = frameY;
                    // A viewport whose view decoded a crop frame draws the crop at the view's own scale, placed by
                    // the viewport offset the way Revit places it (sheet = offset + view / scale): the frame is the
                    // crop rectangle, the content origin is shifted so the crop lands on it, and the content is
                    // clipped to it. Nothing is reduced to fit: the scale stays true. Views without a crop keep the
                    // whole-model frame above.
                    bool cropped = view?.ViewCrop is { Active: true } crop
                        && TryCropFrame(centerX, centerY, viewScale, crop, content.MinX, content.MinY,
                            out frameX, out frameY, out frameWidth, out frameHeight, out drawOriginX, out drawOriginY);
                    if (cropped)
                    {
                        drawScale = viewScale;
                        page2.BeginClip(frameX, frameY, Math.Max(0, frameWidth), Math.Max(0, frameHeight));
                    }
                    else
                    {
                        ClipFrame(frameX, frameY, frameWidth, frameHeight);
                    }
                    PlanPrinter.DrawContent(page2, content, drawScale, drawOriginX, drawOriginY, 0.9,
                        ElevationPrinter.SectionMarks(document));
                    page2.EndClip();
                    effectiveScale = (int)Math.Round(drawScale);
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
                    double drawScale = scale;
                    frameWidth = (site.MaxX - site.MinX) / drawScale;
                    frameHeight = (site.MaxY - site.MinY) / drawScale;
                    FitFrame(ref frameWidth, ref frameHeight, ref drawScale);
                    frameX = centerX - frameWidth / 2;
                    frameY = centerY - frameHeight / 2;
                    ClampFrame(ref frameX, ref frameY, frameWidth, frameHeight);
                    ClipFrame(frameX, frameY, frameWidth, frameHeight);
                    SitePrinter.DrawContent(page2, site, drawScale, frameX, frameY);
                    effectiveScale = (int)Math.Round(drawScale);
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
                                (point.X - site.MinX) / drawScale + frameX,
                                (point.Y - site.MinY) / drawScale + frameY)).ToList();
                            if (polygon.Count >= 3) page2.DrawPolyline(polygon, 0, true, 0.45);
                        }
                        PlanPrinter.DrawContent(page2, wallsOnly, drawScale,
                            (sitePlan.MinX - site.MinX) / drawScale + frameX,
                            (sitePlan.MinY - site.MinY) / drawScale + frameY);
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
                double stripLeftForStair = layout.WidthMillimetres - 5
                    - Math.Min(180, layout.WidthMillimetres * 0.22) - 3;
                bool detailSection = direction == "section" && scale < 100 && !stairSection
                    && viewElement?.ViewDirection is { Length: 3 };
                double stripLeft = layout.WidthMillimetres - 5
                    - Math.Min(180, layout.WidthMillimetres * 0.22) - 3;
                // A stair detail (1:20) is cropped to the stair enclosure, like the details of the reference: a window of
                // 80 x 60 mm on the page around the point of its plane.
                (double Width, double Height)? detailWindow = detailSection ? (3000, 2200)
                    : stairSection && scale < 50 ? (130 * scale, 95 * scale)
                    : null;
                // A detail whose viewport the file puts against the border or outside the page is not printed.
                bool detailOnPage = !detailSection || (centerX > 40 && centerX < stripLeft - 40
                    && centerY > 40 && centerY < layout.HeightMillimetres - 40);
                var elevation = detailOnPage
                    ? ElevationPrinter.Collect(document, direction, stairsOnly, viewElement, detailWindow)
                    : null;
                // A viewport whose view decoded a crop frame draws the crop at the view's own scale, placed by
                // the viewport offset (sheet = offset + view / scale): the frame is the crop rectangle and the
                // content is clipped to it, nothing is reduced to fit. The crop applies only where the collection
                // used the view's own axes (the basis matches the decoded view plane) and the view is not a
                // windowed detail, which keeps its own enclosure. Views without a crop keep the whole-model
                // frame below.
                if (elevation != null && detailWindow == null && view?.ViewCrop is { Active: true } crop
                    && ViewBasisMatches(viewElement, crop)
                    && TryCropFrame(centerX, centerY, scale, crop, elevation.MinX, elevation.MinY,
                        out frameX, out frameY, out frameWidth, out frameHeight, out double cropOriginX, out double cropOriginY))
                {
                    effectiveScale = scale;
                    page2.BeginClip(frameX, frameY, Math.Max(0, frameWidth), Math.Max(0, frameHeight));
                    ElevationPrinter.DrawContent(page2, elevation, scale, cropOriginX, cropOriginY,
                        direction == "section" ? 0.82 : 1);
                    page2.EndClip();
                    placedRects.Add((frameX, frameY, frameWidth, frameHeight));
                }
                else if (elevation != null)
                {
                    double drawScale = scale;
                    frameWidth = (elevation.MaxX - elevation.MinX) / drawScale
                        + ElevationPrinter.LabelMarginMillimetres;
                    frameHeight = (elevation.MaxY - elevation.MinY) / drawScale;
                    // A view whose drawing would not fit its place on the sheet (a stair detail at 1:20 is taller than
                    // the page) is drawn at a scale that fits, and one the file places off the page is put in the free
                    // space of the sheet.
                    double drawableWidth = Math.Max(60, stripLeft - 20);
                    double drawableHeight = Math.Max(60, layout.HeightMillimetres - 30);
                    // A stair detail (1:20) takes the right part of the sheet, not the whole drawing area; any other
                    // elevation or section keeps to the third of the page its place holds, so two of them never merge.
                    if (stairSection && scale < 50)
                    {
                        drawableWidth = Math.Min(drawableWidth, stripLeft * 0.45);
                        drawableHeight = Math.Min(drawableHeight, layout.HeightMillimetres * 0.6);
                    }
                    else
                    {
                        drawableHeight = Math.Min(drawableHeight, layout.HeightMillimetres * 0.30);
                    }
                    if (frameWidth > drawableWidth || frameHeight > drawableHeight)
                    {
                        double factor = Math.Max(frameWidth / drawableWidth, frameHeight / drawableHeight);
                        frameWidth /= factor;
                        frameHeight /= factor;
                        drawScale *= factor;
                    }
                    effectiveScale = (int)Math.Round(drawScale);
                    if (stairSection && scale < 50)
                    {
                        centerX = stripLeftForStair - 20 - frameWidth / 2;
                        centerY = layout.HeightMillimetres - 40 - frameHeight / 2;
                    }
                    else if (centerX < 10 || centerX > stripLeft - 10 || centerY < 10
                        || centerY > layout.HeightMillimetres - 15)
                    {
                        centerX = 40 + frameWidth / 2 + relocated * Math.Min(300, drawableWidth / 2);
                        if (centerX + frameWidth / 2 > stripLeft - 10) centerX = stripLeft - 10 - frameWidth / 2;
                        centerY = layout.HeightMillimetres - 40 - frameHeight / 2;
                        relocated++;
                    }
                    // The drawing stays inside the page frame and clear of the title block strip: a view the file places
                    // against an edge is moved in.
                    frameX = centerX - frameWidth / 2;
                    frameY = centerY - frameHeight / 2;
                    ClampFrame(ref frameX, ref frameY, frameWidth, frameHeight);
                    centerX = frameX + frameWidth / 2;
                    centerY = frameY + frameHeight / 2;
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
                        centerY = layout.HeightMillimetres - 40 - frameHeight / 2;
                        if (centerX - frameWidth / 2 < 15) break;
                        frameX = centerX - frameWidth / 2;
                        frameY = centerY - frameHeight / 2;
                    }
                    ClipFrame(frameX, frameY, frameWidth, frameHeight);
                    ElevationPrinter.DrawContent(page2, elevation, drawScale, frameX, frameY,
                        direction == "section" ? 0.82 : 1);
                    page2.EndClip();
                    placedRects.Add((frameX, frameY, frameWidth, frameHeight));
                }
            }
            if (placed && frameWidth > 0)
            {
                effectiveScales[effectiveScale] = effectiveScales.TryGetValue(effectiveScale, out int counted)
                    ? counted + 1 : 1;
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
                page2.DrawText($"1:{effectiveScale}", textX, labelY - 3.5, 2.5, PdfTextAlignment.Left, 0.45);
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

        // The title block strip goes last, over the drawings: the scale row names the scale the views were printed at
        // (a view the layout reduced no longer shows its nominal scale), and no drawing line runs under the strip.
        if (options.DrawPageFrame && pdf.Font != null)
        {
            string scaleText = effectiveScales.Count > 0
                ? "1:" + effectiveScales.OrderByDescending(pair => pair.Value).First().Key
                : "";
            if (scaleText.Length == 0)
            {
                var nominal = new Dictionary<int, int>();
                foreach (var element in document.Elements)
                {
                    if (element.OwnerViewId != sheet.Id) continue;
                    if (element.CategoryId != (int)BuiltInCategory.OST_Viewports) continue;
                    if (element.ViewportViewId is not int viewId) continue;
                    int scale = document.GetElement(viewId)?.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value
                        is int value && value > 0 ? value : 0;
                    if (scale == 0) continue;
                    nominal[scale] = nominal.TryGetValue(scale, out int count) ? count + 1 : 1;
                }
                if (nominal.Count > 0) scaleText = "1:" + nominal.OrderByDescending(pair => pair.Value).First().Key;
            }
            TitleBlockPrinter.Draw(page2, document, sheet, Math.Min(180, layout.WidthMillimetres * 0.22),
                margin, scaleText, TitleLogo(document), Revisions(document));
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

    /// <summary>The revisions of the model, as the revision schedule of the title block lists them: the sequence
    /// number, the description and the date of each.</summary>
    private static List<(string Number, string Description, string Date)> Revisions(RvtDocument document)
    {
        var revisions = new List<(string Number, string Description, string Date)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Revisions) continue;
            // The sequence number is stored as an integer, the description and the date as strings.
            string number = Value(element, BuiltInParameter.PROJECT_REVISION_SEQUENCE_NUM);
            string description = Value(element, BuiltInParameter.PROJECT_REVISION_REVISION_DESCRIPTION);
            string date = Value(element, BuiltInParameter.PROJECT_REVISION_REVISION_DATE);
            if (number.Length == 0 && description.Length == 0 && date.Length == 0) continue;
            revisions.Add((number, description, date));
        }
        return revisions;
    }

    /// <summary>The text of a parameter: a string as it is, an integer as its digits, anything else empty.</summary>
    private static string Value(Element element, BuiltInParameter id) =>
        element.LookupParameter(id)?.Value switch
        {
            string text => text,
            int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "",
        };

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

    /// <summary>Sheet frame and content origin for a viewport with an axis-aligned crop frame: the frame is the
    /// crop rectangle at the view's own scale, anchored by the viewport offset (sheet = offset + view / scale),
    /// and the content origin maps DrawContent's (content - Min) / scale coordinates onto it. The content
    /// coordinates must run along the crop basis (model feet for a plan, projected millimetres for an elevation
    /// collected with the view's own axes): the view origin in content units is its dot product with each basis
    /// row. Returns false unless the right and up rows are axis-aligned; a rotated view keeps the whole-model
    /// frame.</summary>
    internal static bool TryCropFrame(double centerXMm, double centerYMm, double scale, ViewCropFrame crop,
        double contentMinX, double contentMinY,
        out double frameX, out double frameY, out double frameWidth, out double frameHeight,
        out double originX, out double originY)
    {
        frameX = frameY = frameWidth = frameHeight = originX = originY = 0;
        if (!AxisAligned(crop.Right) || !AxisAligned(crop.Up)
            || !(scale > 0) || crop.X1 <= crop.X0 || crop.Y1 <= crop.Y0) return false;
        double k = FeetToMillimetres / scale;
        frameX = centerXMm + crop.X0 * k;
        frameY = centerYMm + crop.Y0 * k;
        frameWidth = (crop.X1 - crop.X0) * k;
        frameHeight = (crop.Y1 - crop.Y0) * k;
        // Sheet(content) must equal offset + (content - origin) / scale along each basis row.
        double originXV = (crop.OriginX * crop.Right[0] + crop.OriginY * crop.Right[1] + crop.OriginZ * crop.Right[2]) * FeetToMillimetres;
        double originYV = (crop.OriginX * crop.Up[0] + crop.OriginY * crop.Up[1] + crop.OriginZ * crop.Up[2]) * FeetToMillimetres;
        originX = centerXMm + (contentMinX - originXV) / scale;
        originY = centerYMm + (contentMinY - originYV) / scale;
        return true;
    }

    /// <summary>True when the view's decoded plane matches the crop frame's basis and origin: the collection
    /// projected the model with these very axes, so the crop rectangle maps onto its content. Guards the crop
    /// path where the collection fell back to compass axes.</summary>
    internal static bool ViewBasisMatches(RvtIO.Model.Element? view, ViewCropFrame crop)
    {
        if (view?.ViewOrigin is not { Length: 3 } origin
            || view.ViewHorizontalDirection is not { Length: 3 } horiz
            || view.ViewVerticalDirection is not { Length: 3 } vert
            || view.ViewDirection is not { Length: 3 } dir) return false;
        static bool Same(double[] a, double[] b) =>
            Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]) < 1e-6;
        return Same(horiz, crop.Right) && Same(vert, crop.Up)
            && Math.Abs(origin[0] - crop.OriginX) + Math.Abs(origin[1] - crop.OriginY) + Math.Abs(origin[2] - crop.OriginZ) < 1e-3;
    }

    /// <summary>True when the vector is a unit axis of the coordinate system, up to a sign.</summary>
    private static bool AxisAligned(double[] v) =>
        v is { Length: 3 } && v.Count(c => Math.Abs(Math.Abs(c) - 1) < 1e-6) == 1
        && v.All(c => Math.Abs(c) < 1e-6 || Math.Abs(Math.Abs(c) - 1) < 1e-6);
}
