using System.Globalization;

namespace RvtIO.Parsing;

/// <summary>One piece of the centre line of a bar, in feet and model coordinates. A line is Origin + Axis * t for t in
/// [Start, End]; an arc is Origin (its centre) + Radius * (Axis cos a + Axis2 sin a) for a in [Start, End].</summary>
internal sealed record RebarSegment(bool IsArc, double[] Origin, double[] Axis, double[] Axis2, double Radius, double Start, double End)
{
    /// <summary>The segment moved by a Trf (row-major 3x3 followed by the origin: 12 numbers).</summary>
    internal RebarSegment Moved(double[] trf) =>
        this with { Origin = Point(trf, Origin), Axis = Vector(trf, Axis), Axis2 = IsArc ? Vector(trf, Axis2) : Axis2 };

    /// <summary>The lowest Z of the piece: the end points, and for an arc the lowest point of its circle when the arc passes it.</summary>
    internal double MinZ()
    {
        if (!IsArc) return Math.Min(Origin[2] + Axis[2] * Start, Origin[2] + Axis[2] * End);
        // z(a) = cz + r (xz cos a + yz sin a) = cz + r R cos(a - phi); the lowest point is at a = phi + pi (mod 2 pi)
        double min = Math.Min(Origin[2] + Radius * (Axis[2] * Math.Cos(Start) + Axis2[2] * Math.Sin(Start)),
            Origin[2] + Radius * (Axis[2] * Math.Cos(End) + Axis2[2] * Math.Sin(End)));
        double r = Math.Sqrt(Axis[2] * Axis[2] + Axis2[2] * Axis2[2]);
        if (r > 0)
        {
            double low = Math.Atan2(Axis2[2], Axis[2]) + Math.PI;
            double from = Math.Min(Start, End), to = Math.Max(Start, End);
            double turns = Math.Ceiling((from - low) / (2 * Math.PI));
            if (low + turns * 2 * Math.PI <= to) min = Math.Min(min, Origin[2] - Radius * r);
        }
        return min;
    }

    private static double[] Vector(double[] t, double[] v) =>
        [t[0] * v[0] + t[1] * v[1] + t[2] * v[2], t[3] * v[0] + t[4] * v[1] + t[5] * v[2], t[6] * v[0] + t[7] * v[1] + t[8] * v[2]];

    private static double[] Point(double[] t, double[] p)
    {
        var v = Vector(t, p);
        return [v[0] + t[9], v[1] + t[10], v[2] + t[11]];
    }
}

/// <summary>The bars one reinforcement element stands for: the bar type, the system it belongs to (RebarInSystem), and per bar the
/// centre line pieces in model coordinates (feet). <see cref="BarLength"/> is the stored length of one bar, when the element has one;
/// <see cref="ShapeName"/> is the name of the RebarShape of a shape driven bar (RebarInstBase.m_shapeId);
/// the diameters (feet) and the name are those of the bar type (RebarBarType.m_barNominalDiameter, m_barModelDiameter, SymbolInfo.m_name);
/// <see cref="LengthRoundingMillimetres"/> is the total length rounding of the document (ReinforcementSettings, nearest), 0 for none;
/// <see cref="InAssembly"/> is true for a bar that is a member of an assembly (AssemblyGroupMembership cell).
/// A RebarContainer holds no bars of its own: <see cref="Items"/> are its items (one bar each, <see cref="ItemIndex"/> is the index of the item
/// in the container) and <see cref="BarTypeName"/> is the name of the container type.
/// <see cref="OnlyBarOfSystem"/> is true for a RebarInSystem that is the one RebarInSystem of its system.
/// <see cref="ModelPaths"/> gives the bar paths with Revit model placement when it differs from the IFC exporter convention.</summary>
internal sealed record RebarBars(int BarTypeId, int SystemId, double? BarLength, IReadOnlyList<IReadOnlyList<RebarSegment>> Bars,
    double NominalDiameter = 0, double ModelDiameter = 0, string? BarTypeName = null, double LengthRoundingMillimetres = 0, string? ShapeName = null,
    bool InAssembly = false, IReadOnlyList<RebarBars>? Items = null, int ItemIndex = -1, bool OnlyBarOfSystem = false, int ShapeId = 0, bool IsFreeForm = false,
    IReadOnlyList<IReadOnlyList<RebarSegment>>? ModelPaths = null);

/// <summary>A beam system (JoistSystem): its type (JoistSystemAttr, <see cref="TypeName"/> from SymbolInfo.m_name) and the beams it holds
/// (HeldBeam.m_existingBeamId, in stored order, deleted ones left out).</summary>
internal sealed record JoistSystemData(int TypeId, string? TypeName, IReadOnlyList<int> BeamIds);

/// <summary>The reinforcement and the beam systems of a file by element id.</summary>
internal sealed record AreaSketchPlane(double[] Origin, double[] XAxis, double[] YAxis);

internal sealed record ReinforcementData(Dictionary<uint, RebarBars> Bars, Dictionary<uint, JoistSystemData> Joists,
    Dictionary<uint, AreaSketchPlane> AreaPlanes);

/// <summary>
/// Centre lines of the reinforcement of a Revit file, read with the schema-driven field walk (<see cref="WideBlockWalker"/>, classes and
/// fields by NAME of the file's own schema, so every release is read the same way).
///   Rebar (shape driven): RebarInstBase.m_oCacheData -> RebarInstCache -> m_noSelfIntersectingCenterlineCurves[0] -> OneBarCenterlineCurves
///     -> the GLine / GArc pieces of the first bar; bar i is that bar moved by RebarInstBase.m_barPositionsRelativeToFirstBarArr[i] (a Trf).
///   RebarSystem (area reinforcement): four RebarSystemLayer, each with m_uncutCurves (one GLine per bar, already placed); the
///     RebarInSystem element (HeldRebarInstanceKey.m_layer, m_systemId) is the bars of one layer.
/// The queued objects of a walk are numbered in reading order; the children of a queued object are the queue entries it appended
/// (<see cref="WideBlockWalker.Walk.QueueCountAtStart"/> .. <see cref="WideBlockWalker.Walk.QueueEnd"/>), which is how a pointer array is
/// followed. An element whose structure is not the one above (other curve classes, counts that do not add up) yields nothing.
/// </summary>
internal static partial class RebarReader
{
    private const string Positions = "m_barPositionsRelativeToFirstBarArr";

