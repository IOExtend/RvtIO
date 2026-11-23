# Writing and save-as

`RvtWriter` and `RvtDocument.SaveAs` provide lossless container round-trips and targeted paged-stream and partition-record
rewrites for local Revit projects (`.rvt`) and families (`.rfa`), without requiring Revit or the Revit API.

```csharp
using RvtIO;

RvtWriteCapabilities capabilities = RvtWriter.InspectCapabilities("project.rvt");
if (capabilities.Supports(RvtWriteOperation.SaveAs))
{
    RvtWriteResult saved = RvtWriter.SaveAs("project.rvt", "project.copy.rvt");
}
```

From the command line:

```
rvtio save-as project.rvt --check
rvtio save-as project.rvt project.copy.rvt --report save-as.report.json
```

## Editing a parameter

`SetParameter` writes a new RVT without Revit installed. Pass the owning element ID, the parameter identifier, and the replacement value.

```csharp
using RvtIO;
using RvtIO.BuiltIns;

RvtWriter.SetParameter("project.rvt", "project.edited.rvt", 12345, BuiltInParameter.CLIENT_NAME, "Example Client");
RvtWriter.SetParameter("project.rvt", "wall.edited.rvt", 42000, 43000, "Inspected");
```

```sh
rvtio set-parameter project.rvt project.edited.rvt 12345 CLIENT_NAME "Example Client"
rvtio set-parameter project.rvt wall.edited.rvt 42000 43000 "Inspected"
```

The command accepts a built-in name or a signed 64-bit numeric ID. Use the positive definition element ID from the same project
for a project, shared, or loaded-family parameter. The scope is local, non-workshared Revit 2020–2027 projects with exactly one
readable project record for the element and a verified source for the parameter value.

| Owner | Supported parameters |
| --- | --- |
| Project Information (`ProjectInfo`) | `CLIENT_NAME`, `PROJECT_ADDRESS`, `PROJECT_ISSUE_DATE`, `PROJECT_NAME`, `PROJECT_NUMBER`, `PROJECT_STATUS`. The matching system field in the English `ProjectInformation` XML must agree with the stored value. Both are updated together. |
| Straight wall or top-level loaded family instance | `ALL_MODEL_MARK` (Mark). The writer verifies the main-model owner and the element's text store. |
| Straight wall or top-level loaded Generic Model, Door, Window, or Furniture instance | `ALL_MODEL_INSTANCE_COMMENTS` (Comments). |
| Straight wall, room, or top-level loaded Generic Model, Door, Window, or Furniture instance | `ALL_MODEL_IMAGE` (Image). |
| Basic wall type | `ALL_MODEL_TYPE_IMAGE` (Type Image). |
| Basic wall type, primary or directly linked derived family symbol | `ALL_MODEL_TYPE_MARK` (Type Mark). Pass the ID of the type or symbol whose value you want to write. |
| Basic wall type or verified loaded family type | `ALL_MODEL_TYPE_COMMENTS`, `ALL_MODEL_DESCRIPTION`, `ALL_MODEL_MANUFACTURER`, `ALL_MODEL_MODEL`, `ALL_MODEL_URL`, and `ALL_MODEL_COST`. Cost takes a finite invariant number with no currency symbol; a change that Revit can suppress within its numeric comparison tolerance is refused. |
| Basic wall type or primary Door symbol without derived copies | `FIRE_RATING` (also named `DOOR_FIRE_RATING`, the same parameter ID). |
| Straight wall (`SWall`), basic wall type (`BasicWallType`), room (`RoomElem`) | Project and shared parameters of supported types with a verified definition and a matching instance or type binding. |
| Straight wall, basic wall type, room, or loaded family instance or type | **Family Type** parameters supplied by a verified project binding. Assign a matching nested type reference, a verified material ID, or `"-1"`. Shared definitions also accept the verified local element and active primary type references described below. A shared definition with `HideWhenNoValue` supports clearing. |
| Top-level loaded family instance (`FamilyInstance`) or primary family type (`FamilySymbol`) | Family and family-shared parameters of supported types. The loaded family must mark the parameter as independent of geometry. |
| Top-level loaded family instance or primary loaded family type | Family and family-shared **Family Type** parameters with a verified geometry-independent declaration. Assign a matching local nested type reference, a verified local material ID, or the explicit value `"-1"`. Primary type assignments also update verified derived copies. |
| Top-level loaded family instance (`FamilyInstance`) | Writable shared and non-shared parameters supplied by a verified project instance binding. Pass the instance's ID to assign or replace its own value. |
| Primary or directly linked derived family symbol (`FamilySymbol`) | Writable shared and non-shared parameters supplied by a verified project type binding. Pass the symbol's element ID to assign or replace its own value. |
| Global parameter (`GlobalParameter`) | Text, Integer, Yes/No, Number, Length, Area, Volume, Angle, Currency, Mass, Force, Mass Density, Speed, Slope, and Time, including inputs to verified formulas. Currency globals can update verified built-in Cost and shared Currency associations on walls, basic wall types, rooms, and loaded family elements. Use the global parameter's element ID for both `elementId` and `parameterId` in single edits and batches. |

