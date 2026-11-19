using System.Globalization;
using RvtIO.Internal;

namespace RvtIO.Parsing;

/// <summary>
/// Turns the walk of an element block of a 64-bit-id file (<see cref="WideBlockWalker"/>) into the record the downstream
/// rules read (<see cref="TfBlockDeserializer.NativeParams"/>): the four parameter stores, the family parameter records
/// (NamedParam), the captured fields under their "Declaring:Name" keys and the cell classes.
/// The keys and class tags of that record are the ones of the Revit 2022 tables the rules were written against
/// ("160:m_styleSymbolId", class tag 127 for a rectangular duct profile ...). A class of the file is translated to its 2022
/// number by NAME, and only when the 2022 class declares a field of that name; a field or class tag without a 2022
/// counterpart is left out (the rules then see "not decoded"), never guessed.
/// </summary>
internal sealed partial class WideNativeParams
{
    private static readonly string[] StoreNames = ["ParamValueSetDouble", "ParamValueSetInt", "ParamValueSetAString", "ParamValueSetElementId"];

    /// <summary>The text fields that hold the own name of an element, as "Declaring.field", in the order they are tried. Each is
    /// exact for every root class that has it in the Revit 2025 files checked; the fields that were not exact
    /// (FillPattern, TilePattern, the family type pairs of a Family) are not listed. ParamDef.m_caption names the parameter
    /// elements only (a symbol queues ParamDefs too).</summary>
    private static readonly string[] NameFields =
    [
        "SymbolInfo.m_name", "FamilyBase.m_name", "FamilySurrogateBase.m_name", "Category.m_name", "Material.m_name",
        "AppearanceAsset.m_Name", "LinePattern.m_name", "DBView.m_viewName", "DatumPlane.m_text", "RbsSystem.m_strName",
        "HVACLoadSpaceTypeElem.m_name", "HVACLoadScheduleElem.m_strName", "FilterElement.m_name", "ElectricalLoadClassification.m_name",
        "ElectricalDemandFactorDefinition.m_name", "LoadMiscBaseElem.m_name", "RbsSegment.m_name", "MEPAnalyticalDistributionNode.m_strName",
        "ColorFillSchema.m_strName", "PanelScheduleTemplate.m_name", "InteriorElev.m_name", "PhaseFilterElem.m_name",
        "SunAndShadowSettings.m_sName", "ViewSheetSet.m_name", "GeoLocation.m_name", "ProjectPhase.m_name",
        "RevisionNumberingSequence.m_sequenceName", "PrintSettings.m_name", "KeynoteTable.m_name", "PropertySetLibrary.m_name",
        "ParamDef.m_caption",
    ];

    /// <summary>Revit's internal names ("SecretInternal..." styles) are not the names Revit prints.</summary>
    private const string InternalNamePrefix = "SecretInternal";

    internal const string GroupTypeKey = "301:m_groupTypeId";

    private const string ParamDefPointerKey = "2870:m_pParamDef.class";

    internal const string ViewerViewKey = "viewer:m_dbViewId";

    private static readonly string HostLayerPointer = TfClassNames.Key("HostObjAttr", "m_pCompoundStructure");
    private static readonly string HostLayerArray = TfClassNames.Key("CompoundStructure", "m_layers");
    private const string TilePatternPointer = "tile:m_oTilePattern";
    private const string TilePatternId = "tile:m_revitPatternId";
    internal const string TilePatternName = "tile:m_name";

    private const string RailTopHeightKey = "490:m_topRailHeight";

    private const string PlaneOriginKey = "plane:m_origin";

    private const string LineKeyPrefix = "gline:";

    private const string SplineNodeKeyPrefix = "snode:";

    // the key of the Revit 2022 table: the field is a captured key (TfBlockDeserializer.RoomRuleFieldKeys), so KeyOf names it this way
    private const string RoomPhaseKey = "3471:m_phaseId";

    /// <summary>The own field of a system type root that decides its system family (Floor / Foundation Slab, stair construction method ...),
    /// the ones <see cref="TfBlockDeserializer.TryExtractSystemTypeRoot"/> reads; they are not keyed by the Revit 2022 capture list.</summary>
    private static readonly HashSet<string> SystemDiscriminatorFields =
        ["m_footingType", "m_constructionMethod", "m_hasMonolithicSupport", "m_topsideSurfaceType", "m_bWithFitting"];