    internal static string? Key(SchemaClass declaring, SchemaField field)
    {
        string name = declaring.Name;
        bool wanted = name switch
        {
            "GCurve" => field.Name == "m_endParams",
            "GLine" or "GArc" or "Trf" or "OneBarCenterlineCurves" or "RebarInstCache" or "HeldRebarInstanceKey" or "Outline" => true,
            "RebarInstBase" => field.Name is Positions or "m_barTypeId" or "m_barLength" or "m_includedBars" or "m_numberOfBarPositions" or "m_individualBarsTrf" or "m_shapeId"
                or "m_hookTypeId0" or "m_hookTypeId1" or "m_hookOrient0" or "m_hookOrient1" or "m_endTreatmentTypeId0" or "m_endTreatmentTypeId1"
                or "m_orientationAngleAtStart" or "m_orientationAngleAtEnd" or "m_couplerId"
                or "m_endPar" or "m_maxSpacing" or "m_layoutRule" or "m_includeFirstBar" or "m_includeLastBar",
            "std::pair< ElementId, ParamVectorIterator >" => field.Name == "first",
            "ParamVectorIterator" => field.Name == "m_index",
            "ParamVectorValues" => field.Name == "m_doubles",
            "RebarShape" => field.Name is "m_hookOri" or "m_style" or "m_oMultiplanarDefinition" or "m_stirrupTieAttachment" or "m_higherEnd" or "m_orientationAngle",
            "RebarShapeDefinitionBySegments" => field.Name is "m_edges" or "m_vertices",
            "RebarShapeEdge" => field.Name is "m_constraints" or "m_arcHeightParamId" or "m_radiusParamId" or "m_type",
            "RebarShapeVertex" => field.Name is "m_constraints" or "m_bendAngle" or "m_turn",
            "RebarShapeConstraintEdgeLength" => field.Name is "m_paramId" or "m_refType",
            "RebarShapeConstraintProjEdgeLength" => field.Name is "m_paramId" or "m_refType",
            "XYZ" => field.Name == "m_xyz",
            "RebarHookType" => field.Name is "m_hookAngle" or "m_straightLineMultiplier" or "m_style",
            "std::pair< ElementId, HookLengthSettings >" => field.Name == "first",
            "HookLengthSettings" => field.Name is "m_hookLength" or "m_offsetLength" or "m_tangentLength",
            "RebarSystemLayer" => field.Name is "m_barType" or "m_active" or "m_uncutCurves" or "m_individualLineTrf"
                or "m_uncutRebarInstances" or "m_includedUncutBars",
            "RebarSystemBase" => field.Name == "m_sketchId",
            "Sketch" => field.Name is "m_absorbedCurves" or "m_pPlane",
            "Plane" => field.Name is "m_origin" or "m_xVec" or "m_yVec",
            "RebarInSystem" => field.Name == "m_systemId",
            "RebarContainer" => field.Name is "m_rebarContainerTypeId" or "m_hostId",
            "FamilyInstance" => field.Name is "m_instOrigin" or "m_RefDir" or "m_zAxis",
            "GroupMembership" => field.Name == "m_groupId",
            "RebarBarType" => field.Name is "m_barDiameter" or "m_barNominalDiameter" or "m_barModelDiameter" or "m_standardBendDiam" or "m_standardHookBendDiam" or "m_stirrupTieBendDiam",
            "SymbolInfo" => field.Name == "m_name",
            "JoistSystem" => field.Name == "m_symbolId",
            "HeldBeam" => field.Name is "m_existingBeamId" or "m_beamDeleted",
            "ForgeTypeId" => field.Name == "m_typeId",
            "RebarRoundingDataItem" => field.Name is "m_totalLengthRounding" or "m_totalLengthRoundingMethod",
            _ => false,
        };
        if (!wanted || field.Type == 9) return null;
        string key = name + "." + field.Name;
        return field.Name is "m_includedBars" or "m_includedUncutBars" or "m_doubles" or "m_absorbedCurves"
            ? WideBlockWalker.AllItemsKeyPrefix + key : key;
    }

    /// <summary>True for the schema of Revit 2020 and 2021, whose RebarSystemLayer has no m_uncutCurves: the bars of a system are the held
    /// instances of its RebarSystemGStep, not curves of the layers.</summary>
    internal static bool HeldLayout(FileSchema schema) =>
        schema.ByName("RebarSystemLayer") is { } layer && layer.Fields.All(f => f.Name != "m_uncutCurves");

