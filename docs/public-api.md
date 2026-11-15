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
| `RvtReadDiagnostics`, `StreamRecord`, `ReaderLimitHit` | What a read collects when `RvtReaderOptions.Diagnostics` is set: the partition streams read and the streams cut by a limit. |
| `ExportReport`, `SkipLog` | The machine-readable report of a run (`ToJson`) and the elements an export left out; see [the export report](export-report.md). |
| `RvtExitCodes`, `RvtInputException`, `RvtLimitException`, `RvtContainerCheck` | The exit codes 0 to 4 and 6 of the command line tool (code 5 is returned by `downgrade --check` and `save-as` when refused), the exceptions `RvtFileReader.Read` throws for a file it cannot read, and the check that raises them. |
| `FilteredElementCollector` | Query over the elements of a document (`OfCategory`, `WhereElementIsNotElementType`, `Where`, ...). |

## Model

`Element`, `Parameter`, `Definition`, `ParameterDefinition`, `StorageType`, `RevitFileInfo`, `FileMetadata`, `ProjectInfo`,
`TransmissionInfo`, `ExternalFileRef`, `ClassSchema`, `ClassField`, `MaterialLayer` and the geometry types (`GeometryData`,
`GeoFace`, `GeoEdge`, `GeoSolid`, `GeoBrepSolid`, `GeoOpening`, `GeoInstance`, `LocationCurve`, `DimensionData`,
`InstanceTransform`) are the data a read returns. `ParameterListConverter` is the `System.Text.Json` converter that writes
the parameter lists of a `RevitFileInfo`.

`Element.BoundingBox` is the axis-aligned outline stored in the element header, as the Revit API bounding box (null for elements with no stored outline, such as grids and levels). The tight box of the decoded geometry is `Element.Geometry.BoundingBox`; before 0.3.0 `Element.BoundingBox` held that decoded value, which also changes the result of `FilteredElementCollector.WhereBoundingBoxIsNotNull`.

`Element.ViewportViewId` and `Element.ViewportOffset` are the view a viewport displays and the offset of that view
on the sheet (feet), decoded from the viewport's own block.

`Element.Text`, `Element.TextPosition` and `Element.TextFontSizePoints` are the plain text of a text note, the
position the file stores for it (feet) and the font size its RTF declares; they are null for every other element.

`Element.OwnerViewId` is the stored `m_ownerDBViewId` of the element: the view or sheet whose annotations and view-specific graphics hold it (a text note on a sheet names the sheet), null when no view owns it. `SheetCatalog` uses it for the elements a sheet owns.

## Built-in identifiers

`BuiltInCategory`, `BuiltInParameter`, `BuiltInParameterGroup`, `ParameterType`, `UnitType`, `DisplayUnitType`,
`UnitSymbolType`, `UnitGroup`, `MEPSystemClassification`, `FlowDirectionType`, `PipeFlowConfigurationType` and their
extension and label helpers carry the ids and names stored in the file format.

## Export

| Type | Use |
|---|---|
| `IfcExporter` | Writes IFC from a `RevitFileInfo` (`Export`). Properties: `Profile`, `BaseQuantities`, `OpeningQuantitiesSwapped` (true writes the legacy Width and Height of door and window openings; default false), `OpeningQuantitiesInHostAxes` (the opposite of it, kept for compatibility), `ExportPhaseId`, `ExportAllPhases`, `RoomExportPhaseId`, `ExportRoomsOfAllPhases`, `RevitParameterGroups`, `Timestamp`, `SkipLog`, `ExportedProducts`, `DiagnosticOutput`. |
| `IfcProfile` | `Legacy` or `Current` (the default) output profile. |
| `IfcUnitFamily` | The length unit family an export of the current profile declares, by the length format of the project: `Metre` for metres and for metres and centimetres, `Foot` for feet, feet and fractional inches and US survey feet, `Millimetre` for millimetres, for every other format and for an unreadable one. The legacy profile always declares millimetres. |
| `CompactJsonExporter` | `Export(rvtPath, outputPath, readerOptions, options)` reads a file and writes the compact JSON. |
| `CompactJsonExportOptions` | `JsonClassNamePrefix`: text put in front of every class name in the `name` field. |

## Sheets and viewports

