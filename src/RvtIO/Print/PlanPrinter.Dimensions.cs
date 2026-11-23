using RvtIO.Compact;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    internal sealed record PrintedDimension(RevitFileInfo.PlanDimension Dimension,
        RevitFileInfo.PlanDimensionStyle Style, string[] Labels, double LineWidth, double TickWidth);

    internal static List<PrintedDimension> CollectPlanDimensions(RvtDocument document, int viewId, ViewCropFrame crop)
    {
        var data = document.RawData;
        if (!crop.Right.SequenceEqual(new double[] { 1, 0, 0 }) || !crop.Up.SequenceEqual(new double[] { 0, 1, 0 })
            || data.PlanDimensions?.GetValueOrDefault(viewId) is not { Count: > 0 } dimensions
            || data.PlanDimensionStyles == null || data.SerializedFormatSettings is not { } formatting)
            return [];
        var result = new List<PrintedDimension>(dimensions.Count);
        foreach (var dimension in dimensions)
        {
            var d = dimension.Direction;
            if (Math.Abs(d[2]) > 1e-8 || Math.Abs(d[0] * d[0] + d[1] * d[1] - 1) > 1e-8
                || !data.PlanDimensionStyles.TryGetValue(dimension.StyleId, out var style))
                continue;
            var labels = dimension.Values.Select(value =>
                CompactValueFormatter.Format(value, IfcNumericUnitPlan.LengthSpecName, formatting)).ToArray();
            if (labels.Any(string.IsNullOrEmpty)) continue;
            result.Add(new(dimension, style, labels.Select(label => label!).ToArray(),
                PlanAnnotationWidth(document, style.LineCategory, 0.1), PlanAnnotationWidth(document, style.TickCategory, 0.5)));
        }
        return result;
    }

    internal static double PlanAnnotationWidth(RvtDocument document, int category, double fallback)
    {
        if (document.RawData.GraphicsStylePens?.Values.FirstOrDefault(pen =>
                pen.CategoryId == category && pen.GStyleType == 1) is { PenNumber: > 0 } pen
            && PenWidths.TryAnnotationWidth(document, pen.PenNumber, out double width))
            return width;
        return fallback;
    }

    internal static void DrawPlanDimensions(PdfPage page, List<PrintedDimension> dimensions, PlanContent content,
        double scale, double originX, double originY)
    {
        double modelToPaper = FeetToMillimetres / scale;
        double X(double feet) => feet * modelToPaper - content.MinX / scale + originX;
        double Y(double feet) => feet * modelToPaper - content.MinY / scale + originY;
        foreach (var printed in dimensions)
        {
            var (dimension, style, labels, lineWidth, tickWidth) = printed;
            double dx = dimension.Direction[0], dy = dimension.Direction[1];
            double ox = dimension.Origin[0], oy = dimension.Origin[1];
            var along = dimension.References.Select(point => ((point[0] - ox) * dx + (point[1] - oy) * dy)
                * modelToPaper).ToArray();
            double first = along[0], last = along[^1];
            double extension = style.LineExtensionFeet * FeetToMillimetres;
            Line(Math.Min(first, last) - extension, 0, Math.Max(first, last) + extension, 0, lineWidth);
            double tick = style.TickSizeFeet * FeetToMillimetres / 2;
            for (int i = 0; i < along.Length; i++)
            {
                var reference = dimension.References[i];
                double distance = (-(reference[0] - ox) * dy + (reference[1] - oy) * dx) * modelToPaper;
                double side = Math.Sign(distance);
                double gap = style.WitnessGapFeet * FeetToMillimetres;
                Line(along[i], distance - side * gap, along[i], side * style.WitnessExtensionFeet * FeetToMillimetres, lineWidth);
                Line(along[i] - tick, -tick, along[i] + tick, tick, tickWidth);
            }

            if (page.Owner.Font is not { } font) continue;
            double textHeight = style.TextHeightFeet * FeetToMillimetres
                / (font.CapHeight > 0 ? (double)font.CapHeight / font.UnitsPerEm : 0.7);
            double textGap = style.TextGapFeet * FeetToMillimetres - textHeight * font.Descent / font.UnitsPerEm;
            double rotation = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (rotation < -89.999) { rotation += 180; textGap = -textGap; }
            else if (rotation > 90.001) { rotation -= 180; textGap = -textGap; }
            for (int i = 0; i < labels.Length; i++)
            {
                double middle = (along[i] + along[i + 1]) / 2;
                page.DrawTextRotated(labels[i], X(ox) + middle * dx - textGap * dy,
                    Y(oy) + middle * dy + textGap * dx, textHeight, rotation, PdfTextAlignment.Center);
            }

            void Line(double u0, double v0, double u1, double v1, double width)
            {
                page.DrawLine(X(ox) + u0 * dx - v0 * dy, Y(oy) + u0 * dy + v0 * dx,
                    X(ox) + u1 * dx - v1 * dy, Y(oy) + u1 * dy + v1 * dx, width);
            }
        }
    }
}
