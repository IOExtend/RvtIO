using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;

namespace RvtIO.Print;

internal static class DetailAnnotations
{
    private const double FeetToMillimetres = 304.8;
    private const double PointsToMillimetres = 25.4 / 72;

    internal sealed record BreakLine(List<(double X, double Y)> Line, List<(double X, double Y)> Mask);
    internal sealed record LayerHatch(List<(double X, double Y)> Polygon,
        IReadOnlyList<TfBlockDeserializer.PatternGrid> Grids, double AnchorX, double AnchorY, double? PenWidth = null);
    internal sealed record LayerBoundary((double X, double Y) Start, (double X, double Y) End, int CategoryId);

    internal static void AddClosedHostCutHatches(List<LayerHatch> output, RvtDocument document, Element host,
        List<(double X, double Y)> ring, double[] right, double[] up, double[] viewOrigin,
        List<LayerBoundary>? boundaries = null, double? penWidth = null)
    {
        if (document.GetElement(host.TypeId)?.SerializedHostTypeLayers is [{ RawMaterialId: > 0 } layer])
        {
            AddMaterialCutHatch(output, document, layer.RawMaterialId, ring, right, up, viewOrigin, penWidth);
            return;
        }
        // shortcut: split rectangular cuts until the layer bands can be clipped to a nonrectangular ring.
        if (ring.Count != 4) return;
        var cuts = ring.Select((a, i) => (X1: a.X, Y1: a.Y, X2: ring[(i + 1) % ring.Count].X,
            Y2: ring[(i + 1) % ring.Count].Y)).ToArray();
        if (cuts.Any(c => Math.Abs(c.X1 - c.X2) > 1e-5 && Math.Abs(c.Y1 - c.Y2) > 1e-5)) return;
        AddLayerHatches(output, document, host, cuts, right, up, viewOrigin, boundaries, penWidth: penWidth);
    }

    internal static void AddMaterialCutHatch(List<LayerHatch> output, RvtDocument document, int materialId,
        List<(double X, double Y)> ring, double[] right, double[] up, double[] viewOrigin, double? penWidth = null)
    {
        if (CutGrids(document, materialId) is { } grids)
            output.Add(new(ring, grids, Project(viewOrigin, right), Project(viewOrigin, up), penWidth));
    }

