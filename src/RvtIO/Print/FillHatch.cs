using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>Draws a decoded fill pattern inside a polygon: each line family's dashes, in page millimetres.</summary>
internal static class FillHatch
{
    private const double FeetToMillimetres = 304.8;

    internal sealed record ModelGrid(double X, double Y, double Angle, double Spacing,
        double Shift = 0, double[]? Segments = null);

    // shortcut: a single continuous model grid with an authored face placement.
    internal static ModelGrid? ProjectModelGrid(RvtDocument document, GeoFace face,
        Func<double[], (double X, double Y)> inView)
    {
        if (face.ModelPattern is not { } placement
            || document.RawData.FillPatterns?.TryGetValue(placement.PatternId, out var pattern) != true
            || pattern.Grids is not [{ Target: 1, Segments.Length: 0, Offset: > 0 } grid]) return null;
        return ProjectGrid(placement, grid, inView);
    }

    internal static IReadOnlyList<ModelGrid>? ProjectDashedModelGrids(RvtDocument document, GeoFace face,
        Func<double[], (double X, double Y)> inView)
    {
        if (face.ModelPattern is not { } placement
            || document.RawData.FillPatterns?.TryGetValue(placement.PatternId, out var pattern) != true
            || pattern.Grids.Count == 0
            || !pattern.Grids.All(grid => grid is { Target: 1, Orientation: 0, Segments.Length: > 0 })) return null;
        var grids = new List<ModelGrid>(pattern.Grids.Count);
        foreach (var grid in pattern.Grids)
        {
            if (ProjectGrid(placement, grid, inView) is not { } projected) return null;
            grids.Add(projected);
        }
        return grids;
    }

    private static ModelGrid? ProjectGrid(RvtIO.Internal.ModelFacePattern placement,
        TfBlockDeserializer.PatternGrid grid, Func<double[], (double X, double Y)> inView)
    {
        var (ax, ay) = inView(placement.Origin);
        var (ux, uy) = inView(placement.U);
        var (vx, vy) = inView(placement.V);
        double cos = Math.Cos(grid.Angle), sin = Math.Sin(grid.Angle);
        double dx = cos * ux + sin * vx, dy = cos * uy + sin * vy;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double nx = -sin * ux + cos * vx, ny = -sin * uy + cos * vy;
        double signedSpacing = (dx * ny - dy * nx) / length * grid.Offset;
        double spacing = Math.Abs(signedSpacing);
        double x = ax + grid.OriginX * ux + grid.OriginY * vx;
        double y = ay + grid.OriginX * uy + grid.OriginY * vy;
        if (!double.IsFinite(spacing) || spacing <= 1e-6 || !double.IsFinite(x) || !double.IsFinite(y)) return null;
        double angle = Math.Atan2(dy, dx);
        if (grid.Segments.Length == 0) return new ModelGrid(x, y, angle, spacing);

        double shift = grid.Shift * length + grid.Offset * (dx * nx + dy * ny) / length;
        if (signedSpacing < 0) shift = -shift;
        var segments = grid.Segments.Select(segment => segment * length).ToArray();
        double period = segments.Sum();
        return double.IsFinite(shift) && double.IsFinite(period)
            && period > 0 && segments.All(segment => segment >= 0)
            ? new ModelGrid(x, y, angle, spacing, shift, segments) : null;
    }

    /// <summary>Draws the dashes of <paramref name="grids"/> clipped to <paramref name="polygon"/>. The polygon,
    /// the anchor and the pen width are in page millimetres; the pattern's own lengths are feet, read as drafting
    /// pattern paper lengths (they do not scale with the view).</summary>
    internal static void Draw(PdfPage page, IReadOnlyList<(double X, double Y)> polygon,
        IReadOnlyList<TfBlockDeserializer.PatternGrid> grids, double anchorX, double anchorY, double width)
    {
        foreach (var line in Strokes([polygon], grids, anchorX, anchorY))
            page.DrawLine(line.X1, line.Y1, line.X2, line.Y2, width);
    }

