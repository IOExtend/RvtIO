using System.Globalization;
using RvtIO.BuiltIns;
using RvtIO.Compact;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    private static bool DrawPlanRoomTags(PdfPage page, RvtDocument? document, int? viewId,
        Func<double, double> toX, Func<double, double> toY)
    {
        if (document == null || viewId is not int view || page.Owner.Font is not { } font
            || document.RawData.PlanRoomTags?.GetValueOrDefault(view) is not { Count: > 0 } tags
            || document.RawData.PlanRoomTagStyles is not { Count: > 0 } styles) return false;

        double pen = PlanAnnotationWidth(document, (int)BuiltInCategory.OST_RoomTags, 0.1);
        bool drawn = false;
        foreach (var tag in tags)
        {
            if (document.GetElement(tag.RoomId) is not { CategoryId: (int)BuiltInCategory.OST_Rooms } room
                || !styles.TryGetValue(tag.SymbolId, out var style)) continue;
            double x = toX(tag.X * FeetToMillimetres), y = toY(tag.Y * FeetToMillimetres);
            foreach (var line in style.Lines)
                page.DrawLine(x + line.X0 * FeetToMillimetres, y + line.Y0 * FeetToMillimetres,
                    x + line.X1 * FeetToMillimetres, y + line.Y1 * FeetToMillimetres, pen);
            foreach (var (label, sizePoints) in style.Labels)
            {
                string? value = RoomTagValue(document, room, label.ParamId);
                if (string.IsNullOrEmpty(value)) continue;
                var (labelX, labelBottom, baseline, size, alignment) = AnnotationLabelLayout(font, label, sizePoints, x, y);
                if (!(size is > 0 and < 100)) continue;
                // shortcut: vertical single-label attachment only, extend after verifying the other saved attachment modes.
                if (style.Labels.Count == 1 && alignment == PdfTextAlignment.Center
                    && tag.LeaderAnchor is { } anchor && tag.LeaderEnd is { } end
                    && Math.Abs(end.X - anchor.X) < 1e-8
                    && Math.Abs(toX(anchor.X * FeetToMillimetres) - labelX) < 1e-6
                    && Math.Abs(toY(anchor.Y * FeetToMillimetres) - labelBottom) < 1e-6)
                {
                    double endY = toY(end.Y * FeetToMillimetres);
                    double attachment = endY > labelBottom ? baseline + size * font.Ascent / font.UnitsPerEm : labelBottom;
                    page.DrawLine(toX(end.X * FeetToMillimetres), endY, x, attachment, pen);
                }
                page.DrawText(label.Prefix + value + label.Suffix, labelX, baseline, size, alignment);
                drawn = true;
            }
        }
        return drawn;
    }

    internal static (double X, double Bottom, double Baseline, double Size, PdfTextAlignment Alignment)
        AnnotationLabelLayout(PdfTrueTypeFont font, TfBlockDeserializer.TitleBlockLabel label, double sizePoints, double x, double y)
    {
        const double pointsToMm = 25.4 / 72;
        double size = sizePoints > 0 ? sizePoints * pointsToMm
            : label.Height * FeetToMillimetres * font.UnitsPerEm / (font.Ascent - font.Descent);
        var alignment = (label.Flags & 0x80) != 0 ? PdfTextAlignment.Center
            : (label.Flags & 0x100) != 0 ? PdfTextAlignment.Right : PdfTextAlignment.Left;
        double labelBottom = y + label.Oy * FeetToMillimetres;
        if ((label.Flags & 0x400) != 0) labelBottom -= label.Height * FeetToMillimetres / 2;
        return (x + label.Ox * FeetToMillimetres, labelBottom,
            labelBottom - size * font.Descent / font.UnitsPerEm, size, alignment);
    }

    private static string LocalizeRoomArea(string value) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
            && value.EndsWith("m²", StringComparison.Ordinal) ? value[..^2] + "м²" : value;

    private static string? RoomTagValue(RvtDocument document, Element room, int parameter)
    {
        var value = room.LookupParameter((BuiltInParameter)parameter)?.Value;
        if (parameter == (int)BuiltInParameter.ROOM_NAME) return value as string ?? room.Name;
        if (parameter == (int)BuiltInParameter.ROOM_NUMBER) return value as string;
        if (parameter == (int)BuiltInParameter.ROOM_AREA && document.RawData.SerializedFormatSettings is { } settings)
        {
            var area = value as double? ?? room.SerializedRoomPlanAreaSquareFeet
                ?? SolidQuantities.RoomPlanArea(room.SerializedSpaceBoundaryCurrent);
            if (area is > 0 and < double.PositiveInfinity
                && CompactValueFormatter.Format(area.Value, "autodesk.spec.aec:area", settings) is { } formatted)
                return LocalizeRoomArea(formatted);
        }
        return null;
    }
}