| Type | Use |
|---|---|
| `SheetCatalog` | `Extract(document)` (or a `RevitFileInfo`) returns the sheets of the file, the viewports placed on them and the limits of this release: `Sheets`, `Viewports` and `Issues`. Nothing is read from the file again. |
| `SheetRecord` | A sheet: `ElementId`, `Number`, `Name`, `IssueDate`, `Scale`, `DrawnBy`, `DesignedBy`, `CheckedBy`, `ApprovedBy`, `ViewportIds` and `OwnedElementIds`. |
| `ViewportRecord` | A placed view: `ElementId`, `SheetId` and `DetailNumber`; `DisplayedViewId` is null until the viewer element is decoded. |
| `SheetIssue`, `SheetIssueKind` | What this release does not decode: page size, view placement, displayed view, view content and crop region. |

See [sheets and viewports](sheets.md) for what is decoded, what the limits are and why no page coordinate is exposed yet.

## Printing

| Type | Use |
|---|---|
| `PlanPrinter` | `Levels(document)` lists the levels a plan can be printed for; `Render(document, levelId, scale, options)` returns a `PdfDocument` with the plan of one level: walls with their stored width, column footprints, clipped grids. |
| `PlanPrintOptions` | `PaperSize` (null picks the smallest paper that holds the drawing), `MarginMillimetres`, `FallbackWallWidthMillimetres`, `DrawFrame`. |
| `PlanLevel`, `PlanView` | A printable level (id, name, elevation) and a printable plan view (id, name, level, scale). |
| `ViewPrinter` | `Render(document, viewIds, options)` prints plans and elevations into one PDF document, one page per view (each must be one of `PlanPrinter.Views` or `ElevationPrinter.Views`). |
| `ElevationPrinter` | `Views(document)` lists the compass elevations (North, South, East, West) with their scale; `Collect(document, direction)` projects the model faces on the vertical plane and `DrawContent` writes them. A view's plane is not decoded yet, so the direction comes from its name. |
| `ElevationView` | An elevation view: id, name, direction and scale. |
| `SheetComposer` | `Sheets(document)` lists the numbered sheets with the page their title block spans; `Render(document, sheetId, options)` and `RenderSheets` compose pages: each placed viewport draws its plan at the stored offset and view scale, labelled with the view name and scale, and the sheet's own text notes are written. |
| `SheetComposeOptions` | `PaperSize` (null takes the title block extent, snapped to A4-A0), `DrawViewLabels`, `ViewMarginMillimetres`. |
| `ComposedSheet` | A composed sheet: id, number, name, page, placed viewport count, text count. |
| `SheetPrinter` | `Sheets(document)` lists the sheets with page-sized decoded linework; `Render(document, sheetId, options)` and `RenderSheets` print one page per sheet from the lines the sheet owns, on the paper the linework spans. |
| `SheetPrintOptions` | `PaperSize` (null uses the linework extent, snapped to A4-A0), `MarginMillimetres`, `PaperToleranceMillimetres`, `DrawSheetFields`. |
| `PrintableSheet` | A printable sheet: id, number, name, linework extent, curve and text counts. |

See [printing a plan](printing.md) for what is drawn, what is not, and the roadmap to sheet printing.

## PDF output

| Type | Use |
|---|---|
| `PdfDocument` | `AddPage(size)` and `Save(path | stream)`; writes PDF 1.4 with vector lines, polygons, rectangles and text, without a third-party library. |
| `PdfPage` | `DrawLine`, `DrawPolyline`, `DrawRectangle` and `DrawText` in millimetres from the lower-left corner, y up. |
| `PdfPageSize` | Paper sizes A4 to A0 in millimetres, `Landscape` for the turned page. |
| `PdfTextAlignment` | `Left`, `Center`, `Right`: where the text anchor sits. |
| `PdfTrueTypeFont` | A TrueType font parsed for embedding (`UnitsPerEm`, `GlyphOf`, `AdvanceOf`), drawn as a Type0 font with Identity-H encoding and a ToUnicode map, so text in the scripts it covers (Cyrillic included) is real, selectable PDF text. |
| `PdfFonts` | `TryFind` loads the first TrueType font found on the machine (Arial, Segoe UI, Tahoma, DejaVu Sans, Helvetica Neue). |

Without `PdfDocument.Font` the writer uses the standard Helvetica font, which holds printable ASCII only, and a text with
another character is refused. With it, every text is written with the embedded font.

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
