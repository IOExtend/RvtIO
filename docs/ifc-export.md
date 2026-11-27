# IFC Export and Configuration

RvtIO exports BIM geometry, spatial structure, materials, openings, distribution ports, property sets, and base quantities to IFC STEP (`*.ifc`) via both `RvtIO.Ifc.IfcExporter` and the `rvtio` CLI.

## IFC Schema Editions and Model View Definitions (MVDs)

| Option / Enum | STEP `FILE_SCHEMA` | Default Model View | Supported Model Views (`FILE_DESCRIPTION`) |
| --- | --- | --- | --- |
| `IfcSchema.Ifc2x3` (`--ifc-schema ifc2x3`, **default**) | `IFC2X3` | `CoordinationView_V2.0` (`Current`) / `CoordinationView` (`Legacy`) | `CoordinationView_V2.0`, `CoordinationView` |
| `IfcSchema.Ifc4` (`--ifc-schema ifc4`) | `IFC4` (ISO 16739-1:2018 / **IFC4 ADD2 TC1**) | `ReferenceView_V1.2` | `ReferenceView_V1.2`, `DesignTransferView_V1.0` |

### Default Schema Invariance

`IFC2X3` remains the default schema for both `new IfcExporter()` and `rvtio --ifc`. Exporting with `IFC2X3` and a fixed header timestamp (`IfcExporter.Timestamp` / `--ifc-timestamp`) preserves exact `IFC2X3` output across releases.

### Exact IFC4 Edition and Exchange Scenarios

`IfcSchema.Ifc4` emits `FILE_SCHEMA(('IFC4'))` conforming to **ISO 16739-1:2018 (IFC4 Addendum 2 Technical Corrigendum 1 / IFC4 ADD2 TC1)** and supports two explicit Model View Definitions:

1. **`ReferenceView_V1.2` (`IfcModelView.ReferenceView12`, default for `IFC4`)**:
   - Designed for reference coordination, clash detection, and viewer consumption (`ViewDefinition [ReferenceView_V1.2]`).
   - Preserves parametric sweeps (`IfcExtrudedAreaSolid` / `'SweptSolid'`) and mapped items (`IfcMappedItem` / `'MappedRepresentation'`).
   - Converts faceted BRep (`IfcFacetedBrep`) and surface-model (`IfcFaceBasedSurfaceModel`, `IfcShellBasedSurfaceModel`) representations into IFC4 `'Tessellation'` items (`IfcTriangulatedFaceSet` for pure triangle meshes without inner bounds; `IfcPolygonalFaceSet` / `IfcIndexedPolygonalFaceWithVoids` for general or voided polygonal faces), preserving `IfcStyledItem` material/surface styles.
   - Exports reinforcement centre lines as `IfcIndexedPolyCurve` with straight and circular arc segments.
   - Downgrades `IfcWallStandardCase` to `IfcWall` with `PredefinedType = .NOTDEFINED.`.
2. **`DesignTransferView_V1.0` (`IfcModelView.DesignTransferView10`)**:
   - Designed for editable model transfer (`ViewDefinition [DesignTransferView_V1.0]`).
   - Retains exact `'Brep'` (`IfcFacetedBrep`) and `'SurfaceModel'` solids/shells alongside `'SweptSolid'` extrusions.
   - Preserves `IfcWallStandardCase` when backed by an `IfcMaterialLayerSetUsage` relation; downgrades any remaining standard-case wall lacking a layer-set usage to `IfcWall` so EXPRESS WHERE rules hold.

### Independence from Compatibility Profiles (`Current` vs `Legacy`)

Schema (`IFC2X3` vs `IFC4`) and Model View (`ReferenceView_V1.2` vs `DesignTransferView_V1.0` vs `CoordinationView*`) are strictly orthogonal to `IfcProfile` (`Current` vs `Legacy`):

