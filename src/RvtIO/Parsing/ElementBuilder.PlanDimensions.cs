using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    internal static void ReadPlanDimensions(RevitFileInfo info, ParsingState state)
    {
        var byView = new Dictionary<int, List<RevitFileInfo.PlanDimension>>();
        var styles = new Dictionary<int, RevitFileInfo.PlanDimensionStyle>();
        foreach (var (id, saved) in state.NativeParams)
        {
            if (saved is not { DimensionLine: { } line, DimensionReferences: { Count: >= 2 } references,
                    DimensionValues: { } values }
                || saved.Fields.GetValueOrDefault("m_id") != (int)id
                || saved.Fields.GetValueOrDefault("m_ownerDBViewId") is not > 0
                || saved.Fields.GetValueOrDefault("m_styleSymbolId") is not > 0
                || saved.Fields.GetValueOrDefault("160:m_flags") != 12
                || saved.Fields.GetValueOrDefault("160:m_useEqualityFormula") != 0
                || saved.Fields.GetValueOrDefault("160:m_showLabelGP") != 0
                || saved.Fields.GetValueOrDefault("159:m_multiReferenceAnnotationId") > 0
                || saved.CellClasses is { Count: > 0 }
                || values.Count != references.Count - 1 || values.Any(value => !(value > 0 && double.IsFinite(value)))
                || !line.Origin.All(value => Math.Abs(value) < 1_000_000) || !line.Direction.All(double.IsFinite)
                || references.Any(point => point.Any(value => !(Math.Abs(value) < 1_000_000)))) continue;
            int view = saved.Fields["m_ownerDBViewId"], style = saved.Fields["m_styleSymbolId"];
            if (!styles.ContainsKey(style))
            {
                var decoded = ReadStyle(style);
                if (decoded == null) continue;
                styles.Add(style, decoded);
            }

            if (!byView.TryGetValue(view, out var dimensions)) byView.Add(view, dimensions = new());
            dimensions.Add(new(style, line.Origin, line.Direction, references, values));
        }
        info.PlanDimensions = byView;
        info.PlanDimensionStyles = styles;

        RevitFileInfo.PlanDimensionStyle? ReadStyle(int id)
        {
            if (!state.NativeParams.TryGetValue((uint)id, out var style)) return null;
            var fields = style.Fields;
            int fontId = fields.GetValueOrDefault("625:m_fontId");
            int tickId = fields.GetValueOrDefault("174:m_arrowHeadStyleId");
            int category = fields.GetValueOrDefault("625:m_categoryId");
            int tickCategory = fields.GetValueOrDefault("174:m_tickCategoryId");
            // shortcut: continuous dimensions with one diagonal tick and the project length format.
            if (fields.GetValueOrDefault("174:m_dimensionStyleType", -1) != 0
                || fields.GetValueOrDefault("174:m_linearDimType") != 1
                || fields.GetValueOrDefault("174:m_textAlignment") != 1
                || fields.GetValueOrDefault("174:m_textLoc", -1) != 0
                || fields.GetValueOrDefault("174:m_alternateUnits", -1) != 0
                || fields.GetValueOrDefault("174:m_dimWitnCntrlExtBelow", -1) != 0
                || fields.GetValueOrDefault("174:linearFormatUsesDefault") != 1
                || !string.IsNullOrEmpty(style.StringFields.GetValueOrDefault("174:m_prefix"))
                || !string.IsNullOrEmpty(style.StringFields.GetValueOrDefault("174:m_suffix"))
                || category <= 0 || tickCategory <= 0 || fontId <= 0 || tickId <= 0
                || fields.GetValueOrDefault("174:m_interiorTickMarkStyleId") != tickId
                || !state.NativeParams.TryGetValue((uint)fontId, out var font)
                || !state.NativeParams.TryGetValue((uint)tickId, out var tick)
                || tick.Fields.GetValueOrDefault("2295:m_tickType", -1) != 0) return null;
            double height = font.DoubleFields.GetValueOrDefault("1923:m_size");
            double size = PlanAnnotationDouble(tick, BuiltInParameter.ARROW_SIZE);
            double extension = PlanAnnotationDouble(style, BuiltInParameter.DIM_LINE_EXTENSION);
            double witness = PlanAnnotationDouble(style, BuiltInParameter.WITNS_LINE_EXTENSION);
            double gap = PlanAnnotationDouble(style, BuiltInParameter.WITNS_LINE_GAP_TO_ELT);
            double textGap = PlanAnnotationDouble(style, BuiltInParameter.TEXT_DIST_TO_LINE);
            return height is > 0 and < 1 && size is > 0 and < 1 && extension is >= 0 and < 1
                    && witness is >= 0 and < 1 && gap is >= 0 and < 1 && textGap is >= 0 and < 1
                ? new(height, category, tickCategory, extension, witness, gap, textGap, size) : null;
        }
    }

    private static double PlanAnnotationDouble(TfBlockDeserializer.NativeParams item, BuiltInParameter parameter)
    {
        foreach (var (id, value) in item.Doubles)
            if (id == (int)parameter) return value;
        return double.NaN;
    }
}
