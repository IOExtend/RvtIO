using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    private static Dictionary<int, IReadOnlyDictionary<string, uint>> BuildViewRoomColors(RevitFileInfo info,
        ParsingState state)
    {
        var result = new Dictionary<int, IReadOnlyDictionary<string, uint>>();
        if (info.FillPatterns == null) return result;
        foreach (var (viewId, view) in state.NativeParams)
        {
            if (view.Fields.GetValueOrDefault("693:m_bBackground") != 1
                || !view.Fields.TryGetValue("693:m_roomColorScheme", out int schemeId) || schemeId <= 0
                || !state.NativeParams.TryGetValue((uint)schemeId, out var scheme)
                || scheme.RoomColors is not { Count: > 0 } entries
                || scheme.Fields.GetValueOrDefault("690:m_categoryId") != (int)BuiltInCategory.OST_Rooms
                || scheme.Fields.GetValueOrDefault("690:m_paramId") != (int)BuiltInParameter.ROOM_NAME
                || scheme.Fields.GetValueOrDefault("690:m_bByRange", -1) != 0) continue;
            var colors = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var entry in entries)
                if (info.FillPatterns.TryGetValue(entry.PatternId, out var pattern) && pattern.Grids.Count == 0)
                    colors.TryAdd(entry.Name, entry.Color);
            if (colors.Count > 0) result.Add((int)viewId, colors);
        }
        return result;
    }
}