    private const string DiscriminatorKey = "sys:discriminator";

    private readonly WideBlockWalker _walker;
    private readonly HashSet<string> _nameFields = new(NameFields, StringComparer.Ordinal);
    private readonly SchemaDocument _schema;
    private readonly Dictionary<string, int> _r22ByName;
    private readonly Dictionary<int, TfBlockDeserializer.TfClass> _r22;
    private readonly HashSet<string> _bases;
    private readonly Dictionary<int, int> _toR22 = new();
    private readonly HashSet<int> _storeIds;
    private readonly int _namedParam;

    internal WideNativeParams(SchemaDocument schema)
    {
        _schema = schema;
        _r22 = TfBlockDeserializer.Schemas;
        _r22ByName = new(StringComparer.Ordinal);
        foreach (var (id, c) in _r22.OrderBy(pair => pair.Key)) _r22ByName.TryAdd(c.Name, id);
        // "Declaring:Name" of every captured key, without the "@index" / "#" suffix
        _bases = TfBlockDeserializer.CapturedKeys.Select(k => k.Split('@', '#')[0]).ToHashSet(StringComparer.Ordinal);
        _bases.Add(GroupTypeKey);
        _bases.Add(ParamDefPointerKey);
        _bases.Add(ViewerViewKey);
        _bases.UnionWith(Revit2027ApiValues.RootFields);
        foreach (var c in schema.Classes) _toR22[c.Id] = _r22ByName.GetValueOrDefault(c.Name);
        _storeIds = schema.Classes.Where(c => StoreNames.Contains(c.Name)).Select(c => c.Id).ToHashSet();
        _namedParam = schema.Classes.FirstOrDefault(c => c.Name == "NamedParam")?.Id ?? -1;
        _walker = new WideBlockWalker(schema, KeyOf,
            ["ParamValueDouble", "ParamValueInt", "ParamValueAString", "ParamValueElementId", "NamedParam", "ForgeTypeId", "FormatOptions", "AUnits",
                "CompoundStructureLayer", "Connector", "ConnectorId", "SegmentConnectorCalculation", "MEPFamilyConnectorCalculationHvac",
                "MEPFamilyConnectorCalculationPiping", "MEPFamilyConnectorCalculationElectrical"]);
    }

    /// <summary>The class number of the Revit 2022 table for a class of the file, 0 when that class has none.</summary>
    internal int R22Id(int classId) => _toR22.GetValueOrDefault(classId);

    internal WideBlockWalker Walker => _walker;