- **`IfcProfile`** controls Revit-parity dialect behavior (`GlobalId` stability and handle-based derivation, property set formatting and limits, default phase scope, default length unit selection, and base quantities).
- **All combinations** (`IFC2X3` or `IFC4` × `Current` or `Legacy`) are supported: selecting `Legacy` does not revert `IFC4` back to `IFC2X3`, and selecting `IFC4` does not alter `Current` vs `Legacy` property/quantity semantics beyond IFC4 schema requirements.

### Declared IFC4 Coverage and Diagnostics

Within IFC4 exports, RvtIO applies the following schema rules automatically and logs skipped/adapted items via `IfcExporter.DiagnosticOutput` (`Console.Error` in the CLI) and `IfcExporter.SkipLog`:

- **Products and Types**: Maps architectural, structural, and MEP categories to IFC4 entities (replacing removed `IfcElectricDistributionPoint` with `IfcElectricDistributionBoard` / `IfcElectricDistributionBoardType`, mapping electrical fixtures to `IfcOutlet` / `IfcOutletType` and lighting switches/devices to `IfcSwitchingDevice` / `IfcSwitchingDeviceType`, upgrading `IfcDoorStyle` / `IfcWindowStyle` to `IfcDoorType` / `IfcWindowType`, typing stairs/ramps/roofs with `IfcStairType` / `IfcRampType` / `IfcRoofType`, and adding IFC4 `PredefinedType` / `PartitioningType` / `OperationType` attributes).
- **Spatial Containment and Materials**: Moves `IfcSpace` out of `IfcRelContainedInSpatialStructure` into `IfcRelAggregates` under its host spatial structure; suppresses invalid `IfcRelAssociatesMaterial` targets on `IfcSpace` and `IfcOpeningElement`; unwraps deprecated `IfcPresentationStyleAssignment` inside `IfcStyledItem`.
- **Distribution Ports**: Attaches decoded fixed ports to their distribution element with one `IfcRelNests` per host, ordered by connector index. The current profile connects paired ports with `IfcRelConnectsPorts`. Directed links go from source to sink. The exporter reports skipped connectors on non-distribution hosts such as proxies.
- **Quantities and Properties**: Renames `Current`-profile `"BaseQuantities"` sets to IFC4 `Qto_*BaseQuantities` names, adds optional `Formula` slots on `IfcQuantity*`, deduplicates `HasPropertySets` on `IfcTypeObject`, and preserves per-element `GlobalId` uniqueness across shared segment types.
- **Coordinates, Units, and 2D Datum Curves**: Scales `IfcCartesianPointList3D` coordinates alongside all lengths/areas/volumes (`mm`, `m`, `ft`), pairs missing `Axis`/`RefDirection` attributes on `IfcAxis2Placement3D`, and keeps `IfcGridAxis` and `'Axis'` `'Curve2D'` curves strictly 2D (or `'Curve3D'` when an axis has non-zero Z elevation).

---

## Unified Export Configuration (`IfcExportConfiguration`)

Both the `RvtIO` library and `rvtio` CLI share a single typed, JSON-serializable configuration model (`RvtIO.Ifc.IfcExportConfiguration`) with named presets, deterministic precedence, strict validation, and a queryable capability matrix (`IfcExportCapabilities.All`).

### Precedence Order

Settings resolve deterministically in the following order (later layers override earlier ones):

1. **Built-in defaults** (`IfcExportConfiguration.Default` — `IFC2X3`, `Default` MVD, `Current` profile, `HostAxes` opening quantities, `ExportRoomsAsSpaces = true`, `ExportBaseQuantities = true`, `ExportRevitPropertySets = false`).
2. **Named preset** (`IfcExportPresetKind` / `--ifc-preset` or `preset` in JSON).
3. **JSON configuration file** (`IfcExportConfiguration.FromJson(...)` / `--ifc-config <file.json>`).
4. **Explicit API properties / CLI flags** (`Schema`, `ModelView`, `Profile`, `LengthUnit`, `Timestamp`, `ExportPhaseId`, `ExportAllPhases`, `RoomExportPhaseId`, `ExportRoomsOfAllPhases`, `ExportRoomsAsSpaces` / `--ifc-no-spaces`, `BaseQuantities` / `--ifc-no-base-quantities`, `OpeningQuantitiesSwapped` / `--ifc-opening-legacy-axes`, Revit parameter flags).

