using System.Globalization;
using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>Property-set rules of the current profile exporter. They apply only when
/// <see cref="IfcExporter.Profile"/> is <see cref="IfcProfile.Current"/>; the legacy profile paths keep
/// their own code. A type object receives the property sets of every description whose entity list
/// contains the instance or the type class, evaluated with the type element standing for both the
/// element and its type; the sets follow the order of the descriptions' initialisation.</summary>
public partial class IfcExporter
{
    private const int AllModelManufacturerParamId = -1010108;
    private const int FireRatingParamId = -1001206;
    private const int LightingFixtureWattageParamId = -1010500;

    /// <summary>Order in which the descriptions are registered. The type writer
    /// creates the sets in this order, so HasPropertySets lists them in it.</summary>
    private static readonly string[] PropertySetInitOrderCurrent =
    [
        "Pset_BeamCommon", "Pset_BuildingElementProxyCommon", "Pset_ColumnCommon",
        "Pset_ConcreteElementGeneral", "Pset_CoveringCommon", "Pset_CurtainWallCommon",
        "Pset_DistributionFlowElementCommon", "Pset_DoorCommon", "Pset_LightFixtureTypeCommon",
        "Pset_ManufacturerTypeInformation", "Pset_MemberCommon", "Pset_PlateCommon",
        "Pset_QuantityTakeOff", "Pset_RailingCommon", "Pset_ReinforcementBarPitchOfBeam",
        "Pset_ReinforcementBarPitchOfSlab", "Pset_ReinforcementBarPitchOfWall", "Pset_SlabCommon",
        "Pset_StairFlightCommon", "Pset_WallCommon", "Pset_WindowCommon",
    ];

    /// <summary>The element class lists of Pset_ConcreteElementGeneral that can occur as types.</summary>
    private static bool ConcreteElementGeneralAppliesToTypeCurrent(string typeClass) => typeClass is
        "IFCBEAMTYPE" or "IFCBUILDINGELEMENTPROXYTYPE" or "IFCCOLUMNTYPE" or "IFCCOVERINGTYPE"
        or "IFCCURTAINWALLTYPE" or "IFCDOORSTYLE" or "IFCMEMBERTYPE" or "IFCRAILINGTYPE"
        or "IFCSLABTYPE" or "IFCSTAIRFLIGHTTYPE" or "IFCWALLTYPE";

    /// <summary>Whether property sets are created for a type object of this class. The types of stairs,
    /// MEP elements, furniture and spaces never go through the common type registration,
    /// so they carry none; a distribution element
    /// type receives sets only when its instances are flow terminals (cable trays and pipes have
    /// types of their own classes).</summary>
    internal static bool TypeReceivesPropertySetsCurrent(string typeClass, string instanceIfcType, Element typeElem) =>
        typeClass switch
        {
            "IFCMEMBERTYPE" => typeElem.CategoryId is CurtainWallMullionCategoryId or StructuralFramingCategoryId,
            "IFCBEAMTYPE" or "IFCBUILDINGELEMENTPROXYTYPE" or "IFCCOLUMNTYPE" or "IFCCOVERINGTYPE"
                or "IFCCURTAINWALLTYPE" or "IFCDOORSTYLE" or "IFCPLATETYPE" or "IFCRAILINGTYPE"
                or "IFCSLABTYPE" or "IFCSTAIRFLIGHTTYPE" or "IFCWALLTYPE" or "IFCWINDOWSTYLE"
                or "IFCLIGHTFIXTURETYPE" => true,
            "IFCDISTRIBUTIONELEMENTTYPE" => instanceIfcType == "IFCFLOWTERMINAL",
            _ => false,
        };

    /// <summary>Whether the element's record names the parameter or stores it in its own parameter value
    /// sets (exactly the stored parameters of an element are listed).</summary>
    private static bool ElementReferencesParameter(Element element, int parameterId) =>
        element.References?.Contains(parameterId) == true
        || element.NativeStoredParameterIds?.Contains(parameterId) == true;

    /// <summary>Text of an own String parameter, or null when the parameter is absent or empty.</summary>
    private static string? NonEmptyStringParameter(Element element, int parameterId) =>
        element.Parameters.FirstOrDefault(parameter => parameter.Id == parameterId)?.Value is string { Length: > 0 } text
            ? text : null;

    /// <summary>The built-in text parameter an IFC property reads from the instance, then from its type.
    /// The record is taken to hold the parameter when its
    /// reference list names it or its decoded value is not empty: the parser fills missing built-in
    /// strings with "", which confirms nothing. A held parameter without a decoded value is empty text.</summary>
    internal static string? ResolveHeldTextParameterCurrent(Element? instance, Element? type, int parameterId)
    {
        foreach (Element? owner in new[] { instance, type })
            if (owner != null && NonEmptyStringParameter(owner, parameterId) is { } text)
                return text;
        // The reference list of the family instance itself is not the type's; only the type record counts here.
        if (type != null && ElementReferencesParameter(type, parameterId))
            return "";
        // An instance that stores the parameter in its own value sets holds it, empty or not.
        if (instance?.NativeStoredParameterIds?.Contains(parameterId) == true)
            return "";
        return null;
    }

