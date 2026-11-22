using System.Globalization;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Ifc;

/// <summary>Reinforcement bars of the current profile (IFC2X3): every Rebar and RebarInSystem element is an IfcReinforcingBar whose Body is
/// one IfcSweptDiskSolid per bar of the set, swept along the centre line (an IfcCompositeCurve of IfcPolyline pieces and trimmed
/// IfcCircle arcs). A bar is contained in the storey of its lowest point (in the building when the project has no storey), also when it is
/// a member of an assembly that is not written; the bars of one RebarSystem
/// (area reinforcement) that are not members of an assembly are related to an IfcGroup of the system.</summary>
public partial class IfcExporter
{
    /// <summary>RebarBarType's family label, the first part of the name of every bar.</summary>
    private const string RebarFamilyLabel = "Rebar Bar";

    private const double FeetToMillimetres = 304.8;

    /// <summary>The parameter of a line in the unit of the file: the end parameter of a directrix adds it to the degrees
    /// of its arcs, so it is not a length the final conversion could reach.</summary>
    private double LengthUnitsPerFoot => _unitFamily switch
    {
        IfcUnitFamily.Foot => 1,
        IfcUnitFamily.Metre => FeetToMetres,
        IfcUnitFamily.Centimetre => FeetToMillimetres / 10,
        IfcUnitFamily.Inch => 12,
        _ => FeetToMillimetres,
    };

    /// <summary>The bar products of each RebarSystem by system element id, in element order.</summary>
    private readonly Dictionary<int, List<int>> _rebarSystemMembers = new();

    /// <summary>Writes the product of a reinforcement element; false (nothing written) when its type has no usable name.</summary>
    private bool WriteRebarProduct(StepWriter w, Element elem, IReadOnlyDictionary<int, Element> elemById, int ownerHistId,
        int axisId, int parentPlacementId, int bodySubCtxId, Dictionary<(string PropertyName, string ValueExpr), int> propertySingleValueIds,
        bool shapeInName, Dictionary<int, int> storeyIds, BuildingStoreyResolver storeyResolver, Dictionary<int, List<int>> storeyContents)
    {
        var data = elem.RebarBars!;
        if (data.Items != null)
            return WriteRebarContainer(w, elem, data, elemById, ownerHistId, axisId, parentPlacementId, bodySubCtxId, propertySingleValueIds, storeyIds, storeyResolver, storeyContents);
        string? barTypeName = data.BarTypeName;
        if (data.Bars.Count == 0 || data.Bars.Any(bar => bar.Count == 0) || string.IsNullOrEmpty(barTypeName)) return false;
        // For shape driven bars of Revit 2026 files the shape follows the type
        string typeLabel = shapeInName && data.SystemId == 0 && data.ShapeName != null ? $"{barTypeName} : Shape {data.ShapeName}" : barTypeName;
        string elemName = $"{RebarFamilyLabel}:{typeLabel}:{elem.Id.ToString(CultureInfo.InvariantCulture)}";
        string objectType = data.SystemId > 0 ? barTypeName : $"{RebarFamilyLabel}:{barTypeName}";
        // RebarInSystem: the sub-element index is the number of bars plus 3; a Rebar has none. The one RebarInSystem of a system has the
        // GlobalId of the system.
        Element? system = data.OnlyBarOfSystem ? elemById.GetValueOrDefault(data.SystemId) : null;
        int entityId = WriteRebarBar(w, elem, data, ownerHistId, axisId, parentPlacementId, bodySubCtxId, elemName, objectType,
            system != null ? 0 : data.SystemId > 0 ? data.Bars.Count + 3 : 0, globalIdOf: system);
        ExportQuantityTakeOffPropertySet(w, elem, entityId, ownerHistId, barTypeName, propertySingleValueIds);
        PlaceRebarInStorey(elem, data, elemById, entityId, storeyIds, storeyResolver, storeyContents);
        // No group of their system is written for the bars of an assembly, nor for the one bar of a system, which carries its GlobalId
        if (data.SystemId > 0 && !data.InAssembly && system == null)
        {
            if (!_rebarSystemMembers.TryGetValue(data.SystemId, out var members)) _rebarSystemMembers[data.SystemId] = members = [];
            members.Add(entityId);
        }
        return true;
    }

    /// <summary>The bars of each RebarContainer by container element id: the label of its group and its items, in the order of the container.</summary>
    private readonly Dictionary<int, (string Label, List<int> Members)> _rebarContainerMembers = new();

