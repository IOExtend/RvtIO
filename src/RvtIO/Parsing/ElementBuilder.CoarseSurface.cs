using RvtIO.Geometry;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    private const int DoorsCategoryId = -2000023, WindowsCategoryId = -2000014;

    /// <summary>Computed door/window area uses Coarse solids including non-visible objects.
    /// This separate view is placed like the visible body and never replaces the element's geometry.</summary>
    internal static void ApplyCoarseDoorWindowSurfaces(RevitFileInfo info, ParsingState state)
    {
        foreach (var element in info.Elements)
        {
            if (element.CategoryId == StructuralColumnsCategoryId)
            {
                ApplyMediumLevelVolume(element, state);
                continue;
            }
            if (element.CategoryId is not (DoorsCategoryId or WindowsCategoryId)) continue;
            uint id = (uint)element.Id;
            state.FamilyInstanceTransforms.TryGetValue(id, out var transformOnly);
            var visited = new HashSet<uint>();
            const GRepBlockStore.DetailAlternatives alternatives =
                GRepBlockStore.DetailAlternatives.Coarse | GRepBlockStore.DetailAlternatives.CoarseQuantities;
            if (!state.GrepBlocks.HasDetailAlternative(id, alternatives, visited)
                && !(transformOnly != null
                    && state.GrepBlocks.HasDetailAlternative((uint)Math.Max(transformOnly.SymbolId, 0), alternatives, visited)))
                continue;

            state.GrepBlocks.TryGetValue(id, out var grep);
            state.CoarseView = true;
            state.IncludeNonVisibleQuantities = true;
            try
            {
                var coarse = new Element { Id = element.Id, TypeId = element.TypeId, BoundingBox = element.Geometry?.BoundingBox };
                var own = grep?.CoarseQuantities ?? grep?.Coarse ?? grep;
                if (own != null)
                {
                    coarse.Geometry = BuildGeometryData(own, state.SeparateFacesWithDistinctOpenings);
                    ExpandInstanceGeometry(coarse, own, element.TypeId, state);
                }
                ExpandTransformOnlyInstanceGeometry(coarse, id, element.TypeId, state);
                element.CoarseSurfaceArea = BrepMeasures.SurfaceAreaCurrent(coarse.Geometry);
            }
            finally
            {
                state.CoarseView = false;
                state.IncludeNonVisibleQuantities = false;
            }
        }
    }

    /// <summary>The volume of the taken solids of a column at the Medium detail level, when the element has alternatives for it.
    /// Nothing is decoded for an element without them.</summary>
    private static void ApplyMediumLevelVolume(Element element, ParsingState state)
    {
        uint id = (uint)element.Id;
        state.FamilyInstanceTransforms.TryGetValue(id, out var transformOnly);
        var visited = new HashSet<uint>();
        const GRepBlockStore.DetailAlternatives alternativeLevel = GRepBlockStore.DetailAlternatives.Medium;
        if (!state.GrepBlocks.HasDetailAlternative(id, alternativeLevel, visited)
            && !(transformOnly != null
                && state.GrepBlocks.HasDetailAlternative((uint)Math.Max(transformOnly.SymbolId, 0), alternativeLevel, visited)))
            return;
        state.GrepBlocks.TryGetValue(id, out var grep);
        element.HasMediumAlternatives = true;
        state.DetailView = 2;
        try
        {
            var alternative = new Element { Id = element.Id, TypeId = element.TypeId, BoundingBox = element.Geometry?.BoundingBox };
            var block = grep?.Medium ?? grep;
            if (block != null)
            {
                alternative.Geometry = BuildGeometryData(block, state.SeparateFacesWithDistinctOpenings);
                ExpandInstanceGeometry(alternative, block, element.TypeId, state);
            }
            ExpandTransformOnlyInstanceGeometry(alternative, id, element.TypeId, state);
            element.MediumSolidTotals = BrepMeasures.NodeSolidTotalsCurrent(alternative.Geometry);
        }
        finally { state.DetailView = 0; }
    }
}
