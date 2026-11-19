using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class Revit2027ApiValues
{
    private const int Coarse = 0, Medium = 1, Fine = 2;

    private static void ApplyHostQuantities(RevitFileInfo info, ParsingState state, IReadOnlyDictionary<int, Element> elementsById)
    {
        if (!info.ElemTableIsExact || state.WideSchema is not { } schema
            || state.FramedPartitions is not { Count: > 0 } frames || frames.Any(frame => frame?.IdWidth != 8)) return;
        WideSolidQuantities? geometry = null;
        foreach (var element in info.Elements)
        {
            var (areaLevel, volumeLevel) = QuantityLevels(element);
            bool horizontal = UsesHorizontalHostArea(element);
            bool extrusionRoof = element is { NativeRootClassName: "Roof", CategoryId: (int)BuiltInCategory.OST_Roofs };
            bool rawSurface = element.NativeRootClassName == "FamilyInstance" && (BuiltInCategory?)element.CategoryId is
                BuiltInCategory.OST_GenericModel or BuiltInCategory.OST_PlumbingFixtures or BuiltInCategory.OST_Mass
                or BuiltInCategory.OST_Site or BuiltInCategory.OST_Furniture;
            bool rawVolume = rawSurface && element.CategoryId != (int)BuiltInCategory.OST_Mass;
            bool door = element is { NativeRootClassName: "FamilyInstance", CategoryId: (int)BuiltInCategory.OST_Doors };
            bool filled = element.NativeRootClassName == "FilledRegion" || element.NativeRootClassName == "FamilyInstance"
                && (BuiltInCategory?)element.CategoryId is BuiltInCategory.OST_GenericAnnotation or BuiltInCategory.OST_SpotElevSymbols
                    or BuiltInCategory.OST_DetailComponents;
            string? wallType = element.NativeRootClassName == "SWall" && element.CategoryId == (int)BuiltInCategory.OST_Walls
                && ExactRoot(state, element.TypeId) != null ? state.NativeRootClassNames.GetValueOrDefault((uint)element.TypeId) : null;
            bool wallArea = wallType is "BasicWallType" or "NewCurtainWallType";
            if (wallType == "BasicWallType") volumeLevel = Coarse;
            bool areaMissing = (areaLevel.HasValue || wallArea || horizontal || extrusionRoof || door || filled || rawSurface)
                && !HasValue(element, BuiltInParameter.HOST_AREA_COMPUTED);
            bool volumeMissing = (volumeLevel.HasValue || rawVolume) && !HasValue(element, BuiltInParameter.HOST_VOLUME_COMPUTED);
            if ((!areaMissing && !volumeMissing) || ExactRoot(state, element.Id) is not { } root) continue;

            geometry ??= new WideSolidQuantities(schema, frames);
            bool curtainDoor = false;
            if (wallArea && areaMissing)
            {
                bool curtain = wallType == "NewCurtainWallType";
                double? width = state.CompoundLayers.GetValueOrDefault((uint)element.TypeId)?.Sum(layer => layer.ThicknessFt);
                if ((curtain || width is > 0) && root.DoubleFields.TryGetValue("87:m_pCrv.dirVec@0", out double dx)
                    && root.DoubleFields.TryGetValue("87:m_pCrv.dirVec@1", out double dy)
                    && root.DoubleFields.TryGetValue("87:m_pCrv.dirVec@2", out double dz))
                    SetQuantity(element, BuiltInParameter.HOST_AREA_COMPUTED, geometry.WallArea(element.Id, dx, dy, dz, curtain ? null : width, curtain));
            }
            if (horizontal && areaMissing)
                SetQuantity(element, BuiltInParameter.HOST_AREA_COMPUTED, geometry.HorizontalArea(element.Id,
                    bottom: element.CategoryId == (int)BuiltInCategory.OST_StructuralFoundation));
            if (extrusionRoof && areaMissing && root.RoofExtrusionFaceTags is { Count: > 0 } tags)
                SetQuantity(element, BuiltInParameter.HOST_AREA_COMPUTED, geometry.RoofArea(element.Id, tags));
            if (door)
            {
                Element? host = element.SerializedHostId is > 0 and int hostId ? elementsById.GetValueOrDefault(hostId) : null;
                curtainDoor = state.CurtainFamilyHostSource.TryGetValue((uint)element.Id, out var curtainSource)
                    && curtainSource.BlockCount == 1 && curtainSource.Host is { ReferenceClass: 897, HostId: > 0, ReplacementElementId: null }
                    || host != null && RootIs(state, host.TypeId, "NewCurtainWallType");
                if (areaMissing)
                {
                    if (curtainDoor) SetQuantity(element, BuiltInParameter.HOST_AREA_COMPUTED, geometry.CurtainDoorArea(element.Id));
                    else if (host != null && RootIs(state, host.TypeId, "BasicWallType"))
                        SetQuantity(element, BuiltInParameter.HOST_AREA_COMPUTED,
                            geometry.Measure(element.Id, WideSolidQuantities.Source.Directional)?[Coarse]?.Surface / 2);
                }
            }
            if (filled && areaMissing)
            {
                double? filledArea = element.NativeRootClassName == "FilledRegion" ? geometry.FilledArea(element.Id)
                    : root.Fields.TryGetValue("2237:m_symbolId", out int symbol) && RootIs(state, symbol, "FamilySymbol")
                        ? geometry.FilledArea(symbol) / 2 : null;
                SetQuantity(element, BuiltInParameter.HOST_AREA_COMPUTED, filledArea);
            }
            if (!volumeMissing && (!areaMissing || areaLevel == null && !rawSurface)) continue;
            var source = (BuiltInCategory?)element.CategoryId switch
            {
                BuiltInCategory.OST_LightingFixtures => WideSolidQuantities.Source.Directional,
                BuiltInCategory.OST_PipeFitting or BuiltInCategory.OST_DuctFitting => WideSolidQuantities.Source.Fitting,
                BuiltInCategory.OST_StructuralFraming => WideSolidQuantities.Source.StructuralFraming,
                BuiltInCategory.OST_Doors when curtainDoor => WideSolidQuantities.Source.Analytic,
                BuiltInCategory.OST_Floors or BuiltInCategory.OST_Walls or BuiltInCategory.OST_GenericModel
                    or BuiltInCategory.OST_SpecialityEquipment or BuiltInCategory.OST_PlumbingFixtures
                    or BuiltInCategory.OST_ConduitFitting or BuiltInCategory.OST_StructuralFoundation
                    or BuiltInCategory.OST_Furniture or BuiltInCategory.OST_MechanicalEquipment or BuiltInCategory.OST_Casework or BuiltInCategory.OST_Mass
                    => WideSolidQuantities.Source.Analytic,
                _ => WideSolidQuantities.Source.Default,
            };
            var levels = geometry.Measure(element.Id, source);
            if (areaMissing && areaLevel is int detail && levels?[detail] is { } area)
            {
                double? amount = (BuiltInCategory?)element.CategoryId switch
                {
                    BuiltInCategory.OST_CurtainWallPanels => area.LargestFace,
                    BuiltInCategory.OST_Mass => geometry.MassArea(element.Id, area),
                    _ => area.Surface / 2,
                };
                SetQuantity(element, BuiltInParameter.HOST_AREA_COMPUTED, amount);
            }
            if (levels != null && areaMissing && (BuiltInCategory?)element.CategoryId is BuiltInCategory.OST_PipeFitting
                or BuiltInCategory.OST_PlumbingFixtures or BuiltInCategory.OST_MechanicalEquipment
                && !HasValue(element, BuiltInParameter.HOST_AREA_COMPUTED))
                SetQuantity(element, BuiltInParameter.HOST_AREA_COMPUTED,
                    geometry.Measure(element.Id, WideSolidQuantities.Source.Directional)?[Coarse]?.Surface / 2);
            if (rawSurface && areaMissing && !HasValue(element, BuiltInParameter.HOST_AREA_COMPUTED))
            {
                var surfaceSource = element.CategoryId == (int)BuiltInCategory.OST_PlumbingFixtures
                    ? WideSolidQuantities.Source.DirectionalRawSurface : WideSolidQuantities.Source.RawBodies;
                if (geometry.Measure(element.Id, surfaceSource)?[Coarse] is { } surface
                    && (element.CategoryId != (int)BuiltInCategory.OST_Mass
                        || surface.BodyCount == 1 && geometry.HasOneHorizontalCap(element.Id)))
                    SetQuantity(element, BuiltInParameter.HOST_AREA_COMPUTED, surface.Surface / 2);
            }
            var volumeMeasure = volumeLevel is int index ? levels?[index] : null;
            if (element.CategoryId == (int)BuiltInCategory.OST_ConduitFitting && volumeMeasure is null or { BodyCount: 0 })
                volumeMeasure = levels?[Fine];
            if (volumeMissing && volumeMeasure is null or { BodyCount: 0 }
                && (BuiltInCategory?)element.CategoryId is BuiltInCategory.OST_PlumbingFixtures
                    or BuiltInCategory.OST_MechanicalEquipment or BuiltInCategory.OST_Doors
                    or BuiltInCategory.OST_GenericModel or BuiltInCategory.OST_SpecialityEquipment
                && geometry.Measure(element.Id, WideSolidQuantities.Source.VisibleVolume) is { } visible)
            {
                volumeMeasure = visible[Coarse];
                if (element.CategoryId == (int)BuiltInCategory.OST_PlumbingFixtures && volumeMeasure is { BodyCount: 0 })
                    volumeMeasure = visible[Medium];
            }
            if (rawVolume && volumeMissing && volumeMeasure is null or { BodyCount: 0 })
                volumeMeasure = geometry.Measure(element.Id, WideSolidQuantities.Source.RawBodies)?[Coarse];
            if (volumeMissing && volumeMeasure is { } volume)
            {
                double? amount = (BuiltInCategory?)element.CategoryId switch
                {
                    BuiltInCategory.OST_DuctFitting or BuiltInCategory.OST_PipeFitting => volume.Volume - volume.SharedCoarseVolume,
                    _ => volume.Volume,
                };
                SetQuantity(element, BuiltInParameter.HOST_VOLUME_COMPUTED, amount);
            }
        }

        foreach (var line in info.Elements)
        {
            if (line.NativeRootClassName is not ("SegmentCenterLine" or "FlexCenterLine" or "PipeFittingCenterLine"
                or "DuctFittingCenterLine" or "ConduitFittingCenterLine") || ExactRoot(state, line.Id) is not { } root
                || !root.Fields.TryGetValue(CenterLineOwner, out int ownerId) || ownerId == line.Id
                || ExactRoot(state, ownerId) == null || !elementsById.TryGetValue(ownerId, out var owner)
                || QuantityLevels(owner).Volume == null) continue;
            CopyParameter(line, owner, BuiltInParameter.HOST_VOLUME_COMPUTED);
            if (line.NativeRootClassName is "PipeFittingCenterLine" or "DuctFittingCenterLine" or "ConduitFittingCenterLine")
                CopyParameter(line, owner, BuiltInParameter.HOST_AREA_COMPUTED);
        }
    }

    private static bool UsesHorizontalHostArea(Element element) => (element.NativeRootClassName, (BuiltInCategory?)element.CategoryId) is
        ("Floor", BuiltInCategory.OST_Floors or BuiltInCategory.OST_StructuralFoundation)
        or ("Ceiling", BuiltInCategory.OST_Ceilings)
        or ("ContFooting" or "FamilyInstance", BuiltInCategory.OST_StructuralFoundation)
        or ("FamilyInstance", BuiltInCategory.OST_FurnitureSystems)
        or ("BuildingPad", BuiltInCategory.OST_BuildingPad) or ("ProfileRoof", BuiltInCategory.OST_Roofs);

    private static (int? Area, int? Volume) QuantityLevels(Element element)
    {
        switch (element.NativeRootClassName, (BuiltInCategory?)element.CategoryId)
        {
            case ("RbsDuctCurve", BuiltInCategory.OST_DuctCurves):
            case ("RbsFlexDuctCurve", BuiltInCategory.OST_FlexDuctCurves):
            case ("RbsPipeCurve", BuiltInCategory.OST_PipeCurves):
            case ("RbsConduitCurve", BuiltInCategory.OST_Conduit):
            case ("Floor", BuiltInCategory.OST_Floors or BuiltInCategory.OST_StructuralFoundation):
            case ("Ceiling", BuiltInCategory.OST_Ceilings):
            case ("ContFooting" or "FamilyInstance", BuiltInCategory.OST_StructuralFoundation):
            case ("BuildingPad", BuiltInCategory.OST_BuildingPad):
            case ("ProfileRoof" or "Roof", BuiltInCategory.OST_Roofs):
            case ("FamilyInstance", BuiltInCategory.OST_FurnitureSystems):
                return (null, Coarse);
            case ("FamilyInstance", BuiltInCategory.OST_Doors):
                return (null, Coarse);
            case ("FamilyInstance", BuiltInCategory.OST_Mass):
                return (Coarse, null);
            case ("FamilyInstance", BuiltInCategory.OST_StructuralFraming):
                return (null, Fine);
            case ("FamilyInstance", BuiltInCategory.OST_DuctFitting):
                return (Coarse, Medium);
            case ("FamilyInstance", BuiltInCategory.OST_PipeFitting or BuiltInCategory.OST_StructuralColumns):
                return (Coarse, Fine);
            case ("FamilyInstance", BuiltInCategory.OST_CurtainWallMullions or BuiltInCategory.OST_CurtainWallPanels
                or BuiltInCategory.OST_DuctTerminal or BuiltInCategory.OST_ElectricalFixtures or BuiltInCategory.OST_ElectricalEquipment
                or BuiltInCategory.OST_LightingDevices or BuiltInCategory.OST_LightingFixtures or BuiltInCategory.OST_StairsRailingBaluster or BuiltInCategory.OST_BoundaryConditions
                or BuiltInCategory.OST_Furniture or BuiltInCategory.OST_MechanicalEquipment or BuiltInCategory.OST_Windows
                or BuiltInCategory.OST_ConduitFitting or BuiltInCategory.OST_GenericModel or BuiltInCategory.OST_SpecialityEquipment
                or BuiltInCategory.OST_PlumbingFixtures or BuiltInCategory.OST_Sprinklers or BuiltInCategory.OST_Columns or BuiltInCategory.OST_Casework):
                return (Coarse, Coarse);
            default:
                return (null, null);
        }
    }

    private static bool HasValue(Element element, BuiltInParameter id) => element.Parameters.Any(p => p.Id == (int)id && p.HasRevitValue);

    private static void SetQuantity(Element element, BuiltInParameter id, double? value)
    {
        if (value is > 0 and double amount && double.IsFinite(amount)) SetParameter(element, id, StorageType.Double, amount);
    }
}
