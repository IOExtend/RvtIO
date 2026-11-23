using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    private static double? PlanCutHeight(RvtDocument document, int viewId)
    {
        if (document.RawData.PlanViewRanges?.GetValueOrDefault(viewId) is not
                ({ Length: > 0 } offsets, { Length: > 0 } levels)
            || document.GetElement(levels[0])?.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value is not double level)
            return null;
        double cut = level + offsets[0];
        return double.IsFinite(cut) ? cut : null;
    }

    private static DoorPlanSymbol? SelectDoorPlanSymbol(
        IReadOnlyList<DoorPlanSymbol> symbols, double[] matrix, double worldCut, double[]? bounds)
    {
        if (matrix.Length != 12 || !matrix.All(double.IsFinite) || !double.IsFinite(worldCut)) return null;
        const double tolerance = 1e-9;
        if (Math.Abs(matrix[6]) > tolerance || Math.Abs(matrix[7]) > tolerance
            || Math.Abs(matrix[8] - 1) > tolerance
            || Math.Abs(matrix[0] * matrix[0] + matrix[3] * matrix[3] - 1) > tolerance
            || Math.Abs(matrix[1] * matrix[1] + matrix[4] * matrix[4] - 1) > tolerance
            || Math.Abs(matrix[0] * matrix[1] + matrix[3] * matrix[4]) > tolerance)
            return null;

        double localCut = worldCut - matrix[11];
        DoorPlanSymbol? found = null;
        foreach (var symbol in symbols)
        {
            bool matches = symbol.CutRange is { } range
                ? localCut >= range.Lo && localCut <= range.Hi
                : bounds is { Length: 6 } && worldCut >= bounds[2] && worldCut <= bounds[5];
            if (!matches) continue;
            if (found != null) return null;
            found = symbol;
        }
        return found;
    }

    private static void DrawDoorPlanSymbol(PdfPage page, DoorPlanSymbol symbol, double[] matrix, double scale,
        Func<double, double> toX, Func<double, double> toY, (double X0, double Y0, double X1, double Y1)? clip)
    {
        (double X, double Y) Direction(double[] vector) =>
            (matrix[0] * vector[0] + matrix[1] * vector[1] + matrix[2] * vector[2],
             matrix[3] * vector[0] + matrix[4] * vector[1] + matrix[5] * vector[2]);
        foreach (var curve in symbol.Curves)
        {
            var c = Direction(curve.C);
            var x = Direction(curve.X);
            double cx = (c.X + matrix[9]) * FeetToMillimetres;
            double cy = (c.Y + matrix[10]) * FeetToMillimetres;
            if (curve.R > 0)
            {
                var y = Direction(curve.Y);
                double orientation = Math.Sign(x.X * y.Y - x.Y * y.X);
                double angle = Math.Atan2(x.Y, x.X);
                page.DrawArc(toX(cx), toY(cy), curve.R * FeetToMillimetres / scale,
                    (angle + orientation * curve.T0) * 180 / Math.PI,
                    (angle + orientation * curve.T1) * 180 / Math.PI, 0.18);
                continue;
            }

            double x0 = cx + curve.T0 * x.X * FeetToMillimetres;
            double y0 = cy + curve.T0 * x.Y * FeetToMillimetres;
            double x1 = cx + curve.T1 * x.X * FeetToMillimetres;
            double y1 = cy + curve.T1 * x.Y * FeetToMillimetres;
            if (clip is { } bounds && !ElevationPrinter.ClipSegment(x0, y0, x1, y1,
                bounds.X0, bounds.Y0, bounds.X1, bounds.Y1, out x0, out y0, out x1, out y1))
                continue;
            page.DrawLine(toX(x0), toY(y0), toX(x1), toY(y1), 0.18);
        }
    }
}
