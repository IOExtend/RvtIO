namespace RvtIO.Model;

/// <summary>A project grid axis, with its authored name and ordered curves in internal feet.</summary>
public sealed class GridAxis
{
    internal GridAxis(int id, string name, LocationCurve[] segments)
    {
        Id = id;
        Name = name;
        Segments = Array.AsReadOnly(segments);
    }

    /// <summary>Revit element ID of the grid or multi-segment grid.</summary>
    public int Id { get; }
    /// <summary>Datum label of the axis.</summary>
    public string Name { get; }
    /// <summary>Line and circular arc segments in order from the first end to the last.</summary>
    public IReadOnlyList<LocationCurve> Segments { get; }

    /// <summary>Samples the axis as an ordered polyline in feet, with the requested maximum arc-to-chord error.
    /// Returns null if the tolerance would require more than 65,536 vertices or the curve is incomplete.</summary>
    public List<double[]>? Tessellate(double maxErrorFeet)
    {
        if (!double.IsFinite(maxErrorFeet) || maxErrorFeet <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxErrorFeet));
        const int MaxVertices = 65_536;
        var points = new List<double[]> { Segments[0].StartPoint };
        foreach (var curve in Segments)
        {
            if (curve.CurveType == "Arc")
            {
                if (curve.ArcRadius is not double radius || curve.ArcCenter is not { } center
                    || curve.ArcXDirection is not { } x || curve.ArcYDirection is not { } y)
                    return null;
                double start = curve.ArcStartParameter, sweep = curve.ArcEndParameter - start;
                double step = 4 * Math.Asin(Math.Sqrt(Math.Min(1, maxErrorFeet / (2 * radius))));
                double count = Math.Ceiling(Math.Abs(sweep) / Math.Min(Math.PI / 2, step));
                if (!(count >= 1 && count <= MaxVertices - points.Count)) return null;
                for (int j = 1; j < (int)count; j++)
                    points.Add(PointOnArc(center, radius, x, y, start + sweep * j / count));
            }
            else if (points.Count == MaxVertices)
                return null;
            points.Add(curve.EndPoint);
        }
        return points;
    }

    internal static double[] PointOnArc(double[] center, double radius, double[] x, double[] y, double angle)
    {
        double cos = Math.Cos(angle) * radius, sin = Math.Sin(angle) * radius;
        return [center[0] + cos * x[0] + sin * y[0], center[1] + cos * x[1] + sin * y[1], center[2] + cos * x[2] + sin * y[2]];
    }
}
