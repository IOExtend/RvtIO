using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

internal static class CircularAnnotations
{
    private const double FeetToMillimetres = 304.8;
    private const double PointsToMillimetres = 25.4 / 72;

    internal sealed record Placement(double X, double Y, double Factor, RevitFileInfo.CircularAnnotationStyle Style);

    internal static IReadOnlyList<Placement> Project(RvtDocument? document, int? viewId,
        double[]? right = null, double[]? up = null)
    {
        if (viewId is not int view || document?.RawData.CircularAnnotations?.GetValueOrDefault(view) is not { } source)
            return [];
        var result = new List<Placement>();
        foreach (var (t, style) in source)
            if (ProjectTransform(t, right, up) is { } projected)
                result.Add(new(projected.X, projected.Y, projected.Factor, style));
        return result;
    }

    internal static (double X, double Y, double Factor)? ProjectTransform(double[] t, double[]? right, double[]? up)
    {
        double scale = Dot(t[0], t[3], t[6], right, t[0]);
        double other = Dot(t[1], t[4], t[7], up, t[4]);
        if (!(scale is > 0 and <= 100_000) || Math.Abs(scale - other) > scale * 1e-8
            || Math.Abs(Dot(t[0], t[3], t[6], up, t[3])) > scale * 1e-8
            || Math.Abs(Dot(t[1], t[4], t[7], right, t[1])) > scale * 1e-8) return null;
        return (Dot(t[9], t[10], t[11], right, t[9]) * FeetToMillimetres,
            Dot(t[9], t[10], t[11], up, t[10]) * FeetToMillimetres, scale);

        static double Dot(double x, double y, double z, double[]? axis, double fallback) =>
            axis == null ? fallback : x * axis[0] + y * axis[1] + z * axis[2];
    }

    internal static void DrawViewport(PdfPage page, RvtDocument document, Element? view,
        IReadOnlyList<Placement> items, double scale, double originX, double originY,
        double minX, double minY, ViewCropFrame? crop)
    {
        if (items.Count == 0) return;

        double ToX(double x) => originX + (x - minX) / scale;
        double ToY(double y) => originY + (y - minY) / scale;

        bool clip = crop != null && view?.ViewAnnotationCropActive != false;
        if (clip)
        {
            var (x0, y0, x1, y1) = SheetComposer.CropContentRect(crop!);
            var (left, right, top, bottom) = view?.ViewAnnotationCrop ?? (0, 0, 0, 0);
            double x = ToX(x0) - left * FeetToMillimetres;
            double y = ToY(y0) - bottom * FeetToMillimetres;
            page.BeginClip(x, y, ToX(x1) + right * FeetToMillimetres - x,
                ToY(y1) + top * FeetToMillimetres - y);
        }
        Draw(page, document, items, scale, ToX, ToY);
        if (clip) page.EndClip();
    }

    internal static void Draw(PdfPage page, RvtDocument? document, IReadOnlyList<Placement> items, double scale,
        Func<double, double> toX, Func<double, double> toY)
    {
        if (document == null || items.Count == 0) return;
        var regular = page.Owner.Font;
        if (regular != null && items.Any(item => item.Style.Bold)) page.Owner.BoldFont ??= PdfFonts.TryFindBold();
        foreach (var (originX, originY, factor, style) in items)
        {
            double unit = FeetToMillimetres * factor / scale;
            double x = toX(originX), y = toY(originY);
            double pen = PenWidths.TryAnnotationWidth(document, style.BorderPen, out double width) ? width : 0.1;
            var circle = style.Outline;
            page.DrawFilledCircle(x + circle.X * unit, y + circle.Y * unit, circle.Radius * unit, pen,
                Rgb(style.FillColor), Rgb(style.BorderColor));
            if (regular == null) continue;
            var text = style.Text;
            var font = style.Bold ? page.Owner.BoldFont ?? regular : regular;
            var alignment = (text.Flags & 0x80) != 0 ? PdfTextAlignment.Center
                : (text.Flags & 0x100) != 0 ? PdfTextAlignment.Right : PdfTextAlignment.Left;
            double size = style.FontPoints * PointsToMillimetres * factor / scale;
            double baseline = y + text.Oy * unit - size * font.Ascent / font.UnitsPerEm;
            page.DrawStyledText(style.Value, x + text.Ox * unit, baseline, size, alignment, 0, style.Bold, Rgb(style.TextColor));
        }

        static PdfColor Rgb(int color) => new((color & 255) / 255d, ((color >> 8) & 255) / 255d,
            ((color >> 16) & 255) / 255d);
    }
}
