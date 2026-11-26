# GLB export

`RvtIO.Gltf.GlbExporter` writes decoded Revit triangle meshes as a self-contained binary glTF 2.0 (`.glb`) asset. Standalone Revit families (`.rfa`) whose bodies have no decoded triangles are refused. The API throws `NotSupportedException`, and the CLI returns exit code 4 with the reason `glb-family-geometry-unavailable` in the report.

## Coordinates, units, and resource sharing

- **Units and frame**: Coordinates are scaled from feet to metres (`× 0.3048`) and converted from Revit's right-handed Z-up frame into glTF 2.0's right-handed Y-up frame (`(x, y, z) -> (x, z, -y)`).
- **Precision**: Elements drawn with their own triangles rebase vertex positions relative to their local bounding corner in Y-up metres and store the offset in the glTF node `translation`.
- **Shared family symbols**: Elements that place family symbols rigidly (`ElementMesh.Placements`, `MeshSet.Symbols`) reuse a single glTF `mesh` resource across repeated placements, including mirrored placements (`det < 0`, where glTF 2.0 flips triangle winding from the node matrix determinant).
- **Elements without geometry**: By default (`GlbExportOptions.IncludeElementsWithoutGeometry = true`), drawable elements without triangles (`MeshCoverage.NoGeometry`, `MeshCoverage.Undecoded`) remain in the scene as meshless glTF nodes carrying their identity metadata.

## Materials and `extras`

- **PBR materials**: Revit materials (`MATERIAL_PARAM_COLOR`, `MATERIAL_PARAM_TRANSPARENCY`) are converted from sRGB to linear `pbrMetallicRoughness.baseColorFactor`. Transparent materials (`alpha < 1`) emit `alphaMode: "BLEND"` and `doubleSided: true`; opaque materials with unknown color fall back to neutral grey.
- **Node `extras`**: Every element/placement node carries JSON metadata in `extras`: `elementId`, `uniqueId`, `ifcGuid`, `category`, `categoryId`, `typeId`, `typeName`, `materialIds`, optional `symbolId`, and `coverage`.
- **Material `extras`**: Every glTF material carries `materialId`, `uniqueId`, `colorSource` (`"file"` or `"default"`), `alphaSource`, and optional `transparencyPercent`.

## Library and CLI usage

```csharp
using RvtIO.Gltf;

var result = GlbExporter.Export("project.rvt", "project.glb");
```

Or reuse an already opened `RvtDocument` and `MeshSet`:

```csharp
using RvtIO;
using RvtIO.Gltf;
using RvtIO.Meshes;

var doc = RvtDocument.Open("project.rvt", new RvtReaderOptions { R22Layout = true });
var meshes = ElementMeshes.Extract(doc);
var result = GlbExporter.Export(doc, meshes, "project.glb");
```

From the command line:

```sh
rvtio project.rvt project.glb
rvtio project.rvt --glb project.glb --ifc project.ifc --compact-json project.json
```

With `--report`, a GLB-only run includes element, node and mesh counts in `export`. When IFC or JSON provides the main `export` summary, the GLB counts appear in `glbExport`.

When `--glb` is combined with `--ifc`, `--scene`, `--compact-json`, or `--full-json`, the `.rvt` file is read once (and `ElementMeshes` is extracted once when paired with `--scene`).
