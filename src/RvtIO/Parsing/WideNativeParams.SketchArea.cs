using RvtIO.Geometry;

namespace RvtIO.Parsing;

internal sealed partial class WideNativeParams
{
    private const string SketchAbsorbedCurves = WideBlockWalker.AllItemsKeyPrefix + "sketch:m_absorbedCurves";
    internal const string SketchLineArea = "sketch:lineArea";

    private double? ReadSketchLineArea(WideBlockWalker.Walk walk)
    {
        if (!walk.Exact || walk.Captures.Where(c => c.AtRoot && c.Key == SketchAbsorbedCurves + "#")
            .Take(2).ToArray() is not [{ Integer: >= 3 } count]) return null;
        var pointers = walk.Captures.Where(c => c.AtRoot && c.Kind == WideBlockWalker.CaptureKind.PointerClass
            && c.Key.StartsWith(SketchAbsorbedCurves + "@", StringComparison.Ordinal)).ToArray();
        var owned = Enumerable.Range(0, walk.QueueCountAtStart.FirstOrDefault());
        int[] queue = owned.Where(i => _walker.IsA(walk.Queue[i], "GCurve")).ToArray();
        int[] curveData = owned.Where(i => _walker.Class(walk.Queue[i])?.Name == "CurveElemData").ToArray();
        if (pointers.Length != count.Integer || queue.Length != pointers.Length || curveData.Length != queue.Length) return null;
        foreach (int entry in curveData)
        {
            int first = walk.QueueCountAtStart[entry];
            if (walk.QueueEnd[entry] != first + 1 || _walker.Class(walk.Queue[first])?.Name != "FilledRegionCurveData") return null;
        }

        var byCurve = walk.Captures.Where(c => c.Kind == WideBlockWalker.CaptureKind.Real
            && c.Key.StartsWith(LineKeyPrefix, StringComparison.Ordinal)).ToLookup(c => c.QueueIndex);
        var lines = new List<(double[] Start, double[] End)>(queue.Length);
        for (int i = 0; i < queue.Length; i++)
        {
            int entry = queue[i];
            if (pointers[i].Integer != walk.Queue[entry] || _walker.Class(walk.Queue[entry])?.Name != "GLine") return null;
            var fields = byCurve[entry].ToArray();
            if (fields.Length != 8 || fields.Any(c => !double.IsFinite(c.Real))) return null;
            var ends = fields.Where(c => c.Field == "m_endParams").Select(c => c.Real).ToArray();
            var origin = fields.Where(c => c.Field == "m_origin").Select(c => c.Real).ToArray();
            var direction = fields.Where(c => c.Field == "m_dirVec").Select(c => c.Real).ToArray();
            if (ends.Length != 2 || origin.Length != 3 || direction.Length != 3) return null;
            lines.Add((PointAt(ends[0]), PointAt(ends[1])));

            double[] PointAt(double parameter) => [origin[0] + parameter * direction[0],
                origin[1] + parameter * direction[1], origin[2] + parameter * direction[2]];
        }
        return ClosedLineLoopArea(lines);
    }

    internal static double? ClosedLineLoopArea(List<(double[] Start, double[] End)> lines)
    {
        if (lines.Count < 3) return null;
        const double tolerance = 1e-8;
        var origin = lines[0].Start;
        if (lines.Any(e => Math.Abs(e.Start[2] - origin[2]) > tolerance || Math.Abs(e.End[2] - origin[2]) > tolerance
            || SamePoint(e.Start, e.End))) return null;

        var last = lines[0].End;
        var points = new List<double[]>(lines.Count) { new double[3] };
        for (int i = 1; i < lines.Count; i++)
        {
            if (SamePoint(last, origin)) return null;
            int next = -1;
            for (int j = i; j < lines.Count; j++)
            {
                if (!SamePoint(lines[j].Start, last) && !SamePoint(lines[j].End, last)) continue;
                if (next != -1) return null;
                next = j;
            }
            if (next == -1) return null;
            (lines[i], lines[next]) = (lines[next], lines[i]);
            points.Add([last[0] - origin[0], last[1] - origin[1], 0]);
            last = SamePoint(lines[i].Start, last) ? lines[i].End : lines[i].Start;
        }
        if (!SamePoint(last, origin)) return null;
        double area = WallExtrusion.LoopArea(points);
        return area > 0 && double.IsFinite(area) ? area : null;

        static bool SamePoint(double[] a, double[] b) => Math.Abs(a[0] - b[0]) <= tolerance
            && Math.Abs(a[1] - b[1]) <= tolerance && Math.Abs(a[2] - b[2]) <= tolerance;
    }
}
