using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>Instance or type binding of a project or shared parameter to one category (ParamBinding.m_elemOrSymbol:
/// 1 = instance, 2 = type; no other value occurs in the corpus and none is accepted).</summary>
internal readonly record struct UserParameterBinding(int CategoryId, bool Instance);

/// <summary>A parameter defined by the file: the ParamElem (family 2874, shared 2873, project 2876, ...) and its owned
/// ParamDef. <see cref="Id"/> is the positive parameter id that element parameter stores use.</summary>
/// <param name="GroupId">ParamDef.m_groupElemId: a BuiltInParameterGroup value, -1 when the parameter has no group (the layouts that store
/// <paramref name="GroupTypeId"/> instead have -1).</param>
/// <param name="GroupTypeId">ParamDef.m_groupTypeId: the ForgeTypeId string of the group (Revit 2023 and later layouts), null where
/// the file stores the number.</param>
/// <param name="TypeId">ParamDef.m_typeId: "...revit.family:ID", "...revit.project:ID", "...revit.external.-1:GUID".</param>
/// <param name="SpecTypeId">ParamDefValue.m_specTypeId: the measurable spec of a number parameter (null otherwise).</param>
/// <param name="Instance">ParamElemFamily.m_instanceParam; null for project and shared parameters (see bindings).</param>
internal sealed record UserParameterDefinition(
    int Id, ushort ElementClass, ushort DefinitionClass, string Caption, int GroupId, string? TypeId,
    string? SpecTypeId, bool ReadOnly, bool UserVisible, bool? Instance, string? GroupTypeId = null)
{
    /// <summary>Storage kind of the values, from the class of the owned ParamDef (None for a class that is not validated).</summary>
    public StorageType Storage => DefinitionClass switch
    {
        606 or 2852 or 2848 => StorageType.String,
        2866 or 2756 or 2786 or 3200 => StorageType.Integer,
        2781 or 2737 or 2814 or 2752 or 1291 => StorageType.ElementId,
        612 or 2742 when SpecTypeId != null => StorageType.Double,
        _ => StorageType.None,
    };

    /// <summary>The data type id (SpecTypeId) of the parameter; null where the class has none (family types, table
    /// entries) or is not validated.</summary>
    public string? Spec => DefinitionClass switch
    {
        606 => "autodesk.spec:spec.string-2.0.0",
        2852 => "autodesk.spec.string:url-2.0.0",
        2848 => "autodesk.spec.aec:multilineText-2.0.0",
        2752 => "autodesk.spec.reference:image-1.0.0",
        2866 or 3200 => "autodesk.spec:spec.bool-1.0.0",
        2756 => "autodesk.spec:spec.int64-2.0.0",
        2786 => "autodesk.spec.aec:numberOfPoles-2.0.0",
        2781 => "autodesk.spec.aec:material-1.0.0",
        612 or 2742 => SpecTypeId,
        _ => null,
    };

    public bool IsYesNo => DefinitionClass is 2866 or 3200;
    public bool IsImage => DefinitionClass == 2752;
}

/// <summary>The value one element stores for a user parameter: the member that is meaningful follows
/// <see cref="UserParameterDefinition.Storage"/> (Double: Number; Integer: Integer; ElementId: ElementId; String: Text).
/// <paramref name="InstanceRecord"/> is the instance flag of the NamedParam the value was read from (false for a root store value).</summary>
internal readonly record struct UserParameterValue(double Number, int Integer, int ElementId, string? Text, bool InstanceRecord = false);

/// <summary>One user parameter an element lists. <see cref="Value"/> is null when the element stores none
/// (Revit reports eParamHasNoValue and prints an empty string).</summary>
internal readonly record struct UserParameterEntry(UserParameterDefinition Definition, UserParameterValue? Value, string Source = "");

/// <summary>Project, shared and family parameters of a file: the definitions (ParamElem + ParamDef),
/// their category bindings (ParamBinding), the parameters a family defines (Family.m_familyParams) and the values the
/// symbols and instances store (FamilySymbol.m_pParams, FamilyInstance.m_pInstParams, the element stores).
/// The list an element has follows Revit's getListParams:
/// a family symbol lists the type parameters of its family, a family instance the instance parameters of the family of
/// its symbol (both with the stored value of the NamedParam), and every element lists the project and shared parameters
/// bound to its category with the matching instance/type binding (value from the element store, else none).
/// The bound part is only produced for classes whose category is read exactly (<see cref="BoundCategoryOf"/>).</summary>
internal sealed class UserParameterCatalog
{
    private const ushort FamilyBaseClass = 1773, ParamElemClass = 2870, ParamBindingClass = 2632;
    private const ushort FamilySymbolClass = 1829, FamilyInstanceClass = 1794;

    private readonly Dictionary<int, UserParameterDefinition> _definitions;
    private readonly Dictionary<int, List<UserParameterBinding>> _bindingsOfParameter;
    private readonly Dictionary<(int Category, bool Instance), int[]> _parametersOfBinding;
    /// <summary>Family element id -> (parameter id, instance flag) of the positive ids of its m_familyParams.</summary>
    private readonly Dictionary<int, (int Id, bool Instance)[]> _familyParameters;
    private readonly Dictionary<int, int> _familyCategory;
    private readonly Dictionary<int, int> _symbolFamily;
    private readonly Dictionary<int, int> _instanceSymbol;
    /// <summary>Instance id -> InstInfoBase.m_symbolId of its InstanceInfo: the symbol whose NamedParams Revit shows on the instance.</summary>
    private readonly Dictionary<int, int> _instanceInfoSymbol;
    /// <summary>Element id -> its own NamedParams (symbols, instances) and stored positive-id values, by parameter id.</summary>
    private readonly Dictionary<int, Dictionary<int, UserParameterValue>> _namedValues;
    private readonly Dictionary<int, Dictionary<int, UserParameterValue>> _storedValues;
    /// <summary>Section id -> the view (Viewer.m_dbViewId) whose stores hold the parameter values of the section; only views whose stores were read completely.</summary>
    private readonly Dictionary<int, int> _sectionView;

    private UserParameterCatalog(
        Dictionary<int, UserParameterDefinition> definitions, Dictionary<int, List<UserParameterBinding>> bindings,
        Dictionary<int, (int, bool)[]> familyParameters, Dictionary<int, int> familyCategory,
        Dictionary<int, int> symbolFamily, Dictionary<int, int> instanceSymbol,
        Dictionary<int, Dictionary<int, UserParameterValue>> namedValues,
        Dictionary<int, Dictionary<int, UserParameterValue>> storedValues, Dictionary<int, int> instanceInfoSymbol,
        Dictionary<int, int> sectionView)
    {
        _sectionView = sectionView;
        _definitions = definitions;
        _bindingsOfParameter = bindings;
        _familyParameters = familyParameters;
        _familyCategory = familyCategory;
        _symbolFamily = symbolFamily;
        _instanceSymbol = instanceSymbol;
        _instanceInfoSymbol = instanceInfoSymbol;
        _namedValues = namedValues;
        _storedValues = storedValues;
        _parametersOfBinding = bindings
            .SelectMany(kv => kv.Value.Select(b => (b, id: kv.Key)))
            .GroupBy(x => x.b)
            .ToDictionary(g => (g.Key.CategoryId, g.Key.Instance), g => g.Select(x => x.id).Distinct().Order().ToArray());
    }

    public IReadOnlyDictionary<int, UserParameterDefinition> Definitions => _definitions;

    private const string SectionClass = "Section", RefTableClass = "RefTableElem";
    private static readonly HashSet<string> ViewMarkerClasses = [SectionClass, "InteriorElevArrow", "Viewer3d"];
    public bool ValuesKnown(Element element) => element.RuleClassName is not { } cls || !ViewMarkerClasses.Contains(cls) || _sectionView.ContainsKey(element.Id);

    public bool TryGetDefinition(int parameterId, out UserParameterDefinition definition)
        => _definitions.TryGetValue(parameterId, out definition!);

    /// <summary>The category bindings of a project or shared parameter (empty for a family parameter).</summary>
    public IReadOnlyList<UserParameterBinding> BindingsOf(int parameterId)
        => _bindingsOfParameter.TryGetValue(parameterId, out var list) ? list : [];

    /// <summary>The parameter ids bound to a category as instance (or type) parameters, ascending.</summary>
    public IReadOnlyList<int> BoundTo(int categoryId, bool instance)
        => _parametersOfBinding.TryGetValue((categoryId, instance), out var ids) ? ids : [];

    /// <summary>The English name Revit prints for a parameter group id; null for -1 and for an id without a known
    /// label (Revit prints "Invalid group" for both).</summary>
    public static string? GroupLabel(int groupId)
        => groupId != -1 && ((BuiltInParameterGroup)groupId).ToEnglishLabel(out string label) ? label : null;

    internal const string InvalidGroup = "Invalid group";

    /// <summary>The groups a ParamDef.m_groupTypeId (Revit 2024 and later) names, as the BuiltInParameterGroup whose label Revit prints.
    /// Each ForgeTypeId string maps to the group of the label below, the label being the one the same group has in the number layout
    /// of Revit 2023 and older. A string not listed here (analysisResults, mechanicalLoads) is not decided.</summary>
    private static readonly Dictionary<string, BuiltInParameterGroup> GroupOfForgeTypeId = new(StringComparer.Ordinal)
    {
        ["autodesk.parameter.group:adskModelProperties-1.0.0"] = BuiltInParameterGroup.PG_ADSK_MODEL_PROPERTIES,
        ["autodesk.parameter.group:constraints-1.0.0"] = BuiltInParameterGroup.PG_CONSTRAINTS,
        ["autodesk.parameter.group:construction-1.0.0"] = BuiltInParameterGroup.PG_CONSTRUCTION,
        ["autodesk.parameter.group:data-1.0.0"] = BuiltInParameterGroup.PG_DATA,
        ["autodesk.parameter.group:dimensions-1.0.0"] = BuiltInParameterGroup.PG_GEOMETRY,
        ["autodesk.parameter.group:electrical-1.0.0"] = BuiltInParameterGroup.PG_AELECTRICAL,
        ["autodesk.parameter.group:electricalEngineering-1.0.0"] = BuiltInParameterGroup.PG_ELECTRICAL,
        ["autodesk.parameter.group:electricalLighting-1.0.0"] = BuiltInParameterGroup.PG_ELECTRICAL_LIGHTING,
        ["autodesk.parameter.group:electricalLoads-1.0.0"] = BuiltInParameterGroup.PG_ELECTRICAL_LOADS,
        ["autodesk.parameter.group:general-1.0.0"] = BuiltInParameterGroup.PG_GENERAL,
        ["autodesk.parameter.group:graphics-1.0.0"] = BuiltInParameterGroup.PG_GRAPHICS,
        ["autodesk.parameter.group:greenBuilding-1.0.0"] = BuiltInParameterGroup.PG_GREEN_BUILDING,
        ["autodesk.parameter.group:identityData-1.0.0"] = BuiltInParameterGroup.PG_IDENTITY_DATA,
        ["autodesk.parameter.group:lightPhotometrics-1.0.0"] = BuiltInParameterGroup.PG_LIGHT_PHOTOMETRICS,
        ["autodesk.parameter.group:materials-1.0.0"] = BuiltInParameterGroup.PG_MATERIALS,
        ["autodesk.parameter.group:mechanical-1.0.0"] = BuiltInParameterGroup.PG_MECHANICAL,
        ["autodesk.parameter.group:mechanicalAirflow-1.0.0"] = BuiltInParameterGroup.PG_MECHANICAL_AIRFLOW,
        ["autodesk.parameter.group:plumbing-1.0.0"] = BuiltInParameterGroup.PG_PLUMBING,
        ["autodesk.parameter.group:rebarSystemLayers-1.0.0"] = BuiltInParameterGroup.PG_REBAR_SYSTEM_LAYERS,
        ["autodesk.parameter.group:structural-1.0.0"] = BuiltInParameterGroup.PG_STRUCTURAL,
        ["autodesk.parameter.group:text-1.0.0"] = BuiltInParameterGroup.PG_TEXT,
        ["autodesk.parameter.group:title-1.0.0"] = BuiltInParameterGroup.PG_TITLE,
        ["autodesk.parameter.group:analysisResults-1.0.0"] = BuiltInParameterGroup.PG_ANALYSIS_RESULTS,
        ["autodesk.parameter.group:couplerArray-1.0.0"] = BuiltInParameterGroup.PG_COUPLER_ARRAY,
        ["autodesk.parameter.group:electricalCircuiting-1.0.0"] = BuiltInParameterGroup.PG_ELECTRICAL_CIRCUITING,
        ["autodesk.parameter.group:energyAnalysis-1.0.0"] = BuiltInParameterGroup.PG_ENERGY_ANALYSIS,
        ["autodesk.parameter.group:ifc-1.0.0"] = BuiltInParameterGroup.PG_IFC,
        ["autodesk.parameter.group:mechanicalLoads-1.0.0"] = BuiltInParameterGroup.PG_MECHANICAL_LOADS,
        ["autodesk.parameter.group:overallLegend-1.0.0"] = BuiltInParameterGroup.PG_OVERALL_LEGEND,
        ["autodesk.parameter.group:primaryEnd-1.0.0"] = BuiltInParameterGroup.PG_PRIMARY_END,
        ["autodesk.parameter.group:rebarArray-1.0.0"] = BuiltInParameterGroup.PG_REBAR_ARRAY,
        ["autodesk.parameter.group:secondaryEnd-1.0.0"] = BuiltInParameterGroup.PG_SECONDARY_END,
        ["autodesk.parameter.group:segmentsFittings-1.0.0"] = BuiltInParameterGroup.PG_SEGMENTS_FITTINGS,
        ["autodesk.parameter.group:slabShapeEdit-1.0.0"] = BuiltInParameterGroup.PG_SLAB_SHAPE_EDIT,
        ["autodesk.parameter.group:structuralAnalysis-1.0.0"] = BuiltInParameterGroup.PG_STRUCTURAL_ANALYSIS,
        ["autodesk.parameter.group:visibility-1.0.0"] = BuiltInParameterGroup.PG_VISIBILITY,
        ["autodesk.revit.group:structuralSectionGeometry-1.0.0"] = BuiltInParameterGroup.PG_STRUCTURAL_SECTION_GEOMETRY,
    };

    /// <summary>The group label Revit prints for a definition: "Invalid group" for no group (-1 or an empty ForgeTypeId) and for a number
    /// without a label; null when the file names a group by a string that is not validated.</summary>
    public static string? GroupLabelOf(UserParameterDefinition definition)
        => definition.GroupTypeId == null ? GroupLabel(definition.GroupId) ?? InvalidGroup
        : definition.GroupTypeId.Length == 0 ? InvalidGroup
        : GroupOfForgeTypeId.TryGetValue(definition.GroupTypeId, out var group)
            && (group.ToEnglishLabel(out string label) || Lookup.ParameterGroupLabels.TryGetGroupLabel((int)group, out label)) ? label : null;

    /// <summary>Classes (by name) whose element category the reader reads exactly, so that the project
    /// and shared parameters bound to that category can be listed. The reader's CategoryId is a scan of the record body
    /// and is a default for graphics styles, fonts, sketches, tags, surrogates and the like; the classes below each have
    /// one category of their own. Symbols and instances take the category of their family instead (FamilyBase.m_categoryId).</summary>
    private static readonly HashSet<string> BoundCategoryClasses =
    [
        "AbsDuctType", "AbsFlexDuctType", "AnalyticalModelBeam", "AnalyticalModelColumn", "AnalyticalModelIsolatedFoundation",
        "AnalyticalModelSlab", "AnalyticalModelWall", "AnalyticalModelWallFoundation", "AreaLoad", "AreaReinforcementType",
        "BasicWallType", "CompoundCeilingType", "ContFooting", "ContFootingType", "CorniceAttr", "CurtaSystemAttr",
        "CurtainRoofAttributes", "DBView3d", "DBViewAnonDrafting", "DBViewDrafting", "DBViewGraphSchedColumn", "DBViewPlan",
        "DBViewRendering", "DBViewSection", "DPart", "EdgeSlabAttr", "FasciaAttr", "FilledRegion", "Floor", "FloorAttributes",
        "Grid", "GutterAttr", "Hub", "JoistSystem", "JoistSystemAttr", "Level", "LineLoad", "MassLevelData", "MaterialElem",
        "NewCurtainWallType", "ProjectInfo", "PropertyAttr", "RampAttributes", "RbsFlexPipeType", "RbsPipeType", "RbsWireType",
        "Rebar", "RebarBarType", "RebarInSystem", "RebarShape", "RebarSystem", "RoofAttributes", "RoomElem", "Section",
        "StairsAttributes", "StairsElement", "StairsRailingAttr", "StairsRun", "StairsSupport", "StairsType",
        "SysMullionFamSym", "SysPanelFamSym", "Text3dAttrSymbol", "ZoneElement", "ArcWall", "Ceiling", "ElementGroup",
        "ElementGroupType", "ProfileRoof", "BaseRailing", "RbsDuctCurve", "Roof", "RvtLinkInstance", "RvtLinkSymbol",
        "ShaftOpening", "WallSweep",
    ];
    private static readonly HashSet<string> HeaderCategoryClasses =
    [
        "AssemblyInstance", "AssemblyType", "CableTray", "FabricArea", "FabricSheet", "GridAttributes", "HandRailType",
        "RbsCableTrayType", "RbsConduitCurve", "RbsConduitType", "RbsDuctInsulationType", "RbsElectricalSystem", "RbsFlexDuctCurve",
        "RbsFlexPipeCurve", "RbsHvacSystem", "RbsHvacSystemType", "RbsPipeCurve", "RbsPipeInsulation", "RbsPipingSystem",
        "RbsPipingSystemType", "RbsSwitchSystem", "StairsLandingType", "StairsRunType", "StructuralConnectionHandler", "TopRailType",
    ];


    /// <summary>The category an element is bound by, or null where it is not read exactly (see <see cref="BoundCategoryClasses"/>).
    /// Of these classes an element with a family id (Element.m_famId not -1: the cornice, 3D text and property line types a family owns)
    /// lists no bound parameter.
    /// A symbol or instance of a known family has the category of the family, else (family category not captured) the
    /// reader's own category; one without a known family has none. A wall has the category
    /// of its element header (<see cref="Element.NativeHeaderCategoryId"/>): Walls, Curtain Panels (a wall used as a panel of a
    /// curtain wall) or Stacked Walls; Revit binds by that category, the class of the wall type does not decide it.</summary>
    public int? BoundCategoryOf(Element element)
    {
        switch (element.RuleClassName)
        {
            case "FamilySymbol":
                return _symbolFamily.TryGetValue(element.Id, out int symbolFamily) ? FamilyCategory(symbolFamily, element) : null;
            case "FamilyInstance":
                return FamilyOfInstance(element.Id) is int family ? FamilyCategory(family, element) : null;
            case "SWall":
                // a wall with a family id lists no bound parameter
                return element.SerializedFamilyId is null or -1 ? element.NativeHeaderCategoryId : null;
            case "MaterialElem": return -2000700;
            case { } cls when BoundCategoryClasses.Contains(cls): return element.SerializedFamilyId is null or -1 ? element.CategoryId : null;
            case "Viewer3d" or "InteriorElevArrow": return Compact.CompactHeaderCategory.Of(element);
            case { } cls when HeaderCategoryClasses.Contains(cls): return Compact.CompactHeaderCategory.Of(element);
            default: return null;
        }
    }

    private int? FamilyCategory(int family, Element element)
        => _familyCategory.TryGetValue(family, out int category) ? category : element.CategoryId;

    private static bool IsTypeClass(Element element) => element.RuleClassIs("Symbol") || element.RuleClassIs("FamilyBase");

    private int? FamilyOfInstance(int instanceId)
        => _instanceSymbol.TryGetValue(instanceId, out int symbol) && _symbolFamily.TryGetValue(symbol, out int family) ? family : null;

    /// <summary>The user parameters an element lists, by ascending parameter id (Revit's insertion order). A parameter
    /// whose definition is not validated (Storage None) or whose family value record is missing is left out; the element
    /// classes of <see cref="BoundCategoryOf"/> are the only ones that get bound parameters.</summary>
    /// <param name="includeUnvalidated">Diagnostic: also return the entries whose definition class has no validated storage
    /// and the family parameters without a stored record (value null).</param>
    public IReadOnlyList<UserParameterEntry> EntriesOf(Element element, bool includeUnvalidated = false)
    {
        string? cls = element.RuleClassName;
        bool isSymbol = cls == "FamilySymbol", isInstance = cls == "FamilyInstance";
        var ids = new SortedSet<int>();
        var fromFamily = new HashSet<int>();
        int? family = isSymbol ? (_symbolFamily.TryGetValue(element.Id, out int f) ? f : null)
            : isInstance ? FamilyOfInstance(element.Id) : null;
        if (family != null && _familyParameters.TryGetValue(family.Value, out var defined))
            foreach (var (id, instance) in defined)
                if (instance == isInstance && ids.Add(id)) fromFamily.Add(id);
        var named = _namedValues.GetValueOrDefault(element.Id);
        bool sectionOfView = cls != null && ViewMarkerClasses.Contains(cls) && _sectionView.ContainsKey(element.Id);
        var stored = _storedValues.GetValueOrDefault(sectionOfView ? _sectionView[element.Id] : element.Id);
        // An instance that stores no value of its own shows the one of its symbol (the type default): the symbol of its
        // InstanceInfo (not the m_masterSymbolId symbol, which is another one of the family for most instances).
        int? symbol = !isInstance ? null
            : _instanceInfoSymbol.TryGetValue(element.Id, out int info) ? info
            : _instanceSymbol.TryGetValue(element.Id, out int sid) ? sid : null;
        var symbolNamed = symbol is int s1 ? _namedValues.GetValueOrDefault(s1) : null;
        var symbolStored = symbol is int s2 ? _storedValues.GetValueOrDefault(s2) : null;
        if (BoundCategoryOf(element) is int category)
        {
            // A type is a Symbol or a family; everything else (rooms, materials, views, rebar ...) is an instance.
            foreach (int id in BoundTo(category, !IsTypeClass(element)))
                if (!ids.Contains(id)) ids.Add(id);
        }
        var result = new List<UserParameterEntry>(ids.Count);
        foreach (int id in ids)
        {
            if (!_definitions.TryGetValue(id, out var definition) || (definition.Storage == StorageType.None && !includeUnvalidated)) continue;
            UserParameterValue? value = null;
            string source = "none";
            bool noTypeValue = isSymbol && named != null && named.TryGetValue(id, out var flagged) && flagged.InstanceRecord;
            if (!noTypeValue)
            {
                if (named != null && named.TryGetValue(id, out var n)) { value = n; source = "named"; }
                else if (stored != null && stored.TryGetValue(id, out var s)) { value = s; source = "stored"; }
                else if (symbolNamed != null && symbolNamed.TryGetValue(id, out var sn)) { value = sn; source = "symbol-named"; }
                else if (symbolStored != null && symbolStored.TryGetValue(id, out var ss)) { value = ss; source = "symbol-stored"; }
            }
            // The type default of a number is not what Revit prints for instance entries (formula driven family parameters),
            // so a number taken from the symbol is not validated and the entry is left out.
            if (value != null && definition.Storage == StorageType.Double && !includeUnvalidated
                && (named == null || !named.ContainsKey(id)) && (stored == null || !stored.ContainsKey(id))) continue;
            if (value == null && !noTypeValue && fromFamily.Contains(id) && !includeUnvalidated) continue;
            result.Add(new UserParameterEntry(definition, value, fromFamily.Contains(id) ? "family:" + source : "bound:" + source));
        }
        return result;
    }

    /// <summary>Builds the catalog from the native parameter captures of a finished parse.</summary>
    internal static UserParameterCatalog Build(ParsingState state)
    {
        var definitions = new Dictionary<int, UserParameterDefinition>();
        var bindings = new Dictionary<int, List<UserParameterBinding>>();
        var familyParameters = new Dictionary<int, (int, bool)[]>();
        var familyCategory = new Dictionary<int, int>();
        var symbolFamily = new Dictionary<int, int>();
        var instanceSymbol = new Dictionary<int, int>();
        var namedValues = new Dictionary<int, Dictionary<int, UserParameterValue>>();
        var storedValues = new Dictionary<int, Dictionary<int, UserParameterValue>>();
        var instanceInfoSymbol = new Dictionary<int, int>();
        var sectionViews = new List<(int Section, int View)>();

        foreach (var (key, np) in state.NativeParams)
        {
            if (!state.ElemClassIdx.TryGetValue(key, out ushort cls)) continue;
            int id = (int)key;
            if (np.Fields.TryGetValue(WideNativeParams.ViewerViewKey, out int viewerId) && viewerId > 0
                && TfBlockDeserializer.Schemas.TryGetValue(cls, out var viewerClass) && ViewMarkerClasses.Contains(viewerClass.Name))
                sectionViews.Add((id, viewerId));
            if (TfBlockDeserializer.IsSubclassOf(cls, ParamElemClass))
            {
                if (np.StringFields.TryGetValue(TfClassNames.Key("ParamDef", "m_caption"), out string? caption)
                    && np.Fields.TryGetValue("2870:m_pParamDef.class", out int defClass)
                    && np.Fields.TryGetValue(TfClassNames.Key("ParamDef", "m_paramElemId"), out int paramElem) && paramElem == id)
                {
                    string? groupTypeId = null;
                    if (!np.Fields.TryGetValue(TfClassNames.Key("ParamDef", "m_groupElemId"), out int group)
                        && !np.StringFields.TryGetValue(WideNativeParams.GroupTypeKey, out groupTypeId)) continue;
                    if (groupTypeId != null) group = -1;
                    np.StringFields.TryGetValue(TfClassNames.Key("ParamDef", "m_typeId"), out string? typeId);
                    np.StringFields.TryGetValue(TfClassNames.Key("ParamDefValue", "m_specTypeId"), out string? spec);
                    definitions[id] = new UserParameterDefinition(id, cls, (ushort)defClass, caption, group, typeId, spec,
                        np.Fields.GetValueOrDefault(TfClassNames.Key("ParamDef", "m_readOnly")) != 0, np.Fields.GetValueOrDefault(TfClassNames.Key("ParamDef", "m_userVisible")) != 0,
                        np.Fields.TryGetValue(TfClassNames.Key("ParamElemFamily", "m_instanceParam"), out int instanceParam) ? instanceParam != 0 : null, groupTypeId);
                }
            }
            else if (cls == ParamBindingClass)
            {
                if (np.Fields.TryGetValue(TfClassNames.Key("ParamBinding", "m_paramId"), out int paramId) && np.Fields.TryGetValue(TfClassNames.Key("ParamBinding", "m_categoryId"), out int category)
                    && np.Fields.TryGetValue(TfClassNames.Key("ParamBinding", "m_elemOrSymbol"), out int elemOrSymbol) && elemOrSymbol is 1 or 2)
                {
                    if (!bindings.TryGetValue(paramId, out var list)) bindings[paramId] = list = [];
                    list.Add(new UserParameterBinding(category, elemOrSymbol == 1));
                }
            }
            else if (TfBlockDeserializer.IsSubclassOf(cls, FamilyBaseClass))
            {
                familyParameters[id] = np.NamedParams.Where(r => r.ParamId > 0).Select(r => (r.ParamId, r.Instance)).ToArray();
                if (np.Fields.TryGetValue(TfClassNames.Key("FamilyBase", "m_categoryId"), out int familyCat)) familyCategory[id] = familyCat;
            }
            else if (cls == FamilySymbolClass)
            {
                if (np.Fields.TryGetValue(TfClassNames.Key("FamilySymbol", "m_familyId"), out int family)) symbolFamily[id] = family;
            }
            else if (cls == FamilyInstanceClass)
            {
                if (np.Fields.TryGetValue("m_masterSymbolId", out int symbol)) instanceSymbol[id] = symbol;
                if (np.Fields.TryGetValue(TfClassNames.Key("InstInfoBase", "m_symbolId"), out int infoSymbol) && infoSymbol > 0) instanceInfoSymbol[id] = infoSymbol;
            }

            if (np.NamedParams.Count > 0 && (cls == FamilySymbolClass || cls == FamilyInstanceClass))
            {
                var values = new Dictionary<int, UserParameterValue>();
                foreach (var r in np.NamedParams)
                    if (r.ParamId > 0) values.TryAdd(r.ParamId, NamedValue(r));
                if (values.Count > 0) namedValues[id] = values;
            }
            if (np.Doubles.Count + np.Ints.Count + np.Strings.Count + np.ElementIds.Count > 0)
            {
                Dictionary<int, UserParameterValue>? stored = null;
                foreach (var (pid, v) in np.Doubles) if (pid > 0) (stored ??= new()).TryAdd(pid, new(v, 0, 0, null));
                foreach (var (pid, v) in np.Ints) if (pid > 0) (stored ??= new()).TryAdd(pid, new(0, v, 0, null));
                foreach (var (pid, v) in np.Strings) if (pid > 0) (stored ??= new()).TryAdd(pid, new(0, 0, 0, v));
                foreach (var (pid, v) in np.ElementIds) if (pid > 0) (stored ??= new()).TryAdd(pid, new(0, 0, v, null));
                if (stored != null) storedValues[id] = stored;
            }
        }
        var sectionView = sectionViews
            .Where(p => state.NativeParams.TryGetValue((uint)p.View, out var view) && view.RootParameterStoresComplete)
            .ToDictionary(p => p.Section, p => p.View);
        return new UserParameterCatalog(definitions, bindings, familyParameters, familyCategory, symbolFamily, instanceSymbol,
            namedValues, storedValues, instanceInfoSymbol, sectionView);
    }

    private static UserParameterValue NamedValue(TfBlockDeserializer.NamedParamRecord r)
        => new(r.Value, r.Int, r.ElemId, r.Str, r.Instance);
}
