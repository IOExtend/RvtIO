using System.Globalization;
using RvtIO.BuiltIns;
using RvtIO.Compact;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class SitePrinter
{
    internal static void DrawPropertyTags(PdfPage page, RvtDocument document, int viewId, ViewCropFrame? basis,
        SiteContent site, double scale, double originX, double originY)
    {
        var raw = document.RawData;
        // shortcut: quadrant bearings in degrees, minutes and seconds using the resolved active location.
        if (raw.PropertyBearingFormat != (1, 1) || !raw.SharedTransformIsActiveLocation
            || raw.SharedTransform is not { Length: 12 } shared
            || raw.SitePropertyTags?.GetValueOrDefault(viewId) is not { } tags
            || raw.SitePropertyTagStyles is not { } styles
            || raw.SerializedFormatSettings is not { } formatting || page.Owner.Font is not { } font) return;

        var hidden = raw.ViewHiddenElements?.GetValueOrDefault(viewId);
        bool russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
        foreach (var tag in tags)
        {
            if (hidden?.Contains(tag.ElementId) == true || !styles.TryGetValue(tag.SymbolId, out var labels)
                || document.GetElement(tag.ElementId) is not { SitePropertyCurve: { Count: >= 2 } curve } segment)
                continue;
            var a = curve[0];
            var b = curve[^1];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (!(distance > 0) || !double.IsFinite(distance)) continue;

            double east = dx * shared[0] + dy * shared[3], north = dx * shared[1] + dy * shared[4];
            var (ns, bearing, ew) = PropertyBearing(east, north, russian);
            var tangent = segment.SitePropertyArcCenter is { } center
                ? (X: center.Y - tag.Y, Y: tag.X - center.X) : (X: dx, Y: dy);
            if (curve.Count > 2 && segment.SitePropertyArcCenter == null) continue;
            var (tx, ty) = InView(tangent.X, tangent.Y);
            if (tx == 0 && ty == 0) continue;
            if (tx < 0 || (tx == 0 && ty < 0)) { tx = -tx; ty = -ty; }
            double angle = Math.Atan2(ty, tx), cos = Math.Cos(angle), sin = Math.Sin(angle);

            var (x, y) = InView(tag.X, tag.Y);
            double hx = originX + (x * FeetToMillimetres - site.MinX) / scale;
            double hy = originY + (y * FeetToMillimetres - site.MinY) / scale;
            foreach (var (label, fontPoints) in labels)
            {
                string? value = (BuiltInParameter)label.ParamId switch
                {
                    BuiltInParameter.PROPERTY_SEGMENT_N_S => ns,
                    BuiltInParameter.PROPERTY_SEGMENT_BEARING => bearing,
                    BuiltInParameter.PROPERTY_SEGMENT_E_W => ew,
                    BuiltInParameter.PROPERTY_SEGMENT_DISTANCE =>
                        PropertyDistance(distance, label.SingleParameterFormat, formatting, russian),
                    _ => null,
                };
                if (string.IsNullOrEmpty(value)) continue;
                var layout = PlanPrinter.AnnotationLabelLayout(font, label, fontPoints, 0, 0);
                double px = hx + layout.X * cos - layout.Baseline * sin;
                double py = hy + layout.X * sin + layout.Baseline * cos;
                page.DrawTextRotated(label.Prefix + value + label.Suffix, px, py,
                    layout.Size, angle * 180 / Math.PI, layout.Alignment);
            }
        }

        (double X, double Y) InView(double x, double y) => basis == null ? (x, y)
            : (x * basis.Right[0] + y * basis.Right[1], x * basis.Up[0] + y * basis.Up[1]);
    }

    internal static (string NorthSouth, string Bearing, string EastWest) PropertyBearing(
        double east, double north, bool russian)
    {
        string ns = north < 0 ? (russian ? "Ю" : "S") : (russian ? "С" : "N");
        string ew = east < 0 ? (russian ? "З" : "W") : (russian ? "В" : "E");
        int seconds = (int)Math.Round(Math.Atan2(Math.Abs(east), Math.Abs(north)) * (648000 / Math.PI),
            MidpointRounding.AwayFromZero);
        return (ns, FormattableString.Invariant($"{seconds / 3600}° {seconds / 60 % 60:00}' {seconds % 60:00}\""), ew);
    }

    private static string? PropertyDistance(double feet, TfBlockDeserializer.UnitFormatOption? option,
        CompactFormatSettings formatting, bool russian)
    {
        if (option is not { UseDefault: 0 }) return null;
        if (russian && option.Symbol == "autodesk.unit.symbol:footSingleQuote-1.0.1"
            && option.Unit == "autodesk.unit.unit:feet-1.0.1")
            return CompactValueFormatter.Format(feet, option with { Symbol = "" }, formatting) is { } value
                ? value + "фут" : null;
        return CompactValueFormatter.Format(feet, option, formatting);
    }
}