When an explicit `Schema` or `ModelView` override changes away from a base preset's schema/view without explicitly setting `PreferTessellation`, geometry defaults re-resolve to match the target MVD (`true` for `ReferenceView_V1.2`, `false` otherwise). Unknown JSON properties fail deserialization (`JsonUnmappedMemberHandling.Disallow`).

When saving a setup during export, `--ifc-phase last` and `--ifc-room-phase last` remain reusable last-phase selectors in the JSON. The CLI resolves them against the project used for each export.

### Named Presets (`--ifc-preset`)

| CLI / JSON Preset Name | `IfcExportPresetKind` | Schema | Model View | Profile | Geometry Default |
| --- | --- | --- | --- | --- | --- |
| `default` | `Default` | `IFC2X3` | `CoordinationView_V2.0` | `Current` | Brep / SweptSolid |
| `ifc2x3-coordination-view-2.0` | `Ifc2x3CoordinationView20` | `IFC2X3` | `CoordinationView_V2.0` | `Current` | Brep / SweptSolid |
| `ifc2x3-coordination-view-legacy` | `Ifc2x3CoordinationViewLegacy` | `IFC2X3` | `CoordinationView` | `Legacy` | Brep / SweptSolid |
| `ifc4-reference-view-1.2` | `Ifc4ReferenceView12` | `IFC4` | `ReferenceView_V1.2` | `Current` | `Tessellation` (`IfcPolygonalFaceSet` / `IfcTriangulatedFaceSet`) |
| `ifc4-design-transfer-view-1.0` | `Ifc4DesignTransferView10` | `IFC4` | `DesignTransferView_V1.0` | `Current` | Brep / SweptSolid |

### Validation and Rejected Combinations

Calling `configuration.Validate()`, `configuration.Resolve()`, or running an export rejects invalid or unsupported combinations with explicit diagnostic codes (`InvalidOperationException` in the library; non-zero exit code `1` on CLI stderr):

- **Incompatible schema & model view** (`incompatible-schema-model-view`): e.g. `IFC2X3` with `ReferenceView_V1.2` or `DesignTransferView_V1.0`, or `IFC4` with `CoordinationView` / `CoordinationView_V2.0`.
- **Conflicting geometry representation flags** (`conflicting-geometry-tessellation`, `conflicting-geometry-mode`): e.g. `PreferTessellation = true` under `IFC2X3`, `PreferTessellation = false` under `ReferenceView_V1.2`, `UseSolidBrepWhenAvailable = true` under `ReferenceView_V1.2`, or `UseSolidBrepWhenAvailable = false` outside `ReferenceView_V1.2`.
- **Conflicting phase options** (`conflicting-phase-options`, `invalid-phase-id`, `conflicting-room-phase-options`, `invalid-room-phase-id`).
- **Unsupported explicit options** (`unsupported-option`, `unsupported-detail-level`, `unsupported-coordinate-base`).

### Capability Matrix (`rvtio --ifc-capabilities`)