    /// <summary>Reference value of a type element: the own Reference parameter, else the type name
    /// (without the family prefix the legacy profile type name carries). Null when a parameter blocks the validation or
    /// the type has no name.</summary>
    internal static string? ResolveTypeReferenceValueCurrent(Element typeElem)
    {
        ExactIfcStringParameterResult explicitReference =
            ExactIfcStringParameterResolver.ResolveReference(typeElem, typeElem);
        if (explicitReference.State == ExactIfcStringParameterState.Blocked) return null;
        if (explicitReference.State == ExactIfcStringParameterState.Value) return explicitReference.Value;
        // RoofAttributes owns the SymbolInfo name; its general name-table entry can be an alias.
        if (!typeElem.IsBlockOnly && typeElem.CategoryId == -2000035
            && typeElem.SerializedSystemTypeClassId == 3452
            && typeElem.ValidatedSystemFamilyName is { Length: > 0 } roofFamily
            && typeElem.ValidatedHostTypeLayerSetName is { Length: > 0 } roofName
            && roofName.StartsWith(roofFamily + ":", StringComparison.Ordinal)
            && roofName.Length > roofFamily.Length + 1)
            return roofName[(roofFamily.Length + 1)..];
        string? name = TypeObjectNameParts(typeElem, true).Full;
        if (string.IsNullOrEmpty(name)) return null;
        int separator = name.IndexOf(':');
        string typeName = separator >= 0 ? name[(separator + 1)..] : name;
        return typeName.Length == 0 ? null : typeName;
    }

    /// <summary>IsExternal of a type element: the lookup goes through the type of the
    /// element, and a type has none; only the roof and mass exterior wall categories answer true. A
    /// named IsExternal parameter still wins; a parameter that cannot be read exactly yields no value.</summary>
    internal static bool? ResolveTypeIsExternalCurrent(Element typeElem)
    {
        ExactIsExternalParameterResult named = ResolveExactIsExternalParameter(typeElem, typeElem);
        if (named.State == ExactIsExternalParameterState.Value) return named.Value != 0;
        if (named.State == ExactIsExternalParameterState.Blocked) return null;
        return typeElem.CategoryId is RoofCategoryId or MassExteriorWallCategoryId;
    }

