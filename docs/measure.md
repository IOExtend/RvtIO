# Reproducible IFC Comparison Against Revit References

RvtIO includes a deterministic, self-verifying pipeline that compares two RvtIO revisions (`base` and `head`) against the same pinned Autodesk Revit reference IFC exports without committing external binaries or models into the repository.

Both local runs and manual CI workflows execute [tools/measure/measure_re1.py](../tools/measure/measure_re1.py) against [tools/measure/re1-manifest.json](../tools/measure/re1-manifest.json) with identical flags, SHA-256 verification, and scoring tolerances.

## Pinned Reference Models (`RE1`)

All initial models come from the public MIT-licensed `Drshelden/IFC-ECS` repository (`data/RE1`) and are fetched on demand from their pinned upstream URLs.

### Models & Checksums

| Model ID | Artifact | Source | RVT Author / Revit Export Settings | Bytes | SHA-256 |
| --- | --- | --- | --- | ---: | --- |
| `re1-architecture` | `RE1-Architecture.rvt` | `Drshelden/IFC-ECS data/RE1/RE1-Architecture.rvt` (MIT) | Autodesk Revit 2025 | 6,627,328 | `4a78bdd68e7f4dd7806060db07c222da9a810e20b3f303c17a443294b91c8ce4` |
| `re1-architecture` | `RE1-Architecture.ifc` | `Drshelden/IFC-ECS data/RE1/RE1-Architecture.ifc` (MIT) | `IFC2X3 CoordinationView_V2.0`, Autodesk Revit `26.2.0.20 (ENU)`, `shared coordinates`, length unit `MILLIMETRE` | 1,677,715 | `a9b5d36677aa6a8bb91b77d8bc354ed9028a7ca3e9e2491a47ba14cfd8e26200` |
| `re1-mechanical` | `RE1-Mechanical.rvt` | `Drshelden/IFC-ECS data/RE1/RE1-Mechanical.rvt` (MIT) | Autodesk Revit 2025 | 3,379,200 | `d559720177aa0ec435133f804e7c45d60da2deb9d5585bb3d55f4830139c014b` |
| `re1-mechanical` | `RE1-Mechanical.ifc` | `Drshelden/IFC-ECS data/RE1/RE1-Mechanical.ifc` (MIT) | `IFC2X3 CoordinationView_V2.0`, Autodesk Revit `26.2.0.20 (ENU)`, `shared coordinates`, length unit `MILLIMETRE` | 220,068 | `9e56cf7246ce86ef73646dd588a1efb8a4756e5e102b30e28b3b58577a454db0` |

Exact download URLs are pinned in [tools/measure/re1-manifest.json](../tools/measure/re1-manifest.json):
- `https://raw.githubusercontent.com/Drshelden/IFC-ECS/main/data/RE1/RE1-Architecture.rvt`
- `https://raw.githubusercontent.com/Drshelden/IFC-ECS/main/data/RE1/RE1-Architecture.ifc`
- `https://raw.githubusercontent.com/Drshelden/IFC-ECS/main/data/RE1/RE1-Mechanical.rvt`
- `https://raw.githubusercontent.com/Drshelden/IFC-ECS/main/data/RE1/RE1-Mechanical.ifc`

## Rules, Comparable Groups & Tolerances

1. **Pre-export hash verification**: every `.rvt` and reference `.ifc` is checked for exact byte size and SHA-256 against the manifest before any export runs. Mismatches trigger a fresh download or fail the run immediately (`exit 1`).
2. **Strict export validation**: both commits run `RvtIO.Cli` with `--strict --ifc-timestamp 2026-01-01T00:00:00Z --report <report.json>`. A non-zero exit code or a report whose `status` is not `"complete"` (or has any `issues` or `limits.hit` entries — see [Export report and exit codes](export-report.md)) fails the run.
3. **Comparable groups**:
   - **Elements**: `IfcElement` subtypes excluding `IfcOpeningElement`, `IfcOpeningStandardCase`, and `IfcVirtualElement` (openings share their host/door/window `Tag`), paired by `Tag` (source Revit `ElementId`). Reports matched, `GlobalId == Revit`, `GlobalId != Revit`, missing, and added IDs.
   - **Spaces**: `IfcSpace` entities paired by `GlobalId`.
   - **Storeys**: `IfcBuildingStorey` entities paired by `Name` (checking `GlobalId` equality and elevation) plus per-element spatial containment/aggregate resolution.
   - **Spatial roots**: `IfcProject`, `IfcSite`, `IfcBuilding` matched by `GlobalId`.
4. **Coordinate normalization & tolerances**:
   - Coordinates, world bounding boxes, and storey elevations are normalized to SI metres using each file's `IfcUnitAssignment` (`LENGTHUNIT`).
   - Default tolerances: `worldBboxMeters = 0.01` m (10 mm max corner delta across all 6 AABB coordinates) and `storeyElevationMeters = 0.005` m (5 mm). Both tolerances and comparable group definitions are emitted in every machine-readable JSON report (`summary.json` and `*.compare.json`).
5. **Control check**: on each run (unless `--no-control-check` is passed), a synthetic perturbation copy of an exported IFC deletes one tagged element and shifts another element's placement by +0.50 m (+500 mm), verifying that both are caught cleanly, and verifies that bad SHA-256 hashes and `"status": "partial"` export reports are rejected.

## Local Execution

Prerequisites: `.NET 8/10 SDK` and `Python 3.10+` (standard library only, no third-party pip packages).

Compare any two commits (defaults to `origin/main` vs `HEAD`):

```sh
python tools/measure/measure_re1.py --base origin/main --head HEAD
```

Compare prebuilt `RvtIO.Cli.dll` binaries (without rebuilding git worktrees):

```sh
python tools/measure/measure_re1.py \
  --base-cli path/to/base/RvtIO.Cli.dll \
  --head-cli path/to/head/RvtIO.Cli.dll
```

Outputs are written to `_measure_out/`:
- `_measure_out/summary.json`: machine-readable combined report (metrics for both commits, commit-to-commit deltas, tolerances, pinned hashes, control check status)
- `_measure_out/summary.md`: human-readable comparison table
- `_measure_out/<model-id>/<side>.{ifc,report.json,compare.json}`: per-commit artifacts

Run offline unit and failure-gate tests:

```sh
python -m unittest discover -s tools/measure -p "test_*.py" -v
```

## Manual CI Execution

Dispatch [Measure RE1](../.github/workflows/measure.yml) (`workflow_dispatch`) from GitHub Actions or via `gh`:

```sh
gh workflow run measure.yml -f base_ref=main -f head_ref=my-branch -f models=re1-architecture,re1-mechanical
```

Optional inputs:
- `world_bbox_tol_m` (default `"0.01"`)
- `control_check` (default `true`)
- `fail_on_regression` (default `false`)

The workflow runs the same Python unit tests and `tools/measure/measure_re1.py`, publishes `summary.md` to the job summary, and uploads `_measure_out` artifacts (`re1-measure-results`).
