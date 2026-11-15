# Private test fixtures

Some tests need input that cannot be published: Revit project files, class blocks captured from project files, and
dumps made from them. Those tests find their input through one helper, `TestFiles` in the test project, and are reported
as **skipped with a reason** when it is missing. They never pass silently and never fail for lack of a file.

## Where the files are looked for

1. The folder named by the environment variable `RVTIO_TEST_FILES`.
2. A folder `test_files` in the repository root (or in any parent of the test binary folder).

A file may lie directly in the folder or in one level of subfolders (`2020`, `2022`, `2025`, ...). The expected content:

| Path under the fixture folder | Used for |
|---|---|
| `2022/TESTFILE.rvt` | the main project file of the integration tests |
| `2020/Test_2020.rvt`, `2022/Test_2022.rvt`, `2025/Test_2025.rvt` | schema, element table and name tests of three Revit versions |
| `bim_dump.txt` | the dump of an independent BIM reader for `TESTFILE.rvt`, read by `ReferenceApiTests` |
| `formats_latest_decompressed.bin` | the decompressed `Formats/Latest` stream, read by `FormatsLatestAnalysisTests` |
| `TestData/*.bin` | class blocks captured from project files (38 files, below) |

`PageCodecSampleTests` reads every `.rvt` file in the folder and in one level of subfolders, whatever its name, and skips streams over
128 MB.

`ContainerRewriterSampleTests` copies every `.rvt` file in the folder and in one level of subfolders to a temporary file (deleted after
each file; it needs free disk space for the largest file) and compares the compact JSON of the copy with that of the source for the ten largest files of up to 8 MB.

`RvtWriterSampleTests` runs `RvtWriter.SaveAs` and targeted partition rewrites on every `.rvt` and `.rfa` file in the folder and in one
level of subfolders, checking stream hashes, element identities, references, geometry and framing checksums (or refusal on unsupported
containers and cross-directory relative references).

`SchemaStreamEditorSampleTests` reads the `Formats/Latest` stream of every `.rvt` file in the folder and in one level of subfolders,
whatever its name, and tests one stream per distinct schema, so one project per release is enough.

`DowngradeSampleTests` finds one project per release by the release of its class table, whatever the file name, and needs a project of
release 2026 and one of release 2025 (and one of release 2027 for the refusal test). Its numbers (4690 and 4600 classes, 4584 in
common, 106 only in 2026, 16 only in 2025, 67 higher versions, 69 classes with different fields, 10 changed identifier lists, 64 elements in
classes 2025 lacks) hold for any project of those releases, because the class tables of one release are identical.

`CoverageCensusSampleTests` runs the coverage census (`rvtio coverage`, see [the coverage report](coverage-report.md)) on the three
`Test_20xx.rvt` projects and prints the counts per block kind and channel to the test output. It asserts only what holds for every file
(every partition stream frames, the rows add up, every record of channel 101 is exact, the three channels of the project block hold
the same number of records) and pins no measured count, because the counts grow as the decoding does.

The attributes `[SkippableFact]` and `[SkippableTheory]` (`SkippableFact.cs`) report a test that throws `SkipException` as
skipped; xunit 2.9.3 has no runtime skip of its own. `TestFiles.Find`, `FindRelative` and `OpenBlock` throw it, so a test
needs no code of its own to skip. Tests that write diagnostic dumps write them to `rvtio-tests` in the system temporary
folder (`TestFiles.Scratch`); no test uses a fixed absolute path.

## What runs without the private files

With no private file at all, 10529 of the 10976 test cases run and pass on net8.0 and 447 are skipped (net10.0: 10533 of
10980, 447 skipped): 441 need private files (listed below; 17 of them only `bim_dump.txt` or `formats_latest_decompressed.bin`)
and 6 need other inputs (two need symbolic links, three
need `RVTIO_FREEFORM_FULL`, `RVTIO_CYL_FULL` or `RVTIO_MESH_FULL`, one is a known gap). The tests that run use synthetic data built
in the tests and the small fixtures committed under `tests/RvtIO.Tests/Fixtures`. With the full private fixture folder the
remaining cases run as well.

Run with the private files: `RVTIO_TEST_FILES=<folder> dotnet test` (PowerShell: `$env:RVTIO_TEST_FILES = '<folder>'`).

## Tests that need the private files

298 test methods (441 cases, theory rows counted) are skipped without the files:

