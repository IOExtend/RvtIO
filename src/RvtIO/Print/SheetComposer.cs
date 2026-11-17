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
        // The title block family's linework, when it decoded: the GLine objects of the sheet's title block
        // symbol, in symbol feet mapped like the sheet text (feet to millimetres minus the type origin).
        // Otherwise the page border and a title block strip on the right, built from the sheet's own fields,
        // stand in for it, so a printed sheet reads as a framed page of a set.
        IReadOnlyList<(double X0, double Y0, double X1, double Y1, int StyleId)>? titleLines = null;
        int titleSymbolId = 0;
        foreach (var element in document.Elements)
        {
            if (element.OwnerViewId != sheet.Id || element.CategoryId != (int)BuiltInCategory.OST_TitleBlocks) continue;
            if (element.TypeId > 0 && document.RawData.TitleBlockLines?.TryGetValue(element.TypeId, out var decoded) == true
                && decoded.Count > 0)
            {
                titleLines = decoded;
                titleSymbolId = element.TypeId;
                break;
            }
        }
        double margin = 5;
        if (options.DrawPageFrame && titleLines == null)
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
            if (placed && view?.RootClassName == "DBView3d" && !IsAnalysisView(view))
            {
                // A 3D view: an isometric projection of the model, white faces with their outlines, as the massing the
                // view would show without its rendering. A sun or lighting analysis prints as its shaded study, not
                // as vector massing (the reference shows no such linework, and rvt-prt draws 3D only with a camera
                // looking off-axis): those viewports print nothing vector, like an unplaced view.
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
                    ElevationPrinter.DrawContent(page2, model, drawScale, frameX, frameY, 1, document);
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
                    RvtIO.Model.ViewCropFrame? planCrop = view?.ViewCrop is { Active: true } crop ? crop : null;
                    bool cropped = planCrop != null
                        && TryCropFrame(centerX, centerY, viewScale, planCrop, content.MinX, content.MinY,
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
                        ElevationPrinter.SectionMarks(document, viewId, viewScale), document,
                        (frameX, frameY, frameX + frameWidth, frameY + frameHeight),
                        clipRect: cropped && planCrop != null ? CropContentRect(planCrop) : null);
                    page2.EndClip();
                    effectiveScale = (int)Math.Round(drawScale);
                    if (options.DrawRoomLegend && pdf.Font != null && content.Rooms.Count > 0)
                        DrawRoomLegend(page2, content.Rooms.Select(room => room.Name), Math.Max(frameX, 8),
                            frameY - 16);
                }
            }
            else if (placed && name.Contains("site", StringComparison.OrdinalIgnoreCase))
            {
                // A site view: the planting tags over the paper, not the plan of a level. The reference
                // site plan draws no building, terrain or planting graphics, so no level is collected for it.
                var site = SitePrinter.Collect(document);
                if (site != null)
                {
                    double drawScale = scale;
                    // A site viewport whose view decoded a crop box draws the crop at the view's own scale, placed
                    // by the viewport offset, the way a plan viewport does: the frame is the crop rectangle and the
                    // content is clipped to it. Views without a crop keep the whole-model frame below.
                    double siteOriginX = 0, siteOriginY = 0;
                    bool siteCropped = view?.ViewCrop is { Active: true } siteCrop
                        && TryCropFrame(centerX, centerY, scale, siteCrop, site.MinX, site.MinY,
                            out frameX, out frameY, out frameWidth, out frameHeight,
                            out siteOriginX, out siteOriginY);
                    double siteDrawOriginX = frameX, siteDrawOriginY = frameY;
                    if (siteCropped)
                    {
                        drawScale = scale;
                        siteDrawOriginX = siteOriginX;
                        siteDrawOriginY = siteOriginY;
                        page2.BeginClip(frameX, frameY, Math.Max(0, frameWidth), Math.Max(0, frameHeight));
                    }
                    else
                    {
                        frameWidth = (site.MaxX - site.MinX) / drawScale;
                        frameHeight = (site.MaxY - site.MinY) / drawScale;
                        FitFrame(ref frameWidth, ref frameHeight, ref drawScale);
                        frameX = centerX - frameWidth / 2;
                        frameY = centerY - frameHeight / 2;
                        ClampFrame(ref frameX, ref frameY, frameWidth, frameHeight);
                        ClipFrame(frameX, frameY, frameWidth, frameHeight);
                    }
                    SitePrinter.DrawContent(page2, site, drawScale, siteDrawOriginX, siteDrawOriginY);
                    effectiveScale = (int)Math.Round(drawScale);
                    page2.EndClip();
                    // The planting schedule sits under the view title, clear of the site plan, unless its real
                    // instance drew at its own origin below.
                    if (pdf.Font != null && !SchedulesOnSheet(document, sheet).Any(schedule => schedule.CategoryId == PlantingCategoryId))
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
                // A detail section (1:10 to 1:50) is drawn from its own file crop frame: the crop rectangle is
                // the detail's true extent (a roof detail's crop sits at roof height), while the old
                // plane-origin window showed the wrong slice (the plane origin sits floors below the crop).
                // Without a usable crop the invented window stays as a fallback. A stair section is printed
                // whole like a building section, not as a detail; a stair detail (1:20) shows the stair in
                // its enclosure through the file's own crop frame, the way the reference places it next to
                // the stair section.
                bool stairSection = name.Contains("stair", StringComparison.OrdinalIgnoreCase);
                bool stairsOnly = stairSection && scale >= 50;
                double stripLeftForStair = layout.WidthMillimetres - 5
                    - Math.Min(180, layout.WidthMillimetres * 0.22) - 3;
                bool detailSection = direction == "section" && scale < 100 && !stairSection
                    && viewElement?.ViewDirection is { Length: 3 };
                double stripLeft = layout.WidthMillimetres - 5
                    - Math.Min(180, layout.WidthMillimetres * 0.22) - 3;
                // A detail without a usable file crop falls back to a window about three by two metres
                // around the point of its plane, the size a wall or roof detail covers.
                bool detailCropUsable = detailSection && viewElement?.ViewCrop is { Active: true } detailCrop
                    && ViewBasisMatches(viewElement, detailCrop);
                (double Width, double Height)? detailWindow = detailSection && !detailCropUsable ? (3000, 2200) : null;
                // A fallback-windowed detail the file puts against the border or outside the page is still
                // printed: Revit lays such callouts out in a bottom row above their own titles (A104's four
                // details), so the frame is anchored on the decoded title label instead of the file offset.
                // A crop-drawn detail relocates the same way in the crop branch below.
                bool detailRelocated = detailWindow != null
                    && !(centerX > 40 && centerX < stripLeft - 40 && centerY > 40 && centerY < layout.HeightMillimetres - 40)
                    && element.ViewportLabelOrigin is { Length: >= 2 }
                    && element.ViewportLabelLength is double labelLen && labelLen > 0;
                var elevation =
                    ElevationPrinter.Collect(document, direction, stairsOnly, viewElement, detailWindow,
                        isDetail: detailSection);
                // A viewport whose view decoded a crop frame draws the crop at the view's own scale, placed by
                // the viewport offset (sheet = offset + view / scale): the frame is the crop rectangle and the
                // content is clipped to it, nothing is reduced to fit. The crop applies only where the collection
                // used the view's own axes (the basis matches the decoded view plane). A windowed wall or roof
                // detail keeps its own enclosure and its fitted place; a windowed stair detail takes the file's
                // crop frame instead, the way the reference places it next to the stair section. Views without
                // a crop keep the whole-model frame below.
                if (elevation != null && (detailWindow == null || (stairSection && scale < 50))
                    && view?.ViewCrop is { Active: true } crop
                    && ViewBasisMatches(viewElement, crop)
                    && TryCropFrame(centerX, centerY, scale, crop, elevation.MinX, elevation.MinY,
                        out frameX, out frameY, out frameWidth, out frameHeight, out double cropOriginX, out double cropOriginY))
                {
                    effectiveScale = scale;
                    // A crop-drawn detail the file parked off the page is laid out in a bottom row above its own
                    // title (A104's four details): anchor the frame on the decoded title label instead of the
                    // file offset. The title text tops out ~20 mm above its line on A104, so the frame bottom
                    // clears it there.
                    if (detailSection
                        && !(centerX > 40 && centerX < stripLeft - 40 && centerY > 40 && centerY < layout.HeightMillimetres - 40)
                        && element.ViewportLabelOrigin is { Length: >= 2 } cropLabelOrigin
                        && element.ViewportLabelLength is double cropLabelLength && cropLabelLength > 0)
                    {
                        const double DetailTitleGapMillimetres = 20;
                        double titleCxMm = (cropLabelOrigin[0] * FeetToMillimetres - originX)
                            + cropLabelLength * FeetToMillimetres / 2;
                        double titleYmm = cropLabelOrigin[1] * FeetToMillimetres - originY;
                        double relocateX = titleCxMm;
                        double relocateY = titleYmm + DetailTitleGapMillimetres + frameHeight / 2;
                        if (TryCropFrame(relocateX, relocateY, scale, crop, elevation.MinX, elevation.MinY,
                            out double relocatedX, out double relocatedY, out _, out _,
                            out double relocatedOriginX, out double relocatedOriginY))
                        {
                            centerX = relocateX;
                            centerY = relocateY;
                            frameX = relocatedX;
                            frameY = relocatedY;
                            cropOriginX = relocatedOriginX;
                            cropOriginY = relocatedOriginY;
                        }
                    }
                    // The crop rectangle in content units, so only the crop is drawn: the scorer counts
                    // vectors, not the page clip, and the whole model would count without this.
                    var (cropX0, cropY0, cropX1, cropY1) = CropContentRect(crop);
                    page2.BeginClip(frameX, frameY, Math.Max(0, frameWidth), Math.Max(0, frameHeight));
                    ElevationPrinter.DrawContent(page2, elevation, scale, cropOriginX, cropOriginY,
                        direction == "section" ? 0.82 : 1, document, (cropX0, cropY0, cropX1, cropY1));
                    page2.EndClip();
                    placedRects.Add((frameX, frameY, frameWidth, frameHeight));
                }
                else if (elevation != null)
                {
                    double drawScale = scale;
                    frameWidth = (elevation.MaxX - elevation.MinX) / drawScale
                        + ElevationPrinter.LabelMarginMillimetres;
                    frameHeight = (elevation.MaxY - elevation.MinY) / drawScale;
                    if (detailRelocated)
                    {
                        // A windowed detail the file parked off the page is drawn above its own title line, centered
                        // on it, at nominal scale: the reference lays such callouts out in a bottom row this way.
                        // Measured on A104: the reference content bottom sits ~30 mm above its title line.
                        const double DetailTitleGapMillimetres = 30;
                        double titleCxMm = (element.ViewportLabelOrigin![0] * FeetToMillimetres - originX)
                            + element.ViewportLabelLength!.Value * FeetToMillimetres / 2;
                        double titleYmm = element.ViewportLabelOrigin[1] * FeetToMillimetres - originY;
                        centerX = titleCxMm;
                        centerY = titleYmm + DetailTitleGapMillimetres + frameHeight / 2;
                        frameX = centerX - frameWidth / 2;
                        frameY = centerY - frameHeight / 2;
                        effectiveScale = scale;
                        page2.BeginClip(frameX, frameY, Math.Max(0, frameWidth), Math.Max(0, frameHeight));
                        ElevationPrinter.DrawContent(page2, elevation, drawScale, frameX, frameY,
                            direction == "section" ? 0.82 : 1, document);
                        page2.EndClip();
                        placedRects.Add((frameX, frameY, frameWidth, frameHeight));
                    }
                    else
                    {
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
                        direction == "section" ? 0.82 : 1, document);
                    page2.EndClip();
                    placedRects.Add((frameX, frameY, frameWidth, frameHeight));
                    }
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
                // The view title as Revit draws it: a circled detail number, the view name beside it, the scale below
                // and the extension line under the title. The file stores the line's origin on the sheet and its
                // length per viewport (m_labelOrigin, m_labelLength); without them the rule spans the frame as before.
                string detail = element.LookupParameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.Value as string ?? "";
                // A title stays on the page even when its drawing reaches the lower border.
                double labelY = placed && frameHeight > 0 ? Math.Max(frameY - 5, 10) : centerY;
                double textX = frameX;
                double ruleY = labelY + 6, ruleX0 = frameX, ruleX1 = frameX + Math.Min(Math.Max(frameWidth, 40), 120);
                double gridPen = 0.2;
                if (element.ViewportLabelOrigin is { Length: >= 2 } labelOrigin
                    && element.ViewportLabelLength is double labelLength && labelLength > 0)
                {
                    // File-faithful title: the circle's left edge at the label origin, the extension line from there
                    // for the stored length (rvt-prt render/sheet.py), the name above the line and the scale below.
                    const double CircleRadiusMm = 0.125 * 25.4;
                    double lx = labelOrigin[0] * FeetToMillimetres - originX;
                    double ly = labelOrigin[1] * FeetToMillimetres - originY;
                    labelY = ly;
                    textX = lx + 0.079 * 25.4;
                    ruleX0 = lx;
                    ruleX1 = lx + labelLength * FeetToMillimetres;
                    ruleY = ly;
                    if (PenWidths.TryAnnotationWidth(document, 1, out double annotationPen))
                        gridPen = annotationPen;
                    if (detail.Length > 0)
                    {
                        var circle = new List<(double X, double Y)>(20);
                        for (int i = 0; i < 20; i++)
                        {
                            double angle = i * Math.PI * 2 / 20;
                            circle.Add((lx - CircleRadiusMm + CircleRadiusMm * Math.Cos(angle),
                                ly + CircleRadiusMm * Math.Sin(angle)));
                        }
                        page2.DrawPolyline(circle, 0.15, true, 1);
                        page2.DrawText(detail, lx - CircleRadiusMm, ly + 0.2, 2.4, PdfTextAlignment.Center);
                    }
                }
                else if (detail.Length > 0)
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
                page2.DrawLine(ruleX0, ruleY, ruleX1, ruleY, gridPen);
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

        // The title block goes last, over the drawings. Decoded linework draws at its sheet position with its
        // annotation pens (0.7 pt where the style has no pen, as rvt-prt draws it), with its fixed texts and
        // labels; otherwise the strip built from the sheet's own fields stands in. The scale row of the strip
        // names the scale the views were printed at (a view the layout reduced no longer shows its nominal scale),
        // and no drawing line runs under the strip.
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
            if (titleLines != null)
            {
                IReadOnlyList<(double Ox, double Oy, double Height, double Width, int Flags, double Bx, double By, byte[] Rtf)>? titleTexts = null;
                IReadOnlyList<(double Ox, double Oy, double Height, double Width, int Flags, double Bx, double By, int ParamId, string Prefix, string Suffix)>? titleLabels = null;
                document.RawData.TitleBlockTexts?.TryGetValue(titleSymbolId, out titleTexts);
                document.RawData.TitleBlockLabels?.TryGetValue(titleSymbolId, out titleLabels);
                DrawTitleBlockSymbol(page2, document, sheet, scaleText, titleLines,
                    titleTexts, titleLabels, originX, originY);
            }
            else
            {
                TitleBlockPrinter.Draw(page2, document, sheet, Math.Min(180, layout.WidthMillimetres * 0.22),
                    margin, scaleText, TitleLogo(document),
                    Revisions(document).Select(rev => (rev.Number, rev.Description, rev.Date)).ToList());
            }
            // The schedule tables go over the drawings with the title block: each per-sheet ScheduleInstance at
            // its instance origin, revision schedules with the model Revisions() rows and other schedules with
            // their stored rows, the header and grid always drawn (rvt-prt tables.py).
            DrawSchedules(page2, document, sheet, originX, originY);
        }
    }

    private const int RevisionsCategoryId = -2006070;
    private const int PlantingCategoryId = -2001360;
    private const int ScheduleGridCategoryId = -2000042;

    /// <summary>A schedule instance placed on the sheet with its decoded table: the instance origin in sheet feet
    /// and the visible columns, show flags, title-area rows and body row count of its DBViewSchedule.</summary>
    internal sealed record ScheduleOnSheet(int InstanceId, int ScheduleId, double Ox, double Oy,
        List<(string Heading, double WidthFt)> Columns, int CategoryId,
        bool ShowTitle, bool ShowHeaders, bool ShowGridLines, bool ShowOutline, string Title,
        double TitleHeightFt, List<(double HeightFt, string Text)> TitleBands, List<Parsing.TfBlockDeserializer.ScheduleBodySection> BodySections,
        int GridStyleId, int OutlineStyleId, double FixedHeightFt);

    /// <summary>The schedule instances placed on the sheet whose placement and table both decoded: the revision
    /// schedules of the title block, the planting schedule and the How-do-I table.</summary>
    internal static List<ScheduleOnSheet> SchedulesOnSheet(RvtDocument document, Element sheet)
    {
        var result = new List<ScheduleOnSheet>();
        var placements = document.RawData.ScheduleInstances;
        var tables = document.RawData.ScheduleTables;
        if (placements == null || tables == null) return result;
        foreach (var element in document.Elements)
        {
            if (element.OwnerViewId != sheet.Id || element.CategoryId != (int)BuiltInCategory.OST_ScheduleGraphics) continue;
            if (!placements.TryGetValue(element.Id, out var placement)) continue;
            if (!tables.TryGetValue(placement.ScheduleId, out var table)) continue;
            string title = document.GetElement(placement.ScheduleId)?.Name ?? "";
            result.Add(new(element.Id, placement.ScheduleId, placement.Ox, placement.Oy,
                table.Columns, table.CategoryId, table.ShowTitle, table.ShowHeaders,
                table.ShowGridLines, table.ShowOutline, title,
                table.TitleHeightFt, table.TitleBands, table.BodySections,
                table.GridStyleId, table.OutlineStyleId, table.FixedHeightFt));
        }
        return result;
    }

    /// <summary>Draws the schedule instances placed on the sheet at their instance origins: the title (unless the
    /// schedule hides it or names it "&lt;...&gt;"), the header and the grid always, revision schedules with the
    /// model Revisions() rows and the planting schedule with the model planting rows.</summary>
    private static void DrawSchedules(PdfPage page, RvtDocument document, Element sheet, double originX, double originY)
    {
        foreach (var schedule in SchedulesOnSheet(document, sheet))
            DrawSchedule(page, document, sheet, schedule, originX, originY);
    }

    private static void DrawSchedule(PdfPage page, RvtDocument document, Element sheet, ScheduleOnSheet schedule,
        double originX, double originY)
    {
        const double FeetToMillimetres = 304.8;
        const double PtToMm = 25.4 / 72.0;
        const double InchToMm = 25.4;
        bool revision = schedule.CategoryId == RevisionsCategoryId;
        // Measured styles (rvt-prt tables.py SCHEDULE_STYLES): the file decodes no text sizes.
        // The revision body pitch is measured on the sample's reference (16.30 pt rows of 12 pt Arial);
        // rvt-prt's 0.2559 in overshoots here and would drift the grid out of tolerance within a few rows.
        double titleH = revision ? 0 : 0.37 * InchToMm;
        double headH = revision ? 0.3838 * InchToMm : 0.27 * InchToMm;
        double bodyH = revision ? 16.30 * PtToMm : 0.2395 * InchToMm;
        double titlePt = 19, headPt = revision ? 19 : 12.5, bodyPt = 12.5;
        double insetMm = 0.037 * InchToMm;
        // The stored title height wins where the file decodes it (the How-do-I title area: stored 20.2 pt
        // against measured 26.6; the planting title: stored 30 pt against measured 26.6).
        if (!revision && schedule.TitleHeightFt > 0)
            titleH = schedule.TitleHeightFt * FeetToMillimetres;

        double x0 = schedule.Ox * FeetToMillimetres - originX;
        double yTop = schedule.Oy * FeetToMillimetres - originY;
        double[] widths = schedule.Columns.Select(column => column.WidthFt * FeetToMillimetres).ToArray();
        if (widths.Length == 0 || widths.Any(width => !(width > 0) || width > 1000)) return;
        double[] edges = new double[widths.Length + 1];
        edges[0] = x0;
        for (int i = 0; i < widths.Length; i++) edges[i + 1] = edges[i] + widths[i];

        List<List<string>> rows = new();
        if (revision)
        {
            // The revisions explicitly present on this sheet (rvt-prt sheet_revs): the reference lists none,
            // so the table is its header only.
            var present = document.RawData.ScheduleSheetRevisions?.TryGetValue(sheet.Id, out var ids) == true
                ? new HashSet<int>(ids)
                : null;
            foreach (var (number, description, date, id) in Revisions(document))
            {
                if (present != null && !present.Contains(id)) continue;
                rows.Add([number, description, date]);
            }
        }
        else if (schedule.CategoryId == PlantingCategoryId)
        {
            // The stored body section opens with leading empty grid rows (one for the planting schedule)
            // before the evaluated rows; the model rows carry the type marks, names and counts.
            int leadingEmpty = schedule.BodySections.Count > 0 ? schedule.BodySections[0].LeadingEmpty : 0;
            for (int i = 0; i < leadingEmpty && rows.Count < 10000; i++)
                rows.Add(Enumerable.Repeat("", widths.Length).ToList());
            foreach (var (mark, name, count) in PlantingRows(document))
                rows.Add([mark, name, count]);
        }
        else
        {
            // Other schedules draw their stored body sections below (one header per section, then the
            // stored body row count as an empty grid: the body cells carry no evaluated text).
        }
        foreach (var row in rows)
            while (row.Count < widths.Length) row.Add("");

        // The grid and outline pens: the annotation pen of the schedule's own grid and outline line styles
        // (rvt-prt tables.py _style_width), falling back to the schedule grid style and the measured widths.
        double AnnotationWidth(int categoryId, double fallback)
        {
            int? pen = document.RawData.GraphicsStylePens?.Values
                .Where(style => style.CategoryId == categoryId && style.OwnerId == -1 && style.GStyleType == 1)
                .Select(style => (int?)style.PenNumber)
                .FirstOrDefault();
            return pen is int number && PenWidths.TryAnnotationWidth(document, number, out double width)
                ? width : fallback;
        }
        int gridStyle = schedule.GridStyleId != 0 ? schedule.GridStyleId : ScheduleGridCategoryId;
        int outlineStyle = schedule.OutlineStyleId != 0 ? schedule.OutlineStyleId : gridStyle;
        double gridMm = AnnotationWidth(gridStyle, 0.24 * PtToMm);
        double outlineMm = AnnotationWidth(outlineStyle, 0.58 * PtToMm);

        double y = yTop;
        double tableTop = yTop;
        // Schedule titles print one ascent below the row top (measured 18.4 pt below at 19 pt on both
        // the How-do-I and planting titles), not centred in the row.
        bool drewTitleArea = false;
        if (schedule.ShowTitle && titlePt > 0 && schedule.Title.Length > 0 && !schedule.Title.StartsWith("<", StringComparison.Ordinal))
        {
            page.DrawText(schedule.Title, (edges[0] + edges[^1]) / 2, y - 0.97 * titlePt * PtToMm,
                titlePt * PtToMm, PdfTextAlignment.Center);
            y -= titleH;
            drewTitleArea = true;
            if (schedule.ShowGridLines)
                page.DrawLine(edges[0], y, edges[^1], y, gridMm);
        }
        // Stored title bands (the How-do-I long-text band): merged rows with no internal grid, the text
        // top-aligned (measured 5.5 pt below the band top at 12.5 pt).
        foreach (var band in schedule.TitleBands)
        {
            double bandH = band.HeightFt * FeetToMillimetres;
            if (!(bandH > 0) || bandH > 1000) continue;
            if (band.Text.Length > 0)
                page.DrawText(band.Text, edges[0] + insetMm, y - 5.5 * PtToMm, bodyPt * PtToMm);
            y -= bandH;
            drewTitleArea = true;
            if (schedule.ShowGridLines)
                page.DrawLine(edges[0], y, edges[^1], y, gridMm);
        }
        // Header texts wrap into their columns ("Type Mark" wraps to two lines in its narrow column):
        // the header grows one body row per wrapped line after the first (measured 31.8 pt for two lines
        // against 17.3 for one), each column's lines centred as a block.
        List<List<string>> WrapHeaders(List<string> header, double sizeMm)
        {
            var wrapped = new List<List<string>>(widths.Length);
            for (int i = 0; i < widths.Length; i++)
            {
                string text = i < header.Count ? header[i] : "";
                var lines = new List<string>();
                if (text.Length > 0)
                {
                    foreach (string word in text.Split(' '))
                    {
                        string candidate = lines.Count == 0 ? word : lines[^1] + " " + word;
                        if (lines.Count > 0 && page.MeasureText(candidate, sizeMm) <= widths[i])
                            lines[^1] = candidate;
                        else
                            lines.Add(word);
                    }
                }
                if (lines.Count == 0) lines.Add("");
                wrapped.Add(lines);
            }
            return wrapped;
        }
        // Header texts come from the stored section header where the file decodes one, else from the
        // schedule fields; other schedules draw one header per stored body section.
        List<(List<string> Header, List<List<string>> Rows)> sections = new();
        if (revision || schedule.CategoryId == PlantingCategoryId)
        {
            sections.Add((schedule.Columns.Select(column => column.Heading).ToList(), rows));
        }
        else
        {
            foreach (var bodySection in schedule.BodySections)
            {
                var header = bodySection.Header.Count == widths.Length
                    ? bodySection.Header.ToList()
                    : schedule.Columns.Select(column => column.Heading).ToList();
                var body = new List<List<string>>();
                for (int i = 0; i < bodySection.BodyRows && body.Count < 10000; i++)
                    body.Add(Enumerable.Repeat("", widths.Length).ToList());
                sections.Add((header, body));
            }
            if (sections.Count == 0)
                sections.Add((schedule.Columns.Select(column => column.Heading).ToList(), new()));
        }
        // The grid starts below the title area. Outer verticals run header-top to table bottom at the
        // outline weight when the schedule shows its outline (else the header box stands alone at the grid
        // weight); inner verticals span each header and body row; title and band rows have none.
        double gridTop = y;
        bool firstHeader = true;
        // A wrapped header line after the first adds the measured extra line pitch (the planting header:
        // 31.8 pt for two lines against 17.3 for one).
        double extraHeaderLineMm = 14.55 * PtToMm;
        foreach (var (header, body) in sections)
        {
            if (schedule.ShowHeaders)
            {
                // The header top rule is the title-area rule above, drawn where nothing stands over it.
                if (firstHeader && !drewTitleArea && schedule.ShowGridLines)
                    page.DrawLine(edges[0], y, edges[^1], y, gridMm);
                var wrapped = revision
                    ? header.Select(text => new List<string> { text }).ToList()
                    : WrapHeaders(header, headPt * PtToMm);
                int headerLines = Math.Max(1, wrapped.Max(lines => lines.Count));
                double headerH = revision ? headH : bodyH + (headerLines - 1) * extraHeaderLineMm;
                for (int i = 0; i < widths.Length; i++)
                {
                    var lines = i < wrapped.Count ? wrapped[i] : new List<string> { "" };
                    for (int j = 0; j < lines.Count; j++)
                    {
                        if (lines[j].Length == 0) continue;
                        page.DrawText(lines[j], (edges[i] + edges[i + 1]) / 2,
                            y - headerH / 2 - 0.35 * headPt * PtToMm + ((lines.Count - 1) / 2.0 - j) * extraHeaderLineMm,
                            headPt * PtToMm, PdfTextAlignment.Center);
                    }
                }
                y -= headerH;
                if (schedule.ShowGridLines)
                    page.DrawLine(edges[0], y, edges[^1], y, gridMm);
                firstHeader = false;
            }
            if (revision && schedule.FixedHeightFt > 0)
            {
                // A fixed-height table shows empty rows to its height (rvt-prt draw_schedule padding): the
                // sample's revision tables list no revisions but draw empty body rows to their 0.434 ft
                // fixed height, 21 rows at the measured 16.30 pt pitch.
                double fixedBottomY = tableTop - schedule.FixedHeightFt * FeetToMillimetres;
                while (body.Count < 10000 && y - (body.Count + 1) * bodyH >= fixedBottomY - 0.01 * PtToMm)
                    body.Add(Enumerable.Repeat("", widths.Length).ToList());
            }
            foreach (var row in body)
            {
                for (int i = 0; i < widths.Length; i++)
                {
                    string cell = i < row.Count ? row[i] : "";
                    if (cell.Length == 0) continue;
                    double sizeMm = bodyPt * PtToMm;
                    if (revision || i == 0)
                        page.DrawText(cell, edges[i] + insetMm, y - bodyH / 2 - 0.35 * sizeMm, sizeMm);
                    else
                        page.DrawText(cell, (edges[i] + edges[i + 1]) / 2, y - bodyH / 2 - 0.35 * sizeMm, sizeMm,
                            PdfTextAlignment.Center);
                }
                y -= bodyH;
                if (schedule.ShowGridLines)
                    page.DrawLine(edges[0], y, edges[^1], y, gridMm);
            }
        }
        if (schedule.ShowGridLines && y < gridTop)
        {
            for (int i = 1; i < edges.Length - 1; i++)
                page.DrawLine(edges[i], gridTop, edges[i], y, gridMm);
        }
        // The outline closes the table at its own weight: the top rule, the outer verticals and the bottom
        // rule. Without an outline the header box and body rules stand as drawn above.
        if (schedule.ShowOutline && y < tableTop)
        {
            page.DrawLine(edges[0], tableTop, edges[^1], tableTop, outlineMm);
            page.DrawLine(edges[0], tableTop, edges[0], y, outlineMm);
            page.DrawLine(edges[^1], tableTop, edges[^1], y, outlineMm);
            page.DrawLine(edges[0], y, edges[^1], y, outlineMm);
        }
    }

    /// <summary>The planting rows of the model, as the planting schedule lists them: the type mark (T1, T2, ... as
    /// the plan tags the kinds), the common name and the count, sorted by name.</summary>
    /// <summary>The planting rows of the model, as the planting schedule lists them: one row per planting type
    /// (its type mark and common name from the type element), counting the placed instances of that type.</summary>
    internal static List<(string Mark, string Name, string Count)> PlantingRows(RvtDocument document)
    {
        var order = new List<int>();
        var counts = new Dictionary<int, int>();
        var infos = new Dictionary<int, (string Mark, string Name)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Planting) continue;
            // Type definitions and unplaced surrogates are not counted: only placed instances resolve
            // through their type id to a type element carrying a mark.
            if (element.RootClassName is "FamilySymbol" or "FamSymSurrogate") continue;
            var type = element.TypeId > 0 ? document.GetElement(element.TypeId) : null;
            if (type == null || type.Id == element.Id) continue;
            var (mark, common) = SitePrinter.PlantingTypeOf(document, element);
            if (mark.Length == 0) continue;
            if (!counts.TryAdd(type.Id, 1)) counts[type.Id]++;
            else order.Add(type.Id);
            infos[type.Id] = (mark, common);
        }
        var rows = new List<(string Mark, string Name, string Count)>();
        foreach (int typeId in order.OrderBy(id => infos[id].Mark, StringComparer.Ordinal))
            rows.Add((infos[typeId].Mark, infos[typeId].Name,
                counts[typeId].ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return rows;
    }

    /// <summary>Draws the decoded symbol of a title block family at its sheet position: symbol feet to page
    /// millimetres minus the type origin, each line in its graphics style's annotation pen (0.7 pt where the
    /// style has no pen, as rvt-prt draws it), each fixed text and label at its anchor.</summary>
    private static void DrawTitleBlockSymbol(PdfPage page, RvtDocument document, Element sheet, string scaleText,
        IReadOnlyList<(double X0, double Y0, double X1, double Y1, int StyleId)> lines,
        IReadOnlyList<(double Ox, double Oy, double Height, double Width, int Flags, double Bx, double By, byte[] Rtf)>? texts,
        IReadOnlyList<(double Ox, double Oy, double Height, double Width, int Flags, double Bx, double By, int ParamId, string Prefix, string Suffix)>? labels,
        double originX, double originY)
    {
        const double FeetToMillimetres = 304.8;
        var pens = document.RawData.GraphicsStylePens;
        foreach (var line in lines)
        {
            double widthMillimetres = 0.7 * 25.4 / 72.0;
            if (pens != null && pens.TryGetValue(line.StyleId, out var style)
                && PenWidths.TryAnnotationWidth(document, style.PenNumber, out double penWidth))
                widthMillimetres = penWidth;
            page.DrawLine(line.X0 * FeetToMillimetres - originX, line.Y0 * FeetToMillimetres - originY,
                line.X1 * FeetToMillimetres - originX, line.Y1 * FeetToMillimetres - originY, widthMillimetres);
        }
        if (texts != null)
        {
            foreach (var text in texts)
            {
                var parsed = Parsing.RtfText.Parse(text.Rtf, 0, text.Rtf.Length);
                if (parsed?.Text is not { Length: > 0 } plain) continue;
                double sizeMm = parsed.Value.FontSizePoints is double points && points > 0
                    ? points * 25.4 / 72.0 : 2.5;
                DrawTitleBlockText(page, plain, text.Ox * FeetToMillimetres - originX,
                    text.Oy * FeetToMillimetres - originY, sizeMm, text.Flags, text.Bx, text.By);
            }
        }
        if (labels != null)
        {
            foreach (var label in labels)
            {
                string value = TitleBlockLabelValue(document, sheet, scaleText, label.ParamId);
                if (value.Length == 0) continue;
                value = label.Prefix + value + label.Suffix;
                // A label with no decoded text type prints at its box height, as rvt-prt sizes it.
                double sizeMm = label.Height * 304.8 / 1.257;
                DrawTitleBlockText(page, value, label.Ox * FeetToMillimetres - originX,
                    label.Oy * FeetToMillimetres - originY, sizeMm, label.Flags, label.Bx, label.By);
            }
        }
    }

    private static void DrawTitleBlockText(PdfPage page, string text, double x, double y,
        double sizeMm, int flags, double bx, double by)
    {
        if (string.IsNullOrEmpty(text) || sizeMm <= 0) return;
        var alignment = (flags & 0x80) != 0 ? PdfTextAlignment.Center
            : (flags & 0x100) != 0 ? PdfTextAlignment.Right : PdfTextAlignment.Left;
        double angle = Math.Atan2(by, bx) * 180.0 / Math.PI;
        string[] parts = text.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            double lineY = y - i * sizeMm * 1.25;
            if (Math.Abs(angle) < 1e-6) page.DrawText(parts[i], x, lineY, sizeMm, alignment);
            else page.DrawTextRotated(parts[i], x, lineY, sizeMm, angle, alignment);
        }
    }

    /// <summary>The printed value of a title block label: the sheet number and name, the drawn/checked by, the
    /// print time, the view scale, or a project information field, as rvt-prt resolves them.</summary>
    private static string TitleBlockLabelValue(RvtDocument document, Element sheet, string scaleText, int paramId)
    {
        static string Text(Element element, BuiltInParameter id) =>
            element.LookupParameter(id)?.Value as string ?? "";
        var project = document.ProjectInformation;
        return paramId switch
        {
            -1007401 => Text(sheet, BuiltInParameter.SHEET_NUMBER),
            -1007400 => Text(sheet, BuiltInParameter.SHEET_NAME),
            -1007404 => Text(sheet, BuiltInParameter.SHEET_DRAWN_BY),
            -1007405 => Text(sheet, BuiltInParameter.SHEET_CHECKED_BY),
            -1007403 => System.DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
            -1007402 => scaleText,
            -1006316 => project?.Number ?? "",
            -1006317 => project?.Name ?? "",
            -1006319 => project?.Client ?? "",
            -1006321 => project?.IssueDate ?? "",
            _ => "",
        };
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
        var rows = PlantingRows(document);
        if (rows.Count == 0) return;
        // The table of the reference site plan: the type mark of the plant (T1, T2, ... as the plan tags them), its
        // common name and its count.
        page.DrawText("Planting Schedule", x, y, 3.5);
        page.DrawText("Type Mark", x, y - 5, 2.2, PdfTextAlignment.Left, 0.4);
        page.DrawText("Common Name", x + 20, y - 5, 2.2, PdfTextAlignment.Left, 0.4);
        page.DrawText("Count", x + 65, y - 5, 2.2, PdfTextAlignment.Right, 0.4);
        double row = y - 9;
        foreach (var (mark, name, count) in rows)
        {
            page.DrawText(mark, x, row, 2.4);
            page.DrawText(name, x + 20, row, 2.4);
            page.DrawText(count, x + 65, row, 2.4, PdfTextAlignment.Right);
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
    /// number, the description, the date and the element id of each.</summary>
    private static List<(string Number, string Description, string Date, int Id)> Revisions(RvtDocument document)
    {
        var revisions = new List<(string Number, string Description, string Date, int Id)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Revisions) continue;
            // The sequence number is stored as an integer, the description and the date as strings.
            string number = Value(element, BuiltInParameter.PROJECT_REVISION_SEQUENCE_NUM);
            string description = Value(element, BuiltInParameter.PROJECT_REVISION_REVISION_DESCRIPTION);
            string date = Value(element, BuiltInParameter.PROJECT_REVISION_REVISION_DATE);
            if (number.Length == 0 && description.Length == 0 && date.Length == 0) continue;
            revisions.Add((number, description, date, element.Id));
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

    /// <summary>The crop rectangle in content millimetres: the viewer stores origin-relative view feet
    /// (rvt-prt's viewer_frames) while elevation content runs in absolute projected millimetres, so the
    /// view origin's projection on the basis rows offsets it. DrawContent's mapping sends these edges
    /// exactly onto the TryCropFrame frame corners.</summary>
    internal static (double X0, double Y0, double X1, double Y1) CropContentRect(ViewCropFrame crop)
    {
        double originXV = (crop.OriginX * crop.Right[0] + crop.OriginY * crop.Right[1] + crop.OriginZ * crop.Right[2]) * FeetToMillimetres;
        double originYV = (crop.OriginX * crop.Up[0] + crop.OriginY * crop.Up[1] + crop.OriginZ * crop.Up[2]) * FeetToMillimetres;
        return (originXV + Math.Min(crop.X0, crop.X1) * FeetToMillimetres,
            originYV + Math.Min(crop.Y0, crop.Y1) * FeetToMillimetres,
            originXV + Math.Max(crop.X0, crop.X1) * FeetToMillimetres,
            originYV + Math.Max(crop.Y0, crop.Y1) * FeetToMillimetres);
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

    /// <summary>True when the view is a sun, lighting or energy analysis: it prints as its shaded study,
    /// not as vector massing, so a sheet viewport of it draws nothing (the reference shows no such linework).</summary>
    internal static bool IsAnalysisView(Element? view) =>
        view?.Name is string name && (name.Contains("Solar", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Lighting", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Energy Analysis", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Sun Path", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the vector is a unit axis of the coordinate system, up to a sign.</summary>
    private static bool AxisAligned(double[] v) =>
        v is { Length: 3 } && v.Count(c => Math.Abs(Math.Abs(c) - 1) < 1e-6) == 1
        && v.All(c => Math.Abs(c) < 1e-6 || Math.Abs(Math.Abs(c) - 1) < 1e-6);

    /// <summary>The site content kept to the building and its air: topography, contours, plants and entourage
    /// outside the building breadth plus a margin are dropped, the way an elevation drops a far tree, so a far
    /// parking lot does not shrink the building to a corner of the sheet. Returns the site unchanged when the
    /// building has no extent or nothing would remain.</summary>
    internal static SitePrinter.SiteContent KeepSiteNearBuilding(SitePrinter.SiteContent site,
        PlanPrinter.PlanContent building)
    {
        double breadth = building.MaxX - building.MinX, height = building.MaxY - building.MinY;
        if (!(breadth > 0) || !(height > 0)) return site;
        double keepLeft = building.MinX - breadth * 0.25, keepRight = building.MaxX + breadth * 0.25;
        double keepBottom = building.MinY - height * 0.25, keepTop = building.MaxY + height * 0.25;
        static bool Inside(double x, double y, double kl, double kr, double kb, double kt) =>
            x >= kl && x <= kr && y >= kb && y <= kt;
        var topography = site.Topography.Where(polygon => polygon.Any(point => Inside(point.X, point.Y, keepLeft, keepRight, keepBottom, keepTop))).ToList();
        var contours = site.Contours.Where(line => Inside(line.X1, line.Y1, keepLeft, keepRight, keepBottom, keepTop)
            || Inside(line.X2, line.Y2, keepLeft, keepRight, keepBottom, keepTop)).ToList();
        var plants = site.Plants.Where(plant => Inside(plant.X, plant.Y, keepLeft, keepRight, keepBottom, keepTop)).ToList();
        var entourage = site.Entourage.Where(polygon => polygon.Any(point => Inside(point.X, point.Y, keepLeft, keepRight, keepBottom, keepTop))).ToList();
        if (topography.Count == 0 && plants.Count == 0) return site;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(double x, double y)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        foreach (var polygon in topography) foreach (var point in polygon) Grow(point.X, point.Y);
        foreach (var line in contours) { Grow(line.X1, line.Y1); Grow(line.X2, line.Y2); }
        foreach (var plant in plants) { Grow(plant.X - plant.Radius, plant.Y - plant.Radius); Grow(plant.X + plant.Radius, plant.Y + plant.Radius); }
        foreach (var polygon in entourage) foreach (var point in polygon) Grow(point.X, point.Y);
        Grow(building.MinX, building.MinY);
        Grow(building.MaxX, building.MaxY);
        if (!(minX <= maxX) || !(minY <= maxY)) return site;
        return new SitePrinter.SiteContent(topography, contours, plants, entourage, minX, minY, maxX, maxY);
    }
}
