using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    internal sealed record ColumnProfile(List<(double X, double Y)> Ring,
        (double X, double Y, double Radius)? Circle, int CategoryId, int MaterialId);

    private static ColumnProfile? CutColumnProfile(RvtDocument document, Element column, double cut)
    {
        if (column.Geometry is not { Faces.Count: > 0 } geometry
            || geometry.Faces.Any(face => face.CurvedSurface || face.OuterVertices is not { Count: >= 3 } vertices
                || vertices.Any(p => p.Length != 3 || !p.All(double.IsFinite))
                || face.InnerLoops is { Count: > 0 } || face.InnerVertices is { Count: > 0 }))
            return null;

        var rings = ElevationPrinter.ClosedSectionRings(geometry.Faces, [0, 0, cut * FeetToMillimetres],
            [0, 0, 1], [1, 0, 0], [0, 1, 0], requireClosed: true).Take(2).ToArray();
        if (rings is not [var ring]) return null;

        int materialId = column.LookupParameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.Value is int material
            ? material : 0;
        return new(ring, MatchColumnCylinder(document, geometry, cut, ring), column.CategoryId.GetValueOrDefault(), materialId);
    }

    private static (double X, double Y, double Radius)? MatchColumnCylinder(
        RvtDocument document, GeometryData geometry, double worldCut, List<(double X, double Y)> ring)
    {
        if (geometry.Instances is not [{ Transform: { Length: 12 } transform } instance]
            || !ElementBuilder.IsRigidPlacement(transform) || Math.Abs(Math.Abs(transform[8]) - 1) > 1e-9
            || document.GetElement(instance.SymbolId)?.Geometry is not { } symbol)
            return null;

        double localCut = (worldCut - transform[11]) / transform[8];
        foreach (var face in symbol.Faces)
        {
            if (face.AnalyticCylinderRadius is not (> 0 and < double.PositiveInfinity)
                || face.AnalyticCylinderAxis is not { Length: 3 } axis
                || Math.Abs(axis[0]) > 1e-9 || Math.Abs(axis[1]) > 1e-9 || Math.Abs(Math.Abs(axis[2]) - 1) > 1e-9
                || face.AnalyticCylinderOrigin is not { Length: 3 } origin || !origin.All(double.IsFinite)
                || face.OuterVertices is not { Count: >= 3 } vertices || vertices.Any(p => p.Length != 3 || !double.IsFinite(p[2]))
                || localCut < vertices.Min(p => p[2]) || localCut > vertices.Max(p => p[2]))
                continue;

            var centre = ElementBuilder.TransformGeometryPoint(origin, transform);
            double x = centre[0] * FeetToMillimetres, y = centre[1] * FeetToMillimetres;
            double radius = face.AnalyticCylinderRadius * FeetToMillimetres;
            // Require the placed cut to agree with the analytic surface within the ring's join resolution.
            if (ring.All(p => Math.Abs(Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)) - radius) <= 0.1))
                return (x, y, radius);
        }
        return null;
    }

    private static void DrawColumns(PdfPage page, RvtDocument? document, PlanContent content, double scale, double width,
        Func<double, double> toX, Func<double, double> toY, (double X0, double Y0, double X1, double Y1)? clip)
    {
        var white = new PdfColor(1, 1, 1);
        for (int i = 0; i < content.Columns.Count; i++)
        {
            if (content.ColumnProfiles?.GetValueOrDefault(i) is not { } profile)
            {
                var column = content.Columns[i];
                page.DrawRectangle(toX(column.X), toY(column.Y), column.Width / scale, column.Height / scale, width, 1);
                continue;
            }

            double cutWidth = document != null && PenWidths.TryGetWidth(document, profile.CategoryId, true,
                (int)Math.Round(scale), out double savedWidth) ? savedWidth : width;
            if (profile.Circle is { } circle)
            {
                if (clip is { } frame)
                    page.BeginClip(toX(frame.X0), toY(frame.Y0), toX(frame.X1) - toX(frame.X0), toY(frame.Y1) - toY(frame.Y0));
                page.DrawFilledCircle(toX(circle.X), toY(circle.Y), circle.Radius / scale, cutWidth, white, new PdfColor(0, 0, 0));
                if (clip != null) page.EndClip();
            }
            else DrawClippedOutline(page, profile.Ring, toX, toY, cutWidth, white, clip);

            if (document == null || DetailAnnotations.CutGrids(document, profile.MaterialId) is not { } grids)
                continue;
            var hatch = clip is { } bounds
                ? ElevationPrinter.ClipToWindow(profile.Ring, bounds.X0, bounds.X1, bounds.Y0, bounds.Y1) : profile.Ring;
            if (hatch.Count >= 3)
                FillHatch.Draw(page, hatch.Select(p => (toX(p.X), toY(p.Y))).ToList(), grids, toX(0), toY(0), 0.1);
        }
    }
}