    /// <summary>Builds the HasPropertySets of a type object the way the current profile does (see the class remarks).
    /// Returns "$" for the types that carry none.</summary>
    private static string BuildTypeHasPropertySetsCurrent(
        StepWriter w, int ownerHistId, string typeClass, string instanceIfcType, Element typeElem,
        Dictionary<(string PropertyName, string ValueExpr), int> pool, string typeIdentityKey,
        IReadOnlyList<int>? leadingDefinitions = null, IReadOnlyDictionary<int, Element>? elementsById = null)
    {
        if (!TypeReceivesPropertySetsCurrent(typeClass, instanceIfcType, typeElem)) return "$";

        string? reference = ResolveTypeReferenceValueCurrent(typeElem);
        int? Identifier() => reference == null ? null
            : GetOrAddPropertySingleValue(w, pool, "Reference", $"IFCIDENTIFIER({S(reference)})");
        int? Label() => reference == null ? null
            : GetOrAddPropertySingleValue(w, pool, "Reference", $"IFCLABEL({S(reference)})");
        int? IsExternal() => ResolveTypeIsExternalCurrent(typeElem) is bool external
            ? GetOrAddPropertySingleValue(w, pool, "IsExternal", external ? "IFCBOOLEAN(.T.)" : "IFCBOOLEAN(.F.)")
            : null;
        int? LoadBearing()
        {
            // An own LoadBearing parameter, else the structural categories.
            var loadBearing = ResolveExactNamedIntParameter(typeElem, "LoadBearing");
            if (loadBearing.State == ExactIsExternalParameterState.Absent)
                loadBearing = ResolveExactNamedIntParameter(typeElem, "LoadBearing[Type]");
            bool structuralCategory = typeElem.CategoryId is StructuralColumnCategoryId or StructuralFramingCategoryId
                or (int)BuiltIns.BuiltInCategory.OST_StructuralFramingSystem
                or (int)BuiltIns.BuiltInCategory.OST_StructuralTruss;
            if (loadBearing.State == ExactIsExternalParameterState.Blocked
                || loadBearing.State == ExactIsExternalParameterState.Absent && !structuralCategory)
                return null;
            bool bearing = loadBearing.State == ExactIsExternalParameterState.Absent || loadBearing.Value != 0;
            return GetOrAddPropertySingleValue(w, pool, "LoadBearing", bearing ? "IFCBOOLEAN(.T.)" : "IFCBOOLEAN(.F.)");
        }
        string? fireRating = ResolveHeldTextParameterCurrent(null, typeElem, FireRatingParamId);
        int? FireRating() => fireRating == null ? null
            : GetOrAddPropertySingleValue(w, pool, "FireRating", $"IFCLABEL({S(fireRating)})");
        int? ThermalTransmittance() =>
            typeClass is "IFCWINDOWSTYLE" or "IFCDOORSTYLE"
            && ResolveOpeningThermalTransmittanceCurrent(typeElem, null) is double transmittance
                ? w.Add("IFCPROPERTYSINGLEVALUE",
                    $"{S("ThermalTransmittance")},$,IFCTHERMALTRANSMITTANCEMEASURE({D17(transmittance)}),$")
                : null;
        int? HostThermalTransmittance() =>
            typeClass is "IFCWALLTYPE" or "IFCSLABTYPE" && elementsById != null
            && ResolveHostTypeThermalTransmittanceCurrent(typeElem, elementsById) is double hostTransmittance
                ? GetOrAddPropertySingleValue(w, pool, "ThermalTransmittance", ThermalTransmittanceLiteralCurrent(hostTransmittance))
                : null;
        int? TotalThickness() =>
            typeClass == "IFCCOVERINGTYPE" && typeElem.CategoryId == -2000038
            && typeElem.ValidatedCeilingTypeThicknessFeet is double thicknessFeet && thicknessFeet > 0
                ? w.Add("IFCPROPERTYSINGLEVALUE",
                    $"{S("TotalThickness")},$,IFCPOSITIVELENGTHMEASURE({D17(thicknessFeet * 304.8)}),$")
                : null;
        int? NosingLength() => typeElem.SerializedStairRunTypeNosingFeet is double nosingFeet
            ? GetOrAddPropertySingleValue(w, pool, "NosingLength", $"IFCLENGTHMEASURE({D17(nosingFeet * 304.8)})")
            : null;
        int? TotalWattage()
        {
            // The built-in parameter is text; an empty one reads as zero watts.
            if (!ElementReferencesParameter(typeElem, LightingFixtureWattageParamId)) return null;
            return NonEmptyStringParameter(typeElem, LightingFixtureWattageParamId) != null ? null
                : GetOrAddPropertySingleValue(w, pool, "TotalWattage", "IFCPOWERMEASURE(0.)");
        }
        string? manufacturer = ResolveHeldTextParameterCurrent(null, typeElem, AllModelManufacturerParamId);
        int? Manufacturer() => manufacturer == null ? null
            : GetOrAddPropertySingleValue(w, pool, "Manufacturer", $"IFCLABEL({S(manufacturer)})");

        var sets = new List<(string Name, List<int?> Properties, int Copies)>();
        void Add(string name, int copies, params int?[] properties) => sets.Add((name, [.. properties], copies));

        switch (typeClass)
        {
            case "IFCBEAMTYPE":
                Add("Pset_BeamCommon", 1, Identifier(), IsExternal(), LoadBearing(), FireRating());
                // The description registered for the column pitch is named after the beam and applies
                // to beams, so a beam type receives the set twice.
                Add("Pset_ReinforcementBarPitchOfBeam", 2, Label());
                break;
            case "IFCBUILDINGELEMENTPROXYTYPE":
                Add("Pset_BuildingElementProxyCommon", 1, Identifier());
                break;
            case "IFCCOLUMNTYPE":
                Add("Pset_ColumnCommon", 1, Identifier(), IsExternal(), LoadBearing(), FireRating());
                break;
            case "IFCCOVERINGTYPE":
                Add("Pset_CoveringCommon", 1, Identifier(), TotalThickness(), FireRating());
                break;
            case "IFCCURTAINWALLTYPE":
                Add("Pset_CurtainWallCommon", 1, Identifier(), IsExternal(), FireRating());
                break;
            case "IFCDOORSTYLE":
                Add("Pset_DistributionFlowElementCommon", 1, Identifier());
                Add("Pset_DoorCommon", 1, Identifier(), IsExternal(), ThermalTransmittance(), FireRating());
                break;
            case "IFCLIGHTFIXTURETYPE":
                Add("Pset_LightFixtureTypeCommon", 1, TotalWattage());
                break;
            case "IFCMEMBERTYPE":
                Add("Pset_MemberCommon", 1, Identifier(), IsExternal(),
                    typeElem.CategoryId == StructuralFramingCategoryId ? LoadBearing() : null, FireRating());
                break;
            case "IFCPLATETYPE":
                Add("Pset_PlateCommon", 1, Identifier(), IsExternal(), FireRating());
                break;
            case "IFCRAILINGTYPE":
                Add("Pset_RailingCommon", 1, Identifier(), IsExternal());
                break;
            case "IFCSLABTYPE":
                Add("Pset_ReinforcementBarPitchOfSlab", 1, Label());
                Add("Pset_SlabCommon", 1, Identifier(), HostThermalTransmittance(), IsExternal(), FireRating());
                break;
            case "IFCSTAIRFLIGHTTYPE":
                Add("Pset_StairFlightCommon", 1, Identifier(), NosingLength());
                break;
            case "IFCWALLTYPE":
                Add("Pset_ReinforcementBarPitchOfWall", 1, Label());
                Add("Pset_WallCommon", 1, Identifier(), HostThermalTransmittance(), IsExternal(), FireRating());
                break;
            case "IFCWINDOWSTYLE":
                Add("Pset_WindowCommon", 1, Identifier(), IsExternal(), ThermalTransmittance(), FireRating());
                break;
        }
        if (typeClass != "IFCLIGHTFIXTURETYPE")
        {
            if (ConcreteElementGeneralAppliesToTypeCurrent(typeClass))
                Add("Pset_ConcreteElementGeneral", 1, FireRating());
            Add("Pset_ManufacturerTypeInformation", 1, Manufacturer());
            Add("Pset_QuantityTakeOff", 1, Identifier());
        }

        var psetIds = new List<string>();
        if (leadingDefinitions != null) psetIds.AddRange(leadingDefinitions.Select(R));
        foreach (var (name, properties, copies) in sets.OrderBy(set => Array.IndexOf(PropertySetInitOrderCurrent, set.Name)))
        {
            var present = properties.Where(property => property.HasValue).Select(property => R(property!.Value)).ToList();
            if (present.Count == 0) continue;
            for (int copy = 0; copy < copies; copy++)
            {
                string seed = copy == 0 ? $"type-{name}:{typeIdentityKey}" : $"type-{name}:{typeIdentityKey}:{copy}";
                psetIds.Add(R(w.Add("IFCPROPERTYSET",
                    $"{S(IfcGuid.FromName(seed))},{R(ownerHistId)},{S(name)},$,{StepWriter.List(present)}")));
            }
        }
        return psetIds.Count == 0 ? "$" : StepWriter.List(psetIds);
    }

