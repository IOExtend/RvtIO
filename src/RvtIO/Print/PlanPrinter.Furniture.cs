using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    private sealed record FurnitureFace(List<(double X, double Y)> Outer, List<List<(double X, double Y)>> Holes,
        double MinX, double MaxX, double MinY, double MaxY);

    internal static (double Bottom, double Cut, double Top)? GetPlanRange(RvtDocument document, int levelId, int? viewId)
    {
        if (viewId is not int id || document.RawData.PlanViewRanges?.TryGetValue(id, out var range) != true
            || range.Offsets is not { Length: >= 3 } || range.LevelIds is not { Length: >= 3 }) return null;
        double bottom = Plane(2, double.NegativeInfinity), cut = Plane(0, double.NaN), top = Plane(1, double.PositiveInfinity);
        return bottom <= cut && cut <= top && double.IsFinite(cut) ? (bottom, cut, top) : null;

        double Plane(int index, double unlimited)
        {
            int level = range.LevelIds[index];
            if (level == -4) return unlimited;
            if (level == -1) level = levelId;
            return document.GetElement(level)?.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value is double height
                ? height + range.Offsets[index] : double.NaN;
        }
    }

    private static bool FurnitureInView(Element element, int levelId, (double Bottom, double Cut, double Top)? range)
    {
        if (range is { } planes && element.BoundingBox is { Length: 6 } box
            && double.IsFinite(box[2]) && box[2] <= box[5] && double.IsFinite(box[5]))
        {
            double upper = element.CategoryId == (int)BuiltInCategory.OST_Casework ? planes.Top : planes.Cut;
            return box[5] >= planes.Bottom && box[2] <= upper;
        }
        return LevelOf(element) == levelId;
    }

    private static bool FixtureProjectionCoversBody(PlanFixtureProjection projection, GeometryData geometry)
        => projection.Masks is [{ Count: >= 3 } mask] && HorizontalMaskCoversBody(mask, geometry);

    private static bool HorizontalMaskCoversBody(List<double[]> mask, GeometryData geometry)
    {
        var ring = mask.Select(InPlan).ToList();
        double area = SignedArea(ring);
        if (Math.Abs(area) < 1e-8) return false;
        if (area < 0) ring.Reverse();

        // A convex mask contains the full projection of a face when it contains its vertices.
        for (int i = 0; i < ring.Count; i++)
            if (Side(ring[i], ring[(i + 1) % ring.Count], ring[(i + 2) % ring.Count]) < -1e-7)
                return false;
        return geometry.Faces.Where(face => !face.ExcludedFromTopPlan)
            .All(face => face.OuterVertices is { Count: >= 3 } vertices && vertices.All(point =>
            {
                if (point[2] > mask[0][2] + 1e-7) return false;
                var xy = InPlan(point);
                for (int i = 0; i < ring.Count; i++)
                    if (Side(ring[i], ring[(i + 1) % ring.Count], xy) < -1e-7) return false;
                return true;
            }));

        static double Side((double X, double Y) a, (double X, double Y) b, (double X, double Y) point) =>
            (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
    }

    internal static void DrawFixtureLines(PdfPage page, RvtDocument? document,
        List<(double X1, double Y1, double X2, double Y2, int StyleId)> lines,
        double scale, double fallbackWidth, Func<double, double> toX, Func<double, double> toY,
        (double X0, double Y0, double X1, double Y1)? clip)
    {
        foreach (var group in lines.GroupBy(line => line.StyleId))
        {
            var color = new PdfColor(0, 0, 0);
            double pen = fallbackWidth;
            double[]? dashes = null;
            if (document?.RawData.GraphicsStylePens?.TryGetValue(group.Key, out var style) == true && style.GStyleType == 1)
            {
                color = new PdfColor((style.Color & 255) / 255d, ((style.Color >> 8) & 255) / 255d,
                    ((style.Color >> 16) & 255) / 255d);
                if (document.RawData.PenWidthTable is { } widths
                    && PenWidths.TryPenWidth(widths, style.PenNumber, (int)Math.Round(scale), out double savedWidth))
                    pen = savedWidth;
                if (LinePatterns.TryGetPatternDashes(document, style.LinePatternId, out var savedDashes))
                    dashes = savedDashes;
            }
            foreach (var (x1, y1, x2, y2, _) in group)
            {
                double a = x1, b = y1, c = x2, d = y2;
                if (clip is { } bounds && !ElevationPrinter.ClipSegment(a, b, c, d,
                    bounds.X0, bounds.Y0, bounds.X1, bounds.Y1, out a, out b, out c, out d))
                    continue;
                if (dashes != null)
                    page.SetDashPoints(LinePatterns.FitEndpointDashes(Math.Sqrt((c - a) * (c - a) + (d - b) * (d - b)) / scale * 72 / 25.4, dashes));
                page.DrawLine(toX(a), toY(b), toX(c), toY(d), pen, color);
                if (dashes != null) page.ResetDash();
            }
        }
    }

    private static List<FurnitureFace> FurnitureFaces(IEnumerable<GeoFace> faces)
    {
        var result = new List<FurnitureFace>();
        foreach (var face in faces)
        {
            if (face.OuterVertices is not { Count: >= 3 } vertices || Project(vertices, true) is not { } outer) continue;
            var holes = new List<List<(double X, double Y)>>();
            foreach (var inner in face.InnerVertices ?? [])
                if (Project(inner, false) is { } hole) holes.Add(hole);
            result.Add(new(outer, holes, outer.Min(p => p.X), outer.Max(p => p.X),
                outer.Min(p => p.Y), outer.Max(p => p.Y)));
        }
        return result;

        static List<(double X, double Y)>? Project(List<double[]> vertices, bool isOuter)
        {
            if (vertices.Count < 3 || vertices.Any(v => v.Length < 2 || !(Math.Abs(v[0]) < 1e6 && Math.Abs(v[1]) < 1e6)))
                return null;
            var points = vertices.Select(v => (X: v[0] * FeetToMillimetres, Y: v[1] * FeetToMillimetres)).ToList();
            double area = SignedArea(points);
            if (Math.Abs(area) < 1e-8) return null;
            if ((area > 0) != isOuter) points.Reverse();
            return points;
        }
    }

    // shortcut: the silhouette stroke uses a 5 mm grid; the opaque fill uses the projected face rings.
    private static List<(double X1, double Y1, double X2, double Y2)> SilhouetteLines(
        List<FurnitureFace> faces, double cell)
    {
        if (faces.Count == 0) return [];
        double x0 = faces.Min(face => face.MinX), x1 = faces.Max(face => face.MaxX);
        double y0 = faces.Min(face => face.MinY), y1 = faces.Max(face => face.MaxY);
        int nx = (int)Math.Ceiling((x1 - x0) / cell) + 1;
        int ny = (int)Math.Ceiling((y1 - y0) / cell) + 1;
        if (nx < 1 || ny < 1 || (long)nx * ny > 400_000) return [];
        var inside = new bool[nx, ny];
        foreach (var face in faces)
        {
            int startX = Math.Max(0, (int)Math.Floor((face.MinX - x0) / cell));
            int startY = Math.Max(0, (int)Math.Floor((face.MinY - y0) / cell));
            int endX = Math.Min(nx - 1, (int)Math.Floor((face.MaxX - x0) / cell));
            int endY = Math.Min(ny - 1, (int)Math.Floor((face.MaxY - y0) / cell));
            for (int i = startX; i <= endX; i++)
                for (int j = startY; j <= endY; j++)
                {
                    if (inside[i, j]) continue;
                    double cx = x0 + (i + 0.5) * cell, cy = y0 + (j + 0.5) * cell;
                    inside[i, j] = PointInPolygon(cx, cy, face.Outer)
                        && !face.Holes.Any(hole => PointInPolygon(cx, cy, hole));
                }
        }
        var lines = new List<(double X1, double Y1, double X2, double Y2)>();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                if (!inside[i, j]) continue;
                double left = x0 + i * cell, right = left + cell;
                double bottom = y0 + j * cell, top = bottom + cell;
                if (i == 0 || !inside[i - 1, j]) lines.Add((left, bottom, left, top));
                if (i == nx - 1 || !inside[i + 1, j]) lines.Add((right, bottom, right, top));
                if (j == 0 || !inside[i, j - 1]) lines.Add((left, bottom, right, bottom));
                if (j == ny - 1 || !inside[i, j + 1]) lines.Add((left, top, right, top));
            }
        return lines;
    }
}
