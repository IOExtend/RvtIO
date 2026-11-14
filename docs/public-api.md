# Public API

The `RvtIO` package is the supported surface listed here. Everything else (parsers, geometry carriers, schema classes,
census types, the stream decoders) is `internal`; it can change in any release and is visible only to the test and tool
projects through `InternalsVisibleTo`.

## Reading a file

| Type | Use |
|---|---|
| `RvtDocument` | Opens a file (`Open`), saves a verified lossless copy (`SaveAs`), and gives the elements, project information, file metadata and category lookups. |
| `RvtFileReader` | Lower level read (`Read`) that returns the `RevitFileInfo` behind a document. |
| `RvtReaderOptions` | Options of a read, including `DiagnosticOutput`, `ResolveCurrentCopies` (keeps only the current copy of an element that several partition streams hold; default true, set it to false to decode every copy), `RaiseLimits` and `Diagnostics`. `RevitLocalizationDirectory` is `[Obsolete]` and ignored: the texts are built in. |
| `RvtReadDiagnostics`, `StreamRecord`, `ReaderLimitHit` | What a read collects when `RvtReaderOptions.Diagnostics` is set: the partition and metadata streams read (stored and inflated byte sizes and member decode counts) and the streams cut by a limit. |
| `ExportReport`, `SkipLog`, `GeometryQualityClass`, `ElementGeometryQuality` | The machine-readable report of a run (`ToJson`), the per-element geometry quality classification and totals, and the elements an export left out; see [the export report](export-report.md). |
| `RvtExitCodes`, `RvtInputException`, `RvtLimitException`, `RvtContainerCheck` | The exit codes 0 to 4 and 6 of the command line tool (code 5 is returned by `downgrade --check` and `save-as` when refused), the exceptions `RvtFileReader.Read` throws for a file it cannot read, and the check that raises them. |
| `FilteredElementCollector` | Query over the elements of a document (`OfCategory`, `WhereElementIsNotElementType`, `Where`, ...). |

## Model

`Element`, `Parameter`, `Definition`, `ParameterDefinition`, `StorageType`, `RevitFileInfo`, `FileMetadata`, `ProjectInfo`,
`TransmissionInfo`, `ExternalFileRef`, `ClassSchema`, `ClassField`, `MaterialLayer` and the geometry types (`GeometryData`,
`GeoFace`, `GeoEdge`, `GeoSolid`, `GeoBrepSolid`, `GeoOpening`, `GeoInstance`, `LocationCurve`, `DimensionData`,
`InstanceTransform`) are the data a read returns. `ParameterListConverter` is the `System.Text.Json` converter that writes
the parameter lists of a `RevitFileInfo`.

`Element.BoundingBox` is the axis-aligned outline stored in the element header, as the Revit API bounding box (null for elements with no stored outline, such as grids and levels). The tight box of the decoded geometry is `Element.Geometry.BoundingBox`; before 0.3.0 `Element.BoundingBox` held that decoded value, which also changes the result of `FilteredElementCollector.WhereBoundingBoxIsNotNull`.

## Built-in identifiers

`BuiltInCategory`, `BuiltInParameter`, `BuiltInParameterGroup`, `ParameterType`, `UnitType`, `DisplayUnitType`,
`UnitSymbolType`, `UnitGroup`, `MEPSystemClassification`, `FlowDirectionType`, `PipeFlowConfigurationType` and their
extension and label helpers carry the ids and names stored in the file format.

## Export

| Type | Use |
|---|---|
| `IfcExporter` | Writes IFC from a `RevitFileInfo` (`Export`). Properties: `Profile`, `BaseQuantities`, `OpeningQuantitiesSwapped` (true writes the legacy Width and Height of door and window openings; default false), `OpeningQuantitiesInHostAxes` (the opposite of it, kept for compatibility), `ExportPhaseId`, `ExportAllPhases`, `RoomExportPhaseId`, `ExportRoomsOfAllPhases`, `RevitParameterGroups`, `Timestamp`, `SkipLog`, `ExportedProducts`, `ExportedElementQualities`, `DiagnosticOutput`. |
| `IfcProfile` | `Legacy` or `Current` (the default) output profile. |
| `IfcUnitFamily` | The length unit family an export of the current profile declares, by the length format of the project: `Metre` for metres and for metres and centimetres, `Foot` for feet, feet and fractional inches and US survey feet, `Millimetre` for millimetres, for every other format and for an unreadable one. The legacy profile always declares millimetres. |
| `CompactJsonExporter` | `Export(rvtPath, outputPath, readerOptions, options)` reads a file and writes the compact JSON. |
| `CompactJsonExportOptions` | `JsonClassNamePrefix`: text put in front of every class name in the `name` field. |

