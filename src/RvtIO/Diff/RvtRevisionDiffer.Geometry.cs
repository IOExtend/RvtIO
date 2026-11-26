using RvtIO.Model;

namespace RvtIO.Diff;

public static partial class RvtRevisionDiffer
{
    private static readonly RvtDiffVector3 NoShift = new(0, 0, 0);

    private static bool SolidsDiffer(List<GeoSolid>? left, List<GeoSolid>? right, double tol) =>
        !EqualUnordered(left ?? [], right ?? [], s => s.ProfileWidth, tol,
            (a, b) => Near(a.ProfileWidth, b.ProfileWidth, tol)
                && Near(a.ProfileDepth, b.ProfileDepth, tol)
                && Near(a.ExtrusionHeight, b.ExtrusionHeight, tol)
                && EqualUnordered(a.Openings ?? [], b.Openings ?? [], o => o.Width, tol,
                    (x, y) => Near(x.Width, y.Width, tol) && Near(x.Height, y.Height, tol)
                        && SamePoint(x.Position, y.Position, NoShift, tol)));

    private readonly record struct FaceShape(GeoFace Face, double Area, IReadOnlyList<List<double[]>> Rings,
        IReadOnlyList<double[]> UnprojectedHoles);

    private static bool FacesDiffer(List<GeoFace>? left, List<GeoFace>? right, RvtDiffPlacementChange? placement,
        RvtDiffOptions options)
    {
        if ((left?.Count ?? 0) != (right?.Count ?? 0)) return true;
        if (left is not { Count: > 0 }) return false;

        var shift = IsPureTranslation(placement, options) ? placement!.TranslationFeet : NoShift;
        double tol = options.LinearToleranceFeet;
        return !EqualUnordered(left.Select(DescribeFace).ToArray(), right!.Select(DescribeFace).ToArray(),
            face => face.Area, options.AreaToleranceSquareFeet,
            (a, b) => Near(a.Area, b.Area, options.AreaToleranceSquareFeet)
                && SamePoint(a.Face.Normal, b.Face.Normal, NoShift, options.AngleToleranceRadians)
                && (a.Rings.Count > 0 && b.Rings.Count > 0 || SamePoint(a.Face.Origin, b.Face.Origin, shift, tol))
                && EqualUnordered(a.Rings, b.Rings, ring => RingVertexCount(ring, tol), 0,
                    (x, y) => SameRing(x, y, shift, tol))
                && EqualUnordered(a.UnprojectedHoles, b.UnprojectedHoles, h => h[0], tol,
                    (x, y) => Near(x[0], y[0], tol) && Near(x[1], y[1], tol)
                        && Near(x[2], y[2], tol) && Near(x[3], y[3], tol)));
    }

    private static FaceShape DescribeFace(GeoFace face)
    {
        double area = face.Area ?? PolygonAreaEstimate(face.OuterVertices);
        // Rendered inner rings belong to this region. SourceLoops can include other regions of the same face.
        if (face.InnerVertices is { Count: > 0 } inner && inner.Any(r => r.Count > 0))
            return new(face, area, WithOuter(face, inner.Where(r => r.Count > 0)), []);
        if (face.SourceLoops is { Count: > 0 } source)
            return new(face, area, source, []);

        var envelopes = face.InnerLoops?.Where(h => h.Length == 4).ToArray() ?? [];
        if (envelopes.Length == 0)
            return new(face, area, WithOuter(face, []), []);

        var x = face.PlaneXAxis ?? face.XVector;
        var y = face.PlaneYAxis ?? face.YVector;
        if (face.Origin.Length < 3 || !HasDirection(x) || !HasDirection(y))
            return new(face, area, WithOuter(face, []), envelopes);

        return new(face, area, WithOuter(face, envelopes.Select(h =>
            new List<double[]> { Point(h[0], h[1]), Point(h[2], h[1]), Point(h[2], h[3]), Point(h[0], h[3]) })), []);

        double[] Point(double u, double v) =>
        [
            face.Origin[0] + u * x[0] + v * y[0],
            face.Origin[1] + u * x[1] + v * y[1],
            face.Origin[2] + u * x[2] + v * y[2],
        ];

        static bool HasDirection(double[] vector) => vector.Length >= 3
            && (vector[0] != 0 || vector[1] != 0 || vector[2] != 0);
    }

