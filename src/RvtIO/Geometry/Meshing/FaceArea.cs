namespace RvtIO.Geometry.Meshing;

internal static class FaceArea
{
    private const double Tolerance = 1e-6;
    private const int MaxLevels = 20;
    private const int WindowSize = 5;

    /// <summary>One coedge of a loop: the points of its Edge as stored for this Face, the Edge flags, and whether this is the second
    /// Face of the Edge.</summary>
    internal readonly record struct Coedge(IReadOnlyList<(double U, double V)> Points, int Flags, bool SecondFace, uint SourceEdge = 0)
    {
        internal bool Reversed => (Flags & 1) != (SecondFace ? 1 : 0);
    }

    /// <summary>
    /// The area of the Face whose loops are the coedges, in the square of the unit of its (u, v); null when the quadrature does
    /// not converge or a coedge has fewer than two points.
    /// </summary>
    internal static double? Compute(IEnumerable<Coedge> coedges)
        => Integrate(coedges, null) is { } area ? Math.Abs(area) : null;

    internal static double? ComputeWithDistinctSamples(IEnumerable<Coedge> coedges)
        => Compute(coedges.Select(edge =>
        {
            const double tolerance = 1e-9;
            var points = new List<(double U, double V)>(edge.Points.Count);
            foreach (var point in edge.Points)
            {
                if (!double.IsFinite(point.U) || !double.IsFinite(point.V)) return edge;
                if (points.Count == 0 || Math.Abs(point.U - points[^1].U) > tolerance || Math.Abs(point.V - points[^1].V) > tolerance)
                    points.Add(point);
            }
            return edge with { Points = points };
        }));

    internal static double? EllipticalArcLength(double major, double minor, double from, double to)
        => Integrate(new Coedge([(from, 0), (to, 0)], 0, false), (t, _, dt, _) =>
        {
            var (sin, cos) = Math.SinCos(t);
            double x = major * sin, y = minor * cos;
            return Math.Sqrt(x * x + y * y) * Math.Abs(dt);
        });

    internal delegate double SurfaceIntegrand(double u, double v, double du, double dv);

    internal static double? Integrate(IEnumerable<Coedge> coedges, SurfaceIntegrand? integrand)
    {
        double total = 0;
        foreach (var coedge in coedges)
        {
            if (coedge.Points.Count < 2 || Integrate(coedge, integrand) is not { } value) return null;
            total += value;
        }
        return double.IsFinite(total) ? total : null;
    }

    internal static void HermiteBasis(double s, Span<double> value, Span<double> derivative)
    {
        double twoSquare = (s + s) * s, threeSquare = s * 3.0 * s, cube = s * s * s, sixSquare = s * 6.0 * s;
        value[0] = twoSquare * s - threeSquare + 1.0;
        value[1] = threeSquare - twoSquare * s;
        value[2] = cube - twoSquare + s;
        value[3] = cube - s * s;
        derivative[0] = sixSquare - s * 6.0;
        derivative[1] = s * 6.0 - sixSquare;
        derivative[2] = threeSquare - s * 4.0 + 1.0;
        derivative[3] = threeSquare - (s + s);
    }

    private static void Coefficients(IReadOnlyList<(double U, double V)> points, int segment, Span<(double U, double V)> coefficients)
    {
        int interior = points.Count - 2;
        var a = points[segment];
        var b = points[segment + 1];
        (double U, double V) tangentA, tangentB;
        if (interior == 0)
        {
            tangentA = tangentB = (b.U - a.U, b.V - a.V);
        }
        else
        {
            if (segment == interior)
            {
                var before = points[segment - 1];
                tangentB = ((3.0 * b.U - 4.0 * a.U + before.U) * 0.5, (3.0 * b.V - 4.0 * a.V + before.V) * 0.5);
            }
            else
            {
                var after = points[segment + 2];
                tangentB = ((after.U - a.U) * 0.5, (after.V - a.V) * 0.5);
            }
            if (segment != 0)
            {
                var before = points[segment - 1];
                tangentA = ((b.U - before.U) * 0.5, (b.V - before.V) * 0.5);
            }
            else
            {
                var after = points[2];
                tangentA = ((b.U * 4.0 - a.U * 3.0 - after.U) * 0.5, (b.V * 4.0 - a.V * 3.0 - after.V) * 0.5);
            }
        }
        coefficients[0] = a;
        coefficients[1] = b;
        coefficients[2] = tangentA;
        coefficients[3] = tangentB;
    }

