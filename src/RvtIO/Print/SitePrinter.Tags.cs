using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class SitePrinter
{
    internal sealed record PlantingAnnotation(string Mark, RevitFileInfo.PlanOpeningTagStyle Style,
        (double X, double Y) Head, (double X, double Y) Elbow, (double X, double Y) End);

    internal static IReadOnlyList<PlantingAnnotation> CollectTags(RvtDocument document, int viewId, ViewCropFrame? basis)
    {
        if (document.RawData.SitePlantingTags?.GetValueOrDefault(viewId) is not { } tags
            || document.RawData.PlanOpeningTagStyles is not { } styles) return [];
        var result = new List<PlantingAnnotation>();
        foreach (var tag in tags)
        {
            if (!styles.TryGetValue(tag.SymbolId, out var style) || style.Lines.Count == 0
                || !IsLocalPlantingTag(document.GetElement(tag.ElementId), style.Label.ParamId, out string mark)) continue;
            result.Add(new(mark, style, ToView(tag.X, tag.Y), ToView(tag.Elbow.X, tag.Elbow.Y),
                ToView(tag.End.X, tag.End.Y)));
        }
        return result;

        (double X, double Y) ToView(double x, double y) => basis == null
            ? (x * FeetToMillimetres, y * FeetToMillimetres)
            : ((x * basis.Right[0] + y * basis.Right[1]) * FeetToMillimetres,
                (x * basis.Up[0] + y * basis.Up[1]) * FeetToMillimetres);

        bool IsLocalPlantingTag(Element? plant, int param, out string mark)
        {
            mark = "";
            if (plant == null || param != (int)BuiltInParameter.ALL_MODEL_TYPE_MARK) return false;
            mark = PlantingTypeOf(document, plant).Mark;
            return mark.Length > 0;
        }
    }

    internal static void DrawOutlineAnnotations(PdfPage page, RvtDocument document,
        IReadOnlyList<RevitFileInfo.PlanOutlineAnnotation> markers, ViewCropFrame? basis, SiteContent site,
        double scale, double originX, double originY)
    {
        foreach (var (transform, lines, arcs) in markers)
        {
            if (CircularAnnotations.ProjectTransform(transform, basis?.Right, basis?.Up) is not { } marker) continue;
            double x = originX + (marker.X - site.MinX) / scale;
            double y = originY + (marker.Y - site.MinY) / scale;
            double unit = FeetToMillimetres * marker.Factor / scale;
            foreach (var line in lines)
                page.DrawLine(x + line.X0 * unit, y + line.Y0 * unit, x + line.X1 * unit, y + line.Y1 * unit,
                    Width(line.StyleId));
            foreach (var arc in arcs)
                page.DrawArc(x + arc.X * unit, y + arc.Y * unit, arc.Radius * unit, arc.Start, arc.End,
                    Width(arc.StyleId));
        }

        double Width(int id) => document.RawData.GraphicsStylePens?.GetValueOrDefault(id) is { } style
            && PenWidths.TryAnnotationWidth(document, style.PenNumber, out double width) ? width : 0.1;
    }

    internal static void DrawTags(PdfPage page, RvtDocument document, IReadOnlyList<PlantingAnnotation> tags,
        SiteContent site, double scale, double originX, double originY)
    {
        if (page.Owner.Font == null || tags.Count == 0) return;
        double pen = PlanPrinter.PlanAnnotationWidth(document, (int)BuiltInCategory.OST_PlantingTags, 0.1);
        foreach (var (mark, style, head, elbow, end) in tags)
        {
            var (hx, hy) = PagePoint(head);
            var (ex, ey) = PagePoint(elbow);
            var (tx, ty) = PagePoint(end);
            double edge = ex < hx ? style.Lines.Min(line => Math.Min(line.X0, line.X1))
                : style.Lines.Max(line => Math.Max(line.X0, line.X1));
            double start = hx + edge * FeetToMillimetres;
            page.DrawLine(start, hy, ex, ey, pen);
            page.DrawLine(ex, ey, tx, ty, pen);
            PlanPrinter.DrawTagSymbol(page, style, mark, hx, hy, pen);
        }

        (double X, double Y) PagePoint((double X, double Y) point) =>
            (originX + (point.X - site.MinX) / scale, originY + (point.Y - site.MinY) / scale);
    }
}