    internal static ReinforcementData Read(
        IReadOnlyList<IReadOnlyList<byte[]>> streams, FileSchema schema, IReadOnlySet<uint> wanted,
        IReadOnlyDictionary<Guid, SchemaField[]?>? entitySchemas = null, bool passUnknownEntities = false,
        IReadOnlyList<PartitionFraming.Framed?>? framed = null, ElementIdMap? ids = null)
    {
        var result = new Dictionary<uint, RebarBars>();
        var joists = new Dictionary<uint, JoistSystemData>();
        var joistTypeNames = new Dictionary<int, string>();
        var shapeNames = new Dictionary<int, string>();
        var areaPlanes = new Dictionary<uint, AreaSketchPlane>();
        var empty = new ReinforcementData(result, joists, areaPlanes);
        var rebar = schema.ByName("Rebar")?.Id;
        var system = schema.ByName("RebarSystem")?.Id;
        var inSystem = schema.ByName("RebarInSystem")?.Id;
        var barType = schema.ByName("RebarBarType")?.Id;
        var settings = schema.ByName("ReinforcementSettings")?.Id;
        var shape = schema.ByName("RebarShape")?.Id;
        var hookType = schema.ByName("RebarHookType")?.Id;
        // the container classes exist in the schema of Revit 2022 and later; a file without them has no containers
        var container = schema.ByName("RebarContainer")?.Id;
        var containerType = schema.ByName("RebarContainerType")?.Id;
        var joist = schema.ByName("JoistSystem")?.Id;
        var joistType = schema.ByName("JoistSystemAttr")?.Id;
        if (rebar == null || system == null || inSystem == null || barType == null || settings == null || shape == null || joist == null || joistType == null) return empty;
        var walker = new WideBlockWalker(schema.Document, Key, [], captureRootPointers: true, ids: ids);
        if (entitySchemas != null) walker.UseEntitySchemas(entitySchemas, passUnknownEntities);
        var layers = new Dictionary<uint, List<Layer>>();
        var members = new Dictionary<uint, (int System, int Layer, int Index, bool InAssembly)>();
        var diameters = new Dictionary<int, (double Nominal, double Model, string? Name)>();
        var barKinds = new Dictionary<int, BarKind>();
        var shapeDefinitions = new Dictionary<int, ShapeDefinition>();
        var hookKinds = new Dictionary<int, HookKind>();
        var pending = new Dictionary<uint, PendingBar>();
        var containers = new Dictionary<uint, (int TypeId, int HostId, bool InAssembly, List<PendingBar> Items)>();
        var containerNames = new Dictionary<int, string>();
        var held = new Dictionary<(uint System, int Layer, int Index), PendingBar>();
        var areaSketches = new Dictionary<uint, uint>();
        bool heldLayout = HeldLayout(schema);
        double rounding = 0;
        WideElementRecords.BuildIndex(streams, schema, wanted, (id, data, offset, size, cls) =>
        {
            if (cls != rebar && cls != system && cls != inSystem && cls != barType && cls != settings && cls != shape && cls != joist && cls != joistType && cls != hookType
                && cls != container && cls != containerType) return;
            var walk = walker.Run(data, offset, size, cls, cls == shape ? (w, first) => ConstraintsValid(walker, w, first) : null);
            if (cls == system && walk.RootRead &&
                walk.Captures.FirstOrDefault(capture => capture.AtRoot && capture.Key == "RebarSystemBase.m_sketchId").Integer is > 0 and <= int.MaxValue and long sketch)
                areaSketches.TryAdd(id, (uint)sketch);
            // The bar of a Rebar that stopped behind an extensible storage entity is its RebarInstance, read before the entity payloads.
            if (cls == rebar && walk.BeforeEntityBlobs() is { } beforeBlobs)
            {
                if (ReadPending(walker, beforeBlobs, ByQueue(beforeBlobs)) is { } afterEntity) pending.TryAdd(id, afterEntity);
                return;
            }
            // a type or shape block may stop at a class the schema does not declare after its name (RebarShape: tag 31065); an element must be whole
            if (!walk.RootRead || walk.Error != null && cls != shape && cls != barType && cls != joistType) return;
            var byQueue = ByQueue(walk);
            if (cls == barType)
            {
                double? Real(string key) => byQueue.GetValueOrDefault(-1)?.Where(c => c.Key == key && c.Kind == WideBlockWalker.CaptureKind.Real)
                    .Select(c => (double?)c.Real).FirstOrDefault();
                string? name = byQueue.Values.SelectMany(list => list).Where(c => c.Key == "SymbolInfo.m_name").Select(c => c.Text).FirstOrDefault();
                if (Real("RebarBarType.m_barNominalDiameter") is double nominal && Real("RebarBarType.m_barModelDiameter") is double model)
                    diameters.TryAdd((int)id, (nominal, model, name));
                // Revit 2020 keeps one diameter
                else if (Real("RebarBarType.m_barDiameter") is double single) diameters.TryAdd((int)id, (single, single, name));
                if (ReadBarKind(byQueue) is { } kind) barKinds.TryAdd((int)id, kind);
            }
            else if (cls == hookType)
            {
                var root = byQueue.GetValueOrDefault(-1);
                if (RealOf(root, "RebarHookType.m_hookAngle") is double angle && RealOf(root, "RebarHookType.m_straightLineMultiplier") is double multiplier
                    && Integer(root, "RebarHookType.m_style") is long style)
                    hookKinds.TryAdd((int)id, new HookKind(angle, multiplier, (int)style));
            }
            else if (cls == containerType)
            {
                if (byQueue.Values.SelectMany(list => list).Where(c => c.Key == "SymbolInfo.m_name").Select(c => c.Text).FirstOrDefault() is { } containerName)
                    containerNames.TryAdd((int)id, containerName);
            }
            else if (cls == container) { if (ReadContainer(walker, walk, byQueue) is { } found) containers.TryAdd(id, found); }
            else if (cls == settings) rounding = Rounding(byQueue);
            else if (cls == shape)
            {
                if (byQueue.Values.SelectMany(list => list).Where(c => c.Key == "SymbolInfo.m_name").Select(c => c.Text).FirstOrDefault() is { } shapeName)
                    shapeNames.TryAdd((int)id, shapeName);
                if (ReadShapeDefinition(walker, walk, byQueue) is { } definition) shapeDefinitions.TryAdd((int)id, definition);
            }
            else if (cls == joistType)
            {
                if (byQueue.Values.SelectMany(list => list).Where(c => c.Key == "SymbolInfo.m_name").Select(c => c.Text).FirstOrDefault() is { } typeName)
                    joistTypeNames.TryAdd((int)id, typeName);
            }
            else if (cls == joist) { if (ReadJoist(byQueue) is { } found) joists.TryAdd(id, found); }
            else if (cls == rebar)
            {
                if (ReadRebar(walker, walk, byQueue) is { } bars) result.TryAdd(id, bars);
                else if (ReadPending(walker, walk, byQueue) is { } shapeDriven) pending.TryAdd(id, shapeDriven);
            }
            else if (cls == system)
            {
                if (ReadLayers(walker, walk, byQueue) is { } found) layers.TryAdd(id, found);
                if (heldLayout)
                    foreach (var ((layer, index), bar) in ReadHeld(walk.Captures)) held.TryAdd((id, layer, index), bar);
            }
            else if (ReadMember(walker, walk, byQueue) is { } member) members.TryAdd(id, member);
        }, framed, ids);
        if (areaSketches.Count > 0)
        {
            const string curvesKey = WideBlockWalker.AllItemsKeyPrefix + "Sketch.m_absorbedCurves";
            var planes = new Dictionary<uint, AreaSketchPlane>();
            WideElementRecords.BuildIndex(streams, schema, areaSketches.Values.ToHashSet(), (id, data, offset, size, cls) =>
            {
                if (!walker.IsA(cls, "Sketch")) return;
                var walk = walker.Run(data, offset, size, cls);
                // A sketch with a single direction line takes its reinforcement boundary from the host face.
                if (!walk.RootRead || walk.Captures.FirstOrDefault(c => c.AtRoot && c.Key == curvesKey + "#").Integer != 1
                    || walk.Captures.FirstOrDefault(c => c.AtRoot && c.Key == curvesKey + "@0") is not
                        { Kind: WideBlockWalker.CaptureKind.PointerClass, Integer: >= 0 and <= int.MaxValue } direction
                    || walker.Class((int)direction.Integer)?.Name != "GLine"
                    || walk.RootPointers?.TryGetValue("Sketch.m_pPlane", out int plane) != true
                    || plane >= walk.QueueEnd.Count || walker.Class(walk.Queue[plane])?.Name != "Plane")
                    return;

                double[]? Vector(string field)
                {
                    var values = walk.Captures.Where(c => c.QueueIndex == plane && c.Kind == WideBlockWalker.CaptureKind.Real
                        && c.Field == field).Select(c => c.Real).ToArray();
                    return values.Length == 3 && values.All(double.IsFinite) ? values : null;
                }
                if (Vector("m_origin") is { } origin && Vector("m_xVec") is { } x && Vector("m_yVec") is { } y)
                    planes.TryAdd(id, new(origin, x, y));
            }, framed, ids);
            foreach (var (id, sketch) in areaSketches)
                if (planes.TryGetValue(sketch, out var plane)) areaPlanes.Add(id, plane);
        }
        RebarBars? Solve(PendingBar bar) =>
            diameters.TryGetValue(bar.BarTypeId, out var barDiameters) && barKinds.TryGetValue(bar.BarTypeId, out var kind)
            && shapeDefinitions.TryGetValue(bar.ShapeId, out var definition) ? SolveShapeDriven(bar, definition, hookKinds, kind, barDiameters.Model) : null;
        foreach (var (id, bar) in pending)
            if (Solve(bar) is { } solved) result.TryAdd(id, solved);
        // the items of a container that can be solved; their bar types come with the pass, like those of the other bars. They are in the
        // coordinates of the host instance, so a container whose host has no frame is not written.
        var hostFrames = ReadHostFrames(walker, streams, framed, schema, containers.Values.Select(c => (uint)c.HostId).ToHashSet(), ids);
        foreach (var (id, (typeId, hostId, inAssembly, items)) in containers)
        {
            if (!hostFrames.TryGetValue((uint)hostId, out var hostFrame)) continue;
            var solvedItems = new List<RebarBars>();
            for (int index = 0; index < items.Count; index++)
                if (Solve(items[index]) is { } solvedItem && diameters.TryGetValue(items[index].BarTypeId, out var itemDiameters))
                    solvedItems.Add(solvedItem with
                    {
                        ItemIndex = index, InAssembly = inAssembly, ShapeName = null, NominalDiameter = itemDiameters.Nominal, ModelDiameter = itemDiameters.Model,
                        BarTypeName = itemDiameters.Name, LengthRoundingMillimetres = rounding,
                        Bars = MovePaths(solvedItem.Bars, hostFrame),
                        ModelPaths = solvedItem.ModelPaths is { } modelPaths ? MovePaths(modelPaths, hostFrame) : null,
                    });
            if (solvedItems.Count > 0 && containerNames.TryGetValue(typeId, out var typeName))
                result.TryAdd(id, new RebarBars(-1, 0, null, [], BarTypeName: typeName, Items: solvedItems, InAssembly: inAssembly));
        }
        var barsOfSystem = members.Values.GroupBy(member => member.System).ToDictionary(group => group.Key, group => group.Count());
        foreach (var (id, (systemId, layerIndex, index, inAssembly)) in members)
        {
            bool only = barsOfSystem[systemId] == 1;
            if (held.TryGetValue(((uint)systemId, layerIndex, index), out var heldBar))
            {
                if (Solve(heldBar) is { } solvedHeld)
                    result.TryAdd(id, solvedHeld with { SystemId = systemId, ShapeName = null, InAssembly = inAssembly, OnlyBarOfSystem = only });
                continue;
            }
            if (!layers.TryGetValue((uint)systemId, out var found) || layerIndex < 0 || layerIndex >= found.Count) continue;
            var layer = found[layerIndex];
            // the cut instances of the layer are the ones exported; the uncut instances are those of a layer nothing cut
            var cut = layer.Cuts != null && index >= 0 && index < layer.Cuts.Count && diameters.TryGetValue(layer.BarTypeId, out var barDiameters)
                ? CutBars(layer.Lines, layer.Cuts[index], barDiameters.Model / 2) : null;
            if (cut == null && (index < 0 || index >= layer.Instances.Count)) continue;
            result.TryAdd(id, new RebarBars(layer.BarTypeId, systemId, null, cut ?? layer.Instances[index], InAssembly: inAssembly, OnlyBarOfSystem: only));
        }
        // the diameters of the bar types come with the pass, so they are attached once every element was read
        foreach (var (id, bars) in result.ToList())
            if (bars.Items != null) continue;
            else if (diameters.TryGetValue(bars.BarTypeId, out var d))
                result[id] = bars with
                {
                    NominalDiameter = d.Nominal, ModelDiameter = d.Model, BarTypeName = d.Name, LengthRoundingMillimetres = rounding,
                    ShapeName = bars.ShapeName != null && int.TryParse(bars.ShapeName, NumberStyles.None, CultureInfo.InvariantCulture, out int shapeId)
                        ? shapeNames.GetValueOrDefault(shapeId) : null,
                };
            else result.Remove(id);
        foreach (var (id, data) in joists.ToList())
            joists[id] = data with { TypeName = joistTypeNames.GetValueOrDefault(data.TypeId) };
        return empty;
    }

