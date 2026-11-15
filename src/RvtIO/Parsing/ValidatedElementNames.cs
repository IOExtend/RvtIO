using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>
/// The <c>Name</c> of the JSON dump: the Revit API <c>Element.Name</c> where the file confirms it, null elsewhere.
/// <para>
/// <see cref="Element.Name"/> itself is a best guess: <c>ElementBuilder.ResolveElementName</c> falls back to the native
/// 0xFFFF-block string and then to the first string scanned near the element, which can be a parameter value,
/// a font name or the name of another element. The IFC exporter and the API layer read that value as a fallback, so it
/// stays; this class derives the JSON name only from these sources:
/// </para>
/// <list type="bullet">
/// <item>a type (or any named element) takes the name of its own root: the SymbolInfo name of a unique type root
/// (<see cref="Element.ValidatedMepTypeName"/>, wall, host-object, roof and railing, stair component, cornice and
/// family-symbol types), the name table entry, the <c>DatumPlane.m_text</c> of a level, <c>Material.m_name</c> of a
/// material;</item>
/// <item>an occurrence takes the name of its type, as the IFC exporter names the type part of a product
/// (<see cref="IfcExporter.ResolveProductFamilyAndTypeName"/>): the type of an MEP curve, the name table chain of a
/// family instance whose two type links agree, the exact system type link of a host object, a wall's own type name,
/// and the class validations of <see cref="IfcExporter.ResolveValidatedProductTypeName"/>;</item>
/// <item>an element with none of these has no validated name and the JSON omits it.</item>
/// </list>
/// The native block string is not a source, and neither is the floor and ceiling Reference terminal string (0xFFFF077B,
/// which can be the assembly code): floors and ceilings take their type name from the host type root.
/// </summary>
internal static class ValidatedElementNames
{
    /// <summary>A validated name and the source that confirmed it.</summary>
    internal readonly record struct Validation(string Name, string Source);

    /// <summary>
    /// Replaces <see cref="Element.Name"/> of every element by its validated name (null when there is none) and returns the
    /// number of elements that keep a name. Destructive: call it only on the way to the JSON dump, never before the IFC
    /// export or the API, which read <see cref="Element.Name"/> as a fallback.
    /// </summary>
    internal static int Apply(RevitFileInfo info, ParsingState state)
    {
        var byId = new Dictionary<int, Element>();
        var counts = new Dictionary<int, int>();
        foreach (Element element in info.Elements)
        {
            byId.TryAdd(element.Id, element);
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        }
        // Resolve everything before assigning: no resolution may read another element's replaced Name.
        var names = new string?[info.Elements.Count];
        for (int index = 0; index < names.Length; index++)
            names[index] = Resolve(info.Elements[index], byId, counts, state)?.Name;
        int named = 0;
        for (int index = 0; index < names.Length; index++)
        {
            info.Elements[index].Name = names[index];
            if (names[index] != null) named++;
        }
        return named;
    }

    /// <summary>The first validated name of the element, in the order of <see cref="Candidates"/>; null when unvalidated.</summary>
    internal static Validation? Resolve(
        Element element, IReadOnlyDictionary<int, Element> byId, IReadOnlyDictionary<int, int> counts, ParsingState state)
    {
        foreach (Validation validation in Candidates(element, byId, counts, state))
            return validation;
        return null;
    }

    /// <summary>The validated names of the element in priority order. A named element (a type, level or material) reports
    /// its own name and nothing else; only an occurrence with no name of its own reports the name of its type, as the
    /// Revit API does. A type element (it owns a Symbol-subclass root, <see cref="ParsingState.SystemTypeRoots"/>) has no
    /// type: the link it carries points at another symbol, so without a validated name of its own it has none.
    /// Two different names in the result would contradict each other.</summary>
    internal static IEnumerable<Validation> Candidates(
        Element element, IReadOnlyDictionary<int, Element> byId, IReadOnlyDictionary<int, int> counts, ParsingState state)
    {
        if (element.Id < 0 || element.Id == 0 && state.WideSchema == null && !state.ElemClassIdx.ContainsKey(0)) yield break;
        bool named = false;
        foreach (Validation validation in OwnNames(element, state))
        {
            named = true;
            yield return validation;
        }
        if (named || element.Id == 0 || state.SystemTypeRoots.ContainsKey((uint)element.Id)) yield break;
        foreach (Validation validation in TypeNames(element, byId, counts, state))
            yield return validation;
    }

