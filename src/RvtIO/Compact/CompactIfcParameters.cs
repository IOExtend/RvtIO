using RvtIO.BuiltIns;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Compact;

/// <summary>
/// The "IFC Parameters" group of the current profile CompactJsonExport properties block: IfcGUID, Export to IFC, Export to IFC
/// As and IFC Predefined Type of an instance, their "Type" forms for a type element, and the Operation of a door or window
/// type.
/// </summary>
internal sealed class CompactIfcParameters : ICompactPropertySource
{
    internal const string Group = "IFC Parameters";

    /// <summary>Which of the IFC parameters an element lists.</summary>
    internal enum Form
    {
        None,
        /// <summary>IfcGUID, Export to IFC, Export to IFC As, IFC Predefined Type.</summary>
        Instance,
        /// <summary>Type IfcGUID, Export Type to IFC, Export Type to IFC As, Type IFC Predefined Type.</summary>
        Type,
        /// <summary>Export to IFC, IfcGUID (grids and levels).</summary>
        InstanceTwo,
        /// <summary>Export Type to IFC, Type IfcGUID (grid and level types).</summary>
        TypeTwo,
        /// <summary>Export to IFC only (schedules, and viewports that show one).</summary>
        ExportOnly,
    }

    // (builtin id, label, forms that list it).
    private static readonly (int Id, string Label, Form[] Forms)[] Entries =
    [
        ((int)BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE_TYPE, "Type IFC Predefined Type", [Form.Type]),
        ((int)BuiltInParameter.IFC_EXPORT_PREDEFINEDTYPE, "IFC Predefined Type", [Form.Instance]),
        ((int)BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS, "Export Type to IFC As", [Form.Type]),
        ((int)BuiltInParameter.IFC_EXPORT_ELEMENT_AS, "Export to IFC As", [Form.Instance]),
        ((int)BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE, "Export Type to IFC", [Form.Type, Form.TypeTwo]),
        ((int)BuiltInParameter.IFC_EXPORT_ELEMENT, "Export to IFC", [Form.Instance, Form.InstanceTwo, Form.ExportOnly]),
        ((int)BuiltInParameter.IFC_TYPE_GUID, "Type IfcGUID", [Form.Type, Form.TypeTwo]),
        ((int)BuiltInParameter.IFC_GUID, "IfcGUID", [Form.Instance, Form.InstanceTwo]),
    ];

    private const string OperationLabel = "Operation";

