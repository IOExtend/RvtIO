using static RvtIO.Geometry.ArcRuns;
using RvtIO.Geometry.Meshing;
using RvtIO.Model;
using static RvtIO.Geometry.BrepMeasures;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>The area of a horizontal curved profile rebuilt from its source curves. The builder samples circular
    /// curves at 100 points and integrates their Hermite coedges in a plane centred on the curve starts.</summary>
    internal static double? RebuiltHorizontalCurveLoopAreaCurrent(GeoFace face)
    {
        // shortcut: profiles with inner loops retain the existing footprint fallback until loop ownership is covered.
        if (face.CurvedSurface || face.OuterVertices is not { Count: >= 3 } ring
            || face.InnerVertices is { Count: > 0 } || face.MeshArcs is not { Count: > 0 } arcs
            || FaceUnitNormal(face) is not { } normal || Math.Abs(normal[2]) < 1 - 1e-9
            || ring.Any(point => point.Length < 3 || point.Any(value => !double.IsFinite(value))))
            return null;
        var runs = FindArcRunsCurrent(ring, arcs);
        if (runs.Count != arcs.Count || runs.Select(run => run.First).Distinct().Count() != runs.Count)
            return null;
        var byStart = runs.ToDictionary(run => run.First);
        var coedges = new List<FaceArea.Coedge>();
        int first = runs[0].First, step = 0;
        while (step < ring.Count)
        {
            int index = (first + step) % ring.Count;
            if (byStart.TryGetValue(index, out var run))
            {
                if (run.Steps <= 0 || step + run.Steps > ring.Count) return null;
                double start = run.Reversed ? run.Arc.Last : run.Arc.First;
                double end = run.Reversed ? run.Arc.First : run.Arc.Last;
                var points = Enumerable.Range(0, 100).Select(i => run.Arc.At(start + (end - start) * i / 99))
                    .Select(point => (point[0], point[1])).ToList();
                coedges.Add(new(points, 14, false));
                step += run.Steps;
            }
            else
            {
                var a = ring[index];
                var b = ring[(index + 1) % ring.Count];
                coedges.Add(new([(a[0], a[1]), (b[0], b[1])], 6, false));
                step++;
            }
        }
        double x = coedges.Average(edge => edge.Points[0].U);
        double y = coedges.Average(edge => edge.Points[0].V);
        return FaceArea.Compute(coedges.Select(edge => new FaceArea.Coedge(
            edge.Points.Select(point => (point.U - x, point.V - y)).ToList(), edge.Flags, edge.SecondFace)));
    }
}