    /// <summary>Sub-elements of the instance that made a door or window style; a window uses
    /// the same numbers.</summary>
    private const int StyleLiningSubElementIndex = 1, StylePanelSubElementIndex = 2;

    /// <summary>The definitions the current profile puts in front of the property sets of a door or window style,
    /// made from the instance that created the style: an IfcDoorLiningProperties and one IfcDoorPanelProperties (door), an
    /// IfcWindowLiningProperties (window), named after the instance as its product and, for the panel, with ":1" appended;
    /// their GlobalIds are the sub-elements 1 and 2 of the instance. Every dimension is set only when a family parameter
    /// named "LiningDepth", "PanelDepth1" and so on exists, which is not read here, and a style without an operation has
    /// the panel operation and position NOTDEFINED. Window panels need a PanelOperation or PanelPosition parameter and are
    /// not written.</summary>
    internal static List<int> BuildOpeningStyleDefinitionsCurrent(
        StepWriter w, int ownerHistId, string typeClass, Element creator, string nameLiteral, ISet<Guid> usedExportIds)
    {
        string GlobalId(int subElement) => ResolveExactElementGlobalId(creator, usedExportIds, subElement, handleBased: true)
            ?? IfcGuid.FromName($"{typeClass}-definition-{subElement}:{creator.Id}");
        bool door = typeClass == "IFCDOORSTYLE";
        var definitions = new List<int>
        {
            w.Add(door ? "IFCDOORLININGPROPERTIES" : "IFCWINDOWLININGPROPERTIES",
                $"{S(GlobalId(StyleLiningSubElementIndex))},{R(ownerHistId)},{nameLiteral},$,"
                + string.Join(",", Enumerable.Repeat("$", door ? 11 : 9))),
        };
        if (door)
            definitions.Add(w.Add("IFCDOORPANELPROPERTIES",
                $"{S(GlobalId(StylePanelSubElementIndex))},{R(ownerHistId)},{nameLiteral[..^1]}:1',$,$,.NOTDEFINED.,$,.NOTDEFINED.,$"));
        return definitions;
    }

    /// <summary>The attribute number <paramref name="attributeIndex"/> exactly as the writer holds it (a quoted name
    /// literal stays quoted and escaped) for the product of each of the given elements; an element whose product is
    /// not written or has no such attribute is left out.</summary>
    private static Dictionary<int, string> ReadProductAttributes(
        StepWriter w, IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> exportedProducts,
        IEnumerable<int> elementIds, int attributeIndex)
    {
        var elementByEntity = new Dictionary<int, int>();
        foreach (int elementId in elementIds)
            if (exportedProducts.TryGetValue(elementId, out var product)) elementByEntity[product.entityId] = elementId;
        var attributes = new Dictionary<int, string>();
        if (elementByEntity.Count == 0) return attributes;
        for (int i = 0; i < w.EntityCount && attributes.Count < elementByEntity.Count; i++)
        {
            var (id, _, args) = w.EntityAt(i);
            if (!elementByEntity.TryGetValue(id, out int elementId)) continue;
            string[] all = SplitStepArguments(args);
            if (all.Length > attributeIndex) attributes[elementId] = all[attributeIndex];
        }
        return attributes;
    }