    private string? KeyOf(SchemaClass declaring, SchemaField field)
    {
        if (field.Type is 9 or 13) return null;
        if (declaring.Name == "HostObjAttr" && field.Name == "m_pCompoundStructure") return HostLayerPointer;
        if (declaring.Name == "CompoundStructure" && field.Name == "m_layers") return HostLayerArray;
        if ((declaring.Name, field.Name) == ("TilePatternType", "m_oTilePattern")) return TilePatternPointer;
        if ((declaring.Name, field.Name) == ("TilePattern", "m_revitPatternId")) return TilePatternId;
        if ((declaring.Name, field.Name) == ("TilePattern", "m_name")) return TilePatternName;
        if ((declaring.Name, field.Name) == ("Sketch", "m_absorbedCurves")) return SketchAbsorbedCurves;
        // The Revit 2022 rule that reads the Material parameter of a wall sweep type from its render style does not hold for every
        // type of a Revit 2025 file (Revit prints "None" for some with the same field set), so the field is not validated
        if (declaring.Name == "Symbol" && field.Name == "m_renderStyleId") return null;
        // Element.m_docAccess is a ControlledDocAccess whose only field is the back reference to the document (1 = this document)
        if (declaring.Name == "ControlledDocAccess" && field.Name == "m_pDoc") return "0:m_docAccess";
        // a parameter definition of the 2023+ layouts names its group by a ForgeTypeId (ParamDef.m_groupTypeId) instead of the
        // BuiltInParameterGroup number of ParamDef.m_groupElemId; the kind of the owned ParamDef is the class its ParamElem queues
        if (declaring.Name == "ParamDef" && field.Name == "m_groupTypeId") return GroupTypeKey;
        if (declaring.Name == "ParamElem" && field.Name == "m_pParamDef") return ParamDefPointerKey;
        // the view a viewer (a section mark, ...) stands for
        if (declaring.Name == "Viewer" && field.Name == "m_dbViewId") return ViewerViewKey;
        // the top rail height of a railing type (BaseRailingAttr and its subclasses, by the file's own class names), which
        // ElementBuilder.ApplySerializedRailingData reads bare as the shipped 2022 walk stores it
        if (declaring.Name == "BaseRailingAttr" && field.Name == "m_topRailHeight") return RailTopHeightKey;
        int r22 = _toR22.GetValueOrDefault(declaring.Id);
        string key = r22.ToString(CultureInfo.InvariantCulture) + ":" + field.Name;
        if (Revit2027ApiValues.RootFields.Contains(key)) return key;
        bool keyed = r22 != 0 && _r22[r22].Fields.Any(f => f.Name == field.Name) && _bases.Contains(key);
        bool rootName = TfBlockDeserializer.CapturedFieldNames.Contains(field.Name);
        bool name = field.Type == 8 && _nameFields.Contains(declaring.Name + "." + field.Name);
        if (keyed || rootName || name) return key;
        // the origin of the plane a datum plane (a Level) stands on, read by LevelElevation
        if (declaring.Name == "Plane" && field.Name == "m_origin") return PlaneOriginKey;
        // the straight drive curve (AddDriveLine)
        if ((declaring.Name, field.Name) is ("GLine", "m_origin" or "m_dirVec") or ("GCurve", "m_endParams"))
            return LineKeyPrefix + field.Name;
        // the nodes of the Hermite spline that drives a flexible duct or pipe (AddFlexCurveNodes)
        if (declaring.Name == "SplineNode" && field.Name is "m_iParametr" or "m_iPoint" or "m_iTangent")
            return SplineNodeKeyPrefix + field.Name;
        // the phases of the project in their authored order and the phase of a room, read by ProjectPhaseOrder and RoomPhase
        if (declaring.Name == "AllProjectPhases" && field.Name == "m_phaseIds") return WideBlockWalker.AllItemsKeyPrefix + "phases";
        if (declaring.Name == "RoomElem" && field.Name == "m_phaseId") return RoomPhaseKey;
        return SystemDiscriminatorFields.Contains(field.Name) ? DiscriminatorKey : null;
    }

    /// <summary>Ordered layers from the unique CompoundStructure owned by the exact host type root.</summary>
    internal List<CompoundLayer>? HostTypeLayers(WideBlockWalker.Walk walk, int rootClassId)
    {
        if (!walk.Exact || !_walker.IsA(rootClassId, "HostObjAttr")
            || walk.Captures.Where(c => c.AtRoot && c.Key == HostLayerPointer && c.Kind == WideBlockWalker.CaptureKind.PointerClass)
                .Take(2).ToArray() is not [{ Integer: > 0 and <= ushort.MaxValue } pointer])
            return null;

        int rootQueueSize = walk.QueueCountAtStart.FirstOrDefault();
        if (Enumerable.Range(0, rootQueueSize).Where(i => walk.Queue[i] == pointer.Integer).Take(2).ToArray() is not [int index]
            || _walker.Class(walk.Queue[index])?.Name != "CompoundStructure"
            || walk.Captures.Where(c => c.QueueIndex == index && c.Key == HostLayerArray + "#").Take(2).ToArray() is not [{ Integer: > 0 } count])
            return null;

        var layers = new List<CompoundLayer>();
        foreach (var record in walk.Records)
        {
            if (record.QueueIndex != index || _walker.Class(record.ClassId)?.Name != "CompoundStructureLayer") continue;
            var values = record.Values;
            if (values.GetValueOrDefault("m_layerWidth") is not double width || !double.IsFinite(width) || width < 0
                || values.GetValueOrDefault("m_layerFunction") is not long function || function is < int.MinValue or > int.MaxValue
                || values.GetValueOrDefault("m_materialId") is not long material || material is < int.MinValue or > int.MaxValue)
                return null;
            layers.Add(new() { ThicknessFt = width, Function = (int)function, MaterialId = (int)material, LayerIndex = layers.Count });
        }
        return layers.Count == count.Integer ? layers : null;
    }

