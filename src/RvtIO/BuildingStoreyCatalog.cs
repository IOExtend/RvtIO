using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO;

/// <summary>A project's building-storey levels and the assignments used by the scene and current IFC exporters.</summary>
/// <remarks>Build a new catalog after editing a document's levels.</remarks>
public sealed class BuildingStoreyCatalog
{
    private readonly IReadOnlyDictionary<int, Element> _elements;
    private readonly BuildingStoreyResolver _resolver;

    /// <summary>Project building storeys, ordered by elevation and then by Revit element ID.</summary>
    public IReadOnlyList<BuildingStorey> Storeys { get; }

    internal BuildingStoreyCatalog(IReadOnlyDictionary<int, Element> elements)
    {
        _elements = elements;
        var levels = BuildingStoreyResolver.GetBuildingStoreyLevels(elements.Values);
        Storeys = Array.AsReadOnly(levels.Select(level => new BuildingStorey(
            level.Id, level.Name!, BuildingStoreyResolver.GetElevation(level))).ToArray());

        // The resolver reserves zero for the building, independent of the authored level IDs.
        var ids = new Dictionary<int, int>(levels.Count);
        for (int index = 0; index < levels.Count; index++)
            ids.Add(levels[index].Id, index + 1);
        _resolver = new BuildingStoreyResolver(ids, elements, ids.Count == 0 ? 0 : 1, buildingId: 0, currentProfile: true);
    }

    /// <summary>The Revit ID of the building storey assigned to this element, or null if the element is unknown
    /// or its assignment is to the building itself. A stored level takes precedence over placement by geometry.</summary>
    public int? GetStoreyId(int elementId)
    {
        if (Storeys.Count == 0 || !_elements.TryGetValue(elementId, out var element)) return null;
        int slot = _resolver.Resolve(element, out _);
        return slot > 0 ? Storeys[slot - 1].Id : null;
    }
}

/// <summary>A building storey selected from the native project levels of a Revit document.</summary>
/// <param name="Id">The Revit element ID of the level.</param>
/// <param name="Name">The name of the level.</param>
/// <param name="ElevationFeet">The reader's resolved elevation in the document's internal coordinates, in feet.</param>
public readonly record struct BuildingStorey(int Id, string Name, double ElevationFeet);