    private static ((double U, double V) Point, (double U, double V) Derivative) Evaluate(IReadOnlyList<(double U, double V)> points, double t)
    {
        int count = points.Count;
        if (count == 2)
        {
            var (a, b) = (points[0], points[1]);
            return ((a.U + (b.U - a.U) * t, a.V + (b.V - a.V) * t), (b.U - a.U, b.V - a.V));
        }
        double intervals = count - 1;
        int segment = Math.Min((int)Math.Floor(intervals * t), count - 2);
        Span<double> basis = stackalloc double[4];
        Span<double> derivative = stackalloc double[4];
        Span<(double U, double V)> c = stackalloc (double, double)[4];
        HermiteBasis(intervals * t - segment, basis, derivative);
        Coefficients(points, segment, c);
        double u = basis[0] * c[0].U, v = basis[0] * c[0].V, du = derivative[0] * c[0].U, dv = derivative[0] * c[0].V;
        for (int k = 1; k < 4; k++)
        {
            u = basis[k] * c[k].U + u;
            v = basis[k] * c[k].V + v;
            du = derivative[k] * c[k].U + du;
            dv = derivative[k] * c[k].V + dv;
        }
        return ((u, v), (du * intervals, dv * intervals));
    }

    private static double Integrand(Coedge coedge, double t, SurfaceIntegrand? integrand)
    {
        bool reverse = coedge.Reversed;
        var (point, derivative) = Evaluate(coedge.Points, reverse ? 1.0 - t : t);
        if (reverse) derivative = (-derivative.U, -derivative.V);
        return integrand != null ? integrand(point.U, point.V, derivative.U, derivative.V)
            : 0.5 * (point.U * derivative.V - point.V * derivative.U);
    }

    private static double? Integrate(Coedge coedge, SurfaceIntegrand? integrand)
    {
        Span<double> step = stackalloc double[WindowSize];
        Span<double> estimate = stackalloc double[WindowSize];
        step[0] = 1.0;
        estimate[0] = (Integrand(coedge, 0.0, integrand) + Integrand(coedge, 1.0, integrand)) * 0.5;
        int level = 1;
        for (; level < WindowSize; level++)
        {
            estimate[level] = Refine(coedge, level, estimate[level - 1], integrand);
            step[level] = step[level - 1] * 0.25;
        }
        var (value, error) = Extrapolate(step, estimate);
        if (Math.Abs(value) <= Tolerance) return value;
        while (Math.Abs(error) > Math.Abs(value) * Tolerance)
        {
            if (level >= MaxLevels) return null;
            double nextStep = step[^1] * 0.25;
            double nextEstimate = Refine(coedge, level, estimate[^1], integrand);
            step[1..].CopyTo(step);
            estimate[1..].CopyTo(estimate);
            step[^1] = nextStep;
            estimate[^1] = nextEstimate;
            (value, error) = Extrapolate(step, estimate);
            level++;
            if (Math.Abs(value) <= Tolerance) break;
        }
        return value;
    }

    private static double Refine(Coedge coedge, int level, double previous, SurfaceIntegrand? integrand)
    {
        int panels = 1 << (level - 1);
        double width = 1.0 / panels, t = width * 0.5 + 0.0, sum = 0;
        for (int i = 0; i < panels; i++)
        {
            sum += Integrand(coedge, t, integrand);
            t += width;
        }
        return (sum * 1.0 / panels + previous) * 0.5;
    }

    private static (double Value, double Error) Extrapolate(ReadOnlySpan<double> step, ReadOnlySpan<double> estimate)
    {
        int n = step.Length;
        Span<double> c = stackalloc double[WindowSize];
        Span<double> d = stackalloc double[WindowSize];
        estimate.CopyTo(c);
        estimate.CopyTo(d);
        double value = estimate[n - 1], error = 0;
        for (int m = 1; m < n; m++)
        {
            for (int i = 0; i < n - m; i++)
            {
                double den = (c[i + 1] - d[i]) / (step[i] - step[i + m]);
                d[i] = step[i + m] * den;
                c[i] = step[i] * den;
            }
            error = d[n - m - 1];
            value += error;
        }
        return (value, error);
    }
}
