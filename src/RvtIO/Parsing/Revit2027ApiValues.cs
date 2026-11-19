using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>Revit 2027 API values backed by an element's exact record or a link from that record.</summary>
internal static partial class Revit2027ApiValues
{
    private static readonly string ZoneType = TfClassNames.Key("GenericZone", "m_typeId");
    private static readonly string StructuralMaterial = TfClassNames.Key("AnalyticalElement", "m_materialId");
    private static readonly string CenterLineOwner = TfClassNames.Key("CenterLine", "m_OwnerId");
    private static readonly string ViewMarker = TfClassNames.Key("DBView", "m_viewerId");
    private static readonly string ElevationParent = TfClassNames.Key("InteriorElevArrow", "m_parentId");
    private static readonly string ElevationType = TfClassNames.Key("InteriorElev", "m_symbolId");
    private static readonly string StairOwner = TfClassNames.Key("StairsRunAndLanding", "m_stairsId");
    private static readonly string StairType = TfClassNames.Key("StairsElement", "m_typeId");
    private static readonly string RunType = TfClassNames.Key("StairsType", "m_runTypeId");
    private static readonly string LandingType = TfClassNames.Key("StairsType", "m_landingTypeId");
    private static readonly string ConstructionCategory = TfClassNames.Key("ConceptualConstructionType", "m_massSurfaceSubCategoryId");
    private static readonly string ConstructionKind = TfClassNames.Key("ConceptualConstructionType", "m_conceptualConstructionTypeEnumAsInt");
    private static readonly string TriserHost = TfClassNames.Key("StairsTriserSymbol", "m_hostId");
    private static readonly string RegionSketch = TfClassNames.Key("SketchedRegion", "m_sketchId");
    private static readonly string SketchUser = TfClassNames.Key("SketchBase", "m_userId");

    internal static readonly IReadOnlySet<string> RootFields =
        new HashSet<string>([ZoneType, StructuralMaterial, CenterLineOwner, ViewMarker, ElevationParent,
            ElevationType, StairOwner, StairType, RunType, LandingType,
            ConstructionCategory, ConstructionKind, TriserHost, RegionSketch, SketchUser], StringComparer.Ordinal);

