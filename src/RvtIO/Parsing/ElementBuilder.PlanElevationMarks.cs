using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    internal static List<RevitFileInfo.PlanElevationMark> ReadPlanElevationMarks(ParsingState state)
    {
        var result = new List<RevitFileInfo.PlanElevationMark>();
        foreach (var (markerId, marker) in state.NativeParams)
        {
            if (state.ElemClassIdx.GetValueOrDefault(markerId) != 2247
                || !marker.DoubleFields.TryGetValue("2247:m_pos@0", out double x) || !double.IsFinite(x)
                || !marker.DoubleFields.TryGetValue("2247:m_pos@1", out double y) || !double.IsFinite(y)
                || !state.NativeParams.TryGetValue((uint)marker.Fields.GetValueOrDefault("2247:m_symbolId"), out var type)
                || !state.NativeParams.TryGetValue((uint)type.Fields.GetValueOrDefault("1020:m_elevatnAttrId"), out var style)
                || style.Fields.GetValueOrDefault("2251:m_shape") != 1
                || style.DoubleFields.GetValueOrDefault("2251:m_ahAngleDouble") is not (> 0 and < Math.PI / 2 and var angle)
                || style.Fields.GetValueOrDefault("2251:m_elevationSymbolId") is not (> 0 and var bodyId)
                || !state.NativeParams.TryGetValue((uint)bodyId, out var body))
                continue;

            // shortcut: one target view per marker, until the shared body label for multiple targets is decoded.
            int arrowIndex = -1;
            for (int index = 0; index < 4; index++)
            {
                if (marker.Fields.GetValueOrDefault($"2247:m_arrowIds@{index}") <= 0) continue;
                if (arrowIndex >= 0)
                {
                    arrowIndex = -1;
                    break;
                }
                arrowIndex = index;
            }
            if (arrowIndex < 0
                || marker.Fields.GetValueOrDefault($"2247:m_arrowIds@{arrowIndex}") is not (> 0 and var arrowId)
                || !state.NativeParams.TryGetValue((uint)arrowId, out var arrow)
                || arrow.Fields.GetValueOrDefault("2248:m_parentId") != (int)markerId
                || arrow.Fields.GetValueOrDefault("621:m_dbViewId") is not (> 0 and var viewId)
                || !arrow.DoubleFields.TryGetValue("2249:m_coarserScale", out double coarserScale)
                || coarserScale is not (>= 0 and <= 1)
                || !style.Fields.TryGetValue($"2251:m_orderedSubIndices@{arrowIndex}", out int child)
                || body.Fields.GetValueOrDefault($"1829:m_subInstData@{child}") is not (> 0 and var symbolId))
                continue;
            result.Add(new((int)markerId, x, y, bodyId, angle, style.Fields.GetValueOrDefault("2251:m_ahFilled") == 1,
                arrowId, viewId, symbolId, coarserScale));
        }
        return result;
    }
}
