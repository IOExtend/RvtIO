using RvtIO.BuiltIns;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    private static bool TryDrawFilledSectionMark(PdfPage page, RvtDocument document, int planId, int sectionId,
        IReadOnlyList<(double X, double Y)> line, (double X0, double Y0, double X1, double Y1)? frame, double pen)
    {
        // Filled head and tail with parallel terminal legs in an axis-aligned plan.
        if (page.Owner.Font is not { } font
            || document.GetElement(planId)?.ViewCrop is not { Right: [1, 0, 0], Up: [0, 1, 0] }
            || document.GetElement(sectionId) is not { ViewDirection: [var dx, var dy, 0] } section
            || document.RawData.ViewHiddenElements?.GetValueOrDefault(planId)?.Contains(sectionId) == true
            || document.GetElement(section.TypeId)?.LookupParameter(BuiltInParameter.SECTION_TAG)?.Value is not int tag
            || document.RawData.SectionMarkerSymbols?.GetValueOrDefault(tag) is not (var head, var tail)
            || document.GetElement(head) is not { FamilyName: "M_Section Head - Filled", BoundingBox: { Length: 6 } headBox }
            || document.GetElement(tail) is not { FamilyName: "M_Section Tail - Filled", BoundingBox: { Length: 6 } tailBox }
            || document.RawData.TitleBlockLabels?.GetValueOrDefault(head) is not { } labels)
            return false;

        var detail = labels.FirstOrDefault(label => label.ParamId == (int)BuiltInParameter.VIEWER_DETAIL_NUMBER);
        var sheet = labels.FirstOrDefault(label => label.ParamId == (int)BuiltInParameter.VIEWER_SHEET_NUMBER);
        if (detail is not { Bx: 1, By: 0, Hx: 0, Hy: 1 } || sheet is not { Bx: 1, By: 0, Hx: 0, Hy: 1 })
            return false;
        var start = line[0];
        var end = line[^1];
        double ax = line[1].X - start.X, ay = line[1].Y - start.Y;
        double length = double.Hypot(ax, ay);
        if (!(length > 0 && double.IsFinite(length))) return false;
        double ux = ax / length, uy = ay / length;
        double perpendicular = ux * dy - uy * dx;
        if (!(Math.Abs(perpendicular) > 1 - 1e-8)) return false;
        double vx = -uy * Math.Sign(perpendicular), vy = ux * Math.Sign(perpendicular);

        double radius = -headBox[1] * FeetToMillimetres;
        double tip = headBox[4] * FeetToMillimetres;
        double center = (headBox[0] + headBox[3]) * FeetToMillimetres / 2;
        double tailWidth = (tailBox[3] - tailBox[0]) * FeetToMillimetres;
        double tailHeight = (tailBox[4] - tailBox[1]) * FeetToMillimetres;
        if (!(radius is > 0 and <= 100 && tailWidth is > 0 and <= 100 && tailHeight is > 0 and <= 100
            && Math.Abs(center + radius) < 1e-6 && Math.Abs(tip - radius * Math.Sqrt(2)) < 1e-6))
            return false;

        (double X, double Y) At((double X, double Y) p, double x, double y) =>
            (p.X + x * ux + y * vx, p.Y + x * uy + y * vy);
        bool Contains((double X, double Y) p) => frame is not { } f
            || (p.X >= f.X0 && p.X <= f.X1 && p.Y >= f.Y0 && p.Y <= f.Y1);
        if (!Contains(start) || !Contains(end)) return false;

        var viewport = document.Elements.FirstOrDefault(element => element.ViewportViewId == sectionId && element.OwnerViewId is > 0);
        string number = viewport?.OwnerViewId is int sheetId
            ? document.GetElement(sheetId)?.LookupParameter(BuiltInParameter.SHEET_NUMBER)?.Value as string ?? "---" : "---";
        string detailNumber = viewport?.LookupParameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.Value as string ?? "-";
        var mid = At(start, center, 0);
        var left = At(mid, -tip, 0);
        var right = At(end, tailWidth, 0);
        bool vertical = line.Count > 2 && Math.Abs(ux) < 1e-8;
        if (line.Count == 2)
            page.DrawLine(left.X, left.Y, right.X, right.Y, pen);
        else
        {
            page.DrawPolyline(line, pen, false);
            var top = At(mid, tip, 0);
            page.DrawLine(top.X, top.Y, start.X, start.Y, pen);
            if (vertical) page.DrawLine(mid.X - radius, mid.Y, mid.X + radius, mid.Y, pen);
            else page.DrawLine(left.X, left.Y, start.X, start.Y, pen);
        }
        page.DrawPolyline([end, right, At(end, tailWidth, tailHeight), At(end, 0, tailHeight)], pen, true, 0);

        // The family is the portion of a right isosceles triangle outside its circular opening.
        for (int eighth = 0; eighth < 4; eighth++)
        {
            var corner = eighth switch
            {
                0 => At(mid, tip, 0),
                3 => At(mid, -tip, 0),
                _ => At(mid, 0, tip),
            };
            var wing = new List<(double X, double Y)>(5) { corner };
            for (int i = 0; i <= 3; i++)
            {
                double theta = (3 * eighth + i) * Math.PI / 12;
                wing.Add(At(mid, radius * Math.Cos(theta), radius * Math.Sin(theta)));
            }
            page.DrawPolyline(wing, pen, true, 0);
        }
        page.DrawPolyline(ElevationPrinter.DatumCircle(mid.X, mid.Y, radius, 25), pen, true);
        DrawLabel(detail, detailNumber);
        DrawLabel(sheet, number);
        return true;

        void DrawLabel(RvtIO.Parsing.TfBlockDeserializer.TitleBlockLabel label, string value)
        {
            double ox = label.Ox * FeetToMillimetres, oy = label.Oy * FeetToMillimetres;
            var position = vertical ? (X: mid.X + ox - center, Y: mid.Y + oy) : At(start, ox, oy);
            double height = label.Height * FeetToMillimetres;
            double cap = document.RawData.TitleBlockCapHeightsFeet?.GetValueOrDefault(label.AttrId) ?? 0;
            bool opaque = document.RawData.OpaqueSymbolLabelStyles?.Contains(label.AttrId) == true;
            bool arial = document.RawData.RegularArialSymbolLabelStyles?.Contains(label.AttrId) == true;
            bool flip = ux < -1e-8 || (Math.Abs(ux) <= 1e-8 && uy < 0);
            double bx = vertical ? 1 : flip ? -ux : ux, by = vertical ? 0 : flip ? -uy : uy;
            SheetComposer.DrawTitleBlockText(page, [label.Prefix + value + label.Suffix], position.X, position.Y,
                SheetComposer.SymbolLabelTextSize(font, height, cap), height, label.Flags, bx, by, -by, bx, opaque, arial);
        }
    }
}