    /// <summary>ThermalTransmittance of a door or window: the stored ANALYTICAL_HEAT_TRANSFER_COEFFICIENT of the
    /// instance or its type, written unchanged. The parser's wall-type default is marked approximate and never
    /// counts; a type without the parameter has none.</summary>
    internal static double? ResolveOpeningThermalTransmittanceCurrent(Element opening, Element? type)
    {
        const int heatTransferCoefficientParamId = -1005430;
        if (opening.CategoryId is not (-2000014 or -2000023)) return null;
        foreach (Element? owner in new[] { opening, type })
        {
            Parameter? parameter = owner?.Parameters.FirstOrDefault(p => p.Id == heatTransferCoefficientParamId);
            if (parameter is { IsApproximate: false, Value: double value } && double.IsFinite(value) && value > 0)
                return value;
        }
        return null;
    }

    /// <summary>Pset_BeamCommon.Span in millimetres. The parameter mapped to the property (INSTANCE_LENGTH_PARAM) is read from the
    /// occurrence, then from its type, before any computed span is consulted: a beam carries the stored length, while
    /// its extrusion can be shorter. A length the parser derived itself (marked approximate)
    /// is not a stored one.</summary>
    internal static double? ResolveBeamSpanMillimetresCurrent(Element beam, Element? type, bool driveCurveFirst = false)
    {
        // The stored length of a brace is rounded; the drive curve is exact.
        if (driveCurveFirst && beam.CurveDriverLineLength is double exactFeet && double.IsFinite(exactFeet) && exactFeet > 0)
            return exactFeet * 304.8;
        foreach (Element? owner in new[] { beam, type })
            if (owner?.Parameters.FirstOrDefault(parameter => parameter.Id == InstanceLengthParamId) is
                { IsApproximate: false, Value: double lengthFeet } && double.IsFinite(lengthFeet) && lengthFeet > 0)
                return lengthFeet * 304.8;
        // A beam without a stored length spans its drive curve.
        return beam.CurveDriverLineLength is double driveFeet && double.IsFinite(driveFeet) && driveFeet > 0 ? driveFeet * 304.8 : null;
    }

    /// <summary>Pset_BeamCommon.Slope in degrees: the angle between the drive curve of the beam and the horizontal plane, never negative.</summary>
    internal static double? ResolveBeamSlopeDegreesCurrent(Element beam)
    {
        if (beam.CurveDriverLineLength is not double length || !(length > 0) || beam.CurveDriverLineZ is not { } z) return null;
        double ratio = Math.Min(1.0, Math.Abs(z.EndZ - z.StartZ) / length);
        return ratio < 1e-9 ? 0.0 : Math.Asin(ratio) * 180.0 / Math.PI;
    }

    /// <summary>Pset_CoveringCommon.TotalThickness of a ceiling: CEILING_THICKNESS, which a compound ceiling type
    /// computes from its layers.</summary>
    internal static double? ResolveCoveringTotalThicknessMillimetresCurrent(Element covering, Element? type) =>
        covering.CategoryId == -2000038 && type is { CategoryId: -2000038 }
        && type.ValidatedCeilingTypeThicknessFeet is double feet && feet > 0
            ? feet * 304.8 : null;

    /// <summary>External flag of the current profile: the FUNCTION_PARAM of the element's type decides
    /// when the type has one (anything but 0 is external); otherwise a roof or mass exterior wall is external,
    /// a mass interior wall is not, a family instance takes the result of its host, and the rest is internal.
    /// The legacy profile exporter caches the answer per type; the current profile does not. A function parameter that cannot be
    /// read exactly yields false, as the legacy profile does.</summary>
    internal static bool PureIsElementExternalCurrent(Element element, IReadOnlyDictionary<int, Element> elementsById)
    {
        var visited = new HashSet<int>();
        for (Element? current = element; current != null && visited.Add(current.Id);)
        {
            if (current.FamilyTypeId > 0 && current.FamilyTypeId != current.Id
                && elementsById.TryGetValue(current.FamilyTypeId, out Element? type))
            {
                ExactIsExternalParameterResult function = ResolveExactFunctionParameter(
                    type, includeFamilySymbolFunction: true);
                // The native function of a basic wall type is the FUNCTION_PARAM value.
                if (function.State == ExactIsExternalParameterState.Absent
                    && type.SerializedBasicWallTypeFunction is >= 0 and <= 5)
                    function = new(ExactIsExternalParameterState.Value, type.SerializedBasicWallTypeFunction.Value);
                if (function.State == ExactIsExternalParameterState.Value) return function.Value != 0;
                if (function.State == ExactIsExternalParameterState.Blocked) return false;
            }
            if (current.CategoryId is RoofCategoryId or MassExteriorWallCategoryId) return true;
            if (current.CategoryId == MassInteriorWallCategoryId) return false;
            // The raw host of a free family is hidden, except a SketchPlane.
            if (current.RuleClassName == "FamilyInstance" && current.NativeMasterSymbolId is int masterId
                && elementsById.TryGetValue(masterId, out var master) && master.RuleClassName == "FamilySymbol"
                && master.SymbolFamilyId is int familyId && elementsById.TryGetValue(familyId, out var family)
                && family.RuleClassName == "Family" && family.FamilyAllowsRawHost == false
                && current.SerializedHostId is int rawHostId && elementsById.TryGetValue(rawHostId, out var rawHost)
                && rawHost.RuleClassName is { } rawHostClass && rawHostClass != "SketchPlane")
                return false;
            current = current.SerializedHostId is int hostId && elementsById.TryGetValue(hostId, out Element? host)
                ? host : null;
        }
        return false;
    }

