using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class SheetComposer
{
    private static bool DrawViewportTitle(PdfPage page, RvtDocument document, Element viewport, Element? view,
        string viewName, int scale, double originX, double originY)
    {
        if (document.RawData.ViewportTitles?.GetValueOrDefault(viewport.Id) is not { } title) return false;
        if (!title.ShowLabel) return true;
        if (viewport.ViewportLabelOrigin is not { Length: >= 2 } origin
            || page.Owner.Font is not { } font
            || document.RawData.ViewportTitleStyles?.GetValueOrDefault(title.SymbolId) is not { } style)
            return false;

        double x = origin[0] * FeetToMillimetres - originX, y = origin[1] * FeetToMillimetres - originY;
        double pen = PenWidths.TryAnnotationWidth(document, 1, out double width) ? width : 0.1;
        if (title.ShowExtensionLine && viewport.ViewportLabelLength is > 0 and double length)
            page.DrawLine(x, y, x + length * FeetToMillimetres, y, pen);
        foreach (var line in style.Lines)
            page.DrawLine(x + line.X0 * FeetToMillimetres, y + line.Y0 * FeetToMillimetres,
                x + line.X1 * FeetToMillimetres, y + line.Y1 * FeetToMillimetres, pen);
        foreach (var arc in style.Arcs)
            page.DrawArc(x + arc.X * FeetToMillimetres, y + arc.Y * FeetToMillimetres,
                arc.Radius * FeetToMillimetres, arc.Start, arc.End, pen);

        string? sheetTitle = view?.LookupParameter(BuiltInParameter.VIEW_DESCRIPTION)?.Value as string;
        foreach (var (label, fontPoints, wrap) in style.Labels)
        {
            string? value = (BuiltInParameter)label.ParamId switch
            {
                BuiltInParameter.VIEWPORT_VIEW_NAME => string.IsNullOrEmpty(sheetTitle) ? viewName : sheetTitle,
                BuiltInParameter.VIEWPORT_DETAIL_NUMBER =>
                    viewport.LookupParameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.Value as string,
                BuiltInParameter.VIEWPORT_SCALE => title.CustomScale
                    ?? (IsAnalysisView(view) ? null : $" 1 : {scale}"),
                _ => null,
            };
            if (string.IsNullOrEmpty(value)) continue;
            var position = PlanPrinter.AnnotationLabelLayout(font, label, fontPoints, x, y);
            string text = label.Prefix + value + label.Suffix;
            double lineWidth = label.Width * FeetToMillimetres;
            var lines = text.Contains('\n') || (wrap && lineWidth > 0 && page.MeasureText(text, position.Size) > lineWidth)
                ? PlanPrinter.WrapPlanNoteText(page, text, position.Size, lineWidth, wrap && lineWidth > 0)
                : [text];
            double lineHeight = label.Height * FeetToMillimetres;
            double first = position.Baseline;
            if ((label.Flags & 0x400) != 0) first += lineHeight * (lines.Count - 1) / 2;
            for (int i = 0; i < lines.Count; i++)
                page.DrawText(lines[i], position.X, first - i * lineHeight, position.Size, position.Alignment);
        }
        return true;
    }
}
