using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    private static Dictionary<int, List<RevitFileInfo.PlanNote>> ReadPlanNotes(ParsingState state)
    {
        var byView = new Dictionary<int, List<RevitFileInfo.PlanNote>>();
        foreach (var (id, note) in state.NativeParams)
        {
            var fields = note.Fields;
            var numbers = note.DoubleFields;
            // shortcut: the supported left-aligned horizontal note layout.
            if (note.PlanNoteText is not { Text.Length: > 0, FontSizePoints: > 0 and <= 200 } text
                || fields.GetValueOrDefault("m_id") != (int)id
                || fields.GetValueOrDefault("m_ownerDBViewId") is not > 0
                || fields.GetValueOrDefault("3928:m_flags") != 8768
                || numbers.GetValueOrDefault("3928:m_baseVec@0") != 1 || numbers.GetValueOrDefault("3928:m_baseVec@1") != 0
                || numbers.GetValueOrDefault("3928:m_upVec@0") != 0 || numbers.GetValueOrDefault("3928:m_upVec@1") != 1
                || fields.GetValueOrDefault("3928:m_attrId") is not > 0
                || !state.NativeParams.TryGetValue((uint)fields["3928:m_attrId"], out var style)) continue;
            double x = numbers.GetValueOrDefault("3928:m_orig@0", double.NaN);
            double y = numbers.GetValueOrDefault("3928:m_orig@1", double.NaN);
            double width = numbers.GetValueOrDefault("3928:m_lineWidth");
            double height = numbers.GetValueOrDefault("3928:m_height");
            double padding = style.DoubleFields.GetValueOrDefault("3931:m_leaderOffsetSheet", double.NaN);
            int view = fields["m_ownerDBViewId"], category = style.Fields.GetValueOrDefault("625:m_categoryId");
            if (!(Math.Abs(x) < 1_000_000 && Math.Abs(y) < 1_000_000) || !(width is > 0 and < 20)
                || !(height is > 0 and < 20) || !(padding is >= 0 and < 1) || category <= 0) continue;
            double arrowSize = 0, arrowAngle = 0;
            var arrowId = style.ElementIds.Find(entry => entry.ParamId == (int)BuiltInParameter.LEADER_ARROWHEAD).Value;
            if (arrowId > 0 && state.NativeParams.TryGetValue((uint)arrowId, out var arrow)
                && arrow.Fields.GetValueOrDefault("2295:m_tickType") == 8)
            {
                double size = PlanAnnotationDouble(arrow, BuiltInParameter.ARROW_SIZE);
                double angle = PlanAnnotationDouble(arrow, BuiltInParameter.LEADER_ARROW_WIDTH);
                if (size is > 0 and < 1 && angle is > 0 and < Math.PI)
                {
                    arrowSize = size;
                    arrowAngle = angle;
                }
            }
            if (!byView.TryGetValue(view, out var notes)) byView.Add(view, notes = new());
            notes.Add(new(text.Text, text.FontSizePoints!.Value, x, y, width, height,
                fields.GetValueOrDefault("3928:m_bNoWrapping") == 0,
                style.Fields.GetValueOrDefault("3931:m_textBoxVisibility") == 1,
                style.Fields.GetValueOrDefault("625:m_background", -1) == 0, padding, category,
                fields.GetValueOrDefault("3971:m_bArcLeaders") == 1, note.PlanNoteLeaders ?? [],
                arrowSize, arrowAngle, (int)id));
        }
        return byView;
    }
}