    /// <summary>RVTIO_PSET_DIAG: one CSV line per door or window with the inputs of its IsExternal (stored symbol
    /// function, decoded FUNCTION_PARAM and named IsExternal candidates of the occurrence and its type, the host).</summary>
    private static void WriteOpeningIsExternalDiagCurrent(
        string path, Element opening, string ifcType, IReadOnlyDictionary<int, Element> elementsById, bool? result)
    {
        Element? type = opening.FamilyTypeId > 0 && elementsById.TryGetValue(opening.FamilyTypeId, out Element? found)
            ? found : null;
        static string Candidates(Element? owner, Func<Parameter, bool> pick) => owner == null ? "-"
            : string.Join(';', owner.Parameters.Where(pick).Select(parameter =>
                $"{parameter.Id}={parameter.Value}{(parameter.IsApproximate ? "~" : "")}:{parameter.StorageType}"));
        Element? host = opening.SerializedHostId is int hostId && elementsById.TryGetValue(hostId, out Element? hostElement)
            ? hostElement : null;
        File.AppendAllText(path, string.Join(',', opening.Id, ifcType, opening.FamilyTypeId, type != null ? 1 : 0,
            type?.SerializedFamilySymbolFunction?.ToString() ?? "-",
            Candidates(type, parameter => parameter.Id == FunctionParamId),
            type?.NativeStoredParameterIds?.Contains(FunctionParamId) == true ? 1 : 0,
            Candidates(opening, parameter => ParameterNameEquals(parameter, "IsExternal")),
            Candidates(type, parameter => ParameterNameEquals(parameter, "IsExternal")),
            opening.SerializedHostId?.ToString() ?? "-", host?.CategoryId.ToString() ?? "-",
            result?.ToString() ?? "-") + "\n");
    }

    /// <summary>The IsExternal property of an occurrence: an own or type IsExternal parameter, else
    /// <see cref="PureIsElementExternalCurrent"/>. Null when the named parameter cannot be read exactly.</summary>
    private static bool? OccurrenceIsExternalCurrent(Element occurrence, IReadOnlyDictionary<int, Element> elementsById)
    {
        Element? type = occurrence.FamilyTypeId > 0 && elementsById.TryGetValue(occurrence.FamilyTypeId, out Element? found)
            ? found : null;
        ExactIsExternalParameterResult named = ResolveExactIsExternalParameter(occurrence, type);
        if (named.State == ExactIsExternalParameterState.Value) return named.Value != 0;
        if (named.State == ExactIsExternalParameterState.Blocked) return null;
        return PureIsElementExternalCurrent(occurrence, elementsById);
    }

    /// <summary>IsExternal of the occurrences that carry a Common set with it (members, plates, columns,
    /// slabs); an unreadable named parameter reads as false, the portable fallback of the legacy profile replay.</summary>
    internal static IReadOnlyDictionary<int, bool> ResolveOccurrenceIsExternalValuesCurrent(
        IReadOnlyDictionary<int, Element> elementsById, IReadOnlyCollection<int> occurrenceIds)
    {
        var values = new Dictionary<int, bool>();
        foreach (int id in occurrenceIds)
            if (elementsById.TryGetValue(id, out Element? occurrence))
                values[id] = OccurrenceIsExternalCurrent(occurrence, elementsById) ?? false;
        return values;
    }

    /// <summary>The Common set that carries FireRating, per product class (the sets whose description lists it).</summary>
    private static readonly Dictionary<string, string> FireRatingCommonSetCurrent = new(StringComparer.Ordinal)
    {
        ["IFCBEAM"] = "Pset_BeamCommon", ["IFCCOLUMN"] = "Pset_ColumnCommon", ["IFCCOVERING"] = "Pset_CoveringCommon",
        ["IFCCURTAINWALL"] = "Pset_CurtainWallCommon", ["IFCDOOR"] = "Pset_DoorCommon", ["IFCMEMBER"] = "Pset_MemberCommon",
        ["IFCPLATE"] = "Pset_PlateCommon", ["IFCRAMP"] = "Pset_RampCommon", ["IFCROOF"] = "Pset_RoofCommon",
        ["IFCSLAB"] = "Pset_SlabCommon", ["IFCSTAIR"] = "Pset_StairCommon", ["IFCWALL"] = "Pset_WallCommon",
        ["IFCWALLSTANDARDCASE"] = "Pset_WallCommon", ["IFCWINDOW"] = "Pset_WindowCommon",
    };

