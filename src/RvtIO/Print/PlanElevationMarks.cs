using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

internal static class PlanElevationMarks
{
    private const double FeetToMillimetres = 304.8;

    internal static void Draw(PdfPage page, RvtDocument document, Element plan, ViewCropFrame crop,
        double scale, double sheetX, double sheetY)
    {
        if (document.RawData.PlanElevationMarks is not { Count: > 0 } marks
            || crop.Right is not [1, 0, 0] || crop.Up is not [0, 1, 0]
            || document.RawData.PlanViewRanges?.GetValueOrDefault(plan.Id) is not
                ({ Length: > 0 } offsets, { Length: > 0 } levels)
            || document.GetElement(levels[0])?.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value is not double level)
            return;

        double cut = level + offsets[0];
        if (!double.IsFinite(cut)) return;
        var hidden = document.RawData.ViewHiddenElements?.GetValueOrDefault(plan.Id);
        double pen = PenWidths.TryGetPenNumber(document, (int)BuiltInCategory.OST_ElevationMarks, false, out int weight)
            && PenWidths.TryAnnotationWidth(document, weight, out double width) ? width : 0.1;
        foreach (var mark in marks)
        {
            double u = mark.X - crop.OriginX, v = mark.Y - crop.OriginY;
            if (u < crop.X0 || u > crop.X1 || v < crop.Y0 || v > crop.Y1 || hidden?.Contains(mark.Id) == true
                || document.GetElement(mark.BodyId) is not
                    { FamilyName: "M_Elevation Mark Body_Circle-12mm", BoundingBox: { Length: 6 } box })
                continue;

            double r = (box[3] - box[0]) * FeetToMillimetres / 2;
            if (!(r is > 0 and <= 100) || Math.Abs(box[3] - box[0] - box[4] + box[1]) > 1e-6) continue;
            double x = sheetX + u * FeetToMillimetres / scale;
            double y = sheetY + v * FeetToMillimetres / scale;
            double tipLength = r / Math.Cos(mark.ArrowHalfAngle);
            if (plan.ViewAnnotationCropActive == true)
            {
                var (left, right, top, bottom) = plan.ViewAnnotationCrop ?? (0, 0, 0, 0);
                double margin = tipLength * scale / FeetToMillimetres;
                if (u - margin < crop.X0 - left * scale || u + margin > crop.X1 + right * scale
                    || v - margin < crop.Y0 - bottom * scale || v + margin > crop.Y1 + top * scale)
                    continue;
            }

            if (hidden?.Contains(mark.ArrowId) == true || hidden?.Contains(mark.ViewId) == true
                || 1 / scale < mark.CoarserScale || document.GetElement(mark.ViewId) is not
                    { ViewDirection: [var dx, var dy, 0], ViewCrop: { } elevationCrop }
                || elevationCrop.Up is not [0, 0, 1]
                || cut < elevationCrop.OriginZ + elevationCrop.Y0 || cut > elevationCrop.OriginZ + elevationCrop.Y1
                || document.GetElement(mark.ArrowSymbolId)?.FamilyName != "M_Elevation Mark Pointer_Circle")
                continue;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (!(length > 0) || !double.IsFinite(length)) continue;
            dx /= length;
            dy /= length;

            var viewport = document.Elements.FirstOrDefault(element => element.ViewportViewId == mark.ViewId);
            string detail = viewport?.LookupParameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.Value as string ?? "-";
            string number = viewport?.OwnerViewId is int sheetId
                ? document.GetElement(sheetId)?.LookupParameter(BuiltInParameter.SHEET_NUMBER)?.Value as string ?? "-" : "-";
            page.DrawPolyline(ElevationPrinter.DatumCircle(x, y, r, 25), pen, true);
            DrawLabel(page, document, mark.BodyId, BuiltInParameter.VIEWER_SHEET_NUMBER, number, x, y, 0, 1);

            double direction = Math.Atan2(dy, dx);
            var points = new List<(double X, double Y)> { (x + dx * tipLength, y + dy * tipLength) };
            for (int i = 0; i <= 12; i++)
            {
                double a = direction + mark.ArrowHalfAngle * (2.0 * i / 12 - 1);
                points.Add((x + r * Math.Cos(a), y + r * Math.Sin(a)));
            }
            page.DrawPolyline(points, pen, true, mark.Filled ? 0 : null);
            DrawLabel(page, document, mark.ArrowSymbolId, BuiltInParameter.VIEWER_DETAIL_NUMBER, detail, x, y, dx, dy);
        }
    }

    private static void DrawLabel(PdfPage page, RvtDocument document, int symbolId, BuiltInParameter parameter,
        string value, double x, double y, double dx, double dy)
    {
        var label = document.RawData.TitleBlockLabels?.GetValueOrDefault(symbolId)
            ?.FirstOrDefault(candidate => candidate.ParamId == (int)parameter);
        if (label is not { Bx: 1, By: 0, Hx: 0, Hy: 1 } || page.Owner.Font is not { } font) return;
        double cap = document.RawData.TitleBlockCapHeightsFeet?.GetValueOrDefault(label.AttrId) ?? 0;
        double height = label.Height * FeetToMillimetres;
        double size = SheetComposer.SymbolLabelTextSize(font, height, cap);
        bool opaque = document.RawData.OpaqueSymbolLabelStyles?.Contains(label.AttrId) == true;
        SheetComposer.DrawTitleBlockText(page, [label.Prefix + value + label.Suffix],
            x + (label.Ox * dy + label.Oy * dx) * FeetToMillimetres,
            y + (-label.Ox * dx + label.Oy * dy) * FeetToMillimetres,
            size, height, label.Flags, 1, 0, 0, 1, opaque);
    }
}