    internal static void Apply(RevitFileInfo info, ParsingState state, IReadOnlyDictionary<int, Element> elementsById)
    {
        if (state.WideSchema?.Release != 2027 || state.R22View) return;
        foreach (var element in info.Elements)
        {
            if (ExactRoot(state, element.Id) is not { } root) continue;
            string? kind = element.NativeRootClassName;
            var fields = root.Fields;
            if (kind == "GenericZone" && fields.TryGetValue(ZoneType, out int zoneType)
                && RootIs(state, zoneType, "MEPSystemZoneElementType"))
            {
                element.TypeId = zoneType;
                SetTypeParameters(element, zoneType);
            }
            if (element.NativeRootClassChain?.Contains("Viewer") == true
                && fields.TryGetValue(WideNativeParams.ViewerViewKey, out int view)
                && ExactRoot(state, view) is { } viewRoot
                && viewRoot.Fields.TryGetValue("m_dbViewTypeId", out int viewType))
            {
                if (viewType == -1 && kind == "InteriorElevArrow"
                    && state.NativeRootClassNames.GetValueOrDefault((uint)view) == "DBViewSection"
                    && viewRoot.Fields.GetValueOrDefault(ViewMarker) == element.Id)
                    viewType = LinkedElevationType(state, view, viewRoot);
                if (RootIs(state, viewType, "DBViewType"))
                {
                    element.TypeId = viewType;
                    if (kind is "Viewer3d" or "Section" or "GraphSchedColumnTable" or "InteriorElevArrow")
                        SetTypeParameters(element, viewType);
                }
            }
            if (kind == "DBViewSection" && element.TypeId <= 0 && fields.GetValueOrDefault("m_dbViewTypeId") == -1)
            {
                int sectionType = LinkedElevationType(state, element.Id, root);
                if (RootIs(state, sectionType, "DBViewType"))
                {
                    element.TypeId = sectionType;
                    SetTypeParameters(element, sectionType);
                }
            }
            if (element.NativeIsSymbol == true)
            {
                string? storedName = state.WideNames.GetValueOrDefault((uint)element.Id);
                string? ownName = TypeName(state, kind, root) ?? storedName;
                if (ownName != null)
                    SetTypeName(element, ownName, kind == "StairsTriserSymbol" && root.Strings.All(p => p.ParamId != (int)BuiltInParameter.ALL_MODEL_TYPE_NAME)
                        ? storedName : null);
            }

            switch (kind)
            {
                case "FilledRegion" when fields.TryGetValue(RegionSketch, out int sketchId)
                    && ExactRootOf(state, sketchId, "VarSketch") is { } sketch
                    && sketch.Fields.GetValueOrDefault(SketchUser) == element.Id
                    && sketch.DoubleFields.TryGetValue(WideNativeParams.SketchLineArea, out double area):
                    SetParameter(element, BuiltInParameter.HOST_AREA_COMPUTED, StorageType.Double, area);
                    break;
                case "RbsDuctCurve":
                    ApplyRectangularDuctVolume(state, element, root);
                    break;
                case "StairsRun" or "StairsLanding" when element.TypeId <= 0:
                    if (fields.TryGetValue(StairOwner, out int stairId)
                        && ExactRootOf(state, stairId, "StairsElement") is { } stair
                        && stair.Fields.TryGetValue(StairType, out int stairTypeId)
                        && ExactRootOf(state, stairTypeId, "StairsType") is { } stairType
                        && stairType.Fields.TryGetValue(kind == "StairsRun" ? RunType : LandingType, out int partType)
                        && RootIs(state, partType, kind + "Type"))
                    {
                        element.TypeId = partType;
                        SetTypeParameters(element, partType);
                    }
                    break;
                case "Property" when element.TypeId > 0:
                    // Property.m_attrId is the property line's attributes and GetTypeId returns InvalidElementId.
                    element.TypeId = -1;
                    foreach (var parameter in element.Parameters.Where(p => p.Id is -1002050 or -1002000 or -1002052 or -1002051))
                    {
                        parameter.Value = -1;
                        parameter.HasNoRevitValue = true;
                    }
                    break;
                case "Rebar" or "RebarInSystem" when element.RebarBars is { Bars.Count: > 0, IsFreeForm: false } bar:
                    if (RootIs(state, bar.BarTypeId, "RebarBarType"))
                        SetTypeParameters(element, bar.BarTypeId);
                    SetParameter(element, BuiltInParameter.REBAR_ELEM_QUANTITY_OF_BARS, StorageType.Integer, bar.Bars.Count);
                    if (bar.BarLength is double length && double.IsFinite(length) && length > 0)
                        SetParameter(element, BuiltInParameter.REBAR_ELEM_LENGTH, StorageType.Double, length);
                    if (RootIs(state, bar.ShapeId, "RebarShape"))
                        SetParameter(element, BuiltInParameter.REBAR_SHAPE, StorageType.ElementId, bar.ShapeId);
                    break;
                case "AnalyticalMember" or "AnalyticalPanel" when fields.TryGetValue(StructuralMaterial, out int material)
                    && (material == -1 || RootIs(state, material, "MaterialElem")):
                    SetParameter(element, BuiltInParameter.STRUCTURAL_MATERIAL_PARAM, StorageType.ElementId, material);
                    break;
            }

            if (element.NativeRootClassChain?.Contains("CenterLine") == true
                && fields.TryGetValue(CenterLineOwner, out int ownerId) && ownerId != element.Id
                && ExactRoot(state, ownerId) != null && elementsById.TryGetValue(ownerId, out var owner)
                && owner.NativeRootClassChain?.Contains("CenterLine") != true)
            {
                CopyParameter(element, owner, BuiltInParameter.ALL_MODEL_MARK);
                if (ExactRoot(state, owner.TypeId) != null)
                {
                    SetTypeParameters(element, owner.TypeId);
                    if (state.WideNames.TryGetValue((uint)owner.TypeId, out string? typeName))
                        SetTypeName(element, typeName);
                }
                if (kind == "SegmentCenterLine" && owner.NativeRootClassName == "RbsPipeCurve")
                    CopyParameter(element, owner, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            }
        }
        ApplyHostQuantities(info, state, elementsById);
    }

    private static void ApplyRectangularDuctVolume(ParsingState state, Element element, TfBlockDeserializer.NativeParams root)
    {
        if (element.CategoryId != (int)BuiltInCategory.OST_DuctCurves
            || !root.Fields.TryGetValue("602:m_idType", out int type) || !RootIs(state, type, "AbsDuctType")
            || !state.SystemTypeRoots.TryGetValue((uint)type, out var typeRoot) || typeRoot != (1, 110, 127)) return;
        var dimensions = root.DoubleFields;
        if (!dimensions.ContainsKey("87:m_pCrv.endParams@0")
            || !dimensions.TryGetValue("602:m_dWidthOrDiameter", out double width)
            || !dimensions.TryGetValue("602:m_dHeight", out double height)
            || element.Parameters.FirstOrDefault(p => p.Id == (int)BuiltInParameter.CURVE_ELEM_LENGTH)
                is not { HasRevitValue: true, Value: double length }) return;
        double volume = width * height * length;
        if (width > 0 && height > 0 && length > 0 && double.IsFinite(volume))
            SetParameter(element, BuiltInParameter.HOST_VOLUME_COMPUTED, StorageType.Double, volume);
    }

    private static int LinkedElevationType(ParsingState state, int viewId, TfBlockDeserializer.NativeParams view)
    {
        if (view.Fields.TryGetValue(ViewMarker, out int arrowId)
            && ExactRootOf(state, arrowId, "InteriorElevArrow") is { } arrow
            && arrow.Fields.GetValueOrDefault(WideNativeParams.ViewerViewKey) == viewId
            && arrow.Fields.TryGetValue(ElevationParent, out int markerId)
            && ExactRootOf(state, markerId, "InteriorElev") is { } marker
            && marker.Fields.TryGetValue(ElevationType, out int typeId))
            return typeId;
        return -1;
    }

    private static TfBlockDeserializer.NativeParams? ExactRoot(ParsingState state, int id) =>
        id > 0 && state.NativeParamsFromRootClass.Contains((uint)id)
        && state.NativeParams.TryGetValue((uint)id, out var root)
        && root.TailWalkComplete && root.Fields.GetValueOrDefault("m_id") == id ? root : null;

    private static bool RootIs(ParsingState state, int id, string kind) =>
        ExactRootOf(state, id, kind) != null;

    private static TfBlockDeserializer.NativeParams? ExactRootOf(ParsingState state, int id, string kind) =>
        state.NativeRootClassNames.GetValueOrDefault((uint)id) == kind ? ExactRoot(state, id) : null;

    private static void CopyParameter(Element element, Element owner, BuiltInParameter parameter)
    {
        if (owner.Parameters.FirstOrDefault(p => p.Id == (int)parameter) is { HasRevitValue: true, Value: { } value } source)
            SetParameter(element, parameter, source.StorageType, value);
    }

    private static void SetTypeParameters(Element element, int typeId)
    {
        SetParameter(element, BuiltInParameter.ELEM_TYPE_PARAM, StorageType.ElementId, typeId);
        SetParameter(element, BuiltInParameter.ELEM_FAMILY_PARAM, StorageType.ElementId, typeId);
    }

    private static void SetTypeName(Element element, string name, string? storedAlias = null)
    {
        if (element.Parameters.FirstOrDefault(p => p.Id == (int)BuiltInParameter.ALL_MODEL_TYPE_NAME)
            is { Value: string currentName } parameter && (currentName.Length == 0 || currentName == storedAlias))
        {
            parameter.Value = name;
            parameter.FromNativeFields = true;
        }
    }

    private static void SetParameter(Element element, BuiltInParameter id, StorageType type, object value)
    {
        var parameter = element.Parameters.FirstOrDefault(p => p.Id == (int)id);
        if (parameter is { HasRevitValue: true }) return;
        if (parameter == null)
        {
            parameter = BipHelpers.MakeBipParam((int)id, id.ToString(), type, value);
            element.Parameters.Add(parameter);
        }
        else
        {
            parameter.StorageType = type;
            parameter.Value = value;
        }
        parameter.FromNativeFields = true;
        parameter.HasNoRevitValue = type == StorageType.ElementId && value is -1;
    }
}