    /// <summary>The own name of the element: the first non-empty text at the first <see cref="NameFields"/> field the walk reached.</summary>
    internal string? OwnName(WideBlockWalker.Walk walk, int rootClassId)
    {
        // the SymbolInfo name of a leader or dimension style is not always the name Revit prints
        if (_walker.IsA(rootClassId, "LeaderStyle") || _walker.IsA(rootClassId, "DimensionStyle")) return null;
        foreach (string field in NameFields)
        {
            if (field == "ParamDef.m_caption" && !_walker.IsA(rootClassId, "ParamElem")) continue;
            foreach (var c in walk.Captures)
            {
                if (c.Kind != WideBlockWalker.CaptureKind.Text || c.DeclaringClass + "." + c.Field != field) continue;
                if (c.Text is { Length: > 0 } text && !text.StartsWith(InternalNamePrefix, StringComparison.Ordinal)) return text;
                break;      // the first object of that class decides
            }
        }
        return null;
    }

    /// <summary>The elevation (feet) of a Level: the Z of the origin of its datum plane, which the Revit 2022 reader takes from the
    /// surface queued by DatumPlane.m_pSurface. Null for another class or when the plane was not reached.</summary>
    internal double? LevelElevation(WideBlockWalker.Walk walk, int rootClassId)
    {
        if (!_walker.IsA(rootClassId, "Level")) return null;
        foreach (var c in walk.Captures)
            if (c.Key == PlaneOriginKey + "@2" && c.Kind == WideBlockWalker.CaptureKind.Real && double.IsFinite(c.Real)) return c.Real;
        return null;
    }

    /// <summary>The ids of the project phases in their authored order (AllProjectPhases.m_phaseIds); null for another class.</summary>
    internal int[]? ProjectPhaseOrder(WideBlockWalker.Walk walk, int rootClassId)
    {
        if (!_walker.IsA(rootClassId, "AllProjectPhases")) return null;
        var ids = new SortedDictionary<int, int>();
        string prefix = WideBlockWalker.AllItemsKeyPrefix + "phases@";
        foreach (var c in walk.Captures)
            if (c.Key.StartsWith(prefix, StringComparison.Ordinal) && c.Kind == WideBlockWalker.CaptureKind.Integer
                && c.Integer is >= 0 and <= int.MaxValue)
                ids[int.Parse(c.Key[prefix.Length..], CultureInfo.InvariantCulture)] = (int)c.Integer;
        return ids.Count == 0 ? null : ids.Values.ToArray();
    }

    /// <summary>The phase of a room (RoomElem.m_phaseId); null for another class or a room without one.</summary>
    internal int? RoomPhase(WideBlockWalker.Walk walk, int rootClassId)
    {
        if (!_walker.IsA(rootClassId, "RoomElem")) return null;
        foreach (var c in walk.Captures)
            if (c.Key == RoomPhaseKey && c.Kind == WideBlockWalker.CaptureKind.Integer && c.Integer is >= 0 and <= int.MaxValue)
                return (int)c.Integer;
        return null;
    }

    /// <summary>The discriminator of a system type root: the value of the field of <see cref="SystemDiscriminatorFields"/> that the root
    /// class itself declares. Null for a class without one.</summary>
    internal int? SystemDiscriminator(WideBlockWalker.Walk walk, int rootClassId)
    {
        string rootName = _walker.Class(rootClassId)!.Name;
        foreach (var c in walk.Captures)
            if (c.AtRoot && c.DeclaringClass == rootName && SystemDiscriminatorFields.Contains(c.Field)
                && c.Kind == WideBlockWalker.CaptureKind.Integer && c.Integer is >= int.MinValue and <= int.MaxValue)
                return (int)c.Integer;
        return null;
    }

