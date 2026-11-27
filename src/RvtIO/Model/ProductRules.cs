using RvtIO.BuiltIns;

namespace RvtIO.Model;

/// <summary>
/// The rules that decide whether a decoded element is a placed product of the model (and not a definition, a void, an annotation or
/// a record of another phase). The IFC export and <see cref="Meshes.ElementMeshes"/> apply them.
/// </summary>
internal static class ProductRules
{
    private const int PhaseCategoryId = -2000112;        // OST_Phases

    /// <summary>Bit 0x200 of the GInfo flags of a serialized Face. Every element has at least one
    /// Face with it, except those whose Faces are all display (0x00200000) or auxiliary (0x04000000) surfaces, which never
    /// reach the body.</summary>
    internal const uint FaceFillFlag = 0x00000200;

    /// <summary>Whether every decoded Face of the element lacks <see cref="FaceFillFlag"/>. The current IFC profile writes no such instance:
    /// an instance whose own geometry is null is not exported (for example the floor-hosted family
    /// "Assignment - Rectangular opening (floor)"). Faces whose flags are not known (placed from a nested symbol) count as filled.</summary>
    internal static bool HasOnlyUnfilledFaces(Element element) =>
        element.Geometry?.Faces is { Count: > 0 } faces
        && faces.All(face => face.GInfoFlags is uint flags && (flags & FaceFillFlag) == 0);

    /// <summary>Whether the element names its created phase (PHASE_CREATED) in its own record graph. Preview and type records
    /// inherit the parameter during parameter propagation without referencing that phase.</summary>
    internal static bool HasProjectPhaseReference(Element e)
    {
        int? phase = e.Parameters.FirstOrDefault(p => p.Id == -1012100)?.Value is int n ? n : null; // PHASE_CREATED
        // Id 0 is a phase of Revit 2023 and later (the first project phase, a row of its
        // element table); the earlier layouts number phases from 1.
        return phase is > 0 && (e.References?.Contains(phase.Value) == true || e.SerializedCreatedPhaseId == phase.Value)
            || phase == 0 && e.SerializedCreatedPhaseId == 0;
    }

    /// <summary>The elements that other elements name as their type (ELEM_TYPE_PARAM, ELEM_FAMILY_AND_TYPE_PARAM).</summary>
    internal static HashSet<int> TypeElementIds(IEnumerable<Element> elements)
    {
        var typeElementIds = new HashSet<int>();
        foreach (var e in elements)
            foreach (var p in e.Parameters)
                if (p.Id is -1002050 or -1002052 && p.Value is int typeId && typeId > 0)
                    typeElementIds.Add(typeId);
        return typeElementIds;
    }

    /// <summary>A family instance placed by its FamilyInstance transform whose category is unknown or generic.</summary>
    internal static bool IsTransformOnlyFamily(Element elem) =>
        elem.Transform != null && elem.TypeId > 0 && elem.CategoryId is null or -2000080 or -2001000 or -2001100;