    private static IReadOnlyList<List<double[]>> WithOuter(GeoFace face, IEnumerable<List<double[]>> holes) =>
        face.OuterVertices is { Count: > 0 } outer ? [outer, .. holes] : [.. holes];

    private static bool IsPureTranslation(RvtDiffPlacementChange? placement, RvtDiffOptions options) =>
        placement != null && !placement.MirroredChanged
        && (placement.RotationDeltaDegrees ?? 0) <= options.AngleToleranceRadians * (180.0 / Math.PI) * 10.0;

    private static bool Near(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;

    private static bool SamePoint(double[] a, double[] b, RvtDiffVector3 shift, double tol) =>
        Near(Component(a, 0) + shift.X, Component(b, 0), tol)
        && Near(Component(a, 1) + shift.Y, Component(b, 1), tol)
        && Near(Component(a, 2) + shift.Z, Component(b, 2), tol);

    private static double Component(double[] point, int axis) => axis < point.Length ? point[axis] : 0;

    private static int RingVertexCount(IReadOnlyList<double[]> ring, double tol) =>
        ring.Count > 1 && SamePoint(ring[0], ring[^1], NoShift, tol) ? ring.Count - 1 : ring.Count;

    private static bool SameRing(List<double[]> left, List<double[]> right, RvtDiffVector3 shift, double tol)
    {
        int count = RingVertexCount(left, tol);
        if (count != RingVertexCount(right, tol)) return false;
        if (count == 0) return true;

        for (int start = 0; start < count; start++)
        {
            if (!SamePoint(left[0], right[start], shift, tol)) continue;
            if (InOrder(start, 1) || InOrder(start, -1)) return true;
        }
        return false;

        bool InOrder(int start, int direction)
        {
            int j = start;
            for (int i = 1; i < count; i++)
            {
                j = (j + direction + count) % count;
                if (!SamePoint(left[i], right[j], shift, tol)) return false;
            }
            return true;
        }
    }

    private static bool EqualUnordered<T>(IReadOnlyList<T> left, IReadOnlyList<T> right, Func<T, double> key,
        double keyTolerance, Func<T, T, bool> equal)
    {
        int count = left.Count;
        if (count != right.Count) return false;
        int firstDifferent = 0;
        while (firstDifferent < count && equal(left[firstDifferent], right[firstDifferent])) firstDifferent++;
        if (firstDifferent == count) return true;

        var sorted = right.Select(item => (Item: item, Key: key(item))).OrderBy(x => x.Key).ToArray();
        var owners = new int[count];
        Array.Fill(owners, -1);
        var seen = new int[count];
        var reachedFrom = new int[count];
        var previousRight = new int[count];
        var pending = new Queue<int>();

        // A first match can consume another face's only candidate near a tolerance boundary. Rematch along a path.
        for (int i = 0; i < count; i++)
        {
            if (owners[i] == -1 && equal(left[i], sorted[i].Item))
            {
                owners[i] = i;
                continue;
            }
            pending.Enqueue(i);
            int freeRight = -1;
            while (freeRight < 0 && pending.TryDequeue(out int current))
            {
                double value = key(left[current]);
                int low = 0, high = count;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (value - sorted[middle].Key > keyTolerance) low = middle + 1;
                    else high = middle;
                }
                for (int j = low; j < count && sorted[j].Key - value <= keyTolerance; j++)
                {
                    if (seen[j] == i + 1 || !equal(left[current], sorted[j].Item)) continue;
                    seen[j] = i + 1;
                    reachedFrom[j] = current;
                    if (owners[j] == -1)
                    {
                        freeRight = j;
                        break;
                    }
                    previousRight[owners[j]] = j;
                    pending.Enqueue(owners[j]);
                }
            }
            if (freeRight < 0) return false;
            pending.Clear();
            for (int j = freeRight;; j = previousRight[owners[j]])
            {
                owners[j] = reachedFrom[j];
                if (owners[j] == i) break;
            }
        }
        return true;
    }
}
