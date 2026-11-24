# Public API

The `RvtIO` package is the supported surface listed here. Everything else (parsers, geometry carriers, schema classes,
census types, the stream decoders) is `internal`; it can change in any release and is visible only to the test and tool
projects through `InternalsVisibleTo`.

## Reading a file

| Type | Use |
|---|---|
| `RvtDocument` | Opens a file (`Open`) or a readable, seekable stream (`OpenStream`), saves a verified lossless copy (`SaveAs`), and gives the elements, project information, file metadata and category lookups. A document opened from a stream has no `SourcePath`: `SaveAs` throws `InvalidOperationException`, and printing cannot read the images it takes from the file (the title block logo and the picture of a rendering view). |
| `RvtFileReader` | Lower level read that returns the `RevitFileInfo` behind a document. `Read` takes a path and `ReadStream` reads from the beginning of a readable, seekable stream, whatever its position. The stream is only read: it is never written to, shortened or closed, also when it is writable. A failure of the stream itself is thrown as the stream threw it. |
| `RvtReaderOptions` | Options of a read, including `DiagnosticOutput`, `Progress`, `CreatePartitionScratch`, `ResolveCurrentCopies` (keeps only the current copy of an element that several partition streams hold; default true, set it to false to decode every copy), `RaiseLimits` and `Diagnostics`. `RevitLocalizationDirectory` is `[Obsolete]` and ignored: the texts are built in. |
| `RvtReadDiagnostics`, `StreamRecord`, `ReaderLimitHit` | What a read collects when `RvtReaderOptions.Diagnostics` is set: the partition and metadata streams read (stored and inflated byte sizes and member decode counts) and the streams cut by a limit. |
| `RvtReadProgress` | File-reading updates delivered through `RvtReaderOptions.Progress`. |
| `ExportReport`, `SkipLog`, `GeometryQualityClass`, `ElementGeometryQuality` | The machine-readable report of a run (`ToJson`), the per-element geometry quality classification and totals, and the elements an export left out; see [the export report](export-report.md). |
| `RvtExitCodes`, `RvtInputException`, `RvtLimitException`, `RvtContainerCheck` | The exit codes 0 to 4 and 6 of the command line tool (code 5 is returned by `downgrade --check` and `save-as` when refused), the exceptions `RvtFileReader.Read` and `ReadStream` throw for input they cannot read (`ReadStream` also throws `ArgumentNullException` and `ArgumentException` for a missing, unreadable or non-seekable stream), and the check that raises them. |
| `FilteredElementCollector` | Query over the elements of a document (`OfCategory`, `WhereElementIsNotElementType`, `Where`, ...). |
| `BuildingStoreyCatalog`, `BuildingStorey` | `RvtDocument.GetBuildingStoreyCatalog()` lists native project building storeys in order of elevation and Revit ID. Each storey carries its ID, name and resolved elevation in internal feet. `GetStoreyId(elementId)` uses the current IFC storey rules and returns null for an unknown element or one assigned to the building itself. Obtain a new catalog after editing levels. |
| `GridAxis` | `RvtDocument.GetGridAxes()` lists decoded project axes in Revit ID order. A circular or multi-segment axis has ordered analytic segments. `GridAxis.Tessellate(maxErrorFeet)` returns path points in internal feet. |

`RvtReaderOptions.CreatePartitionScratch` can return a new, empty stream for the current read. The stream must support reading,
writing and seeking. RvtIO writes inflated partition segments to it, reads them back in windows and disposes it before the read
returns or throws. Use private temporary storage outside managed memory to reduce the reader's working set. With a null factory,
segments are kept in memory.

Exact framing is validated in both modes. Layout translation reads the partition by item and writes completed chunks to scratch
when configured. Raw element record bodies can be read back from those same chunks. With scratch storage, the geometry block
index keeps ranges in the inflated chunks and decodes the selected blocks again when building the document. The parser
stores decoded parameter records individually in scratch and reads them by element ID. Recovery of damaged framing and bytes
outside the layout grammar can still require a whole partition in memory. The returned document and its geometry are built in
memory.

With a scratch factory, `ReadStream` can validate and decode an exactly framed partition stream larger than one array.
The reader also stores decoded element-record references and field vectors on pages in that stream during a scratch-backed read.
The array-size limit still applies to other streams and to compressed partitions that require the recovery scanner.

