using System.Buffers.Binary;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Lookup;
using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>Type links backed by complete project records in the file's own schema.</summary>
internal sealed class FramedElementTypes
{
    private const string OwnerField = "Element.m_id";
    private const string ViewerView = "Viewer.m_dbViewId";
    private const string CenterLineOwner = "CenterLine.m_OwnerId";
    private const string BoundaryType = "BoundaryConditions.m_type";
    private const string BoundaryPick = "LoadBCBase.m_oPick";
    private const string PickElement = "ElemPick.m_elemId";
    private const string ViewType = "DBView.m_dbViewTypeId";
    private const string ViewMarker = "DBView.m_viewerId";
    private const string ViewportType = "Viewport.m_attrId";
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
    private const string WallSweepType = "WallSweep.m_typeId";
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
    private const string CurveDetail = "CurveElem.m_detail";
    private const string CurveFamily = "Element.m_famId";
    private const string CurveUserData = "CurveElemData.m_oUserData";
    private const string CellList = "Element.m_cellList";
    private const string CurvePlane = "CurveElem.m_sketchPlaneId";
    private const string SketchPlane = "Sketch.m_sketchPlaneId";
    private const string HeaderCategory = "ElementHeader.m_categroryId";
    private const string VisibilityFlags = "FamElemVisibility.m_flags";
    private const string IntegerStore = "Element.m_pParamValueSetInt";
    private const string ParameterSet = "ParamValueSetInt.m_paramSet";
    private const string IntegerParameter = "ParamValueInt.m_paramId";

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
        OwnerField, HeaderCategory, ViewerView, CenterLineOwner, BoundaryType, BoundaryPick, PickElement,
        ViewType, ViewMarker, ViewportType, ReferenceViewer, MarkerParent, MarkerType, StairOwner,
        StairType, RunType, LandingType, FabricType, RailType, ActualSpanSymbol, TagSymbol, MasterSymbol,
        SupportHost, SupportOverridden, LeftSupport, RightSupport, LeftSupportStyle, RightSupportStyle, IntermediateSupport,
        GridChainType, RebarContainerType, RebarInstance, RebarType, SoffitType, RampType, WallSweepType,
    ];
    private static readonly HashSet<string> UntypedClasses =
    [
        "EnergyAnalysisSurface", "EnergyAnalysisOpening", "LegendComponent", "AreaMeasureElem",
        "PlanRegion", "RefPlane", "AnalysisDisplayStyle", "CurtainGridLine", "Opening", "ReferencePoint",
        "ScheduleInstance", "BuildingOperatingYearSchedule", "DPart", "FaceSplitter", "PartMaker", "Matchline",
        "Property", "RevisionCloud", "ShaftOpening", "BoundaryConditions", "ProjectPhase",
        "GStyleElem", "CategoryElem", "FontElem", "ParamElemFamily",
        "ActiveGeoLocationTrackingElement", "AllPlanTopologies", "AllProjectPhases", "AllProjectRevisions",
        "AreaSchemePlanTopologies", "AreaTypeElem", "AssemblyTracker", "AutoCamSettingsElem", "BaseLevelTracker",
        "BlendElem", "ComplexOpening", "ConceptualSurfaceType", "ConduitSizesElem", "CopyWatchProperties",
        "DBDrawing", "DataStorage", "DaylightSourceIdSet", "Dwg2dExportUserSettingsData",
        "EnergyAnalysisConstruction", "EnergyAnalysisDetailModel", "EnergyAnalysisMaterial", "EnergyAnalysisWindowType",
        "EnergyAnalysisZone", "EnergyDataSettings", "ExtentElem", "ExtrusionElem", "FamSymSurrogate", "Family",
        "FamilyGeomCombination", "FamilySurrogate", "FillPatternElem", "FormElem", "GCSTracker", "HubsTracker",
        "InitialViewSettings", "KeynoteTable", "KeynoteTagsOnSheetsTracker", "KeynotingSystem", "LayoutNodesTracker",
        "LegendComponentGRepHolder", "LevelRoomPlan", "LightGroup", "LightSchemeElement", "MEPSystemTracker",
        "ModelGraphicsStyle", "NestedFamSymRefElem", "NonPlanarSketch", "ParamBinding", "ParamElemExternal",
        "ParamElemRefTableEntry", "ParameterFilterElement", "Path3d", "PenWidthTableElem", "PhaseFilterElem",
        "PostedWarningElem", "RbsDuctSettingsElem", "RbsPipeSettingsElem", "RbsPipeSizesElem", "RbsWireSettingsElem",
        "RbsWireSizesElem", "RefTableElem", "RegenSplitterElem", "RevisionCloudsOnSheetsTracker",
        "ScheduleInstanceBaseOnSheetsTracker", "SelectionFilterElement", "SketchPlane", "SlaveSymbolTrackerElem",
        "StructSettingsElem", "SweepElem", "TrueNorth", "UnitsElem", "VarSketch", "ViewReferencing", "ViewSheetSet",
        "WallJoinDefaultSetting", "WatchElem", "WorksetVisibilitySettingsElem",
    ];
    private static readonly HashSet<string> Classes =
    [
        "Viewer", "Viewer3d", "DBView3d", "DBViewType", "DBViewSection", "DBViewPlan", "Section", "InteriorElevArrow", "InteriorElev",
        "DBViewSchedule", "WallSweep", "Viewport", "ViewportAttributes",
        "FabricSheet", "FabricSheetType", "StairsRun", "StairsLanding", "StairsElement", "StairsType",
        "StairsRunType", "StairsLandingType", "HandRail", "TopRail", "HandRailType", "TopRailType",
        "SpanDirectionSymbol", "FamilyInstance", "StairsSupport", "StairsSupportType", "GridChain", "GridAttributes",
        "RebarContainer", "RebarContainerType", "Rebar", "RebarBarType", "RoofSoffit", "RoofSoffitType", "Ramp", "RampAttributes",
    ];

    private static readonly HashSet<string> CategoryParameterSources =
    [
        "Viewer", "Viewer3d", "Section", "InteriorElevArrow", "SegmentCenterLine",
        "PipeFittingCenterLine", "DuctFittingCenterLine", "PropertySetElement", "BoundaryConditions",
        "RbsPipeCurve", "RbsDuctCurve", "RbsConduitCurve", "CableTray",
    ];

    private enum HeaderParameterValue { InvalidOnly, NamedOnly, InvalidOrNamed }
    private readonly Dictionary<int, HeaderParameterValue> _headerParameterClasses = new();
    private readonly HashSet<long> _headerParameterOwners = [];

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

    private readonly SchemaDocument _schema;
    private readonly WideBlockWalker _walker;
    private readonly Lazy<WideBlockWalker> _familyVisibility;
    private readonly HashSet<int> _classes;
    private readonly HashSet<int> _untypedClasses;
    private readonly bool _readGroupMembership;
    private readonly Dictionary<long, List<Source>> _sources = new();
    private readonly Dictionary<long, Root?> _roots = new();

    private FramedElementTypes(SchemaDocument schema, IReadOnlyDictionary<Guid, SchemaField[]?> entities,
        bool recoverUnanchoredTypes, bool recoverRoutedTypes, bool englishCurveNames, bool applyTypeLinks, bool projectCategories)
    {
        _schema = schema;
        var extraFields = recoverUnanchoredTypes ? UnanchoredTypes.Values.Select(link => link.Field).ToHashSet() : null;
        if (recoverRoutedTypes) extraFields!.UnionWith(RoutedTypes.Values.Select(link => link.Field));
        _walker = new WideBlockWalker(schema, (declaring, field) =>
        {
            string key = declaring.Name + "." + field.Name;
            return key == "InteriorElev.m_arrowIds" ? ArrowList
                : englishCurveNames && key is CurveDetail or CurveFamily or CurveUserData
                    or CellList or GroupType or CurvePlane or SketchPlane ? key
                : CapturedFields.Contains(key) || extraFields?.Contains(key) == true ? key : null;
        }, [], captureRootPointers: true);
        _walker.UseEntitySchemas(entities, passUnknown: true);
        _familyVisibility = new(() =>
        {
            var walker = new WideBlockWalker(schema, (cls, field) =>
            {
                string key = cls.Name + "." + field.Name;
                return key is VisibilityFlags or IntegerStore or ParameterSet or IntegerParameter ? key : null;
            }, [], captureRootPointers: true);
            walker.UseEntitySchemas(entities);
            return walker;
        });
        var extraTargets = recoverUnanchoredTypes ? UnanchoredTypes.Values.Select(link => link.Target).ToHashSet() : null;
        if (recoverRoutedTypes)
        {
            extraTargets!.UnionWith(RoutedTypes.Values.Select(link => link.Target));
            extraTargets.Add("NewCurtainWallType");
            extraTargets.Add("StackedWallType");
            _readGroupMembership = SchemaRelease.FromEvidence(
                schema.Classes.Find(cls => cls.Name == "ContentMarker")?.Id, schema.Classes.Count) == 2022;
        }
        _untypedClasses = applyTypeLinks ? schema.Classes.Where(cls => UntypedClasses.Contains(cls.Name) || _walker.IsA(cls.Id, "Symbol"))
            .Select(cls => cls.Id).ToHashSet() : [];
        if (applyTypeLinks && projectCategories)
            foreach (var cls in schema.Classes)
            {
                if (_walker.IsA(cls.Id, "Symbol") || _walker.IsA(cls.Id, "ParamElem") || _walker.IsA(cls.Id, "DBView"))
                    _headerParameterClasses.Add(cls.Id, HeaderParameterValue.InvalidOrNamed);
                else if (cls.Name is "RoomTag" or "LegendComponent" or "SWall" or "FamilyInstance" || _walker.IsA(cls.Id, "Dimension"))
                    _headerParameterClasses.Add(cls.Id, HeaderParameterValue.NamedOnly);
                else if (cls.Name is "GStyleElem" or "ParamElemFamily" or "CategoryElem" or "FontElem" or "ParamBinding"
                    or "SketchPlane" or "PostedWarningElem" or "Family" or "DBDrawing" or "AppearanceAssetElem"
                    or "ExtentElem" or "FillPatternElem" or "RefTableElem" or "LinePatternElem"
                    or "LightGroup" or "LightSchemeElement" or "AreaTypeElem" or "LevelRoomPlan"
                    or "AllPlanTopologies" or "AreaSchemePlanTopologies"
                    || _walker.IsA(cls.Id, "FamilySurrogateBase") || _walker.IsA(cls.Id, "SketchBase") || _walker.IsA(cls.Id, "FilterElement"))
                    _headerParameterClasses.Add(cls.Id, HeaderParameterValue.InvalidOnly);
            }
        _classes = schema.Classes.Where(cls => englishCurveNames && cls.Name is ("CurveElem" or "Family" or "VarSketch")
                || projectCategories && cls.Name == "BoundaryConditions"
                || applyTypeLinks && (Classes.Contains(cls.Name) || projectCategories && CategoryParameterSources.Contains(cls.Name)
                    || _untypedClasses.Contains(cls.Id) || _walker.IsA(cls.Id, "FamilySymbol")
                    || _walker.IsA(cls.Id, "DBView") || _walker.IsA(cls.Id, "DBViewType")
                    || recoverUnanchoredTypes && (UnanchoredTypes.ContainsKey(cls.Name) || extraTargets!.Contains(cls.Name))
                    || recoverRoutedTypes && RoutedTypes.ContainsKey(cls.Name)))
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
        IReadOnlyDictionary<ulong, int>? unroutedTypePartitions = null, bool recoverUnanchoredTypes = false,
        bool englishCurveNames = false, bool applyTypeLinks = true, bool projectCategories = true)
    {
        if (frames.Count == 0 || frames.Any(frame => frame == null)) return 0;
        var routes = recoverUnanchoredTypes ? state.IncrementRoutedPartitions : null;
        var reader = new FramedElementTypes(schema, entities, recoverUnanchoredTypes, routes is { Count: > 0 }, englishCurveNames, applyTypeLinks, projectCategories);
        var ids = state.ElementIds;
        long SourceId(int key) => ids?.SourceId(key) ?? key;
        bool LocalId(long sourceId, out int key)
        {
            key = 0;
            return sourceId > 0 && (ids != null ? ids.TryGetKey(sourceId, out key)
                : sourceId <= int.MaxValue && (key = (int)sourceId) > 0);
        }
        int? CurrentPartition(long sourceId)
            => LocalId(sourceId, out int key) && state.IncrementRoutedPartitions?.TryGetValue((uint)key, out int part) == true
                ? part : null;

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
                if (reader._headerParameterClasses.ContainsKey(tag)) reader._headerParameterOwners.Add((long)item.Owner);
                if (!reader._classes.Contains(tag) || reader._walker.Class(tag) is not { } kind) continue;
                bool routed = routes?.ContainsKey((uint)key) == true && RoutedTypes.ContainsKey(kind.Name);
                if (!routed && (!Classes.Contains(kind.Name) && !UnanchoredTypes.ContainsKey(kind.Name)
                        && RoutedTypes.ContainsKey(kind.Name)
                    || UnanchoredTypes.ContainsKey(kind.Name) && state.NativeParamsFromRootClass.Contains((uint)key)
                    || kind.Name == "FamilyInstance" && (!recoverUnanchoredTypes && !details.Contains((long)item.Owner)
                        && unroutedTypePartitions?.ContainsKey(item.Owner) != true
                        || state.IncrementRoutedPartitions?.ContainsKey((uint)key) == true))) continue;
                if (!reader._sources.TryGetValue((long)item.Owner, out var copies))
                    reader._sources.Add((long)item.Owner, copies = []);
                copies.Add(new(frame, item, kind));
            }

        // A second record of an unsupported class still makes the owner's type ambiguous.
        var recordCounts = new Dictionary<long, int>();
        var headerOwners = new HashSet<long>();
        foreach (var element in elements)
        {
            long owner = SourceId(element.Id);
            if (!reader._sources.TryGetValue(owner, out var copies)) continue;
            string kind = copies[0].Class.Name;
            if (kind == "CurveElem" && englishCurveNames && !element.IsBlockOnly && !element.NativeHeaderCategoryId.HasValue)
                headerOwners.Add(owner);
        }
        var framedCategories = new Dictionary<long, int?>();
        foreach (var frame in frames.Cast<PartitionFraming.Framed>())
            foreach (var item in frame.Items)
            {
                if (item.Channel == 102 && item.Owner <= long.MaxValue && reader._sources.ContainsKey((long)item.Owner))
                    recordCounts[(long)item.Owner] = recordCounts.GetValueOrDefault((long)item.Owner) + 1;
                else if (item.Channel == 101 && item.Owner <= long.MaxValue && headerOwners.Contains((long)item.Owner))
                {
                    long owner = (long)item.Owner;
                    int? category = reader.ReadHeaderCategory(frame, item);
                    framedCategories[owner] = framedCategories.TryGetValue(owner, out var prior) && category != prior ? null : category;
                }
            }
        foreach (var (owner, count) in recordCounts)
            if (reader._sources[owner].Count != count) reader._sources.Remove(owner);

        int changed = 0;
        foreach (var element in elements)
        {
            long owner = SourceId(element.Id);
            if (!reader._sources.TryGetValue(owner, out var copies)) continue;
            if (copies[0].Class.Name == "CurveElem")
            {
                if (!element.IsBlockOnly && reader.ReadCurveName(owner, element.NativeHeaderCategoryId ?? framedCategories.GetValueOrDefault(owner)) is string name)
                    element.Name = element.ValidatedCurveName = name;
                continue;
            }
            long? type = copies[0].Class.Name switch
            {
                "Viewer" => reader.PrimaryViewerType(owner, CurrentPartition),
                "Viewer3d" => reader.ViewerType(owner),
                "Section" => reader.SectionType(owner, CurrentPartition),
                "DBViewSchedule" or "DBViewPlan" or "DBViewProject" or "RbsDbViewSystemNavigator"
                    when element.TypeId != -1 && reader.ReadRoot(owner)?.Field(ViewType) == -1 => -1,
                "Viewport" => reader.ViewportAttributeId(owner),
                "WallSweep" when element.TypeId != -1 && reader.ReadRoot(owner)?.Field(WallSweepType) == -1 => -1,
                _ when element.TypeId != -1 && reader._untypedClasses.Contains(copies[0].Class.Id)
                    && reader.ReadRoot(owner) != null => -1,
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
                    && state.IncrementRoutedPartitions?.ContainsKey((uint)element.Id) != true
                    => reader.DirectType(owner, "FamilyInstance", MasterSymbol, "FamilySymbol"),
                "FamilyInstance" when recoverUnanchoredTypes && copies.Count == 1 && element.TypeId > 0
                    && state.IncrementRoutedPartitions?.ContainsKey((uint)element.Id) != true
                    && reader.IsNonSymbolRecord(SourceId(element.TypeId))
                    => reader.DirectType(owner, "FamilyInstance", MasterSymbol, "FamilySymbol"),
                "FamilyInstance" when unroutedTypePartitions?.TryGetValue((ulong)owner, out int partition) == true
                    => reader.UnroutedFamilyType(owner, copies, partition),
                // A single exact record can supply the type even when the stream walk never anchored on it.
                var kind when copies.Count == 1 && UnanchoredTypes.TryGetValue(kind, out var link)
                    && !state.NativeParamsFromRootClass.Contains((uint)element.Id)
                    => reader.DirectType(owner, kind, link.Field, link.Target),
                _ => null,
            };
            if (type == null && copies.Count > 1 && element.TypeId > 0
                && routes?.TryGetValue((uint)element.Id, out int current) == true)
                type = reader.RoutedType(owner, copies, current, SourceId(element.TypeId));
            int typeKey = -1;
            if (type is not long sourceType || sourceType == owner || sourceType != -1 && !LocalId(sourceType, out typeKey)
                || element.TypeId == typeKey) continue;
            element.TypeId = typeKey;
            changed++;
        }
        if (projectCategories)
        {
            var projectFrames = frames.Cast<PartitionFraming.Framed>().ToArray();
            if (applyTypeLinks)
                reader.ApplyProjectCategories(projectFrames, elements, state, unroutedTypePartitions);
            else
                reader.ApplyCategoryParameters(projectFrames,
                    elements.Where(e => e.RootClassName == "BoundaryConditions" && state.ElemTable.ContainsKey((uint)e.Id))
                        .ToDictionary(e => SourceId(e.Id)), state);
        }
        return changed;
    }

    private sealed class CategoryCandidate(Element element, int category)
    {
        internal Element Element { get; } = element;
        internal int Category { get; } = category;
        internal int? RootClass { get; set; }
    }

    private void ApplyProjectCategories(IReadOnlyList<PartitionFraming.Framed> frames,
        IReadOnlyList<Element> elements, ParsingState state, IReadOnlyDictionary<ulong, int>? unroutedTypePartitions)
    {
        var owners = elements.Where(e => state.ElemTable.ContainsKey((uint)e.Id))
            .ToDictionary(e => state.ElementIds?.SourceId(e.Id) ?? e.Id);
        ApplyCategoryParameters(frames, owners, state);
        var links = new List<(long Owner, long Type, int Partition, Element Element)>();
        var linkHeaders = new Dictionary<long, Dictionary<int, int?>>();
        foreach (var (owner, element) in owners)
        {
            if (element.TypeId <= 0 || !_sources.TryGetValue(owner, out var copies) || copies.Count < 2
                || copies[0].Class.Name is not ("FamilyInstance" or "SWall")) continue;
            long type = state.ElementIds?.SourceId(element.TypeId) ?? element.TypeId;
            if (state.IncrementRoutedPartitions?.TryGetValue((uint)element.Id, out int partition) != true
                && (copies[0].Class.Name != "FamilyInstance"
                    || unroutedTypePartitions?.TryGetValue((ulong)owner, out partition) != true
                    || UnroutedFamilyType(owner, copies, partition) != type)) continue;
            if (!_sources.ContainsKey(type)) continue;
            links.Add((owner, type, partition, element));
            if (!linkHeaders.ContainsKey(owner)) linkHeaders.Add(owner, []);
            if (!linkHeaders.ContainsKey(type)) linkHeaders.Add(type, []);
        }
        var candidates = new Dictionary<long, CategoryCandidate>();
        foreach (var frame in frames)
            foreach (var item in frame.Items)
            {
                if (item.Channel != 101 || item.Owner > long.MaxValue) continue;
                long owner = (long)item.Owner;
                linkHeaders.TryGetValue(owner, out var editions);
                if (!owners.TryGetValue(owner, out var element) && editions == null) continue;
                int? stored = ReadHeaderCategory(frame, item);
                if (editions != null)
                {
                    int part = frame.Partition ?? -1;
                    if (!editions.TryAdd(part, stored)) editions[part] = null;
                }
                if (element == null) continue;
                if (stored is int category && (category == -1
                        ? element.CategoryId.HasValue || element.Category != null : element.CategoryId != category))
                {
                    if (!candidates.TryGetValue(owner, out var prior))
                    {
                        candidates.Add(owner, new(element, category));
                        continue;
                    }
                    if (prior.Category == category) continue;
                }
                // An unreadable or conflicting header, or one agreeing with the public value,
                // rules out a correction even if another retained edition suggests one.
                owners.Remove(owner);
                candidates.Remove(owner);
            }

        foreach (var partition in frames.GroupBy(f => f.Partition))
        {
            var balance = new Dictionary<long, int>();
            foreach (var frame in partition)
                foreach (var item in frame.Items)
                {
                    if (item.Channel is not (101 or 102) || item.Owner > long.MaxValue
                        || !candidates.TryGetValue((long)item.Owner, out var candidate)) continue;
                    long owner = (long)item.Owner;
                    balance[owner] = balance.GetValueOrDefault(owner) + (item.Channel == 101 ? 1 : -1);
                    if (item.Channel == 101) continue;
                    int? tag = CategoryRootClass(frame, item);
                    if (tag == null || candidate.RootClass.HasValue && candidate.RootClass != tag)
                        candidates.Remove(owner);
                    else candidate.RootClass = tag;
                }
            foreach (var (owner, difference) in balance)
                if (difference != 0) candidates.Remove(owner);
        }

        foreach (var (owner, type, partition, element) in links)
        {
            var headers = linkHeaders[owner];
            if (headers.GetValueOrDefault(partition) is not int category || category == element.CategoryId
                || !headers.Values.Any(value => value != category)
                || !HasPairedEditionHeaders(headers, _sources[owner]) || !HasPairedEditionHeaders(linkHeaders[type], _sources[type])
                || linkHeaders[type].Values.Any(value => value != category)
                || !EditionHasType(owner, partition, type, headers, category)) continue;
            candidates[owner] = new(element, category);
        }

        foreach (var candidate in candidates.Values)
        {
            int category = candidate.Category;
            string? name = null;
            if (category != -1 && !BuiltInCategoryNames.Lookup.TryGetValue(category, out name))
            {
                if (category <= 0 || state.CategoryNames.GetValueOrDefault((uint)category)
                    is not (1, { } customName)) continue;
                name = customName;
            }
            candidate.Element.CategoryId = category == -1 ? null : category;
            candidate.Element.Category = name;
        }
    }

    private sealed class CategoryRecords
    {
        internal readonly Dictionary<int, int> Balance = [];
        internal long? Category;
        internal int? RootClass;
        internal bool Usable = true;

        internal bool Complete => Usable && Category.HasValue && RootClass.HasValue && Balance.Values.All(n => n == 0);
    }

    private void ApplyCategoryParameters(IReadOnlyList<PartitionFraming.Framed> frames,
        Dictionary<long, Element> owners, ParsingState state)
    {
        var links = new List<(long Owner, string Kind, long? Target)>();
        var records = new Dictionary<long, CategoryRecords>();
        long? RegisterTarget(long owner, long? id)
        {
            if (id is not > 0 || id == owner) return null;
            int key;
            if (state.ElementIds != null ? !state.ElementIds.TryGetKey(id.Value, out key)
                : id > int.MaxValue || (key = (int)id.Value) <= 0) return null;
            if (!state.ElemTable.ContainsKey((uint)key)) return null;
            if (!records.ContainsKey(id.Value)) records.Add(id.Value, new());
            return id;
        }
        foreach (long owner in _headerParameterOwners)
            records.Add(owner, new());
        var fittings = owners.Where(pair => pair.Value.CategoryId is (int)BuiltInCategory.OST_PipeFitting
            or (int)BuiltInCategory.OST_DuctFitting).ToArray();
        foreach (var (owner, _) in fittings)
            records.TryAdd(owner, new());
        foreach (var (owner, copies) in _sources)
        {
            string kind = copies[0].Class.Name;
            if (!CategoryParameterSources.Contains(kind) || !owners.ContainsKey(owner) || GetRoot(owner) is not { } root) continue;
            long? target = null;
            if (kind is "Viewer" or "Viewer3d" or "Section" or "InteriorElevArrow"
                or "SegmentCenterLine" or "PipeFittingCenterLine" or "DuctFittingCenterLine")
            {
                target = RegisterTarget(owner, root.Field(kind is "SegmentCenterLine" or "PipeFittingCenterLine" or "DuctFittingCenterLine"
                    ? CenterLineOwner : ViewerView));
                if (target == null) continue;
            }
            else if (kind == "BoundaryConditions")
                target = RegisterTarget(owner, root.Field(PickElement));
            if (!records.TryGetValue(owner, out var source)) records.Add(owner, source = new());
            source.RootClass = root.Class.Id;
            links.Add((owner, kind, target));
        }
        if (records.Count == 0) return;

        foreach (var frame in frames)
            foreach (var item in frame.Items)
            {
                if (item.Channel is not (101 or 102) || item.Owner > long.MaxValue
                    || !records.TryGetValue((long)item.Owner, out var record) || !record.Usable) continue;
                if (frame.Partition is not int part || part < 0) { record.Usable = false; continue; }
                record.Balance[part] = record.Balance.GetValueOrDefault(part) + (item.Channel == 101 ? 1 : -1);
                if (item.Channel == 101)
                {
                    long? category = ReadHeaderCategoryId(frame, item);
                    record.Usable = category.HasValue && (!record.Category.HasValue || record.Category == category);
                    record.Category = category;
                }
                else
                {
                    // GetRoot already verified every body for a source. Linked targets can be outside _sources.
                    if (_roots.GetValueOrDefault((long)item.Owner) is { } verified)
                        record.RootClass = verified.Class.Id;
                    else
                    {
                        int? cls = CategoryRootClass(frame, item);
                        record.Usable = cls.HasValue && (!record.RootClass.HasValue || record.RootClass == cls);
                        record.RootClass = cls;
                    }
                }
            }

        // A run and its center line report the volume of the run's own coarse solid.
        WideSolidQuantities? geometry = null;
        var volumes = new Dictionary<long, double>();
        foreach (var (owner, kind, _) in links)
        {
            var category = kind switch
            {
                "RbsPipeCurve" => BuiltInCategory.OST_PipeCurves,
                "RbsDuctCurve" => BuiltInCategory.OST_DuctCurves,
                "RbsConduitCurve" => BuiltInCategory.OST_Conduit,
                "CableTray" => BuiltInCategory.OST_CableTray,
                _ => BuiltInCategory.INVALID,
            };
            if (category == BuiltInCategory.INVALID || records[owner] is not { Complete: true, Category: long recorded }
                || recorded != (int)category || owners[owner].CategoryId != (int)category) continue;
            var run = owners[owner];
            if (category == BuiltInCategory.OST_CableTray)
                Revit2027ApiValues.SetUnsetArea(run);
            var existing = run.Parameters.FirstOrDefault(p => p.Id == (int)BuiltInParameter.HOST_VOLUME_COMPUTED && p.HasRevitValue);
            double volume;
            if (existing == null)
            {
                geometry ??= new WideSolidQuantities(_schema, frames, frames[0].IdWidth, state.ElementIds);
                if (geometry.OwnCoarseVolume(run.Id) is not double measured) continue;
                volume = measured;
                Revit2027ApiValues.SetQuantity(run, BuiltInParameter.HOST_VOLUME_COMPUTED, volume);
            }
            else if (existing is { FromNativeFields: true, StorageType: StorageType.Double, Value: double saved }
                && saved > 0 && double.IsFinite(saved)) volume = saved;
            else continue;
            if (category == BuiltInCategory.OST_CableTray) continue;
            volumes.Add(owner, volume);

            // The physical run exposes an unset Area parameter. Its center line has no such slot.
            Revit2027ApiValues.SetUnsetArea(run);
        }

        foreach (var (owner, fitting) in fittings)
        {
            if (records[owner] is not { Complete: true, Category: long category, RootClass: int cls }
                || category != fitting.CategoryId || _walker.Class(cls)?.Name != "FamilyInstance") continue;
            geometry ??= new WideSolidQuantities(_schema, frames, frames[0].IdWidth, state.ElementIds);
            Revit2027ApiValues.ApplyHistoricalFittingQuantities(fitting, geometry);
        }

        foreach (var (owner, kind, target) in links)
        {
            var source = records[owner];
            if (!source.Complete) continue;
            if (kind == "BoundaryConditions")
            {
                // Historical headers can carry the parent category for all three kinds of support.
                int? subcategory = GetRoot(owner)?.Field(BoundaryType) switch
                {
                    0 => -2005302, // OST_PointBoundaryConditions
                    1 => -2005303, // OST_LineBoundaryConditions
                    2 => -2005304, // OST_AreaBoundaryConditions
                    _ => null,
                };
                if (subcategory is int id && (source.Category == (int)BuiltInCategory.OST_BoundaryConditions || source.Category == id))
                {
                    var element = owners[owner];
                    element.CategoryId = id;
                    element.Category = BuiltInCategoryNames.Lookup[id];
                    SetCategoryParameters(element, id, true);
                    if (target is long pick && records[pick] is { Complete: true, RootClass: int targetClass }
                        && _walker.Class(targetClass)?.Name is "AnalyticalMember" or "AnalyticalPanel"
                            or "AnalyticalModelColumn" or "AnalyticalModelBeam" or "AnalyticalModelWall" or "AnalyticalModelSlab")
                        element.HostId ??= state.ElementIds?.Key(pick) ?? checked((int)pick);
                }
                continue;
            }
            if (target == null)
            {
                if (kind == "PropertySetElement" && source.Category == (int)BuiltInCategory.OST_PropertySet)
                    SetCategoryParameters(owners[owner], -1, false);
                continue;
            }
            var host = records[target.Value];
            if (!host.Complete || host.RootClass is not int cls || host.Category is not long category
                || category is < int.MinValue or > int.MaxValue) continue;
            string? hostKind = _walker.Class(cls)?.Name;
            bool matches = kind switch
            {
                "SegmentCenterLine" => hostKind is "RbsPipeCurve" or "RbsDuctCurve" or "RbsConduitCurve",
                "PipeFittingCenterLine" => hostKind == "FamilyInstance" && category == (int)BuiltInCategory.OST_PipeFitting,
                "DuctFittingCenterLine" => hostKind == "FamilyInstance" && category == (int)BuiltInCategory.OST_DuctFitting,
                _ => _walker.IsA(cls, "DBView"),
            };
            if (matches)
            {
                SetCategoryParameters(owners[owner], (int)category, true);
                if (kind == "SegmentCenterLine" && volumes.TryGetValue(target.Value, out double volume))
                    Revit2027ApiValues.SetQuantity(owners[owner], BuiltInParameter.HOST_VOLUME_COMPUTED, volume);
                if (kind is "PipeFittingCenterLine" or "DuctFittingCenterLine"
                    && owners.TryGetValue(target.Value, out var fitting) && fitting.CategoryId == category)
                {
                    Revit2027ApiValues.CopyPositiveQuantity(owners[owner], fitting, BuiltInParameter.HOST_AREA_COMPUTED);
                    Revit2027ApiValues.CopyPositiveQuantity(owners[owner], fitting, BuiltInParameter.HOST_VOLUME_COMPUTED);
                }
            }
        }

        foreach (long owner in _headerParameterOwners)
        {
            var record = records[owner];
            if (!owners.TryGetValue(owner, out var element) || !record.Complete || record.Category is not long category
                || record.RootClass is not int cls || !_headerParameterClasses.TryGetValue(cls, out var policy)) continue;
            if (category == (int)BuiltInCategory.INVALID && policy != HeaderParameterValue.NamedOnly
                || policy != HeaderParameterValue.InvalidOnly && category is >= int.MinValue and < -1
                    && BuiltInCategoryNames.Lookup.ContainsKey((int)category))
                SetCategoryParameters(element, (int)category, true);
            else if (category > 0 && _walker.Class(cls)?.Name is ("MasterImportSymbol" or "SlaveImportSymbol")
                && records.TryGetValue(category, out var target) && target.Complete && target.Category == -1
                && target.RootClass is int targetClass && _walker.Class(targetClass)?.Name == "CategoryElem")
                SetCategoryParameters(element, state.ElementIds?.Key(category) ?? checked((int)category), true);
        }
    }

    private static void SetCategoryParameters(Element element, int category, bool hasValue)
    {
        Set(-1140362, "ELEM_CATEGORY_PARAM");
        Set(-1140363, "ELEM_CATEGORY_PARAM_MT");

        void Set(int id, string name)
        {
            Parameter? slot = null;
            foreach (var parameter in element.Parameters)
            {
                if (parameter.Id != id) continue;
                if (slot != null) return;
                slot = parameter;
            }
            if (slot == null) element.Parameters.Add(slot = BipHelpers.MakeBipParam(id, name, StorageType.ElementId, category));
            else
            {
                slot.Value = Parameter.BoxParsedValue(category);
                slot.StorageType = StorageType.ElementId;
                slot.IsApproximate = false;
            }
            slot.HasNoRevitValue = !hasValue;
            slot.HasComputedInvalidCategory = category == -1 && hasValue;
        }
    }

    private static bool HasPairedEditionHeaders(Dictionary<int, int?> headers, List<Source> copies) =>
        headers.Count == copies.Count && copies.All(copy => copy.Frame.Partition is int part && part >= 0
            && headers.GetValueOrDefault(part).HasValue)
        && copies.Select(copy => copy.Frame.Partition).Distinct().Count() == copies.Count;

    private bool EditionHasType(long owner, int partition, long type, Dictionary<int, int?> headers, int category)
    {
        if (GetRoot(owner) is not { } occurrence || GetRoot(type) is not { } target) return false;
        var link = RoutedTypes[occurrence.Class.Name];
        if (!IsRoutedTypeTarget(occurrence.Class.Name, link.Target, target)) return false;
        // Check the type on the routed edition and the category of every other edition naming that type.
        for (int i = 0; i < occurrence.Copies.Count; i++)
        {
            int part = _sources[owner][i].Frame.Partition!.Value;
            if (!occurrence.Copies[i].TryGetValue(link.Field, out long stored) || stored <= 0
                || part == partition && stored != type || stored == type && headers[part] != category) return false;
        }
        return true;
    }

    private int? CategoryRootClass(PartitionFraming.Framed frame, PartitionFraming.ProjectItem item)
    {
        int size = item.Length - frame.IdWidth - 12;
        if (size < sizeof(ushort)) return null;
        var (data, start, _) = frame.Bytes(item);
        int offset = start + frame.IdWidth + 8;
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        // Boundary conditions can expose a derived subcategory in Revit.
        if (!_walker.IsA(tag, "Element") || _walker.IsA(tag, "BoundaryConditions")) return null;
        _walker.StepBudget = 16L * size + 65536;
        var walk = _walker.Run(data, offset, size, tag);
        if (!walk.Exact) return null;
        long? owner = null;
        foreach (var field in walk.Captures)
            if (field.AtRoot && field.Key == OwnerField && field.Kind == WideBlockWalker.CaptureKind.Integer)
            {
                if (owner.HasValue) return null;
                owner = field.Integer;
            }
        return owner == (long)item.Owner ? tag : null;
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
            int pick = kind.Name == "BoundaryConditions" && walk.RootPointers?.TryGetValue(BoundaryPick, out int selected) == true
                && _walker.Class(walk.Queue[selected])?.Name == "Pick" ? selected : -1;
            var values = new Dictionary<string, long>();
            foreach (var capture in walk.Captures)
            {
                if (capture.Kind == WideBlockWalker.CaptureKind.Integer
                    && (capture.Key switch
                    {
                        RebarType => rebarInstance >= 0 && capture.QueueIndex == rebarInstance,
                        PickElement => pick >= 0 && capture.QueueIndex == pick,
                        _ => capture.AtRoot,
                    })
                    && !values.TryAdd(capture.Key, capture.Integer)) return null;
                if (capture.Key == CurveUserData && capture.Kind == WideBlockWalker.CaptureKind.PointerClass
                    && capture.QueueIndex == -1 && !capture.AtRoot && !values.TryAdd(CurveUserData, capture.Integer))
                    return null;
            }
            if (!values.TryGetValue(OwnerField, out long decodedOwner) || decodedOwner != owner) return null;
            if (kind.Name == "CurveElem" && values.GetValueOrDefault(CurveDetail) == 1
                && values.GetValueOrDefault(CurveFamily) > 0 && FamilySketchId(walk) is long sketch
                && !values.TryAdd(GroupType, sketch)) return null;
            if (_readGroupMembership && kind.Name == "ElementGroup" && cls.Id == 1322 && frame.IdWidth == 4
                && TfBlockDeserializer.ExtractParams(data, start, (uint)size, (ushort)cls.Id) is { } parameters
                && NativeTypePointers.Of((ushort)cls.Id, parameters) is int groupType
                && walk.Captures.Any(capture => capture.Key == GroupType && !capture.AtRoot
                    && capture.Kind == WideBlockWalker.CaptureKind.Integer && capture.Integer == groupType)
                && !values.TryAdd(GroupType, groupType)) return null;
            fields.Add(values);
        }
        return new(kind, fields);
    }

    private long? FamilySketchId(WideBlockWalker.Walk walk)
    {
        if (walk.RootPointers?.TryGetValue(CellList, out int cell) != true
            || _walker.Class(walk.Queue[cell])?.Name != "CellList") return null;
        int member = -1;
        for (int i = walk.QueueCountAtStart[cell]; i < walk.QueueEnd[cell]; i++)
        {
            if (_walker.Class(walk.Queue[i])?.Name != "SketchMembership") continue;
            if (member >= 0) return null;
            member = i;
        }
        if (member < 0) return null;
        long? id = null;
        foreach (var field in walk.Captures)
            if (field.QueueIndex == member && field.Key == GroupType && field.Kind == WideBlockWalker.CaptureKind.Integer)
            {
                if (id.HasValue) return null;
                id = field.Integer;
            }
        return id;
    }

    private int? ReadHeaderCategory(PartitionFraming.Framed frame, PartitionFraming.ProjectItem item)
        => ReadHeaderCategoryId(frame, item) is long id && id is >= int.MinValue and <= int.MaxValue ? (int)id : null;

    private long? ReadHeaderCategoryId(PartitionFraming.Framed frame, PartitionFraming.ProjectItem item)
    {
        int size = item.Length - frame.IdWidth - 8;
        if (size < sizeof(ushort)) return null;
        var (data, start, _) = frame.Bytes(item);
        int offset = start + frame.IdWidth + 4;
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        if (_walker.Class(tag)?.Name != "ElementHeader") return null;
        _walker.StepBudget = 16L * size + 65536;
        var walk = _walker.Run(data, offset, size, tag);
        if (!walk.Exact) return null;
        long? category = null;
        foreach (var capture in walk.Captures)
            if (capture.AtRoot && capture.Key == HeaderCategory && capture.Kind == WideBlockWalker.CaptureKind.Integer)
            {
                if (category.HasValue) return null;
                category = capture.Integer;
            }
        return category;
    }

    private string? ReadCurveName(long owner, int? headerCategory)
    {
        if (headerCategory is not ((int)BuiltInCategory.OST_Lines or (int)BuiltInCategory.OST_SketchLines
            or (int)BuiltInCategory.OST_AreaSchemeLines or (int)BuiltInCategory.OST_StairsSketchBoundaryLines
            or (int)BuiltInCategory.OST_StairsSketchPathLines)) return null;
        var root = ReadRoot(owner);
        if (root?.Field(CurveUserData) is not long tag || tag < 0) return null;
        string? data = tag == 0 ? null : _walker.Class((int)tag)?.Name;
        if (tag > 0 && data == null) return null;
        if (root.Field(CurveFamily) is not long family) return null;
        long? detail = root.Field(CurveDetail);
        if (family != -1)
        {
            if (GetRoot(family, "Family") == null) return null;
            if (detail == 1
                && ((BuiltInCategory)headerCategory, data) is (BuiltInCategory.OST_Lines, null)
                    or (BuiltInCategory.OST_SketchLines, "FilledRegionCurveData")
                && root.Field(CurvePlane) is > 0 and long plane
                && GetRoot(root.Field(GroupType), "VarSketch") is { } sketch
                && sketch.Field(CurveFamily) == family && sketch.Field(SketchPlane) == plane) return "Line";
            if (detail != 0) return null;
            return ((BuiltInCategory)headerCategory, data) switch
            {
                (BuiltInCategory.OST_SketchLines, null or "FloorCurveData" or "ProfileRoofCurveData") => "Line",
                (BuiltInCategory.OST_Lines, null) => FamilyLineName(_sources[owner]),
                _ => null,
            };
        }
        return ((BuiltInCategory)headerCategory, detail, data) switch
        {
            (BuiltInCategory.OST_Lines, 1, null) => "Detail Lines",
            (BuiltInCategory.OST_SketchLines, 1, null or "FilledRegionCurveData" or "Cloud") => "Detail Lines",
            (BuiltInCategory.OST_Lines, 0, null) => "Model Lines",
            (BuiltInCategory.OST_SketchLines, 0, null or "FloorCurveData" or "ProfileRoofCurveData" or "RailingSketchCurveData" or "FabricAreaCurveData") => "Model Lines",
            (BuiltInCategory.OST_StairsSketchBoundaryLines or BuiltInCategory.OST_StairsSketchPathLines, 0, "StairsCurveData") => "Model Lines",
            (BuiltInCategory.OST_SketchLines, 0, "JoistSystemDirSpanData") => "Span Direction Edges",
            (BuiltInCategory.OST_AreaSchemeLines, 0, "AreaMeasureCurveData") => "Model Lines",
            _ => null,
        };
    }

    private string? FamilyLineName(List<Source> copies)
    {
        var walker = _familyVisibility.Value;
        string? name = null;
        foreach (var (frame, item, cls) in copies)
        {
            var (bytes, start, _) = frame.Bytes(item);
            int size = item.Length - frame.IdWidth - 12;
            walker.StepBudget = 16L * size + 65536;
            var walk = walker.Run(bytes, start + frame.IdWidth + 8, size, cls.Id);
            if (!walk.Exact || walk.RootPointers?.TryGetValue(IntegerStore, out int slot) != true
                || walker.Class(walk.Queue[slot])?.Name != "ParamValueSetInt") return null;

            long? flags = null, count = null;
            int pointers = 0;
            var keys = new HashSet<long>();
            foreach (var field in walk.Captures)
            {
                if (field.Key == IntegerStore && field.AtRoot)
                {
                    if (++pointers != 1 || field.Kind != WideBlockWalker.CaptureKind.PointerClass || field.Integer <= 0) return null;
                }
                if (field.Kind != WideBlockWalker.CaptureKind.Integer) continue;
                if (field.QueueIndex == -1 && field.Key == VisibilityFlags)
                {
                    if (flags.HasValue) return null;
                    flags = field.Integer;
                }
                if (field.QueueIndex != slot) continue;
                if (field.Key == ParameterSet + "#")
                {
                    if (count.HasValue) return null;
                    count = field.Integer;
                }
                if (field.Key == IntegerParameter && !keys.Add(field.Integer)) return null;
            }
            if (pointers != 1 || count != keys.Count || !keys.Contains((int)BuiltInParameter.IS_VISIBLE_PARAM)) return null;
            bool headOnPlacement = keys.Contains((int)BuiltInParameter.HEAD_ON_PLACEMENT_METHOD);
            string? current = flags switch
            {
                >= 0 when (flags.Value & 2) == 0 && headOnPlacement => "Symbolic Lines",
                -1 when !headOnPlacement => "Model Lines",
                >= 0 when (flags.Value & 2) != 0 && !headOnPlacement => "Model Lines",
                _ => null,
            };
            if (current == null || name != null && name != current) return null;
            name = current;
        }
        return name;
    }

    private long? ViewerType(long viewer)
    {
        var view = GetRoot(GetRoot(viewer, "Viewer3d")?.Field(ViewerView), "DBView3d");
        return view?.Field(ViewMarker) == viewer ? VerifyType(view.Field(ViewType), "DBViewType") : null;
    }

    private long? PrimaryViewerType(long owner, Func<long, int?> currentPartition)
    {
        var marker = GetRoot(owner, "Viewer");
        if (marker?.Field(ReferenceViewer) != 0 || marker.Field(ViewerView) is not long viewId || GetRoot(viewId) is not { } view
            || !_walker.IsA(view.Class.Id, "DBView") || view.Field(ViewMarker) != owner) return null;
        long? type = view.Field(ViewType);
        if (type == -1) return -1;
        type ??= ViewTypeFromCurrentCopy(viewId, view, currentPartition(viewId));
        return GetRoot(type) is { } target && _walker.IsA(target.Class.Id, "DBViewType") ? type : null;
    }

    private long? SectionType(long owner, Func<long, int?> currentPartition)
    {
        var marker = GetRoot(owner, "Section");
        if (marker?.Field(ViewerView) is not long viewId || GetRoot(viewId) is not { } view
            || view.Class.Name is not ("DBViewSection" or "DBViewPlan") || view.Field(ViewMarker) is not long primaryId)
            return null;
        // A reference marker points to the same view as its primary section.
        if (marker.Field(ReferenceViewer) != (primaryId == owner ? 0 : 1)) return null;
        if (primaryId != owner && (GetRoot(primaryId, "Section") is not { } primary
            || primary.Field(ReferenceViewer) != 0 || primary.Field(ViewerView) != viewId)) return null;
        return VerifyType(view.Field(ViewType) ?? ViewTypeFromCurrentCopy(viewId, view, currentPartition(viewId)), "DBViewType");
    }

    private long? ViewTypeFromCurrentCopy(long viewId, Root view, int? current)
    {
        if (current == null) return null;
        var copies = _sources[viewId];
        long? type = null;
        for (int i = 0; i < copies.Count; i++)
        {
            if (!view.Copies[i].TryGetValue(ViewType, out long storedType)) return null;
            if (copies[i].Frame.Partition != current) continue;
            if (type.HasValue) return null;
            type = storedType;
        }
        return type;
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

    private long? RoutedType(long owner, List<Source> copies, int current, long oldType)
    {
        string kind = copies[0].Class.Name;
        if (!RoutedTypes.TryGetValue(kind, out var link) || GetRoot(owner) is not { } root) return null;
        var partitions = new HashSet<int>();
        bool retainedTypeMatches = false;
        long type = 0;
        for (int i = 0; i < copies.Count; i++)
        {
            if (copies[i].Frame.Partition is not int partition || !partitions.Add(partition)
                || !root.Copies[i].TryGetValue(link.Field, out long storedType) || storedType <= 0) return null;
            if (partition == current) type = storedType;
            else if (storedType == oldType) retainedTypeMatches = true;
        }
        if (!retainedTypeMatches || type == oldType || type == owner || GetRoot(type) is not { } target) return null;
        return IsRoutedTypeTarget(kind, link.Target, target) ? type : null;
    }

    private bool IsRoutedTypeTarget(string kind, string expected, Root target) =>
        _walker.IsA(target.Class.Id, expected)
        || kind == "SWall" && (_walker.IsA(target.Class.Id, "NewCurtainWallType") || target.Class.Name == "StackedWallType");

    private bool IsNonSymbolRecord(long owner) => _sources.TryGetValue(owner, out var records)
        && !_walker.IsA(records[0].Class.Id, "FamilySymbol") && GetRoot(owner) != null;

    private long? DirectType(long id, string kind, string field, string target)
        => VerifyType(GetRoot(id, kind)?.Field(field), target);

    private long? ViewportAttributeId(long owner)
    {
        long? id = GetRoot(owner, "Viewport")?.Field(ViewportType);
        return id == -1 ? -1 : VerifyType(id, "ViewportAttributes");
    }

    private long? SpanType(long id)
    {
        var root = GetRoot(id, "SpanDirectionSymbol");
        return root?.Field(ActualSpanSymbol) is long symbol && root.Field(TagSymbol) == symbol
            ? VerifyType(symbol, "FamilySymbol") : null;
    }

    private long? VerifyType(long? id, string kind) => GetRoot(id, kind) == null ? null : id;
}
