namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    private static List<(double X, double Y)> ReadSitePropertyCurve(Cursor cur, ushort classId,
        out (double X, double Y)? arcCenter)
    {
        arcCenter = null;
        if (!cur.Take(16)) throw Fail; // Inline GInfo in the property segment's own curve.
        double start = ReadF64(cur), end = ReadF64(cur);
        if (!double.IsFinite(start) || !double.IsFinite(end) || start == end) throw Fail;
        if (classId == 1736)
        {
            var origin = ReadPoint();
            var direction = ReadPoint();
            return [PointAt(start), PointAt(end)];

            (double X, double Y) PointAt(double t) => (origin.X + t * direction.X, origin.Y + t * direction.Y);
        }

        var xAxis = ReadPoint();
        var yAxis = ReadPoint();
        double radius = ReadF64(cur);
        var center = ReadPoint();
        double sweep = end - start;
        if (!cur.Take(1) || !(radius is > 0 and < 1_000_000) || Math.Abs(sweep) > 2 * Math.PI + 1e-7)
            throw Fail;
        if (xAxis.Z == 0 && yAxis.Z == 0) arcCenter = (center.X, center.Y);
        int segments = (int)Math.Ceiling(Math.Abs(sweep) * 60 / Math.PI);
        var points = new List<(double X, double Y)>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            double t = start + sweep * i / segments;
            double cos = radius * Math.Cos(t), sin = radius * Math.Sin(t);
            points.Add((center.X + cos * xAxis.X + sin * yAxis.X, center.Y + cos * xAxis.Y + sin * yAxis.Y));
        }
        return points;

        (double X, double Y, double Z) ReadPoint()
        {
            double x = ReadF64(cur), y = ReadF64(cur), z = ReadF64(cur);
            if (!(Math.Abs(x) < 1_000_000 && Math.Abs(y) < 1_000_000 && Math.Abs(z) < 1_000_000)) throw Fail;
            return (x, y, z);
        }
    }
}
