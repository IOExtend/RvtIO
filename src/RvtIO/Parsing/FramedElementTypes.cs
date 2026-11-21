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
    private const string SupportHost = "StairsSupport.m_hostCompId";
    private const string SupportOverridden = "StairsSupport.m_isTypeOverridden";
    private const string LeftSupport = "StairsType.m_leftSideSupportTypeId";
    private const string RightSupport = "StairsType.m_rightSideSupportTypeId";
    private const string LeftSupportStyle = "StairsType.m_leftSupportStyle";
    private const string RightSupportStyle = "StairsType.m_rightSupportStyle";
    private const string IntermediateSupport = "StairsType.m_hasIntermediateSupport";
    private const string GridChainType = "GridChain.m_typeId";
    private const string RebarContainerType = "RebarContainer.m_rebarContainerTypeId";
    private const string RebarInstance = "Rebar.m_oInstance";
    private const string RebarType = "RebarInstBase.m_barTypeId";
    private const string SoffitType = "Ceiling.m_ceilingAttributesId";
    private const string RampType = "Ramp.m_rampAttrId";

    private static readonly Dictionary<string, (string Field, string Target)> UnanchoredTypes = new()
    {
        ["LinearDimString"] = ("Dimension.m_styleSymbolId", "DimensionStyle"),
        ["TextNote"] = ("TextElement.m_attrId", "TextNoteAttributes"),
        ["SWall"] = ("VWall.m_WallAttributesId", "BasicWallType"),
    };

    private static readonly HashSet<string> CapturedFields =
    [
        OwnerField, ViewerView, ViewType, ViewMarker, ReferenceViewer, MarkerParent, MarkerType, StairOwner,
        StairType, RunType, LandingType, FabricType, RailType, ActualSpanSymbol, TagSymbol, MasterSymbol,
        SupportHost, SupportOverridden, LeftSupport, RightSupport, LeftSupportStyle, RightSupportStyle, IntermediateSupport,
        GridChainType, RebarContainerType, RebarInstance, RebarType, SoffitType, RampType,
    ];
    private static readonly HashSet<string> Classes =
    [
        "Viewer3d", "DBView3d", "DBViewType", "DBViewSection", "InteriorElevArrow", "InteriorElev",
        "FabricSheet", "FabricSheetType", "StairsRun", "StairsLanding", "StairsElement", "StairsType",
        "StairsRunType", "StairsLandingType", "HandRail", "TopRail", "HandRailType", "TopRailType",
        "SpanDirectionSymbol", "FamilyInstance", "StairsSupport", "StairsSupportType", "GridChain", "GridAttributes",
        "RebarContainer", "RebarContainerType", "Rebar", "RebarBarType", "RoofSoffit", "RoofSoffitType", "Ramp", "RampAttributes",
    ];

    private readonly record struct Source(PartitionFraming.Framed Frame, PartitionFraming.ProjectItem Item, SchemaClass Class);

    private sealed record Root(SchemaClass Class, IReadOnlyList<Dictionary<string, long>> Copies)
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
    private readonly HashSet<int> _classes;
    private readonly Dictionary<long, List<Source>> _sources = new();
    private readonly Dictionary<long, Root?> _roots = new();

    private FramedElementTypes(SchemaDocument schema, IReadOnlyDictionary<Guid, SchemaField[]?> entities, bool recoverUnanchoredTypes)
    {
        var extraFields = recoverUnanchoredTypes ? UnanchoredTypes.Values.Select(link => link.Field).ToHashSet() : null;
        _walker = new WideBlockWalker(schema, (declaring, field) =>
        {
            string key = declaring.Name + "." + field.Name;
            return key == "InteriorElev.m_arrowIds" ? ArrowList
                : CapturedFields.Contains(key) || extraFields?.Contains(key) == true ? key : null;
        }, [], captureRootPointers: true);
        _walker.UseEntitySchemas(entities, passUnknown: true);
        var extraTargets = recoverUnanchoredTypes ? UnanchoredTypes.Values.Select(link => link.Target).ToHashSet() : null;
        _classes = schema.Classes.Where(cls => Classes.Contains(cls.Name) || _walker.IsA(cls.Id, "FamilySymbol")
                || recoverUnanchoredTypes && (UnanchoredTypes.ContainsKey(cls.Name) || extraTargets!.Contains(cls.Name)))
            .Select(cls => cls.Id).ToHashSet();
    }

    internal static Dictionary<ulong, int>? TypePartitionsWithoutRoutedCopy(
        IReadOnlyDictionary<ulong, List<int>> holdersByOwner, IReadOnlyList<ElemTableLayout.Row> rows,
        DocumentIncrementTable.Table increments, IReadOnlyDictionary<ulong, int> routed)
    {
        if (increments.Permutation.Count != 0) return null;
        var partitions = new Dictionary<ulong, int>();
        foreach (var row in rows)
        {
            ulong owner = (ulong)row.Id;
            if (!holdersByOwner.TryGetValue(owner, out var holders) || holders.Count < 2 || routed.ContainsKey(owner)
                || row.LastModificationDate is not int episode || DocumentIncrementTable.Route(increments, episode) is not int partition
                || holders.Contains(partition)) continue;
            int latest = holders.Max();
            if ((uint)latest < (uint)increments.Increments.Count) partitions.Add(owner, latest);
        }
        return partitions;
    }

    internal static int Apply(SchemaDocument schema, IReadOnlyDictionary<Guid, SchemaField[]?> entities,
        IReadOnlyList<PartitionFraming.Framed?> frames, ParsingState state, IReadOnlyList<Element> elements,
        IReadOnlyDictionary<ulong, int>? unroutedTypePartitions = null, bool recoverUnanchoredTypes = false)
    {
        if (frames.Count == 0 || frames.Any(frame => frame == null)) return 0;
        var reader = new FramedElementTypes(schema, entities, recoverUnanchoredTypes);
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
                if (!reader._classes.Contains(tag) || reader._walker.Class(tag) is not { } kind
                    || UnanchoredTypes.ContainsKey(kind.Name) && state.NativeParamsFromRootClass.Contains((uint)key)
                    || kind.Name == "FamilyInstance" && (!details.Contains((long)item.Owner)
                        && unroutedTypePartitions?.ContainsKey(item.Owner) != true
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
                "StairsSupport" when state.ElemRecOwningElementIds?.TryGetValue((uint)element.Id, out int stairKey) == true
                    => reader.StairSupportType(owner, SourceId(stairKey)),
                "FabricSheet" => reader.DirectType(owner, "FabricSheet", FabricType, "FabricSheetType"),
                "HandRail" => reader.DirectType(owner, "HandRail", RailType, "HandRailType"),
                "TopRail" => reader.DirectType(owner, "TopRail", RailType, "TopRailType"),
                "SpanDirectionSymbol" => reader.SpanType(owner),
                "GridChain" => reader.DirectType(owner, "GridChain", GridChainType, "GridAttributes"),
                "RebarContainer" => reader.DirectType(owner, "RebarContainer", RebarContainerType, "RebarContainerType"),
                "Rebar" => reader.DirectType(owner, "Rebar", RebarType, "RebarBarType"),
                "RoofSoffit" => reader.DirectType(owner, "RoofSoffit", SoffitType, "RoofSoffitType"),
                "Ramp" => reader.DirectType(owner, "Ramp", RampType, "RampAttributes"),
                "FamilyInstance" when copies.Count == 1 && details.Contains(owner)
                    => reader.DirectType(owner, "FamilyInstance", MasterSymbol, "FamilySymbol"),
                "FamilyInstance" when unroutedTypePartitions?.TryGetValue((ulong)owner, out int partition) == true
                    => reader.UnroutedFamilyType(owner, copies, partition),
                // A single exact record can supply the type even when the stream walk never anchored on it.
                var kind when copies.Count == 1 && UnanchoredTypes.TryGetValue(kind, out var link)
                    => reader.DirectType(owner, kind, link.Field, link.Target),
                _ => null,
            };
            if (type is not long sourceType || sourceType == owner || !LocalId(sourceType, out int typeKey)
                || element.TypeId == typeKey) continue;
            element.TypeId = typeKey;
            changed++;
        }
        return changed;
    }

    private Root? GetRoot(long? id)
    {
        if (id is not > 0) return null;
        long owner = id.Value;
        if (!_roots.TryGetValue(owner, out var root)) _roots[owner] = root = ReadRoot(owner);
        return root;
    }

    private Root? GetRoot(long? id, string kind)
        => GetRoot(id) is { } root && root.Class.Name == kind ? root : null;

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
            int rebarInstance = kind.Name == "Rebar" && walk.RootPointers?.TryGetValue(RebarInstance, out int queued) == true
                && _walker.IsA(walk.Queue[queued], "RebarInstBase") ? queued : -1;
            var values = new Dictionary<string, long>();
            foreach (var capture in walk.Captures)
                if (capture.Kind == WideBlockWalker.CaptureKind.Integer
                    && (capture.Key == RebarType ? rebarInstance >= 0 && capture.QueueIndex == rebarInstance : capture.AtRoot)
                    && !values.TryAdd(capture.Key, capture.Integer)) return null;
            if (!values.TryGetValue(OwnerField, out long decodedOwner) || decodedOwner != owner) return null;
            fields.Add(values);
        }
        return new(kind, fields);
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

    private long? StairSupportType(long supportId, long stairId)
    {
        var support = GetRoot(supportId, "StairsSupport");
        if (support?.Field(SupportOverridden) != 0) return null;
        var host = GetRoot(support.Field(SupportHost));
        if (host?.Class.Name is not ("StairsRun" or "StairsLanding") || host.Field(StairOwner) != stairId) return null;
        var stairType = GetRoot(GetRoot(stairId, "StairsElement")?.Field(StairType), "StairsType");
        // The confirmed side-support layout uses style 1 and has no intermediate support.
        if (stairType?.Field(IntermediateSupport) != 0
            || stairType.Field(LeftSupportStyle) != 1 || stairType.Field(RightSupportStyle) != 1
            || stairType.Field(LeftSupport) is not long left || stairType.Field(RightSupport) != left) return null;
        return VerifyType(left, "StairsSupportType");
    }

    private long? UnroutedFamilyType(long owner, List<Source> copies, int partition)
    {
        if (copies.Count < 2 || GetRoot(owner, "FamilyInstance") is not { } root) return null;
        int latest = -1;
        var holders = new HashSet<int>();
        for (int i = 0; i < copies.Count; i++)
        {
            if (copies[i].Frame.Partition is not int holder || !holders.Add(holder)
                || root.Copies[i].GetValueOrDefault(MasterSymbol) <= 0) return null;
            if (holder == partition) latest = i;
        }
        if (latest < 0 || holders.Max() != partition || root.Field(MasterSymbol) != null) return null;
        // The increment named by ElemTable lost its record. Use the last holder just for the symbol link.
        long symbol = root.Copies[latest][MasterSymbol];
        return GetRoot(symbol) is { } target && _walker.IsA(target.Class.Id, "FamilySymbol") ? symbol : null;
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
