using RvtIO.BuiltIns;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    private static bool DrawPlanOpeningTags(PdfPage page, RvtDocument? document, int? viewId,
        Func<double, double> toX, Func<double, double> toY)
    {
        if (document == null || viewId is not int view || page.Owner.Font == null
            || document.RawData.PlanOpeningTags?.GetValueOrDefault(view) is not { Count: > 0 } tags
            || document.RawData.PlanOpeningTagStyles is not { Count: > 0 } styles) return false;

        bool drawn = false;
        foreach (var tag in tags)
        {
            var element = document.GetElement(tag.ElementId);
            if (element == null || !styles.TryGetValue(tag.SymbolId, out var style)) continue;
            var label = style.Label;
            var owner = label.ParamId == (int)BuiltInParameter.ALL_MODEL_TYPE_MARK
                ? document.GetElement(element.TypeId) : element;
            if (owner?.LookupParameter((BuiltInParameter)label.ParamId)?.Value is not string { Length: > 0 } value)
                continue;
            double x = toX(tag.X * FeetToMillimetres), y = toY(tag.Y * FeetToMillimetres);
            double pen = PlanAnnotationWidth(document,
                element.CategoryId == (int)BuiltInCategory.OST_Windows
                    ? (int)BuiltInCategory.OST_WindowTags : (int)BuiltInCategory.OST_DoorTags, 0.1);
            DrawTagSymbol(page, style, value, x, y, pen);
            drawn = true;
        }
        return drawn;
    }

    internal static void DrawTagSymbol(PdfPage page, Model.RevitFileInfo.PlanOpeningTagStyle style, string value,
        double x, double y, double pen)
    {
        if (page.Owner.Font is not { } font) return;
        var label = style.Label;
        var (labelX, _, baseline, size, alignment) = AnnotationLabelLayout(font, label, style.FontPoints, x, y);
        foreach (var line in style.Lines)
            page.DrawLine(x + line.X0 * FeetToMillimetres, y + line.Y0 * FeetToMillimetres,
                x + line.X1 * FeetToMillimetres, y + line.Y1 * FeetToMillimetres, pen);
        foreach (var arc in style.Arcs)
            page.DrawArc(x + arc.X * FeetToMillimetres, y + arc.Y * FeetToMillimetres,
                arc.Radius * FeetToMillimetres, arc.Start, arc.End, pen);
        page.DrawText(label.Prefix + value + label.Suffix, labelX, baseline, size, alignment);
    }
}