    /// <summary>The record of a walked block, null when the root fields could not be read. <paramref name="walk"/> is the walk of the block.</summary>
    internal TfBlockDeserializer.NativeParams? Build(WideBlockWalker.Walk walk, int rootClassId = -1)
    {
        if (!walk.RootRead) return null;
        var np = new TfBlockDeserializer.NativeParams();
        // keyed captures: the key is "<2022 id>:<name>[@i|#]"; a root field is also kept under its bare name
        foreach (var c in walk.Captures)
        {
            if (c.Key.Contains('#') && !c.AtRoot) continue;      // a count is kept for the root arrays only
            int colon = c.Key.IndexOf(':');
            string name = c.Key[(colon + 1)..];
            string key = c.Key;
            bool keyed = !c.Key.StartsWith("0:", StringComparison.Ordinal) && _bases.Contains(c.Key.Split('@', '#')[0])
                && (c.AtRoot || !Revit2027ApiValues.RootFields.Contains(c.Key));
            bool bare = (c.AtRoot || c.Key == "0:m_docAccess") && TfBlockDeserializer.CapturedFieldNames.Contains(name.Split('@', '#')[0]);
            if (c.Kind == WideBlockWalker.CaptureKind.PointerClass)
            {
                int value = (int)c.Integer;
                if (value > 0)
                {
                    value = R22Id(value);
                    if (value == 0) continue;      // a class the 2022 table does not know: not decided
                }
                if (keyed) np.Fields[key] = value;
                continue;
            }
            if (c.Kind == WideBlockWalker.CaptureKind.Integer)
            {
                if (c.Integer < int.MinValue || c.Integer > int.MaxValue) continue;
                if (keyed) np.Fields[key] = (int)c.Integer;
                if (bare) np.Fields[name] = (int)c.Integer;
            }
            else if (c.Kind == WideBlockWalker.CaptureKind.Real)
            {
                if (keyed) np.DoubleFields[key] = c.Real;
                if (bare) np.DoubleFields[name] = c.Real;
                if (c.AtRoot && c.Key == RailTopHeightKey) np.DoubleFields["m_topRailHeight"] = c.Real;
            }
            else if (c.Text != null)
            {
                if (keyed) np.StringFields[key] = c.Text;
                if (bare) np.StringFields[name] = c.Text;
            }
        }

        // parameter stores: the leading run of the queue
        int leading = 0;
        while (leading < walk.Queue.Count && _storeIds.Contains(walk.Queue[leading])) leading++;
        // A store behind the leading run is read as "not decoded completely". A material queues four stores of its own in the ParamSet of its
        // physical properties, behind the stores of the element; they hold no parameter of the element, so only the objects the
        // root queued count. Another class keeps the whole queue: the Class and Property Set Name of a property set element are
        // values of its nested objects.
        bool laterStore = false;
        int rootQueued = rootClassId >= 0 && _walker.IsA(rootClassId, "MaterialElem") && walk.QueueCountAtStart.Count > 0
            ? walk.QueueCountAtStart[0] : walk.Queue.Count;
        // Objects queued behind an extensible storage entity without values are not counted: the walk used to stop at that entity, and passing it must not
        // change which elements list their unset parameters (the reference output lists them).
        if (walk.EmptyEntityAt >= 0 && walk.EmptyEntityAt + 1 < walk.QueueCountAtStart.Count)
            rootQueued = Math.Min(rootQueued, walk.QueueCountAtStart[walk.EmptyEntityAt + 1]);
        for (int i = leading; i < rootQueued; i++) laterStore |= _storeIds.Contains(walk.Queue[i]);
        int finished = walk.QueueEnd.Count;
        np.RootParameterStoresComplete = !laterStore && finished >= leading;
        foreach (var (classId, queueIndex, values) in walk.Records)
        {
            if (queueIndex >= 0 && queueIndex < leading)
            {
                string name = _walker.Class(classId)!.Name;
                int id = Int(values, "m_paramId", int.MinValue);
                if (id == int.MinValue) { np.RootParameterStoresComplete = false; continue; }
                switch (name)
                {
                    case "ParamValueDouble": np.Doubles.Add((id, Real(values, "m_value"))); break;
                    case "ParamValueInt": np.Ints.Add((id, Int(values, "m_value", 0))); break;
                    case "ParamValueAString": np.Strings.Add((id, values.GetValueOrDefault("m_value") as string ?? "")); break;
                    case "ParamValueElementId": np.ElementIds.Add((id, Int(values, "m_value", -1))); break;
                }
            }
            // a NamedParam read inline from a field of the root (FamilySymbol.m_subInstData: the instance parameters of the components
            // nested in the family) is not a parameter of the element; the element's own ones are the items of its queued FamilyParams
            else if (classId == _namedParam && !values.ContainsKey(WideBlockWalker.RootFieldKey))
            {
                np.NamedParams.Add(new TfBlockDeserializer.NamedParamRecord(
                    Int(values, "m_paramId", -1), values.GetValueOrDefault("m_str") as string, Real(values, "m_value"),
                    Int(values, "m_elemId", -1), Int(values, "m_int", 0),
                    Int(values, "m_instance", 0) != 0, Int(values, "m_reporting", 0) != 0));
            }
        }

        AddConnectorRecords(np, walk);
        AddDriveLine(np, walk, rootClassId);
        AddFlexCurveNodes(np, walk, rootClassId);
        if (rootClassId >= 0 && _walker.Class(rootClassId)?.Name == "TilePatternType") ReadTilePatternName(np, walk);
        if (rootClassId >= 0 && _walker.Class(rootClassId)?.Name == "VarSketch" && ReadSketchLineArea(walk) is double area)
            np.DoubleFields[SketchLineArea] = area;
        if (walk.Exact && rootClassId >= 0 && _walker.Class(rootClassId)?.Name == "Roof")
        {
            var extrusions = Enumerable.Range(0, walk.Queue.Count)
                .Where(slot => _walker.Class(walk.Queue[slot])?.Name == "ExtrusionGStep").Take(2).ToArray();
            if (extrusions is [var step])
                np.RoofExtrusionFaceTags = walk.Captures.Where(c => c.QueueIndex == step && c.DeclaringClass == "FaceHistEntry"
                    && c.Field == "m_id" && c.Kind == WideBlockWalker.CaptureKind.Integer && c.Integer is >= 0 and <= int.MaxValue)
                    .Select(c => (int)c.Integer).ToHashSet();
        }

        // cell classes: what the CellList object queued
        for (int qi = 0; qi < walk.QueueEnd.Count; qi++)
        {
            if (_walker.Class(walk.Queue[qi])?.Name != "CellList") continue;
            np.CellClasses = Enumerable.Range(walk.QueueCountAtStart[qi], walk.QueueEnd[qi] - walk.QueueCountAtStart[qi])
                .Select(i => R22Id(walk.Queue[i])).ToList();
            break;
        }
        np.TailWalkComplete = walk.Exact;
        return np;
    }