`RvtReaderOptions.Progress` observes one `RvtFileReader.Read` or `ReadStream`. The reader calls `IProgress<RvtReadProgress>.Report`
synchronously on the thread that calls `Read` or `ReadStream`, one call at a time and in phase order, and does not catch an exception the
observer throws: it ends the read and reaches the caller as it was thrown, so an observer can cancel by throwing.
The standard `Progress<T>` does not give these guarantees: it posts each update to the `SynchronizationContext` it captured
when it was created, or to the thread pool when there is none, so its handler can run after `Read` or `ReadStream` has returned and, on the
thread pool, out of order. Implement `IProgress<RvtReadProgress>` to receive the updates in order on the reading thread.

The phases are `metadata`, `partitions`, `class-schema`, `wide-model`, `r22-layout`, `element-records`, `element-blocks`,
`parameter-stream`, `recover-geometry`, `element-properties`, `build-model`, `finalize-model` and `complete`, and a read
reports them in this order and never returns to an earlier phase. A file can skip phases it does not need:
`r22-layout` appears only when the file's own class schema is rewritten into the Revit 2022 layout (`R22Layout`), and
`wide-model` (no counts) only when `PreserveWideLayout` also parses the model of the wide layout first.
`class-schema` covers reading the file's own class schema, its element table records and the episode table;
`r22-layout` is the rewrite that follows, which can take as long as the phases around it.
`Completed` and `Total` count partition streams (`partitions`), the streams the rewrite reads (`r22-layout`), data chunks scanned
for records or names (`element-records`, `element-properties`), block scan and recovery passes
(`element-blocks`, `recover-geometry`), decoded stream bytes
(`parameter-stream`) or element candidates processed (`build-model`).
`Completed` does not decrease within a phase.
`GeometryBlocks` optionally gives the applied and queued geometry blocks within the current block pass. Such updates
repeat the pass number while the block count advances. Geometry recovery passes cover decoding blocks from the main
walk, locating additional blocks, decoding those blocks, and recovering planar wall bodies.
A zero total means the reader has no work count for that update. Counts describe units within one phase. `complete`
(1 of 1) is the last update of a read.

`CompactJsonExporter.Export` passes `Progress` to its first read only. A file of a wide layout is then read a second time
for solid quantities; that read reports the single update `quantities` after the first read's `complete`, and the JSON
writing that follows reports nothing.

## Model

`Element`, `Parameter`, `Definition`, `ParameterDefinition`, `StorageType`, `RevitFileInfo`, `FileMetadata`, `ProjectInfo`,
`TransmissionInfo`, `ExternalFileRef`, `ClassSchema`, `ClassField`, `MaterialLayer` and the geometry types (`GeometryData`,
`GeoFace`, `GeoEdge`, `GeoSolid`, `GeoBrepSolid`, `GeoOpening`, `GeoInstance`, `LocationCurve`, `DimensionData`,
`InstanceTransform`) are the data a read returns. `ParameterListConverter` is the `System.Text.Json` converter that writes
the parameter lists of a `RevitFileInfo`: full JSON keeps every `Parameter` entry (`Parameter.HasValue` is `Value != null`),
including default slots synthesized for API parity, while `CompactJsonExporter`, `IfcExporter` and `RvtSceneReader` leave out
parameters that carry no value in Revit (`Parameter.HasRevitValue` is `false` for `Double` with `|v| >= 1e29` or non-finite,
unresolved `ElementId` sentinels `-1`, uninitialized `0` without a resolved id-0 target, or non-positive pointers on unhosted host-id parameters, `-1` placeholders on
non-integer built-in parameters, unstored reader placeholders and `Parameter.IsApproximate` estimates; stored `0`, `0.0`,
`false`, `""`, valid resolved id-0 references such as project phase `0`, and valid negative `ElementId` option or category values are kept).

Compact JSON formats numeric user parameters using the specification's document format options, unit conversion,
accuracy, symbol and suppression settings. If the document settings, specification's format or required unit/symbol
data cannot be recovered, the numeric property is omitted. This does not change raw parameter values in full JSON.
An unavailable format does not mean zero or an unset value: a stored zero is formatted normally when its format is
known, while a known unset bound user parameter is written as an empty string. A missing family value record is omitted.

`Element.ExtensibleStorage` gives the public Extensible Storage entities decoded from the element's own cell, including
files with 32-bit element IDs. Full JSON includes the same data. Each entity has a schema GUID, a schema name and a field dictionary.
Values can include nested entities, arrays, or maps represented as lists of key/value pairs. Integers outside the exact
JavaScript integer range are decimal strings. An entity is omitted if any of its values cannot be recovered completely.
The field dictionary is separate from Revit's normal parameter list.