    /// <summary>RebarContainerType's family label, the first part of the name of every item of a container.</summary>
    private const string ContainerFamilyLabel = "Structural Rebar Container";

    /// <summary>Writes the items of a RebarContainer, each an IfcReinforcingBar of one swept disk (name "Structural Rebar Container:type:container id: index",
    /// GlobalId of the sub-element index + 4 of the container), with one Pset_QuantityTakeOff (Reference = the container type) for all of them; the
    /// group of the container is written with the groups of the reinforcement systems. False (nothing written) when no item can be written.</summary>
    private bool WriteRebarContainer(StepWriter w, Element elem, RebarBars data, IReadOnlyDictionary<int, Element> elemById, int ownerHistId, int axisId,
        int parentPlacementId, int bodySubCtxId, Dictionary<(string PropertyName, string ValueExpr), int> propertySingleValueIds,
        Dictionary<int, int> storeyIds, BuildingStoreyResolver storeyResolver, Dictionary<int, List<int>> storeyContents)
    {
        string? typeName = data.BarTypeName;
        if (string.IsNullOrEmpty(typeName)) return false;
        string label = $"{ContainerFamilyLabel}:{typeName}";
        var members = new List<int>();
        foreach (var item in data.Items!)
        {
            if (item.Bars.Count == 0 || item.Bars.Any(bar => bar.Count == 0) || string.IsNullOrEmpty(item.BarTypeName)) continue;
            int entityId = WriteRebarBar(w, elem, item, ownerHistId, axisId, parentPlacementId, bodySubCtxId,
                $"{label}:{elem.Id.ToString(CultureInfo.InvariantCulture)}: {item.ItemIndex.ToString(CultureInfo.InvariantCulture)}", label, item.ItemIndex + 4,
                IfcGuid.FromName($"rebar-container-item:{elem.Id}:{item.ItemIndex}"));
            PlaceRebarInStorey(elem, item, elemById, entityId, storeyIds, storeyResolver, storeyContents);
            members.Add(entityId);
        }
        if (members.Count == 0) return false;
        int valueId = GetOrAddPropertySingleValue(w, propertySingleValueIds, "Reference", $"IFCIDENTIFIER({S(typeName)})");
        int psetId = w.Add("IFCPROPERTYSET",
            $"{S(IfcGuid.FromName($"qto-pset:{elem.Id}"))},{R(ownerHistId)},{S("Pset_QuantityTakeOff")},$,{StepWriter.List(R(valueId))}");
        w.Add("IFCRELDEFINESBYPROPERTIES",
            $"{S(IfcGuid.FromName($"qto-rel:{elem.Id}"))},{R(ownerHistId)},$,$,{StepWriter.List(members.Select(R))},{R(psetId)}");
        _rebarContainerMembers[elem.Id] = (label, members);
        return true;
    }

    /// <summary>One IfcReinforcingBar of <paramref name="data"/> (its swept disks, placement and quantities), without property set and storey.
    /// <paramref name="fallbackGlobalId"/> is its GlobalId when the element has no usable export id (the items of a container share their element);
    /// <paramref name="globalIdOf"/> is the element whose GlobalId the bar takes instead of its own (the system of its one RebarInSystem).</summary>
    private int WriteRebarBar(StepWriter w, Element elem, RebarBars data, int ownerHistId, int axisId, int parentPlacementId, int bodySubCtxId,
        string elemName, string objectType, int subElementIndex, string? fallbackGlobalId = null, Element? globalIdOf = null)
    {
        var items = new List<string>();
        double firstBarLengthFt = 0;
        foreach (var bar in data.Bars)
        {
            double lengthFt;
            items.Add(R(WriteSweptDisk(w, bar, data.ModelDiameter / 2 * FeetToMillimetres, out lengthFt)));
            if (firstBarLengthFt == 0) firstBarLengthFt = lengthFt;
        }
        int representationId = w.Add("IFCSHAPEREPRESENTATION",
            $"{R(bodySubCtxId)},{S("Body")},{S("AdvancedSweptSolid")},{StepWriter.List(items)}");
        int shapeId = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"{S("")},{S("")},{StepWriter.List(R(representationId))}");
        int placementId = w.Add("IFCLOCALPLACEMENT", $"{R(parentPlacementId)},{R(axisId)}");

