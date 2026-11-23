using RvtIO.Model;

namespace RvtIO.Print;

internal sealed class ViewportSurfacePattern
{
    private readonly (FillHatch.ModelGrid Model, double Cos, double Sin, double Period)[] _grids;
    private readonly double _width;
    internal int Color { get; }

    private ViewportSurfacePattern(IEnumerable<FillHatch.ModelGrid> grids, double width, int color)
    {
        _grids = grids.Select(grid => (grid, Math.Cos(grid.Angle), Math.Sin(grid.Angle), grid.Segments?.Sum() ?? 0)).ToArray();
        _width = width;
        Color = color == 0xffffff ? 0xfefefe : color;
    }

    internal static ViewportSurfacePattern? Create(RvtDocument document, GeoFace face,
        Func<double[], (double X, double Y)> project, double offsetX, double offsetY, double width)
    {
        IReadOnlyList<FillHatch.ModelGrid>? grids = FillHatch.ProjectModelGrid(document, face, project) is { } solid
            ? [solid] : FillHatch.ProjectDashedModelGrids(document, face, project);
        if (grids == null) return null;
        int color = 0;
        if ((face.RenderStyleId ?? face.NestedRenderStyleId) is int materialId
            && document.RawData.MaterialPatterns?.TryGetValue(materialId, out var material) == true
            && material.SurfacePatternId == face.ModelPattern?.PatternId
            && material.SurfacePatternColor is >= 0 and <= 0xffffff and int savedColor)
            color = savedColor;
        return new(grids.Select(grid => grid with { X = grid.X + offsetX, Y = grid.Y + offsetY }), width, color);
    }

    internal double Coverage(double x, double y, double stepX, double stepY)
    {
        double uncovered = 1;
        foreach (var (grid, cos, sin, period) in _grids)
        {
            double dx = x - grid.X, dy = y - grid.Y;
            double perpendicular = -dx * sin + dy * cos;
            double footprint = Math.Abs(sin) * stepX + Math.Abs(cos) * stepY;
            if (grid.Segments is not { } segments)
            {
                // Integrate the parallel bands over the pixel's projected width, including subpixel grids.
                double width = Math.Min(_width, grid.Spacing);
                double InkLength(double p)
                {
                    double bands = Math.Floor(p / grid.Spacing);
                    return bands * width + Math.Min(p - bands * grid.Spacing, width);
                }
                double edge = perpendicular + width / 2 - footprint / 2;
                uncovered *= 1 - Math.Clamp((InkLength(edge + footprint) - InkLength(edge)) / footprint, 0, 1);
                continue;
            }

            double row = Math.Round(perpendicular / grid.Spacing);
            double distance = perpendicular - row * grid.Spacing;
            double lo = Math.Max(distance - footprint / 2, -_width / 2);
            double hi = Math.Min(distance + footprint / 2, _width / 2);
            if (hi <= lo) continue;
            double phase = dx * cos + dy * sin - row * grid.Shift;
            phase -= Math.Floor(phase / period) * period;
            for (int i = 0; i < segments.Length; i++)
            {
                if (phase < segments[i])
                {
                    if (i % 2 == 0) uncovered *= 1 - (hi - lo) / footprint;
                    break;
                }
                phase -= segments[i];
            }
        }
        return 1 - uncovered;
    }
}