    private static IReadOnlyList<IReadOnlyList<RebarSegment>> MovePaths(IReadOnlyList<IReadOnlyList<RebarSegment>> paths, double[] frame) =>
        paths.Select(path => (IReadOnlyList<RebarSegment>)path.Select(piece => piece.Moved(frame)).ToList()).ToList();

    /// <summary>A RebarContainer: its type and the shape driven bar of each of its RebarContainerItem objects (the walk of the root queues them in
    /// the order of the items); null when an item cannot be read.</summary>
    private static (int TypeId, int HostId, bool InAssembly, List<PendingBar> Items)? ReadContainer(
        WideBlockWalker walker, WideBlockWalker.Walk walk, Dictionary<int, List<WideBlockWalker.Capture>> byQueue)
    {
        var root = byQueue.GetValueOrDefault(-1);
        if (walk.Error != null || Integer(root, "RebarContainer.m_rebarContainerTypeId") is not long typeId || Integer(root, "RebarContainer.m_hostId") is not long hostId) return null;
        bool inAssembly = InAssembly(walker, walk, byQueue);
        var items = new List<PendingBar>();
        for (int q = 0; q < walk.Queue.Count; q++)
        {
            if (walker.Class(walk.Queue[q])?.Name != "RebarContainerItem") continue;
            if (!byQueue.TryGetValue(q, out var caps) || ReadPending(caps, inAssembly, containerItem: true) is not { } item) return null;
            items.Add(item);
        }
        return items.Count == 0 ? null : ((int)typeId, (int)hostId, inAssembly, items);
    }

