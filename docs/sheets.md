# Sheets and viewports

`SheetCatalog` reads the sheets of a file that a read has already decoded, the viewports placed on them and the
elements each sheet owns. It is the first slice of the sheet presentation model of [issue #17](https://github.com/IOExtend/RvtIO/issues/17):
identity and links now, page geometry and 2D content later, with every missing piece named in `SheetCatalog.Issues`.

## Use

```csharp
using RvtIO;
using RvtIO.Sheets;

var document = RvtDocument.Open("project.rvt");
var catalog = SheetCatalog.Extract(document);

foreach (var sheet in catalog.Sheets)
{
    Console.WriteLine($"{sheet.Number} {sheet.Name}: {sheet.ViewportIds.Count} viewports, " +
                      $"{sheet.OwnedElementIds.Count} owned elements");
}

foreach (var issue in catalog.Issues)
    Console.WriteLine($"{issue.Kind}: {issue.Message}");
```

`SheetCatalog.Extract` reads nothing from the file again: it works on the elements of an opened `RvtDocument` or of a
`RevitFileInfo` a read returned. The command line tool writes the same data as JSON:

```sh
rvtio sheets project.rvt sheets.json
```

With no output path the JSON goes to standard output; diagnostics go to standard error. Exit code 0 means the catalog
was written, 1 a usage error or an output path that cannot be written, 2 a file that cannot be read, 3 a reader limit
that cut a stream (the codes of [the export report](export-report.md)).

Each `SheetRecord` also carries `Parameters`: the text and whole-number project and shared parameters
of the sheet as display strings (numbers with units stay out until their units are read). A Yes/No parameter
(`Appears In Sheet List` and project/shared parameters of Yes/No kind) reads as `Yes`/`No`; its names are also
listed in `SheetRecord.YesNoParameters`. The port follows rvt-prt's `list --params` (Unreleased after 0.1.0).

```sh
rvtio sheets project.rvt --where "Appears In Sheet List=Yes" --where "Sheet Group=Architectural"
rvtio sheets project.rvt sheets.csv --csv --where "Sheet Name~plan" --where "Sheet Number!=A1*"
rvtio sheets project.rvt --params --where "Drawn By=AB"
```

`--where NAME=VALUE` (also `NAME!=VALUE` and `NAME~TEXT` for contains) keeps only the sheets whose parameters
match, as in rvt-prt. `*` stands for any text and `?` for one character; names and values compare
case-insensitively. On a Yes/No parameter `true`, `false`, `1` and `0` mean the same as `Yes`/`No`;
every other field compares literally, so a sheet number `1` never matches `=true`. A parameter a sheet has no
value for is empty. Repeat a filter for several: all must hold. A name no sheet carries stops with the names there
are (exit code 1), as does a filter nothing matches. `--params` lists each kept sheet's parameters on standard
error, `--csv` writes them as CSV (one row per sheet, one column per parameter), `--json` keeps the default JSON.

## What a sheet is

A sheet is an element that carries the `Sheet Number` or `Sheet Name` parameter. The reader creates those parameters
only for the sheet class of the file (class 995 of the Revit 2022 schema), which is also the only class of that schema
with a `m_sheetNumber` field. The `OST_Sheets` category is not a test for a sheet: a project also holds thousands of
elements whose category is `OST_Sheets` because a block whose class word is 0x03E3 takes that category, and those
elements have neither parameter. In one architectural sample the full JSON dump has 14,354 elements of that category
and 100 sheets.

A viewport is an element that carries the `Detail Number` parameter and references a sheet; its sheet is the first
element it references that is a sheet. A viewport that references no sheet is left out.

The elements a sheet owns are those whose `Element.OwnerViewId` is the sheet element id. `OwnerViewId` is the stored
`m_ownerDBViewId` of the element, exposed in this release: a text note placed on a sheet names the sheet, a detail line
of a plan names the plan, and an element with no owner names the project (-1). It is null when the reader decoded no
owner, when the stored owner is not a view, or when it is the element itself.

## What is decoded

| Field | Source |
|---|---|
| `SheetRecord.Number` | `Sheet Number` parameter (`SHEET_NUMBER`) |
| `SheetRecord.Name` | `Sheet Name` parameter (`SHEET_NAME`) |
| `SheetRecord.IssueDate` | `Sheet Issue Date` parameter (`SHEET_ISSUE_DATE`) |
| `SheetRecord.Scale` | `View Scale` parameter (`VIEW_SCALE`), the denominator of 1:N |
| `SheetRecord.DrawnBy`, `DesignedBy`, `CheckedBy`, `ApprovedBy` | the `SHEET_*` by-line parameters |
| `SheetRecord.ViewportIds` | the viewports whose sheet is this sheet |
| `SheetRecord.OwnedElementIds` | elements whose `OwnerViewId` is this sheet |
| `SheetRecord.Parameters` | text and whole-number project/shared parameters; `SHEET_SCHEDULED` as `Appears In Sheet List` |
| `ViewportRecord.SheetId` | the referenced sheet |
| `ViewportRecord.DetailNumber` | `Detail Number` parameter (`VIEWPORT_DETAIL_NUMBER`) |

Sheets are ordered by number (ordinal, case-insensitive; a sheet without a number keeps document order behind the
numbered sheets) and then by element id. Viewports, viewport ids and owned element ids are ordered by id.

A project can store `???` as a sheet number or name; the catalog returns it as stored. An empty value is reported as
null.

## What is not decoded

`SheetCatalog.Issues` lists every limit of this release, one entry per kind (`SheetIssueKind`). A downstream exporter
reads them to mark a page incomplete instead of drawing it wrongly.

| Kind | Limit |
|---|---|
| `PageSizeNotDecoded` | A sheet stores no page width, height or orientation; Revit keeps them in the title block family, which the reader does not decode. |
| `ViewPlacementNotDecoded` | A viewport stores only the origin of its view title label (the `m_labelOrigin` field of the viewport block); the position and rotation of the view on the sheet are not decoded, so no placed view has a sheet coordinate. |
| `DisplayedViewNotDecoded` | The view a viewport displays is reached through the sheet's viewer element (schema class 621), which the reader does not decode; `ViewportRecord.DisplayedViewId` is null. |
| `ViewContentNotDecoded` | No 2D linework, text, dimension, tag, image or schedule content of a sheet or drawing view is decoded. |
| `CropRegionNotDecoded` | The crop region of a drawing view is not decoded. |

Because of these limits the catalog exposes no coordinate, size or unit: there is no decoded sheet coordinate to give
one. The catalog is a list of pages with identity and links, not a printable layout yet.

## Which files expose sheets

The catalog finds the sheets for which the reader maps the sheet parameters. In the samples available while this
feature was written, two Revit 2022 projects gave 100 and 17 sheets. A Revit 2024 and a Revit 2025 save of the same
projects gave none: the reader reads those files by their own class schema, does not map the sheet class
(DBViewDrafting of the file's schema) and therefore creates no sheet parameter; the elements it returns carry neither
the `OST_Sheets` category nor any viewport reference. The command line tool says so on standard error and writes an
empty catalog. Covering the view and sheet classes of those releases is future work, as are page size and view
placement (see the issues above).

`--r22-layout` reads the file in the Revit 2022 layout, as the IFC export does, for a file whose own class schema
the reader would otherwise use.

## Limits of the identification

- A file can hold several copies of one sheet id (a project saved in several increments) or sheet objects of an
  embedded document. The catalog lists every element that carries the sheet parameters, as the reader returns them;
  the public API does not choose a "current" copy for sheets.
- A sheet whose number is empty cannot be ordered by number; it follows the numbered sheets in document order.