`RevitFileInfo.EmbeddedFamilyExtensibleStorage` lists the same data for elements in loaded family documents.
Each entry identifies its owner by the saved content-key GUID and the exact decimal element ID in that block.
Element ID fields inside an entity use the IDs saved in that document. Opening an RFA directly exposes its own data through
`Element.ExtensibleStorage`.

`Element.BoundingBox` is the axis-aligned outline stored in the element header, as the Revit API bounding box (null for elements with no stored outline, such as grids and levels). The tight box of the decoded geometry is `Element.Geometry.BoundingBox`; before 0.3.0 `Element.BoundingBox` held that decoded value, which also changes the result of `FilteredElementCollector.WhereBoundingBoxIsNotNull`.

`Element.ViewportViewId` and `Element.ViewportOffset` are the view a viewport displays and the offset of that view
on the sheet (feet), decoded from the viewport's own block.

`Element.Text`, `Element.TextPosition` and `Element.TextFontSizePoints` are the plain text of a text note, the
position the file stores for it (feet) and the font size its RTF declares; they are null for every other element.

`Element.ViewportScale` is the view scale (the denominator) decoded from a viewport record; `Element.ViewportViewId` and `Element.ViewportOffset` are what that record places on the sheet. `Element.OwnerViewId` is the stored `m_ownerDBViewId` of the element: the view or sheet whose annotations and view-specific graphics hold it (a text note on a sheet names the sheet), null when no view owns it. `SheetCatalog` uses it for the elements a sheet owns.

In a non-workshared Revit 2027 file, the default workset is represented by 0 in `Element.WorksetId`
and by -1 in the built-in `ELEM_PARTITION_PARAM` value. A property set element has a `WorksetId`
and omits `ELEM_PARTITION_PARAM` in these files. The schema-driven reader uses the stored header
category for Revit 2027 elements with a decoded category.

For Revit 2027, the reader follows the stored links from stair runs and landings to their stair's
type. Elevation views without a direct type use their linked elevation marker's type when the
view and arrow point to each other.

The Revit 2027 reader recovers the `Type Name` of tile patterns from the owned pattern and
uses the built-in English labels for conceptual constructions, site data and internal stair symbols.
It derives `Area` in square feet from the linked filled-region sketch when it contains one closed
line loop and `Volume` in cubic feet from the stored dimensions and drive line of a straight
rectangular duct.
For supported Revit 2027 categories the reader also measures closed bodies from the project's
saved geometry, including geometry reused at rigid placements. Mullion and lighting fixture area is
half the summed solid surface area. Lighting fixture quantities include the closed bodies saved for
different viewing directions at Coarse detail. Curtain panel area is the largest planar face.
Straight basic wall area is the larger net area of its two finish sides. Straight curtain wall
area comes from the stored closed wall profile.

The reader integrates supported circular and straight profiles of revolution along the saved trim
loops. Pipe fitting volume uses the detailed solids and excludes coarse proxies when their complete
source faces are reused. Supported MEP center lines get quantities from their linked physical owner.

The reader measures horizontal floors, ceilings, and foundations at their saved cap faces, with trim
loops applied. Door area uses the coarse source bodies for ordinary wall-hosted doors and the opening
spanned by the rectangular leaves for curtain wall doors. For 2D components the reader measures the
filled faces of the instance's geometry symbol, including stretched variants. Filled regions use the
area of their own loops.

Duct fitting volume integrates ruled faces with a collapsed end and excludes complete reused coarse
bodies. For the supported trimmed structural framing, volume uses the closed body retained by the cut.
For supported single-body masses, area comes from matching horizontal caps or from the planar faces
of a body with a horizontal base and no horizontal top.

The schema-driven reader uses the owned compound structure of host types to populate `Element.Layers` in stored
order. It also resolves `Element.TypeId` for decoded reinforcement when the stored target is a rebar bar type.

## Built-in identifiers

`BuiltInCategory`, `BuiltInParameter`, `BuiltInParameterGroup`, `ParameterType`, `UnitType`, `DisplayUnitType`,
`UnitSymbolType`, `UnitGroup`, `MEPSystemClassification`, `FlowDirectionType`, `PipeFlowConfigurationType` and their
extension and label helpers carry the ids and names stored in the file format.

## Export

