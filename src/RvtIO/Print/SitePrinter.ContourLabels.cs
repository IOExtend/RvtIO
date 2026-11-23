using RvtIO.Compact;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class SitePrinter
{
    private sealed record PrintedContourLabel(double X, double Y, double Level, double Height, double HalfWidth,
        double Baseline, double Angle, double Cosine, double Sine, string Text);

    private static List<PrintedContourLabel> ContourLabels(PdfPage page, RvtDocument? document, int? viewId,
        ViewCropFrame? basis, Func<double, double> toX, Func<double, double> toY)
    {
        var result = new List<PrintedContourLabel>();
        if (viewId is not int view || page.Owner.Font is not { CapHeight: > 0 } font
            || document?.RawData.SerializedFormatSettings is not { } formatting
            || document.RawData.SiteContourLabels?.GetValueOrDefault(view) is not { } labels)
            return result;
        var hidden = document.RawData.ViewHiddenElements?.GetValueOrDefault(view);
        foreach (var label in labels)
        {
            if (hidden?.Contains(label.ElementId) == true || hidden?.Contains(label.SurfaceId) == true) continue;
            double level = label.Position[2];
            string? text = CompactValueFormatter.Format(level, IfcNumericUnitPlan.LengthSpecName, formatting);
            if (string.IsNullOrEmpty(text)) continue;
            var (x, y) = Project(label.Position);
            var (dx, dy) = Project(label.Direction);
            if (dx == 0 && dy == 0) continue;
            if (dx < 0) { dx = -dx; dy = -dy; }
            double angle = Math.Atan2(dy, dx);
            double height = label.TextHeightFeet * FeetToMillimetres;
            double capHeight = height * font.CapHeight / font.UnitsPerEm;
            double halfWidth = (page.MeasureText(text, height) + height) / 2;
            result.Add(new(toX(x * FeetToMillimetres), toY(y * FeetToMillimetres), level * FeetToMillimetres,
                height, halfWidth, capHeight / 2, angle, Math.Cos(angle), Math.Sin(angle), text));
        }
        return result;

        (double X, double Y) Project(double[] point) => basis == null ? (point[0], point[1])
            : (point[0] * basis.Right[0] + point[1] * basis.Right[1], point[0] * basis.Up[0] + point[1] * basis.Up[1]);
    }

    private static IEnumerable<(double Start, double End)> ContourSpans(double x1, double y1, double x2, double y2,
        double level, List<PrintedContourLabel> labels)
    {
        var gaps = new List<(double Start, double End)>();
        foreach (var label in labels)
        {
            if (Math.Abs(level - label.Level) > 1e-6) continue;
            var (ax, ay) = InLabel(x1, y1);
            var (bx, by) = InLabel(x2, y2);
            if (!ElevationPrinter.ClipSegment(ax, ay, bx, by,
                -label.HalfWidth, -label.Height / 2, label.HalfWidth, label.Height / 2,
                out double sx, out double sy, out double ex, out double ey)) continue;
            bool horizontal = Math.Abs(bx - ax) >= Math.Abs(by - ay);
            gaps.Add(horizontal ? ((sx - ax) / (bx - ax), (ex - ax) / (bx - ax))
                : ((sy - ay) / (by - ay), (ey - ay) / (by - ay)));

            (double X, double Y) InLabel(double x, double y)
            {
                x -= label.X;
                y -= label.Y;
                return (x * label.Cosine + y * label.Sine, -x * label.Sine + y * label.Cosine);
            }
        }
        double start = 0;
        foreach (var gap in gaps.OrderBy(gap => gap.Start))
        {
            if (gap.Start > start) yield return (start, gap.Start);
            start = Math.Max(start, gap.End);
        }
        if (start < 1) yield return (start, 1);
    }
}
