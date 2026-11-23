using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO;

public partial class RvtFileReader
{
    private static void ReadFamilyAnnotations(RevitFileInfo info, ParsingState state,
        IReadOnlyList<PartitionFraming.Framed?> frames)
    {
        var tags = new Dictionary<int, List<RevitFileInfo.PlanRoomTag>>();
        var openings = new Dictionary<int, List<RevitFileInfo.PlanOpeningTag>>();
        var openingSymbols = new HashSet<int>();
        var plantingTags = new Dictionary<int, List<RevitFileInfo.SitePlantingTag>>();
        var plantingSymbols = new HashSet<int>();
        var propertyTags = new Dictionary<int, List<RevitFileInfo.PlanOpeningTag>>();
        var propertySymbols = new HashSet<int>();
        var propertyTargets = info.Elements.Where(element => element.SitePropertyCurve is { Count: >= 2 })
            .Select(element => element.Id).ToHashSet();
        var plantingTargets = info.Elements.Where(e => e.CategoryId == (int)BuiltInCategory.OST_Planting)
            .Select(e => e.Id).ToHashSet();
        var openingTargets = info.Elements.Where(element =>
                element.CategoryId is (int)BuiltInCategory.OST_Doors or (int)BuiltInCategory.OST_Windows)
            .Select(element => element.Id).ToHashSet();
        var symbols = new HashSet<int>();
        var viewportTitles = new Dictionary<int, RevitFileInfo.ViewportTitle>();
        var titleSymbols = new HashSet<int>();
        foreach (var (id, native) in state.NativeParams)
        {
            var fields = native.Fields;
            if (fields.GetValueOrDefault("m_id") != (int)id) continue;
            if (fields.GetValueOrDefault("1990:m_attrId") is > 0 and int attributesId
                && state.NativeParams.TryGetValue((uint)attributesId, out var attributes)
                && attributes.Fields.GetValueOrDefault("m_id") == attributesId
                && attributes.Fields.TryGetValue("4124:m_showLabel", out int showLabel)
                && showLabel is 0 or 1
                && attributes.Fields.TryGetValue("4124:m_showExtensionLine", out int showLine)
                && showLine is 0 or 1)
            {
                int titleSymbol = attributes.Fields.GetValueOrDefault("4124:m_familyTagId");
                string? customScale = null;
                if (fields.GetValueOrDefault("1990:m_dbViewId") is > 0 and int titleView
                    && state.NativeParams.TryGetValue((uint)titleView, out var viewParams)
                    && viewParams.Fields.GetValueOrDefault("108:m_bCustomScaleName") == 1)
                    customScale = viewParams.StringFields.GetValueOrDefault("108:m_strCustomScaleName");
                // shortcut: use the fallback for rotated titles until their family transform is verified.
                bool horizontal = fields.GetValueOrDefault("1990:m_viewportOrientation", -1) == 0;
                viewportTitles.Add((int)id, new(horizontal ? titleSymbol : 0, showLabel == 1, showLine == 1, customScale));
                if (horizontal && showLabel == 1 && titleSymbol > 0) titleSymbols.Add(titleSymbol);
            }
            var values = native.DoubleFields;
            int view = fields.GetValueOrDefault("m_ownerDBViewId");
            var head = Point("3934:m_headLocation");
            if (view <= 0 || head == null
                || values.GetValueOrDefault("3934:m_rotationAngle", double.NaN) != 0
                || fields.GetValueOrDefault("3934:m_orientation", -1) != 0
                || info.ViewHiddenElements?.GetValueOrDefault(view)?.Contains((int)id) == true) continue;

            int spatial = fields.GetValueOrDefault("3934:m_isSpatialElementTag", -1);
            if (spatial == 0 && fields.GetValueOrDefault("527:singleTaggedElement") == 1
                && fields.GetValueOrDefault("3935:linkedElementId") == -1)
            {
                int target = fields.GetValueOrDefault("3935:elementOrLinkId");
                int tagSymbol = fields.GetValueOrDefault("527:m_symbolId");
                int leader = fields.GetValueOrDefault("527:m_leader", -1);
                if (tagSymbol > 0 && leader == 0 && openingTargets.Contains(target))
                {
                    if (!openings.TryGetValue(view, out var openingTags)) openings.Add(view, openingTags = new());
                    openingTags.Add(new(target, tagSymbol, head.Value.X, head.Value.Y));
                    openingSymbols.Add(tagSymbol);
                }
                if (tagSymbol > 0 && leader == 0 && propertyTargets.Contains(target)
                    && fields.GetValueOrDefault("527:m_tagRelOffsetCondition", -1) == 2
                    && fields.GetValueOrDefault("527:m_isRelPosValid", -1) == 1)
                {
                    if (!propertyTags.TryGetValue(view, out var siteTags)) propertyTags.Add(view, siteTags = []);
                    siteTags.Add(new(target, tagSymbol, head.Value.X, head.Value.Y));
                    propertySymbols.Add(tagSymbol);
                }
                if (tagSymbol > 0 && leader == 1 && plantingTargets.Contains(target)
                    && fields.GetValueOrDefault("527:m_leaderEndCondition", -1) == 1
                    && fields.GetValueOrDefault("527:m_tagRelOffsetCondition", -1) == 1
                    && fields.GetValueOrDefault("527:m_isRelPosValid", -1) == 1
                    && fields.GetValueOrDefault("3936:m_isStraightLeader", -1) == 0
                    && fields.GetValueOrDefault("3936:m_needReconcile", -1) == 0
                    && Point("3936:m_elbowOffset") is { } elbow && Point("3936:m_endOffset") is { } end)
                {
                    if (!plantingTags.TryGetValue(view, out var siteTags)) plantingTags.Add(view, siteTags = new());
                    double x = head.Value.X, y = head.Value.Y;
                    siteTags.Add(new(target, tagSymbol, x, y, (x + elbow.X, y + elbow.Y), (x + end.X, y + end.Y)));
                    plantingSymbols.Add(tagSymbol);
                    openingSymbols.Add(tagSymbol);
                }
                continue;
            }

            int room = fields.GetValueOrDefault("3475:roomOrLinkId");
            int symbol = fields.GetValueOrDefault("3475:m_symbolId");
            var displacement = Point("3475:m_tagOffset");
            if (spatial != 1 || fields.GetValueOrDefault("3475:linkedRoomId") != -1
                || room <= 0 || symbol <= 0 || displacement == null) continue;
            var origin = (X: head.Value.X + displacement.Value.X, Y: head.Value.Y + displacement.Value.Y);
            var anchor = fields.GetValueOrDefault("3475:m_bLeader") == 1
                && fields.GetValueOrDefault("3475:m_bLeaderIsStraight") == 1 ? Point("3475:m_leaderAnchor") : null;
            if (!tags.TryGetValue(view, out var items)) tags.Add(view, items = new());
            items.Add(new(room, symbol, origin.X, origin.Y, anchor, anchor != null ? head : null));
            symbols.Add(symbol);

            (double X, double Y)? Point(string key)
            {
                double x = values.GetValueOrDefault(key + "@0", double.NaN);
                double y = values.GetValueOrDefault(key + "@1", double.NaN);
                return Math.Abs(x) < 1_000_000 && Math.Abs(y) < 1_000_000 ? (x, y) : null;
            }
        }
        info.ViewportTitles = viewportTitles;
        var outlineInstances = info.Elements.Where(element => element.OwnerViewId is > 0 && !element.IsDiscovered
            && element.CategoryId == (int)BuiltInCategory.OST_GenericAnnotation && element.RootClassName == "FamilyInstance"
            && element.TypeId > 0
            && state.TitleBlockTexts.GetValueOrDefault((uint)element.TypeId) is { BlockCount: 1, Texts.Count: 0 }
            && state.TitleBlockLabels.GetValueOrDefault((uint)element.TypeId) is { BlockCount: 1, Labels.Count: 0 })
            .ToList();
        if (symbols.Count == 0 && openingSymbols.Count == 0 && titleSymbols.Count == 0
            && propertySymbols.Count == 0 && outlineInstances.Count == 0)
            return;

        var annotationSymbols = symbols.Union(openingSymbols).Union(titleSymbols).Union(propertySymbols)
            .Union(outlineInstances.Select(e => e.TypeId));
        var contentKeys = new Dictionary<int, string>();
        foreach (int symbol in annotationSymbols)
        {
            if (state.NativeParams.TryGetValue((uint)symbol, out var type)
                && type.Fields.TryGetValue("1829:m_familyId", out int family) && family > 0
                && state.NativeParams.TryGetValue((uint)family, out var familyParams)
                && familyParams.StringFields.TryGetValue("1740:contentDoc", out string? key))
                contentKeys.Add(symbol, key);
        }
        var source = ReadTagFamilyGraphics(frames, new HashSet<string>(contentKeys.Values, StringComparer.Ordinal));
        var elements = info.Elements.Where(e => symbols.Contains(e.Id) || plantingSymbols.Contains(e.Id))
            .ToDictionary(e => e.Id);
        var styles = new Dictionary<int, RevitFileInfo.PlanRoomTagStyle>();
        var openingStyles = new Dictionary<int, RevitFileInfo.PlanOpeningTagStyle>();
        var titleStyles = new Dictionary<int, RevitFileInfo.ViewportTitleStyle>();
        var propertyStyles = new Dictionary<int, IReadOnlyList<(TfBlockDeserializer.TitleBlockLabel Label, double FontPoints)>>();
        foreach (int symbol in annotationSymbols)
        {
            if (!state.TitleBlockLabels.TryGetValue((uint)symbol, out var symbolLabels)
                || symbolLabels is not { BlockCount: 1, Labels: { Count: > 0 } labels }) continue;
            var graphics = contentKeys.TryGetValue(symbol, out var key) ? source.GetValueOrDefault(key) : null;
            var styled = labels.Where(label => label.Bx == 1 && label.By == 0 && label.Hx == 0 && label.Hy == 1)
                .Select(label =>
                {
                    var text = graphics?.Text.GetValueOrDefault((label.Ox, label.Oy)) ?? default;
                    return (Label: label, text.FontPoints, text.Wrap);
                })
                .ToList();
            if (titleSymbols.Contains(symbol))
            {
                if (graphics is { UnresolvedCurves: false } && styled.Count == labels.Count
                    && styled.All(entry => entry.FontPoints > 0 && entry.Label.ParamId is
                        (int)BuiltInParameter.VIEWPORT_VIEW_NAME or (int)BuiltInParameter.VIEWPORT_DETAIL_NUMBER
                        or (int)BuiltInParameter.VIEWPORT_SCALE))
                    titleStyles[symbol] = new(styled, graphics.Lines, graphics.Arcs);
                continue;
            }
            if (openingSymbols.Contains(symbol))
            {
                bool hasFrame = graphics is { Lines.Count: 4, Arcs.Count: 0 } && plantingSymbols.Contains(symbol)
                    && elements.GetValueOrDefault(symbol)?.BoundingBox is { Length: 6 } plantingBox
                    && FrameMatchesBounds(graphics.Lines, plantingBox);
                if ((graphics is { UnresolvedCurves: false } || hasFrame) && graphics!.Lines.Count + graphics.Arcs.Count > 0
                    && styled is [{ FontPoints: > 0 } entry]
                    && entry.Label.ParamId is (int)BuiltInParameter.ALL_MODEL_MARK or (int)BuiltInParameter.ALL_MODEL_TYPE_MARK)
                    openingStyles[symbol] = new(entry.Label, entry.FontPoints, graphics.Lines, graphics.Arcs);
                continue;
            }
            if (propertySymbols.Contains(symbol))
            {
                if (graphics is { UnresolvedCurves: false, HasFill: false, Lines.Count: 0, Arcs.Count: 0 }
                    && styled.Count == labels.Count
                    && styled.All(entry => entry.Label is { Height: > 0, SingleParameterFormat: not null }
                        && entry.Label.ParamId is (int)BuiltInParameter.PROPERTY_SEGMENT_N_S
                            or (int)BuiltInParameter.PROPERTY_SEGMENT_BEARING
                            or (int)BuiltInParameter.PROPERTY_SEGMENT_E_W
                            or (int)BuiltInParameter.PROPERTY_SEGMENT_DISTANCE))
                    propertyStyles[symbol] = styled.Select(entry => (entry.Label, entry.FontPoints)).ToList();
                continue;
            }
            if (styled.Count == 0) continue;
            // A cached planar symbol bound confirms that the family's four lines are enabled on this type.
            var lines = graphics is { Lines.Count: 4 } && elements.GetValueOrDefault(symbol)?.BoundingBox is { Length: 6 } box
                && FrameMatchesBounds(graphics.Lines, box) ? graphics.Lines : [];
            styles[symbol] = new(styled.Select(entry => (entry.Label, entry.FontPoints)).ToList(), lines);
        }
        info.PlanRoomTags = tags;
        info.PlanRoomTagStyles = styles;
        info.PlanOpeningTags = openings;
        info.SitePlantingTags = plantingTags;
        info.SitePropertyTags = propertyTags;
        info.SitePropertyTagStyles = propertyStyles;
        info.PlanOpeningTagStyles = openingStyles;
        info.ViewportTitleStyles = titleStyles;

        var outlines = new Dictionary<int, List<RevitFileInfo.PlanOutlineAnnotation>>();
        foreach (var element in outlineInstances)
        {
            int owner = element.OwnerViewId!.Value;
            if (!contentKeys.TryGetValue(element.TypeId, out var key)
                || source.GetValueOrDefault(key) is not { UnresolvedCurves: false, HasFill: false } graphics
                || graphics.Lines.Count + graphics.Arcs.Count == 0
                || info.ViewHiddenElements?.GetValueOrDefault(owner)?.Contains(element.Id) == true
                || state.ComputedFamilySymbols.GetValueOrDefault((uint)element.Id)
                    is not { BlockCount: 1, Symbol: { Transform.Length: 12 } computed }
                || computed.SymbolId != element.TypeId
                || graphics.Lines.Select(line => line.StyleId).Concat(graphics.Arcs.Select(arc => arc.StyleId)).Any(id =>
                    info.GraphicsStylePens?.GetValueOrDefault(id) is not { Color: 0, PenNumber: > 0, LinePatternId: -3000010 or -1 }))
                continue;
            if (!outlines.TryGetValue(owner, out var list)) outlines.Add(owner, list = []);
            list.Add(new(computed.Transform, graphics.Lines, graphics.Arcs));
        }
        info.PlanOutlineAnnotations = outlines;
    }