| Class | Methods | Cases | Files |
|---|---|---|---|
| `ApiTests` | 49 | 52 | TESTFILE.rvt |
| `ArcProfileTests` | 1 | 1 | TestData blocks |
| `BinarySearchTests` | 4 | 4 | TESTFILE.rvt |
| `BlockDiagnosticTests` | 5 | 5 | TESTFILE.rvt |
| `CompactFormatSettingsTests` | 8 | 23 | TESTFILE.rvt |
| `CompactIdentityTests` | 1 | 1 | Test_2022.rvt, TESTFILE.rvt |
| `CompactIfcParametersTests` | 1 | 1 | Test_2022.rvt |
| `CompactOwnValueRulesTests` | 4 | 5 | Test_2022.rvt, TESTFILE.rvt |
| `CompactPropertySourcesTests` | 4 | 4 | Test_2022.rvt, TESTFILE.rvt |
| `CompactSolidValueTests` | 5 | 6 | Test_2022.rvt, TESTFILE.rvt |
| `CompactUserParametersTests` | 2 | 2 | Test_2022.rvt |
| `CompoundLayerFunctionTests` | 1 | 1 | rac_basic_sample_project.rvt |
| `ContainerRewriterSampleTests` | 3 | 3 | any `.rvt` project files |
| `CoverageCensusSampleTests` | 1 | 3 | Test_2020.rvt, Test_2022.rvt, Test_2025.rvt |
| `CurrentCopiesDefaultTests` | 2 | 2 | TESTFILE.rvt, test_angled_wall.rvt |
| `CurtainPanelOrderTests` | 3 | 3 | TestData blocks |
| `CurtainRoofReferenceSourceTests` | 4 | 16 | TestData blocks |
| `DocumentExportIdTests` | 1 | 1 | TESTFILE.rvt |
| `DowngradeSampleTests` | 7 | 8 | any `.rvt` project files |
| `EdgeBoundsTests` | 3 | 15 | TestData blocks |
| `ElemTableAuthorityTests` | 2 | 2 | TESTFILE.rvt |
| `ElementExportIdTests` | 3 | 6 | TESTFILE.rvt |
| `ElementHeaderBoundingBoxTests` | 2 | 13 | one `.rvt` project per release |
| `ElementHeaderCategoryTest2022Tests` | 2 | 2 | TESTFILE.rvt, Test_2022.rvt |
| `ElementHeaderCategoryTestFileTests` | 1 | 1 | TESTFILE.rvt |
| `ElementMeshesSampleTests` | 5 | 5 | Test_2022.rvt, Test_2025.rvt, test_door_in_wall.rvt |
| `EpisodeGuidTests` | 2 | 2 | TESTFILE.rvt |
| `ExportReportTests` | 8 | 13 | Test_2020.rvt, TESTFILE.rvt |
| `FaceAreaTests` | 2 | 2 | TestData blocks |
| `FormatsLatestAnalysisTests` | 1 | 1 | TESTFILE.rvt, formats_latest_decompressed.bin |
| `FormatsSchemaTests` | 3 | 5 | Test_2020.rvt, Test_2022.rvt, Test_2025.rvt |
| `GlobalStorageDecodeTests` | 2 | 4 | TESTFILE.rvt |
| `IfcCurrentFamilyHostTests` | 1 | 3 | TestData blocks |
| `IfcCurrentRoofTypeReferenceTests` | 1 | 2 | TestData blocks |
| `IfcCurrentSourcePlaneSlopeTests` | 1 | 4 | TestData blocks |
| `IfcDeterminismTests` | 1 | 1 | TESTFILE.rvt |
| `IfcExporterPlainWallBodyCurrentTests` | 4 | 4 | TestData blocks |
| `IfcExporterSkipReasonTests` | 1 | 1 | TESTFILE.rvt |
| `IfcExporterSlabQuantitiesSampleTests` | 3 | 3 | rstbasicsampleproject.rvt, struct25.rvt |
| `MultistoryStairCopyTests` | 1 | 1 | rac_advanced_sample_project.rvt |
| `OwnElementClassTests` | 1 | 1 | TESTFILE.rvt |
| `PageCodecSampleTests` | 2 | 2 | any `.rvt` project files |
| `PartitionFramingFuzzTests` | 1 | 1 | TESTFILE.rvt |
| `ProjectLengthUnitTests` | 1 | 1 | One Way Sign in Route Analysis.rfa |
| `R22TranscoderTests` | 2 | 2 | Test_2025.rvt, TESTFILE.rvt |
| `RailingQuantitySourceTests` | 4 | 18 | TestData blocks |
| `RecordRoutingTests` | 1 | 1 | TESTFILE.rvt |
| `ReferenceApiTests` | 18 | 18 | TESTFILE.rvt, bim_dump.txt |
| `RvtSceneIdentitySampleTests` | 1 | 1 | TESTFILE.rvt |
| `RvtWriterSampleTests` | 2 | 2 | any `.rvt` or `.rfa` files |
| `SchemaModelTests` | 1 | 1 | Test_2022.rvt |
| `SchemaReleaseTests` | 2 | 7 | Test_2020.rvt, Test_2022.rvt, Test_2025.rvt, TESTFILE.rvt |
| `SchemaStreamEditorSampleTests` | 2 | 2 | any `.rvt` project files |
| `SpaceBoundaryLiteralTests` | 5 | 5 | TestData blocks |
| `StairComponentSampleTests` | 6 | 16 | Test_2020.rvt, Test_2022.rvt, Test_2025.rvt, rac_basic_sample_project.rvt |
| `SystemFamilyNameTests` | 1 | 1 | Test_2020.rvt |
| `TfSchemaToolsTests` | 3 | 6 | Test_2020.rvt, Test_2022.rvt, Test_2025.rvt |
| `UserParameterCatalogTests` | 1 | 1 | Test_2022.rvt, TESTFILE.rvt |
| `ValidatedElementNamesTest2022Tests` | 2 | 2 | Test_2022.rvt, TESTFILE.rvt |
| `ValidatedTests` | 72 | 88 | TESTFILE.rvt |
| `VkConnectorBlockTests` | 6 | 6 | TestData blocks |
| `WallWithoutSolidAndTypeElementTests` | 1 | 1 | Test_2025.rvt |
| `WideBlockWalkerTests` | 3 | 14 | Test_2025.rvt, TESTFILE.rvt |
| `WideElementRecordsTests` | 2 | 2 | Test_2025.rvt, TESTFILE.rvt |

