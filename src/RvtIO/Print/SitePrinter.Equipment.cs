using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class SitePrinter
{
    private static PlanFixtureProjection? CollectEquipment(RvtDocument document, int viewId,
        double bottom, double cut, IReadOnlySet<int>? hidden, out HashSet<int> shadowCasters)
    {
        shadowCasters = [];
        int? detail = document.GetElement(viewId)?.LookupParameter(BuiltInParameter.VIEW_DETAIL_LEVEL)?.Value as int?;
        if (detail is not (1 or 2)) return null;

        var hiddenCategories = document.RawData.ViewHiddenCategories?.GetValueOrDefault(viewId);
        var lines = new List<StyledModelLine>();
        var masks = new List<List<double[]>>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId is not ((int)BuiltInCategory.OST_ElectricalEquipment or (int)BuiltInCategory.OST_Site)
                || element.IsDiscovered || element.SerializedUnplacedOwnerId is > 0
                || element.NativeIsSymbol == true || element.RootClassIs("FamilySymbol")
                || element.OwnerViewId is > 0 && element.OwnerViewId != viewId
                || hidden?.Contains(element.Id) == true || hiddenCategories?.Contains(element.CategoryId.Value) == true
                || element.BoundingBox is not { Length: 6 } box || !(box[2] >= bottom && box[5] <= cut)
                || element.PlacedPlanFixtureGraphics is not { } graphics)
                continue;

            var projection = detail == 1 ? graphics.CoarseProjection : graphics.MediumProjection;
            if (projection == null) continue;
            var visibleLines = projection.Lines.Where(line =>
                document.RawData.GraphicsStylePens?.TryGetValue(line.StyleId, out var style) != true
                || hiddenCategories?.Contains(style.CategoryId) != true).ToArray();
            if (visibleLines.Length == 0) continue;
            lines.AddRange(visibleLines);
            masks.AddRange(projection.Masks);
            if (element.CategoryId == (int)BuiltInCategory.OST_Site) shadowCasters.Add(element.Id);
        }
        return lines.Count == 0 ? null : new(lines, masks);
    }

    private static void DrawEquipment(PdfPage page, RvtDocument document, SiteGraphics graphics, ViewCropFrame? basis,
        double scale, Func<double, double> toX, Func<double, double> toY)
    {
        var equipment = graphics.Equipment!;
        var masks = new List<List<(double X, double Y)>>();
        foreach (var mask in equipment.Masks)
        {
            var ring = mask.Select(point =>
            {
                var (x, y) = InView(point);
                return (toX(x), toY(y));
            }).ToList();
            double area = PlanPrinter.SignedArea(ring);
            if (!double.IsFinite(area) || Math.Abs(area) < 1e-9) continue;
            if (area < 0) ring.Reverse();
            masks.Add(ring);
        }
        if (masks.Count > 0)
        {
            page.FillPolygonUnion(masks, 1);
            // The mask hides contours behind the symbol. Cast shadows can still cross it.
            if (graphics.GroundShadows.Count > 0)
            {
                page.BeginPolygonClip(masks, evenOdd: false);
                page.FillPolygonUnion(graphics.GroundShadows.Select(ring => ring.Select(p => (toX(p.X), toY(p.Y))).ToList()),
                    graphics.ShadowGray);
                page.EndClip();
            }
        }

        var lines = equipment.Lines.Select(line =>
        {
            var (ax, ay) = InView(line.Start);
            var (bx, by) = InView(line.End);
            return (ax, ay, bx, by, line.StyleId);
        }).ToList();
        PlanPrinter.DrawFixtureLines(page, document, lines, scale, 0.1, toX, toY, null);

        (double X, double Y) InView(double[] point) => basis == null
            ? (point[0] * FeetToMillimetres, point[1] * FeetToMillimetres)
            : ((point[0] * basis.Right[0] + point[1] * basis.Right[1]) * FeetToMillimetres,
                (point[0] * basis.Up[0] + point[1] * basis.Up[1]) * FeetToMillimetres);
    }
}
