using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    /// <summary>
    /// Records the number of solids and the closed-polyhedron quantities of every element at
    /// Coarse, Medium and Fine.
    /// </summary>
    internal static void ApplyShellQuantities(RevitFileInfo info, ParsingState state)
    {
        foreach (var element in info.Elements)
        {
            var shells = new SolidQuantities.Shell?[3];
            var counts = new int[3];
            for (int slot = 0; slot < 3; slot++)
            {
                var geometry = slot == 2 ? element.Geometry : GeometryAtDetailLevel(element, state, slot + 1);
                shells[slot] = SolidQuantities.OfAllSolids(geometry);
                counts[slot] = geometry?.BrepSolids?.Count ?? 0;
            }
            element.LevelShells = shells;
            element.LevelSolidCounts = counts;
        }
    }

    /// <summary>Geometry for measuring solids at Coarse (1) or Medium (2), falling back to Fine when the level has no alternative.</summary>
    internal static GeometryData? GeometryAtDetailLevel(Element element, ParsingState state, int level)
    {
        if (element.Id <= 0) return element.Geometry;
        uint id = (uint)element.Id;
        state.GrepBlocks.TryGetValue(id, out var grep);
        state.FamilyInstanceTransforms.TryGetValue(id, out var transformOnly);
        var visited = new HashSet<uint>();
        bool has = HasAlternative(grep, state, visited, level)
            || (transformOnly != null && state.GrepBlocks.TryGetValue((uint)Math.Max(transformOnly.SymbolId, 0), out var symbol)
                && HasAlternative(symbol, state, visited, level));
        if (!has) return element.Geometry;

        return BuildQuantityGeometry(element.Id, grep, state, level == 1 ? GeometryDetail.Coarse : GeometryDetail.Medium);
    }

    private static GRepData? SelectDetailGeometry(GRepData? block, GeometryDetail detail) => detail switch
    {
        GeometryDetail.CoarseQuantities => block?.CoarseQuantities ?? block?.Coarse ?? block,
        GeometryDetail.Coarse => block?.Coarse ?? block,
        GeometryDetail.Medium => block?.Medium ?? block,
        _ => block,
    };

    private static GeometryData? BuildQuantityGeometry(int elementId, GRepData? grep, ParsingState state,
        GeometryDetail detail)
    {
        var alternative = new Element { Id = elementId };
        if (SelectDetailGeometry(grep, detail) is { } block)
        {
            alternative.Geometry = BuildGeometryData(block, state.SeparateFacesWithDistinctOpenings, includeEdgesAndInstances: false);
            ExpandInstanceGeometry(alternative, block, state, detail);
        }
        ExpandTransformOnlyInstanceGeometry(alternative, (uint)elementId, state, detail);
        return alternative.Geometry;
    }

    private static bool HasAlternative(GRepData? block, ParsingState state, HashSet<uint> visited, int level)
    {
        if (block == null) return false;
        if ((level == 1 ? block.Coarse : block.Medium) != null) return true;
        foreach (var instance in block.Instances)
            if (instance.SymbolElemId > 0 && visited.Add((uint)instance.SymbolElemId)
                && state.GrepBlocks.TryGetValue((uint)instance.SymbolElemId, out var symbol)
                && HasAlternative(symbol, state, visited, level))
                return true;
        return false;
    }
}