    private void ReadTilePatternName(TfBlockDeserializer.NativeParams np, WideBlockWalker.Walk walk)
    {
        if (!walk.Exact || walk.Captures.Where(c => c.AtRoot && c.Key == TilePatternPointer
            && c.Kind == WideBlockWalker.CaptureKind.PointerClass).Take(2).ToArray() is not [{ Integer: > 0 } pointer]) return;
        int rootQueueSize = walk.QueueCountAtStart.FirstOrDefault();
        if (Enumerable.Range(0, rootQueueSize).Where(i => walk.Queue[i] == pointer.Integer).Take(2).ToArray() is not [int index]
            || !_walker.IsA(walk.Queue[index], "TilePattern")) return;
        if (walk.Captures.Where(c => c.QueueIndex == index && c.Key == TilePatternName && c.Kind == WideBlockWalker.CaptureKind.Text)
            .Take(2).ToArray() is not [{ Text: { Length: > 0 } name }]) return;
        if (walk.Captures.Where(c => c.QueueIndex == index && c.Key == TilePatternId && c.Kind == WideBlockWalker.CaptureKind.Integer)
            .Take(2).ToArray() is not [{ Integer: var pattern }]) return;
        np.StringFields[TilePatternName] = (pattern, name) switch
        {
            (0, "<No Pattern>") => "_No Pattern",
            (2, "Triangle") or (7, "Triangle Checkerboard") or (10, "Triangle Step") => name + " (bent)",
            _ => name,
        };
    }