    /// <summary>The product classes of Pset_ConcreteElementGeneral that this exporter emits.</summary>
    private static readonly HashSet<string> ConcreteElementGeneralProductsCurrent = new(StringComparer.Ordinal)
    {
        "IFCBEAM", "IFCBUILDINGELEMENTPROXY", "IFCCOLUMN", "IFCCOVERING", "IFCCURTAINWALL", "IFCDOOR", "IFCMEMBER",
        "IFCRAILING", "IFCROOF", "IFCSLAB", "IFCSTAIR", "IFCSTAIRFLIGHT", "IFCWALL", "IFCWALLSTANDARDCASE",
    };

    /// <summary>FireRating: a product whose instance or type holds the Fire Rating parameter (empty or not)
    /// receives the property in the Common set of its description and a
    /// Pset_ConcreteElementGeneral of its own. The Common sets are written class by class earlier in the export,
    /// so the property is appended to the written set of the product afterwards.</summary>
    private void ApplyFireRatingPropertySetsCurrent(
        StepWriter w, IReadOnlyDictionary<int, Element> elementsById,
        IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> products, int ownerHistId,
        Dictionary<(string PropertyName, string ValueExpr), int> pool)
    {
        var targets = new List<(int EntityId, int ElementId, string IfcType, string Text)>();
        var diagTypes = new HashSet<int>();
        foreach (var (elementId, product) in products)
        {
            if (!ConcreteElementGeneralProductsCurrent.Contains(product.ifcType) && !FireRatingCommonSetCurrent.ContainsKey(product.ifcType)
                || !elementsById.TryGetValue(elementId, out Element? element)) continue;
            Element? type = element.FamilyTypeId > 0 && elementsById.TryGetValue(element.FamilyTypeId, out Element? found)
                ? found : null;
            // RVTIO_PSET_DIAG: why a product holds FireRating or not (once per type id).
            if (Environment.GetEnvironmentVariable("RVTIO_PSET_DIAG") is { Length: > 0 } diagPath
                && diagTypes.Add(element.FamilyTypeId))
                File.AppendAllText(diagPath, string.Join(',', "FireRating", element.FamilyTypeId, product.ifcType,
                    type?.References?.Contains(FireRatingParamId) == true ? 1 : 0,
                    type?.NativeStoredParameterIds?.Contains(FireRatingParamId) == true ? 1 : 0,
                    NonEmptyStringParameter(type ?? element, FireRatingParamId) != null ? 1 : 0,
                    type?.NativeStoredParameterIds?.Count ?? 0) + "\n");
            if (ResolveHeldTextParameterCurrent(element, type, FireRatingParamId) is { } text)
                targets.Add((product.entityId, elementId, product.ifcType, text));
        }
        if (targets.Count == 0) return;

        var wanted = targets.Select(target => target.EntityId).ToHashSet();
        var psetIndexById = new Dictionary<int, int>();
        var psetsByObject = new Dictionary<int, List<int>>();
        int count = w.EntityCount;
        for (int i = 0; i < count; i++)
        {
            var (id, type, args) = w.EntityAt(i);
            if (type == "IFCPROPERTYSET") psetIndexById[id] = i;
            else if (type == "IFCRELDEFINESBYPROPERTIES" && SingleObjectPropertyRelation.Match(args) is { Success: true } relation
                && int.Parse(relation.Groups[1].ValueSpan, CultureInfo.InvariantCulture) is int objectId && wanted.Contains(objectId))
            {
                if (!psetsByObject.TryGetValue(objectId, out var list)) psetsByObject[objectId] = list = [];
                list.Add(int.Parse(relation.Groups[2].ValueSpan, CultureInfo.InvariantCulture));
            }
        }

        foreach (var (entityId, elementId, ifcType, text) in targets)
        {
            int propertyId = GetOrAddPropertySingleValue(w, pool, "FireRating", $"IFCLABEL({S(text)})");
            if (FireRatingCommonSetCurrent.TryGetValue(ifcType, out string? commonName)
                && psetsByObject.TryGetValue(entityId, out var psetIds))
                foreach (int psetId in psetIds)
                {
                    if (!psetIndexById.TryGetValue(psetId, out int index)) continue;
                    string args = w.EntityAt(index).Args;
                    if (PropertySetName.Match(args) is not { Success: true } name
                        || StepWriter.Unescape(name.Groups[1].Value) != commonName
                        || !args.EndsWith(')')) continue;
                    w.ReplaceArgs(index, $"{args[..^1]},#{propertyId.ToString(CultureInfo.InvariantCulture)})");
                    break;
                }
            if (!ConcreteElementGeneralProductsCurrent.Contains(ifcType)) continue;
            int concreteId = w.Add("IFCPROPERTYSET",
                $"{S(IfcGuid.FromName($"concrete-element-general-pset:{elementId}"))},{R(ownerHistId)},{S("Pset_ConcreteElementGeneral")},$,{StepWriter.List(R(propertyId))}");
            w.Add("IFCRELDEFINESBYPROPERTIES",
                $"{S(IfcGuid.FromName($"concrete-element-general-rel:{elementId}"))},{R(ownerHistId)},$,$,{StepWriter.List(R(entityId))},{R(concreteId)}");
        }
    }

