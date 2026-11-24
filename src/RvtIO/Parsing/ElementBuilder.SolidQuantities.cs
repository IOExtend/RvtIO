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
            LevelGeometries(element, state, (slot, geometry) =>
            {
                shells[slot] = SolidQuantities.OfAllSolids(geometry);
                counts[slot] = geometry?.BrepSolids?.Count ?? 0;
            });
            element.LevelShells = shells;
            element.LevelSolidCounts = counts;
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
        GRepData? grep = null;
        state.FamilyInstanceTransforms.TryGetValue(id, out var transformOnly);
        for (int level = 1; level <= 2; level++)
        {
            var visited = new HashSet<uint>();
            var alternative = level == 1 ? GRepBlockStore.DetailAlternatives.Coarse : GRepBlockStore.DetailAlternatives.Medium;
            bool has = state.GrepBlocks.HasDetailAlternative(id, alternative, visited)
                || (transformOnly != null
                    && state.GrepBlocks.HasDetailAlternative((uint)Math.Max(transformOnly.SymbolId, 0), alternative, visited));
            int slot = level == 1 ? 0 : 1;
            if (!has) { visit(slot, element.Geometry); continue; }
            if (grep == null) state.GrepBlocks.TryGetValue(id, out grep);
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
}
