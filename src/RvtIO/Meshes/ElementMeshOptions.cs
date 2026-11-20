namespace RvtIO.Meshes;

/// <summary>Options of <see cref="ElementMeshes.Extract"/>.</summary>
public sealed class ElementMeshOptions
{
    /// <summary>Returns an element that is exactly its symbols placed as <see cref="ElementMesh.Placements"/> of shared symbol meshes
    /// (true, the default). False gives every element its own triangles and leaves <see cref="MeshSet.Symbols"/> empty.</summary>
    public bool UseSymbolPlacements { get; set; } = true;

    /// <summary>Includes decoded straight model lines and meshes of reinforcement described by its centre lines.</summary>
    public bool IncludeModelLinesAndReinforcement { get; set; }

    /// <summary>Includes the finite reference lines of grids and levels for native cache export.</summary>
    public bool IncludeDatumGeometry { get; set; }
}