    /// <summary>
    /// Returns true if the Category string indicates a non-physical element (annotation, link,
    /// symbol, etc).
    /// </summary>
    internal static bool IsNonPhysicalCategory(string? category)
    {
        if (string.IsNullOrEmpty(category)) return false; // null category handled by the IFC class mapping
        // Annotations, symbols, links, views, grids, etc.
        return category.Contains("Annotation", StringComparison.OrdinalIgnoreCase)
            || category.Contains("RvtLink", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Tag", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Schedule", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Legend", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Titleblock", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Detail", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Camera", StringComparison.OrdinalIgnoreCase)
            || category.Contains("Analytical", StringComparison.OrdinalIgnoreCase)
            // Spatial/compound sub-elements are represented by their parent IFC entity, not as
            // independent building-element proxies.
            || category.Contains("OST_Rooms", StringComparison.OrdinalIgnoreCase)
            || category.Contains("OST_MEPSpaces", StringComparison.OrdinalIgnoreCase)
            || category.Contains("OST_RailingTopRail", StringComparison.OrdinalIgnoreCase)
            || category.Contains("OST_RailingHandRail", StringComparison.OrdinalIgnoreCase)
            || category.Contains("OST_StairsTrisers", StringComparison.OrdinalIgnoreCase)
            || category.Contains("OST_StairsRailingBaluster", StringComparison.OrdinalIgnoreCase)
            || category.Equals("OST_Parts", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A rectangular wall opening: a void, not a product of its own.</summary>
    internal static bool IsWallRectangularOpening(Element element) =>
        element.Category?.Contains("OST_SWallRectOpening", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Topography stored with a model-group definition rather than placed on the site.</summary>
    internal static bool IsGroupDefinitionTopography(Element element, IReadOnlyDictionary<int, Element> elementsById) =>
        element.CategoryId == (int)BuiltInCategory.OST_Topography
        && element.References?.Any(id => elementsById.TryGetValue(id, out var referenced)
            && referenced.CategoryId == (int)BuiltInCategory.OST_IOSModelGroups) == true;

    /// <summary>A StackedWall parent, whose member walls are the products; not its source record or children.</summary>
    internal static bool IsValidatedStackedWallContainer(
        Element element, IReadOnlyDictionary<int, Element> elementsById) =>
        element.CategoryId == -2000011 && element.FamilyTypeId > 0
        && elementsById.TryGetValue(element.FamilyTypeId, out var type)
        && type.Id == element.FamilyTypeId && type.CategoryId == -2003500
        && type.IsSerializedStackedWallType;

    /// <summary>A wall whose own geometry carries no decoded faces or BRep solids, no location curve, and only an unresolved or unexpanded symbol placement.</summary>
    internal static bool IsGeometrylessUnresolvedWall(Element element) =>
        element.Location is not { CurveType: "Line" or "Arc" }
        && element.Geometry is { Faces.Count: 0, BrepSolids: not { Count: > 0 } } geometry
        && (geometry.UnresolvedSymbol || geometry.Instances is { Count: > 0 });

    /// <summary>The last project phase of the authored phase order that is a phase element of the document, null when there is none.</summary>
    internal static int? LastProjectPhase(RevitFileInfo info)
    {
        if (info.SerializedProjectPhaseIds is not { Length: > 0 } order) return null;
        // a 64-bit-id file can have a phase whose category is not resolved, so the class counts too
        var phases = info.Elements.Where(element => (element.CategoryId == PhaseCategoryId || element.RootClassName == "ProjectPhase")
                && !(info.ElemTableIsExact && element.IsDiscovered))
            .Select(element => element.Id).ToHashSet();
        for (int index = order.Length - 1; index >= 0; index--)
            if (phases.Contains(order[index])) return order[index];
        return null;
    }

    /// <summary>The filter of the elements that do not exist in the export phase, null when there is none to apply. The export phase is
    /// <paramref name="exportPhaseId"/>, else, with <paramref name="lastPhaseByDefault"/>, the last project phase. With a known phase
    /// order it is the general rule (<see cref="ExportPhaseFilter"/>). Without it, the default export phase (the last one, no explicit
    /// <paramref name="exportPhaseId"/>) still needs only the set of phases: an element demolished in any phase of the project is
    /// demolished at or before the last one. That set is the ProjectPhase elements of the document; a document without any is not
    /// filtered.</summary>
    internal static ExportPhaseFilter? BuildExportPhaseFilter(RevitFileInfo info, int? exportPhaseId, bool lastPhaseByDefault)
    {
        if (info.SerializedProjectPhaseIds is { } order)
            return (exportPhaseId ?? (lastPhaseByDefault ? LastProjectPhase(info) : null)) is int phase
                ? ExportPhaseFilter.For(order, phase) : null;
        if (exportPhaseId != null || !lastPhaseByDefault) return null;
        var phases = info.Elements.Where(element => element.RootClassName == "ProjectPhase"
                && !(info.ElemTableIsExact && element.IsDiscovered))
            .Select(element => element.Id).ToArray();
        return phases.Length == 0 ? null : ExportPhaseFilter.OfLastPhase(phases);
    }
}
