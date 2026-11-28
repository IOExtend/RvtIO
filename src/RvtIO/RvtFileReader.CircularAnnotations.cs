using System.Text;
using System.Text.RegularExpressions;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO;

public partial class RvtFileReader
{
    private static void ReadCircularAnnotations(RevitFileInfo info, ParsingState state,
        IReadOnlyList<PartitionFraming.Framed?> frames)
    {
        var placed = info.Elements.Where(e => e.CategoryId == (int)BuiltInCategory.OST_GenericAnnotation
            && e.OwnerViewId is > 0 && e.TypeId > 0 && e.RootClassName == "FamilyInstance").ToList();
        var keys = new Dictionary<int, string>();
        foreach (int id in placed.Select(e => e.TypeId).Distinct())
        {
            if (state.TitleBlockTexts.GetValueOrDefault((uint)id) is not { BlockCount: 1, Texts.Count: 1 }
                || state.NativeParams.GetValueOrDefault((uint)id)?.Fields.GetValueOrDefault("1829:m_familyId")
                    is not (> 0 and int family)) continue;
            if (state.NativeParams.GetValueOrDefault((uint)family)?.StringFields.GetValueOrDefault("1740:contentDoc") is { } key)
                keys.Add(id, key);
        }
        if (keys.Count == 0) return;

        var members = keys.Values.Distinct().ToDictionary(key => key,
            _ => new Dictionary<int, (byte[] Data, int Start, uint Size, ushort ClassId)>(), StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            if (frame?.FamilyAnnotationItems is not { } items) continue;
            foreach (var (key, item) in items)
            {
                if (item.Owner > int.MaxValue || !members.TryGetValue(key, out var group)
                    || group.ContainsKey((int)item.Owner)) continue;
                var (data, start, _) = frame.Bytes(item);
                group.Add((int)item.Owner, (data, start, BitConverter.ToUInt32(data, start + 8),
                    BitConverter.ToUInt16(data, start + 12)));
            }
        }

        var styles = new Dictionary<int, RevitFileInfo.CircularAnnotationStyle>();
        foreach (var (symbol, key) in keys)
        {
            var group = members[key];
            var regions = group.Values.Where(r => r.ClassId == 1860).ToArray();
            var curves = group.Values.Where(r => r.ClassId == 838).ToArray();
            var text = state.TitleBlockTexts[(uint)symbol].Texts![0];
            if (regions.Length != 1 || curves.Length != 1 || (text.Bx, text.By, text.Hx, text.Hy) != (1d, 0d, 0d, 1d))
                continue;
            var region = regions[0];
            var fields = TfBlockDeserializer.ExtractParams(region.Data, region.Start, region.Size, region.ClassId)?.Fields;
            if (fields == null || fields.GetValueOrDefault("1860:m_bMasking", -1) != 0
                || fields.GetValueOrDefault("1860:m_attrId") is not (> 0 and int attributeId)
                || !group.TryGetValue(attributeId, out var attribute) || attribute.ClassId != 1862) continue;
            var attr = TfBlockDeserializer.ExtractParams(attribute.Data, attribute.Start, attribute.Size, attribute.ClassId)?.Fields;
            if (attr == null || attr.GetValueOrDefault("1862:m_backgroundPatternElemId") != -1
                || info.FillPatterns?.GetValueOrDefault(attr.GetValueOrDefault("1862:m_fillPatternElemId")).Grids is not { Count: 0 }
                || attr.GetValueOrDefault("625:m_categoryId") is not (> 0 and int category)) continue;

            var curve = curves[0];
            if (TfBlockDeserializer.ReadFamilyAnnotationCurve(curve.Data, curve.Start, curve.Size)
                is not { Arc: { } arc, TypeDependent: false, SketchId: > 0 } outline
                || fields.GetValueOrDefault("1861:m_sketchId") != outline.SketchId
                || Math.Abs(Math.Abs(arc.End - arc.Start) - 360) > 1e-6
                || info.GraphicsStylePens?.GetValueOrDefault(arc.StyleId) is not { PenNumber: > 0, LinePatternId: -3000010 } border
                || border.Color is < 0 or > 0xFFFFFF
                || RtfText.Parse(text.Rtf, 0, text.Rtf.Length) is not { Text.Length: > 0, FontSizePoints: > 0 and <= 200 } caption
                || !TryCircularAnnotationTextStyle(text.Rtf, out int ink, out bool bold)) continue;

            var fills = new List<int>();
            foreach (var style in group.Values.Where(m => m.ClassId == 2050))
                if (TfBlockDeserializer.TryExtractGraphicsStyleFull(style.Data, style.Start, style.Size, style.ClassId,
                        out int styleCategory, out _, out int type, out _, out int color, out _, out _)
                    && styleCategory == category && type == 1 && color is >= 0 and <= 0xFFFFFF)
                    fills.Add(color);
            if (fills is [int fill])
                styles.Add(symbol, new(arc, fill, border.Color, border.PenNumber,
                    text, caption.Text, caption.FontSizePoints.Value, ink, bold));
        }

        var annotations = new Dictionary<int, List<RevitFileInfo.CircularAnnotation>>();
        foreach (var element in placed)
        {
            int view = element.OwnerViewId!.Value;
            if (!styles.TryGetValue(element.TypeId, out var style)
                || info.ViewHiddenElements?.GetValueOrDefault(view)?.Contains(element.Id) == true
                || !state.ComputedFamilySymbols.TryGetValue((uint)element.Id, out var instance)
                || instance is not { BlockCount: 1, Symbol: { Transform: { Length: 12 } transform } computed }
                || computed.SymbolId != element.TypeId) continue;
            if (!annotations.TryGetValue(view, out var list)) annotations.Add(view, list = []);
            list.Add(new(transform, style));
        }
        info.CircularAnnotations = annotations;
    }

    private static bool TryCircularAnnotationTextStyle(byte[] rtf, out int color, out bool bold)
    {
        color = 0;
        bold = false;
        // shortcut: a single horizontal run, extend when filled-region families need richer text.
        string text = Encoding.ASCII.GetString(rtf);
        var run = Regex.Match(text,
            @"\\pard(?<style>(?:\s*\\(?:q[lcr]|cf\d+|fs\d+|f\d+|b[01]?)(?![a-zA-Z0-9]))+)\s+[^{}\\\r\n]+\\par\s*\}\s*\x00*$");
        if (!run.Success) return false;
        var controls = run.Groups["style"].Value;
        foreach (Match m in Regex.Matches(controls, @"\\b([01])?(?![a-zA-Z0-9])"))
            bold = m.Groups[1].Value != "0";
        var cf = Regex.Matches(controls, @"\\cf(\d+)");
        if (cf.Count == 0) return true;
        if (!int.TryParse(cf[^1].Groups[1].Value, out int index)) return false;
        if (index == 0) return true;
        var table = Regex.Match(text, @"\{\\colortbl([^{}]*)\}");
        string[] entries = table.Groups[1].Value.Split(';');
        if (index >= entries.Length) return false;
        var rgb = Regex.Match(entries[index], @"^\s*\\red(\d+)\\green(\d+)\\blue(\d+)\s*$");
        if (!rgb.Success || !byte.TryParse(rgb.Groups[1].Value, out byte r)
            || !byte.TryParse(rgb.Groups[2].Value, out byte g) || !byte.TryParse(rgb.Groups[3].Value, out byte b))
            return false;
        color = r | g << 8 | b << 16;
        return true;
    }
}