    /// <summary>The element classes the Pset_DistributionFlowElementCommon description lists, as far as this
    /// exporter emits them: the door (the description names IfcDoor) and the flow elements, fittings
    /// included, which the legacy profile group leaves out.</summary>
    internal static bool ReceivesDistributionFlowElementCommonPropertySetCurrent(string ifcType) =>
        ifcType is "IFCDOOR" or "IFCFLOWFITTING" or "IFCFLOWSEGMENT" or "IFCFLOWTERMINAL"
            or "IFCDISTRIBUTIONFLOWELEMENT";

    /// <summary>The property sets of the current profile that need no product-specific validation beyond the parameters of the
    /// product and its type: Pset_ManufacturerTypeInformation (ALL_MODEL_MANUFACTURER of the instance, then of
    /// its type) and, on a flow segment, the Length sets of the duct and pipe segment descriptions
    /// (the Length parameter, CURVE_ELEM_LENGTH, in feet).</summary>
    private void ExportInstancePropertySetsCurrent(
        StepWriter w, Element elem, Element? elementType, string ifcType, int elemEntityId, int ownerHistId,
        Dictionary<(string PropertyName, string ValueExpr), int> pool, Element? curveType = null)
    {
        void Attach(string psetName, string seed, int propertyId)
        {
            int psetId = w.Add("IFCPROPERTYSET",
                $"{S(IfcGuid.FromName($"{seed}-pset:{elem.Id}"))},{R(ownerHistId)},{S(psetName)},$,{StepWriter.List(R(propertyId))}");
            w.Add("IFCRELDEFINESBYPROPERTIES",
                $"{S(IfcGuid.FromName($"{seed}-rel:{elem.Id}"))},{R(ownerHistId)},$,$,{StepWriter.List(R(elemEntityId))},{R(psetId)}");
        }

        // A pipe, duct or cable tray takes the Manufacturer from its own MEP curve type: the instance values the record
        // scan attaches to some of them are not parameters of the pipe.
        if ((curveType != null ? ResolveHeldTextParameterCurrent(null, curveType, AllModelManufacturerParamId)
                : ResolveHeldTextParameterCurrent(elem, elementType, AllModelManufacturerParamId)) is { } manufacturer)
            Attach("Pset_ManufacturerTypeInformation", "manufacturer",
                GetOrAddPropertySingleValue(w, pool, "Manufacturer", $"IFCLABEL({S(manufacturer)})"));

        if (ifcType == "IFCFLOWSEGMENT"
            && elem.Parameters.FirstOrDefault(parameter => parameter.Id == CurveElemLengthParamId && parameter.CarriesRevitValue())?.Value
                is double lengthFeet && lengthFeet > 0)
        {
            int lengthId = GetOrAddPropertySingleValue(
                w, pool, "Length", $"IFCPOSITIVELENGTHMEASURE({D17(lengthFeet * 304.8)})");
            Attach("Pset_FlowSegmentDuctSegment", "flow-segment-duct", lengthId);
            Attach("Pset_FlowSegmentPipeSegment", "flow-segment-pipe", lengthId);
        }
    }

    /// <summary>Pset_ManufacturerTypeInformation of an opening a family instance cuts (the opening is handed
    /// to the product wrapper with the cutter, which supplies the sets): the Manufacturer the cutter
    /// holds, instance first, then its type - the same property its own product receives. Cut openings whose cutter product
    /// carries the set carry it too, and those without it do not. The set is named after the opening's own GlobalId, which the
    /// exact-id pass later replaces.</summary>
    internal static void ExportOpeningManufacturerPropertySetCurrent(
        StepWriter w, string openingGlobalId, int openingEntityId, int ownerHistId, Element cutter,
        IReadOnlyDictionary<int, Element> elementsById, Dictionary<(string PropertyName, string ValueExpr), int> pool)
    {
        Element? cutterType = cutter.FamilyTypeId > 0 && elementsById.TryGetValue(cutter.FamilyTypeId, out Element? found)
            ? found : null;
        if (ResolveHeldTextParameterCurrent(cutter, cutterType, AllModelManufacturerParamId) is not { } manufacturer) return;
        int propertyId = GetOrAddPropertySingleValue(w, pool, "Manufacturer", $"IFCLABEL({S(manufacturer)})");
        int psetId = w.Add("IFCPROPERTYSET",
            $"{S(IfcGuid.FromName($"opening-manufacturer-pset:{openingGlobalId}"))},{R(ownerHistId)},{S("Pset_ManufacturerTypeInformation")},$,{StepWriter.List(R(propertyId))}");
        w.Add("IFCRELDEFINESBYPROPERTIES",
            $"{S(IfcGuid.FromName($"opening-manufacturer-rel:{openingGlobalId}"))},{R(ownerHistId)},$,$,{StepWriter.List(R(openingEntityId))},{R(psetId)}");
    }
}