| Setting Path | Status | Supported Values | Default | Notes |
| --- | --- | --- | --- | --- |
| `schema.schema` | `Supported` | `IFC2X3`, `IFC4` | `IFC2X3` | IFC4 implements ISO 16739-1:2018 (IFC4 ADD2 TC1). Independent of `Legacy`/`Current`. |
| `schema.modelView` | `SchemaOrViewScoped` | `Default`, `CoordinationView`, `CoordinationView_V2.0` (`IFC2X3`); `ReferenceView_V1.2`, `DesignTransferView_V1.0` (`IFC4`) | `Default` | Resolves per schema/profile when `Default`. |
| `schema.profile` | `Supported` | `Current`, `Legacy` | `Current` | Orthogonal to schema and MVD. |
| `preset` | `Supported` | All 5 `IfcExportPresetKind` presets | `default` | Supplies base layer overridden by JSON and API/CLI flags. |
| `scope.exportElementsInCurrentViewOnly` | `Unsupported` | `false` | `false` | View-specific element filtering fails validation if `true`. |
| `scope.exportAnnotations2D` | `Unsupported` | `false` | `false` | General 2D annotations beyond grid `FootPrint` curves fail validation if `true`. |
| `scope.exportLinkedFiles` | `Unsupported` | `false` | `false` | Linked `.rvt` files are not loaded; `true` fails validation. |
| `scope.respectExportToIfcParameter` | `Supported` | `true` | `true` | Honors Revit `Export to IFC` parameter; `false` fails validation. |
| `scope.splitWallsAndColumnsByLevel` | `Unsupported` | `false` | `false` | Level splitting is not implemented; `true` fails validation. |
| `phases.scope` | `Supported` | `Default`, `LastPhase`, `AllPhases`, `SpecificPhase` | `Default` | `Default` resolves to `LastPhase` (`Current`) or `AllPhases` (`Legacy`). |
| `phases.phaseId` | `Supported` | Non-negative Revit Phase ID | `null` | Exports element state at the specified phase. |
| `phases.roomScope` | `Supported` | `FollowPhaseScope`, `LastPhase`, `AllPhases`, `SpecificPhase` | `FollowPhaseScope` | Selects phase for exported rooms (`IfcSpace`). |
| `phases.roomPhaseId` | `Supported` | Non-negative Revit Phase ID | `null` | Exports rooms belonging to the specified phase. |
| `unitsAndCoordinates.lengthUnit` | `Supported` | `Millimetre` (`mm`), `Metre` (`m`), `Foot` (`ft`) | `null` | Default uses project length unit (`Current`) or `mm` (`Legacy`). |
| `unitsAndCoordinates.coordinateBase` | `SchemaOrViewScoped` | `SharedCoordinates` | `SharedCoordinates` | Exports active shared coordinates/geo-reference on `IfcSite`. |
| `unitsAndCoordinates.headerTimestamp` | `Supported` | UTC timestamp / `null` | `null` | Deterministic `FILE_NAME` timestamp for byte-identical regression. |
| `geometry.detailLevel` | `SchemaOrViewScoped` | `High` | `High` | Native decoded geometry; coarser LOD levels fail validation. |
| `geometry.preferTessellation` | `SchemaOrViewScoped` | `true` (`RV 1.2`), `false` (`DTV 1.0`, `IFC2X3`) | MVD-dependent | Controls `IfcPolygonalFaceSet` / `IfcTriangulatedFaceSet` conversion. |
| `geometry.useSolidBrepWhenAvailable` | `SchemaOrViewScoped` | MVD-dependent | MVD-dependent | Valid in `DTV 1.0` / `IFC2X3`; rejected under `ReferenceView_V1.2`. |
| `spatial.exportRoomsAsSpaces` | `Supported` | `true`, `false` | `true` | `false` (`--ifc-no-spaces`) omits `IfcSpace` and room relations. |
| `spatial.exportRoomBoundaries` | `Unsupported` | `false` | `false` | `IfcRelSpaceBoundary` export fails validation if `true`. |
| `quantities.exportBaseQuantities` | `Supported` | `true`, `false` | `true` | `'BaseQuantities'` (`IFC2X3`) or `'Qto_*BaseQuantities'` (`IFC4`). |
| `quantities.openingQuantityFrame` | `Supported` | `HostAxes`, `LegacyOpeningAxes` | `HostAxes` | Selects opening Width/Height axes frame. |
| `propertySets.exportIfcCommonPropertySets` | `SchemaOrViewScoped` | `true` | `true` | Standard `Pset_*Common` property sets are always emitted. |
| `propertySets.exportRevitPropertySets` | `Supported` | `true`, `false` | `false` | Opt-in export of Revit parameters. |
| `propertySets.groupRevitParametersByGroup` | `Supported` | `true`, `false` | `false` | Groups Revit parameter sets per group (implies `exportRevitPropertySets`). Property names are unique within a set: when parameters of one group share a name, each gets ` [parameter id]` appended. |
| `propertySets.revitParameterUnits` | `Supported` | `internal`, `ifc` | `internal` (untyped) | Converts length, area, volume, and angle to IFC units and other physical specs to SI units when `ifc`, and assigns the project unit of each converted measure. A parameter whose spec neither the file nor the API definitions give keeps its stored number as `IfcReal`; `IfcMonetaryMeasure` has no currency unit. |
| `propertySets.revitParameterNames` | `Supported` | `builtin`, `display` | `builtin` (untyped) | Uses parameter display names (with `BuiltInParameter` in `Description`) and resolves user parameter names when `display`. Property names are unique within a set: when two parameters share a name, each gets ` [parameter id]` appended (also with an explicit `builtin`). The CLI options for names and units turn the Revit property sets on, with `--ifc-config` too. |
| `propertySets.exportSchedulesAsPropertySets` | `Unsupported` | `false` | `false` | Schedule Psets are not implemented; `true` fails validation. |
| `mappings.useRevitIfcExportAsParameters` | `Supported` | `true` | `true` | Reads `IfcExportAs` / `IfcExportType`; `false` fails validation. |
| `mappings.familyMappingTablePath` | `Unsupported` | `null` | `null` | External mapping files fail validation if non-empty. |
| `mappings.parameterMappingTablePath` | `Unsupported` | `null` | `null` | External mapping files fail validation if non-empty. |

