using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>Options of sheet printing: the paper (null uses the size the decoded sheet linework spans), the margin
/// added around that linework, and whether the sheet fields are written on the page.</summary>
public sealed class SheetPrintOptions
{
    /// <summary>The paper size; null uses the extent of the sheet's decoded linework, snapped to A4-A0 when it is
    /// within <see cref="PaperToleranceMillimetres"/> of one.</summary>
    public PdfPageSize? PaperSize { get; set; }

    /// <summary>The margin added around the sheet linework, in millimetres.</summary>
    public double MarginMillimetres { get; set; } = 5;

    /// <summary>How far the linework extent may be from a standard paper size and still snap to it.</summary>
    public double PaperToleranceMillimetres { get; set; } = 8;

    /// <summary>True writes the sheet number and name in the upper-left corner; the title block text itself is not
    /// decoded.</summary>
    public bool DrawSheetFields { get; set; } = true;
}

/// <summary>A sheet that can be printed: the element id, its fields and the extent of its decoded linework.</summary>
/// <param name="SheetId">The element id of the sheet.</param>
/// <param name="Number">The sheet number, null when empty.</param>
/// <param name="Name">The sheet name, null when empty.</param>
/// <param name="WidthMillimetres">The width of the decoded linework in millimetres, 0 when nothing is drawn.</param>
/// <param name="HeightMillimetres">The height of the decoded linework in millimetres.</param>
/// <param name="CurveCount">The number of location curves of the sheet's own elements.</param>
/// <param name="TextCount">The number of text notes of the sheet (their text is not decoded).</param>
public sealed record PrintableSheet(int SheetId, string? Number, string? Name, double WidthMillimetres,
    double HeightMillimetres, int CurveCount, int TextCount);

/// <summary>
/// Prints a sheet to a PDF page from the linework the reader decodes: the location curves of the elements the sheet
/// owns (the border, the title block frame and its rules) at their sheet coordinates. Text notes are not decoded, so a
/// title block is printed as its linework with the sheet number and name written in a corner; the placed views are not
/// composed yet (see docs/printing.md).
/// </summary>
public static class SheetPrinter
{
    private const double FeetToMillimetres = 304.8;

