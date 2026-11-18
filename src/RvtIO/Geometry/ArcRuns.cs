namespace RvtIO.Geometry;

internal static class ArcRuns
{
    private static double[] CrossCurrent(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    private static double DotCurrent(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    /// <summary>One circular edge of a ring: the ring vertices <c>First..First+Steps</c> (cyclic, in ring order) are samples of
    /// <c>Arc</c>, whose stored parameter runs against the ring order when <c>Reversed</c>. <c>EitherWay</c>: the other way round
    /// the ring passed as the arc too, so the run is the shorter of the two, not a known one.</summary>
    internal readonly record struct ArcRunCurrent(int First, int Steps, Internal.Arc Arc, bool Reversed, bool EitherWay = false);

    /// <summary>Finds the circular edges of a ring: the arcs whose two ends are vertices of the ring and whose vertices between
    /// them lie on the circle. An arc that closes on itself or has no such run is left out (the ring keeps its own points).</summary>
    internal static List<ArcRunCurrent> FindArcRunsCurrent(IReadOnlyList<double[]> ring, IReadOnlyList<Internal.Arc> arcs)
    {
        var runs = new List<ArcRunCurrent>();
        int n = ring.Count;
        foreach (var arc in arcs)
        {
            double radius = Math.Abs(arc.Radius), tolerance = 1e-6 * Math.Max(radius, 1e-3);
            double[] normal = CrossCurrent(arc.X, arc.Y);
            bool Near(double[] p, double[] q) => Math.Abs(p[0] - q[0]) <= tolerance && Math.Abs(p[1] - q[1]) <= tolerance
                && Math.Abs(p[2] - q[2]) <= tolerance;
            bool OnCircle(double[] p)
            {
                double[] d = [p[0] - arc.Centre[0], p[1] - arc.Centre[1], p[2] - arc.Centre[2]];
                double axial = DotCurrent(d, normal);
                return Math.Abs(axial) <= tolerance && Math.Abs(Math.Sqrt(DotCurrent(d, d) - axial * axial) - radius) <= tolerance;
            }
            // a vertex between two samples of the circle that lies on the straight segment joining them: the mesh of the neighbouring
            // cylinder added it to the ring
            bool OnChord(double[] p, double[] a, double[] b)
            {
                double[] ab = [b[0] - a[0], b[1] - a[1], b[2] - a[2]], ap = [p[0] - a[0], p[1] - a[1], p[2] - a[2]];
                double length2 = DotCurrent(ab, ab);
                if (!(length2 > 0)) return false;
                double t = DotCurrent(ap, ab) / length2;
                if (t < -1e-9 || t > 1 + 1e-9) return false;
                double[] off = [ap[0] - t * ab[0], ap[1] - t * ab[1], ap[2] - t * ab[2]];
                return Math.Sqrt(DotCurrent(off, off)) <= tolerance;
            }
            bool IsArcRoute(List<double[]> route)
            {
                int previous = 0;
                for (int i = 1; i < route.Count; i++)
                {
                    if (!OnCircle(route[i])) continue;
                    for (int between = previous + 1; between < i; between++)
                        if (!OnChord(route[between], route[previous], route[i])) return false;
                    previous = i;
                }
                return previous == route.Count - 1;
            }
            double[] start = arc.At(arc.First), end = arc.At(arc.Last);
            int s = -1, e = -1;
            for (int i = 0; i < n; i++)
            {
                if (s < 0 && Near(ring[i], start)) s = i;
                if (e < 0 && Near(ring[i], end)) e = i;
            }
            if (s < 0 || e < 0 || s == e) continue;
            // forward from s to e, or backward (the ring runs against the stored parameter): the way that is the arc
            int bestSteps = int.MaxValue, bestDirection = 0, ways = 0;
            for (int direction = 1; direction >= -1; direction -= 2)
            {
                var route = new List<double[]> { ring[s] };
                for (int i = (s + direction + n) % n; ; i = (i + direction + n) % n)
                {
                    route.Add(ring[i]);
                    if (i == e || route.Count > n) break;
                }
                if (route.Count > n || !IsArcRoute(route)) continue;
                ways++;
                if (route.Count - 1 < bestSteps) (bestSteps, bestDirection) = (route.Count - 1, direction);
            }
            if (bestDirection != 0)
                runs.Add(bestDirection == 1 ? new ArcRunCurrent(s, bestSteps, arc, false, ways == 2) : new ArcRunCurrent(e, bestSteps, arc, true, ways == 2));
        }
        return runs;
    }
}
