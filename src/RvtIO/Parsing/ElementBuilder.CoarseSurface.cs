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
        var candidates = new List<Element>();
        foreach (var element in info.Elements)
            if (element.CategoryId is DoorsCategoryId or WindowsCategoryId or StructuralColumnsCategoryId)
                candidates.Add(element);

        // The temporary decoded bodies are large. Bound the number built at the same time.
        if (SymbolDiagPath != null || Environment.GetEnvironmentVariable("RVTIO_GREPV2_DBG") == "1"
            || Environment.GetEnvironmentVariable("RVTIO_GREPV2_DBG_ID") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("RVTIO_TOPO") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("RVTIO_MESH_DIAG") is { Length: > 0 })
        {
            foreach (var element in candidates) Apply(element);
        }
        else Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = 2 }, Apply);

        void Apply(Element element)
        {
            if (element.CategoryId == StructuralColumnsCategoryId)
            {
                ApplyMediumLevelVolume(element, state);
                return;
            }
            uint id = (uint)element.Id;
            state.GrepBlocks.TryGetValue(id, out var grep);
            state.FamilyInstanceTransforms.TryGetValue(id, out var transformOnly);
            var visited = new HashSet<uint>();
            if (!HasQuantityAlternative(grep, state, visited)
                && !(transformOnly != null && state.GrepBlocks.TryGetValue((uint)Math.Max(transformOnly.SymbolId, 0), out var symbol)
                    && HasQuantityAlternative(symbol, state, visited)))
                return;

            var coarse = BuildQuantityGeometry(element.Id, grep, state, GeometryDetail.CoarseQuantities);
            element.CoarseSurfaceArea = BrepMeasures.SurfaceAreaCurrent(coarse);
        }
    }

    /// <summary>The volume of the taken solids of a column at the Medium detail level, when the element has alternatives for it.
    /// Nothing is decoded for an element without them.</summary>
    private static void ApplyMediumLevelVolume(Element element, ParsingState state)
    {
        uint id = (uint)element.Id;
        state.GrepBlocks.TryGetValue(id, out var grep);
        state.FamilyInstanceTransforms.TryGetValue(id, out var transformOnly);
        var visited = new HashSet<uint>();
        if (!HasAlternative(grep, state, visited, 2)
            && !(transformOnly != null && state.GrepBlocks.TryGetValue((uint)Math.Max(transformOnly.SymbolId, 0), out var symbol)
                && HasAlternative(symbol, state, visited, 2)))
            return;
        element.HasMediumAlternatives = true;
        var medium = BuildQuantityGeometry(element.Id, grep, state, GeometryDetail.Medium);
        element.MediumSolidTotals = BrepMeasures.NodeSolidTotalsCurrent(medium);
    }

    /// <summary>True when the block or a nested symbol has a separate Coarse or non-visible quantity view.</summary>
    private static bool HasQuantityAlternative(GRepData? block, ParsingState state, HashSet<uint> visited)
    {
        if (block == null) return false;
        if (block.Coarse != null || block.CoarseQuantities != null) return true;
        foreach (var instance in block.Instances)
            if (instance.SymbolElemId > 0 && visited.Add((uint)instance.SymbolElemId)
                && state.GrepBlocks.TryGetValue((uint)instance.SymbolElemId, out var symbol)
                && HasQuantityAlternative(symbol, state, visited))
                return true;
        return false;
    }
}
