using RvtIO.Meshes;

namespace RvtIO.Gltf;

/// <summary>Summary counts and coverage metrics of a binary glTF 2.0 (.glb) export.</summary>
public sealed class GlbExportResult
{
    /// <summary>Number of element nodes written to the glTF scene.</summary>
    public int ElementCount { get; init; }

    /// <summary>Number of exported elements that have at least one rendered mesh or placement.</summary>
    public int ElementsWithGeometry { get; init; }

    /// <summary>Total number of glTF nodes written.</summary>
    public int NodeCount { get; init; }

    /// <summary>Total number of unique glTF mesh resources written (shared family symbols are counted once).</summary>
    public int MeshCount { get; init; }

    /// <summary>Number of unique shared symbol mesh resources written.</summary>
    public int SharedSymbolMeshCount { get; init; }

    /// <summary>Total number of symbol placements emitted across all elements.</summary>
    public int PlacementCount { get; init; }

    /// <summary>Number of unique glTF materials written.</summary>
    public int MaterialCount { get; init; }

    /// <summary>True when the source document was read without decoded geometry (<see cref="MeshSet.GeometryUndecoded"/>).</summary>
    public bool GeometryUndecoded { get; init; }

    /// <summary>Counts of drawable elements processed by <see cref="MeshCoverage"/>.</summary>
    public IReadOnlyDictionary<MeshCoverage, int> Coverage { get; init; } = new Dictionary<MeshCoverage, int>();
}