    /// <summary>The straight drive curve of a wall, duct, pipe, conduit or cable tray (the GLine its CurveDriver queued) under the keys the
    /// Revit 2022 walk gives it ("87:m_pCrv.origin@i", ".dirVec@i", ".endParams@i"), which ElementBuilder.ApplyMepCurveGeometry reads for
    /// the length of the element. Nothing is written when the driver queued no GLine, more than one, or one with a value missing
    /// (an arc or a spline driver).</summary>
    private void AddDriveLine(TfBlockDeserializer.NativeParams np, WideBlockWalker.Walk walk, int rootClassId)
    {
        if (rootClassId < 0 || R22Id(rootClassId) is not (3148 or 3210 or 3134 or 598 or 3524)) return;
        bool wall = R22Id(rootClassId) == 3524;
        int line = -1;
        for (int owner = 0; owner < walk.QueueEnd.Count; owner++)
        {
            if (!_walker.IsA(walk.Queue[owner], "CurveDriver")) continue;
            if (wall && (owner >= walk.QueueCountAtStart[0] || _walker.Class(walk.Queue[owner])?.Name != "VWallDriver")) continue;
            for (int child = walk.QueueCountAtStart[owner]; child < walk.QueueEnd[owner]; child++)
            {
                if (_walker.Class(walk.Queue[child])?.Name != "GLine") continue;
                if (line >= 0) return;
                line = child;
            }
        }
        if (line < 0) return;
        var values = new Dictionary<string, double>();
        foreach (var c in walk.Captures)
            if (c.QueueIndex == line && c.Kind == WideBlockWalker.CaptureKind.Real && c.Key.StartsWith(LineKeyPrefix, StringComparison.Ordinal)
                && double.IsFinite(c.Real))
                values[c.Key[LineKeyPrefix.Length..]] = c.Real;
        string[] targets = ["origin", "dirVec", "endParams"];
        string[] sources = ["m_origin", "m_dirVec", "m_endParams"];
        int[] counts = [3, 3, 2];
        for (int part = 0; part < 3; part++)
            for (int i = 0; i < counts[part]; i++)
                if (!values.ContainsKey($"{sources[part]}@{i}")) return;
        for (int part = 0; part < 3; part++)
            for (int i = 0; i < counts[part]; i++)
                np.DoubleFields[$"87:m_pCrv.{targets[part]}@{i}"] = values[$"{sources[part]}@{i}"];
    }

    /// <summary>The nodes of the Hermite spline that drives a flexible duct or pipe (the GHermiteSpline its CurveDriver queued), seven
    /// values each (parameter, point, tangent), under the keys of the Revit 2022 walk ("87:m_pCrv.nodes", "87:m_pCrv.node{i}@{k}"), which
    /// ElementBuilder.FlexCurveLength reads. Nothing is written for another root, a driver without exactly one spline, or nodes that
    /// are not complete.</summary>
    private void AddFlexCurveNodes(TfBlockDeserializer.NativeParams np, WideBlockWalker.Walk walk, int rootClassId)
    {
        if (rootClassId < 0 || R22Id(rootClassId) is not (3177 or 3180)) return;
        int spline = -1;
        for (int owner = 0; owner < walk.QueueEnd.Count; owner++)
        {
            if (!_walker.IsA(walk.Queue[owner], "CurveDriver")) continue;
            for (int child = walk.QueueCountAtStart[owner]; child < walk.QueueEnd[owner]; child++)
            {
                if (_walker.Class(walk.Queue[child])?.Name != "GHermiteSpline") continue;
                if (spline >= 0) return;
                spline = child;
            }
        }
        if (spline < 0) return;
        var values = new List<double>();
        string[] order = ["m_iParametr", "m_iPoint@0", "m_iPoint@1", "m_iPoint@2", "m_iTangent@0", "m_iTangent@1", "m_iTangent@2"];
        foreach (var c in walk.Captures)
        {
            if (c.QueueIndex != spline || c.Kind != WideBlockWalker.CaptureKind.Real || !c.Key.StartsWith(SplineNodeKeyPrefix, StringComparison.Ordinal)) continue;
            if (c.Key[SplineNodeKeyPrefix.Length..] != order[values.Count % order.Length] || !double.IsFinite(c.Real)) return;
            values.Add(c.Real);
        }
        if (values.Count < 2 * order.Length || values.Count % order.Length != 0) return;
        for (int i = 0; i < values.Count; i++)
            np.DoubleFields[$"87:m_pCrv.node{i / order.Length}@{i % order.Length}"] = values[i];
        np.DoubleFields["87:m_pCrv.nodes"] = values.Count / order.Length;
    }

