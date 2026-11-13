# Scene adapter

`RvtIO.Scene` turns a Revit project read by RvtIO into a scene of the `IOScene` package: a `SceneDocument` with one
model, the drawable elements, their triangle meshes in metres, their names and their parameters. A viewer or another
consumer of the scene reads it without knowing Revit. The scene itself is specified in the contract of the `IOScene`
repository (`docs/contract.md`), and the property keys it writes are registered there in `docs/keys.md`.

The adapter is a preview. It is published as the package `RvtIO.Scene` on each RvtIO release tag, depends on exactly
the same RvtIO version and needs IOScene 0.1.0 or a later 0.1 release.

## Usage

```csharp
using IOScene;
using RvtIO.Scene;

var result = RvtSceneReader.Read("project.rvt");
foreach (var diagnostic in result.Diagnostics)
    Console.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
SceneJson.Save(result.Document, "project.ioscene.json");
```

`Read(path)` opens the file as the command line tool opens it for an IFC export (`R22Layout`, English system family
names, faces with distinct openings kept apart). `Read(document, options)` takes a document you opened yourself; it
needs `RvtSceneOptions.ModelKey`, because a document does not know its file name. `Read(document, meshes, options)` also
takes the meshes you extracted from that document yourself, for example with `ElementMeshes.Extract` and options of your
own. Open a file of a release other than
Revit 2022 with `R22Layout = true`, or its elements have no geometry and the result has the error `geometry-undecoded`.
Only `Read(path)` reports the diagnostics of the read (`read-cut`, `failed-members`, `recovered-members`,
`unverified-members`), as only it opens the file; if you open the document yourself, read them from the
`RvtReaderOptions.Diagnostics` you opened it with.

`RvtSceneResult` holds the `Document`, the `Diagnostics` and counts: elements, elements with geometry, elements by
coverage, geometries, instances and symbol placements.

## What the scene holds

| Scene | From |
|---|---|
| `SceneModel.Key` | `RvtSceneOptions.ModelKey`, else the file name without its directory (`project.rvt`). This derivation does not change. |
| `SceneModel` | `SourceFormat` `rvt`, `SourceLengthUnit` `ft`, `Producer` `RvtIO.Scene <version>`. |
| `SceneElement` | One per drawable element of `ElementMeshes.Extract` (the elements the IFC export would consider products, see the remarks of `ElementMeshes`). `Id` is the element id. |
| `SourceId` | The Revit `UniqueId`. This derivation does not change; a golden test pins it on a fixture file. |
| `IfcGuid` | The GlobalId Revit's IFC export gives the element: the value of its built-in IfcGUID parameter (`IFC_GUID`, matched by id as RvtIO's IFC export matches it) when it is a valid 22-character GlobalId; else the export id of the `UniqueId` (the episode GUID with its last 32 bits XORed with the element id the `UniqueId` ends with) in the IFC base-64 form. It depends on the element only, never on export order or options. This derivation does not change; the golden test pins it. |
| `Name`, `Category`, `TypeName` | `Element.Name`; the English name of the built-in category (else `Element.Category`); the name of the type element. |
| `SceneGeometry`, `SceneInstance` | A symbol placed whole is one geometry per mesh in the frame of its family, and each placement is one instance per mesh with the placement's transform. A mirrored placement keeps its transform as it is: the winding of the triangles is not changed. A mesh of an element's own is a geometry used once, with vertices relative to the lowest corner of the element's meshes, which is the translation of its instances. All lengths are feet x 0.3048, in the internal coordinates of the document, Z up. |
| `SceneMaterial` | One per material element that a mesh uses: its name and its colour (`MATERIAL_PARAM_COLOR`, alpha from `MATERIAL_PARAM_TRANSPARENCY`), else a neutral grey. |
| `ScenePropertySet` | `revit:instance`: the parameters of the element. `revit:type`: the parameters of its type, one set shared by every element of the type. A parameter that carries no value in Revit (unset sentinels such as `Double` `>= 1e29` or `ElementId` `-1`, and unstored default placeholders synthesized by the reader) is left out, and so is one whose value the reader only estimates (`Parameter.IsApproximate`), such as the fixed thermal values of a wall; stored `0`, `0.0` and `""` are kept. |
| `SceneProperty` | Name: `Definition.Name` (for a built-in parameter its enum name, such as `WALL_USER_HEIGHT_PARAM`). Key: `revit:bip:<id>` for a built-in parameter, none for the others. Value: text, the display string of the public parameter API (see below). |

### Property text

A value is written as the text `Parameter.AsValueString` shows, with the invariant culture, and only when that text is
the one Revit shows; any other value is left out. By storage type:

| Storage | Text |
|---|---|
| String | The stored text. |
| Double | The value converted to its display unit and the unit symbol, as `AsValueString` writes it from `UnitSymbol` and `ConversionFactor` (`3200.4 mm`), when the unit was resolved from the parameter's measurement spec (`SpecType`) and is mm, m², m³, ° or A: the units whose factor converts Revit's internal unit (foot, square foot, cubic foot, radian, ampere). Left out: a value without a spec, which is every built-in double (the reader then guesses `UnitSymbol` from the parameter name, which gives the factor `TEXT_WIDTH_SCALE` the unit mm), a unit the reader converts with a factor of 1 that is not Revit's conversion (W, V, °C, Pa, ...), and an area per unit length, which the reader shows in m². |
| Integer | A shared, project or family parameter: the number in decimal; such an integer is a plain integer or a Yes/No, which `AsValueString` shows as `1` or `0`. A built-in integer is left out: it may be an enumeration or a Yes/No, and the public API cannot tell it from a number. |
| ElementId | Left out: the public API does not give the name Revit shows for the referenced element. |

The text a value is written with under a key is frozen with the key: changing it is a breaking release.

## Diagnostics

| Code | Severity | Meaning |
|---|---|---|
| `geometry-undecoded` | error | The geometry of the file was not decoded; open it with `R22Layout`. |
| `read-cut` | error | A reader limit cut a stream while the file was read (`Read(path)` only); elements may be missing. |
| `failed-members` | error | Gzip members of a stream failed to decode (`Read(path)` only); elements may be missing. One per stream. |
| `recovered-members` | warning | Gzip members of a stream were rebuilt by structural recovery (`Read(path)` only); their elements may be incomplete or wrong. One per stream. |
| `unverified-members` | warning | Gzip members of a stream decoded without a matching checksum, not counting the rebuilt ones (`Read(path)` only); their elements may be incomplete or wrong. One per stream. |
| `undecoded` | warning | An element of such a file; it is in the scene without geometry. |
| `no-geometry` | warning | An element whose geometry record has nothing that could be drawn; it is in the scene without geometry. |
| `partial-geometry` | warning | Faces that could not be triangulated, or a placed symbol or Geometry node without a decoded polygon. |
| `box-substitute` | warning | A rescaled symbol drawn as its bounding box. |
| `no-source-id` | warning | Elements without a UniqueId; they cannot be identified outside the scene. |

## Limits

- Values are text only, and only those whose display string the public API gives (see Property text). Typed values
  (numbers in SI units, booleans, element references) wait for public RvtIO API.
- Shared, project and family parameters have no key yet. Their formats (for example `revit:shared:<guid>`) are
  registered in the IOScene key registry when the adapter first writes them; that is an addition.
- No levels (`LevelId` and the `Levels` table stay empty): RvtIO has no public level of an element.
- No tree: every element is a top-level element of the model. No links, grids or views.
- Materials have a name and a colour; no roughness, metalness or textures.
- Element names are those of `RvtDocument`; the validated names the IFC export applies to files read by their own class
  schema are not public API.
