using static RvtIO.Geometry.ArcRuns;

namespace RvtIO.Ifc;

/// <summary>The profile, position and direction of an IfcExtrudedAreaSolid written for the extrusion of the curve loops of a face.</summary>
public partial class IfcExporter
{
    internal readonly record struct ExtrusionPartsCurrent(int ProfileId, int PositionId, int DirectionId);

    private static double[] CrossCurrent(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    private static double DotCurrent(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static double[] UnitCurrent(double[] a)
    {
        double length = Math.Sqrt(DotCurrent(a, a));
        return [a[0] / length, a[1] / length, a[2] / length];
    }

    internal static List<double[]> CurveLoopCurrent(IReadOnlyList<double[]> ring, double[] direction, bool outer)
    {
        int n = ring.Count;
        var loop = new List<double[]>(n);
        for (int i = 0; i < n; i++) loop.Add(ring[(i + 1) % n]);
        double area = 0;
        for (int i = 0; i < n; i++)
            area += DotCurrent(CrossCurrent(loop[i], loop[(i + 1) % n]), direction);
        if (outer ? area >= 0 : area <= 0) return loop;
        var flipped = new List<double[]>(n) { loop[0] };
        for (int i = n - 1; i >= 1; i--) flipped.Add(loop[i]);
        return flipped;
    }

    /// <summary>An arc of a resampled ring: the <c>Chords</c> equal chords that start at the point <c>First</c> of the ring (cyclic)
    /// stand for one circular arc of the angle <c>Sweep</c>, positive where the arc bulges out of the ring.</summary>
    internal readonly record struct ArcChordsCurrent(int First, int Chords, double Sweep);

    internal static bool TryResampleArcsCurrent(IReadOnlyList<double[]> outer, IReadOnlyList<List<double[]>> inner, IReadOnlyList<Internal.Arc>? arcs,
        out List<double[]> resampledOuter, out List<List<double[]>> resampledInner, out List<double[]> outerCorners) =>
        TryResampleArcsCurrent(outer, inner, arcs, out resampledOuter, out resampledInner, out outerCorners, out _);

    /// <summary><paramref name="outerArcs"/>: the arcs of the resampled outer ring, none when the method returns false or when a
    /// run of the ring could have been laid either way (an arc closed by one straight side: the ring is resampled as before, but
    /// where its arc lies is not known).</summary>
    internal static bool TryResampleArcsCurrent(IReadOnlyList<double[]> outer, IReadOnlyList<List<double[]>> inner, IReadOnlyList<Internal.Arc>? arcs,
        out List<double[]> resampledOuter, out List<List<double[]>> resampledInner, out List<double[]> outerCorners,
        out List<ArcChordsCurrent> outerArcs)
    {
        (resampledOuter, resampledInner, outerCorners, outerArcs) = (outer.ToList(), inner.ToList(), outer.ToList(), []);
        if (arcs is not { Count: > 0 }) return false;
        var rings = new List<IReadOnlyList<double[]>> { outer };
        rings.AddRange(inner);
        var runsByRing = rings.Select(ring => FindArcRunsCurrent(ring, arcs)).ToList();
        if (runsByRing.Sum(runs => runs.Count) != arcs.Count) return false;
        var results = rings.Select((ring, index) => ResampleRingCurrent(ring, runsByRing[index], out _, out _)).ToList();
        resampledOuter = results[0];
        resampledInner = results.Skip(1).ToList();
        ResampleRingCurrent(outer, runsByRing[0], out outerCorners, out outerArcs);
        if (runsByRing[0].Any(run => run.EitherWay)) outerArcs = [];
        return true;
    }

    private static List<double[]> ResampleRingCurrent(IReadOnlyList<double[]> ring, IReadOnlyList<ArcRunCurrent> runs, out List<double[]> corners,
        out List<ArcChordsCurrent> arcChords)
    {
        int n = ring.Count;
        // the normal the ring turns counter-clockwise about
        double[] normal = OthersCurrentAreaVector(ring);
        var inside = new bool[n];
        var runAt = new Dictionary<int, ArcRunCurrent>();
        foreach (var run in runs)
        {
            runAt[run.First] = run;
            for (int step = 1; step < run.Steps; step++) inside[(run.First + step) % n] = true;
        }
        var points = new List<double[]>();
        corners = [];
        arcChords = [];
        for (int i = 0; i < n; i++)
        {
            if (inside[i]) continue;
            points.Add(ring[i]);
            corners.Add(ring[i]);
            if (!runAt.TryGetValue(i, out var found)) continue;
            int steps = (int)Math.Ceiling(Math.Abs(found.Arc.Last - found.Arc.First) / (Math.PI / 16));
            double from = found.Reversed ? found.Arc.Last : found.Arc.First, to = found.Reversed ? found.Arc.First : found.Arc.Last;
            // An arc that turns the way the ring does has its centre on the inner side of the ring: it bulges out.
            bool outward = (to - from) * DotCurrent(CrossCurrent(found.Arc.X, found.Arc.Y), normal) > 0;
            if (steps > 0) arcChords.Add(new ArcChordsCurrent(points.Count - 1, steps, outward ? Math.Abs(to - from) : -Math.Abs(to - from)));
            for (int step = 1; step < steps; step++) points.Add(found.Arc.At(from + (to - from) * step / steps));
        }
        return points;
    }

    internal static List<double[]> CurveLoopCurrent(IReadOnlyList<double[]> ring, double[] direction, bool outer,
        IReadOnlyList<Internal.Arc>? arcs) => CurveLoopCurrent(ring, direction, outer, arcs, out _);

    internal static List<double[]> CurveLoopCurrent(IReadOnlyList<double[]> ring, double[] direction, bool outer,
        IReadOnlyList<Internal.Arc>? arcs, out List<double[]> corners, int shift = 1)
    {
        // (an arc of the face that the ring does not carry means that the ring is not made of the edges the file names: left as it is)
        if (arcs is not { Count: > 0 } || FindArcRunsCurrent(ring, arcs) is not { Count: > 0 } runs || runs.Count != arcs.Count)
        {
            var plain = CurveLoopCurrent(ring, direction, outer);
            corners = plain;
            return plain;
        }

        int n = ring.Count;
        var inside = new bool[n];
        var runAt = new Dictionary<int, ArcRunCurrent>();
        foreach (var run in runs)
        {
            runAt[run.First] = run;
            for (int step = 1; step < run.Steps; step++) inside[(run.First + step) % n] = true;
        }
        var ringCorners = new List<double[]>();
        var edges = new List<(Internal.Arc Arc, bool Reversed)?>();
        for (int i = 0; i < n; i++)
        {
            if (inside[i]) continue;
            ringCorners.Add(ring[i]);
            edges.Add(runAt.TryGetValue(i, out var found) ? (found.Arc, found.Reversed) : null);
        }
        int m = ringCorners.Count;
        if (m < 2)
        {
            var plain = CurveLoopCurrent(ring, direction, outer);
            corners = plain;
            return plain;
        }

        List<double[]> Expand(IReadOnlyList<double[]> loopCorners, IReadOnlyList<(Internal.Arc Arc, bool Reversed)?> loopEdges)
        {
            var points = new List<double[]>();
            for (int k = 0; k < loopCorners.Count; k++)
            {
                points.Add(loopCorners[k]);
                if (loopEdges[k] is not { } edge) continue;
                // the stored parameter runs from First to Last along the ring unless Reversed
                var (arc, reversed) = edge;
                int steps = (int)Math.Ceiling(Math.Abs(arc.Last - arc.First) / (Math.PI / 16));
                double from = reversed ? arc.Last : arc.First, to = reversed ? arc.First : arc.Last;
                for (int i = 1; i < steps; i++) points.Add(arc.At(from + (to - from) * i / steps));
            }
            return points;
        }

        var loopCorners = new List<double[]>(m);
        var loopEdges = new List<(Internal.Arc Arc, bool Reversed)?>(m);
        for (int k = 0; k < m; k++)
        {
            loopCorners.Add(ringCorners[(k + shift) % m]);
            loopEdges.Add(edges[(k + shift) % m]);
        }
        var expanded = Expand(loopCorners, loopEdges);
        double area = 0;
        for (int i = 0; i < expanded.Count; i++)
            area += DotCurrent(CrossCurrent(expanded[i], expanded[(i + 1) % expanded.Count]), direction);
        if (outer ? area >= 0 : area <= 0)
        {
            corners = loopCorners;
            return expanded;
        }

        var flippedCorners = new List<double[]>(m) { loopCorners[0] };
        var flippedEdges = new List<(Internal.Arc Arc, bool Reversed)?>(m);
        for (int k = 0; k < m; k++)
        {
            if (k > 0) flippedCorners.Add(loopCorners[m - k]);
            var original = loopEdges[(m - k - 1) % m];
            flippedEdges.Add(original is { } edge ? (edge.Arc, !edge.Reversed) : null);
        }
        corners = flippedCorners;
        return Expand(flippedCorners, flippedEdges);
    }

    /// <summary>
    /// Writes the swept area, the position and the direction of an extrusion over <paramref name="loops"/> (metres, in the
    /// frame of the product; the first loop is the outer one, counter-clockwise about <paramref name="direction"/>, the others
    /// clockwise).
    /// </summary>
    internal static ExtrusionPartsCurrent WriteExtrusionProfileCurrent(StepWriter w, GlobalsCurrent g, string profileNameText,
        IReadOnlyList<IReadOnlyList<double[]>> loops, double[] direction, double[] internalY, double[]? extrusion = null,
        IReadOnlyList<double[]>? originPoints = null)
    {
        var outer = loops[0];
        var mean = originPoints ?? outer;
        double[] origin = [mean.Average(p => p[0]), mean.Average(p => p[1]), mean.Average(p => p[2])];
        double[] x = CrossCurrent([0, 0, 1], direction);
        if (DotCurrent(x, x) < 1e-18) x = CrossCurrent(internalY, direction);
        x = UnitCurrent(x);
        double[] y = CrossCurrent(direction, x);

        (double X, double Y) Project(double[] p)
        {
            double[] d = [p[0] - origin[0], p[1] - origin[1], p[2] - origin[2]];
            return (DotCurrent(d, x), DotCurrent(d, y));
        }

        int profile;
        bool rectangle = loops.Count == 1 && outer.Count == 4;
        if (rectangle)
        {
            for (int i = 0; i < 4 && rectangle; i++)
            {
                double[] e0 = [outer[(i + 1) % 4][0] - outer[i][0], outer[(i + 1) % 4][1] - outer[i][1], outer[(i + 1) % 4][2] - outer[i][2]];
                double[] e1 = [outer[(i + 2) % 4][0] - outer[(i + 1) % 4][0], outer[(i + 2) % 4][1] - outer[(i + 1) % 4][1], outer[(i + 2) % 4][2] - outer[(i + 1) % 4][2]];
                rectangle = Math.Abs(DotCurrent(e0, e1)) <= 1e-7 * Math.Sqrt(DotCurrent(e0, e0) * DotCurrent(e1, e1));
            }
        }
        if (rectangle)
        {
            var v = outer.Select(Project).ToArray();
            (double X, double Y)[] corners = [v[3], v[0], v[1], v[2]];
            bool flipX = Math.Abs(Math.Abs(direction[2]) - 1.0) > 1e-9 && Math.Abs(x[2]) < Math.Abs(y[2]);
            var xLength = (X: corners[1].X - corners[0].X, Y: corners[1].Y - corners[0].Y);
            var yLength = (X: corners[3].X - corners[0].X, Y: corners[3].Y - corners[0].Y);
            if (flipX) (xLength, yLength) = (yLength, xLength);
            double xLen = Math.Sqrt(xLength.X * xLength.X + xLength.Y * xLength.Y), yLen = Math.Sqrt(yLength.X * yLength.X + yLength.Y * yLength.Y);
            int centre = g.Point2D((corners[0].X + corners[2].X) / 2, (corners[0].Y + corners[2].Y) / 2);
            int placement2D = w.Add("IFCAXIS2PLACEMENT2D", $"{StepWriter.Ref(centre)},{StepWriter.Ref(g.Direction2D(xLength.X / xLen, xLength.Y / xLen))}");
            profile = w.Add("IFCRECTANGLEPROFILEDEF",
                $".AREA.,{profileNameText},{StepWriter.Ref(placement2D)},{StepWriter.Real(xLen)},{StepWriter.Real(yLen)}");
        }
        else
        {
            var curves = loops.Select(loop =>
            {
                var points = loop.Select(p => { var (px, py) = Project(p); return g.Point2D(px, py); }).ToList();
                return w.Add("IFCPOLYLINE", StepWriter.List(points.Append(points[0]).Select(StepWriter.Ref)));
            }).ToList();
            profile = loops.Count == 1
                ? w.Add("IFCARBITRARYCLOSEDPROFILEDEF", $".AREA.,{profileNameText},{StepWriter.Ref(curves[0])}")
                : w.Add("IFCARBITRARYPROFILEDEFWITHVOIDS",
                    $".AREA.,{profileNameText},{StepWriter.Ref(curves[0])},{StepWriter.List(curves.Skip(1).Select(StepWriter.Ref))}");
        }

        int position = g.Axis3D(origin, direction, x);
        // the extrusion direction in the axes of the plane: along the normal, whose slant factor is one (against it when the
        // plane faces away from the extrusion, as the plane of a loop that runs clockwise about the extrusion does)
        double[] along = extrusion ?? direction;
        int relativeDirection = g.Direction3D(DotCurrent(along, x), DotCurrent(along, y), DotCurrent(along, direction));
        return new ExtrusionPartsCurrent(profile, position, relativeDirection);
    }
}
