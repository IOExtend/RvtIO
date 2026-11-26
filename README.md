<p align="center">
  <img src="docs/images/rvtio-banner.png" alt="RvtIO" width="640">
</p>

RvtIO is a .NET library and command line tool that reads Autodesk Revit projects (`.rvt`) and families (`.rfa`) directly from disk, without Revit or the Revit API, and exports IFC and JSON.

## What it does

- Reads elements, parameters, geometry, file metadata and project information.
- Queries elements through `FilteredElementCollector`.
- Exports IFC2X3 coordination view (default) and IFC4 (ISO 16739-1:2018 / ADD2 TC1; `ReferenceView_V1.2` and `DesignTransferView_V1.0`), with shared JSON/API configuration presets and optional Revit parameters as property sets.
- Exports binary glTF 2.0 (`.glb`) directly via `GlbExporter` / `--glb` in Y-up metres with shared family symbol meshes, PBR colors/transparency, and element/material metadata in `extras`.
- Writes compact JSON or a full JSON dump with every parameter and geometry.
- Returns the triangle meshes of the model elements, with one shared mesh per family symbol for repeated instances (`ElementMeshes`).
- Lists the sheets of a file and the viewports placed on them (`SheetCatalog`), with the sheet number, name, issue date, by-lines, the viewports of each sheet and the elements each sheet owns. Page size, view placement and 2D content are not decoded yet; the catalog reports each limit.
- Prints the plan of a level to PDF (`rvtio print`, `PlanPrinter`): walls with their stored width, column footprints and grids, with a PDF writer built in. Openings, floors, text and the sheet layout are the next part of the printing work.

## Install

The packages are published on nuget.org, not on GitHub Packages, so the Packages box of this repository stays empty.