| Type | Use |
|---|---|
| `IfcExporter` | Writes IFC from a `RevitFileInfo` (`Export`). Properties: `Profile`, `BaseQuantities`, `OpeningQuantitiesSwapped` (true writes the legacy Width and Height of door and window openings; default false), `OpeningQuantitiesInHostAxes` (the opposite of it, kept for compatibility), `ExportPhaseId`, `ExportAllPhases`, `RoomExportPhaseId`, `ExportRoomsOfAllPhases`, `RevitParameterGroups`, `LengthUnit`, `Timestamp`, `SkipLog`, `ExportedProducts`, `ExportedElementQualities`, `DiagnosticOutput`. |
| `IfcProfile` | `Legacy` or `Current` (the default) output profile. |
| `IfcUnitFamily` | Set `IfcExporter.LengthUnit` to `Millimetre`, `Centimetre`, `Metre`, `Inch` or `Foot`. `Centimetre` and `Inch` are explicit overrides only. `Inch` declares inch-based area and volume units; `Centimetre` declares the length as `CENTI` `METRE` and the area and volume as the conversion-based units `SQUARE CENTIMETRE` (factor 0.0001) and `CUBIC CENTIMETRE` (factor 0.000001) of the square and cubic metre, and writes square and cubic centimetres. Without an override, the current profile maps the stored project format to millimetres, metres or feet as before; the legacy profile defaults to millimetres. |
| `CompactJsonExporter` | `Export(rvtPath, outputPath, readerOptions, options)` reads a file and writes the compact JSON. |
| `CompactJsonExportOptions` | `JsonClassNamePrefix`: text put in front of every class name in the `name` field. |

**Compact JSON unit policy.** `IfcExporter.LengthUnit` and `--ifc-length-unit` never affect JSON. Any future compact JSON
override must be a separate opt-in setting. Convert only values with a resolved length, area or volume specification
from their raw stored value and identify the resulting unit; do not parse or relabel the project's formatted strings.
Values with unknown specs or no stored value keep their existing behavior. Other specs and full JSON are out of scope.
No compact JSON override is implemented.

## Sheets and viewports

| Type | Use |
|---|---|
| `SheetCatalog` | `Extract(document)` (or a `RevitFileInfo`) returns the sheets of the file, the viewports placed on them and the limits of this release: `Sheets`, `Viewports` and `Issues`. Nothing is read from the file again. |
| `SheetRecord` | A sheet: `ElementId`, `Number`, `Name`, `IssueDate`, `Scale`, `DrawnBy`, `DesignedBy`, `CheckedBy`, `ApprovedBy`, `ViewportIds`, `OwnedElementIds`, `Parameters` (text/whole-number project/shared params; Yes/No params read as `Yes`/`No`) and `YesNoParameters` (their names). |
| `ViewportRecord` | A placed view: `ElementId`, `SheetId` and `DetailNumber`; `DisplayedViewId` is null until the viewer element is decoded. |
| `SheetIssue`, `SheetIssueKind` | What this release does not decode: page size, view placement, displayed view, view content and crop region. |
| `SheetFilter` | A `--where` filter (`NAME=VALUE`, `!=`, `~`, `*`/`?` wildcards; `true`/`false`/`1`/`0` aliases apply only to Yes/No params); `SheetCatalog.FromFiltered` keeps matching sheets. |

See [sheets and viewports](sheets.md) for what is decoded, what the limits are and why no page coordinate is exposed yet.

## Printing

| Type | Use |
|---|---|
| `PlanPrinter` | `Levels(document)` lists the levels a plan can be printed for; `Render(document, levelId, scale, options)` returns a `PdfDocument` with the plan of one level: walls with their stored width, column footprints, clipped grids. |
| `PlanPrintOptions` | `PaperSize` (null picks the smallest paper that holds the drawing), `MarginMillimetres`, `FallbackWallWidthMillimetres`, `DrawFrame`. |
| `PlanLevel`, `PlanView` | A printable level (id, name, elevation) and a printable plan view (id, name, level, scale). |
| `ViewPrinter` | `Render(document, viewIds, options)` prints views into one PDF document, one page per view: a plan, an elevation or a section (`PlanPrinter.Views`, `ElevationPrinter.Views`), a 3D view (an isometric massing page) and a rendering (its image page). `OtherViews(document)` lists the 3D views and the renderings. |
| `ElevationPrinter` | `Views(document)` lists the elevations and the building sections (North, South, East, West, Section, Stair Section) with their scale; `Collect(document, direction, stairsOnly, planeView)` projects the model faces on the plane of the view when the reader decoded it (a section is cut at that plane: the geometry in front of it is removed and the crossing polygons are clipped) or on the plane of the direction taken from the name; a stair section projects the stairs alone. `DrawContent` writes them. |
| `ElevationView` | An elevation view: id, name, direction (north, south, east, west, section, iso) and scale. |
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
| `PdfPage` | `DrawLine`, `DrawPolyline`, `DrawRectangle`, `DrawText` and `DrawImage` in millimetres from the lower-left corner, y up. |
| `IPdfImage`, `PdfRasterImage`, `PdfJpegImage` | An image the writer embeds: RGB pixels (`PdfRasterImage`, Flate) or a JPEG as it is (`PdfJpegImage`, DCTDecode, size and components read from its header). |
| `PdfPageSize` | Paper sizes A4 to A0 in millimetres, `Landscape` for the turned page. |
| `PdfTextAlignment` | `Left`, `Center`, `Right`: where the text anchor sits. |
| `PdfTrueTypeFont` | A TrueType font parsed for embedding (`UnitsPerEm`, `GlyphOf`, `AdvanceOf`), drawn as a Type0 font with Identity-H encoding and a ToUnicode map, so text in the scripts it covers (Cyrillic included) is real, selectable PDF text. |
| `PdfFonts` | `TryFind` loads the first TrueType font found on the machine (Arial, its metric-compatible substitutes Liberation Sans and Arimo, Segoe UI, Tahoma, DejaVu Sans, Helvetica Neue); `RVTIO_FONTS` names extra folders searched first. |