## Triangle meshes

| Type | Use |
|---|---|
| `ElementMeshes` | `Extract(document, options)` returns the triangle meshes of the drawable model elements of an opened `RvtDocument`, from the geometry the document already holds (the file is not read again). Coordinates are in feet, in the coordinates of the document (Revit internal coordinates, Z up). |
| `ElementMeshOptions` | `UseSymbolPlacements` (default true): an element that is exactly its family symbols placed rigidly is returned as placements of shared symbol meshes; false gives every element its own triangles. |
| `MeshSet` | `Elements` (one `ElementMesh` per drawable element, in document order), `Symbols` (symbol element id to its triangle meshes in the frame of the family, only for placed symbols) and `GeometryUndecoded` (a file of a release other than Revit 2022, read by its own class schema, opened without `RvtReaderOptions.R22Layout`: its elements are listed as `Undecoded`). |
| `ElementMesh` | `ElementId`, `Meshes` (the element's own triangles, one mesh per material), `Placements`, `Coverage` and `UntriangulatedFaces`. |
| `TriangleMesh` | `Positions` (x, y, z per vertex), `Indices` (three per triangle, counter-clockwise around the normals), `Normals` (one unit normal per vertex, flat per face: vertices are not shared between faces) and `MaterialId` (the material element id, 0 for none). |
| `SymbolPlacement`, `MeshTransform`, `MeshVector` | A symbol id with its transform. `MeshTransform` maps a family point p to `Origin + BasisX * p.X + BasisY * p.Y + BasisZ * p.Z`; the basis vectors are the columns of the row-major 3x3 matrix of `GeoInstance.Transform` (`BasisX = (t[0], t[3], t[6])`, `Origin = (t[9], t[10], t[11])`, see `FromRowMajor`). A negative `Determinant` is a mirror image. |
| `MeshCoverage` | `Complete`, `Partial` (some faces did not triangulate, a placed symbol or a symbol nested in one has no geometry, or a Geometry node that Revit exports has no decoded polygon), `BoxSubstitute` (a symbol placed at a rescaled placement was replaced by its box), `NoGeometry` (a geometry record with nothing to draw) and `Undecoded`. |

A placement is given only when it is shown that the element is exactly its symbols placed: the element's geometry record holds
no face of its own, the tight box of the element cut nothing off the placed symbols, every symbol resolved, no box was put in
the place of a component, and every transform is rigid (orthonormal, determinant 1 or -1). Any other element, a cut or clipped
instance included, gets its own triangles. The shared symbol triangles keep the winding of the family: a consumer that culls back
faces reverses the winding of a placement with a negative determinant. The own triangles of an element, those of a mirrored
instance included, are wound around the normals of their faces.

The drawable elements follow a subset of the product rules of the IFC export of the current profile, and the materials and the
hidden Geometry nodes follow its rules: they are listed in the documentation comment of `ElementMeshes`, with the product rules
that are not applied (the "export to IFC" flag, topography, walls that are not visible and others), so the meshes can include
elements that the IFC leaves out. Curved surfaces are the dense polygons the reader decodes for them.

## Writing and save-as

| Type | Use |
|---|---|
| `RvtWriter` | Inspects the write-capability matrix (`InspectCapabilities`), writes lossless copies (`SaveAs`), and persists targeted stream and partition-record edits (`Rewrite`) for local `2020` to `2027` `.rvt` and `.rfa` files; see [writing and save-as](writing.md). |
| `RvtWriteCapabilities`, `RvtOperationSupport`, `RvtFileKind`, `RvtWriteOperation` | Capability classification by file kind (`Project`, `Family`), release (`2020` to `2027`), element ID width (`4` or `8` bytes), worksharing/cloud flags, and operation (`SaveAs`, `PagedStreamRewrite`, `PartitionRecordRewrite`). |
| `RvtWriteRequest`, `RvtPagedStreamEdit`, `RvtPartitionRecordEdit`, `RvtWriteResult` | Edit specifications for `Formats/Latest`, `Global/*`, and `Partitions/<n>` records, and the verified write summary. |
| `RvtWriteRefusedException` | Thrown with a stable `Kind` code when a file state or edit request is outside the supported write scope. |

## Diagnostics

The library never writes to the console. A diagnostic message is written only when the caller gives a `TextWriter`:
`RvtReaderOptions.DiagnosticOutput` for a read (and for `CompactJsonExporter.Export`), `IfcExporter.DiagnosticOutput` (or the
constructor argument) for an IFC export. The default is `null`, which is silent. The writer is scoped to the call that
received it, so concurrent calls with different writers do not mix.

The `rvtio` command line tool sets the writer to standard error.
