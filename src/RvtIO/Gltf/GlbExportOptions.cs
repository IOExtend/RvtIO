namespace RvtIO.Gltf;

/// <summary>Options controlling binary glTF 2.0 (.glb) export in <see cref="GlbExporter"/>.</summary>
public sealed class GlbExportOptions
{
    /// <summary>When true (default), drawable elements without geometry (<see cref="RvtIO.Meshes.MeshCoverage.NoGeometry"/> or
    /// <see cref="RvtIO.Meshes.MeshCoverage.Undecoded"/>) are emitted as meshless glTF nodes carrying their element metadata in
    /// <c>extras</c>. When false, elements that produce no mesh are omitted.</summary>
    public bool IncludeElementsWithoutGeometry { get; set; } = true;

    /// <summary>When true (default), repeated family symbol placements (<see cref="RvtIO.Meshes.ElementMesh.Placements"/>) reuse
    /// shared glTF mesh resources. Passed to <see cref="RvtIO.Meshes.ElementMeshes.Extract"/> when exporting directly from a
    /// path or <see cref="RvtDocument"/>.</summary>
    public bool UseSymbolPlacements { get; set; } = true;

    /// <summary>Optional <c>asset.generator</c> string written into the glTF header. Null writes <c>RvtIO &lt;version&gt;</c>.</summary>
    public string? Generator { get; set; }

    /// <summary>Optional writer for non-fatal export diagnostics (degenerate transforms, skipped invalid meshes, undecoded geometry).
    /// Null (default) keeps the library silent.</summary>
    public TextWriter? DiagnosticOutput { get; set; }

    internal bool CollectExportReport { get; init; }
}
