using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    private static readonly Regex UnwrappedReferenceRegex = new(@"#(\d+)", RegexOptions.Compiled);

    internal static string FormatIfcSchemaName(IfcSchema schema) => schema switch
    {
        IfcSchema.Ifc4 => "IFC4",
        _ => "IFC2X3",
    };

    internal static string FormatModelView(IfcModelView modelView) => modelView switch
    {
        IfcModelView.ReferenceView12 => "ReferenceView_V1.2",
        IfcModelView.DesignTransferView10 => "DesignTransferView_V1.0",
        IfcModelView.CoordinationView20 => "CoordinationView_V2.0",
        IfcModelView.CoordinationView => "CoordinationView",
        _ => "Default",
    };

    internal static string FormatFileDescription(ResolvedIfcExportConfiguration config) => config.EffectiveModelView switch
    {
        IfcModelView.ReferenceView12 => "ViewDefinition [ReferenceView_V1.2]",
        IfcModelView.DesignTransferView10 => "ViewDefinition [DesignTransferView_V1.0]",
        IfcModelView.CoordinationView20 => $"ViewDefinition [CoordinationView_V2.0{(config.ExportBaseQuantities ? ", QuantityTakeOffAddOnView" : "")}]",
        IfcModelView.CoordinationView => "ViewDefinition [CoordinationView]",
        _ => config.Schema == IfcSchema.Ifc4
            ? "ViewDefinition [ReferenceView_V1.2]"
            : $"ViewDefinition [CoordinationView_V2.0{(config.ExportBaseQuantities ? ", QuantityTakeOffAddOnView" : "")}]",
    };

    internal static string? MapCategoryToIfcTypeForSchema(int? categoryId, IfcSchema schema)
    {
        if (schema != IfcSchema.Ifc4)
            return MapCategoryToIfcType(categoryId);
        return categoryId switch
        {
            -2001040 => "IFCELECTRICDISTRIBUTIONBOARD", // OST_ElectricalEquipment
            -2001060 => "IFCOUTLET",                    // OST_ElectricalFixtures
            -2008087 => "IFCSWITCHINGDEVICE",           // OST_LightingDevices
            -2001140 => "IFCDISTRIBUTIONFLOWELEMENT",   // OST_MechanicalEquipment
            -2001352 => "IFCDISTRIBUTIONFLOWELEMENT",   // OST_MechanicalEquipment (alt)
            _ => NormalizeIfcClassForSchema(MapCategoryToIfcType(categoryId), schema),
        };
    }

    internal static string? NormalizeIfcClassForSchema(string? ifcType, IfcSchema schema)
    {
        if (ifcType is null || schema != IfcSchema.Ifc4)
            return ifcType;
        return ifcType.ToUpperInvariant() switch
        {
            "IFCELECTRICDISTRIBUTIONPOINT" => "IFCELECTRICDISTRIBUTIONBOARD",
            _ => ifcType,
        };
    }

    internal static bool IsIfc4DistributionElementClass(string ifcType) =>
        ifcType.StartsWith("IFCDISTRIBUTION", StringComparison.Ordinal)
        || ifcType.StartsWith("IFCFLOW", StringComparison.Ordinal)
        || ifcType is "IFCENERGYCONVERSIONDEVICE" or "IFCELECTRICDISTRIBUTIONBOARD" or "IFCOUTLET"
            or "IFCSWITCHINGDEVICE" or "IFCSENSOR" or "IFCUNITARYEQUIPMENT";

    private static readonly HashSet<string> Ifc4ProductPredefinedTypeClasses = new(StringComparer.Ordinal)
    {
        "IFCWALL", "IFCWALLSTANDARDCASE", "IFCBEAM", "IFCCOLUMN", "IFCMEMBER", "IFCPLATE",
        "IFCCURTAINWALL", "IFCRAMPFLIGHT", "IFCOPENINGELEMENT", "IFCELECTRICDISTRIBUTIONBOARD",
        "IFCOUTLET", "IFCSWITCHINGDEVICE", "IFCSENSOR", "IFCUNITARYEQUIPMENT",
        "IFCDISTRIBUTIONCHAMBERELEMENT",
        "IFCGRID", "IFCREINFORCINGMESH",
        // These also accept .NOTDEFINED. (14 total for IFCREINFORCINGBAR -> no trailing append needed; 9 total for IFCFURNITURE/IFCSYSTEMFURNITUREELEMENT).
        "IFCFURNITURE", "IFCSYSTEMFURNITUREELEMENT",
    };

    private static readonly HashSet<string> Ifc4NineArgProductClassesNeedingPredefinedType = new(StringComparer.Ordinal)
    {
        // Note: IFCSLAB, IFCROOF, IFCFOOTING, IFCCOVERING, IFCRAILING, IFCSTAIR, IFCRAMP, IFCBUILDINGELEMENTPROXY already write 9 arguments.
        "IFCWALL", "IFCWALLSTANDARDCASE", "IFCBEAM", "IFCCOLUMN", "IFCMEMBER", "IFCPLATE",
        "IFCCURTAINWALL", "IFCRAMPFLIGHT", "IFCOPENINGELEMENT", "IFCELECTRICDISTRIBUTIONBOARD",
        "IFCOUTLET", "IFCSWITCHINGDEVICE", "IFCSENSOR", "IFCUNITARYEQUIPMENT",
        "IFCDISTRIBUTIONCHAMBERELEMENT", "IFCFURNITURE", "IFCSYSTEMFURNITUREELEMENT",
    };

    private static string ResolveIfc4QtoSetName(string? productType) => productType switch
    {
        "IFCWALL" or "IFCWALLSTANDARDCASE" or "IFCWALLELEMENTEDCASE" => "Qto_WallBaseQuantities",
        "IFCSLAB" or "IFCSLABSTANDARDCASE" or "IFCSLABELEMENTEDCASE" => "Qto_SlabBaseQuantities",
        "IFCDOOR" or "IFCDOORSTANDARDCASE" => "Qto_DoorBaseQuantities",
        "IFCWINDOW" or "IFCWINDOWSTANDARDCASE" => "Qto_WindowBaseQuantities",
        "IFCOPENINGELEMENT" or "IFCOPENINGSTANDARDCASE" => "Qto_OpeningElementBaseQuantities",
        "IFCSPACE" => "Qto_SpaceBaseQuantities",
        "IFCBEAM" or "IFCBEAMSTANDARDCASE" => "Qto_BeamBaseQuantities",
        "IFCCOLUMN" or "IFCCOLUMNSTANDARDCASE" => "Qto_ColumnBaseQuantities",
        "IFCMEMBER" or "IFCMEMBERSTANDARDCASE" => "Qto_MemberBaseQuantities",
        "IFCPLATE" or "IFCPLATESTANDARDCASE" => "Qto_PlateBaseQuantities",
        "IFCCOVERING" => "Qto_CoveringBaseQuantities",
        "IFCRAILING" => "Qto_RailingBaseQuantities",
        "IFCRAMP" => "Qto_RampBaseQuantities",
        "IFCRAMPFLIGHT" => "Qto_RampFlightBaseQuantities",
        "IFCSTAIR" => "Qto_StairBaseQuantities",
        "IFCSTAIRFLIGHT" => "Qto_StairFlightBaseQuantities",
        "IFCROOF" => "Qto_RoofBaseQuantities",
        "IFCFOOTING" => "Qto_FootingBaseQuantities",
        "IFCCURTAINWALL" => "Qto_CurtainWallBaseQuantities",
        "IFCBUILDINGSTOREY" => "Qto_BuildingStoreyBaseQuantities",
        _ => "BaseQuantities",
    };

    /// <summary>
    /// Upgrades written STEP entities to valid IFC4 (ISO 16739-1:2018) and applies the selected
    /// IFC4 Model View Definition constraints (<c>ReferenceView_V1.2</c> or <c>DesignTransferView_V1.0</c>).
    /// Runs before <see cref="IfcLengthUnit.Converter"/> while coordinates are in metres.
    /// </summary>
    internal int ApplyIfc4SchemaAndMvdCompliance(
        StepWriter w,
        ResolvedIfcExportConfiguration config,
        out Ifc4ComplianceSummary summary)
    {
        summary = default;
        if (config.Schema != IfcSchema.Ifc4)
            return 0;

        int updatedEntities = 0;

        // 1. Collect relation graph needed for WHERE-rule and Qto checks.
        var indexById = new Dictionary<int, int>(w.EntityCount);
        var wallIdsWithLayerSetUsage = new HashSet<int>();
        var qtoNameByEntityId = new Dictionary<int, string>();
        var psetNameById = new Dictionary<int, string>();
        var styleAssignmentStyles = new Dictionary<int, string[]>();
        var referencedShapeIds = new HashSet<int>();
        var productShapeIds = new List<int>();
        var emptyMaterialRelations = new List<int>();
        var spaceIds = new HashSet<int>();
        var aggregatedSpaceIds = new HashSet<int>();
        var emptyContainmentRelations = new List<int>();
        var spacesToAggregateByStorey = new Dictionary<int, List<int>>();
        int ownerHistId = 0;
        int suppressedSpaceMaterials = 0;
        int downgradedWalls = 0;

        int initialCount = w.EntityCount;
        for (int i = 0; i < initialCount; i++)
        {
            var (id, type, args) = w.EntityAt(i);
            indexById[id] = i;
            if (type == "IFCOWNERHISTORY" && ownerHistId == 0)
                ownerHistId = id;
            else if (type == "IFCSPACE")
                spaceIds.Add(id);
            else if (type == "IFCPRODUCTDEFINITIONSHAPE")
                productShapeIds.Add(id);
            else if (type == "IFCPRESENTATIONSTYLEASSIGNMENT")
            {
                var assignmentArgs = SplitStepArguments(args);
                if (assignmentArgs.Length >= 1 && assignmentArgs[0].StartsWith('(') && assignmentArgs[0].EndsWith(')'))
                    styleAssignmentStyles[id] = SplitStepArguments(assignmentArgs[0][1..^1]);
            }
            else if (type is "IFCPROPERTYSET" or "IFCELEMENTQUANTITY" or "IFCDOORLININGPROPERTIES"
                or "IFCWINDOWLININGPROPERTIES" or "IFCDOORPANELPROPERTIES" or "IFCWINDOWPANELPROPERTIES")
            {
                var propArgs = SplitStepArguments(args);
                if (propArgs.Length >= 3 && propArgs[2].Length >= 2 && propArgs[2][0] == '\'')
                    psetNameById[id] = propArgs[2];
            }
        }

        // Map IFCELEMENTQUANTITY -> primary product class, collect existing IfcRelAggregates for spaces,
        // move IfcSpace out of IFCRELCONTAINEDINSPATIALSTRUCTURE into IFCRELAGGREGATES, and find WallStandardCase layer usages.
        for (int i = 0; i < initialCount; i++)
        {
            var (id, type, args) = w.EntityAt(i);
            switch (type)
            {
                case "IFCRELAGGREGATES":
                {
                    var rel = SplitStepArguments(args);
                    if (rel.Length == 6 && rel[5].StartsWith('(') && rel[5].EndsWith(')'))
                    {
                        foreach (string tok in SplitStepArguments(rel[5][1..^1]))
                            if (TryEntityId(tok, out int childId) && spaceIds.Contains(childId))
                                aggregatedSpaceIds.Add(childId);
                    }
                    break;
                }
                case "IFCRELCONTAINEDINSPATIALSTRUCTURE":
                {
                    var rel = SplitStepArguments(args);
                    if (rel.Length != 6 || !rel[4].StartsWith('(') || !rel[4].EndsWith(')')
                        || !TryEntityId(rel[5], out int structureId))
                        break;
                    var tokens = SplitStepArguments(rel[4][1..^1]);
                    var kept = new List<string>(tokens.Length);
                    bool removedSpace = false;
                    foreach (string token in tokens)
                    {
                        if (TryEntityId(token, out int elemId) && spaceIds.Contains(elemId))
                        {
                            removedSpace = true;
                            if (!spacesToAggregateByStorey.TryGetValue(structureId, out var moved))
                                spacesToAggregateByStorey[structureId] = moved = [];
                            moved.Add(elemId);
                            continue;
                        }
                        kept.Add(token);
                    }
                    if (removedSpace)
                    {
                        if (kept.Count == 0)
                            emptyContainmentRelations.Add(id);
                        else
                        {
                            rel[4] = StepWriter.List(kept);
                            w.ReplaceArgs(i, string.Join(",", rel));
                            updatedEntities++;
                        }
                    }
                    break;
                }
                case "IFCRELDEFINESBYPROPERTIES":
                {
                    var rel = SplitStepArguments(args);
                    if (rel.Length != 6 || !TryEntityId(rel[5], out int propDefId)
                        || !indexById.TryGetValue(propDefId, out int propIndex)
                        || w.EntityAt(propIndex).Type != "IFCELEMENTQUANTITY")
                        break;
                    var relatedTokens = rel[4].StartsWith('(') && rel[4].EndsWith(')') ? SplitStepArguments(rel[4][1..^1]) : [];
                    if (relatedTokens.Length > 0 && TryEntityId(relatedTokens[0], out int firstObjId)
                        && indexById.TryGetValue(firstObjId, out int firstObjIndex))
                    {
                        string objType = w.EntityAt(firstObjIndex).Type;
                        string qtoName = objType == "IFCWALLSTANDARDCASE" && !config.UseWallStandardCase
                            ? "Qto_WallBaseQuantities"
                            : ResolveIfc4QtoSetName(objType);
                        qtoNameByEntityId.TryAdd(propDefId, qtoName);
                    }
                    break;
                }
                case "IFCRELASSOCIATESMATERIAL":
                {
                    var rel = SplitStepArguments(args);
                    if (rel.Length != 6) break;
                    bool isLayerSetUsage = TryEntityId(rel[5], out int matId)
                        && indexById.TryGetValue(matId, out int matIndex)
                        && w.EntityAt(matIndex).Type == "IFCMATERIALLAYERSETUSAGE";
                    if (rel[4].StartsWith('(') && rel[4].EndsWith(')'))
                    {
                        var tokens = SplitStepArguments(rel[4][1..^1]);
                        var kept = new List<string>(tokens.Length);
                        bool removedAny = false;
                        foreach (string token in tokens)
                        {
                            if (TryEntityId(token, out int targetId) && indexById.TryGetValue(targetId, out int targetIndex))
                            {
                                string targetType = w.EntityAt(targetIndex).Type;
                                if (targetType is "IFCSPACE" or "IFCOPENINGELEMENT")
                                {
                                    removedAny = true;
                                    if (targetType == "IFCSPACE") suppressedSpaceMaterials++;
                                    continue;
                                }
                                if (isLayerSetUsage && targetType == "IFCWALLSTANDARDCASE")
                                    wallIdsWithLayerSetUsage.Add(targetId);
                            }
                            kept.Add(token);
                        }
                        if (removedAny)
                        {
                            if (kept.Count == 0)
                                emptyMaterialRelations.Add(id);
                            else
                            {
                                rel[4] = StepWriter.List(kept);
                                w.ReplaceArgs(i, string.Join(",", rel));
                                updatedEntities++;
                            }
                        }
                    }
                    break;
                }
                default:
                {
                    var a = SplitStepArguments(args);
                    // Record IfcProduct.Representation (attribute 6) and IfcShapeAspect.PartOfProductDefinitionShape.
                    if (type == "IFCSHAPEASPECT" && a.Length >= 5 && TryEntityId(a[4], out int aspectShape))
                        referencedShapeIds.Add(aspectShape);
                    else if (a.Length >= 8 && TryEntityId(a[6], out int shapeRef)
                        && indexById.TryGetValue(shapeRef, out int shapeIdx)
                        && w.EntityAt(shapeIdx).Type == "IFCPRODUCTDEFINITIONSHAPE")
                        referencedShapeIds.Add(shapeRef);
                    break;
                }
            }
        }

        // Remove empty IFCRELCONTAINEDINSPATIALSTRUCTURE relations and emit IFCRELAGGREGATES for moved IfcSpace elements.
        if (emptyContainmentRelations.Count > 0)
            w.RemoveEntities(emptyContainmentRelations.ToHashSet());
        if (ownerHistId > 0 && spacesToAggregateByStorey.Count > 0)
        {
            foreach (var (structureId, movedSpaces) in spacesToAggregateByStorey)
            {
                var unaggregated = movedSpaces.Where(sId => aggregatedSpaceIds.Add(sId)).ToList();
                if (unaggregated.Count == 0) continue;
                w.Add("IFCRELAGGREGATES",
                    $"{S(IfcGuid.FromName($"ifc4-spatial-spaces:{structureId}"))},{R(ownerHistId)},$,$,"
                    + $"{R(structureId)},{StepWriter.List(unaggregated.Select(R))}");
                updatedEntities++;
            }
        }

        // Remove empty IFCRELASSOCIATESMATERIAL relations left after pruning IfcSpace.
        if (emptyMaterialRelations.Count > 0)
            w.RemoveEntities(emptyMaterialRelations.ToHashSet());

        // Clean orphaned IfcProductDefinitionShape entities (ShapeOfProduct : SET [1:?]).
        var orphanShapes = productShapeIds.Where(id => !referencedShapeIds.Contains(id)).ToList();
        int prunedOrphanShapes = orphanShapes.Count;
        if (orphanShapes.Count > 0)
            RemoveDetachedCurrent(w, orphanShapes);

        // Unwrap deprecated IFCPRESENTATIONSTYLEASSIGNMENT inside IFCSTYLEDITEM and collect assignments for deletion.
        var removedAssignments = new HashSet<int>();
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (_, type, args) = w.EntityAt(i);
            if (type != "IFCSTYLEDITEM") continue;
            var styledArgs = SplitStepArguments(args);
            if (styledArgs.Length != 3 || !styledArgs[1].StartsWith('(') || !styledArgs[1].EndsWith(')')) continue;
            var tokens = SplitStepArguments(styledArgs[1][1..^1]);
            var replacedTokens = new List<string>(tokens.Length);
            bool changed = false;
            foreach (string token in tokens)
            {
                if (TryEntityId(token, out int assignId) && styleAssignmentStyles.TryGetValue(assignId, out var inner))
                {
                    replacedTokens.AddRange(inner);
                    removedAssignments.Add(assignId);
                    changed = true;
                }
                else
                {
                    replacedTokens.Add(token);
                }
            }
            if (changed && replacedTokens.Count > 0)
            {
                styledArgs[1] = StepWriter.List(replacedTokens.Distinct(StringComparer.Ordinal));
                w.ReplaceArgs(i, string.Join(",", styledArgs));
                updatedEntities++;
            }
        }
        if (removedAssignments.Count > 0)
            RemoveDetachedCurrent(w, removedAssignments);

        // Rebuild index snapshot after deletions before entity attribute upgrades.
        var dirLookupIndex = new Dictionary<int, int>(w.EntityCount);
        for (int i = 0; i < w.EntityCount; i++)
            dirLookupIndex[w.EntityAt(i).Id] = i;
        var cachedDirections = new Dictionary<(double X, double Y, double Z), int>();
        var duplicatePsetsToPrune = new HashSet<int>();

        int EnsureDirection3D(double x, double y, double z)
        {
            var key = (x, y, z);
            if (!cachedDirections.TryGetValue(key, out int dirId))
            {
                dirId = w.Add("IFCDIRECTION", $"({StepWriter.Real(x)},{StepWriter.Real(y)},{StepWriter.Real(z)})");
                cachedDirections[key] = dirId;
            }
            return dirId;
        }

        // Ensure all entities comply with IFC4 attribute counts, renamed types, and WHERE rules.
        int upgradeLoopCount = w.EntityCount;
        for (int i = 0; i < upgradeLoopCount; i++)
        {
            var (id, type, args) = w.EntityAt(i);
            string[] a = SplitStepArguments(args);
            string newType = type;
            string? newArgs = null;

            switch (type)
            {
                case "IFCPERSON":
                    if (a.Length >= 3 && a[0] == "$" && a[1] == "$" && a[2] == "$")
                    {
                        a[0] = "''";
                        newArgs = string.Join(",", a);
                    }
                    break;

                case "IFCGEOMETRICREPRESENTATIONSUBCONTEXT":
                    if (a.Length >= 8 && a[7] is "0." or "0" or "0.0")
                    {
                        a[7] = "$";
                        newArgs = string.Join(",", a);
                    }
                    break;

                case "IFCAXIS2PLACEMENT3D":
                    if (a.Length == 3 && ((a[1] == "$") ^ (a[2] == "$")))
                    {
                        if (a[1] == "$")
                        {
                            var refVec = ReadDirectionTuple(w, dirLookupIndex, a[2]);
                            bool nearZ = refVec is { } rv && Math.Abs(rv.Z) > 0.99;
                            a[1] = R(nearZ ? EnsureDirection3D(0, 1, 0) : EnsureDirection3D(0, 0, 1));
                        }
                        else
                        {
                            var axVec = ReadDirectionTuple(w, dirLookupIndex, a[1]);
                            bool nearX = axVec is { } av && Math.Abs(av.X) > 0.99;
                            a[2] = R(nearX ? EnsureDirection3D(0, 1, 0) : EnsureDirection3D(1, 0, 0));
                        }
                        newArgs = string.Join(",", a);
                    }
                    break;

                case "IFCPROJECT":
                    if (a.Length == 9 && a[2] == "$")
                    {
                        a[2] = "''";
                        newArgs = string.Join(",", a);
                    }
                    break;

                case "IFCBUILDINGELEMENTPROXY":
                    if (a.Length == 9)
                    {
                        bool changed = false;
                        if (a[2] == "$") { a[2] = "''"; changed = true; }
                        if (a[8] == ".USERDEFINED." && a[4] == "$") { a[4] = "''"; changed = true; }
                        if (changed) newArgs = string.Join(",", a);
                    }
                    break;

                case "IFCWALLSTANDARDCASE":
                    if (a.Length == 8)
                    {
                        bool keepStandard = config.UseWallStandardCase && wallIdsWithLayerSetUsage.Contains(id);
                        if (!keepStandard)
                        {
                            newType = "IFCWALL";
                            downgradedWalls++;
                            newArgs = args + ",.NOTDEFINED.";
                        }
                        else
                        {
                            newArgs = args + ",.STANDARD.";
                        }
                    }
                    break;

                case "IFCOPENINGELEMENT":
                    if (a.Length == 8)
                    {
                        string predefined = a[4] switch
                        {
                            "'Recess'" => ".RECESS.",
                            "'Opening'" => ".OPENING.",
                            _ => ".NOTDEFINED.",
                        };
                        newArgs = $"{args},{predefined}";
                    }
                    break;

                case "IFCELECTRICDISTRIBUTIONPOINT":
                    newType = "IFCELECTRICDISTRIBUTIONBOARD";
                    if (a.Length == 8)
                        newArgs = args + ",.NOTDEFINED.";
                    break;

                case "IFCDISTRIBUTIONCONTROLELEMENT":
                    if (a.Length == 9)
                        newArgs = string.Join(",", a.Take(8));
                    break;

                case "IFCDISTRIBUTIONPORT":
                    if (a.Length == 8)
                        newArgs = args + ",$,$";
                    break;

                case "IFCDOOR":
                    if (a.Length == 10)
                        newArgs = args + ",.DOOR.,.NOTDEFINED.,$";
                    break;

                case "IFCWINDOW":
                    if (a.Length == 10)
                        newArgs = args + ",.WINDOW.,.NOTDEFINED.,$";
                    break;

                case "IFCSTAIRFLIGHT":
                    if (a.Length == 12)
                        newArgs = args + ",.NOTDEFINED.";
                    break;

                case "IFCGRID":
                    if (a.Length == 10)
                        newArgs = args + ",.NOTDEFINED.";
                    break;

                case "IFCZONE":
                    if (a.Length == 5)
                        newArgs = args + ",$";
                    break;

                case "IFCREINFORCINGMESH":
                    if (a.Length == 17)
                        newArgs = args + ",.NOTDEFINED.";
                    break;

                case "IFCDOORSTYLE":
                    if (a.Length == 12)
                    {
                        newType = "IFCDOORTYPE";
                        // IFCDOORSTYLE(8=OperationType,9=ConstructionType,10=ParameterTakesPrecedence,11=Sizeable)
                        // -> IFCDOORTYPE(8=ElementType,9=PredefinedType,10=OperationType,11=ParameterTakesPrecedence,12=UserDefinedOperationType)
                        newArgs = $"{string.Join(",", a.Take(8))},$,.DOOR.,{a[8]},{a[10]},$";
                    }
                    break;

                case "IFCWINDOWSTYLE":
                    if (a.Length == 12)
                    {
                        newType = "IFCWINDOWTYPE";
                        // IFCWINDOWSTYLE(8=ConstructionType,9=OperationType,10=ParameterTakesPrecedence,11=Sizeable)
                        // -> IFCWINDOWTYPE(8=ElementType,9=PredefinedType,10=PartitioningType,11=ParameterTakesPrecedence,12=UserDefinedPartitioningType)
                        newArgs = $"{string.Join(",", a.Take(8))},$,.WINDOW.,.NOTDEFINED.,{a[10]},$";
                    }
                    break;

                case "IFCSPACETYPE":
                    if (a.Length == 10)
                        newArgs = args + ",$";
                    break;

                case "IFCFURNITURETYPE":
                    if (a.Length == 10)
                    {
                        if (a[9] == "$") a[9] = ".NOTDEFINED.";
                        newArgs = $"{string.Join(",", a)},.NOTDEFINED.";
                    }
                    break;

                case "IFCSYSTEMFURNITUREELEMENTTYPE":
                    if (a.Length == 9)
                        newArgs = args + ",.NOTDEFINED.";
                    break;

                case "IFCDOORLININGPROPERTIES":
                    if (a.Length == 15)
                        newArgs = args + ",$,$";
                    break;

                case "IFCWINDOWLININGPROPERTIES":
                    if (a.Length == 13)
                        newArgs = args + ",$,$,$";
                    break;

                case "IFCCLASSIFICATION":
                    if (a.Length == 4)
                        newArgs = args + ",$,$,$";
                    break;

                case "IFCCLASSIFICATIONREFERENCE":
                    if (a.Length == 4)
                        newArgs = args + ",$,$";
                    break;

                case "IFCMATERIAL":
                    if (a.Length == 1)
                        newArgs = args + ",$,$";
                    break;

                case "IFCMATERIALLAYER":
                    if (a.Length == 3)
                        newArgs = args + ",$,$,$,$";
                    break;

                case "IFCMATERIALLAYERSET":
                    if (a.Length == 2)
                        newArgs = args + ",$";
                    break;

                case "IFCMATERIALLAYERSETUSAGE":
                    if (a.Length == 4)
                        newArgs = args + ",$";
                    break;

                case "IFCCURVESTYLE":
                    if (a.Length == 4)
                        newArgs = args + ",$";
                    break;

                case "IFCQUANTITYLENGTH":
                case "IFCQUANTITYAREA":
                case "IFCQUANTITYVOLUME":
                case "IFCQUANTITYCOUNT":
                case "IFCQUANTITYWEIGHT":
                case "IFCQUANTITYTIME":
                    if (a.Length == 4)
                        newArgs = args + ",$";
                    break;

                case "IFCELEMENTQUANTITY":
                    if (a.Length == 6 && a[2] == "'BaseQuantities'" && qtoNameByEntityId.TryGetValue(id, out string? qtoName)
                        && qtoName != "BaseQuantities")
                    {
                        a[2] = S(qtoName);
                        newArgs = string.Join(",", a);
                    }
                    break;

                default:
                    if (a.Length == 8 && Ifc4NineArgProductClassesNeedingPredefinedType.Contains(type))
                        newArgs = args + ",.NOTDEFINED.";
                    else if (Ifc4ProductPredefinedTypeClasses.Contains(type) && a.Length == 9 && a[8] == ".USERDEFINED." && a[4] == "$")
                    {
                        a[4] = "''";
                        newArgs = string.Join(",", a);
                    }
                    break;
            }

            // Enforce IfcTypeObject.UniquePropertySetNames on HasPropertySets (index 5).
            if ((newType == "IFCTYPEOBJECT" || newType == "IFCTYPEPRODUCT" || newType.EndsWith("TYPE", StringComparison.Ordinal))
                && psetNameById.Count > 0)
            {
                string[] typeArgs = newArgs != null ? SplitStepArguments(newArgs) : a;
                if (typeArgs.Length >= 6 && typeArgs[5].StartsWith('(') && typeArgs[5].EndsWith(')'))
                {
                    var psetTokens = SplitStepArguments(typeArgs[5][1..^1]);
                    if (psetTokens.Length > 1)
                    {
                        var seenNames = new HashSet<string>(StringComparer.Ordinal);
                        var dedupedTokens = new List<string>(psetTokens.Length);
                        bool deduped = false;
                        foreach (string tok in psetTokens)
                        {
                            if (TryEntityId(tok, out int psetId) && psetNameById.TryGetValue(psetId, out string? psetName)
                                && !seenNames.Add(psetName))
                            {
                                deduped = true;
                                duplicatePsetsToPrune.Add(psetId);
                                continue;
                            }
                            dedupedTokens.Add(tok);
                        }
                        if (deduped)
                        {
                            typeArgs[5] = StepWriter.List(dedupedTokens);
                            newArgs = string.Join(",", typeArgs);
                        }
                    }
                }
            }

            if (newType != type || newArgs != null)
            {
                if (newType != type)
                    w.ReplaceEntity(i, newType, newArgs ?? args);
                else
                    w.ReplaceArgs(i, newArgs!);
                updatedEntities++;
            }
        }

        if (duplicatePsetsToPrune.Count > 0)
            RemoveDetachedCurrent(w, duplicatePsetsToPrune);

        updatedEntities += FixIfc4GridAxesAnd2DRepresentations(w);

        int tessellatedItems = 0;
        if (config.PreferTessellation)
            tessellatedItems = ConvertBrepAndSurfaceModelsToTessellation(w);

        summary = new Ifc4ComplianceSummary(
            UpdatedEntities: updatedEntities,
            TessellatedItems: tessellatedItems,
            DowngradedWalls: downgradedWalls,
            SuppressedSpaceMaterials: suppressedSpaceMaterials,
            PrunedOrphanShapes: prunedOrphanShapes);

        return updatedEntities + tessellatedItems;
    }

    private static (double X, double Y, double Z)? ReadDirectionTuple(StepWriter w, Dictionary<int, int> indexById, string token)
    {
        if (!TryEntityId(token, out int dirId) || !indexById.TryGetValue(dirId, out int dirIdx))
            return null;
        var ent = w.EntityAt(dirIdx);
        if (ent.Type != "IFCDIRECTION") return null;
        string raw = ent.Args.Trim();
        if (!raw.StartsWith('(') || !raw.EndsWith(')')) return null;
        var parts = SplitStepArguments(raw[1..^1]);
        if (parts.Length < 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            return null;
        double z = parts.Length >= 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedZ)
            ? parsedZ : 0;
        return (x, y, z);
    }

    private static int FixIfc4GridAxesAnd2DRepresentations(StepWriter w)
    {
        var indexById = new Dictionary<int, int>(w.EntityCount);
        int originalCount = w.EntityCount;
        for (int i = 0; i < originalCount; i++)
            indexById[w.EntityAt(i).Id] = i;

        var point2DCache = new Dictionary<string, int>(StringComparer.Ordinal);
        var dir2DCache = new Dictionary<string, int>(StringComparer.Ordinal);
        var curve2DCache = new Dictionary<int, int>();
        var oldCurvesToPrune = new List<int>();
        int updated = 0;

        bool CurveHasNonZeroZ(int curveId, int depth = 0)
        {
            if (depth > 8 || !indexById.TryGetValue(curveId, out int idx)) return false;
            var ent = w.EntityAt(idx);
            string[] a = SplitStepArguments(ent.Args);
            switch (ent.Type)
            {
                case "IFCTRIMMEDCURVE" when a.Length >= 3:
                    if (TryEntityId(a[0], out int basis) && CurveHasNonZeroZ(basis, depth + 1)) return true;
                    foreach (string trimArg in new[] { a[1], a[2] })
                        if (trimArg.StartsWith('(') && trimArg.EndsWith(')'))
                            foreach (string tok in SplitStepArguments(trimArg[1..^1]))
                                if (TryEntityId(tok, out int ptId) && PointHasNonZeroZ(ptId)) return true;
                    return false;
                case "IFCLINE" when a.Length >= 2:
                    if (TryEntityId(a[0], out int linePt) && PointHasNonZeroZ(linePt)) return true;
                    if (TryEntityId(a[1], out int vecId) && indexById.TryGetValue(vecId, out int vIdx))
                    {
                        var vArgs = SplitStepArguments(w.EntityAt(vIdx).Args);
                        if (vArgs.Length >= 1 && TryEntityId(vArgs[0], out int dId) && DirHasNonZeroZ(dId)) return true;
                    }
                    return false;
                case "IFCPOLYLINE" when a.Length >= 1 && a[0].StartsWith('(') && a[0].EndsWith(')'):
                    foreach (string tok in SplitStepArguments(a[0][1..^1]))
                        if (TryEntityId(tok, out int ptId) && PointHasNonZeroZ(ptId)) return true;
                    return false;
                case "IFCCIRCLE" when a.Length >= 2 && TryEntityId(a[0], out int plId) && indexById.TryGetValue(plId, out int plIdx):
                    var plEnt = w.EntityAt(plIdx);
                    if (plEnt.Type == "IFCAXIS2PLACEMENT3D")
                    {
                        var pa = SplitStepArguments(plEnt.Args);
                        if (pa.Length >= 1 && TryEntityId(pa[0], out int cPt) && PointHasNonZeroZ(cPt)) return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        bool PointHasNonZeroZ(int ptId)
        {
            if (!indexById.TryGetValue(ptId, out int pIdx)) return false;
            var ent = w.EntityAt(pIdx);
            if (ent.Type != "IFCCARTESIANPOINT") return false;
            string raw = ent.Args.Trim();
            if (!raw.StartsWith('(') || !raw.EndsWith(')')) return false;
            var parts = SplitStepArguments(raw[1..^1]);
            return parts.Length >= 3
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)
                && Math.Abs(z) > 1e-6;
        }

        bool DirHasNonZeroZ(int dirId)
        {
            if (!indexById.TryGetValue(dirId, out int dIdx)) return false;
            var ent = w.EntityAt(dIdx);
            if (ent.Type != "IFCDIRECTION") return false;
            string raw = ent.Args.Trim();
            if (!raw.StartsWith('(') || !raw.EndsWith(')')) return false;
            var parts = SplitStepArguments(raw[1..^1]);
            return parts.Length >= 3
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double dz)
                && Math.Abs(dz) > 1e-6;
        }

        int? ClonePoint2D(int ptId)
        {
            if (!indexById.TryGetValue(ptId, out int pIdx)) return null;
            var ent = w.EntityAt(pIdx);
            if (ent.Type != "IFCCARTESIANPOINT") return null;
            string raw = ent.Args.Trim();
            if (!raw.StartsWith('(') || !raw.EndsWith(')')) return null;
            var parts = SplitStepArguments(raw[1..^1]);
            if (parts.Length < 2) return null;
            if (parts.Length == 2) return ptId;
            string args2 = $"({parts[0]},{parts[1]})";
            if (!point2DCache.TryGetValue(args2, out int newPt))
                point2DCache[args2] = newPt = w.Add("IFCCARTESIANPOINT", args2);
            return newPt;
        }

        int? CloneDir2D(int dirId)
        {
            if (!indexById.TryGetValue(dirId, out int dIdx)) return null;
            var ent = w.EntityAt(dIdx);
            if (ent.Type != "IFCDIRECTION") return null;
            string raw = ent.Args.Trim();
            if (!raw.StartsWith('(') || !raw.EndsWith(')')) return null;
            var parts = SplitStepArguments(raw[1..^1]);
            if (parts.Length < 2) return null;
            bool nonZeroXY = double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double dx)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double dy)
                && (Math.Abs(dx) > 1e-12 || Math.Abs(dy) > 1e-12);
            if (parts.Length == 2 && nonZeroXY) return dirId;
            string args2 = nonZeroXY ? $"({parts[0]},{parts[1]})" : "(1.,0.)";
            if (!dir2DCache.TryGetValue(args2, out int newDir))
                dir2DCache[args2] = newDir = w.Add("IFCDIRECTION", args2);
            return newDir;
        }

        int? CloneCurve2D(int curveId, int depth = 0)
        {
            if (depth > 8) return null;
            if (curve2DCache.TryGetValue(curveId, out int cached)) return cached;
            if (!indexById.TryGetValue(curveId, out int idx)) return null;
            var ent = w.EntityAt(idx);
            string[] a = SplitStepArguments(ent.Args);
            int? created = null;
            switch (ent.Type)
            {
                case "IFCTRIMMEDCURVE" when a.Length == 5:
                {
                    if (!TryEntityId(a[0], out int basisId) || CloneCurve2D(basisId, depth + 1) is not { } basis2)
                        return null;
                    string? RewriteTrimList(string listArg)
                    {
                        if (!listArg.StartsWith('(') || !listArg.EndsWith(')')) return null;
                        var items = SplitStepArguments(listArg[1..^1]);
                        for (int k = 0; k < items.Length; k++)
                        {
                            if (TryEntityId(items[k], out int tId))
                            {
                                if (ClonePoint2D(tId) is not { } pt2) return null;
                                items[k] = R(pt2);
                            }
                        }
                        return StepWriter.List(items);
                    }
                    if (RewriteTrimList(a[1]) is not { } t1 || RewriteTrimList(a[2]) is not { } t2) return null;
                    created = w.Add("IFCTRIMMEDCURVE", $"{R(basis2)},{t1},{t2},{a[3]},{a[4]}");
                    break;
                }
                case "IFCLINE" when a.Length == 2:
                {
                    if (!TryEntityId(a[0], out int pId) || ClonePoint2D(pId) is not { } p2) return null;
                    if (!TryEntityId(a[1], out int vId) || !indexById.TryGetValue(vId, out int vIdx)) return null;
                    var vEnt = w.EntityAt(vIdx);
                    if (vEnt.Type != "IFCVECTOR") return null;
                    var vArgs = SplitStepArguments(vEnt.Args);
                    if (vArgs.Length != 2 || !TryEntityId(vArgs[0], out int dId) || CloneDir2D(dId) is not { } d2) return null;
                    int vec2 = d2 == dId ? vId : w.Add("IFCVECTOR", $"{R(d2)},{vArgs[1]}");
                    if (p2 == pId && vec2 == vId) return curveId;
                    created = w.Add("IFCLINE", $"{R(p2)},{R(vec2)}");
                    break;
                }
                case "IFCPOLYLINE" when a.Length == 1 && a[0].StartsWith('(') && a[0].EndsWith(')'):
                {
                    var pts = SplitStepArguments(a[0][1..^1]);
                    var pts2 = new List<int>(pts.Length);
                    bool changed = false;
                    foreach (string ptTok in pts)
                    {
                        if (!TryEntityId(ptTok, out int pId) || ClonePoint2D(pId) is not { } p2) return null;
                        if (p2 != pId) changed = true;
                        pts2.Add(p2);
                    }
                    if (!changed) return curveId;
                    created = w.Add("IFCPOLYLINE", StepWriter.List(pts2.Select(R)));
                    break;
                }
                case "IFCCIRCLE" when a.Length == 2 && TryEntityId(a[0], out int plId) && indexById.TryGetValue(plId, out int plIdx):
                {
                    var plEnt = w.EntityAt(plIdx);
                    if (plEnt.Type == "IFCAXIS2PLACEMENT2D") return curveId;
                    if (plEnt.Type != "IFCAXIS2PLACEMENT3D") return null;
                    var pa = SplitStepArguments(plEnt.Args);
                    if (pa.Length < 1 || !TryEntityId(pa[0], out int locId) || ClonePoint2D(locId) is not { } loc2) return null;
                    string refDirArg = "$";
                    if (pa.Length >= 3 && TryEntityId(pa[2], out int rdId) && CloneDir2D(rdId) is { } rd2)
                        refDirArg = R(rd2);
                    int pl2 = w.Add("IFCAXIS2PLACEMENT2D", $"{R(loc2)},{refDirArg}");
                    created = w.Add("IFCCIRCLE", $"{R(pl2)},{a[1]}");
                    break;
                }
            }
            if (created.HasValue)
                curve2DCache[curveId] = created.Value;
            return created;
        }

        for (int i = 0; i < originalCount; i++)
        {
            var (_, type, args) = w.EntityAt(i);
            if (type == "IFCGRIDAXIS")
            {
                var a = SplitStepArguments(args);
                if (a.Length == 3 && TryEntityId(a[1], out int oldCurve) && CloneCurve2D(oldCurve) is { } newCurve && newCurve != oldCurve)
                {
                    a[1] = R(newCurve);
                    w.ReplaceArgs(i, string.Join(",", a));
                    oldCurvesToPrune.Add(oldCurve);
                    updated++;
                }
            }
            else if (type == "IFCSHAPEREPRESENTATION")
            {
                var a = SplitStepArguments(args);
                if (a.Length == 4 && a[2] == "'Curve2D'" && a[3].StartsWith('(') && a[3].EndsWith(')'))
                {
                    var items = SplitStepArguments(a[3][1..^1]);
                    bool use3D = false;
                    foreach (string itemTok in items)
                    {
                        if (TryEntityId(itemTok, out int itemId) && CurveHasNonZeroZ(itemId))
                        {
                            use3D = true;
                            break;
                        }
                    }
                    if (use3D)
                    {
                        a[2] = "'Curve3D'";
                        w.ReplaceArgs(i, string.Join(",", a));
                        updated++;
                    }
                    else
                    {
                        bool anyReplaced = false;
                        for (int k = 0; k < items.Length; k++)
                        {
                            if (TryEntityId(items[k], out int itemId) && CloneCurve2D(itemId) is { } curve2 && curve2 != itemId)
                            {
                                items[k] = R(curve2);
                                oldCurvesToPrune.Add(itemId);
                                anyReplaced = true;
                            }
                        }
                        if (anyReplaced)
                        {
                            a[3] = StepWriter.List(items);
                            w.ReplaceArgs(i, string.Join(",", a));
                            updated++;
                        }
                    }
                }
            }
        }

        if (oldCurvesToPrune.Count > 0)
            RemoveDetachedCurrent(w, oldCurvesToPrune);

        return updated;
    }

    internal readonly record struct Ifc4ComplianceSummary(
        int UpdatedEntities,
        int TessellatedItems,
        int DowngradedWalls,
        int SuppressedSpaceMaterials,
        int PrunedOrphanShapes);

    internal static string ConvertCartesianPointList3D(string args, IfcUnitFamily family)
    {
        if (family == IfcUnitFamily.Metre || args.Length < 4 || args[0] != '(' || args[^1] != ')')
            return args;
        string[] tuples = SplitStepArguments(args[1..^1]);
        for (int i = 0; i < tuples.Length; i++)
            tuples[i] = IfcLengthUnit.ToFamily("IFCCARTESIANPOINT", tuples[i], family);
        return $"({string.Join(",", tuples)})";
    }

    /// <summary>
    /// Replaces <c>Brep</c> (<c>IfcFacetedBrep</c>) and <c>SurfaceModel</c> (<c>IfcFaceBasedSurfaceModel</c> /
    /// <c>IfcShellBasedSurfaceModel</c>) representations with IFC4 <c>Tessellation</c> (<c>IfcTriangulatedFaceSet</c> for
    /// pure triangle meshes without voids, or <c>IfcPolygonalFaceSet</c> for general polygonal faces and faces with voids),
    /// preserving surface styles and cleaning detached BRep entities.
    /// </summary>
    internal static int ConvertBrepAndSurfaceModelsToTessellation(StepWriter w)
    {
        var indexById = new Dictionary<int, int>(w.EntityCount);
        var styledItemsByTarget = new Dictionary<int, List<int>>();
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (id, type, args) = w.EntityAt(i);
            indexById[id] = i;
            if (type == "IFCSTYLEDITEM")
            {
                var sa = SplitStepArguments(args);
                if (sa.Length >= 1 && TryEntityId(sa[0], out int target))
                {
                    if (!styledItemsByTarget.TryGetValue(target, out var list))
                        styledItemsByTarget[target] = list = [];
                    list.Add(i);
                }
            }
        }

        var detachedRoots = new List<int>();
        var repsToRewrite = new List<(int RepIndex, List<int> ItemIds)>();
        int convertedCount = 0;
        int originalCount = w.EntityCount;

        for (int i = 0; i < originalCount; i++)
        {
            var (_, type, args) = w.EntityAt(i);
            if (type != "IFCSHAPEREPRESENTATION") continue;
            var repArgs = SplitStepArguments(args);
            if (repArgs.Length != 4 || repArgs[2] is not ("'Brep'" or "'SurfaceModel'")
                || !repArgs[3].StartsWith('(') || !repArgs[3].EndsWith(')'))
                continue;
            var itemTokens = SplitStepArguments(repArgs[3][1..^1]);
            var newItems = new List<int>(itemTokens.Length);
            bool allConverted = itemTokens.Length > 0;

            foreach (string itemToken in itemTokens)
            {
                if (!TryEntityId(itemToken, out int oldItemId) || !indexById.TryGetValue(oldItemId, out int itemIndex))
                {
                    allConverted = false;
                    break;
                }
                var item = w.EntityAt(itemIndex);
                if (!TryBuildTessellatedItem(w, indexById, item.Type, item.Args, out int newItemId))
                {
                    allConverted = false;
                    break;
                }
                newItems.Add(newItemId);
                detachedRoots.Add(oldItemId);
                if (styledItemsByTarget.TryGetValue(oldItemId, out var styleIndices))
                {
                    foreach (int sIdx in styleIndices)
                    {
                        var sa = SplitStepArguments(w.EntityAt(sIdx).Args);
                        sa[0] = StepWriter.Ref(newItemId);
                        w.ReplaceArgs(sIdx, string.Join(",", sa));
                    }
                }
                convertedCount++;
            }

            if (allConverted && newItems.Count > 0)
            {
                repArgs[2] = "'Tessellation'";
                repArgs[3] = StepWriter.List(newItems.Select(StepWriter.Ref));
                w.ReplaceArgs(i, string.Join(",", repArgs));
            }
        }

        if (detachedRoots.Count > 0)
            RemoveDetachedCurrent(w, detachedRoots);

        return convertedCount;
    }

    private static bool TryBuildTessellatedItem(
        StepWriter w,
        IReadOnlyDictionary<int, int> indexById,
        string itemType,
        string itemArgs,
        out int newItemId)
    {
        newItemId = 0;
        var shellRefs = new List<(int ShellId, bool Closed)>();

        if (itemType == "IFCFACETEDBREP")
        {
            if (!TryEntityId(itemArgs.Trim(), out int shellId)) return false;
            shellRefs.Add((shellId, true));
        }
        else if (itemType is "IFCFACEBASEDSURFACEMODEL" or "IFCSHELLBASEDSURFACEMODEL")
        {
            var sa = SplitStepArguments(itemArgs);
            if (sa.Length != 1 || !sa[0].StartsWith('(') || !sa[0].EndsWith(')')) return false;
            foreach (string tok in SplitStepArguments(sa[0][1..^1]))
            {
                if (!TryEntityId(tok, out int sId) || !indexById.TryGetValue(sId, out int sIdx)) return false;
                bool closed = w.EntityAt(sIdx).Type == "IFCCLOSEDSHELL";
                shellRefs.Add((sId, closed));
            }
        }
        else
        {
            return false;
        }

        var coordStrings = new List<string>();
        var coordIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var faces = new List<(List<int> Outer, List<List<int>> Inners)>();
        bool allClosed = shellRefs.Count > 0 && shellRefs.All(s => s.Closed);
        bool allPureTriangles = true;

        int AddPoint(string pointRef)
        {
            if (!TryEntityId(pointRef, out int pId) || !indexById.TryGetValue(pId, out int pIdx)) return 0;
            var pEnt = w.EntityAt(pIdx);
            if (pEnt.Type != "IFCCARTESIANPOINT") return 0;
            string coords = pEnt.Args.Trim();
            if (!coords.StartsWith('(') || !coords.EndsWith(')')) return 0;
            if (!coordIndexByKey.TryGetValue(coords, out int idx))
            {
                idx = coordStrings.Count + 1;
                coordIndexByKey[coords] = idx;
                coordStrings.Add(coords);
            }
            return idx;
        }

        List<int>? ReadLoop(string loopRef, bool reverse)
        {
            if (!TryEntityId(loopRef, out int lId) || !indexById.TryGetValue(lId, out int lIdx)) return null;
            var lEnt = w.EntityAt(lIdx);
            if (lEnt.Type != "IFCPOLYLOOP") return null;
            var lArgs = SplitStepArguments(lEnt.Args);
            if (lArgs.Length != 1 || !lArgs[0].StartsWith('(') || !lArgs[0].EndsWith(')')) return null;
            var pts = new List<int>();
            foreach (string ptTok in SplitStepArguments(lArgs[0][1..^1]))
            {
                int pIdx = AddPoint(ptTok);
                if (pIdx <= 0) return null;
                if (pts.Count == 0 || pts[^1] != pIdx)
                    pts.Add(pIdx);
            }
            if (pts.Count > 1 && pts[0] == pts[^1])
                pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) return null;
            if (reverse)
                pts.Reverse();
            return pts;
        }

        foreach (var (shellId, _) in shellRefs)
        {
            if (!indexById.TryGetValue(shellId, out int shellIndex)) return false;
            var shEnt = w.EntityAt(shellIndex);
            var shArgs = SplitStepArguments(shEnt.Args);
            if (shArgs.Length != 1 || !shArgs[0].StartsWith('(') || !shArgs[0].EndsWith(')')) return false;

            foreach (string faceTok in SplitStepArguments(shArgs[0][1..^1]))
            {
                if (!TryEntityId(faceTok, out int fId) || !indexById.TryGetValue(fId, out int fIdx)) return false;
                var fEnt = w.EntityAt(fIdx);
                if (fEnt.Type != "IFCFACE") return false;
                var fArgs = SplitStepArguments(fEnt.Args);
                if (fArgs.Length != 1 || !fArgs[0].StartsWith('(') || !fArgs[0].EndsWith(')')) return false;

                List<int>? outer = null;
                var inners = new List<List<int>>();
                foreach (string bTok in SplitStepArguments(fArgs[0][1..^1]))
                {
                    if (!TryEntityId(bTok, out int bId) || !indexById.TryGetValue(bId, out int bIdx)) return false;
                    var bEnt = w.EntityAt(bIdx);
                    if (bEnt.Type is not ("IFCFACEOUTERBOUND" or "IFCFACEBOUND")) return false;
                    var bArgs = SplitStepArguments(bEnt.Args);
                    if (bArgs.Length != 2) return false;
                    bool reverse = bArgs[1] == ".F.";
                    var loop = ReadLoop(bArgs[0], reverse);
                    if (loop is null) continue;
                    if (bEnt.Type == "IFCFACEOUTERBOUND" && outer is null)
                        outer = loop;
                    else
                        inners.Add(loop);
                }
                if (outer is null) continue;
                if (outer.Count != 3 || inners.Count > 0)
                    allPureTriangles = false;
                faces.Add((outer, inners));
            }
        }

        if (coordStrings.Count < 3 || faces.Count == 0)
            return false;

        int pointListId = w.Add("IFCCARTESIANPOINTLIST3D", $"({string.Join(",", coordStrings)})");
        string closedArg = allClosed ? ".T." : ".F.";

        if (allPureTriangles)
        {
            var triStrings = faces.Select(f => $"({f.Outer[0].ToString(CultureInfo.InvariantCulture)},{f.Outer[1].ToString(CultureInfo.InvariantCulture)},{f.Outer[2].ToString(CultureInfo.InvariantCulture)})");
            newItemId = w.Add("IFCTRIANGULATEDFACESET", $"{StepWriter.Ref(pointListId)},$,{closedArg},({string.Join(",", triStrings)}),$");
            return true;
        }

        var polyFaceIds = new List<int>(faces.Count);
        foreach (var (outer, inners) in faces)
        {
            string outerStr = $"({string.Join(",", outer.Select(idx => idx.ToString(CultureInfo.InvariantCulture)))})";
            if (inners.Count == 0)
            {
                polyFaceIds.Add(w.Add("IFCINDEXEDPOLYGONALFACE", outerStr));
            }
            else
            {
                string innerStr = $"({string.Join(",", inners.Select(ring => $"({string.Join(",", ring.Select(idx => idx.ToString(CultureInfo.InvariantCulture)))})"))})";
                polyFaceIds.Add(w.Add("IFCINDEXEDPOLYGONALFACEWITHVOIDS", $"{outerStr},{innerStr}"));
            }
        }
        newItemId = w.Add("IFCPOLYGONALFACESET", $"{StepWriter.Ref(pointListId)},{closedArg},({string.Join(",", polyFaceIds.Select(StepWriter.Ref))}),$");
        return true;
    }
}