Without `PdfDocument.Font` the writer uses the standard Helvetica font, which holds printable ASCII only, and a text with
another character is refused. With it, every text is written with the embedded font.

## Triangle meshes

| Type | Use |
|---|---|
| `ElementMeshes` | `Extract(document, options)` returns the triangle meshes of the drawable model elements of an opened `RvtDocument`, from the geometry the document already holds (the file is not read again). Coordinates are in feet, in the coordinates of the document (Revit internal coordinates, Z up). |
| `ElementMeshOptions` | `UseSymbolPlacements` (default true): an element that is exactly its family symbols placed rigidly is returned as placements of shared symbol meshes; false gives every element its own triangles. `IncludeModelLinesAndReinforcement` (default false) adds straight model lines and triangulated reinforcement where decoded bar paths supply the geometry. |
| `MeshSet` | `Elements` (one `ElementMesh` per drawable element, in document order), `Symbols` (symbol element id to its triangle meshes in the frame of the family, only for placed symbols) and `GeometryUndecoded` (a file of a release other than Revit 2022, read by its own class schema, opened without `RvtReaderOptions.R22Layout`: its elements are listed as `Undecoded`). |
| `ElementMesh` | `ElementId`, `Meshes` (the element's own triangles, one mesh per material), `Placements`, `Coverage`, `UntriangulatedFaces` and optional `Line` endpoints in feet. |
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
| `RvtWriter` | Inspects write capabilities (`InspectCapabilities`), writes lossless copies (`SaveAs`), and persists targeted stream and partition-record edits (`Rewrite`). For supported parameters in local Revit 2020–2027 files it provides single assignments (`SetParameter`, `SetFamilyParameter`) and atomic batches (`SetParameters`, `SetFamilyParameters`). See [writing and save-as](writing.md) for edit records, `long` IDs and supported clears. |
| `RvtWriteCapabilities`, `RvtOperationSupport`, `RvtFileKind`, `RvtWriteOperation` | Capability classification by file kind (`Project`, `Family`), release (`2020` to `2027`), element ID width (`4` or `8` bytes), worksharing/cloud flags, and operation (`SaveAs`, `PagedStreamRewrite`, `PartitionRecordRewrite`). |
| `RvtWriteRequest`, `RvtPagedStreamEdit`, `RvtPartitionRecordEdit`, `RvtWriteResult` | Edit specifications for `Formats/Latest`, `Global/*`, and `Partitions/<n>` records, and the verified write summary. |
| `RvtParameterEdit`, `RvtFamilyParameterEdit` | One entry of a `SetParameters` batch: the element ID, parameter ID and value (`null` clears), or the family type name, parameter ID and value for `SetFamilyParameters`. |
| `RvtWriteRefusedException` | Thrown with a stable `Kind` code when a file state or edit request is outside the supported write scope. |

## Diagnostics

The library never writes to the console. A diagnostic message is written only when the caller gives a `TextWriter`:
`RvtReaderOptions.DiagnosticOutput` for a read (and for `CompactJsonExporter.Export`), `IfcExporter.DiagnosticOutput` (or the
constructor argument) for an IFC export. The default is `null`, which is silent. The writer is scoped to the call that
received it, so concurrent calls with different writers do not mix.

The `rvtio` command line tool sets the writer to standard error.