    /// <summary>The frame (row-major 3x3 of the columns x direction (m_RefDir), its cross product with the axis, axis, then the origin: 12 numbers) of
    /// each of the FamilyInstance elements <paramref name="hosts"/> (FamilyInstance.m_instOrigin, m_RefDir and m_zAxis); a host that is no
    /// FamilyInstance or whose values are not a frame has none.</summary>
    private static Dictionary<uint, double[]> ReadHostFrames(WideBlockWalker walker, IReadOnlyList<IReadOnlyList<byte[]>> streams,
        IReadOnlyList<PartitionFraming.Framed?>? framed, FileSchema schema, IReadOnlySet<uint> hosts, ElementIdMap? ids)
    {
        var frames = new Dictionary<uint, double[]>();
        if (hosts.Count == 0) return frames;
        WideElementRecords.BuildIndex(streams, schema, hosts, (id, data, offset, size, cls) =>
        {
            if (walker.Class(cls)?.Name != "FamilyInstance") return;
            var root = ByQueue(walker.Run(data, offset, size, cls)).GetValueOrDefault(-1);
            double[]? Vector(string key)
            {
                var v = new double[3];
                for (int i = 0; i < 3; i++)
                    if (RealOf(root, $"{key}@{i}") is not double r || !double.IsFinite(r)) return null;
                    else v[i] = r;
                return v;
            }
            if (Vector("FamilyInstance.m_instOrigin") is not { } origin || Vector("FamilyInstance.m_RefDir") is not { } x || Vector("FamilyInstance.m_zAxis") is not { } z) return;
            double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
            if (Math.Abs(Dot(x, x) - 1) > 1e-9 || Math.Abs(Dot(z, z) - 1) > 1e-9 || Math.Abs(Dot(x, z)) > 1e-9) return;
            double[] y = [z[1] * x[2] - z[2] * x[1], z[2] * x[0] - z[0] * x[2], z[0] * x[1] - z[1] * x[0]];
            frames.TryAdd(id, [x[0], y[0], z[0], x[1], y[1], z[1], x[2], y[2], z[2], origin[0], origin[1], origin[2]]);
        }, framed, ids);
        return frames;
    }

    private static JoistSystemData? ReadJoist(Dictionary<int, List<WideBlockWalker.Capture>> byQueue)
    {
        if (!byQueue.TryGetValue(-1, out var root) || Integer(root, "JoistSystem.m_symbolId") is not long typeId) return null;
        // HeldBeam is an inline structure of the counted array m_heldBeams: its fields come in stored order, one group per beam
        var beams = new List<int>();
        long? beam = null;
        foreach (var c in root)
        {
            if (c.DeclaringClass != "HeldBeam" || c.Kind != WideBlockWalker.CaptureKind.Integer) continue;
            if (c.Field == "m_existingBeamId") beam = c.Integer;
            else if (c.Field == "m_beamDeleted" && beam is long id)
            {
                if (c.Integer == 0 && id > 0) beams.Add((int)id);
                beam = null;
            }
        }
        return new JoistSystemData((int)typeId, null, beams);
    }

    /// <summary>The total length rounding of ReinforcementSettings in millimetres, 0 when the document has none (no rounding cell) or its
    /// method is not "nearest" (0), the only one supported.</summary>
    private static double Rounding(Dictionary<int, List<WideBlockWalker.Capture>> byQueue)
    {
        var all = byQueue.Values.SelectMany(list => list).ToList();
        double? value = all.Where(c => c.Key == "RebarRoundingDataItem.m_totalLengthRounding" && c.Kind == WideBlockWalker.CaptureKind.Real)
            .Select(c => (double?)c.Real).FirstOrDefault();
        long? method = all.Where(c => c.Key == "RebarRoundingDataItem.m_totalLengthRoundingMethod" && c.Kind == WideBlockWalker.CaptureKind.Integer)
            .Select(c => (long?)c.Integer).FirstOrDefault();
        string? unit = all.Where(c => c.Key == "ForgeTypeId.m_typeId").Select(c => c.Text).FirstOrDefault();
        double? millimetres = unit switch
        {
            not null when unit.StartsWith("autodesk.unit.unit:millimeters-", StringComparison.Ordinal) => 1,
            not null when unit.StartsWith("autodesk.unit.unit:centimeters-", StringComparison.Ordinal) => 10,
            not null when unit.StartsWith("autodesk.unit.unit:meters-", StringComparison.Ordinal) => 1000,
            not null when unit.StartsWith("autodesk.unit.unit:fractionalInches-", StringComparison.Ordinal)
                || unit.StartsWith("autodesk.unit.unit:inches-", StringComparison.Ordinal) => 25.4,
            not null when unit.StartsWith("autodesk.unit.unit:feet-", StringComparison.Ordinal) => 304.8,
            _ => null,
        };
        return value is double v && v > 0 && method == 0 && millimetres is double scale ? v * scale : 0;
    }

