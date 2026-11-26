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
    private const string GroupType = "GroupMembership.m_groupId";

    private static readonly Dictionary<string, (string Field, string Target)> UnanchoredTypes = new()
    {
        ["LinearDimString"] = ("Dimension.m_styleSymbolId", "DimensionStyle"),
        ["TextNote"] = ("TextElement.m_attrId", "TextNoteAttributes"),
        ["SWall"] = ("VWall.m_WallAttributesId", "BasicWallType"),
    };

    private static readonly Dictionary<string, (string Field, string Target)> RoutedTypes = new()
    {
        ["SWall"] = UnanchoredTypes["SWall"],
        ["FamilyInstance"] = (MasterSymbol, "FamilySymbol"),
        ["RoomTag"] = ("RoomTag.m_symbolId", "FamilySymbol"),
        ["ProfileRoof"] = ("RoofBase.m_roofAttributesId", "RoofAttributes"),
        ["LinearDimString"] = UnanchoredTypes["LinearDimString"],
        ["Floor"] = ("Floor.m_floorAttributesId", "FloorAttributes"),
        ["DBViewPlan"] = (ViewType, "DBViewType"),
        ["DBViewSection"] = (ViewType, "DBViewType"),
        ["BaseRailing"] = ("BaseRailing.m_stairsRailingAttrId", "StairsRailingAttr"),
        ["ElementGroup"] = (GroupType, "ElementGroupType"),
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

    private readonly record struct Source(PartitionFraming.Framed? Frame, PartitionFraming.ProjectItem Item,
        SchemaClass Class, int IdWidth, int? Partition, byte[]? Raw)
    {
        internal (byte[] Data, int Start, bool Copied) Bytes() =>
            Raw is { } bytes ? (bytes, 0, false) : Frame!.Bytes(Item);

        internal Source Retain()
        {
            var (bytes, offset, _) = Bytes();
            return this with { Frame = null, Raw = bytes.Length == Item.Length ? bytes : bytes.AsSpan(offset, Item.Length).ToArray() };
        }
    }

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
    private readonly bool _readGroupMembership;
    private readonly Dictionary<long, List<Source>> _sources = new();
    private readonly HashSet<long> _requiresDetailCategory = new();
    private readonly Dictionary<long, Root?> _roots = new();

    private FramedElementTypes(SchemaDocument schema, IReadOnlyDictionary<Guid, SchemaField[]?> entities,
        bool recoverUnanchoredTypes, bool recoverRoutedTypes)
    {
        var extraFields = recoverUnanchoredTypes ? UnanchoredTypes.Values.Select(link => link.Field).ToHashSet() : null;
        if (recoverRoutedTypes) extraFields!.UnionWith(RoutedTypes.Values.Select(link => link.Field));
        _walker = new WideBlockWalker(schema, (declaring, field) =>
        {
            string key = declaring.Name + "." + field.Name;
            return key == "InteriorElev.m_arrowIds" ? ArrowList
                : CapturedFields.Contains(key) || extraFields?.Contains(key) == true ? key : null;
        }, [], captureRootPointers: true);
        _walker.UseEntitySchemas(entities, passUnknown: true);
        var extraTargets = recoverUnanchoredTypes ? UnanchoredTypes.Values.Select(link => link.Target).ToHashSet() : null;
        if (recoverRoutedTypes)
        {
            extraTargets!.UnionWith(RoutedTypes.Values.Select(link => link.Target));
            extraTargets.Add("NewCurtainWallType");
            _readGroupMembership = SchemaRelease.FromEvidence(
                schema.Classes.Find(cls => cls.Name == "ContentMarker")?.Id, schema.Classes.Count) == 2022;
        }
        _classes = schema.Classes.Where(cls => Classes.Contains(cls.Name) || _walker.IsA(cls.Id, "FamilySymbol")
                || recoverUnanchoredTypes && (UnanchoredTypes.ContainsKey(cls.Name) || extraTargets!.Contains(cls.Name))
                || recoverRoutedTypes && RoutedTypes.ContainsKey(cls.Name))
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
        var details = DetailOwners(state, elements);
        return Prepare(schema, entities, frames, state, unroutedTypePartitions, recoverUnanchoredTypes, details)
            ?.Apply(state, elements, unroutedTypePartitions, recoverUnanchoredTypes, details) ?? 0;
    }

    private static HashSet<long> DetailOwners(ParsingState state, IReadOnlyList<Element> elements) =>
        elements.Where(element => element.CategoryId == (int)BuiltInCategory.OST_DetailComponents)
            .Select(element => state.ElementIds?.SourceId(element.Id) ?? element.Id).ToHashSet();

    private static bool LocalId(ElementIdMap? ids, long sourceId, out int key)
    {
        key = 0;
        return sourceId > 0 && (ids != null ? ids.TryGetKey(sourceId, out key)
            : sourceId <= int.MaxValue && (key = (int)sourceId) > 0);
    }

    // When called before the model is built, keep only the candidate records and defer
    // the detail-component filter until categories have been resolved.
    internal static FramedElementTypes? Prepare(SchemaDocument schema, IReadOnlyDictionary<Guid, SchemaField[]?> entities,
        IReadOnlyList<PartitionFraming.Framed?> frames, ParsingState state,
        IReadOnlyDictionary<ulong, int>? unroutedTypePartitions, bool recoverUnanchoredTypes, HashSet<long>? details = null)
    {
        if (frames.Count == 0 || frames.Any(frame => frame == null)) return null;
        var routes = recoverUnanchoredTypes ? state.IncrementRoutedPartitions : null;
        var reader = new FramedElementTypes(schema, entities, recoverUnanchoredTypes, routes is { Count: > 0 });
        foreach (var frame in frames.Cast<PartitionFraming.Framed>())
            foreach (var item in frame.Items)
            {
                int headerSize = frame.IdWidth + 8;
                if (item.Channel != 102 || item.Length < headerSize + 6 || item.Owner > long.MaxValue
                    || !LocalId(state.ElementIds, (long)item.Owner, out int key) || !state.ElemTable.ContainsKey((uint)key)) continue;
                var (header, start, _) = frame.Bytes(item with { Length = headerSize + 2 });
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(start + headerSize, 2));
                if (!reader._classes.Contains(tag) || reader._walker.Class(tag) is not { } kind) continue;
                bool routed = routes?.ContainsKey((uint)key) == true && RoutedTypes.ContainsKey(kind.Name);
                bool needsDetail = !routed && kind.Name == "FamilyInstance" && unroutedTypePartitions?.ContainsKey(item.Owner) != true;
                if (!routed && (!Classes.Contains(kind.Name) && !UnanchoredTypes.ContainsKey(kind.Name)
                        && RoutedTypes.ContainsKey(kind.Name)
                    || UnanchoredTypes.ContainsKey(kind.Name) && state.NativeParamsFromRootClass.Contains((uint)key)
                    || kind.Name == "FamilyInstance" && state.IncrementRoutedPartitions?.ContainsKey((uint)key) == true
                    || needsDetail && details != null && !details.Contains((long)item.Owner))) continue;
                if (needsDetail && details == null) reader._requiresDetailCategory.Add((long)item.Owner);
                if (!reader._sources.TryGetValue((long)item.Owner, out var copies))
                    reader._sources.Add((long)item.Owner, copies = []);
                copies.Add(new(frame, item, kind, frame.IdWidth, frame.Partition, null));
            }

        // A second record of an unsupported class still makes the owner's type ambiguous.
        var recordCounts = new Dictionary<long, int>();
        foreach (var frame in frames.Cast<PartitionFraming.Framed>())
            foreach (var item in frame.Items)
                if (item.Channel == 102 && item.Owner <= long.MaxValue && reader._sources.ContainsKey((long)item.Owner))
                    recordCounts[(long)item.Owner] = recordCounts.GetValueOrDefault((long)item.Owner) + 1;
        foreach (var (owner, count) in recordCounts)
            if (reader._sources[owner].Count != count) reader._sources.Remove(owner);

        if (details == null)
            foreach (var copies in reader._sources.Values)
                for (int i = 0; i < copies.Count; i++) copies[i] = copies[i].Retain();
        return reader;
    }

    internal int Apply(ParsingState state, IReadOnlyList<Element> elements,
        IReadOnlyDictionary<ulong, int>? unroutedTypePartitions, bool recoverUnanchoredTypes, HashSet<long>? details = null)
    {
        var routes = recoverUnanchoredTypes ? state.IncrementRoutedPartitions : null;
        long SourceId(int key) => state.ElementIds?.SourceId(key) ?? key;
        details ??= DetailOwners(state, elements);
        foreach (long owner in _requiresDetailCategory)
            if (!details.Contains(owner)) _sources.Remove(owner);

        int changed = 0;
        foreach (var element in elements)
        {
            long owner = SourceId(element.Id);
            if (!_sources.TryGetValue(owner, out var copies)) continue;
            long? type = copies[0].Class.Name switch
            {
                "Viewer3d" => ViewerType(owner),
                "DBViewSection" => ElevationType(owner),
                "InteriorElevArrow" => ElevationArrowType(owner),
                "StairsRun" => StairPartType(owner, "StairsRun", RunType),
                "StairsLanding" => StairPartType(owner, "StairsLanding", LandingType),
                "StairsSupport" when state.ElemRecOwningElementIds?.TryGetValue((uint)element.Id, out int stairKey) == true
                    => StairSupportType(owner, SourceId(stairKey)),
                "FabricSheet" => DirectType(owner, "FabricSheet", FabricType, "FabricSheetType"),
                "HandRail" => DirectType(owner, "HandRail", RailType, "HandRailType"),
                "TopRail" => DirectType(owner, "TopRail", RailType, "TopRailType"),
                "SpanDirectionSymbol" => SpanType(owner),
                "GridChain" => DirectType(owner, "GridChain", GridChainType, "GridAttributes"),
                "RebarContainer" => DirectType(owner, "RebarContainer", RebarContainerType, "RebarContainerType"),
                "Rebar" => DirectType(owner, "Rebar", RebarType, "RebarBarType"),
                "RoofSoffit" => DirectType(owner, "RoofSoffit", SoffitType, "RoofSoffitType"),
                "Ramp" => DirectType(owner, "Ramp", RampType, "RampAttributes"),
                "FamilyInstance" when copies.Count == 1 && details.Contains(owner)
                    && state.IncrementRoutedPartitions?.ContainsKey((uint)element.Id) != true
                    => DirectType(owner, "FamilyInstance", MasterSymbol, "FamilySymbol"),
                "FamilyInstance" when unroutedTypePartitions?.TryGetValue((ulong)owner, out int partition) == true
                    => UnroutedFamilyType(owner, copies, partition),
                // A single exact record can supply the type even when the stream walk never anchored on it.
                var kind when copies.Count == 1 && UnanchoredTypes.TryGetValue(kind, out var link)
                    && !state.NativeParamsFromRootClass.Contains((uint)element.Id)
                    => DirectType(owner, kind, link.Field, link.Target),
                _ => null,
            };
            if (type == null && copies.Count > 1 && element.TypeId > 0
                && routes?.TryGetValue((uint)element.Id, out int current) == true)
                type = RoutedType(owner, copies, current, SourceId(element.TypeId));
            if (type is not long sourceType || sourceType == owner || !LocalId(state.ElementIds, sourceType, out int typeKey)
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
        foreach (var source in copies)
        {
            var cls = source.Class;
            if (cls.Id != kind.Id) return null;
            var (data, start, _) = source.Bytes();
            int size = source.Item.Length - source.IdWidth - 12;
            _walker.StepBudget = 16L * size + 65536;
            var walk = _walker.Run(data, start + source.IdWidth + 8, size, cls.Id);
            if (!walk.Exact) return null;
            int rebarInstance = kind.Name == "Rebar" && walk.RootPointers?.TryGetValue(RebarInstance, out int queued) == true
                && _walker.IsA(walk.Queue[queued], "RebarInstBase") ? queued : -1;
            var values = new Dictionary<string, long>();
            foreach (var capture in walk.Captures)
                if (capture.Kind == WideBlockWalker.CaptureKind.Integer
                    && (capture.Key == RebarType ? rebarInstance >= 0 && capture.QueueIndex == rebarInstance : capture.AtRoot)
                    && !values.TryAdd(capture.Key, capture.Integer)) return null;
            if (!values.TryGetValue(OwnerField, out long decodedOwner) || decodedOwner != owner) return null;
            if (_readGroupMembership && kind.Name == "ElementGroup" && cls.Id == 1322 && source.IdWidth == 4
                && TfBlockDeserializer.ExtractParams(data, start, (uint)size, (ushort)cls.Id) is { } parameters
                && NativeTypePointers.Of((ushort)cls.Id, parameters) is int groupType
                && walk.Captures.Any(capture => capture.Key == GroupType && !capture.AtRoot
                    && capture.Kind == WideBlockWalker.CaptureKind.Integer && capture.Integer == groupType)
                && !values.TryAdd(GroupType, groupType)) return null;
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
            if (copies[i].Partition is not int holder || !holders.Add(holder)
                || root.Copies[i].GetValueOrDefault(MasterSymbol) <= 0) return null;
            if (holder == partition) latest = i;
        }
        if (latest < 0 || holders.Max() != partition || root.Field(MasterSymbol) != null) return null;
        // The increment named by ElemTable lost its record. Use the last holder just for the symbol link.
        long symbol = root.Copies[latest][MasterSymbol];
        return GetRoot(symbol) is { } target && _walker.IsA(target.Class.Id, "FamilySymbol") ? symbol : null;
    }

    private long? RoutedType(long owner, List<Source> copies, int current, long oldType)
    {
        string kind = copies[0].Class.Name;
        if (!RoutedTypes.TryGetValue(kind, out var link) || GetRoot(owner) is not { } root) return null;
        var partitions = new HashSet<int>();
        bool retainedTypeMatches = false;
        long type = 0;
        for (int i = 0; i < copies.Count; i++)
        {
            if (copies[i].Partition is not int partition || !partitions.Add(partition)
                || !root.Copies[i].TryGetValue(link.Field, out long storedType) || storedType <= 0) return null;
            if (partition == current) type = storedType;
            else if (storedType == oldType) retainedTypeMatches = true;
        }
        if (!retainedTypeMatches || type == oldType || type == owner || GetRoot(type) is not { } target) return null;
        return _walker.IsA(target.Class.Id, link.Target)
            || kind == "SWall" && _walker.IsA(target.Class.Id, "NewCurtainWallType") ? type : null;
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
