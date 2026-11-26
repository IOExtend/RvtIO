# Revision Comparison (`RvtIO.Diff` & `rvtio diff`)

`RvtRevisionDiffer` (API) and `rvtio diff` (CLI) compare two Revit model revisions and report element additions, deletions, parameter edits, type changes (with linked affected instances), world-space placement shifts, geometry/layer/sketch modifications, relationship changes, and external transmission link changes.

## Identity and Relatedness Rules

1. **Persistent Identity (`UniqueId`)**: Elements are matched primarily by `Element.UniqueId` (Revit creation-episode GUID + original creation element ID), which stays invariant across saves, renumbering, and Revit file version upgrades.
2. **No Blind Numeric `ElementId` Matching**: Numeric `Element.Id` / `ElementId` alone is never assumed to match elements across unrelated or ambiguous documents. Elements lacking a `UniqueId` are surfaced under `Unavailable` (`reason = "missing-persistent-identity"`) by default; `RvtDiffOptions.AllowElementIdFallbackWhenRelated` (`--allow-id-fallback`) optionally pairs them by unique `(ElementId, CategoryId)` only when both documents are verified as `Related`.
3. **Ambiguous Identity**: If a `UniqueId` appears more than once in either revision, all instances carrying that `UniqueId` are placed in `AmbiguousMatches` (`duplicate-unique-id`) rather than guessed.
4. **Relatedness Detection**: Comparing two documents computes `RvtRevisionRelation`:
   - `Related`: shared persistent `UniqueId`s or matching `SerializedDocumentGuid`.
   - `Unrelated`: distinct document GUIDs and zero shared `UniqueId`s, or disjoint non-empty `UniqueId` sets. A warning is emitted and numeric IDs are never paired.
   - `Indeterminate`: neither side contains persistent document/element identity markers.
5. **Partial Decoding & Unavailable Data**:
   - When `ElemTableIsExact == false` or `Diagnostics.LimitHits` / failed streams exist on a side, missing elements on the incomplete side are reported in `Unavailable` (`partial-decode-prevents-deletion-confirmation` / `partial-decode-prevents-addition-confirmation`) instead of confirmed removals or additions.
   - When one revision has `WideLayoutNotRewritten` / `GeometryUndecoded` (`R22Layout = false` on Revit 2023+ wide schema) and no geometry, missing geometry is marked `RvtDiffAvailability.Unavailable` rather than deleted.
   - When faces were left untriangulated or placed symbols were unresolved, geometry is marked `Partial` even if all decoded faces match. Availability alone does not imply a semantic change.
   - Incomplete parameter stores (`!NativeParameterStoresComplete`) mark missing parameters as unavailable rather than deleted.

## Units, Coordinate Frames, and Tolerances

- **Internal Units**: All numeric parameter values, transforms, location curves, and geometry coordinates are compared in Revit internal units (feet, square feet, cubic feet, radians) regardless of project display unit settings. By default (`IncludeDisplayFormattingDifferences = false`), changes solely to display formatting (`UnitSymbol`, `ConversionFactor`, `AsValueString`) do not trigger false parameter changes.
- **Local vs. World Coordinates**:
  - `LocationCurve`, `InstanceTransform`, `Geometry.Transform`, and `Element.DecodedBox` are compared in document world coordinates (feet).
  - Pure rigid translation of a placed instance reports a `PlacementChange` (`TranslationFeet`, `TranslationDistanceFeet`, `RotationDeltaDegrees`, `MirroredChanged`). Bounding boxes, face origins, and contours are compared after compensating for that translation.
  - Shared `FamilySymbol` geometry stays in family-local coordinates; modifying a symbol/type records a `RvtDiffTypeImpact` linked to every affected placed instance (`AffectedInstances` and `AffectedByTypeIdentityKeys`).
- **Default Tolerances (`RvtDiffOptions`)**:
  - `LinearToleranceFeet`: `1e-4` ft (~`0.03048` mm)
  - `AngleToleranceRadians`: `1e-5` rad (~`0.00057` deg)
  - `AreaToleranceSquareFeet`: `1e-4` ft² (~`9.3` mm²)
  - `NumericParameterTolerance`: `1e-6`

Face and trim-loop order, the start vertex of a contour, and contour winding do not affect the comparison. The geometry diff reports changes to decoded contours and to the position or size of openings in extruded solids. Tolerances must be finite and non-negative.

## Version Upgrades (Revit 2020–2027)

When comparing paths (`RvtRevisionDiffer.Compare(leftPath, rightPath)` or `rvtio diff`), both revisions default to `R22Layout = true` and `ResolveCurrentCopies = true` so geometry, parameters, and persistent `UniqueId`s decode consistently across 32-bit (up to Revit 2023) and 64-bit (Revit 2024+) schemas. Target element references (`ELEM_TYPE_PARAM`, `LEVEL_PARAM`, `HostId`, `Cutters`, `Connectors`) are resolved by target `UniqueId` so internal numeric ID remapping across upgrades produces zero false diffs.

## CLI & API Usage

```sh
rvtio diff rev1.rvt rev2.rvt
rvtio diff rev1.rvt rev2.rvt --json diff.json
rvtio diff rev1.rvt rev2.rvt --category OST_Walls --linear-tol 0.001 --redact
```

`--redact` scrubs recognized personal identifiers and sensitive paths in the report and in read and write diagnostics.

```csharp
var report = RvtRevisionDiffer.Compare("rev1.rvt", "rev2.rvt", new RvtDiffOptions
{
    Categories = ["OST_Walls"],
    LinearToleranceFeet = 1e-4,
});
File.WriteAllText("diff.json", report.ToJson());
```