    internal static List<PdfLinePattern>? DraftingTiles(IReadOnlyList<TfBlockDeserializer.PatternGrid> grids,
        double anchorX, double anchorY, double width)
    {
        var tiles = new List<PdfLinePattern>();
        foreach (var grid in grids)
        {
            if (grid.Segments.Length == 0) continue;
            double spacing = Math.Abs(grid.Offset) * FeetToMillimetres;
            double shift = (grid.Offset < 0 ? -grid.Shift : grid.Shift) * FeetToMillimetres;
            double x = anchorX + grid.OriginX * FeetToMillimetres, y = anchorY + grid.OriginY * FeetToMillimetres;
            var segments = grid.Segments.Select(segment => segment * FeetToMillimetres).ToArray();
            double period = segments.Sum();
            if (!double.IsFinite(spacing) || spacing <= width || !double.IsFinite(shift)
                || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(grid.Angle)
                || !double.IsFinite(period) || period <= 0 || segments.Any(segment => segment < 0)) return null;
            tiles.Add(new(spacing, grid.Angle, x, y, width, shift, segments));
        }
        return tiles;
    }

    internal static IEnumerable<(double X1, double Y1, double X2, double Y2)> Strokes(
        IEnumerable<IReadOnlyList<(double X, double Y)>> polygons,
        IReadOnlyList<TfBlockDeserializer.PatternGrid> grids, double anchorX, double anchorY,
        bool continuousLines = false)
    {
        foreach (var grid in grids)
        {
            bool continuous = grid.Segments.Length == 0;
            if (continuous && !continuousLines) continue;
            double dx = Math.Cos(grid.Angle), dy = Math.Sin(grid.Angle);
            double nx = -dy, ny = dx;
            double offset = grid.Offset * FeetToMillimetres;
            double shift = grid.Shift * FeetToMillimetres;
            double period = 0;
            foreach (double segment in grid.Segments) period += segment * FeetToMillimetres;
            if (Math.Abs(offset) < 1e-9 || !continuous && period <= 0) continue;
            var segments = new double[grid.Segments.Length];
            for (int i = 0; i < segments.Length; i++) segments[i] = grid.Segments[i] * FeetToMillimetres;
            double ox = anchorX + grid.OriginX * FeetToMillimetres;
            double oy = anchorY + grid.OriginY * FeetToMillimetres;

            var rows = new SortedDictionary<int, List<(double From, double To)>>();
            foreach (var polygon in polygons)
            {
                if (polygon.Count < 3) continue;
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
                        if (crossings[i + 1] - crossings[i] < 1e-9) continue;
                        if (!rows.TryGetValue(k, out var intervals)) rows[k] = intervals = [];
                        intervals.Add((crossings[i], crossings[i + 1]));
                    }
                }
            }

            foreach (var (k, intervals) in rows)
            {
                double bx = ox + k * (shift * dx + offset * nx);
                double by = oy + k * (shift * dy + offset * ny);
                foreach (var (ta, tb) in UnionIntervals(intervals))
                {
                    if (continuous)
                    {
                        yield return (bx + ta * dx, by + ta * dy, bx + tb * dx, by + tb * dy);
                        continue;
                    }
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
                                    yield return (bx + from * dx, by + from * dy, bx + to * dx, by + to * dy);
                            }
                            cursor += segment;
                        }
                    }
                }
            }
        }
    }

    // Terrain triangles may overlap or share a boundary. Ink each grid's covered intervals once.
    private static IEnumerable<(double From, double To)> UnionIntervals(List<(double From, double To)> intervals)
    {
        intervals.Sort((a, b) => a.From.CompareTo(b.From));
        var (from, to) = intervals[0];
        for (int i = 1; i < intervals.Count; i++)
        {
            var (nextFrom, nextTo) = intervals[i];
            if (nextFrom > to + 1e-9)
            {
                yield return (from, to);
                from = nextFrom;
            }
            to = Math.Max(to, nextTo);
        }
        yield return (from, to);
    }
}
