using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    internal static Dictionary<int, List<RevitFileInfo.PlanStairPath>> ReadPlanStairPaths(ParsingState state)
    {
        var byView = new Dictionary<int, List<RevitFileInfo.PlanStairPath>>();
        foreach (var (id, path) in state.NativeParams)
        {
            var fields = path.Fields;
            // shortcut: automatic direction with an open arrow at the riser and no start extension.
            if (fields.GetValueOrDefault("3702:m_stairsLinkElementId") is not (> 0 and var host)
                || id > int.MaxValue || fields.GetValueOrDefault("m_id") != (int)id
                || fields.GetValueOrDefault("m_ownerDBViewId") is not (> 0 and var view)
                || path.DoubleFields.GetValueOrDefault("3702:m_stairsPathOffset", double.NaN) != 0
                || fields.GetValueOrDefault("3702:m_typeId") is not (> 0 and var typeId)
                || !state.NativeParams.TryGetValue((uint)typeId, out var type)
                || type.Fields.GetValueOrDefault("3703:m_stairsPathDirection") != 1
                || type.Fields.GetValueOrDefault("3703:m_startFromRiser") != 1
                || type.Fields.GetValueOrDefault("3703:m_endAtRiser") != 1
                || type.Fields.GetValueOrDefault("3703:m_startSymbolTypeId") != -1
                || type.Fields.GetValueOrDefault("3703:m_fullStepArrow", -1) != 0
                || type.DoubleFields.GetValueOrDefault("3703:m_startExtension", double.NaN) != 0
                || type.Fields.GetValueOrDefault("3703:m_arrowheadTypeId") is not (> 0 and var arrowId)
                || !state.NativeParams.TryGetValue((uint)arrowId, out var arrow)
                || arrow.Fields.GetValueOrDefault("2295:m_tickType") != 8
                || arrow.Fields.GetValueOrDefault("2295:m_arrowClosed", -1) != 0
                || arrow.Fields.GetValueOrDefault("2295:m_arrowFilled", -1) != 0)
                continue;

            double size = PlanAnnotationDouble(arrow, BuiltInParameter.ARROW_SIZE);
            double angle = PlanAnnotationDouble(arrow, BuiltInParameter.LEADER_ARROW_WIDTH);
            if (!(size is > 0 and < 1 && angle is > 0 and < Math.PI)) continue;
            string? text = null;
            double cap = 0, textX = 0, textY = 0;
            if (fields.GetValueOrDefault("3702:m_showDownText") == 1
                && fields.GetValueOrDefault("3702:m_textOrientation", -1) == 0
                && path.StringFields.GetValueOrDefault("3702:m_downText") is { Length: > 0 } label
                && type.Fields.GetValueOrDefault("3703:m_textTypeId") is > 0 and var textId
                && state.NativeParams.TryGetValue((uint)textId, out var textType)
                && textType.Fields.GetValueOrDefault("625:m_fontId") is > 0 and var fontId
                && textType.Fields.GetValueOrDefault("625:m_bBold", -1) == 0
                && textType.Fields.GetValueOrDefault("625:m_bItalic", -1) == 0
                && textType.Fields.GetValueOrDefault("625:m_bUnderline") == 0
                && state.NativeParams.TryGetValue((uint)fontId, out var font)
                && string.Equals(font.StringFields.GetValueOrDefault("1923:m_name"), "Arial", StringComparison.OrdinalIgnoreCase)
                && font.DoubleFields.GetValueOrDefault("1923:m_size") is > 0 and < 1 and var height)
            {
                textX = path.DoubleFields.GetValueOrDefault("3702:m_downTextOffset@0", double.NaN);
                textY = path.DoubleFields.GetValueOrDefault("3702:m_downTextOffset@1", double.NaN);
                if (double.IsFinite(textX) && double.IsFinite(textY))
                {
                    text = label;
                    cap = height;
                }
            }
            if (!byView.TryGetValue(view, out var paths)) byView.Add(view, paths = []);
            paths.Add(new((int)id, host, size, angle, text, cap, textX, textY));
        }
        return byView;
    }
}