    /// <summary>The sheets that have decoded linework, by number and then id.</summary>
    /// <param name="document">The opened document.</param>
    public static IReadOnlyList<PrintableSheet> Sheets(RvtDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var owned = new Dictionary<int, List<Element>>();
        foreach (var element in document.Elements)
        {
            if (element.OwnerViewId is not int owner) continue;
            if (!owned.TryGetValue(owner, out var list)) owned[owner] = list = new List<Element>();
            list.Add(element);
        }
        var sheets = new List<PrintableSheet>();
        foreach (var sheet in document.Elements)
        {
            if (sheet.LookupParameter(BuiltInParameter.SHEET_NAME) == null
                && sheet.LookupParameter(BuiltInParameter.SHEET_NUMBER) == null) continue;
            if (!owned.TryGetValue(sheet.Id, out var elements)) continue;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            int curves = 0, texts = 0;
            foreach (var element in elements)
            {
                if (element.Category == "OST_TextNotes") { texts++; continue; }
                if (element.Location is not { } location) continue;
                curves++;
                Grow(location.StartPoint);
                Grow(location.EndPoint);
                if (location.ArcCenter is { } center && location.ArcRadius is double radius)
                {
                    Grow(center);
                    minX = Math.Min(minX, center[0] * FeetToMillimetres - radius * FeetToMillimetres);
                    maxX = Math.Max(maxX, center[0] * FeetToMillimetres + radius * FeetToMillimetres);
                    minY = Math.Min(minY, center[1] * FeetToMillimetres - radius * FeetToMillimetres);
                    maxY = Math.Max(maxY, center[1] * FeetToMillimetres + radius * FeetToMillimetres);
                }
            }
            // A page-sized border is what a printable sheet has; the reader also returns the small drawing frames of
            // embedded family documents as sheet elements.
            if (curves == 0 || maxX - minX < 50 || maxY - minY < 50) continue;
            sheets.Add(new PrintableSheet(sheet.Id,
                Text(sheet, BuiltInParameter.SHEET_NUMBER), Text(sheet, BuiltInParameter.SHEET_NAME),
                maxX - minX, maxY - minY, curves, texts));
            continue;

            void Grow(double[] point)
            {
                double x = point[0] * FeetToMillimetres, y = point[1] * FeetToMillimetres;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        sheets.Sort((left, right) =>
        {
            int order = string.CompareOrdinal(left.Number, right.Number);
            return order != 0 ? order : left.SheetId.CompareTo(right.SheetId);
        });
        return sheets;
    }

    /// <summary>Prints one sheet to a one-page PDF document.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="sheetId">The element id of the sheet; it must be one of <see cref="Sheets"/>.</param>
    /// <param name="options">The paper and margin; null uses the defaults.</param>
    /// <exception cref="InvalidOperationException">The sheet has no decoded linework.</exception>
    public static PdfDocument Render(RvtDocument document, int sheetId, SheetPrintOptions? options = null) =>
        RenderSheets(document, [sheetId], options);

    /// <summary>Prints several sheets, one page each, to one PDF document.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="sheetIds">The element ids of the sheets; each must be one of <see cref="Sheets"/>.</param>
    /// <param name="options">The paper and margin; null uses the defaults.</param>
    /// <exception cref="InvalidOperationException">A sheet has no decoded linework.</exception>
    public static PdfDocument RenderSheets(RvtDocument document, IReadOnlyList<int> sheetIds,
        SheetPrintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sheetIds);
        options ??= new SheetPrintOptions();
        var byId = new Dictionary<int, Element>();
        foreach (var element in document.Elements) byId.TryAdd(element.Id, element);
        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _) };
        foreach (int sheetId in sheetIds)
        {
            if (!byId.TryGetValue(sheetId, out var sheet)
                || (sheet.LookupParameter(BuiltInParameter.SHEET_NAME) == null
                    && sheet.LookupParameter(BuiltInParameter.SHEET_NUMBER) == null))
                throw new InvalidOperationException($"Element {sheetId} is not a sheet.");
            AddSheetPage(pdf, document, sheet, options);
        }
        if (pdf.Pages.Count == 0) throw new InvalidOperationException("No sheet was given to print.");
        return pdf;
    }

    private static void AddSheetPage(PdfDocument pdf, RvtDocument document, Element sheet, SheetPrintOptions options)
    {
        var curves = new List<(double X1, double Y1, double X2, double Y2)>();
        var arcs = new List<(double X1, double Y1, double X2, double Y2, double Cx, double Cy, double R)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(double x, double y)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        foreach (var element in document.Elements)
        {
            if (element.OwnerViewId != sheet.Id) continue;
            if (element.Location is not { } location) continue;
            double x1 = location.StartPoint[0] * FeetToMillimetres, y1 = location.StartPoint[1] * FeetToMillimetres;
            double x2 = location.EndPoint[0] * FeetToMillimetres, y2 = location.EndPoint[1] * FeetToMillimetres;
            if (location is { CurveType: "Arc", ArcCenter: { } center, ArcRadius: double radius } && radius > 0)
            {
                arcs.Add((x1, y1, x2, y2, center[0] * FeetToMillimetres, center[1] * FeetToMillimetres,
                    radius * FeetToMillimetres));
                Grow(center[0] * FeetToMillimetres - radius * FeetToMillimetres,
                     center[1] * FeetToMillimetres - radius * FeetToMillimetres);
                Grow(center[0] * FeetToMillimetres + radius * FeetToMillimetres,
                     center[1] * FeetToMillimetres + radius * FeetToMillimetres);
            }
            else
            {
                curves.Add((x1, y1, x2, y2));
                Grow(x1, y1); Grow(x2, y2);
            }
        }
        if (curves.Count == 0 && arcs.Count == 0)
            throw new InvalidOperationException($"Sheet {sheet.Id} has no decoded linework to print.");

        double contentWidth = maxX - minX, contentHeight = maxY - minY;
        PdfPageSize paper = options.PaperSize ?? PaperFor(contentWidth, contentHeight, options.PaperToleranceMillimetres);
        double margin = options.MarginMillimetres;
        double offsetX = (paper.WidthMillimetres - contentWidth) / 2 - minX;
        double offsetY = (paper.HeightMillimetres - contentHeight) / 2 - minY;

        PdfPage page = pdf.AddPage(paper);
        foreach (var curve in curves)
            page.DrawLine(curve.X1 + offsetX, curve.Y1 + offsetY, curve.X2 + offsetX, curve.Y2 + offsetY, 0.18);
        foreach (var arc in arcs)
        {
            double start = Math.Atan2(arc.Y1 - arc.Cy, arc.X1 - arc.Cx) * 180 / Math.PI;
            double end = Math.Atan2(arc.Y2 - arc.Cy, arc.X2 - arc.Cx) * 180 / Math.PI;
            if (end <= start) end += 360;
            page.DrawArc(arc.Cx + offsetX, arc.Cy + offsetY, arc.R, start, end, 0.18);
        }

        // The text notes of the sheet, at the positions the file stores (sheet coordinates, feet). The text is real,
        // selectable PDF text when the document has an embedded font.
        bool drewText = false;
        if (pdf.Font != null)
        {
            foreach (var element in document.Elements)
            {
                if (element.OwnerViewId != sheet.Id) continue;
                if (element.Text is not { Length: > 0 } text || element.TextPosition is not { Length: >= 2 } position)
                    continue;
                double x = position[0] * FeetToMillimetres + offsetX;
                double y = position[1] * FeetToMillimetres + offsetY;
                double height = element.TextFontSizePoints is double points && points > 0
                    ? points * 25.4 / 72 : 2.5;
                string[] lines = text.Split('\n');
                for (int line = 0; line < lines.Length; line++)
                    page.DrawText(lines[line], x, y - line * height * 1.25, height);
                drewText = true;
            }
        }

        // The number and name in a corner only when the sheet has no decoded text of its own (the stamp labels are
        // then missing and the corner identifies the page).
        if (options.DrawSheetFields && pdf.Font != null && !drewText)
        {
            string number = Text(sheet, BuiltInParameter.SHEET_NUMBER) ?? string.Empty;
            string name = Text(sheet, BuiltInParameter.SHEET_NAME) ?? string.Empty;
            string title = (number + "  " + name).Trim();
            if (title.Length > 0) page.DrawText(title, margin + 1, paper.HeightMillimetres - 3.5, 2.8);
        }
    }

    /// <summary>The paper that holds the extent: the standard size it matches within the tolerance, else the extent
    /// itself plus a small margin.</summary>
    private static PdfPageSize PaperFor(double width, double height, double tolerance)
    {
        PdfPageSize[] standard = [PdfPageSize.A4, PdfPageSize.A3, PdfPageSize.A2, PdfPageSize.A1, PdfPageSize.A0];
        foreach (var size in standard)
        {
            if ((Math.Abs(size.WidthMillimetres - width) <= tolerance
                    && Math.Abs(size.HeightMillimetres - height) <= tolerance)
                || (Math.Abs(size.WidthMillimetres - height) <= tolerance
                    && Math.Abs(size.HeightMillimetres - width) <= tolerance))
                return size.WidthMillimetres <= size.HeightMillimetres == (width <= height) ? size : size.Landscape;
        }
        return new PdfPageSize(width + 10, height + 10);
    }

    private static string? Text(Element element, BuiltInParameter parameter) =>
        element.LookupParameter(parameter)?.Value is string value && value.Length > 0 ? value : null;
}