    private sealed class TagFamilyGraphics
    {
        internal Dictionary<(double X, double Y), (double FontPoints, bool Wrap)> Text { get; } = new();
        internal List<TfBlockDeserializer.TitleBlockLine> Lines { get; } = new();
        internal List<TfBlockDeserializer.FamilyAnnotationArc> Arcs { get; } = new();
        internal bool UnresolvedCurves { get; set; }
        internal bool HasFill { get; set; }
    }

    private static Dictionary<string, TagFamilyGraphics> ReadTagFamilyGraphics(
        IReadOnlyList<PartitionFraming.Framed?> frames, HashSet<string> keys)
    {
        var result = new Dictionary<string, TagFamilyGraphics>(StringComparer.Ordinal);
        if (keys.Count == 0) return result;
        var seen = new HashSet<(string Key, ulong Owner)>();
        foreach (var frame in frames)
        {
            if (frame?.FamilyAnnotationItems is not { } items) continue;
            foreach (var (key, item) in items)
            {
                if (!keys.Contains(key) || !seen.Add((key, item.Owner))) continue;
                var (data, start, _) = frame.Bytes(item);
                uint size = BitConverter.ToUInt32(data, start + 8);
                ushort tag = BitConverter.ToUInt16(data, start + 12);
                if (!result.TryGetValue(key, out var graphics)) result.Add(key, graphics = new());
                if (tag == 1860) graphics.HasFill = true;
                if (tag == 838)
                {
                    var curve = TfBlockDeserializer.ReadFamilyAnnotationCurve(data, start, size);
                    if (curve is { Line: { } line }) graphics.Lines.Add(line);
                    if (curve is { Arc: { } arc }) graphics.Arcs.Add(arc);
                    if (curve is not { TypeDependent: false }) graphics.UnresolvedCurves = true;
                }
                if (tag == 3927 && TfBlockDeserializer.ExtractParams(data, start, size, tag) is { } note
                    && note.PlanNoteText is { FontSizePoints: > 0 and <= 200 } text
                    && note.DoubleFields.TryGetValue("3928:m_orig@0", out double x)
                    && note.DoubleFields.TryGetValue("3928:m_orig@1", out double y))
                    graphics.Text.TryAdd((x, y), (text.FontSizePoints.Value,
                        note.Fields.GetValueOrDefault("3928:m_bNoWrapping", 1) == 0));
            }
        }
        return result;
    }

    private static bool FrameMatchesBounds(List<TfBlockDeserializer.TitleBlockLine> lines, double[] box)
    {
        if (box[2] != box[5] || !(box[0] < box[3] && box[1] < box[4])) return false;
        const double tolerance = 1e-6;
        var corners = new[] { (box[0], box[1]), (box[3], box[1]), (box[3], box[4]), (box[0], box[4]) };
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % corners.Length];
            if (!lines.Any(line => Matches(a, line.X0, line.Y0) && Matches(b, line.X1, line.Y1)
                                || Matches(b, line.X0, line.Y0) && Matches(a, line.X1, line.Y1))) return false;
        }
        return true;

        static bool Matches((double X, double Y) point, double x, double y) =>
            Math.Abs(point.X - x) < tolerance && Math.Abs(point.Y - y) < tolerance;
    }
}