    private static Dictionary<int, List<WideBlockWalker.Capture>> ByQueue(WideBlockWalker.Walk walk)
    {
        var byQueue = new Dictionary<int, List<WideBlockWalker.Capture>>();
        foreach (var c in walk.Captures)
        {
            if (!byQueue.TryGetValue(c.QueueIndex, out var list)) byQueue[c.QueueIndex] = list = [];
            list.Add(c);
        }
        return byQueue;
    }

    /// <summary>The bars of the walk of a Rebar root block (null when the block is not the cached shape driven layout).</summary>
    internal static RebarBars? ReadRebar(WideBlockWalker walker, WideBlockWalker.Walk walk) => ReadRebar(walker, walk, ByQueue(walk));

    /// <summary>One layer of a system: the bars of each of its RebarInstance objects (the RebarInSystem of key m_index is one of them).</summary>
    internal sealed record Layer(int BarTypeId, IReadOnlyList<IReadOnlyList<IReadOnlyList<RebarSegment>>> Instances,
        IReadOnlyList<IReadOnlyList<RebarSegment>> Lines, IReadOnlyList<CutInstance>? Cuts = null);

    /// <summary>One instance of a layer after the host cut it: the first bar (RebarInstCache -> OneBarCenterlineCurves -> GLine) and the
    /// bounding box of all its bars (Outline: the centre lines widened by the bar radius), in feet.</summary>
    internal sealed record CutInstance(RebarSegment First, double[] Min, double[] Max);

    /// <summary>The bars of a cut instance: the first bar moved to every line of the layer (the layer lines are the bar positions, all
    /// parallel and equal apart from the offset across the line) whose moved bar lies inside the Outline of the instance. The first bar
    /// sits on one line of the layer (offset across the line below 1e-7 ft), else null.</summary>
    internal static IReadOnlyList<IReadOnlyList<RebarSegment>>? CutBars(IReadOnlyList<IReadOnlyList<RebarSegment>> lines, CutInstance cut, double radius)
    {
        const double Tolerance = 1e-6;
        var first = cut.First;
        double[] Across(double[] from, double[] to)
        {
            double[] v = [to[0] - from[0], to[1] - from[1], to[2] - from[2]];
            double along = v[0] * first.Axis[0] + v[1] * first.Axis[1] + v[2] * first.Axis[2];
            return [v[0] - along * first.Axis[0], v[1] - along * first.Axis[1], v[2] - along * first.Axis[2]];
        }
        int home = lines.ToList().FindIndex(line => line[0].Origin is { } o && Across(first.Origin, o).All(c => Math.Abs(c) < 1e-7));
        if (home < 0) return null;
        var bars = new List<(double Distance, IReadOnlyList<RebarSegment> Bar)>();
        foreach (var line in lines)
        {
            var shift = Across(lines[home][0].Origin, line[0].Origin);
            double[] from = [first.Origin[0] + shift[0] + first.Axis[0] * first.Start, first.Origin[1] + shift[1] + first.Axis[1] * first.Start, first.Origin[2] + shift[2] + first.Axis[2] * first.Start];
            double[] to = [from[0] + first.Axis[0] * (first.End - first.Start), from[1] + first.Axis[1] * (first.End - first.Start), from[2] + first.Axis[2] * (first.End - first.Start)];
            bool inside = Enumerable.Range(0, 3).All(i => Math.Min(from[i], to[i]) - radius >= cut.Min[i] - Tolerance && Math.Max(from[i], to[i]) + radius <= cut.Max[i] + Tolerance);
            if (inside) bars.Add((Math.Sqrt(shift.Sum(c => c * c)), [first with { Origin = [first.Origin[0] + shift[0], first.Origin[1] + shift[1], first.Origin[2] + shift[2]] }]));
        }
        return bars.Count == 0 ? null : bars.OrderBy(bar => bar.Distance).Select(bar => bar.Bar).ToList();
    }

    private static long? Integer(IEnumerable<WideBlockWalker.Capture>? captures, string key) =>
        captures?.FirstOrDefault(c => c.Key == key && c.Kind == WideBlockWalker.CaptureKind.Integer) is { } c ? c.Integer : null;

    private static (int System, int Layer, int Index, bool InAssembly)? ReadMember(
        WideBlockWalker walker, WideBlockWalker.Walk walk, Dictionary<int, List<WideBlockWalker.Capture>> byQueue)
    {
        if (!byQueue.TryGetValue(-1, out var root)) return null;
        if (Integer(root, "RebarInSystem.m_systemId") is not long systemId
            || Integer(root, "HeldRebarInstanceKey.m_layer") is not long layer
            || Integer(root, "HeldRebarInstanceKey.m_index") is not long index)
            return null;
        return ((int)systemId, (int)layer, (int)index, InAssembly(walker, walk, byQueue));
    }

    /// <summary>The membership cell of an assembly is a queued object of the element's CellList .</summary>
    private static bool InAssembly(WideBlockWalker walker, WideBlockWalker.Walk walk, Dictionary<int, List<WideBlockWalker.Capture>> byQueue) =>
        byQueue.Any(pair => pair.Key >= 0 && pair.Key < walk.Queue.Count && walker.Class(walk.Queue[pair.Key])?.Name == "AssemblyGroupMembership"
            && Integer(pair.Value, "GroupMembership.m_groupId") > 0);