    internal static void AddLayerHatches(List<LayerHatch> output, RvtDocument document, Element host,
        IEnumerable<(double X1, double Y1, double X2, double Y2)> cuts,
        double[] right, double[] up, double[] viewOrigin, List<LayerBoundary>? boundaries = null,
        List<List<(double X, double Y)>>? wallSections = null, double? penWidth = null)
    {
        bool horizontalHost = host.CategoryId is (int)BuiltInCategory.OST_Floors or (int)BuiltInCategory.OST_Ceilings;
        if (Math.Abs(up[2] - 1) > 1e-6
            || document.GetElement(host.TypeId)?.SerializedHostTypeLayers is not { Length: > 1 } layers) return;
        bool exteriorAtLow = false;
        if (!horizontalHost)
        {
            if (host.CategoryId != (int)BuiltInCategory.OST_Walls
                || host.Location is not { CurveType: "Line", Direction: { Length: 3 } axis }
                || host.SerializedWallFlipped is not bool flipped
                || Math.Abs(axis[0] * right[0] + axis[1] * right[1]) > 1e-6) return;
            double normal = (-axis[1] * right[0] + axis[0] * right[1]) * (flipped ? -1 : 1);
            if (!(Math.Abs(normal) > 0.999)) return;
            exteriorAtLow = normal < 0;
        }
        else
        {
            cuts = cuts.Select(c => (c.Y1, c.X1, c.Y2, c.X2));
        }

        var widths = layers.Select(l => BitConverter.Int64BitsToDouble(l.LayerWidthBits) * FeetToMillimetres).ToArray();
        if (widths.Any(w => !(w is >= 0 and < 10000))) return;
        var sides = cuts.Where(c => Math.Abs(c.X1 - c.X2) < 0.01 && Math.Abs(c.Y1 - c.Y2) > 0.1)
            .Select(c => (X: c.X1, Bottom: Math.Min(c.Y1, c.Y2), Top: Math.Max(c.Y1, c.Y2))).ToArray();
        if (sides.Length < 2) return;
        double left = sides.Min(c => c.X), rightmost = sides.Max(c => c.X);
        if (Math.Abs(rightmost - left - widths.Sum()) > 0.1) return;
        double start = exteriorAtLow ? left : rightmost;
        double direction = exteriorAtLow ? 1 : -1;
        double anchorX = Project(viewOrigin, right), anchorY = Project(viewOrigin, up);
        for (int i = 0; i < layers.Length; i++)
        {
            double end = start + direction * widths[i];
            if (end != start)
            {
                var grids = CutGrids(document, layers[i].RawMaterialId);
                var layerLines = end > left + 0.01 && end < rightmost - 0.01 ? boundaries : null;
                if (wallSections != null)
                {
                    double bandLeft = Math.Min(start, end), bandRight = Math.Max(start, end);
                    foreach (var section in wallSections)
                    {
                        if (grids != null)
                        {
                            var band = ElevationPrinter.ClipToWindow(section, bandLeft, bandRight,
                                double.NegativeInfinity, double.PositiveInfinity);
                            if (band.Count >= 3 && Math.Abs(PlanPrinter.SignedArea(band)) > 1e-6)
                                output.Add(new(band, grids, anchorX, anchorY, penWidth));
                        }
                        if (layerLines != null)
                        {
                            var ring = section.Select(point => new[] { point.X, point.Y, 0d }).ToList();
                            foreach (var (a, b) in ElevationPrinter.CutChords(ring, [end, 0, 0], [1, 0, 0]))
                                layerLines.Add(new((end, a[1]), (end, b[1]), host.CategoryId!.Value));
                        }
                    }
                }
                else if (grids != null || layerLines != null)
                    foreach (var a in sides.Where(c => Math.Abs(c.X - left) < 0.01))
                    foreach (var b in sides.Where(c => Math.Abs(c.X - rightmost) < 0.01))
                    {
                        double low = Math.Max(a.Bottom, b.Bottom), high = Math.Min(a.Top, b.Top);
                        if (high <= low) continue;
                        if (grids != null)
                            output.Add(new([Point(start, low), Point(end, low), Point(end, high), Point(start, high)],
                                grids, anchorX, anchorY, penWidth));
                        layerLines?.Add(new(Point(end, low), Point(end, high), host.CategoryId!.Value));
                    }
            }
            start = end;
        }

        (double X, double Y) Point(double across, double along) =>
            horizontalHost ? (along, across) : (across, along);
    }

    internal static IReadOnlyList<TfBlockDeserializer.PatternGrid>? CutGrids(RvtDocument document, int materialId)
    {
        if (document.RawData.MaterialPatterns?.TryGetValue(materialId, out var material) != true
            || document.RawData.FillPatterns?.TryGetValue(material.CutPatternId, out var pattern) != true
            || pattern.Grids.Count == 0 || !pattern.Grids.All(grid =>
                grid.Target == 0 && double.IsFinite(grid.Angle) && double.IsFinite(grid.Offset)
                && Math.Abs(grid.Offset) > 0.0001 && double.IsFinite(grid.OriginX) && double.IsFinite(grid.OriginY)
                && double.IsFinite(grid.Shift) && grid.Segments.All(length => length is >= 0 and < 100)
                && (grid.Segments.Length == 0 || grid.Segments.Sum() > 0.0001))) return null;
        return pattern.Grids;
    }

    internal static List<BreakLine> BreakLines(RvtDocument document, int viewId, double[] right, double[] up)
    {
        var result = new List<BreakLine>();
        var hidden = document.RawData.ViewHiddenElements?.GetValueOrDefault(viewId);
        foreach (var element in document.Elements)
        {
            if (element.OwnerViewId != viewId || hidden?.Contains(element.Id) == true
                || element.FamilyName != "M_Break Line"
                || element.Transform is not { Origin.Length: 3, BasisX.Length: 3, BasisY.Length: 3 } transform
                || document.GetElement(transform.SymbolId)?.DetailBreak is not { } profile) continue;
            var line = profile.Line.Select(p => ToView(p.X, p.Y)).ToList();
            var mask = new List<(double X, double Y)>(line)
            {
                ToView(profile.Line[^1].X, profile.MaskY), ToView(profile.Line[0].X, profile.MaskY)
            };
            result.Add(new(line, mask));

            (double X, double Y) ToView(double x, double y)
            {
                double[] world = new double[3];
                for (int k = 0; k < world.Length; k++)
                    world[k] = transform.Origin[k] + x * transform.BasisX[k] + y * transform.BasisY[k];
                return (Project(world, right), Project(world, up));
            }
        }
        return result;
    }

