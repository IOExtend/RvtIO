using System.Buffers.Binary;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>Type links backed by complete project records in the file's own schema.</summary>
internal sealed class FramedElementTypes
{
    private const string OwnerField = "Element.m_id";
    private const string ViewerView = "Viewer.m_dbViewId";
    private const string ViewType = "DBView.m_dbViewTypeId";
    private const string ViewMarker = "DBView.m_viewerId";
    private const string ReferenceViewer = "Viewer.m_bIsViewerReference";
    private const string ArrowList = WideBlockWalker.AllItemsKeyPrefix + "InteriorElev.m_arrowIds";
    private const string MarkerParent = "InteriorElevArrow.m_parentId";
    private const string MarkerType = "InteriorElev.m_symbolId";
    private const string StairOwner = "StairsRunAndLanding.m_stairsId";
    private const string StairType = "StairsElement.m_typeId";
    private const string RunType = "StairsType.m_runTypeId";
    private const string LandingType = "StairsType.m_landingTypeId";
    private const string FabricType = "FabricSheet.m_sheetTypeId";
    private const string RailType = "ContinuousRail.m_typeId";
    private const string ActualSpanSymbol = "SpanDirectionSymbol.m_actualSymbolId";
    private const string TagSymbol = "IndependentTag.m_symbolId";
    private const string MasterSymbol = "FamilyInstance.m_masterSymbolId";

    private static readonly HashSet<string> CapturedFields =
    [
        OwnerField, ViewerView, ViewType, ViewMarker, ReferenceViewer, MarkerParent, MarkerType, StairOwner,
        StairType, RunType, LandingType, FabricType, RailType, ActualSpanSymbol, TagSymbol, MasterSymbol,
    ];
    private static readonly HashSet<string> Classes =
    [
        "Viewer3d", "DBView3d", "DBViewType", "DBViewSection", "InteriorElevArrow", "InteriorElev",
        "FabricSheet", "FabricSheetType", "StairsRun", "StairsLanding", "StairsElement", "StairsType",
        "StairsRunType", "StairsLandingType", "HandRail", "TopRail", "HandRailType", "TopRailType",
        "SpanDirectionSymbol", "FamilyInstance", "FamilySymbol",
    ];

    private readonly record struct Source(PartitionFraming.Framed Frame, PartitionFraming.ProjectItem Item, SchemaClass Class);

    private sealed record Root(string Kind, IReadOnlyList<Dictionary<string, long>> Copies)
    {
        internal long? Field(string name)
            => Copies[0].TryGetValue(name, out long value)
               && Copies.All(copy => copy.GetValueOrDefault(name, long.MinValue) == value) ? value : null;

        internal bool ContainsArrow(long arrow) => Copies.All(copy =>
            copy.TryGetValue(ArrowList + "#", out long count)
            && copy.Where(field => field.Key.StartsWith(ArrowList + "@", StringComparison.Ordinal))
                .Count(field => field.Value == arrow) == 1
            && copy.Count(field => field.Key.StartsWith(ArrowList + "@", StringComparison.Ordinal)) == count);
    }

    private readonly WideBlockWalker _walker;
    private readonly Dictionary<long, List<Source>> _sources = new();
    private readonly Dictionary<long, Root?> _roots = new();

    private FramedElementTypes(SchemaDocument schema, IReadOnlyDictionary<Guid, SchemaField[]?> entities)
    {
        _walker = new WideBlockWalker(schema, (declaring, field) =>
        {
            string key = declaring.Name + "." + field.Name;
            return key == "InteriorElev.m_arrowIds" ? ArrowList : CapturedFields.Contains(key) ? key : null;
        }, []);
        _walker.UseEntitySchemas(entities, passUnknown: true);
    }

    internal static int Apply(SchemaDocument schema, IReadOnlyDictionary<Guid, SchemaField[]?> entities,
        IReadOnlyList<PartitionFraming.Framed?> frames, ParsingState state, IReadOnlyList<Element> elements)
    {
        if (frames.Count == 0 || frames.Any(frame => frame == null)) return 0;
        var reader = new FramedElementTypes(schema, entities);
        var ids = state.ElementIds;
        long SourceId(int key) => ids?.SourceId(key) ?? key;
        bool LocalId(long sourceId, out int key)
        {
            key = 0;
            return sourceId > 0 && (ids != null ? ids.TryGetKey(sourceId, out key)
                : sourceId <= int.MaxValue && (key = (int)sourceId) > 0);
        }

        var details = elements.Where(element => element.CategoryId == (int)BuiltInCategory.OST_DetailComponents)
            .Select(element => SourceId(element.Id)).ToHashSet();
        foreach (var frame in frames.Cast<PartitionFraming.Framed>())
            foreach (var item in frame.Items)
            {
                int headerSize = frame.IdWidth + 8;
                if (item.Channel != 102 || item.Length < headerSize + 6 || item.Owner > long.MaxValue
                    || !LocalId((long)item.Owner, out int key) || !state.ElemTable.ContainsKey((uint)key)) continue;
                var (header, start, _) = frame.Bytes(item with { Length = headerSize + 2 });
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(start + headerSize, 2));
                if (reader._walker.Class(tag) is not { } kind || !Classes.Contains(kind.Name)
                    || kind.Name == "FamilyInstance" && (!details.Contains((long)item.Owner)
                        || state.IncrementRoutedPartitions?.ContainsKey((uint)key) == true)) continue;
                if (!reader._sources.TryGetValue((long)item.Owner, out var copies))
                    reader._sources.Add((long)item.Owner, copies = []);
                copies.Add(new(frame, item, kind));
            }