    internal static List<Layer>? ReadLayers(WideBlockWalker walker, WideBlockWalker.Walk walk, Dictionary<int, List<WideBlockWalker.Capture>> byQueue)
    {
        if (!byQueue.TryGetValue(-1, out var root) || walk.QueueCountAtStart.Count == 0) return null;
        // the layers are consecutive fields of the root; per layer the capture groups open with m_barType
        var groups = new List<List<WideBlockWalker.Capture>>();
        foreach (var c in root)
        {
            if (c.DeclaringClass != "RebarSystemLayer") continue;
            if (c.Field == "m_barType") groups.Add([]);
            if (groups.Count > 0) groups[^1].Add(c);
        }
        if (groups.Count == 0) return null;
        var counts = groups.Select(g => ((int)(Integer(g, "RebarSystemLayer.m_uncutCurves#") ?? -1),
            (int)(Integer(g, "RebarSystemLayer.m_uncutRebarInstances#") ?? -1))).ToList();
        if (counts.Any(c => c.Item1 < 0 || c.Item2 < 0)) return null;
        // the pointers of the layers are the last entries of the root's queue, in field order
        int at = walk.QueueCountAtStart[0] - counts.Sum(c => c.Item1 + c.Item2);
        if (at < 0) return null;
        var result = new List<Layer>();
        for (int i = 0; i < groups.Count; i++)
        {
            var bars = new List<IReadOnlyList<RebarSegment>>();
            var flags = groups[i].Where(c => c.Key.StartsWith(WideBlockWalker.AllItemsKeyPrefix, StringComparison.Ordinal) && c.Key.Contains('@')).Select(c => c.Integer).ToList();
            for (int k = 0; k < counts[i].Item1; k++)
            {
                var segment = Curve(walker, walk, byQueue, at + k);
                if (segment == null) return null;
                if (flags.Count == counts[i].Item1 && flags[k] == 0) continue;
                bars.Add([segment]);
            }
            // the lines of a layer are cut into runs of consecutive lines, one run per
            // RebarInstance, as long as its m_numberOfBarPositions says
            var sizes = Enumerable.Range(at + counts[i].Item1, counts[i].Item2)
                .Select(q => Integer(byQueue.GetValueOrDefault(q), "RebarInstBase.m_numberOfBarPositions") ?? -1).ToList();
            var instances = new List<IReadOnlyList<IReadOnlyList<RebarSegment>>>();
            if (counts[i].Item2 == 1) instances.Add(bars);
            else if (sizes.All(n => n >= 0) && sizes.Sum() == bars.Count)
            {
                int from = 0;
                foreach (long n in sizes) { instances.Add(bars.GetRange(from, (int)n)); from += (int)n; }
            }
            at += counts[i].Item1 + counts[i].Item2;
            result.Add(new Layer((int)(Integer(groups[i], "RebarSystemLayer.m_barType") ?? -1), instances, bars));
        }
        return WithCuts(walker, walk, byQueue, result);
    }

    /// <summary>The RebarInstCache objects that no RebarInstance owns are the instances the host cut; in a layer nothing cut each
    /// instance owns its cache. They belong to the one layer that has lines; with several such layers the owner is not known and the
    /// layers stay as they are.</summary>
    private static List<Layer> WithCuts(WideBlockWalker walker, WideBlockWalker.Walk walk, Dictionary<int, List<WideBlockWalker.Capture>> byQueue, List<Layer> layers)
    {
        var owned = new HashSet<int>();
        for (int q = 0; q < walk.Queue.Count; q++)
            if (walker.Class(walk.Queue[q])?.Name == "RebarInstance")
                foreach (int child in Children(walk, q)) owned.Add(child);
        var orphans = Enumerable.Range(0, walk.Queue.Count).Where(q => walker.Class(walk.Queue[q])?.Name == "RebarInstCache" && !owned.Contains(q)).ToList();
        int withLines = layers.Count(layer => layer.Lines.Count > 0);
        if (orphans.Count == 0 || withLines != 1) return layers;
        var cuts = new List<CutInstance>();
        foreach (int cache in orphans)
        {
            if (!byQueue.TryGetValue(cache, out var caps)) return layers;
            var oneBars = Children(walk, cache).ToList();
            if (oneBars.Count != 1) return layers;
            var curves = Children(walk, oneBars[0]).Select(q => Curve(walker, walk, byQueue, q)).ToList();
            double[] outline = caps.Where(c => c.Key.StartsWith("Outline.m_minmax@", StringComparison.Ordinal) && c.Kind == WideBlockWalker.CaptureKind.Real).Select(c => c.Real).ToArray();
            if (curves.Count != 1 || curves[0] is not { IsArc: false } line || outline.Length != 6) return layers;
            cuts.Add(new CutInstance(line, outline[..3], outline[3..]));
        }
        return layers.Select(layer => layer.Lines.Count > 0 ? layer with { Cuts = cuts } : layer).ToList();
    }

