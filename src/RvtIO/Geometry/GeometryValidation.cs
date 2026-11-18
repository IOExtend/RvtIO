namespace RvtIO.Geometry;

internal static class GeometryValidation
{
    /// <summary>Checks that the six bounds coordinates are finite and each minimum is at or below its maximum.</summary>
    internal static bool BoundsAreValid(double[]? bounds)
    {
        if (bounds is not { Length: >= 6 }) return false;
        for (int i = 0; i < 6; i++)
            if (!double.IsFinite(bounds[i])) return false;
        return bounds[0] <= bounds[3] && bounds[1] <= bounds[4] && bounds[2] <= bounds[5];
    }

    internal static bool AllVerticesStrictlyInside(IReadOnlyList<double[]> inner, IReadOnlyList<double[]> outer)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int index = 0; index < outer.Count; index++)
        {
            var a = outer[index];
            var b = outer[(index + 1) % outer.Count];
            nx += (a[1] - b[1]) * (a[2] + b[2]);
            ny += (a[2] - b[2]) * (a[0] + b[0]);
            nz += (a[0] - b[0]) * (a[1] + b[1]);
        }
        int drop = Math.Abs(nx) >= Math.Abs(ny) && Math.Abs(nx) >= Math.Abs(nz) ? 0 : Math.Abs(ny) >= Math.Abs(nz) ? 1 : 2;
        (double U, double V) Project(double[] point) => drop switch
        {
            0 => (point[1], point[2]),
            1 => (point[0], point[2]),
            _ => (point[0], point[1]),
        };
        var polygon = outer.Select(Project).ToArray();
        const double tolerance = 1e-9;
        foreach (var vertex in inner)
        {
            var (u, v) = Project(vertex);
            bool inside = false;
            for (int index = 0; index < polygon.Length; index++)
            {
                var (u1, v1) = polygon[index];
                var (u2, v2) = polygon[(index + 1) % polygon.Length];
                double dx = u2 - u1, dy = v2 - v1, length = Math.Sqrt(dx * dx + dy * dy);
                if (length > 0)
                {
                    double along = ((u - u1) * dx + (v - v1) * dy) / length;
                    double across = Math.Abs((u - u1) * dy - (v - v1) * dx) / length;
                    if (across <= tolerance && along >= -tolerance && along <= length + tolerance) return false;
                }
                if ((v1 > v) != (v2 > v) && u < (u2 - u1) * (v - v1) / (v2 - v1) + u1) inside = !inside;
            }
            if (!inside) return false;
        }
        return true;
    }
}