        // A second record of an unsupported class still makes the owner's type ambiguous.
        var recordCounts = new Dictionary<long, int>();
        foreach (var frame in frames.Cast<PartitionFraming.Framed>())
            foreach (var item in frame.Items)
                if (item.Channel == 102 && item.Owner <= long.MaxValue && reader._sources.ContainsKey((long)item.Owner))
                    recordCounts[(long)item.Owner] = recordCounts.GetValueOrDefault((long)item.Owner) + 1;
        foreach (var (owner, count) in recordCounts)
            if (reader._sources[owner].Count != count) reader._sources.Remove(owner);

        int changed = 0;
        foreach (var element in elements)
        {
            long owner = SourceId(element.Id);
            if (!reader._sources.TryGetValue(owner, out var copies)) continue;
            long? type = copies[0].Class.Name switch
            {
                "Viewer3d" => reader.ViewerType(owner),
                "DBViewSection" => reader.ElevationType(owner),
                "InteriorElevArrow" => reader.ElevationArrowType(owner),
                "StairsRun" => reader.StairPartType(owner, "StairsRun", RunType),
                "StairsLanding" => reader.StairPartType(owner, "StairsLanding", LandingType),
                "FabricSheet" => reader.DirectType(owner, "FabricSheet", FabricType, "FabricSheetType"),
                "HandRail" => reader.DirectType(owner, "HandRail", RailType, "HandRailType"),
                "TopRail" => reader.DirectType(owner, "TopRail", RailType, "TopRailType"),
                "SpanDirectionSymbol" => reader.SpanType(owner),
                "FamilyInstance" when copies.Count == 1 => reader.DirectType(owner, "FamilyInstance", MasterSymbol, "FamilySymbol"),
                _ => null,
            };
            if (type is not long sourceType || sourceType == owner || !LocalId(sourceType, out int typeKey)
                || element.TypeId == typeKey) continue;
            element.TypeId = typeKey;
            changed++;
        }
        return changed;
    }

    private Root? GetRoot(long? id, string kind)
    {
        if (id is not > 0) return null;
        long owner = id.Value;
        if (!_roots.TryGetValue(owner, out var root)) _roots[owner] = root = ReadRoot(owner);
        return root?.Kind == kind ? root : null;
    }

    private Root? ReadRoot(long owner)
    {
        if (!_sources.TryGetValue(owner, out var copies)) return null;
        var fields = new List<Dictionary<string, long>>(copies.Count);
        var kind = copies[0].Class;
        foreach (var (frame, item, cls) in copies)
        {
            if (cls.Id != kind.Id) return null;
            var (data, start, _) = frame.Bytes(item);
            int size = item.Length - frame.IdWidth - 12;
            _walker.StepBudget = 16L * size + 65536;
            var walk = _walker.Run(data, start + frame.IdWidth + 8, size, cls.Id);
            if (!walk.Exact) return null;
            var values = new Dictionary<string, long>();
            foreach (var capture in walk.Captures)
                if (capture.AtRoot && capture.Kind == WideBlockWalker.CaptureKind.Integer
                    && !values.TryAdd(capture.Key, capture.Integer)) return null;
            if (!values.TryGetValue(OwnerField, out long decodedOwner) || decodedOwner != owner) return null;
            fields.Add(values);
        }
        return new(kind.Name, fields);
    }

    private long? ViewerType(long viewer)
    {
        var view = GetRoot(GetRoot(viewer, "Viewer3d")?.Field(ViewerView), "DBView3d");
        return view?.Field(ViewMarker) == viewer ? VerifyType(view.Field(ViewType), "DBViewType") : null;
    }

    private long? ElevationType(long? viewId)
    {
        var view = GetRoot(viewId, "DBViewSection");
        if (view?.Field(ViewType) != -1 || view.Field(ViewMarker) is not long arrowId) return null;
        var arrow = GetRoot(arrowId, "InteriorElevArrow");
        return arrow?.Field(ViewerView) == viewId && arrow?.Field(ReferenceViewer) == 0
            ? ParentMarkerType(arrowId, arrow) : null;
    }

    private long? ElevationArrowType(long arrowId)
    {
        var arrow = GetRoot(arrowId, "InteriorElevArrow");
        if (arrow?.Field(ViewerView) is not long viewId || ElevationType(viewId) is not long type) return null;
        return GetRoot(viewId, "DBViewSection")?.Field(ViewMarker) == arrowId
            || arrow.Field(ReferenceViewer) == 1 && ParentMarkerType(arrowId, arrow) == type ? type : null;
    }

    private long? ParentMarkerType(long arrowId, Root arrow)
    {
        var marker = GetRoot(arrow.Field(MarkerParent), "InteriorElev");
        return marker?.ContainsArrow(arrowId) == true ? VerifyType(marker.Field(MarkerType), "DBViewType") : null;
    }

    private long? StairPartType(long part, string kind, string field)
    {
        var stair = GetRoot(GetRoot(part, kind)?.Field(StairOwner), "StairsElement");
        return VerifyType(GetRoot(stair?.Field(StairType), "StairsType")?.Field(field), kind + "Type");
    }

    private long? DirectType(long id, string kind, string field, string target)
        => VerifyType(GetRoot(id, kind)?.Field(field), target);

    private long? SpanType(long id)
    {
        var root = GetRoot(id, "SpanDirectionSymbol");
        return root?.Field(ActualSpanSymbol) is long symbol && root.Field(TagSymbol) == symbol
            ? VerifyType(symbol, "FamilySymbol") : null;
    }

    private long? VerifyType(long? id, string kind) => GetRoot(id, kind) == null ? null : id;
}