    private static RebarBars? ReadRebar(WideBlockWalker walker, WideBlockWalker.Walk walk, Dictionary<int, List<WideBlockWalker.Capture>> byQueue)
    {
        int instance = walk.Queue.FindIndex(c => walker.Class(c)?.Name is "RebarInstance" or "RebarInstFreeForm");
        if (instance < 0 || !byQueue.TryGetValue(instance, out var caps)) return null;
        bool freeForm = walker.Class(walk.Queue[instance])?.Name == "RebarInstFreeForm";
        // the lists of the cache are two counts, then the OneBarCenterlineCurves objects of the first and of the second list
        int cache = Children(walk, instance).FirstOrDefault(q => walker.Class(walk.Queue[q])?.Name == "RebarInstCache", -1);
        if (cache < 0 || !byQueue.TryGetValue(cache, out var cacheCaps)) return null;
        long firstCount = Integer(cacheCaps, "RebarInstCache.m_noSelfIntersectingCenterlineCurves#") ?? -1;
        long secondCount = Integer(cacheCaps, "RebarInstCache.m_selfIntersectingCenterlineCurves#") ?? -1;
        if (firstCount < 0 || secondCount < 0) return null;
        var lists = Children(walk, cache).ToList();
        if (lists.Count != firstCount + secondCount) return null;
        // the list of bars without self intersection is the one exported
        var oneBar = firstCount > 0 ? lists[0] : secondCount > 0 ? lists[(int)firstCount] : -1;
        if (oneBar < 0) return null;
        var flags = IncludedFlags(caps);
        var bars = new List<IReadOnlyList<RebarSegment>>();
        List<IReadOnlyList<RebarSegment>>? modelPaths = null;
        if (freeForm)
        {
            // The free-form cache stores one already positioned path for each bar.
            long count = Integer(caps, "RebarInstBase.m_numberOfBarPositions") ?? -1;
            if ((firstCount > 0 ? firstCount : secondCount) != count || (flags.Count != 0 && flags.Count != count))
                return null;
            for (int i = 0; i < count; i++)
            {
                if (flags.Count != 0 && flags[i] == 0) continue;
                if (ReadPath(lists[i]) is not { } path) return null;
                bars.Add(path);
            }
        }
        else
        {
            if (ReadPath(oneBar) is not { } first) return null;
            if (!Transforms(caps).TryGetValue(Positions, out var positions) || positions.Count == 0) return null;
            modelPaths = new();
            for (int i = 0; i < positions.Count; i++)
            {
                if (flags.Count == positions.Count && flags[i] == 0) continue;
                var placed = first.Select(s => s.Moved(positions[i])).ToList();
                modelPaths.Add(placed);
                // The IFC compatibility path applies the position transform twice.
                bars.Add(placed.Select(s => s.Moved(positions[i])).ToList());
            }
        }
        double? length = caps.FirstOrDefault(c => c.Key == "RebarInstBase.m_barLength" && c.Kind == WideBlockWalker.CaptureKind.Real) is { } l ? l.Real : null;
        // the shape id travels in ShapeName until the shapes are read
        long shape = Integer(caps, "RebarInstBase.m_shapeId") ?? 0;
        return new RebarBars((int)(Integer(caps, "RebarInstBase.m_barTypeId") ?? -1), 0, length, bars,
            ShapeName: shape.ToString(CultureInfo.InvariantCulture), InAssembly: InAssembly(walker, walk, byQueue),
            ShapeId: shape is > 0 and <= int.MaxValue ? (int)shape : 0, IsFreeForm: freeForm, ModelPaths: modelPaths);

        IReadOnlyList<RebarSegment>? ReadPath(int bar)
        {
            var path = new List<RebarSegment>();
            foreach (int q in Children(walk, bar))
            {
                if (Curve(walker, walk, byQueue, q) is not { } segment) return null;
                path.Add(segment);
            }
            return path.Count > 0 ? path : null;
        }
    }

    /// <summary>The queue indexes the queued object <paramref name="q"/> appended while it was read.</summary>
    private static IEnumerable<int> Children(WideBlockWalker.Walk walk, int q)
    {
        if (q >= walk.QueueEnd.Count) yield break;
        for (int i = walk.QueueCountAtStart[q]; i < walk.QueueEnd[q]; i++) yield return i;
    }

    /// <summary>The Trf arrays of one object by field name, in reading order: 9 numbers of m_3x3 (rows), then the 3 of m_or.</summary>
    private static Dictionary<string, List<double[]>> Transforms(List<WideBlockWalker.Capture> captures)
    {
        var result = new Dictionary<string, List<double[]>>(StringComparer.Ordinal);
        // a RebarInstance of Revit 2020 has no counted array of transforms: its only Trf is the frame of the shape
        List<double[]>? target = result[Trailing] = [];
        long expected = 0;
        var current = new double[12];
        foreach (var c in captures)
        {
            if (c.Key.EndsWith('#') && c.Field is Positions or "m_individualBarsTrf" or "m_individualLineTrf")
            {
                target = result[c.Field] = [];
                expected = c.Integer;
                // the Trf behind the last counted array belong to the next field (RebarInstance.m_shapeInstTrf)
                if (expected == 0) target = result[Trailing] = [];
                current = new double[12];
            }
            else if (target != null && c.DeclaringClass == "Trf" && c.Kind == WideBlockWalker.CaptureKind.Real)
            {
                if (c.Key.StartsWith("Trf.m_3x3@", StringComparison.Ordinal))
                {
                    var parts = c.Key[(c.Key.IndexOf('@') + 1)..].Split('/');
                    current[int.Parse(parts[0], CultureInfo.InvariantCulture) * 3 + int.Parse(parts[1], CultureInfo.InvariantCulture)] = c.Real;
                }
                else
                {
                    int axis = int.Parse(c.Key[(c.Key.IndexOf('@') + 1)..], CultureInfo.InvariantCulture);
                    current[9 + axis] = c.Real;
                    if (axis == 2)
                    {
                        target.Add(current);
                        current = new double[12];
                        if (target.Count == expected && !ReferenceEquals(target, result.GetValueOrDefault(Trailing))) target = result[Trailing] = [];
                    }
                }
            }
        }
        return result;
    }

    private static RebarSegment? Curve(WideBlockWalker walker, WideBlockWalker.Walk walk, Dictionary<int, List<WideBlockWalker.Capture>> byQueue, int q)
    {
        if (q < 0 || q >= walk.Queue.Count || !byQueue.TryGetValue(q, out var caps)) return null;
        double[]? Numbers(string key, int n)
        {
            var v = new double[n];
            for (int i = 0; i < n; i++)
            {
                var c = caps.FirstOrDefault(c => c.Key == key + "@" + i && c.Kind == WideBlockWalker.CaptureKind.Real);
                if (c.Key is null) return null;
                v[i] = c.Real;
            }
            return v;
        }
        var ends = Numbers("GCurve.m_endParams", 2);
        if (ends == null) return null;
        switch (walker.Class(walk.Queue[q])?.Name)
        {
            case "GLine":
                return Numbers("GLine.m_origin", 3) is { } o && Numbers("GLine.m_dirVec", 3) is { } d
                    ? new RebarSegment(false, o, d, [0, 0, 0], 0, ends[0], ends[1]) : null;
            case "GArc":
                return Numbers("GArc.m_center", 3) is { } c && Numbers("GArc.m_xVec", 3) is { } x && Numbers("GArc.m_yVec", 3) is { } y
                    && caps.FirstOrDefault(k => k.Key == "GArc.m_radius") is { } r
                    ? new RebarSegment(true, c, x, y, r.Real, ends[0], ends[1]) : null;
            default:
                return null;
        }
    }
}