    internal static IEnumerable<ElevationPrinter.CollectedEdge> BattInsulation(
        double ax, double ay, double bx, double by, double width, bool mirrored)
    {
        double dx = bx - ax, dy = by - ay;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double radius = width / 6;
        double steps = length / radius;
        if (!(steps is > 0 and <= 4096)) yield break;
        dx /= length;
        dy /= length;
        for (int i = -1; i < steps; i++)
        {
            double side = (i % 2 == 0) == mirrored ? 1 : -1;
            for (int k = 0; k < 8; k++)
            {
                double a = Math.PI * k / 8, b = Math.PI * (k + 1) / 8;
                if (Stroke(i + 1 - Math.Cos(a), side * (2 + Math.Sin(a)),
                    i + 1 - Math.Cos(b), side * (2 + Math.Sin(b))) is { } arc) yield return arc;
            }
            for (int k = 0; k < 6; k++)
            {
                double a = k / 6.0, b = (k + 1) / 6.0;
                if (Stroke(i + Smooth(a), side * (2 - 4 * a),
                    i + Smooth(b), side * (2 - 4 * b)) is { } bridge) yield return bridge;
            }
        }

        static double Smooth(double t) => t * t * (3 - 2 * t);
        ElevationPrinter.CollectedEdge? Stroke(double x1, double y1, double x2, double y2)
        {
            if (x2 <= 0 || x1 >= steps) return null;
            if (x1 < 0)
            {
                y1 += (y2 - y1) * (-x1 / (x2 - x1));
                x1 = 0;
            }
            if (x2 > steps)
            {
                y2 = y1 + (y2 - y1) * ((steps - x1) / (x2 - x1));
                x2 = steps;
            }
            return new(ax + radius * (x1 * dx - y1 * dy), ay + radius * (x1 * dy + y1 * dx),
                ax + radius * (x2 * dx - y2 * dy), ay + radius * (x2 * dy + y2 * dx),
                0, 0, -1, -1, 0, 0, 0, 0);
        }
    }

