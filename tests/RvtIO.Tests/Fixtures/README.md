# Unfilled wall opening regression (#6)

`issue6-unfilled-opening.json` contains the 36 STEP records needed to reproduce
one incorrectly measured opening from Autodesk's public Revit 2022 architectural
basic sample. The source URL, file SHA-256, host id, cutter id and opening GlobalId
are recorded in the fixture. No Revit model or vendor binary is included.

This is the secondary cut of door 906937 in adjacent wall 422243, with no
`IfcRelFillsElement` for this opening. It must be measured as its own final solid,
without borrowing the nominal dimensions of the door or its type. In the wall
frame its extent along the wall is 0.202 m, its vertical extent is 2.134 m and its
extent across the wall is 1.73 m. The original quantities are Height 0.202 m,
Width 2.134 m and Depth 1.73 m: Width and Height name the wrong sides.

The capture retains the opening profile, extrusion direction, solid placement,
opening placement, host placement and host Axis representation. Lengths were
converted from millimetres to metres (the quantity pass's working unit), names
and ownership were removed, the unrelated host Body was removed, the site
transform was excluded and entity ids were renumbered. The JSON records are
quantity-pass input, not a complete IFC exchange file.

Run the regression without downloading the model:

```sh
dotnet test tests/RvtIO.Tests/RvtIO.Tests.csproj --filter FullyQualifiedName~IfcUnfilledOpeningRegressionTests
```

It checks the same capture under four rotations and three output unit families,
and verifies that measuring it does not rewrite the existing geometry or
relationships. The fixture is intentionally anonymous: dimensions come from the
solid and the host frame, not element names or a type-name size.

This covers unfilled extruded openings in straight walls. Curved hosts, curtain
hosts and the separately cached quantities of certified cut-pair bodies are not
covered by this fixture.

# Schema release selection regression (#31)

`SchemaReleaseTests.SyntheticFilesKeepTheDeclaredTableAndRoutingWidths` builds
small compound files with paged schema and element-table streams. It covers
both directions of a saving/schema year mismatch across the 32/64-bit ID
boundary, matching years, an unknown schema release and a missing saving year.
It requires no downloaded files.

`SchemaReleaseExportTests` additionally reads public Autodesk samples and copies
whose labelled `BasicFileInfo` `Format` year is changed. Only that saving-year
field is normalized when comparing the full model, IFC and compact JSON with
properties; all remaining content must match. The 2024 sample is tested both
with the native wide-schema reader and with `R22Layout` geometry decoding.
The tests are skipped unless the following variables name downloaded files:

| Variable | Public sample | SHA-256 |
| --- | --- | --- |
| `RVTIO_SCHEMA_SAMPLE_2022` | [Revit 2022 structural basic sample](https://revit.downloads.autodesk.com/download/2022RVT_RTM/Docs/InProd/rstbasicsampleproject.rvt) | `054a6e5803098dece0f7aa8d5248a74915696daed1a56492b95ce54d0f1de1de` |
| `RVTIO_SCHEMA_SAMPLE_2024` | [Snowdon Towers Sample Site](https://revit.downloads.autodesk.com/download/2024RVT_RTM/Docs/InProd/Samples/Snowdon%20Towers%20Sample%20Site.rvt) | `6bf4e490959c687e9ee851662c2f0f202ef486b00bf95d61c05f3fee34a2ebe5` |

The repository contains no Revit files. To run these checks in PowerShell after
downloading the samples outside the checkout:

```powershell
$env:RVTIO_SCHEMA_SAMPLE_2022 = 'C:\Samples\rstbasicsampleproject.rvt'
$env:RVTIO_SCHEMA_SAMPLE_2024 = 'C:\Samples\Snowdon Towers Sample Site.rvt'
dotnet test tests/RvtIO.Tests/RvtIO.Tests.csproj --filter 'FullyQualifiedName~SchemaRelease'
```