`ReferenceApiTests` (16 of its 18 methods need `bim_dump.txt`) and `FormatsLatestAnalysisTests` (needs
`formats_latest_decompressed.bin`) account for the 17 cases that stay skipped with a fixture folder that lacks those two files.

## Class blocks (`TestData`)

The 38 captured blocks were removed from the repository: they are pieces of third-party project files that cannot be published. The 13 test classes that read
them (38 methods, 90 cases depend on a block) were not given synthetic replacements. Their assertions are exact values decoded from the real records (areas,
slopes, names, counts of faces and connectors), and the code under test walks the record layout of a real Revit class,
so a synthetic byte sequence would need a writer for those layouts and different expected values: it would not be the
same test. The tests therefore read the blocks from `TestData` of the fixture folder. In the block names, `sample-a` stands for one sample architectural project, followed by the element kind, the element id and the part of the record that was captured:

| Class | Blocks |
|---|---|
| `CurtainPanelOrderTests` | `sample_a_curtain_wall_9169323.bin` |
| `CurtainRoofReferenceSourceTests` | `sample-a-curtain-roof-2158349`, `-2698440`, `-4066652`, `-4201316`, `sample-a-curtain-roof-type-16025` |
| `IfcCurrentFamilyHostTests` | `sample-a-family-314844`, `-363949`, `-363968` |
| `IfcCurrentRoofTypeReferenceTests` | `sample-a-roof-type-9792925` |
| `IfcCurrentSourcePlaneSlopeTests` | `sample-a-support-6957681-grep`, `-6957682-grep`, `-6957698-grep`, `-6957699-grep` |
| `IfcExporterPlainWallBodyCurrentTests` | `sample-a-wall-2520908-hole`, `sample-a-wall-638404-cut`, `sample-a-wall-9751957-round` |
| `ArcProfileTests` | `sample-a-wall-9765668-grep` |
| `EdgeBoundsTests` | `sample-a-wall-8954843-grep`, `-8959579-grep`, `-9750515-grep`, `-9750549-grep`, `-9765007-grep`, `-9765665-grep`, `-9765668-grep`, `-9766112-grep` |
| `FaceAreaTests` | `sample-a-room-8964053-curved`, `sample-a-wall-8961372-arc` |
| `RailingQuantitySourceTests` | `sample-a-railing-7583306-root`, `-7583306-symbol`, `sample-a-railing-2653815-symbol` |
| `SpaceBoundaryLiteralTests` | `sample-a-room-2766296-fourfaces`, `sample-a-room-5992163-sliver`, `sample-a-room-8964053-curved` |
| `VkConnectorBlockTests` | `vk_flex_pipe_9123013`, `vk_proxy_directional_5919313`, `vk_proxy_mixed_domain_2823056` |

(The blocks listed without extension are `.bin` files.) The remaining tests of these classes, which build their input in the
test, still run everywhere.

## Inline captures

Some tests carry small byte captures inline, as base64 or hex strings in the source (for example `BaseRailingSerializationTests`,
`IfcExporterCoveringCommonPropertySetTests`, `WallFaceLoopTests`, `PlanarSpace*Tests`, `StairElementSerializationTests`). They run everywhere.
Every capture was decoded, including the compressed and nested ones, and checked for personal data. Texts that identify a person, an
organisation or a local path were replaced by neutral text of the same length, so that the record layouts and all assertions stay the same.
Expected values that come from private project files and could identify their source are compared by SHA-256 instead of by text.