    internal static void DrawCallouts(PdfPage page, RvtDocument document, Element sheet, Element parent,
        ViewCropFrame parentCrop, double scale, double originX, double originY)
    {
        var (x0, y0, x1, y1) = SheetComposer.CropContentRect(parentCrop);
        var annotationCrop = parent.ViewAnnotationCrop;
        double boundaryPen = Pen(BuiltInCategory.OST_CalloutBoundary, 0.7);
        double leaderPen = Pen(BuiltInCategory.OST_CalloutLeaderLine, 0.1);
        double headPen = Pen(BuiltInCategory.OST_CalloutHeads, 0.1);
        LinePatterns.TryGetDashes(document, (int)BuiltInCategory.OST_CalloutBoundary, out var points);
        double[] pattern = points.Select(value => value * PointsToMillimetres).ToArray();
        string number = sheet.LookupParameter(BuiltInParameter.SHEET_NUMBER)?.Value as string ?? "";
        foreach (var viewport in document.Elements)
        {
            if (viewport.OwnerViewId != sheet.Id || viewport.ViewportViewId is not int viewId
                || document.GetElement(viewId) is not { SectionCallout: { } callout, ViewCrop: { Active: true } crop } child
                || callout.ParentId != parent.Id || !SameAxis(crop.Right, parentCrop.Right) || !SameAxis(crop.Up, parentCrop.Up))
                continue;
            if (document.GetElement(child.TypeId)?.LookupParameter(BuiltInParameter.CALLOUT_TAG)?.Value is not int tagId
                || document.GetElement(tagId) is not { } tag
                || tag.LookupParameter(BuiltInParameter.CALLOUT_CORNER_SHEET_RADIUS)?.Value is not double cornerFeet
                || cornerFeet is <= 0 or > 1
                || tag.LookupParameter(BuiltInParameter.CALLOUT_ATTR_HEAD_TAG)?.Value is not int headId
                || document.GetElement(headId) is not { FamilyName: "M_Callout Head", BoundingBox: { Length: 6 } box })
                continue;
            double headRadius = (box[3] - box[0]) * FeetToMillimetres / 2;
            if (!(headRadius is > 0 and <= 100) || Math.Abs(box[3] - box[0] - box[4] + box[1]) > 1e-6)
                continue;
            var (left, bottom, right, top) = SheetComposer.CropContentRect(crop);
            if (right <= left || top <= bottom || right < x0 || top < y0 || left > x1 || bottom > y1) continue;
            left = ToX(left); right = ToX(right); bottom = ToY(bottom); top = ToY(top);
            double r = Math.Min(cornerFeet * FeetToMillimetres, Math.Min(right - left, top - bottom) / 2);
            double hx = ToX(Project(callout.Head, parentCrop.Right)), hy = ToY(Project(callout.Head, parentCrop.Up));
            double qx = Math.Min(Math.Max(hx, left + r), right - r);
            double qy = Math.Min(Math.Max(hy, bottom + r), top - r);
            double dx = hx - qx, dy = hy - qy;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (!(length > r)) continue;
            dx /= length;
            dy /= length;
            double cx = hx + dx * headRadius, cy = hy + dy * headRadius;
            if (annotationCrop is { } pad
                && (Math.Min(left, cx - headRadius) < ToX(x0) - pad.Left * FeetToMillimetres
                    || Math.Max(right, cx + headRadius) > ToX(x1) + pad.Right * FeetToMillimetres
                    || Math.Min(bottom, cy - headRadius) < ToY(y0) - pad.Bottom * FeetToMillimetres
                    || Math.Max(top, cy + headRadius) > ToY(y1) + pad.Top * FeetToMillimetres)) continue;

            ElevationPrinter.DrawDatumDashes(page, left + r, bottom, right - r, bottom, pattern, boundaryPen, roundCaps: true);
            ElevationPrinter.DrawDatumDashes(page, left + r, top, right - r, top, pattern, boundaryPen, roundCaps: true);
            ElevationPrinter.DrawDatumDashes(page, left, bottom + r, left, top - r, pattern, boundaryPen, roundCaps: true);
            ElevationPrinter.DrawDatumDashes(page, right, bottom + r, right, top - r, pattern, boundaryPen, roundCaps: true);
            Arc(right - r, top - r, 0);
            Arc(left + r, top - r, 90);
            Arc(left + r, bottom + r, 180);
            Arc(right - r, bottom + r, 270);
            page.DrawLine(qx + dx * r, qy + dy * r, hx, hy, leaderPen);
            page.DrawPolyline(Circle(cx, cy, headRadius, 0, 360, 24), headPen, true, 1);
            page.DrawLine(cx - headRadius, cy, cx + headRadius, cy, headPen);
            string detail = viewport.LookupParameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.Value as string ?? "";
            if (page.Owner.Font != null || detail.All(c => c is >= ' ' and <= '~'))
                page.DrawText(detail, cx, cy + 1, 12 * PointsToMillimetres, PdfTextAlignment.Center);
            if (page.Owner.Font != null || number.All(c => c is >= ' ' and <= '~'))
                page.DrawText(number, cx, cy - 4, 8 * PointsToMillimetres, PdfTextAlignment.Center);

            void Arc(double x, double y, int angle) =>
                page.DrawPolyline(Circle(x, y, r, angle, 90, 5), boundaryPen);
        }

        double Pen(BuiltInCategory category, double fallback) =>
            PenWidths.TryGetPenNumber(document, (int)category, false, out int pen)
            && PenWidths.TryAnnotationWidth(document, pen, out double width) ? width : fallback;
        double ToX(double x) => originX + x / scale;
        double ToY(double y) => originY + y / scale;
    }

    private static (double X, double Y)[] Circle(double x, double y, double radius, double start, double sweep, int segments)
    {
        var result = new (double X, double Y)[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            double angle = (start + sweep * i / segments) * Math.PI / 180;
            result[i] = (x + radius * Math.Cos(angle), y + radius * Math.Sin(angle));
        }
        return result;
    }

    private static bool SameAxis(double[] a, double[] b) =>
        Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]) < 1e-6;

    private static double Project(double[] point, double[] axis) =>
        (point[0] * axis[0] + point[1] * axis[1] + point[2] * axis[2]) * FeetToMillimetres;
}
