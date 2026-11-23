namespace RvtIO.Print;

internal sealed class ViewportSurfaceEdges
{
    private readonly record struct Segment(double X, double Y, double Dx, double Dy, double LengthSquared, double Length);

    private readonly Segment[] _segments;
    private readonly double _width;
    internal int Color { get; }

    internal ViewportSurfaceEdges(IEnumerable<((double X, double Y) Start, (double X, double Y) End)> lines,
        double width, int color)
    {
        _segments = lines.Select(line =>
        {
            double dx = line.End.X - line.Start.X, dy = line.End.Y - line.Start.Y;
            double squared = dx * dx + dy * dy;
            return new Segment(line.Start.X, line.Start.Y, dx, dy, squared, Math.Sqrt(squared));
        }).Where(edge => edge.LengthSquared > 1e-12 && double.IsFinite(edge.LengthSquared)).ToArray();
        _width = width;
        Color = color == 0xffffff ? 0xfefefe : color;
    }

    internal double Coverage(double x, double y, double stepX, double stepY)
    {
        double maximum = 0;
        double halfWidth = _width / 2;
        double margin = halfWidth + (stepX + stepY) / 2;
        foreach (var edge in _segments)
        {
            double dx = x - edge.X, dy = y - edge.Y;
            if (dx < Math.Min(0, edge.Dx) - margin || dx > Math.Max(0, edge.Dx) + margin
                || dy < Math.Min(0, edge.Dy) - margin || dy > Math.Max(0, edge.Dy) + margin) continue;
            double along = Math.Clamp((dx * edge.Dx + dy * edge.Dy) / edge.LengthSquared, 0, 1);
            double u = dx - along * edge.Dx, v = dy - along * edge.Dy;
            double distance = Math.Sqrt(u * u + v * v);
            double footprint = (Math.Abs(edge.Dy) * stepX + Math.Abs(edge.Dx) * stepY) / edge.Length;
            double lo = Math.Max(distance - footprint / 2, -halfWidth);
            double hi = Math.Min(distance + footprint / 2, halfWidth);
            maximum = Math.Max(maximum, (hi - lo) / footprint);
        }
        return maximum;
    }
}