    /// <summary>The connectors of the element and their calculations under the keys of the Revit 2022 walk
    /// (<c>CopyOccurrence</c>): "771#k:m_nIndex" and the ConnectorId entries of connector k, "3556#j:..." (2381, 2382, 2380) for
    /// the j-th calculation of its class and "calc#n:class/occurrence" for the n-th calculation of any class. Unlike the 2022 walk
    /// this one knows which objects a connector queued: "771#k:m_mode" is its mode and "771#k:calcClass/calcOccurrence" name the
    /// calculation among its own modifiers (absent for a connector with none, such as a communication or surface connector).
    /// A ConnectorId item is stored before the Connector that holds it.</summary>
    private void AddConnectorRecords(TfBlockDeserializer.NativeParams np, WideBlockWalker.Walk walk)
    {
        var pending = new List<Dictionary<string, object?>>();
        var occurrences = new Dictionary<string, int>();
        var ownerOfQueued = new Dictionary<int, int>();
        int connectors = 0, calculations = 0;
        foreach (var (classId, queueIndex, values) in walk.Records)
        {
            string name = _walker.Class(classId)!.Name;
            if (name == "ConnectorId") { pending.Add(values); continue; }
            if (name == "Connector")
            {
                string prefix = "771#" + connectors.ToString(CultureInfo.InvariantCulture) + ":";
                np.Fields[prefix + "m_nIndex"] = Int(values, "m_nIndex", 0);
                np.Fields[prefix + "m_mode"] = Int(values, "m_mode", 0);
                if (queueIndex >= 0 && queueIndex < walk.QueueEnd.Count)
                    for (int child = walk.QueueCountAtStart[queueIndex]; child < walk.QueueEnd[queueIndex]; child++)
                        ownerOfQueued[child] = connectors;
                np.Fields[prefix + "m_arrRefs#"] = pending.Count;
                for (int i = 0; i < pending.Count; i++)
                {
                    np.Fields[$"{prefix}m_arrRefs@{i}:id"] = Int(pending[i], "m_id", 0);
                    np.Fields[$"{prefix}m_arrRefs@{i}:index"] = Int(pending[i], "m_nIndex", 0);
                    np.Fields[$"{prefix}m_arrRefs@{i}:type"] = Int(pending[i], "m_connType", 0);
                }
                pending.Clear();
                connectors++;
                continue;
            }
            int r22 = R22Id(classId);
            if (r22 is not (3556 or 2380 or 2381 or 2382)) continue;
            int occurrence = occurrences.GetValueOrDefault(name);
            occurrences[name] = occurrence + 1;
            string order = "calc#" + calculations.ToString(CultureInfo.InvariantCulture) + ":";
            np.Fields[order + "class"] = r22;
            np.Fields[order + "occurrence"] = occurrence;
            string calc = r22.ToString(CultureInfo.InvariantCulture) + "#" + occurrence.ToString(CultureInfo.InvariantCulture) + ":";
            foreach (string field in new[] { "m_idSystem", "m_nSection", "m_eSystemType", "m_eDirection", "m_idDownstreamCircuit" })
                if (values.ContainsKey(field)) np.Fields[calc + field] = Int(values, field, 0);
            if (ownerOfQueued.TryGetValue(queueIndex, out int owner) && !np.Fields.ContainsKey($"771#{owner}:calcClass"))
            {
                np.Fields[$"771#{owner}:calcClass"] = r22;
                np.Fields[$"771#{owner}:calcOccurrence"] = occurrence;
            }
            calculations++;
        }
    }

    private static int Int(Dictionary<string, object?> values, string name, int fallback) =>
        values.GetValueOrDefault(name) is long v && v >= int.MinValue && v <= int.MaxValue ? (int)v : fallback;

    private static double Real(Dictionary<string, object?> values, string name) =>
        values.GetValueOrDefault(name) is double v ? v : 0;
}
