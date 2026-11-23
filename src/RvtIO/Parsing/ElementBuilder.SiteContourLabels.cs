using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    private static Dictionary<int, List<RevitFileInfo.SiteContourLabel>> BuildSiteContourLabels(ParsingState state)
    {
        var labels = new Dictionary<int, List<RevitFileInfo.SiteContourLabel>>();
        foreach (var (id, element) in state.NativeParams)
        {
            if (element.SiteContourLabels is not { } data
                || element.Fields.GetValueOrDefault("m_id") != id
                || element.Fields.GetValueOrDefault("m_ownerDBViewId") is not (> 0 and var view)
                || !state.NativeParams.TryGetValue((uint)data.SurfaceId, out var surface) || surface.SiteContours == null
                || element.Fields.GetValueOrDefault("m_attrId") is not (> 0 and var styleId)
                || !state.NativeParams.TryGetValue((uint)styleId, out var style)) continue;

            // shortcut: plain labels using the internal elevation and project length format.
            var fields = style.Fields;
            if (fields.GetValueOrDefault("836:m_baseElevation", -1) != 0
                || fields.GetValueOrDefault("84:m_bUseDefault") != 1
                || fields.GetValueOrDefault("625:m_bBold") != 0 || fields.GetValueOrDefault("625:m_bItalic") != 0
                || fields.GetValueOrDefault("625:m_bUnderline") != 0
                || fields.GetValueOrDefault("625:m_fontId") is not (> 0 and var fontId)
                || !state.NativeParams.TryGetValue((uint)fontId, out var font)
                || font.DoubleFields.GetValueOrDefault("1923:m_size") is not (> 0 and < 1 and var size)) continue;

            if (!labels.TryGetValue(view, out var group)) labels.Add(view, group = []);
            for (int i = 0; i < data.Positions.Length; i++)
                group.Add(new((int)id, data.SurfaceId, data.Positions[i], data.Directions[i], size));
        }
        return labels;
    }
}