For project and shared parameters, the writer checks the definition's read-only flag and verifies the category binding in the main model.
Revit API editability governs shared parameters that the UI displays as read-only. The writer can also assign the first
value of a supported parameter on a straight wall, a basic wall type, or a room. First assignment of a wall comment requires
a wall in the main model. First assignment of project-bound text on walls and rooms requires a non-empty value.
The supported built-in Comments and text identity fields, and project-bound text on loaded family elements, accept an
empty string even on first assignment.
Ordinary element edits also work in projects with global parameters. RvtIO checks project associations by the
parameter ID on the element. A supported parameter can be assigned or cleared when another parameter on that
element has an association. The associated parameter is read-only. Group membership and unverified association
layouts still cause refusal. [Autodesk Revit API guide to parameter associations](https://help.autodesk.com/cloudhelp/2025/ENU/Revit-API/files/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Parameters/Revit_API_Revit_API_Developers_Guide_Basic_Interaction_with_Revit_Elements_Parameters_Parameter_html.html)

For a **Family Type** parameter declared in a loaded family, the writer assigns a first value or replaces the current value
on the selected type or instance. An instance can receive its first local value while its type retains the inherited
default. A primary type assignment updates directly linked derived copies when their existing value agrees with
the primary value. Revit can report success for a direct assignment to a family-declared parameter on a derived
symbol while keeping its inherited value. RvtIO refuses that direct edit. Use the element ID of a verified
`NestedFamilyTypeReference` in the parameter's category or of a local material. An already active primary
`FamilySymbol` in the parameter's category can also be a target. RvtIO checks that the target belongs to a
different family in the main model. It follows registered family and type IDs in family maps and saved parameter
values, including edits already staged in the batch. The writer refuses unverified dependencies. For a family-declared,
geometry-independent parameter on an active primary type, the target can directly nest that shared type. The
container must map both the referring family and its type, and any private nested type references in the map
must resolve to records owned by that family. A dependency path back to the referring family remains outside
the supported instance edits. Use the explicit string `"-1"` to assign Revit's invalid element ID.
[Autodesk Family.GetFamilyTypeParameterValues reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/a9c8ff23-17ec-1b87-e58a-4be589217766.htm)

For a **Family Type** parameter supplied by a shared project binding, a verified active primary
`FamilySymbol` can also come from a different category. This applies on supported loaded family types and
instances, straight walls, basic wall types, and rooms. The value is stored on the addressed element. On a
loaded family type or instance, the target can be its own primary type, another primary type of the same family,
or a family type that nests the referring family. The same binding also accepts a verified local basic `WallType`,
`RebarCoverType`, `Level`, loadable `Family`, or top-level `FamilyInstance`. RvtIO checks the
target ID, class, and main-model header. A `NestedFamilyTypeReference` must still match the parameter category.
Assigning `"-1"` leaves a stored value. Clearing removes the assigned value and requires a shared project binding
with `HideWhenNoValue`.

Global parameter edits require an explicit value. The writer checks the full project element table and stored
formula, dimension, and element property references before changing a global value. Use the normal numeric and
unit formats below. A writable Currency global takes a finite invariant number with no currency symbol,
for example `1234.56789` or `-58.625`. The writer refuses differences for which Revit can retain the previous
number under its comparison tolerance. Use an empty string to assign empty Text, or `0` to assign an unchecked Yes/No value.
If a global value controls a verified project-bound user parameter, RvtIO updates both values in the same batch.
The controlled property must have matching storage and compatible units, and its saved value must match the previous global
value. This applies to project properties controlled directly by the edited global and to properties controlled by
the result of a recalculated global formula. The element parameter remains read-only through a direct assignment.
[Autodesk GlobalParameter.SetValue reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/df012c22-6e65-8de5-1057-f15660d02288.htm)
The same write path supports verified parameters declared by a loaded family when the family marks the parameter
as independent of geometry. An instance association updates the local value and its dependent formulas. A type
association updates the primary symbol and recalculates affected instance results, including on placements with
derived symbols. Derived symbol records retain their saved values in this case.
A Text global can also update Type Comments, Description, Manufacturer, and Model on a primary Generic Model,
Door, Window, or Furniture type, and Fire Rating on a primary Door type. A Currency global can update the
type's Cost. The writer verifies the existing value in the typed element store
or named family store. A Generic Model global updates the primary or directly derived symbols to which it is
associated. Starting with the 2022 format, a Text global can also update Comments on a main-model wall
or a top-level Generic Model instance. These edits require an existing association and use the global parameter's
ID for both edit IDs.
Verified formulas support parentheses, negation, addition, subtraction, multiplication, and division with
compatible units. They also support `=`, `>`, `<`, `and`, `or`, `not`, and `if` with numeric, Yes/No, or Text
branches as permitted by the formula type. Only the selected branch of `if` is evaluated. In `and` and `or`,
the writer evaluates the second operand only when the first operand does not determine the answer. Unitless formulas
support `round`, `roundup`, and `rounddown`. Integer results use Revit's nearest-integer rounding, with exact
halves rounded toward the greater integer. Recalculation uses the stored integer result in downstream formulas.
The rule for a negative argument of `round`, `roundup`, and `rounddown` has not been compared with Revit. The writer
assumes that `round` takes an exact half toward positive infinity and that `roundup` and `rounddown` are the ceiling
and the floor, so `roundup(-2.2)` gives `-2` and `rounddown(-2.2)` gives `-3`. Treat results of negative arguments as
unverified.
Global formulas treat Currency and Number as scalar numbers. Results retain their original parameter type for
associations. Revit accepted `CurrencyInput + 2` in the tested formulas and rejected
`CurrencyInput + 2 $`.
Mass, Force, Mass Density, Speed, Slope, and Time also support verified formula dependencies and existing global
associations. Formulas can scale the quantity and add a compatible measured constant. Revit evaluates Slope as
a ratio in formulas. For example, a formula that adds 2 percentage points stores the constant as the number `0.02`.
Recalculation also supports Mass Density multiplied by Volume to give Mass, and Speed multiplied by Time to give
Length. Each product accepts either operand order. Dividing Mass or Length by one factor gives the other factor.
The same operations apply to verified family type and instance formulas in projects, and to type values and instance
defaults in standalone families.
An existing association between Number and Slope transfers the ratio directly. For example, a Number global
of `0.375` updates its associated Slope parameter to `37.5 %`. Use the global's own input format when editing
it. A Number global can update a project-bound or loaded-family Slope parameter. A Slope global can update a
Number type parameter declared in a loaded family. Using a Slope global with a shared Number parameter bound
in the project requires Revit 2022 or later. Revit 2020 and 2021 refused that project binding on the wall
instances and basic wall types tested.
[Autodesk global parameter association reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/796f3d95-956e-a2a9-7f8e-e8efd2a0eea0.htm)
Mathematical functions include `abs`, `sqrt`, `exp`, `log`, `ln`, `sin`, `cos`, `tan`, `asin`, `acos`, `atan`,
and `pi()`. Square roots support scalars and areas. Trigonometric functions accept angles in radians internally,
and the inverse functions return angles. Powers (`^`) support scalar bases with scalar exponents, length squared
or cubed, and the square root of an area.
The writer checks the current stored results and updates every affected value in the same batch. For verified
cases where Revit accepts a new input and retains a formula's previous result, RvtIO preserves that result's bytes.
These include division by a zero or almost-zero denominator, negative square roots, logarithms below the
verified positive threshold, inverse sine or cosine outside their domain, tangent at a verified pole,
exponential results above a magnitude of `1e30`, and powers that underflow to zero. Other formulas referencing
the affected parameter use its stored value until it can be recalculated. Unverified mathematical inputs,
ambiguous changes or comparisons, and rounding near tolerance boundaries still cause refusal. Global values
that also control dimensions or other unverified dependents still cause refusal.

For a parameter declared in the loaded family, pass the instance ID to update an override or create one from a verified
inherited value. Pass the primary type ID to update that type and its verified derived copies together. Assigning a
verified input on the primary type also recalculates dependent formula values in the type, its derived copies, and
the affected stored values on top-level instances. Each instance uses its own stored overrides as formula inputs.
Assigning a stored input on a top-level instance recalculates that instance's stored formula results using its
effective symbol and local values. Every updated value must be classified by the family as independent of geometry.
On a primary loaded type, RvtIO also supports direct assignments to numeric results of verified linear formulas
with one writable type input. It solves for the input and recalculates the affected values. With an integer input,
an exact half rounds toward positive infinity, so assigning `99` to the result of `input * 2` can store input `50`
and result `100`. If the requested result already matches, the input stays at its current value. Inverses that
change a real input require exact scaling. Reporting parameters and unverified expressions, scopes, or associations
still cause refusal.

Revit 2025 exposes the tested loaded type formulas for Boolean identity, Boolean negation, a numeric conditional
controlled by a Boolean parameter, and text identity as read-only. Revit 2027 also refused the tested numeric
formula with two independent inputs.
For a parameter supplied only by a project binding, the assignment edits the addressed symbol or instance's
typed value. The writer verifies that the loaded family does not also declare the same parameter.
The supported built-in Comments and identity fields also edit the addressed element's own value. An assignment to a
primary symbol through the verified project value store preserves local values of its derived symbols.
Door, Window, and Furniture identity fields support primary symbols without derived copies. The writer follows the
storage Revit uses for the parameter, either a value in the project store or an existing value imported from the
family. Editing an imported Generic Model identity field on the primary symbol also updates its verified derived
copies when they match the previous primary value. Generic Model identity fields in the project store can be
edited on the primary or a directly linked derived symbol by its own ID.

Supported types are Text, Integer, Yes/No, Number, Length, Area, Volume, Angle, and Material. Use `0` or `1` for Yes/No.
Integers respect the stored definition bounds. For Number, pass a finite invariant decimal without a unit. For measurements,
pass a finite invariant decimal followed by a space and the unit. Length accepts `mm`, `cm`, `m`, `in`, and `ft`. Area accepts
`m2` and `ft2`. Volume accepts `m3` and `ft3`. Angles accept `deg` and `rad`. The corresponding `²`, `³`, and `°` symbols also work.
Assignments also accept Mass in `kg`, Force in `kN`, Mass Density in `kg/m3` or `kg/m³`,
Speed in `m/s`, Slope in `%`, and Time in `s`. In a value such as `16.5 %`, the space separates the value
from the unit. RvtIO converts the quantity to Revit's internal units before writing.
[Autodesk Revit API unit system](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Application_and_Document/Revit_API_Revit_API_Developers_Guide_Introduction_Application_and_Document_Units_html.html)
Shared project-bound Currency values on walls, basic wall types, rooms, top-level loaded family instances, and
primary and directly linked derived symbols use a finite invariant number with no currency symbol. An explicit zero
gives the parameter a value. Clearing requires `HideWhenNoValue`. The writer refuses changes that Revit may
suppress within its numeric comparison tolerance. [Autodesk shared parameter guide](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API/files/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Parameters/Revit_API_Revit_API_Developers_Guide_Basic_Interaction_with_Revit_Elements_Parameters_Shared_Parameters_html.html)
Family-declared Currency values on loaded primary types and top-level instances use the same numeric input.
The writer checks the effective value, including an inherited instance default, against Revit's comparison tolerance.
It recalculates verified dependent results and supports a calculated Currency type value when its formula has
one verified Currency input with exact scaling. An existing Currency global association uses the same
family dependency checks.
When Revit keeps an instance's calculated Currency value within its relative comparison tolerance, the writer preserves
that saved value for a result with no downstream formulas. A subsequent input change can recalculate the result from
the retained value.
On loaded families, the writer recalculates verified Mass, Force, Mass Density, Speed, Slope, and Time results after
editing a type value or a top-level instance input. It also supports assignment to a primary type's calculated
value when Revit permits solving the exact scaling of one input of the same type.
For Material, pass the positive ID of an existing material in the source file. A non-shared project Material
parameter on a loaded family type also accepts `-1` to explicitly assign `By Category` through the same operation
as `Parameter.Set(ElementId.InvalidElementId)`.

For a supported project Image or Type Image built-in, pass the positive ID of a verified imported or locally
linked image in the project. Revit also persists a verified project level ID in these fields as an element
reference. An explicit `-1` assigns None and leaves a stored value. Linked images require the verified absolute
or relative local resource layout introduced for image links in Revit 2021. [Autodesk linking documentation](https://help.autodesk.com/cloudhelp/2021/ENU/Revit-HaveYouTried/files/GUID-52C359EE-4FBC-400F-9216-73CB0F578CBA.htm)

When writing to another directory, RvtIO checks the saved path of root project image links. It refuses a write
if a relative image path would resolve to a different file, or if the link's path layout cannot be verified.
This check belongs to every write that changes the directory, not only to parameter edits: `SaveAs` and `Rewrite`
refuse such a project with `relative-reference-relocation` too. An imported image and a write into the directory of
the source are not checked.

```sh
rvtio set-parameter project.rvt project.edited.rvt 42000 43000 "300 mm"
rvtio set-parameter project.rvt with-material.rvt 42000 43001 44000
rvtio set-parameter project.rvt with-global-value.rvt 45000 45000 "3.625 ft"
rvtio set-family-parameter cabinet.rfa cabinet.edited.rfa "Standard" 12345 "0.25 m²"
```

`HOST_AREA_COMPUTED` fails with `read-only-parameter`. Unsupported edits fail without creating an output.
An identical value produces a lossless copy. The input and existing destination files are preserved.

The command uses exit code 0 for success, 1 for invalid arguments, 2 for input or I/O errors, and 5 for a refused edit.
Low-level `Rewrite` accepts raw stream or record payloads and does not perform parameter editability checks.

## Editing a standalone family

`SetFamilyParameter` saves a new local Revit 2020–2027 `.rfa` without Revit installed. Pass the exact family type name,
the replacement value, and either the positive definition ID from the same family or the signed ID of a supported built-in.
The command also accepts the built-in name.

```sh
rvtio set-family-parameter door.rfa door.edited.rfa "900 x 2100" 12345 "Acoustic seal"
rvtio set-family-parameter fixture.rfa fixture.edited.rfa "Standard" ALL_MODEL_TYPE_COMMENTS "Inspection required"
rvtio set-family-parameter fixture.rfa fixture.elevated.rfa "Standard" FAMILY_FREEINST_DEFAULT_ELEVATION "300 mm"
```

```csharp
RvtWriter.SetFamilyParameter("door.rfa", "door.edited.rfa", "900 x 2100", 12345, "Acoustic seal");
```

Standalone families use the same input formats as projects. Existing family and shared parameters can supply type values
or instance defaults. The writer checks each active record for dependencies and refuses
read-only definitions, reporting parameters, direct writes to formula results, element associations, and
families with unverified dimension constraints or protection.
Mass, Force, Mass Density, Speed, Slope, and Time assignments update the matching type's `PartAtom`
entry when its stored display format can be verified. The same checks apply to values calculated by verified formulas.
Currency values take a finite invariant number with no currency symbol. Changes that Revit may suppress within
its numeric comparison tolerance are refused. Formulas use the unrounded stored value. With the verified two-decimal
currency format, `PartAtom` stores the rounded display value and its currency unit. Amounts rounded to zero use `0.00`.
Native checks covered first assignments, replacements, and ordered batches on active and inactive types and on
instance defaults. The 24 family-parameter files and 24 shared-parameter files matched `FamilyManager.Set` after
Save As and reopening without Audit in Revit 2020–2025 and 2027. The 2026-format files passed after upgrade in
Revit 2027. Eighty-eight invalid batches left the source bytes intact and created no output.
[Autodesk FamilyManager.Set reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/7fc40346-6188-66ff-4c00-bd4360e70c6f.htm)

An assignment to a verified formula input recalculates the selected type's dependent results in the same batch. This
also applies to formulas for instance defaults stored on that type. The writer checks saved results against the
formula, verifies that no affected parameter drives geometry or an association, and updates the corresponding
`PartAtom` values. It refuses results it cannot compute or store exactly. The verified cases include arithmetic
chains, measured values, Yes/No conditions, and text results.

The writer updates the selected type and its current-value copy if it is the active type. It also updates matching `PartAtom`
metadata and adds the missing entry when filling an empty, visible user text parameter. User and shared parameters must
have a stored row for the selected type. The source, any existing destination, and the active type selection are preserved.
For measured type parameters, a matching `PartAtom` entry requires a verified decimal display format. Decimal feet,
decimal inches, square feet, and cubic feet use the labels `'`, `"`, `SF`, and `CF`. RvtIO also handles the matching
format symbols when they appear in the displayed value. Use `ft`, `in`, `ft2`, and `ft3` in edit requests.
Family metadata follows the document's decimal point or comma. The verified grouping options use commas, dots,
spaces, or apostrophes, with groups of three digits or with two-digit groups to the left of the final three digits.
Unitless Number values use the document's decimal separator without digit grouping. Numeric edit requests still use
a decimal point and omit grouping separators. A Number value whose shortest round-trip text needs exponent notation
(for example `1E-05`) is refused, because how Revit writes it into the family metadata has not been verified.
For materials, `PartAtom` uses the name of the material read from the same document.
[Autodesk Units and FormatOptions guide](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Application_and_Document/Revit_API_Revit_API_Developers_Guide_Introduction_Application_and_Document_Units_html.html)

**Family Type** parameters in standalone families accept the element ID of a verified local nested type.
Family and shared definitions support first assignment and replacement on both type values and instance defaults. For a
shared definition, the metadata entry is identified by its GUID. Use the explicit string
`"-1"` to assign Revit's invalid element ID. Revit also stores an ID for a nested type in another category or
for a local material passed to this setter, and RvtIO supports those verified cases. The writer keeps the active
type's copy in sync and updates the visible type entry in `PartAtom` when the reference resolves to a nested type.
Revit omits that entry for `-1` and for material IDs. A new entry requires a verified metadata category label.
Missing element IDs are rejected. [Autodesk FamilyManager.Set reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/5004314a-c77f-e469-1e03-395f5e17de5a.htm)

The built-in type fields `ALL_MODEL_TYPE_COMMENTS`, `ALL_MODEL_DESCRIPTION`, `ALL_MODEL_MANUFACTURER`,
`ALL_MODEL_MODEL`, `ALL_MODEL_URL`, and `ALL_MODEL_COST` support first assignment and replacement in Generic Model,
Door, Window, and Furniture families. Doors also support `FIRE_RATING` (also named `DOOR_FIRE_RATING`).
Door identity writes accept the verified layout for dimension constraints independent of the edited parameter. The writer checks parameter
references from dimension labels, constraint expressions, family formulas, and element associations.

On first assignment the other named types receive the Revit default, empty text or zero. If a door or window
also contains an unnamed type, Revit copies each assigned identity value to it. The command targets the named
type. Assign an empty string to write empty built-in text. Cost takes a finite invariant number and requires
the default two-decimal currency format with `$` in the family metadata. A Cost change within Revit's numeric
comparison tolerance is refused.

In Generic Model families, `FAMILY_FREEINST_DEFAULT_ELEVATION` takes a length with an explicit unit. The type table must contain the parameter
and the family must use a verified decimal length format for the matching `PartAtom` value.
`ALL_MODEL_TYPE_IMAGE` takes the positive ID of a verified raster image already imported into the same family.
An explicit `-1` sets it to None. On first assignment the other types receive None, and the imported image data is preserved.
Revit accepts imported images in family documents. [Autodesk ImageTypeOptions reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/47f3832b-9cf5-4628-214d-7e7e7d705393.htm)

A visible non-shared custom type parameter of data type **Image** also accepts the positive ID of a verified raster
image imported into the same family. RvtIO replaces the selected type's saved reference and the image name in its
`PartAtom` entry. On the active type it updates the current-value copy too. Assign `"-1"` explicitly to select None.

The command uses the same exit codes as `set-parameter`.

## Applying a parameter batch

`SetParameters` edits one local RVT and `SetFamilyParameters` edits one local RFA. Each takes a non-empty list and
applies it in order. Repeated edits to the same parameter see the previous value in the batch. The writer publishes
the new file only after every operation and the final container pass validation. A refused edit leaves the source
untouched and produces no destination file. The writer plans each edit with its own pass over the project or family
records, so the time of a batch grows in proportion to the number of edits and to the size of the file.

```csharp
RvtWriter.SetParameters("project.rvt", "project.edited.rvt",
[
    new RvtParameterEdit(42000, 43000, "Inspected"),
    new RvtParameterEdit(42000, 43001, null),
]);

RvtWriter.SetFamilyParameters("door.rfa", "door.edited.rfa",
[
    new RvtFamilyParameterEdit("900 x 2100", 12345, null),
    new RvtFamilyParameterEdit("900 x 2100", 12346, "300 mm"),
]);
```

```sh
rvtio edit-parameters project.rvt project.edited.rvt project-edits.json
rvtio edit-parameters door.rfa door.edited.rfa family-edits.json
```

Each manifest is a JSON array. An RVT entry has `elementId`, numeric `parameterId`, and `value`.
An RFA entry has `typeName`, signed numeric `parameterId`, and `value`.
For example, an RVT assignment is `{"elementId":42000,"parameterId":43000,"value":"Inspected"}`.
An RFA reset is `{"typeName":"900 x 2100","parameterId":12345,"value":null}`.

Pass a string to assign a value using the same formats and write checks as single edits. To clear or reset,
pass an explicit JSON or C# `null`. `SetParameter` and `SetFamilyParameter` take no `null` value and throw
`ArgumentNullException`; clear through a batch.

| File | Effect of `null` |
| --- | --- |
| RVT | Removes the stored value of a writable, supported shared parameter with `HideWhenNoValue` and a matching project binding. This corresponds to Revit API `Parameter.ClearValue()`. Supported owners include straight walls, basic wall types, rooms, top-level loaded family instances, and primary and directly linked derived symbols of loaded families. |
| RFA | Resets supported user and shared text parameters to empty text and material parameters to `By Category`. This corresponds to `FamilyManager.Set(parameter, "")` or `FamilyManager.Set(parameter, ElementId.InvalidElementId)`. Built-ins and numeric values require an explicit assignment. |

An empty string in an RVT is a text assignment where supported. It is distinct from clearing the value.
Revit's `ClearValue()` returns false for values declared inside a loaded family, even when the shared definition
has `HideWhenNoValue` and the same definition is also bound in the project. The batch rejects those clears,
along with built-ins, non-shared parameters, and shared parameters with the flag disabled.
For a loaded type with derived symbols, use the element ID of the symbol whose value you want to clear.
Revit clears the primary value and each local derived value independently. Include one edit per symbol to clear both
the primary value and local copies in the same batch. The writer verifies the direct links and the local parameter stores.
An assignment to a primary or derived symbol also uses its own ID and edits its local value.
The batch command uses the same exit codes as `set-parameter`.

The batch round-trip checks used Revit 2020, 2022 and 2027. The API values in the edited RVT and RFA matched the
Revit-authored reference files both at the initial open and after SaveAs and reopen, with Audit disabled.

For shared project instance bindings on loaded families, these checks covered first assignment, replacement, and
first empty text. They included Text, Integer, Yes/No, Number, Length, and Material with both settings of
`HideWhenNoValue`. This scenario still lacks direct checks in Revit 2021, 2023, 2024, 2025, and 2026.

The Mark and Type Mark checks in the same three Revit releases covered first assignment, replacement, and empty
text, including a separate Type Mark assignment to a derived symbol. Direct checks for these assignments are pending
in Revit 2021, 2023, 2024, 2025, and 2026.

The Comments and type identity checks compared first assignment, replacement, and empty text against values
written by the Revit API in Revit 2020–2025 and 2027. Each check opened the edited RVT without Audit and read
the values again after SaveAs and reopening without Audit. The cases included basic wall types, straight walls,
and instances and symbols of a loaded Generic Model family. The same releases also verified first assignment and
replacement on a derived symbol and checked the values of the other symbols. Revit 2027 upgraded and verified
the same cases in controlled 2026-format files. Direct checks in Revit 2026 are pending.

Revit 2020–2025 and 2027 verified 15 Comments assignments each on hosted Door and Window instances and
unhosted Furniture instances. First assignment, replacement, and empty text matched the API-authored files,
including `HasValue`, symbol and host IDs, and instance locations, after SaveAs and reopening without Audit.
Revit 2027 verified the 2026-format fixtures after upgrading them.

The standalone Generic Model built-in checks covered first assignment and replacement in the active type and
another type, empty strings, and the default values that Revit records for the other types. Revit 2020–2025 and
2027 opened, saved, and reopened the edited RFA files without Audit. All values and `HasValue` flags matched
the files written by `FamilyManager.Set`. Revit 2027 also verified the 2026-format fixtures after upgrading
them. Direct verification in Revit 2026 is pending.

Revit 2020–2025 and 2027 verified first assignment and replacement of Default Elevation and Type Image in the
active and inactive types, and resetting a recorded image to None. The edited values, the active type, and
`HasValue` matched the API-authored references after open, SaveAs, and reopen without Audit. Revit 2022 and
2027 also checked setting an unassigned image to None. Revit 2027 verified the cases in the 2026 format after
upgrading the files. Direct verification in Revit 2026 is pending.

Project Image and Type Image checks covered first assignment, replacement, and explicit None on straight
walls, rooms, top-level Generic Model instances, and basic wall types. Revit 2020–2025 and 2027 verified the
image IDs and `HasValue` after resaving and reopening without Audit. Revit 2027 also verified the 2026-format
files after upgrade. The measured wall and room geometry matched the native results. Batches with a missing
image ID, a `null` clear, or a Type Image edit on the tested read-only loaded type preserved the original bytes
and created no output in every format.

The same four assignment cases were verified on top-level Door, Window, and Furniture instances. Revit
2020–2025 and 2027 opened, resaved, and reopened the files without Audit. Revit 2027 verified the 2026 format
after upgrade. All 48 assignments per format matched the native image IDs, `HasValue`, instance hosts, and
bounds. The tested loaded family types reported Type Image as read-only throughout these checks.

The project Image checks also covered first assignment and replacement with local absolute image links,
local relative image links, and level references. Revit 2021–2025 and 2027 verified 24 assignments per
release on straight walls, rooms, top-level Generic Model instances, and basic wall types. The files opened,
resaved, and reopened without Audit. Revit 2027 also verified the 2026-format files after upgrade.
Revit 2020 verified the eight level assignments. Revit 2021 introduced
image links. The linked images reloaded successfully in the applicable releases. Batches with missing IDs
left their source bytes intact and created no output file. Relocation that changed a relative image's target
was also refused. Revit 2025 verified the copy between sibling folders that resolved the same relative
image path to the same file, including reloading both relative and absolute links after resaving.

For non-shared custom type Image parameters in standalone Generic Model families, Revit verified the first local
image, replacement, and explicit None in both the active type and another type. Revit 2020–2025 and 2027 opened,
resaved, and reopened the results without Audit. Revit 2027 verified the 2026-format files after upgrade. The API
returned the expected image IDs, `HasValue`, and active type. The image names in `PartAtom` matched the native
results. A batch containing a missing image ID or a `null` clear left the source bytes intact and created no output
file in every format.

Standalone Door, Window, and Furniture identity assignments were verified in Revit 2020–2025 and 2027 after opening,
saving, and reopening without Audit. The checks covered first assignment, replacement, and empty values,
and compared formula values, dimensions, the active type, and the unnamed type against native files.
Revit 2022 and 2027 also checked assignments while the unnamed type was active. Revit 2027 verified both
cases in files upgraded from the 2026 format. Direct verification in Revit 2026 is pending.

For loaded types in RVT, Revit 2020–2025 and 2027 verified 220 assignments each on Door, Window, Furniture,
and Generic Model symbols. The tests covered first assignment, replacement, and empty values in the applicable
project and imported family stores. Values, `HasValue`, and sibling types matched the API-authored files after
SaveAs and reopening without Audit. Revit 2027 also verified the 2026-format cases after upgrading the files.

For imported Generic Model identity fields with derived symbol copies, the values and instance geometry matched
the API-authored files after SaveAs and reopening without Audit. Revit 2020, 2022, and 2027 checked all six
Generic Model type identity fields. Revit 2025 checked Type Comments and Cost. Revit 2027 also verified all six
fields after upgrading fixtures from the 2026 format. Direct `Set` calls on the derived copies reported success.
Revit saved the previous values in these fixtures. Use the primary type ID for imported identity edits.
Direct verification in Revit 2021, 2023, 2024, and 2026 is pending for this scenario.

Independent global values matched native first assignments and replacements after opening, SaveAs, and
reopening without Audit in Revit 2020–2025 and 2027. The checks covered Text, Integer, Yes/No, Number, Length,
Area, Volume, and Angle, and compared the source and result of an unrelated formula. Revit 2027 also verified
the 2026-format fixtures after upgrading them. In Revit 2027, another fixture preserved an unrelated wall association through the same
round-trip and verified whole-batch refusal when an edit targeted the global value driving that wall.

Independent Currency checks covered the first assignment, a negative replacement, and both edits in an
ordered batch. The 24 edited files matched the API after Save As and reopening without Audit in Revit 2020–2025
and 2027, including 2026-format files upgraded in Revit 2027. Seven batches per format refused an invalid value,
a clear, or an ambiguous numeric change. The source bytes were preserved and no output was produced.
Two additional Revit 2025 files retained Currency changes just outside the comparison tolerance after reopening.

Currency formula checks compared first assignments, replacements, and ordered batches in 24 files. The fixtures
covered arithmetic, a comparison, a conditional branch, Currency and Number results, and a formula-driven
Currency result associated with a Generic Model type's built-in Cost. Revit 2020–2025 and 2027 retained all values
and the Cost association after Save As and reopening without Audit. The 2026-format files passed after upgrade
in Revit 2027. Forty batches that included a direct assignment to a calculated value or controlled Cost,
an ambiguous numeric change, or a Currency clear were refused with the source bytes intact and no output.

Ordinary element writes alongside global associations matched 168 Revit API assignments and clears across the
eight formats. The checks covered walls, a wall type, a loaded family instance, and a loaded family type, including
editing one parameter while preserving an association on another parameter of the same element. Revit 2020–2025
and 2027 opened, saved, and reopened the files with Audit disabled. Revit 2027 upgraded and verified the
2026-format files. In every format, batches targeting a controlled wall, instance, or type parameter left the
source bytes intact and created no output. A mixed batch with a global formula input and an independent wall
comment matched Revit 2021 in either edit order.

Global assignments that update project properties matched 136 Revit API assignments and 224 linked property
updates across the eight formats. The checks covered all eight supported global data types on wall properties,
one global text value shared by multiple walls, a wall type and a loaded family instance, and a recalculated
integer formula driving a property. Empty text stayed present after the assignment. Revit 2020–2025 and 2027
opened, saved, and reopened the files with Audit disabled. Revit 2027 upgraded and verified the 2026-format files.
Four invalid batches per format left the source bytes intact and created no output.

Shared Currency checks covered first assignment, replacement, clearing, and ordered batches on walls and basic wall
types, including an explicit first zero and updates through an existing global association. The 56 edited files matched
the API values, presence flags, and associations after Save As and reopening without Audit in Revit 2020–2025 and 2027.
Revit 2027 also verified files upgraded from the 2026 format. Forty invalid batches preserved the source bytes and
created no output.
Three further Revit 2025 files verified first assignments of tiny nonzero values and updates from a recalculated
Currency global formula after Save As and reopening without Audit.

Shared Currency checks also covered project bindings on rooms, top-level loaded family instances, and primary and
directly linked derived family symbols. The matrix used shared definitions with both settings of `HideWhenNoValue`,
including a first value of zero, replacement, clearing hidden values, ordered batches, and updates through an
existing global association. For 49 edited files, the affected element records matched native writes in
Revit 2020–2025 and 2027. Revit 2027 verified the seven 2026-format files after upgrade. All 56 projects matched the
expected API values, presence flags, associations, room area, and instance bounds after Save As and reopening
without Audit. Thirty-two refused batches preserved the source bytes and created no output.

Independent Mass, Force, Mass Density, Speed, Slope, and Time checks covered 64 files. Project fixtures included
shared values on walls and wall types, family-defined values on loaded primary types and top-level instances,
and independent global parameters. Standalone family fixtures covered custom and shared type values and instance
defaults. First assignments, negative replacements, and ordered batches matched the Revit API. Project-bound
values with `HideWhenNoValue` also matched after clearing. For 56 files, affected records matched native writes
in Revit 2020–2025 and 2027. Revit 2027 verified eight 2026-format files after upgrade. API values and family
`PartAtom` entries matched after Save As and reopening without Audit. Fifty-six refused batches preserved the
source bytes and created no output. Three additional Revit 2025 family files matched stored values and
`PartAtom` at a negative value that rounds to zero and at positive and negative decimal rounding midpoints
after Save As and reopening without Audit.

Formula checks for the same six specs covered 80 projects and standalone families. They compared first assignments,
replacements, ordered batches, an exact loaded type inverse, and updates through existing global associations to
project-bound and family-declared values. The fixtures used scaling, sums of compatible parameters, and measured
constants. For 70 files, the affected records matched Revit 2020–2025 and 2027. Revit 2027 verified ten files after
upgrading them from the 2026 format. All files matched the API values and, for families, their `PartAtom` entries
after Save As and reopening without Audit. Fifty-six refused batches preserved the source bytes and created no output.

Number and Slope association checks covered first edits, replacements, and ordered batches in 24 projects.
The fixtures included direct and formula-driven associations on wall instances, basic wall types, and loaded
family elements. Revit 2020–2025 and 2027 matched the saved parameter values and dependent formulas after
Save As and reopening without Audit. In those releases the affected records in 21 files also matched native
writes byte for byte. Revit 2027 verified the three 2026-format projects after upgrade. Forty-eight refused
batches preserved the source bytes and created no output.

Mixed-unit formula checks covered eight products and quotients in 48 edited files. The tests changed global
parameters and loaded family type and instance inputs in projects, and type values and instance defaults in
standalone families. Replacement and ordered batch results matched the Revit API after Save As and reopening
without Audit. The affected records in 42 files matched native Revit 2020–2025 and 2027 writes byte for byte.
The six 2026-format files were verified after upgrade in Revit 2027, with projects and families checked in
separate sessions. Thirty-two invalid batches preserved the source bytes and created no output.

Decimal imperial formats were verified in 96 edited standalone family files. The tests covered first edits from defaults,
replacements on active and inactive types, and ordered batches. They checked user-defined Length, Area, and Volume,
dependent formula results, and Default Elevation. For 84 files, family records matched native Revit 2020–2025
and 2027 writes byte for byte. Revit 2027 verified the twelve 2026-format files after upgrade. API values and
`PartAtom` entries matched after Save As and reopening without Audit. Ninety-eight refused batches preserved the
source bytes and created no output, including files with unrecognized unit labels or stale displayed values.
Six project files also verified assignments in inches to Length globals and loaded family type and instance parameters
in Revit 2020 and 2025. The affected records and formula results matched native writes and survived Save As and
reopening without Audit.

Decimal separators and digit grouping were verified in 144 edited standalone family files. The tests compared
first assignments, replacements on active and inactive types, and ordered batches for Length, Area, Volume, Angle,
Currency, and Number, including dependent formulas, Default Elevation, and Cost. Family records in 126 files matched
native Revit 2020–2025 and 2027 writes byte for byte. Revit 2027 verified the eighteen 2026-format files after upgrade.
API values and `PartAtom` entries matched after Save As and reopening without Audit. All 146 refused batches preserved
the source bytes and created no output, including cases with an incorrect grouping separator or stale metadata.
Three project files in Revit 2025 also matched the internal value and affected records after assignments in degrees,
Save As, and reopening without Audit.

Global assignments to loaded family parameters matched 240 Revit API assignments across the eight formats.
The fixtures included shared family definitions, empty text, unitless and measured values, links to recalculated
global formula results, and type inputs consumed by formulas on multiple placements with separate local inputs.
The affected placements retained their values after SaveAs and reopening without Audit in Revit 2020–2025 and
2027. Revit 2027 upgraded and verified the 2026-format files. In each format, batches with a geometry-driving
global, a formula-driven global, or a direct write to an associated instance or type parameter were refused
without changing the source or creating an output.

Global Text assignments to built-in properties matched 48 Revit API assignments and 132 linked property
updates across the eight formats. The fixtures covered the first nonempty value, replacement, and explicit
empty text. Revit 2020–2025 and 2027 verified the values and association IDs after SaveAs and reopening
without Audit. The same checks covered 16 Currency assignments that updated Cost on Generic Model types.
Revit 2027 upgraded the controlled 2026-format fixtures. Seventy-six batches containing a direct write to an
associated property, an unverified geometry-driving global, or an invalid Currency value preserved the source
bytes and created no output.

The Door, Window, and Furniture type fixtures matched 96 global assignments and 768 linked built-in writes
against Revit. They covered first nonempty values, replacements, empty text and zero Cost in both the typed
and named stores. The Generic Model fixtures with derived symbol copies matched 144 more global assignments
and 480 linked built-in writes. They covered a primary symbol, a derived symbol, and both symbols linked to
the same global. Unassociated symbol records matched the source byte for byte. Revit 2020–2025 and 2027 read
the values and association IDs before and after SaveAs with Audit disabled. Revit 2027 upgraded the
2026-format fixtures. All 88 rejected batches preserved the source bytes and left no output.

Non-shared project parameter checks matched 264 direct assignments and 48 global assignments that updated
72 linked values. The Generic Model fixtures covered Text and Length on top-level instances and Text and
Material on primary and directly derived symbols. They included first assignment, replacement, explicit
empty text, zero Length and Material set to `By Category`. Revit 2020–2025 and 2027 checked the values
after opening, SaveAs and reopening without Audit, with the 2026-format fixtures upgraded in Revit 2027.
The 48 refused batches preserved the source bytes and created no output.

Global formula checks in Revit 2020–2025 and 2027 compared 12 input changes and 24 recalculated values per
release, including chained formulas and mixed length, area, angle, and scalar operations. Both opening and
reopening after SaveAs used Audit disabled. Revit 2027 also verified the 2026-format fixtures after upgrading
them. Additional volume calculations and constants with length and angle units passed the same round-trip in
Revit 2020 and 2027. Those additional fixtures still lack direct checks in Revit 2021–2026.

The conditional and integer formula fixtures checked 12 input assignments and 38 formula results per
format against the Revit API after opening, SaveAs, and reopening with Audit disabled. The checks ran
directly in Revit 2020–2025 and 2027. For the 2026 format, Revit 2027 upgraded and verified the files.
Four negative batches per format preserved the source bytes and left no output file. Revit 2027 also
verified separate changes to the results of `and` and `or`, and preserved the selected `if` result while
the inactive branch had a zero divisor, after SaveAs and reopening without Audit.

The mathematical function fixtures compared 12 input assignments and 38 formula result values per format
against Revit API assignments. Revit 2020–2025 and 2027 opened, saved, and reopened edited files with Audit
disabled. Revit 2027 also verified upgraded 2026-format fixtures. Three batches per format refused unverified
formula domains in that verification run. Subsequent coverage verified the retained results for those domains.

The retained-result fixtures compared 32 assignments and 57 formula result values per format against the
Revit API, including changes between inputs that retain results and inputs that resume recalculation. Revit
2020–2025 and 2027 opened, saved, and reopened the files with Audit disabled. Revit 2027 verified upgraded
2026-format fixtures and five additional results for Boolean formulas with unavailable operands in both
positions. For each format, a batch containing an allowed input edit and an attempted write to a calculated
result left the source bytes intact and created no output file.

Standalone family formula checks compared 20 input assignments and 28 formula result values per format
against the Revit API. The selected type's values and matching `PartAtom` entries agreed with native files,
including type parameters and instance defaults. Revit 2020–2025 and 2027 opened, saved, and reopened the
files with Audit disabled. Revit 2027 upgraded and verified the 2026-format files. A mixed batch that tried
to assign a formula result preserved the source bytes and left no output file in each format.

Loaded family type formula checks compared 194 input assignments against Revit API assignments across the
eight formats. They included formulas on primary types and derived symbols, stored results on top-level
instances, and recalculation using local instance inputs. Revit 2020–2025 and 2027 opened, saved, and reopened
the files with Audit disabled. Revit 2027 upgraded and verified the 2026-format files. A mixed batch with an
invalid value left the source bytes intact and created no output file in each format.

Loaded family instance formula checks compared 24 integer input assignments across the eight formats. The fixtures
covered assigning an input that matched the type default before the edit, replacing a local input, and setting it back
to the default. Only the selected instance record changed, and its stored input and formula result matched
the Revit API. Revit 2020–2025 and 2027 opened, saved, and reopened the files with Audit disabled. Revit 2027
upgraded and verified the 2026-format files. In each format, a batch with invalid input or a direct write to
the read-only instance formula result left the source bytes intact and created no output file.

Loaded family Currency checks covered 65 edited project files. The fixtures exercised first assignment and
replacement for shared and non-shared parameters, ordered batches, an exact type formula inverse, existing
global associations, and an instance formula that retains its saved result within Revit's comparison tolerance.
For 57 files, the affected element records matched native writes in Revit 2020–2025 and 2027. Revit 2027
verified the eight 2026-format files after upgrade. All 65 files retained the expected API values after
Save As and reopening without Audit. Forty refused batches preserved the source bytes and created no output.
In Revit 2025, `Parameter.ClearValue()` returned `false` for a family-declared shared Currency parameter
with `HideWhenNoValue` on a primary type and on a placed instance. Both existing values remained after
saving and reopening without Audit. [Autodesk shared parameter documentation](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API/files/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Parameters/Revit_API_Revit_API_Developers_Guide_Basic_Interaction_with_Revit_Elements_Parameters_Shared_Parameters_html.html)

Direct writes to loaded type formula results were checked against 118 Revit API assignments across the eight
formats. The checks covered integer rounding and exact real scaling, dependent formulas, derived symbols, and
stored instance results. Revit 2020–2025 and 2027 opened, saved, and reopened the files with Audit disabled.
Revit 2027 upgraded and verified the 2026-format files. Mixed batches containing an invalid or unsupported
inverse left the source bytes intact and created no output file.

Standalone non-shared Family Type assignments were checked in all eight file formats. The checks covered
first assignment from `-1`, replacement, explicit `-1`, another category's nested type, and a local material.
Revit 2020–2025 and 2027 opened, saved, and reopened the files with Audit disabled. The API values and
`PartAtom` entries agreed after saving. Revit 2027 upgraded and verified the 2026-format files. Native Revit
2026 verification is pending. In every format, batches with a missing reference or a null clearing request
left the source bytes intact and created no output file.

Shared Family Type assignments were also checked in all eight formats. For each format, the API comparison
covered first assignment, replacement, explicit `-1`, and reassignment to type values and instance defaults
on both active and inactive types. The checks compared the shared GUIDs and `PartAtom` values after opening,
saving, and reopening without Audit in Revit 2020–2025 and 2027. Revit 2027 verified the converted 2026-format
fixtures after upgrading them. Native Revit 2026 verification is pending.

Project Family Type checks compared 16 assignments per format on primary types and top-level instances. They
covered family and shared definitions, first assignment, replacement, material IDs, and explicit `-1`. Revit
2020–2025 and 2027 opened, saved, and reopened the results without Audit. Revit 2027 verified the 2026-format
fixtures after upgrading them. Revit 2025 also verified a first local instance value over an inherited default.
In each format, batches with a missing ID, a family symbol the writer could not verify as a safe reference,
or a null clearing request left the source bytes intact and created no output file.

Shared project bindings for Family Type were checked with 40 assignments and five clears per format. The
fixtures covered loaded family types and instances, basic wall types, straight walls, and rooms. They verified
first assignment, replacement, material IDs, explicit `-1`, and clearing a shared value with `HideWhenNoValue`.
Revit 2020–2025 and 2027 opened, saved, and reopened the files without Audit. Revit 2027 verified the 2026
file format after upgrade. Revit 2025 separately verified first assignment, replacement, and clearing of
project-bound material references on a primary symbol and its two derived symbols. The tested dimensions
of the placed instances stayed the same. In each format, rejected batches preserved the source bytes and left no output.

Family-declared references on primary types with directly linked derived copies were checked in all eight
file formats. First assignment, replacement and explicit `-1` on the primary type, plus local assignment
and reset on a placed instance, matched the API. Revit 2020–2025 and 2027 opened, saved, and reopened the
results without Audit. Revit 2027 verified the 2026 format after upgrade. The instance dimensions stayed
the same. The corresponding shared family definitions were checked in Revit 2020, 2025 and 2027 and with
the 2026 format after upgrade in Revit 2027. Direct checks of shared definitions in Revit 2021–2024 and
2026 are pending. Across the tested releases, direct calls on the derived symbols returned success but
the inherited value persisted. A batch containing such a direct edit left the source bytes intact and
created no output file.

Ordinary active primary family types used as reference targets were checked in all eight project formats.
Each check compared first assignment and replacement on primary types and top-level instances, for both a family
declaration and a shared project binding. It also checked inherited copies on derived types, explicit `-1`, and
clearing the shared bindings. Revit 2020–2025 and 2027 opened, saved, and reopened the files with Audit disabled.
Revit 2027 upgraded and verified the 2026-format files. The API returned the expected values and the measured
instance geometry stayed the same. Batches containing a missing target left the source bytes intact and created
no output file.

For targets that contain another shared family, first assignment and replacement matched the native API on
primary types and instances, for family-declared and shared project parameters. Revit 2020–2025 and 2027 saved
and reopened the files without Audit. Revit 2027 verified the 2026-format files after upgrade. Revit 2025
separately verified a target whose dependency was stored as a project parameter value. Both verification sets
checked the API values and measured instance geometry. Direct verification of the stored-parameter dependency
variant in other releases is pending.

The direct shared nesting case was checked with first assignments and replacements of the independent
family-declared type parameter. Revit 2020–2025 and 2027 confirmed the API values and instance geometry after
opening, saving, and reopening with Audit disabled. Revit 2027 also checked the 2026-format files after upgrade.
Each batch containing the corresponding instance assignment was refused, leaving the source bytes intact and
creating no output file. In Revit 2025, a separate case started with a private nested type value and a derived
placement. Replacing that value changed the same primary and derived type records as Revit. The saved reference
and the instance geometry matched the API after reopening without Audit. Direct checks of the derived-placement
variant in other releases are pending.

Shared project bindings were also checked with a target type from a different category. First assignment and
replacement matched the API on loaded family types and instances, basic wall types, straight walls, and rooms,
with and without `HideWhenNoValue`. Revit 2020–2025 and 2027 saved and reopened the results without Audit.
Revit 2027 verified the 2026-format files after upgrade. The tested wall location, bounds, and room measurements
matched the native results. A batch containing a missing target was rejected in every format without changing its
source or creating an output file.

Revit 2025 also verified first assignment and replacement of local shared project values on two derived
symbols using active primary symbols as targets. The primary symbol retained its existing value. Both local
values persisted after saving and reopening without Audit. Direct checks of this variant in other releases are pending.

For shared project bindings on a loaded family type and instance, Revit accepted an active target type that
contained the referring family, the referring primary type itself, and another primary type of the same family.
First assignment and replacement matched the native values and measured instance geometry after opening, saving,
and reopening without Audit in Revit 2020–2025 and 2027. Revit 2027 verified the 2026-format files after upgrade.
In every format a mixed batch with a missing target preserved the original bytes and created no output file.
Revit 2025 also confirmed references to a family type containing a shared nested family on loaded family types
and instances, basic wall types, straight walls, and rooms, with and without `HideWhenNoValue`. The values and
measured wall and room geometry matched after resaving and reopening without Audit. Direct checks of this
additional variant in other releases are pending.

For references to a basic wall type, rebar cover type, level, loadable family, or top-level family instance, the
matrix checks first assignment and replacement on a shared project-bound type parameter and a shared
project-bound instance parameter. Revit 2020–2025 and 2027 saved and reopened the results without Audit.
Revit 2027 also checked the 2026-format files after upgrading them. The API returned the expected target IDs
and classes, the measured instance geometry matched, and the checked files opened without warnings. Batches
containing a missing target preserved their source bytes and created no output file in every format.
[Autodesk Parameter.Set(ElementId) reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/992097b4-0477-249f-581d-7903dfafd66d.htm)

The same five targets were also verified on loaded family types and instances, basic wall types, straight walls,
and rooms, with and without `HideWhenNoValue`. The matrix compared 100 first assignments and replacements per
format, including values read before and after resaving the offline results. Revit 2020–2025 and 2027 opened
and reopened them without Audit. Revit 2027 verified the 2026-format files after upgrade. Wall and room geometry
matched the native controls. Mixed batches with a missing target preserved the source and created no output file.

The single-assignment round-trip checks open edited projects and families with Audit disabled, read values through the Revit API,
then save and reopen in Revit. These checks ran in Revit 2020–2025 and 2027. For the 2026 format, Revit 2027 upgraded and verified a project
sample from Autodesk and controlled project and family fixtures converted to the 2026 format.

## Remaining parameter coverage

| Case | Current boundary |
| --- | --- |
| Read-only values | Revit API rejects assignments to parameters such as computed wall area. RvtIO also rejects these assignments. |
| Wall Unconnected Height | The Revit API permits some assignments on unconnected walls. The writer requires further coverage of the affected wall, opening, and geometry records. |
| Clearing an RVT value | Revit API `ClearValue()` requires a shared definition with `HideWhenNoValue`. RvtIO also checks the binding and stored value layout. |
| Additional writable RVT built-ins and element classes | The RVT parameter writer supports the combinations in the table above. Type identity assignments on Door, Window, and Furniture types with derived symbol copies still require coverage. |
| Additional built-ins in standalone families | The verified Generic Model, Door, Window, and Furniture fields are listed above. Other built-ins, other family categories, and selecting an unnamed family type still require coverage. |
| User Currency parameters in standalone families | RvtIO supports verified type values, instance defaults, and formula dependencies. Additional Currency display formats still require coverage. |
| Family-declared Currency parameters in projects | RvtIO supports the verified primary type and top-level instance assignments, exact type formula inverses, and existing global associations described above. Other formula paths and geometry dependencies still require coverage. |
| Project-bound Currency parameters | RvtIO supports verified shared values on walls, basic wall types, rooms, and loaded family types and instances. Non-shared bindings and other owner classes still require coverage. |
| Additional measurable specs and units | RvtIO supports the input units, display formats, and formula combinations listed above. Further measurable specs and combinations still require coverage. Feet and fractional inches, identical decimal and grouping separators, and other display symbols in standalone family metadata also require verification. |
| Invalid standalone image references | Revit 2022 and 2027 rejected a missing element ID and an existing family element that was outside the image class. RvtIO requires a local imported image with a verified raster record, or the explicit None value. |
| Standalone instance and shared Image definitions | The Revit API prohibits creating instance Image parameters. Revit 2025 also rejected the tested attempt to create a shared type definition. RvtIO supports the non-shared type scope described above. [Autodesk FamilyManager.AddParameter reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/3ac89d60-4b71-694f-002f-125d2e6565fc.htm) |
| Linked images in standalone families | Revit 2025 rejected `ImageTypeSource.Link` when creating an image in a family document. The verified standalone Image path uses imported images. [Autodesk ImageType.Create reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/31bb13e3-e8f5-cb66-18d0-619578d56cde.htm) |
| Project Image targets and loaded family types | Revit exposes Type Image as read-only on the tested loaded family types. A missing ID was accepted during the Revit 2025 transaction and read back as `-1` on reopen. RvtIO refuses missing IDs and unverified target or external resource layouts. [Autodesk ImageTypeOptions reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/7dda4131-548f-7c39-4dcd-ba9b85846018.htm) |
| Additional parameter types and references | RvtIO supports the verified material, standalone family type image, and Family Type assignments described above. Revit 2025 accepted an ordinary top-level symbol even when the API candidate list omitted it. Revit activated inactive targets in both family-declared and project-bound tests. RvtIO still needs coverage for that regeneration, dependencies represented by unresolved `NestedFamilyTypeReference` proxies, and other combinations of categories and parameter definitions. In the shared nesting test, Revit committed the independent type assignment and rolled back the corresponding instance assignment after `Parameter.Set` returned true. RvtIO supports the described type scope. A return path without the verified direct nesting remains outside that scope. After a test assignment of a missing ID, the API read back the previous value on reopen. [Autodesk NestedFamilyTypeReference reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/ff71e3b0-4300-7d04-1356-a045b9a90407.htm) |
| Driven global parameters | Revit API rejects direct assignments to global parameters driven by a formula or reporting dimension. [Autodesk GlobalParameter.SetValue reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/df012c22-6e65-8de5-1057-f15660d02288.htm) |
| Global parameters with dependents | Revit API can assign a global value that drives a formula, dimension, or element property. RvtIO recalculates verified formulas and updates verified project-bound or geometry-independent loaded family parameters and the built-in properties described above. Dimension recalculation, family geometry updates, and other unverified formula or association layouts still require coverage. |
| Global associations on built-in fields | Revit 2020 and 2021 refused the tested wall and family instance Comments associations. Revit 2022–2025 and 2027 accepted them. The tested wall Mark, BasicWallType Type Comments, Manufacturer, and Model, and type URL on Generic Model, Door, Window, and Furniture could not be associated in the tested releases. Derived Door, Window, and Furniture symbol associations and other family categories still require coverage. |
| Family formulas | RvtIO can recalculate verified dependencies of inputs in standalone families and on primary types and top-level instances of loaded families. Direct assignments to standalone formula results are refused by the writer. In Revit 2025, `FamilyManager.Set` returned without an exception on the tested Mass and Slope results and left both the inputs and results unchanged after Save As and reopening without Audit. RvtIO supports the loaded type inverses described above. Nonlinear inverses, real-valued inverses that depend on the starting value, unverified instance storage, and geometry-driving formulas still require coverage. [Autodesk FamilyManager.Set reference](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/7fc40346-6188-66ff-4c00-bd4360e70c6f.htm) |
| Geometry, formulas, groups, and other dependencies | The writer refuses unverified family geometry dependencies, formulas and associations, and unverified project model contexts. These checks are RvtIO write restrictions. |

## Write-capability matrix

Every write call inspects the source file first and checks the requested operation against the capability matrix:

| Dimension | Supported | Refused |
| --- | --- | --- |
| File kind | Local project (`.rvt`, `.rte`) and family (`.rfa`, `.rft`) containers (`RvtFileKind.Project`, `RvtFileKind.Family`) | Containers whose kind cannot be identified (`unknown-file-kind`) |
| Sharing and hosting | Non-workshared local files (`Worksharing: Not enabled`, `IsSingleUserCloudModel: False`) | Workshared central or local copies (`workshared-model`), cloud models (`cloud-model`), or missing/unrecognised `BasicFileInfo` sharing/hosting/release fields (`missing-metadata`) |
| Release and ID width | Releases `2020` to `2023` (4-byte element IDs) and `2024` to `2027` (8-byte element IDs) when `BasicFileInfo` and `Formats/Latest` agree | Releases outside `2020` to `2027` (`unsupported-release`), unrecognised schemas (`unrecognised-release`), or saved/schema mismatches (`release-mismatch`) |
| `SaveAs` | All streams, storages, storage directory-entry metadata (`CLSID`, `StateBits`, timestamps), external references, preview images, and unknown payloads copied with sector size (`512` or `4,096` bytes) preserved | Truncated containers (`RvtInputException`), corrupt page parity (`corrupt-page-parity`), or cross-directory relocation that changes relative external reference targets (`relative-reference-relocation`, `unreadable-transmission-data`) |
| `PagedStreamRewrite` | Replacing the uncompressed payload of `Formats/Latest` or `Global/<Name>` while preserving stream prefixes, gzip framing, page parity, and container compatibility | Unreadable `Global/*` streams (`unreadable-paged-stream`) or malformed/incompatible replacement `Formats/Latest` schemas (`invalid-schema-payload`, `release-mismatch`, `incompatible-schema-payload`) |
| `PartitionRecordRewrite` | Replacing targeted record bodies inside `Partitions/<n>` (`101`, `102`, `103`) while preserving untouched blocks, loaded families, channels, and unknown class payloads | Missing framing marker classes (`missing-framing-markers`), missing partition streams (`missing-partitions`), framing errors (`partition-framing-failed`), or mismatched input Adler-32 checksums (`partition-checksum-failed`) |

## Framing, checksums, and preservation invariants

Cross-stream tables in a Revit container (`Global/ElemTable`, `Global/DocumentIncrementTable`, `Global/PartitionTable`) store
element identifiers, episode numbers, and partition identifiers, never byte offsets or compressed stream lengths. Changing the
byte length of a record inside `Partitions/<n>` therefore requires no cross-stream offset table edits.

When rewriting data, `RvtWriter` enforces the following invariants:

1. **Untouched streams and storage metadata stay identical.** `SaveAs` decodes nothing; every stream (`BasicFileInfo`,
   `TransmissionData`, `ProjectInformation`, `PartAtom`, `RevitPreview4.0`, `Formats/*`, `Global/*`, `Partitions/*`, and any
   unrecognised streams) is copied verbatim, and root/child storage directory-entry metadata (`HeaderCLSID`, `CLSID`, `StateBits`,
   and creation/modified FILETIMEs) is preserved and verified on read-back.
2. **Paged stream prefixes and parity are preserved.** `Formats/Latest` starts directly with its gzip member; `Global/*` streams
   keep their exact 8-byte header prefix before the gzip member. Every rewritten paged stream is sealed with cyclic Hamming
   parity pages (`64,896` payload bytes + `353` parity bytes per full page) and read back through both the parity verifier and
   gzip decoder before being written.
3. **Untouched blocks, chains, and opaque payloads stay byte-for-byte identical.** Inside an edited `Partitions/<n>` stream:
   - Untouched content blocks (including every keyed block belonging to a loaded family document) are copied verbatim with their
     original deflate payloads and `SignatureMarker` intact.
   - Within an edited block, untouched segment chains (including untouched chains on the same channel) are copied verbatim.
   - Within an edited chain, untouched records (including records whose class tag has no known field layout or carries opaque
     trailing bytes) are copied verbatim in file order.
   - Edited records receive updated `[size][body][size]` framing and fresh Adler-32 checksums on channels `102` and `103`
     (channel `101` carries no record checksum).
   - Edited chains respect Revit's `131,072`-byte (`128 KB`) segment body bound, emitting standalone segments (`flags & 3 == 0`)
     for batches up to `131,072` bytes and multi-segment chains (`2 -> 3 -> 1`) for larger record bodies while preserving the
     source chain's upper flag bits and opaque dword.
   - Edited blocks write an empty `SignatureMarker` (`n = 0, m = 0`) because their bytes are no longer signed by Revit.
4. **Post-encode verification.** Every rewritten partition stream is verified in memory before container assembly: page parity
   must pass, full `PartitionFraming` must walk every block and segment without error, Adler-32 checksums must match on every
   record, every unedited record must match the input byte-for-byte, and every edited record must match its requested body.
5. **Atomic persistence and source immutability.** Output is written to a sibling `.tmp` file, re-inspected against the capability
   matrix when edits were applied, read back from disk to verify every storage's directory-entry metadata and every stream's
   declared length and SHA-256 hash, and moved to the destination with `overwrite: false`. Existing files (including the source
   file itself) are never overwritten, and any failure deletes the temporary file while leaving the source untouched.

## Refusal codes

`RvtWriteRefusedException.Kind` and `RvtOperationSupport.RefusalCode` report stable machine-readable strings:

| Code | Meaning |
| --- | --- |
| `unknown-file-kind` | Container has neither `PartAtom` nor `ProjectInformation` and its path extension is not `.rvt`/`.rte`/`.rfa`/`.rft`. |
| `cloud-model` | `BasicFileInfo` marks a single-user or workshared cloud model (`IsSingleUserCloudModel: True` or cloud central path). |
| `workshared-model` | `BasicFileInfo` reports worksharing enabled. |
| `missing-metadata` | `BasicFileInfo` is missing, unreadable, or lacks a recognised `Worksharing`, `IsSingleUserCloudModel`, or saving-release field, or, when relocating a file with `RvtLinkSymbol` records, `TransmissionData` is missing or lists no external reference with a saved path and path type for one of them. |
| `relative-reference-relocation` | Saving to a different directory would change the resolved target of a relative external file reference (including local image links), a reference has an unrecognised path type, or a point cloud or image link has an unverified external path. |
| `unreadable-transmission-data` | `TransmissionData` cannot be parsed cleanly when saving to a different directory. |
| `corrupt-page-parity` | A `Formats/*`, `Global/*`, or `Partitions/*` stream fails Hamming page parity verification. |
| `missing-schema` | `Formats/Latest` is missing, unreadable, or cannot be parsed. |
| `unrecognised-release` | `Formats/Latest` does not match a known Revit schema release. |
| `release-mismatch` | `BasicFileInfo` saving release differs from the `Formats/Latest` schema release (on source inspection or after replacing `Formats/Latest`). |
| `unsupported-release` | Effective release is outside `2020` to `2027`. |
| `unreadable-paged-stream` | A `Global/*` stream fails decompression or prefix verification. |
| `missing-framing-markers` | `Formats/Latest` lacks one or more partition framing marker classes. |
| `missing-partitions` | No numbered stream exists under `Partitions/`. |
| `partition-framing-failed` | A `Partitions/<n>` stream fails strict block, segment, gzip, or owner-census framing. |
| `partition-checksum-failed` | An input record on channel `102` or `103` has a mismatched Adler-32 checksum. |
| `invalid-stream-target` | A `RvtPagedStreamEdit` names a missing or non-paged stream. |
| `invalid-schema-payload` | A replacement `Formats/Latest` payload does not parse as a valid class schema. |
| `incompatible-schema-payload` | A replacement `Formats/Latest` payload changes element ID width or framing marker class identifiers required by existing partition streams. |
| `duplicate-edit` | The same stream or `(PartitionNumber, Block, Channel, OwnerId)` record is targeted twice in one request. |
| `missing-partition` | A `RvtPartitionRecordEdit` names a `Partitions/<n>` stream that does not exist in the source container. |
| `missing-record` | A `RvtPartitionRecordEdit` names a `(Block, Channel, OwnerId)` record that does not exist in the target partition. |
| `invalid-record-target` | A `RvtPartitionRecordEdit` uses an invalid partition number, block index, channel, `-1` terminator owner, 32-bit overflow owner, or body shorter than 2 bytes. |
| `read-only-parameter` | Revit exposes the computed parameter as read-only, or the stored project or shared definition marks the parameter read-only. |
| `unsupported-parameter` | Writing this built-in parameter, definition class, or data type has not been verified. |
| `unsupported-parameter-file` | The source is outside the verified parameter editing file scope, or the paths lack the extension required by the operation (`.rvt` for project edits and `.rfa` for family edits). |
| `unsupported-parameter-owner` | The element class is outside the verified scope for this parameter. |
| `invalid-parameter-value` | The value is invalid for the supported text representation, integer bounds, or numeric type. |
| `missing-element` | The element ID is absent from the source element table. |
| `missing-parameter-definition` | The positive parameter definition ID is absent from the source element table. |
| `missing-family-type` | The standalone family type name has no unique match. |
| `unverified-parameter-storage` | The writer cannot confirm the element's record, stored value, editability conditions, or mirrored project metadata. |