[![RvtIO on NuGet](https://img.shields.io/nuget/v/RvtIO?label=RvtIO)](https://www.nuget.org/packages/RvtIO) [![RvtIO.Cli on NuGet](https://img.shields.io/nuget/v/RvtIO.Cli?label=RvtIO.Cli)](https://www.nuget.org/packages/RvtIO.Cli)

Install the command line tool:

```sh
dotnet tool install --global RvtIO.Cli
```

Add the library to a .NET project:

```sh
dotnet add package RvtIO
```

`RvtIO.Scene` (a separate package released with RvtIO) turns a Revit project into an `IOScene` scene document for
viewers; see the [scene adapter](docs/scene.md) for usage and limits.

The optional [RvtIO.Nwc tool](docs/rvt-to-nwc.md) exports Revit projects to NWC with a managed writer.
Download the ready-to-run tool from [RvtIO releases](https://github.com/IOExtend/RvtIO/releases/latest).

The library targets .NET 8 and .NET 10. It uses managed code without native dependencies or Windows-only APIs and is tested on Windows, Linux and macOS.

## Command line

Export a project to IFC:

```sh
rvtio project.rvt project.ifc
```

Export compact JSON:

```sh
rvtio project.rvt project.compact.json --compact-json
```

Export full JSON, including every parameter and geometry:

```sh
rvtio project.rvt project.full.json --json-full
```

Export IFC with Revit parameters as property sets:

```sh
rvtio project.rvt project.parameters.ifc --ifc-revit-parameters
```

Export IFC4 (`ReferenceView_V1.2` by default, or `DesignTransferView_V1.0`) independently of `Current`/`Legacy` profiles, or drive exports through a shared JSON configuration preset (`--ifc-preset`, `--ifc-config`, `--ifc-save-config`, `--ifc-capabilities`):

```sh
rvtio project.rvt project-rv12.ifc --ifc-schema ifc4
rvtio project.rvt project-dtv.ifc --ifc-schema ifc4 --ifc-mvd design-transfer-view-1.0 --ifc-profile legacy
rvtio --ifc-capabilities
```

See [IFC export and configuration](docs/ifc-export.md) for the exact IFC4 edition details, supported MVD exchange scenarios, precedence rules, validation checks, and capability matrix.

Export a project to binary glTF 2.0 (GLB):

```sh
rvtio project.rvt project.glb
```

Export multiple formats in a single read:

```sh
rvtio project.rvt --ifc project.ifc --glb project.glb --compact-json project.compact.json --scene project.scene.json
```

A `.ifc` output path selects IFC and a `.glb` output path selects binary glTF 2.0 (GLB). Any other output path selects JSON, which is compact by default. When `--ifc`, `--glb`, `--compact-json`, `--full-json`, or `--scene` are specified, the model is read once and each requested format is exported sequentially. Positional output arguments remain fully backward compatible. Single-output and multi-output exports produce byte-identical files. On Revit 2023+ (own-schema) models, JSON formats retain their wide-schema representation by default (use `--r22-layout` to export JSON in Revit 2022 layout too), while IFC, GLB, and Scene exports use the Revit 2022 geometry layout, sharing decoded geometry and quantities without reading the model again. If no output path is given, the tool writes `output.json`. Diagnostics go to standard error. See [GLB export](docs/glb-export.md).

List the sheets and the viewports placed on them:

```sh
rvtio sheets project.rvt sheets.json
```

The catalog has the sheet number, name, issue date, by-lines and scale, the viewports of each sheet and the elements each sheet owns, plus the text and whole-number project/shared parameters of each sheet (`--params`, `--csv`, `--where NAME=VALUE` with `!=`/`~` and `*`/`?` wildcards). Page size, view placement, the displayed view and 2D content are not decoded; every limit is reported in the catalog itself. See [sheets and viewports](docs/sheets.md).

Print the plan of a level to PDF — walls with their stored width, column footprints and grids, on the smallest paper that holds the drawing at the scale:

```sh
rvtio print project.rvt plan.pdf --level "Уровень 1" --scale 100
```

`--list-levels` prints the levels that can be printed. A sheet prints from its decoded linework (page border, title block frame, rules) on the paper it spans:

```sh
rvtio print project.rvt sheets.pdf --sheet 516334
rvtio print project.rvt sheets.pdf --all-sheets --where "Appears In Sheet List=Yes"
rvtio print project.rvt --list-sheets
```

Openings cut the walls; floors, annotations and the text of a title block are not drawn yet; see [printing a plan](docs/printing.md).

Export the state of one project phase:

```sh
rvtio project.rvt project.ifc --ifc-phase last
rvtio project.rvt project.ifc --ifc-phase 12345
rvtio project.rvt project.ifc --ifc-phase all
```

An element is left out of the IFC when it is demolished in the export phase or an earlier one, or created in a later one. Without the option the current profile exports the last project phase and the legacy profile every phase; `all` keeps every element and room, and an id selects that phase (it must occur in the project's phase order, or the tool exits with code 1). Rooms follow the export phase unless `--ifc-room-phase` names another one. The compact and full JSON are not filtered. In the library the same choices are `IfcExporter.ExportPhaseId` and `IfcExporter.ExportAllPhases`.

By default the current IFC profile maps the project length format to millimetres, metres (also for "metres and centimetres") or feet (also for feet and fractional inches and for US survey feet). Other or unreadable project length formats fall back to millimetres; diagnostics name the stored format or the reason. The legacy profile defaults to millimetres. Use `--ifc-length-unit` (`IfcExporter.LengthUnit` in the library) to select `mm`, `cm`, `m`, `in` or `ft` explicitly:

```sh
rvtio project.rvt project.ifc --ifc-length-unit m
rvtio project.rvt project.ifc --ifc-length-unit cm
rvtio project.rvt project.ifc --ifc-length-unit in
```

IFC coordinates, geometry and quantities use the declared units: `cm` writes centimetres, `in` inches and `ft` feet, with areas and volumes in the squares and cubes of that unit. `cm`, `in` and `ft` declare them as conversion-based units of the square and cubic metre (`SQUARE CENTIMETRE` with the factor 0.0001, `CUBIC CENTIMETRE` with 0.000001, `SQUARE INCH`, `CUBIC FOOT` and so on), so a reader needs no rule for prefixes of area and volume units; the length of `cm` is `CENTI` `METRE`. The existing `mm` and `m` families both keep square and cubic metres. Unknown unit names exit with code 1. The IFC choice does not affect JSON, including multi-output exports. Compact JSON uses the project's display formatting; full JSON keeps Revit's internal units (feet, square feet, cubic feet, radians).

The Width and Height quantities of the opening of a door or window are those of the door or window as Revit has them: for a door 0.8 wide and 2.1 high the opening gets Width 0.8 (along the wall) and Height 2.1 (vertical), and the Depth is the thickness of the wall, in a wall of any direction. The Width and Height are the instance values, else the type values; a side the door or window holds no value for is measured from the opening, vertical for Height and along the wall for Width. Where the cut loop of the door or window is decoded, the opening is measured by that loop, as Revit cuts it, which can be larger than the nominal size (a door 0.9 by 2.1 with a frame cut of 1.0 by 2.2 gets Width 1.0 and Height 2.2); the nominal size stays only where the cut loop is not decoded. This applies to openings in straight walls. Unfilled openings in straight walls are measured from their final extrusion in the same wall frame: Width along the wall, Height vertical and Depth across it. The openings of curved walls and curtain walls and openings of other fillers are measured as before. Releases before this one named the sides of the cut loop by the frame they are measured in, which gave the same door Height 0.8 and Width 2.1. To write those values, restore the earlier reading:

```sh
rvtio project.rvt project.ifc --ifc-opening-legacy-axes
```

`--ifc-opening-host-axes`, the opt-in of the earlier release for the new reading, is still accepted and does nothing. In the library the option is `IfcExporter.OpeningQuantitiesSwapped`; `IfcExporter.OpeningQuantitiesInHostAxes` is kept and is the opposite of it.

Use `--ifc-timestamp <timestamp>` (an ISO-8601 timestamp or Unix epoch seconds; `IfcExporter.Timestamp` in the library) to write a fixed UTC timestamp into the STEP `FILE_NAME` header so repeated exports of the same input are byte-for-byte identical. Use `--ifc-revit-parameter-groups` for one property set per parameter group. In IFC (`--ifc-revit-parameters`, `--ifc-revit-parameter-groups`, and named property sets) and in `RvtIO.Scene`, parameters that carry no value in Revit (`!Parameter.HasRevitValue`) are left out: `Double` sentinels (`|v| >= 1e29` or non-finite), unresolved `ElementId` values (`-1`, uninitialized `0` without a resolved id-0 target, and non-positive pointers on unhosted host-id parameters, while valid resolved id-0 references such as project phase `0` and valid negative `ElementId` option and category values are kept), unstored reader placeholders (`0`, `0.0`, `""`), and `IsApproximate` estimates, while stored `0`, `0.0`, `false` and `""` are written. Compact JSON applies its own schedule-matching value gate; `--json-full` keeps every `Parameter` entry on `RevitFileInfo` (`Parameter.HasValue` remains `Value != null`).

A file saved in several increments can hold an element in more than one partition stream; the tool keeps only its current copy (every copy is kept when the file's increment tables cannot be decoded). Use `--keep-all-copies` to decode every copy instead (the earlier behavior; `--resolve-current-copies` is still accepted and does nothing). Run `rvtio --help` for all options. Unknown options exit with code 1; options starting with `--dev-` are not a stable interface.

### Exit codes and the export report

| Code | Meaning |
| --- | --- |
| 0 | The export was written. |
| 1 | The command line is wrong. |
| 2 | The input cannot be read: it is missing, empty, not a compound file, or cut or damaged. One line on standard error, no stack trace. |
| 3 | A stream exceeded a limit of the reader and the read was cut. Nothing is exported unless `--allow-partial` is given. |
| 4 | The file is readable but a decoder failed on its content. |
| 5 | `downgrade --check` refused the request, for example because the source is newer than release 2026, the profile is not older than the source, the saving release of a file does not match its class table, or a class name occurs twice in a table. One line on standard error. |
| 6 | `--strict` was given and the read was partial without hitting a limit (a member failed to decode, was rebuilt by structural recovery, or decoded without a matching checksum, or the element table decoded no row). Nothing is exported; the report lists the streams and the reason. |

`--report project.report.json` writes a machine-readable report: the fraction of each partition stream and of the element table that decoded, the streams cut by a limit, the elements the export left out with their class and reason, and the exported fraction. `--strict` makes partial reads that did not hit a limit fail with exit code 6 instead of exporting with exit code 0. `--raise-limits` raises the limits for a very large model. A complete export without these options behaves as before. The report format and the limits are described in [the export report](docs/export-report.md).

### Plan a downgrade to an older release (experimental)

```powershell
rvtio downgrade project2026.rvt --target-profile project2025.rvt --check --report downgrade.json
```

`downgrade --check` compares the class table of a project (the stream `Formats/Latest`) with the class table of a project or family saved by an older release, the profile, and reports what editing the first table to fit the second would change. It writes nothing but the report. Nothing is embedded in RvtIO: the profile is read from the file you give. The report lists:

- the classes only the source defines, with the number of elements the file holds in each (counted from the partition streams); in an earlier trial with one 2026 sample project edited by hand and opened in Revit 2025, the 64 elements of such classes were not in the file that Revit 2025 then saved;
- the classes only the profile defines;
- the classes whose version is higher in the source (the version is lowered to the profile's);
- per class, the stored fields that exist in one table only, matched by name (the command only lists them; what an older release does with them was compared in that one trial only, where one element out of 13872 kept in both files differed in the values of a property);
- the classes whose list of upgrade identifiers changes, with the number added and removed. `--guid-rule exact` (the default) makes the list the profile's list of the same class; `--guid-rule minimal` only removes the identifiers the profile does not list. A list is empty after the edit only when the rule yields no identifier for that class.

Limits: the plan is computed from the bytes of the two files. This version cannot write a converted file, and no application opened a converted file as part of this change. The tested source release is 2026; a source of a newer release, a source whose saving release differs from the release of its class table, and a profile that is not older than the source are refused with exit code 5 and one line on standard error. A file that cannot be read, or whose partition stream is damaged, exits with code 2; a wrong command line, including an empty or unusable path, exits with code 1. Without `--check` the command refuses to run.

### Measure how much of a file is decoded

```powershell
rvtio coverage project.rvt --report coverage.json
```

`coverage` counts, per channel and class, the records of the partition streams that the class schema of the file walks to their last byte, and lists every stream of the file. It writes nothing but the report. `--top <n>` sets how many classes with records that are not walked to the end are printed (default 15, `0` for none). The exit code is 0 when the file was measured, 1 for a wrong command line and 2 when the file cannot be read. What is counted, the reasons a walk stops and the report format are described in [the coverage report](docs/coverage-report.md).

## Library

Open a project, list wall instances through `FilteredElementCollector`, and export the document to IFC:

```csharp
using System;
using RvtIO;
using RvtIO.BuiltIns;
using RvtIO.Ifc;

var doc = RvtDocument.Open("project.rvt");

FilteredElementCollector walls = doc.Collector()
    .OfCategory(BuiltInCategory.OST_Walls)
    .WhereElementIsNotElementType();

foreach (var wall in walls)
{
    Console.WriteLine($"{wall.Id}: {wall.Name}");
}

new IfcExporter().Export(doc.RawData, "project.ifc");
```

The library is silent by default. Set `RvtReaderOptions.DiagnosticOutput` for reading or `IfcExporter.DiagnosticOutput` for export to receive diagnostics through a `TextWriter`.

See the [public API](docs/public-api.md) for the supported surface and [dependencies](docs/dependencies.md) for package details.

## Documentation

The documentation site with the API reference is at <https://ioextend.github.io/RvtIO/>. [![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/IOExtend/RvtIO) offers an automatically generated overview of the repository.

## Examples

IFC exports of sample models opened in an IFC viewer:

| Revit 2022 sample project | Revit 2025 sample project exported to IFC |
| --- | --- |
| ![Revit 2022 sample project](docs/images/export-example-2022.png) | ![Revit 2025 sample project exported to IFC](docs/images/export-example-2025.png) |

## Supported files

Reads `.rvt` projects and `.rfa` families. Testing covers sample projects saved by Revit 2020, 2021, 2022, 2023, 2024, 2025 and 2026. Revit 2027 files open with partial support.

The tested projects include basic and advanced architecture, structure and MEP samples, plus several larger multi-discipline models.

The stored class schema determines the decoding layout, including element row and ID widths. The saving year in `BasicFileInfo` is a fallback when the schema release cannot be recognized. Diagnostics report both years when they differ; an older saving year does not override a recognized newer schema.

For projects with wide element IDs, use `Element.ElementId` and `RvtDocument.GetElement(long)` for exact source
identity. `TypeElementId`, `HostElementId`, and `Parameter.AsElementId64()` return the exact IDs of those references.
Existing `int` members use document-local keys. `RevitFileInfo.GetSourceElementId(int)` resolves any such key.
Full JSON records the remapped keys in `SourceElementIds` and writes remapped parameter references as decimal strings.
Compact JSON keeps the exact source ID in the existing `object` string. Scene keeps local numeric links and carries wide
source IDs as text. IFC products with a Tag attribute carry the exact source ID in that attribute.

Wide creation IDs are available as `Element.OriginalElementId`. The Revit UniqueId and IFC GUID derivation used for
32-bit original IDs is not extended to 64-bit originals without a reference from Revit.

## Limitations

- Version 0.4.0 is a preview. The API can change before 1.0.
- Geometry coverage is partial for some element kinds.
- Revit 2027 support is partial.
- Parameter editing supports single assignments and atomic batches of verified assignments and clears in local Revit 2020–2027 projects and standalone families. See the [writing guide](docs/writing.md).
- IFC output supports IFC2X3 coordination view (default) and IFC4 (`ReferenceView_V1.2` and `DesignTransferView_V1.0`); see [IFC export and configuration](docs/ifc-export.md) for declared schema/MVD coverage and unsupported options.
- Output was compared with an independent reference exporter on the tested samples. Byte equality with Revit's own IFC export is not claimed.

## Build from source and tests

Use the .NET SDK selected by `global.json`, which specifies 10.0.100 with `latestFeature` roll-forward.

From the repository root:

```sh
dotnet build RvtIO.slnx -c Release
dotnet test
```

Tests requiring real Revit files are skipped with a message unless `RVTIO_TEST_FILES` points to a folder containing the private fixtures. The repository includes no Revit models.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). Release changes are recorded in [CHANGELOG.md](CHANGELOG.md).

## Security

Report vulnerabilities through GitHub private vulnerability reporting in the repository's Security tab. Do not open public issues for security vulnerabilities. See [SECURITY.md](SECURITY.md).

## License and trademarks

RvtIO is licensed under the [MIT License](LICENSE). Copyright holder: mahach666.

RvtIO is not affiliated with, endorsed by or sponsored by Autodesk. Autodesk and Revit are trademarks of Autodesk, Inc.

The file format is read by analysing files for interoperability. No Autodesk code or binaries are included.
