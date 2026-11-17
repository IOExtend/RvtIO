using RvtIO.Parsing;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>Draws a decoded fill pattern inside a polygon: each line family's dashes, in page millimetres.</summary>
internal static class FillHatch
{
    private const double FeetToMillimetres = 304.8;

    /// <summary>Draws the dashes of <paramref name="grids"/> clipped to <paramref name="polygon"/>. The polygon,
    /// the anchor and the pen width are in page millimetres; the pattern's own lengths are feet, read as drafting
    /// pattern paper lengths (they do not scale with the view).</summary>
    internal static void Draw(PdfPage page, IReadOnlyList<(double X, double Y)> polygon,
        IReadOnlyList<TfBlockDeserializer.PatternGrid> grids, double anchorX, double anchorY, double width)
    {
        if (polygon.Count < 3) return;
        foreach (var grid in grids)
        {
            if (grid.Segments.Length == 0) continue;
            double dx = Math.Cos(grid.Angle), dy = Math.Sin(grid.Angle);
            double nx = -dy, ny = dx;
            double offset = grid.Offset * FeetToMillimetres;
            double shift = grid.Shift * FeetToMillimetres;
            double period = 0;
            foreach (double segment in grid.Segments) period += segment * FeetToMillimetres;
            if (Math.Abs(offset) < 1e-9 || period <= 0) continue;
            var segments = new double[grid.Segments.Length];
            for (int i = 0; i < segments.Length; i++) segments[i] = grid.Segments[i] * FeetToMillimetres;
            double ox = anchorX + grid.OriginX * FeetToMillimetres;
            double oy = anchorY + grid.OriginY * FeetToMillimetres;

            double minK = double.MaxValue, maxK = double.MinValue;
            foreach (var point in polygon)
            {
                double k = ((point.X - ox) * nx + (point.Y - oy) * ny) / offset;
                if (k < minK) minK = k;
                if (k > maxK) maxK = k;
            }

            for (int k = (int)Math.Floor(minK); k <= (int)Math.Ceiling(maxK); k++)
            {
                double bx = ox + k * (shift * dx + offset * nx);
                double by = oy + k * (shift * dy + offset * ny);
                var crossings = new List<double>(8);
                for (int i = 0; i < polygon.Count; i++)
                {
                    var a = polygon[i];
                    var b = polygon[(i + 1) % polygon.Count];
                    double ex = b.X - a.X, ey = b.Y - a.Y;
                    double den = dx * ey - dy * ex;
                    if (Math.Abs(den) < 1e-12) continue;
                    double t = ((a.X - bx) * ey - (a.Y - by) * ex) / den;
                    double u = ((bx - a.X) * dy - (by - a.Y) * dx) / -den;
                    if (u >= -1e-9 && u <= 1 + 1e-9) crossings.Add(t);
                }
                crossings.Sort();
                for (int i = 0; i + 1 < crossings.Count; i += 2)
                {
                    double ta = crossings[i], tb = crossings[i + 1];
                    if (tb - ta < 1e-9) continue;
                    for (double start = Math.Floor(ta / period) * period; start < tb; start += period)
                    {
                        double cursor = start;
                        for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
                        {
                            double segment = segments[segmentIndex];
                            // The dash list alternates dash, gap, dash, ...: only even entries ink.
                            if (segmentIndex % 2 == 0 && segment > 0)
                            {
                                double from = Math.Max(cursor, ta), to = Math.Min(cursor + segment, tb);
                                if (to > from + 1e-9)
                                    page.DrawLine(bx + from * dx, by + from * dy, bx + to * dx, by + to * dy, width);
                            }
                            cursor += segment;
                        }
                    }
                }
            }
        }
    }
}