    internal static readonly IReadOnlySet<string> ListingClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "AbsDuctType", "AbsFlexDuctType", "ArcWall", "AreaReinforcementType", "AssemblyInstance",
        "AssemblyType", "BaseRailing", "BasicWallType", "BuildingPad", "BuildingPadType",
        "CableTray", "Ceiling", "CompoundCeilingType", "ConduitFittingCenterLine", "ConduitRun",
        "ContFooting", "ContFootingType", "CorniceAttr", "CurtaSystemAttr",
        "CurtainRoofAttributes", "CutMarkType", "DBViewSchedule", "DPart",
        "DecalElement", "DecalType", "DuctFittingCenterLine", "EdgeSlabAttr", "ElementGroup",
        "ElementGroupType", "FabricArea", "FabricAreaType", "FabricSheet", "FabricSheetType",
        "FabricWireType", "FaceRoof", "FamilyInstance", "FamilySymbol", "FasciaAttr",
        "FilledRegion", "FilledRegionAttributes", "FlexCenterLine", "Floor", "FloorAttributes",
        "Grid", "GridAttributes", "GutterAttr", "HandRailType", "JoistSystem",
        "JoistSystemAttr", "Level", "LevelAttributes", "MassLevelData", "MaterialElem",
        "NestedFamilyTypeReference", "NewCurtainWallType", "PathReinforcement",
        "PathReinforcementType", "PipeFittingCenterLine", "ProfileRoof", "Property",
        "PropertyAttr", "PropertyLineSegment", "Ramp", "RampAttributes", "RbsCableTrayType",
        "RbsConduitCurve", "RbsConduitType", "RbsDuctCurve", "RbsDuctInsulationType",
        "RbsDuctLiningType", "RbsElectricalSystem", "RbsFlexDuctCurve", "RbsFlexPipeCurve",
        "RbsFlexPipeType", "RbsHvacSystem", "RbsPipeCurve", "RbsPipeInsulation",
        "RbsPipeInsulationType", "RbsPipeType", "RbsPipingSystem", "RbsSwitchSystem",
        "RbsWireCurve", "RbsWireType", "Rebar", "RebarBarType", "RebarContainer",
        "RebarContainerType", "RebarInSystem", "RebarShape", "RebarSystem", "Roof",
        "RoofAttributes", "RoofSoffit", "RoofSoffitType", "RoomElem", "RvtLinkInstance",
        "RvtLinkSymbol", "SWall", "SegmentCenterLine", "SharedSymbol", "SiteSurface",
        "Stairs", "StairsAttributes", "StairsElement", "StairsLanding", "StairsLandingType",
        "StairsRailingAttr", "StairsRun", "StairsRunType", "StairsSupport", "StairsSupportType",
        "StairsType", "StructuralConnectionHandler", "StructuralConnectionHandlerType", "SweepHost",
        "SysMullionFamSym", "SysPanelFamSym", "Text3dAttrSymbol", "TopRail", "TopRailType",
        "TrussElement", "WallSweep", "ZoneElement",
    };

    private static readonly HashSet<int> InstanceCategories = Ids(
    [
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors,
        BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Furniture, BuiltInCategory.OST_IOSModelGroups, BuiltInCategory.OST_Columns,
        BuiltInCategory.OST_Stairs, BuiltInCategory.OST_StairsStringerCarriage, BuiltInCategory.OST_StairsRailing,
        BuiltInCategory.OST_StairsRailingBaluster, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Rooms,
        BuiltInCategory.OST_CurtainWallPanels, BuiltInCategory.OST_CurtainWallMullions, BuiltInCategory.OST_Ramps,
        BuiltInCategory.OST_Cornices, BuiltInCategory.OST_Assemblies, BuiltInCategory.OST_Parts,
        BuiltInCategory.OST_Materials, BuiltInCategory.OST_StairsRuns, BuiltInCategory.OST_StairsLandings,
        BuiltInCategory.OST_RailingTopRail, BuiltInCategory.OST_Casework, BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_FurnitureSystems,
        BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_MechanicalEquipment,
        BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Parking, BuiltInCategory.OST_Site,
        BuiltInCategory.OST_BuildingPad, BuiltInCategory.OST_SiteProperty,
        BuiltInCategory.OST_SitePropertyLineSegment, BuiltInCategory.OST_StructuralFoundation,
        BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFramingSystem,
        BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralTruss, BuiltInCategory.OST_Topography,
        BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_RvtLinks,
        BuiltInCategory.OST_StructuralStiffener, BuiltInCategory.OST_Planting, BuiltInCategory.OST_Entourage,
        BuiltInCategory.OST_Fascia, BuiltInCategory.OST_Gutter, BuiltInCategory.OST_EdgeSlab,
        BuiltInCategory.OST_RoofSoffit, BuiltInCategory.OST_DetailComponents, BuiltInCategory.OST_Areas,
        BuiltInCategory.OST_Mass, BuiltInCategory.OST_MassFloor, BuiltInCategory.OST_MEPSpaces,
        BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctCurvesCenterLine, BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_DuctSystem, BuiltInCategory.OST_DuctAccessory,
        BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_FlexDuctCurvesCenterLine,
        BuiltInCategory.OST_ElectricalCircuit, BuiltInCategory.OST_Wire, BuiltInCategory.OST_PipingSystem,
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeCurvesCenterLine, BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_FlexPipeCurvesCenterLine,
        BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_DuctFittingCenterLine,
        BuiltInCategory.OST_PipeFittingCenterLine, BuiltInCategory.OST_CommunicationDevices,
        BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_LightingDevices, BuiltInCategory.OST_Sprinklers,
        BuiltInCategory.OST_SwitchSystem, BuiltInCategory.OST_HVAC_Zones, BuiltInCategory.OST_PipeInsulations,
        BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_ConduitFitting, BuiltInCategory.OST_CableTray,
        BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitCenterLine,
        BuiltInCategory.OST_ConduitFittingCenterLine, BuiltInCategory.OST_ConduitRun, BuiltInCategory.OST_Rebar,
        BuiltInCategory.OST_AreaRein, BuiltInCategory.OST_PathRein, BuiltInCategory.OST_FabricReinforcement,
        BuiltInCategory.OST_FabricAreas, BuiltInCategory.OST_StructConnections, BuiltInCategory.OST_Truss,
    ]);

    private static readonly HashSet<int> TypeCategories = Ids(
    [
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors,
        BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Furniture, BuiltInCategory.OST_IOSModelGroups, BuiltInCategory.OST_Columns,
        BuiltInCategory.OST_Stairs, BuiltInCategory.OST_StairsStringerCarriage, BuiltInCategory.OST_StairsRailing,
        BuiltInCategory.OST_StairsRailingBaluster, BuiltInCategory.OST_GenericModel,
        BuiltInCategory.OST_CurtainWallPanels, BuiltInCategory.OST_CurtainWallMullions, BuiltInCategory.OST_Ramps,
        BuiltInCategory.OST_Cornices, BuiltInCategory.OST_Assemblies, BuiltInCategory.OST_CurtaSystem,
        BuiltInCategory.OST_StairsRuns, BuiltInCategory.OST_StairsLandings, BuiltInCategory.OST_StairsCutMarks,
        BuiltInCategory.OST_RailingTopRail, BuiltInCategory.OST_RailingHandRail, BuiltInCategory.OST_RailingSupport,
        BuiltInCategory.OST_RailingTermination, BuiltInCategory.OST_Casework,
        BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalFixtures,
        BuiltInCategory.OST_FurnitureSystems, BuiltInCategory.OST_LightingFixtures,
        BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Parking,
        BuiltInCategory.OST_Site, BuiltInCategory.OST_BuildingPad, BuiltInCategory.OST_SiteProperty,
        BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_StructuralFramingSystem, BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_RvtLinks,
        BuiltInCategory.OST_StructuralStiffener, BuiltInCategory.OST_Planting, BuiltInCategory.OST_Entourage,
        BuiltInCategory.OST_Fascia, BuiltInCategory.OST_Gutter, BuiltInCategory.OST_EdgeSlab,
        BuiltInCategory.OST_RoofSoffit, BuiltInCategory.OST_DetailComponents, BuiltInCategory.OST_ProfileFamilies,
        BuiltInCategory.OST_Mass, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_Wire, BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_PipeAccessory,
        BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_DataDevices,
        BuiltInCategory.OST_LightingDevices, BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_PipeInsulations,
        BuiltInCategory.OST_DuctInsulations, BuiltInCategory.OST_DuctLinings, BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_ConduitFitting, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_DivisionProfile, BuiltInCategory.OST_Rebar, BuiltInCategory.OST_AreaRein,
        BuiltInCategory.OST_PathRein, BuiltInCategory.OST_RebarShape, BuiltInCategory.OST_FabricReinforcement,
        BuiltInCategory.OST_FabricAreas, BuiltInCategory.OST_StructConnections, BuiltInCategory.OST_Truss,
    ]);

    private static HashSet<int> Ids(BuiltInCategory[] categories) => categories.Select(category => (int)category).ToHashSet();

    private static bool IsDatum(int category) => category is (int)BuiltInCategory.OST_Grids or (int)BuiltInCategory.OST_Levels;

    /// <summary>The center lines of pipes, ducts, conduits and their fittings list the IfcGUID of their owning element.</summary>
    internal static readonly IReadOnlySet<string> CenterLineClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "SegmentCenterLine", "PipeFittingCenterLine", "DuctFittingCenterLine", "ConduitFittingCenterLine",
        "FlexCenterLine",
    };

    private readonly HashSet<int> _scheduleViews = [];
    private readonly Dictionary<int, Guid> _exportIds = [];
    /// <summary>The IfcGUID text an element stores itself (a center line lists the one of its owner).</summary>
    private readonly Dictionary<int, string> _storedGuids = [];

    public CompactIfcParameters() { }

    /// <summary>With the file, so that a viewport can be told to show a schedule (the viewport then lists the group as a
    /// schedule does) and a center line can take the export id of its owner.</summary>
    public CompactIfcParameters(RevitFileInfo info)
    {
        foreach (Element element in info.Elements)
        {
            if (ClassName(element) == "DBViewSchedule") _scheduleViews.Add(element.Id);
            if (element.SerializedExportId is Guid exportId) _exportIds.TryAdd(element.Id, exportId);
            if (element.Parameters.FirstOrDefault(p => p.Id == (int)BuiltInParameter.IFC_GUID)?.Value is string { Length: > 0 } storedGuid)
                _storedGuids.TryAdd(element.Id, storedGuid);
        }
    }

    /// <summary>The class name of an element: the name of its own Tf class, except for the classes the
    /// reader's schema names differently.</summary>
    internal static string? ClassName(Element element) => CompactNodeBuilder.ResolveClassName(element, out _);

    /// <summary>
    /// A wall (SWall) takes its header category: Walls, Curtain Panels (a wall used as a curtain panel) or Stacked Walls, which
    /// lists no IFC parameter, while the reader gives every wall the category Walls.
    /// </summary>
    internal static int? CategoryOf(Element element, string recordClass) =>
        recordClass is "SWall" or "FilledRegion" or "FilledRegionAttributes" ? CompactHeaderCategory.Of(element)
        : element.CategoryId == (int)BuiltInCategory.OST_Materials && recordClass != "MaterialElem" ? null : element.CategoryId;

    /// <summary>The form an element lists. Null category (unknown) lists nothing.</summary>
    internal static Form FormOf(string recordClass, int? category, bool isType, bool scheduleViewport = false)
    {
        if (recordClass == "Viewport") return scheduleViewport ? Form.ExportOnly : Form.None;
        if (!ListingClasses.Contains(recordClass) || category is not int c) return Form.None;
        if (isType)
            return IsDatum(c) ? Form.TypeTwo : TypeCategories.Contains(c) ? Form.Type : Form.None;
        if (IsDatum(c)) return Form.InstanceTwo;
        if (c == (int)BuiltInCategory.OST_Schedules) return Form.ExportOnly;
        return InstanceCategories.Contains(c) ? Form.Instance : Form.None;
    }

    /// <summary>Door and window types list the Operation of their door or window style after the other IFC parameters; its
    /// value is the stored text of DOOR_OPERATION_TYPE, empty when nothing is stored.</summary>
    internal static bool ListsOperation(string recordClass, int? category, Form form) =>
        form == Form.Type && recordClass == "FamilySymbol"
        && category is (int)BuiltInCategory.OST_Doors or (int)BuiltInCategory.OST_Windows;

    /// <summary>IfcGUID of a Revit UniqueId: the IFC base-64 of the episode GUID with its
    /// last 64 bits XORed with the original id. Null when the text is not a UniqueId.</summary>
    internal static string? IfcGuidOf(string? uniqueId)
    {
        if (uniqueId is not { Length: >= 45 and <= 53 } || uniqueId[36] != '-'
            || !Guid.TryParseExact(uniqueId.AsSpan(0, 36), "D", out Guid episode)
            || !long.TryParse(uniqueId.AsSpan(37), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out long id) || id < 0) return null;
        return IfcGuid.ToIfcGuid(ElementExportIds.ExportId(episode, (ulong)id));
    }

    internal static IReadOnlyList<CompactProperty> Properties(
        Form form, string? ifcGuid, string? operation, IReadOnlySet<int>? stored = null) =>
        PropertyEntries(form, ifcGuid, operation, stored).Select(entry => entry.Property).ToList();

    /// <summary>The builtin parameter ids this class answers for: the eight IFC parameters and Operation. A composite that also
    /// lists builtin ids by the parameter-list rules takes these ids from here only.</summary>
    internal static readonly IReadOnlySet<int> OwnedIds = Entries.Select(entry => entry.Id)
        .Append((int)BuiltInParameter.DOOR_OPERATION_TYPE).ToHashSet();

    /// <summary><see cref="Properties"/> with the builtin id of each entry.</summary>
    internal static IReadOnlyList<(int Id, CompactProperty Property)> PropertyEntries(
        Form form, string? ifcGuid, string? operation, IReadOnlySet<int>? stored = null)
    {
        var result = new List<(int, CompactProperty)>();
        if (form == Form.None) return result;
        foreach (var (id, label, forms) in Entries)
        {
            if (Array.IndexOf(forms, form) < 0 || stored?.Contains(id) == true) continue;
            string? value = id switch
            {
                (int)BuiltInParameter.IFC_GUID or (int)BuiltInParameter.IFC_TYPE_GUID => ifcGuid,
                (int)BuiltInParameter.IFC_EXPORT_ELEMENT or (int)BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE => "0",
                _ => "",
            };
            if (value != null) result.Add((id, new CompactProperty(Group, label, value)));
        }
        if (operation != null)
            result.Add(((int)BuiltInParameter.DOOR_OPERATION_TYPE, new CompactProperty(Group, OperationLabel, operation)));
        return result;
    }

    public IReadOnlyList<CompactProperty> GetProperties(Element element) =>
        GetEntries(element).Select(entry => entry.Property).ToList();

    /// <summary><see cref="GetProperties"/> with the builtin id of each entry.</summary>
    internal IReadOnlyList<(int Id, CompactProperty Property)> GetEntries(Element element)
    {
        if (ClassName(element) is not string recordClass) return [];
        bool isType = element.NativeIsSymbol ?? element.RuleClassIs("Symbol");
        bool schedule = recordClass == "Viewport" && element.References != null
            && element.References.Any(id => id != element.Id && _scheduleViews.Contains(id));
        int? category = CategoryOf(element, recordClass);
        Form form = FormOf(recordClass, category, isType, schedule);
        if (form == Form.None) return [];

        HashSet<int>? stored = null;
        string? operation = ListsOperation(recordClass, category, form) ? "" : null;
        foreach (Parameter parameter in element.Parameters)
        {
            if (parameter.Id == (int)BuiltInParameter.DOOR_OPERATION_TYPE)
            {
                if (operation != null) operation = parameter.Value as string ?? "";
            }
            else if (OwnedIds.Contains(parameter.Id))
                (stored ??= []).Add(parameter.Id);
        }
        Guid? exportId = element.SerializedExportId;
        if (CenterLineClasses.Contains(recordClass))
            exportId = element.SerializedOwningElementId is int owner && _exportIds.TryGetValue(owner, out Guid ownerId)
                ? ownerId : null;
        string? ifcGuid = exportId is Guid guid ? IfcGuid.ToIfcGuid(guid) : null;
        // an owner that stores its own IfcGUID lists that text on its center line
        if (element.NativeRootClassName != null && CenterLineClasses.Contains(recordClass) && element.SerializedOwningElementId is int ownerKey && _storedGuids.TryGetValue(ownerKey, out string? ownerStored))
            ifcGuid = ownerStored;
        return PropertyEntries(form, ifcGuid, operation, stored);
    }
}