    /// <summary>The element is itself the named one. The own SymbolInfo name of a unique type root comes before the name
    /// table: the table holds another string for the pipe, flex pipe and insulation types.</summary>
    private static IEnumerable<Validation> OwnNames(Element element, ParsingState state)
    {
        uint id = (uint)element.Id;
        if (state.WideNames.TryGetValue(id, out string? wideName) && Named(wideName)) yield return new(wideName, "wide-own-name");
        if (Named(element.ValidatedMepTypeName)) yield return new(element.ValidatedMepTypeName!, "mep-type");
        if (Named(element.ValidatedDuctTypeName)) yield return new(element.ValidatedDuctTypeName!, "duct-type");
        if (Named(element.ValidatedStairComponentTypeName)) yield return new(element.ValidatedStairComponentTypeName!, "stair-component-type");
        if (Named(element.ValidatedCorniceTypeName)) yield return new(element.ValidatedCorniceTypeName!, "cornice-type");
        if (Named(element.SerializedBasicWallTypeName)) yield return new(element.SerializedBasicWallTypeName!, "basic-wall-type");
        if (Named(element.SerializedCurtainWallTypeName)) yield return new(element.SerializedCurtainWallTypeName!, "curtain-wall-type");
        if (HostTypeName(element, state) is { } hostName) yield return new(hostName, "host-type");
        if (state.RoofOrRailingTypeNames.TryGetValue(id, out var roofOrRailing)
            && roofOrRailing is { BlockCount: 1, ClassId: 908 or 3452 or 3705 } && Named(roofOrRailing.Name))
            yield return new(roofOrRailing.Name!, "roof-railing-type");
        if (state.FamilySymbolNames.TryGetValue(id, out var symbol) && symbol.BlockCount == 1 && Named(symbol.Name))
            yield return new(symbol.Name!, "family-symbol");
        if (state.ElementNameTable.TryGetValue(id, out string? tableName) && Named(tableName))
            yield return new(tableName, "name-table");
        if (state.NativeParams.TryGetValue(id, out var native) && native.Fields.ContainsKey("__isLevel")
            && native.StringFields.TryGetValue("m_text", out string? levelName) && Named(levelName))
            yield return new(levelName, "level");
        if (Named(element.SerializedMaterialName)) yield return new(element.SerializedMaterialName!, "material");
    }

    /// <summary>An occurrence: the name of its type, through the links the IFC exporter confirms.</summary>
    private static IEnumerable<Validation> TypeNames(
        Element element, IReadOnlyDictionary<int, Element> byId, IReadOnlyDictionary<int, int> counts, ParsingState state)
    {
        if (IfcExporter.ResolveMepCurveTypeName(element, byId) is { } mepName && Named(mepName))
            yield return new(mepName, "mep-curve-type");
        // Family instance: both type links agree, the type is unique and its name table entry has the parent family chain
        // (IfcExporter.ResolveProductFamilyAndTypeName).
        if (element.FamilyTypeId > 0 && element.TypeId == element.FamilyTypeId && element.FamilyTypeId != element.Id
            && counts.GetValueOrDefault(element.FamilyTypeId) == 1
            && byId.TryGetValue(element.FamilyTypeId, out Element? chainType) && Named(chainType.ValidatedIfcTypeOwnName))
            yield return new(chainType.ValidatedIfcTypeOwnName!, "name-table-chain");
        // A host object (wall, floor, ceiling, roof, building pad) reaches the own root of its type through the exact
        // own link on which both unique roots and their classes agree.
        if (element.SerializedSystemTypeId is int systemTypeId && counts.GetValueOrDefault(systemTypeId) == 1
            && byId.TryGetValue(systemTypeId, out Element? systemType) && HostTypeName(systemType, state) is { } linkedName)
            yield return new(linkedName, "host-type-link");
        if (element.CategoryId == -2000011 && IfcExporter.ResolveValidatedWallTypeName(
                element, byId.GetValueOrDefault(element.FamilyTypeId)) is { } wallName && Named(wallName))
            yield return new(wallName, "wall-type");
        // The class validations of the exporter. Not for floors and ceilings: their validation is the terminal string of the
        // type block, a Reference value that can be the assembly code.
        if (element.CategoryId is not (-2000032 or -2000038)
            && IfcExporter.MapCategoryToIfcType(element.CategoryId) is { } ifcType
            && IfcExporter.ResolveValidatedProductTypeName(element, ifcType, byId, currentProfile: true) is { } classValidation && Named(classValidation))
            yield return new(classValidation, "class-validation");
    }

    /// <summary>The SymbolInfo name of the host object type's own unique root, when its class and category agree with the
    /// occurrences (<see cref="ElementBuilder.ApplyHostTypeCompoundStructures"/> sets <see cref="Element.SerializedHostTypeLayers"/>
    /// only then).</summary>
    private static string? HostTypeName(Element type, ParsingState state) =>
        type.SerializedHostTypeLayers != null
        && state.HostTypeCompoundStructures.TryGetValue((uint)type.Id, out var validation) && Named(validation.TypeName)
            ? validation.TypeName : null;

    private static bool Named(string? name) => !string.IsNullOrWhiteSpace(name);
}
