using IOScene;
using RvtIO.Meshes;

namespace RvtIO.Scene;

/// <summary>The scene made from one Revit document by <see cref="RvtSceneReader"/>, what was lost on the way, and counts.</summary>
public sealed class RvtSceneResult
{
    /// <summary>The scene: one model, its elements, geometries, instances, materials and property sets, in metres.</summary>
    public required SceneDocument Document { get; init; }

    /// <summary>What the conversion could not carry over: an error when the geometry of the file was not decoded or the read was
    /// cut by a reader limit, and a warning for every element whose geometry is incomplete or missing. Empty when nothing was lost.
    /// <c>SourceId</c> names the element (its Revit UniqueId) where there is one.</summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>The number of elements in the scene: one per drawable element of the document (see <see cref="ElementMeshes"/>).</summary>
    public int ElementCount { get; init; }

    /// <summary>The number of elements that have at least one instance.</summary>
    public int ElementsWithGeometry { get; init; }

    /// <summary>Revit datum IDs whose reference geometry is hidden by default in native export.</summary>
    public IReadOnlyList<long> HiddenDatumIds { get; init; } = [];

    /// <summary>The number of elements by the coverage of their meshes; a coverage that no element has is not listed.</summary>
    public IReadOnlyDictionary<MeshCoverage, int> Coverage { get; init; } = new Dictionary<MeshCoverage, int>();

    /// <summary>The number of geometries in the scene.</summary>
    public int GeometryCount { get; init; }

    /// <summary>The number of instances in the scene.</summary>
    public int InstanceCount { get; init; }

    /// <summary>The number of symbol placements read: each is one instance per mesh of its symbol.</summary>
    public int PlacementCount { get; init; }
}
