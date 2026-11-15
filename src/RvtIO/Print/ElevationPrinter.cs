namespace RvtIO.Print;

using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

/// <summary>An elevation view that can be printed: its element id, name and the direction it looks at, decoded from the
/// name (North, South, East, West).</summary>
/// <param name="ViewId">The element id of the view.</param>
/// <param name="Name">The view name.</param>
/// <param name="Direction">The direction the view looks at: north, south, east or west.</param>
/// <param name="Scale">The view scale denominator.</param>
public sealed record ElevationView(int ViewId, string Name, string Direction, int Scale);

/// <summary>
/// Prints an elevation from the 3D geometry the reader decodes: every planar face of the model (walls, floors, roofs,
/// columns, windows and doors) is projected on the vertical plane of the view, painted white from the farthest to the
/// nearest and outlined when it faces the viewer. The view plane itself is not decoded yet, so the direction is taken
/// from the view name (North, South, East, West); a section is not printed (see docs/printing.md).
/// </summary>
public static class ElevationPrinter
{
    private const double FeetToMillimetres = 304.8;

    /// <summary>The elevation views of the document: named North, South, East or West, by name.</summary>
    /// <param name="document">The opened document.</param>
    public static IReadOnlyList<ElevationView> Views(RvtDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var views = new List<ElevationView>();
        foreach (var element in document.Elements)
        {
            if (element.RootClassName is not ("DBViewSection" or "DBViewElevation") && element.NativeRecordClass != 1016)
                continue;
            if (element.LookupParameter(BuiltInParameter.VIEW_NAME)?.Value is not string name || name.Length == 0)
                continue;
            string? direction = DirectionOf(name);
            if (direction == null) continue;
            int scale = element.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value is int value && value > 0 ? value : 100;
            views.Add(new ElevationView(element.Id, name, direction, scale));
        }
        views.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return views;
    }

    /// <summary>The direction a view name looks at, or null when the name is not a compass elevation.</summary>
    internal static string? DirectionOf(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("north")) return "north";
        if (lower.Contains("south")) return "south";
        if (lower.Contains("east")) return "east";
        if (lower.Contains("west")) return "west";
        return null;
    }

    /// <summary>The projected faces of an elevation: the polygons in view millimetres, the frame extents and the
    /// faces that get an outline.</summary>
    internal sealed record ElevationContent(
        List<(List<(double X, double Y)> Points, double Depth, bool Outline)> Faces,
        double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>Projects the model geometry on the vertical plane of a direction. Null when the model has no face.</summary>
    internal static ElevationContent? Collect(RvtDocument document, string direction)
    {
        (double[] H, double[] V, double[] D) = direction switch
        {
            "south" => (new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }),
            "north" => (new[] { -1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, -1, 0 }),
            "east" => (new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, new[] { -1.0, 0, 0 }),
            _ => (new[] { 0.0, -1, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }),
        };
        var faces = new List<(List<(double X, double Y)> Points, double Depth, bool Outline)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var element in document.Elements)
        {
            // The plants and the site surfaces are drawn in an elevation as symbols, not as their tessellated faces:
            // projecting them buries the building under a mesh of leaves and ground.
            if (element.CategoryId is (int)BuiltInCategory.OST_Planting or (int)BuiltInCategory.OST_Topography
                or (int)BuiltInCategory.OST_Site) continue;
            if (element.Geometry is not { Faces.Count: > 0 } geometry) continue;
            foreach (var face in geometry.Faces)
            {
                if (face.OuterVertices is not { Count: >= 3 } vertices) continue;
                if (face.CurvedSurface) continue;
                var points = new List<(double X, double Y)>(vertices.Count);
                double depth = 0;
                foreach (var vertex in vertices)
                {
                    double x = vertex[0] * FeetToMillimetres, y = vertex[1] * FeetToMillimetres;
                    double z = vertex[2] * FeetToMillimetres;
                    points.Add((x * H[0] + y * H[1] + z * H[2], z));
                    depth += x * D[0] + y * D[1] + z * D[2];
                }
                depth /= vertices.Count;
                double facing = Math.Abs(face.Normal[0] * D[0] + face.Normal[1] * D[1] + face.Normal[2] * D[2]);
                foreach (var (x, y) in points)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
                faces.Add((points, depth, facing > 0.5));
            }
        }
        if (faces.Count == 0) return null;
        // Far to near: painter's algorithm, so a nearer wall hides the one behind it.
        faces.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        return new ElevationContent(faces, minX, minY, maxX, maxY);
    }

    /// <summary>Draws the projected elevation with (MinX, MinY) at (originX, originY), one model unit per 1/scale.</summary>
    internal static void DrawContent(PdfPage page, ElevationContent content, double scale, double originX, double originY)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        foreach (var (points, _, outline) in content.Faces)
        {
            var pagePoints = points.Select(point => (ToX(point.X), ToY(point.Y))).ToList();
            page.DrawPolyline(pagePoints, outline ? 0.2 : 0, true, 1);
        }
    }
}