        // BarLength: the stored length of a shape driven bar, else the length of the first bar, rounded as the document says
        // (a length a hair below a half step rounds up: 1444.9999999999927 mm gives 1450)
        double barLengthMm = (data.BarLength ?? firstBarLengthFt) * FeetToMillimetres;
        if (data.LengthRoundingMillimetres > 0)
            barLengthMm = Math.Floor(barLengthMm / data.LengthRoundingMillimetres + 0.5 + 1e-9) * data.LengthRoundingMillimetres;
        double nominalMm = data.NominalDiameter * FeetToMillimetres;
        double area = Math.PI * Math.Pow(nominalMm / 2000, 2);

        string globalId = ResolveExactElementGlobalId(globalIdOf ?? elem, _usedExportIds, subElementIndex, CurrentProfile) ?? fallbackGlobalId ?? IfcGuid.FromElementId(elem.Id);
        return w.Add("IFCREINFORCINGBAR",
            $"{S(globalId)},{R(ownerHistId)},{S(elemName)},{S("")},{S(objectType)},{R(placementId)},{R(shapeId)},{S(elem.Id.ToString(CultureInfo.InvariantCulture))},"
            + $"{S("")},{D(nominalMm)},{D(area)},{D(barLengthMm)},.NOTDEFINED.,$");
    }

    /// <summary>Contains a bar in a storey. A bar has no level of its own (Element.m_assocLevelId = -1), so the storey is the one that holds the bottom of its
    /// bounding box; a project without a storey puts them in the building. A member of an assembly is contained too: the assemblies that hold bars
    /// (parts assemblies) are not written, so nothing aggregates them. Aggregate them instead when those assemblies are written.
    /// The assembly goes to the highest storey at or below the bottom of the Part that hosts the bar, so its bars follow that rule.</summary>
    private void PlaceRebarInStorey(Element elem, RebarBars data, IReadOnlyDictionary<int, Element> elemById, int entityId,
        Dictionary<int, int> storeyIds, BuildingStoreyResolver storeyResolver, Dictionary<int, List<int>> storeyContents)
    {
        int storeyId = 0;
        if (data.InAssembly && storeyIds.Count > 0 && HostPartBottom(elem, elemById) is double partBottomFt)
            storeyId = ResolveStoreyAtOrBelow(partBottomFt, storeyIds, elemById);
        if (storeyId == 0)
        {
            double bottomFt = data.Bars.SelectMany(bar => bar).Min(piece => piece.MinZ()) - data.ModelDiameter / 2;
            storeyId = storeyResolver.ResolveByBottom(bottomFt);
        }
        if (!storeyContents.TryGetValue(storeyId, out var contents)) storeyContents[storeyId] = contents = [];
        contents.Add(entityId);
    }

    /// <summary>The bottom of the first Part on the host chain of a bar (the bar is hosted by a held instance, that by the Part); null without one.</summary>
    private static double? HostPartBottom(Element bar, IReadOnlyDictionary<int, Element> elemById)
    {
        var host = bar;
        for (int hop = 0; hop < 2 && host.HostId is int hostId && elemById.TryGetValue(hostId, out var next); hop++)
        {
            host = next;
            if (host.CategoryId == -2000269 && (host.DecodedBox ?? host.Geometry?.BoundingBox) is { Length: >= 6 } box) return box[2];
        }
        return null;
    }

    /// <summary>The storey of the highest level at or below <paramref name="bottomFt"/> (the lowest level's when none is below); 0 without levels.</summary>
    private static int ResolveStoreyAtOrBelow(double bottomFt, Dictionary<int, int> storeyIds, IReadOnlyDictionary<int, Element> elemById)
    {
        const double Tolerance = 1e-6;
        int chosen = 0, lowest = 0;
        double chosenElevation = double.NegativeInfinity, lowestElevation = double.PositiveInfinity;
        foreach (var (levelId, storeyId) in storeyIds.OrderBy(pair => pair.Key))
            if (elemById.TryGetValue(levelId, out var level))
            {
                double elevation = GetElevation(level);
                if (elevation <= bottomFt + Tolerance && elevation >= chosenElevation) { chosen = storeyId; chosenElevation = elevation; }
                if (elevation < lowestElevation) { lowest = storeyId; lowestElevation = elevation; }
            }
        return chosen != 0 ? chosen : lowest;
    }

    /// <summary>One IfcSweptDiskSolid along the pieces of a bar. Lengths of the directrix are written in metres and shifted to
    /// the file unit with the rest of the geometry; the disk (radius) is written in millimetres and converted with it, its end parameter in the file unit.</summary>
    private int WriteSweptDisk(StepWriter w, IReadOnlyList<RebarSegment> pieces, double radiusMm, out double lengthFt)
    {
        string Point(double[] p) => R(w.Add("IFCCARTESIANPOINT", StepWriter.List(D(p[0] * FeetToMetres), D(p[1] * FeetToMetres), D(p[2] * FeetToMetres))));
        string Direction(double[] v) => R(w.Add("IFCDIRECTION", StepWriter.List(D(v[0]), D(v[1]), D(v[2]))));
        var curves = new List<int>();
        lengthFt = 0;
        // The end parameter adds line lengths in file units and arc extents in degrees.
        double endParameter = 0;
        foreach (var piece in pieces)
        {
            int curve;
            if (!piece.IsArc)
            {
                double[] At(double t) => [piece.Origin[0] + piece.Axis[0] * t, piece.Origin[1] + piece.Axis[1] * t, piece.Origin[2] + piece.Axis[2] * t];
                curve = w.Add("IFCPOLYLINE", StepWriter.List(Point(At(piece.Start)), Point(At(piece.End))));
                lengthFt += Math.Abs(piece.End - piece.Start);
                endParameter += Math.Abs(piece.End - piece.Start) * LengthUnitsPerFoot;
            }
            else
            {
                // the circle is placed so that the arc starts at its parameter 0: the reference direction points to the start of the arc
                double[] x = piece.Axis, y = piece.Axis2;
                double[] axis = [x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0]];
                double cos = Math.Cos(piece.Start), sin = Math.Sin(piece.Start);
                double[] start = [x[0] * cos + y[0] * sin, x[1] * cos + y[1] * sin, x[2] * cos + y[2] * sin];
                int placement = w.Add("IFCAXIS2PLACEMENT3D", $"{Point(piece.Origin)},{Direction(axis)},{Direction(start)}");
                int circle = w.Add("IFCCIRCLE", $"{R(placement)},{D(piece.Radius * FeetToMetres)}");
                double degrees = (piece.End - piece.Start) * 180 / Math.PI;
                curve = w.Add("IFCTRIMMEDCURVE",
                    $"{R(circle)},{StepWriter.List("IFCPARAMETERVALUE(0.)")},{StepWriter.List($"IFCPARAMETERVALUE({D(degrees)})")},.T.,.PARAMETER.");
                lengthFt += Math.Abs(piece.End - piece.Start) * piece.Radius;
                endParameter += Math.Abs(degrees);
            }
            curves.Add(curve);
        }
        var segments = curves.Select((curve, i) =>
            R(w.Add("IFCCOMPOSITECURVESEGMENT", $"{(i == curves.Count - 1 ? ".DISCONTINUOUS." : ".CONTINUOUS.")},.T.,{R(curve)}"))).ToList();
        int composite = w.Add("IFCCOMPOSITECURVE", $"{StepWriter.List(segments)},.F.");
        return w.Add("IFCSWEPTDISKSOLID", $"{R(composite)},{D(radiusMm)},$,0.,{D(endParameter)}");
    }

    /// <summary>The IfcGroup of every RebarSystem and every RebarContainer that has bars, with the IfcRelAssignsToGroup that relates them.</summary>
    private void WriteRebarSystemGroups(StepWriter w, int ownerHistId, IReadOnlyDictionary<int, Element> elemById)
    {
        foreach (var (systemId, members) in _rebarSystemMembers.OrderBy(pair => pair.Key))
        {
            if (!elemById.TryGetValue(systemId, out var system) || string.IsNullOrEmpty(system.Name) || ExportFlagIsNo(system, elemById)) continue;
            string label = $"Structural Area Reinforcement:{system.Name}";
            string globalId = ResolveExactElementGlobalId(system, _usedExportIds, handleBased: CurrentProfile) ?? IfcGuid.FromElementId(system.Id);
            int group = w.Add("IFCGROUP", $"{S(globalId)},{R(ownerHistId)},{S(label)},{S("")},{S(label)}");
            w.Add("IFCRELASSIGNSTOGROUP",
                $"{S(IfcGuid.FromMd5($"IfcRelAssignsToGroup:{globalId}"))},{R(ownerHistId)},{S("")},{S("")},{StepWriter.List(members.Select(R))},$,{R(group)}");
        }
        _rebarSystemMembers.Clear();
        // the group of a container is named by its type and carries the GlobalId of the container itself
        foreach (var (containerId, (label, members)) in _rebarContainerMembers.OrderBy(pair => pair.Key))
        {
            if (!elemById.TryGetValue(containerId, out var container) || ExportFlagIsNo(container, elemById)) continue;
            string globalId = ResolveExactElementGlobalId(container, _usedExportIds, handleBased: CurrentProfile) ?? IfcGuid.FromName($"rebar-container:{container.Id}");
            int group = w.Add("IFCGROUP", $"{S(globalId)},{R(ownerHistId)},{S(label)},{S("")},{S(label)}");
            w.Add("IFCRELASSIGNSTOGROUP",
                $"{S(IfcGuid.FromMd5($"IfcRelAssignsToGroup:{globalId}"))},{R(ownerHistId)},{S("")},{S("")},{StepWriter.List(members.Select(R))},$,{R(group)}");
        }
        _rebarContainerMembers.Clear();
    }

    /// <summary>JoistSystem's family label, the first part of the name of every beam system.</summary>
    private const string BeamSystemFamilyLabel = "Structural Beam System";

    /// <summary>The IfcElementAssembly (BEAM_GRID, no representation) of every beam system whose beams were exported, and the
    /// IfcRelAggregates to those beams. The beams leave their storeys and the assembly stands in the storey of its first beam.
    /// Where that beam is on a level that is not a project storey, the assembly stands where the storey rule puts the beam system
    /// itself: it has no body, so that is measured by its own outline (a system without an outline stays with its first beam).
    /// The assembly is not registered as an exported element: it gets no material or type relation (the later passes read that list).
    /// Returns the ids of the beam systems written.</summary>
    private List<int> WriteJoistAssemblies(StepWriter w, IReadOnlyList<Element> elements, int ownerHistId, int parentPlacementId, int axisId,
        IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> exported, IReadOnlyDictionary<int, int> exportedStoreyIds,
        Dictionary<int, List<int>> storeyContents, Dictionary<(string PropertyName, string ValueExpr), int> propertySingleValueIds,
        Func<Element, int> storeyOfSystem)
    {
        var written = new List<int>();
        foreach (var elem in elements.Where(e => e.JoistSystem != null).OrderBy(e => e.Id))
        {
            var data = elem.JoistSystem!;
            var memberIds = data.BeamIds.Where(exported.ContainsKey).Distinct().ToList();
            if (string.IsNullOrEmpty(data.TypeName) || memberIds.Count == 0 || !exportedStoreyIds.TryGetValue(memberIds[0], out int storeyId)) continue;
            if (elem.BoundingBox is { Length: >= 6 } && _offStoreyLevelElementIds.Contains(memberIds[0])) storeyId = storeyOfSystem(elem);
            string objectType = $"{BeamSystemFamilyLabel}:{data.TypeName}";
            string globalId = ResolveExactElementGlobalId(elem, _usedExportIds, handleBased: CurrentProfile) ?? IfcGuid.FromElementId(elem.Id);
            string tag = elem.Id.ToString(CultureInfo.InvariantCulture);
            int placement = w.Add("IFCLOCALPLACEMENT", $"{R(parentPlacementId)},{R(axisId)}");
            int entity = w.Add("IFCELEMENTASSEMBLY",
                $"{S(globalId)},{R(ownerHistId)},{S($"{objectType}:{tag}")},{S("")},{S(objectType)},{R(placement)},$,{S(tag)},.NOTDEFINED.,.BEAM_GRID.");
            // IfcRelAggregates GlobalId: the export id with sub-element index 2
            var memberEntities = memberIds.Select(id => exported[id].entityId).Order().ToList();
            w.Add("IFCRELAGGREGATES",
                $"{S(ResolveExactElementGlobalId(elem, _usedExportIds, 2, CurrentProfile) ?? IfcGuid.FromName($"joist-aggregate:{elem.Id}"))},{R(ownerHistId)},{S("")},{S("")},"
                + $"{R(entity)},{StepWriter.List(memberEntities.Select(R))}");
            ExportQuantityTakeOffPropertySet(w, elem, entity, ownerHistId, data.TypeName, propertySingleValueIds);
            var memberSet = memberEntities.ToHashSet();
            foreach (var contents in storeyContents.Values) contents.RemoveAll(memberSet.Contains);
            if (!storeyContents.TryGetValue(storeyId, out var storey)) storeyContents[storeyId] = storey = [];
            storey.Add(entity);
            written.Add(elem.Id);
        }
        return written;
    }
}
