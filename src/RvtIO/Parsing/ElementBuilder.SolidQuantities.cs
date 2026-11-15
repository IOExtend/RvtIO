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
            element.LevelShells = new SolidQuantities.Shell?[3];
            element.LevelSolidCounts = new int[3];
            LevelGeometries(element, state, (slot, geometry) =>
            {
                element.LevelShells[slot] = SolidQuantities.OfAllSolids(geometry);
                element.LevelSolidCounts[slot] = geometry?.BrepSolids?.Count ?? 0;
            });
        }
    }

    /// <summary>
    /// Hands the geometry of an element at Coarse (slot 0), Medium (slot 1) and Fine (slot 2) to
    /// <paramref name="visit"/>, Fine first. A level the element's blocks have no alternative for
    /// is the Fine geometry.
    /// </summary>
    internal static void LevelGeometries(Element element, ParsingState state, Action<int, GeometryData?> visit)
    {
        visit(2, element.Geometry);
        if (element.Id <= 0)
        {
            visit(0, element.Geometry);
            visit(1, element.Geometry);
            return;
        }
        uint id = (uint)element.Id;
        state.GrepBlocks.TryGetValue(id, out var grep);
        state.FamilyInstanceTransforms.TryGetValue(id, out var transformOnly);
        for (int level = 1; level <= 2; level++)
        {
            var visited = new HashSet<uint>();
            bool has = HasAlternative(grep, state, visited, level)
                || (transformOnly != null && state.GrepBlocks.TryGetValue((uint)Math.Max(transformOnly.SymbolId, 0), out var symbol)
                    && HasAlternative(symbol, state, visited, level));
            int slot = level == 1 ? 0 : 1;
            if (!has) { visit(slot, element.Geometry); continue; }
            state.CoarseView = level == 1; state.DetailView = level;
            try
            {
                var alt = new Element { Id = element.Id, TypeId = element.TypeId, BoundingBox = element.Geometry?.BoundingBox };
                var own = level == 1 ? grep?.Coarse ?? grep : grep?.Medium ?? grep;
                if (own != null)
                {
                    alt.Geometry = BuildGeometryData(own, state.SeparateFacesWithDistinctOpenings);
                    ExpandInstanceGeometry(alt, own, element.TypeId, state);
                }
                ExpandTransformOnlyInstanceGeometry(alt, id, element.TypeId, state);
                visit(slot, alt.Geometry);
            }
            finally { state.CoarseView = false; state.DetailView = 0; }
        }
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
