# Contributing to RvtIO

## Reporting bugs

Open a [GitHub issue](https://github.com/IOExtend/RvtIO/issues) with:

- The Revit version that saved the file.
- The rvtio version.
- The command line used, with confidential values removed.
- A minimal description of how to reproduce the problem.
- Expected and actual results, including relevant error messages.

Do not attach Revit files if they contain confidential data.
A minimal description is better than sharing a confidential model.

Report security issues through GitHub private vulnerability reporting in
the Security tab. Do not open public issues for security vulnerabilities.

## Proposing changes

Open an issue first for larger changes so the scope and approach can be
discussed before implementation. Keep changes focused on the agreed problem.

## Clean-room rule

All code is written independently from the analysis of files for
interoperability. Do not add vendor code, SDKs, SDK internals, headers or
binaries, decompiled or leaked source, or anything taken from the files of an
installed vendor application. Do not add Revit models or other Revit files,
customer data, or names, paths, sizes, counts, GUIDs or other details of
private sample files to the repository, including tests, comments and
documentation. Test inputs are built synthetically in the tests, or taken from
public samples whose source is recorded with the fixture; tests that need
private files read them from folders named by environment variables
(`RVTIO_TEST_FILES`) and are skipped when those folders are not set.

Do not put personal data in code, documentation, test data or commit metadata. Use your GitHub noreply address as the commit email.

## Development setup

Install the .NET SDK specified by `global.json`. It currently specifies
10.0.100, allows roll-forward to the latest feature band, and excludes
prerelease SDKs.

From the repository root, run:

```sh
dotnet build RvtIO.slnx -c Release
dotnet test
```

The library is in `src/RvtIO`, the CLI in `src/RvtIO.Cli`, and tests in
`tests/RvtIO.Tests`.

Keep file and geometry operations shared by the reader, writer and exporters
in the library's format or geometry code. For example, polygon and bounds
validation belong in `Geometry`, and path identity belongs in `Internal`.
Read the project's stored length unit in `Parsing`. Choosing the unit
declared in an IFC file belongs to the IFC exporter.
`RvtIO.Scene` builds on the public RvtIO API and references IOScene separately.

CI also compiles the native library and its scene adapter without the Compact
JSON, IFC and PDF output code or export reporting. Reader diagnostics and exceptions
belong in `Diagnostics`; the export report and CLI exit codes belong in `Export`.
Use a separate artifact path for the compile check. The check produces no NuGet package.

```sh
dotnet build src/RvtIO.Scene/RvtIO.Scene.csproj -c Release -p:BuildNativeCoreOnly=true --artifacts-path ./artifacts/native-core-check
```

Tests that need real Revit files are skipped with a message when private
fixtures are unavailable. Set `RVTIO_TEST_FILES` to the folder containing
your private fixtures to run them. The repository ships no Revit models.

## Code conventions

- Follow the surrounding code and repository formatting settings.
- Keep nullable reference types enabled.
- Warnings are treated as errors in `src`.
- Write short English comments that explain why.
- Document public APIs with XML documentation comments.
- Do not write console output from the library. Use the `DiagnosticOutput`
  sinks for diagnostics. The library must remain silent by default.

## Validating exports

Export outputs must not change unintentionally.

Before changing export behavior, export the same input files with the
existing code and keep the results outside the repository. Export those
files again after the change, using the same options. Compare the before
and after IFC and JSON outputs, investigate differences, and explain
intentional changes in the pull request.

## Pull requests

Include:

- A description of the problem and the change.
- Tests for behavior changes, including relevant errors and edge cases.
- The build and test commands run, their results, and any skipped checks.
- Before and after export comparison results when exports are affected.

Do not add Revit models, customer data, vendor code, or binaries to the
repository. Keep private test fixtures outside version control.

Write commit messages in the imperative mood, for example,
"Fix room export phase selection".

## Releases

Push a tag matching the package version, such as `v0.2.0`, to run `release.yml`.
The workflow builds and tests the solution, attaches packages, symbols, source and checksums to a GitHub release published at once (not a draft),
and publishes the NuGet packages through Trusted Publishing. The release notes are the section of `CHANGELOG.md` for the version, so add that section before the tag.
The NuGet policy must name this repository, `release.yml`, the configured NuGet profile and the `RvtIO*` package pattern.

## Licensing contributions

Contributions are licensed under the MIT license. By submitting a
contribution, you confirm that you have the right to contribute it under
that license.

## Questions

Ask questions in GitHub Discussions or repository issues.