---

## Examples

### C# Library API

```csharp
using RvtIO;
using RvtIO.Ifc;

var info = new RvtFileReader(new RvtReaderOptions { R22Layout = true }).Read("model.rvt");

// 1. IFC4 Reference View 1.2 (Current profile) via preset
var rvConfig = IfcExportConfiguration.FromPreset(IfcExportPresetKind.Ifc4ReferenceView12);
new IfcExporter(rvConfig, diagnosticOutput: null).Export(info, "model-rv12.ifc");

// 2. IFC4 Design Transfer View 1.0 under Legacy profile (profiles remain orthogonal)
var dtvLegacy = new IfcExportConfiguration
{
    Schema = new IfcSchemaConfiguration
    {
        Schema = IfcSchema.Ifc4,
        ModelView = IfcModelView.DesignTransferView10,
        Profile = IfcProfile.Legacy,
    },
    UnitsAndCoordinates = new IfcCoordinateAndUnitConfiguration
    {
        LengthUnit = IfcUnitFamily.Metre,
        HeaderTimestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
    },
};
new IfcExporter(dtvLegacy, diagnosticOutput: null).Export(info, "model-dtv10-legacy.ifc");
```

### CLI Usage

```bash
# Default export (IFC2X3 CoordinationView_V2.0)
rvtio model.rvt --ifc out-2x3.ifc

# IFC4 ADD2 TC1 ReferenceView_V1.2 (default IFC4 MVD)
rvtio model.rvt --ifc out-rv12.ifc --ifc-schema ifc4

# IFC4 DesignTransferView_V1.0 in metres under Legacy profile
rvtio model.rvt --ifc out-dtv.ifc --ifc-schema ifc4 --ifc-mvd design-transfer-view-1.0 --ifc-profile legacy --ifc-length-unit m

# Save effective JSON config and inspect capability matrix
rvtio --ifc-save-config setup.json --ifc-preset ifc4-reference-view-1.2
rvtio model.rvt --ifc out.ifc --ifc-config setup.json --ifc-no-spaces
rvtio --ifc-capabilities
```
