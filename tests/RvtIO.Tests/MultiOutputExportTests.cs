using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace RvtIO.Tests;

public class MultiOutputExportTests
{
    private static (int ExitCode, string StdErr, string StdOut) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(CliExitCodeTests.DotnetExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RvtIO.Cli.dll"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "The tool did not end within two minutes.");
        Task.WaitAll(output, error);
        return (process.ExitCode, error.Result, output.Result);
    }

    [Fact]
    public void MultiOutput_IfcLengthUnitChoiceAppliesAlongsideJsonAndScene()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-units-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            string ifc = Path.Combine(directory, "model.ifc");
            string compact = Path.Combine(directory, "compact.json");
            string full = Path.Combine(directory, "full.json");
            string scene = Path.Combine(directory, "scene.json");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            var (exit, error, _) = Run(rvt, "--ifc", ifc, "--compact-json", compact,
                "--full-json", full, "--scene", scene, "--ifc-length-unit", "ft");
            Assert.True(exit == 0, error);
            Assert.Contains(".LENGTHUNIT.,'FOOT'", File.ReadAllText(ifc));
            using var compactDocument = JsonDocument.Parse(File.ReadAllText(compact));
            using var fullDocument = JsonDocument.Parse(File.ReadAllText(full));
            Assert.NotNull(IOScene.SceneJson.Read(File.ReadAllText(scene)));

            string rejected = Path.Combine(directory, "rejected.json");
            var (invalidExit, invalidError, _) = Run(rvt, "--full-json", rejected, "--ifc-length-unit", "ft");
            Assert.Equal(1, invalidExit);
            Assert.Contains("--ifc-length-unit requires IFC output", invalidError);
            Assert.False(File.Exists(rejected));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultiOutput_AllOutputs_GeneratesValidFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            string ifc = Path.Combine(directory, "model.ifc");
            string compactJson = Path.Combine(directory, "model.compact.json");
            string scene = Path.Combine(directory, "model.scene.json");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            var (exit, err, outText) = Run(rvt, "--ifc", ifc, "--compact-json", compactJson, "--scene", scene, "--ifc-timestamp", "2026-01-01T00:00:00Z");
            Assert.True(exit == 0, $"Expected exit code 0 but got {exit}. Stderr: {err}");

            Assert.True(File.Exists(ifc), "IFC output file was not created.");
            string ifcContent = File.ReadAllText(ifc);
            Assert.StartsWith("ISO-10303-21;", ifcContent.TrimStart());

            Assert.True(File.Exists(compactJson), "Compact JSON output file was not created.");
            using var doc = JsonDocument.Parse(File.ReadAllText(compactJson));
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

            Assert.True(File.Exists(scene), "IOScene output file was not created.");
            var sceneDoc = IOScene.SceneJson.Read(File.ReadAllText(scene));
            Assert.NotNull(sceneDoc);
            Assert.NotNull(sceneDoc.Models);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultiOutput_ByteIdentical_ToSingleOutputRuns()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            string singleIfc = Path.Combine(directory, "single.ifc");
            string singleCompact = Path.Combine(directory, "single.compact.json");
            string multiIfc = Path.Combine(directory, "multi.ifc");
            string multiCompact = Path.Combine(directory, "multi.compact.json");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            const string fixedTimestamp = "2026-01-01T00:00:00Z";

            var (exitSingleIfc, errSingleIfc, _) = Run(rvt, singleIfc, "--ifc-timestamp", fixedTimestamp);
            Assert.Equal(0, exitSingleIfc);

            var (exitSingleCompact, errSingleCompact, _) = Run(rvt, singleCompact, "--compact-json");
            Assert.Equal(0, exitSingleCompact);

            var (exitMulti, errMulti, _) = Run(rvt, "--ifc", multiIfc, "--compact-json", multiCompact, "--ifc-timestamp", fixedTimestamp);
            Assert.Equal(0, exitMulti);

            byte[] singleIfcBytes = File.ReadAllBytes(singleIfc);
            byte[] multiIfcBytes = File.ReadAllBytes(multiIfc);
            Assert.Equal(singleIfcBytes, multiIfcBytes);

            byte[] singleCompactBytes = File.ReadAllBytes(singleCompact);
            byte[] multiCompactBytes = File.ReadAllBytes(multiCompact);
            Assert.Equal(singleCompactBytes, multiCompactBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultiOutput_InputCollision_ExitsWithCode1()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-incoll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            string safeIfc = Path.Combine(directory, "safe.ifc");
            CliExitCodeTests.WriteSyntheticRvt(rvt);
            byte[] initialRvtBytes = File.ReadAllBytes(rvt);

            var (exit, err, _) = Run(rvt, "--ifc", safeIfc, "--full-json", rvt);
            Assert.Equal(1, exit);
            Assert.Contains("must not name the input file", err);
            Assert.False(File.Exists(safeIfc));
            Assert.Equal(initialRvtBytes, File.ReadAllBytes(rvt));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultiOutput_MutualDestinationCollision_ExitsWithCode1()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-destcoll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            string sameJson = Path.Combine(directory, "same.json");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            var (exit, err, _) = Run(rvt, "--compact-json", sameJson, "--full-json", sameJson);
            Assert.Equal(1, exit);
            Assert.Contains("must not name the same file", err);
            Assert.False(File.Exists(sameJson));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("--ifc")]
    [InlineData("--scene")]
    [InlineData("--full-json")]
    [InlineData("--json")]
    public void MultiOutput_MissingPathOption_ExitsWithCode1(string option)
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-opt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            // Trailing option without value
            var (exit1, err1, _) = Run(rvt, option);
            Assert.Equal(1, exit1);
            Assert.Contains($"{option} requires a path", err1);

            // Followed by another option instead of a valid file path
            var (exit2, err2, _) = Run(rvt, option, "--scene", "scene.json");
            Assert.Equal(1, exit2);
            Assert.Contains($"{option} requires a path", err2);

            // Empty string value
            var (exit3, err3, _) = Run(rvt, option, "");
            Assert.Equal(1, exit3);
            Assert.Contains($"{option} requires a path", err3);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CopiedElementCanReplaceDrawingAndStairFactsIndependently()
    {
        var original = new RvtIO.Model.Element
        {
            Text = "Original",
            ViewOrigin = [1, 2, 3],
            SerializedStairTypeId = 42,
            SerializedStairTypeLinkIsResolved = true,
            SerializedStairCopyLevelIds = [10, 20],
        };
        var copy = original.ShallowCopy();

        Assert.Same(original.ViewOrigin, copy.ViewOrigin);
        Assert.Same(original.SerializedStairCopyLevelIds, copy.SerializedStairCopyLevelIds);
        copy.Text = null;
        copy.ViewOrigin = null;
        copy.SerializedStairTypeId = 17;
        copy.SerializedStairTypeLinkIsResolved = false;

        Assert.Equal("Original", original.Text);
        Assert.Equal([1d, 2d, 3d], original.ViewOrigin);
        Assert.Equal(42, original.SerializedStairTypeId);
        Assert.True(original.SerializedStairTypeLinkIsResolved);
        Assert.Null(copy.Text);
        Assert.Null(copy.ViewOrigin);
        Assert.Equal(17, copy.SerializedStairTypeId);
        Assert.False(copy.SerializedStairTypeLinkIsResolved);
    }

    [Fact]
    public void MultiOutput_RvtDocumentFromInfo_DoesNotMutateSourceInfo()
    {
        var info = new RvtIO.Model.RevitFileInfo
        {
            Elements = new List<RvtIO.Model.Element>
            {
                new()
                {
                    Id = 101,
                    Name = "Instance Wall",
                    FamilyName = "Basic Wall",
                    CategoryId = -2000011,
                    References = new List<int> { 202 },
                    Parameters = new List<RvtIO.Model.Parameter>
                    {
                        new()
                        {
                            Id = (int)RvtIO.BuiltIns.BuiltInParameter.SYMBOL_ID_PARAM,
                            Definition = new RvtIO.Model.Definition { Name = "SYMBOL_ID_PARAM" },
                            StorageType = RvtIO.Model.StorageType.ElementId,
                            Value = null
                        }
                    }
                },
                new()
                {
                    Id = 202,
                    Name = "Generic - 200mm",
                    FamilyName = "Basic Wall Type",
                    CategoryId = -2000011,
                    Parameters = new List<RvtIO.Model.Parameter>()
                }
            }
        };

        int originalElemCount = info.Elements.Count;
        int originalParamCount = info.Elements[0].Parameters.Count;
        string originalName = info.Elements[0].Name;
        object? originalSymbolValue = info.Elements[0].Parameters[0].Value;

        var doc = RvtDocument.FromInfo(info);
        Assert.NotNull(doc);

        // Document element has synthesized params and resolved names
        var docElem = doc.GetElement(101)!;
        Assert.NotNull(docElem);
        Assert.Equal("Generic - 200mm", docElem.Name);
        Assert.True(docElem.Parameters.Count > originalParamCount);

        // Source RevitFileInfo MUST be completely unchanged
        Assert.Equal(originalElemCount, info.Elements.Count);
        Assert.Equal(originalParamCount, info.Elements[0].Parameters.Count);
        Assert.Equal(originalName, info.Elements[0].Name);
        Assert.Equal(originalSymbolValue, info.Elements[0].Parameters[0].Value);
    }

    [Fact]
    public async Task MultiOutput_IfcWithBareCompactJson_ExportsBothFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-barejson-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            string ifc = Path.Combine(directory, "result.ifc");
            string defaultOutputJson = Path.Combine(directory, "output.json");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            // Run in working directory where output.json would land
            var start = new ProcessStartInfo(CliExitCodeTests.DotnetExecutable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RvtIO.Cli.dll"));
            start.ArgumentList.Add("model.rvt");
            start.ArgumentList.Add("--ifc");
            start.ArgumentList.Add(ifc);
            start.ArgumentList.Add("--compact-json");
            start.ArgumentList.Add("--ifc-timestamp");
            start.ArgumentList.Add("2026-01-01T00:00:00Z");

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000));
            await Task.WhenAll(output, error);

            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(ifc), "IFC was not created.");
            Assert.True(File.Exists(defaultOutputJson), "Default output.json was not created.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultiOutput_SceneIsolatedFromElementMutations_ByteIdentical()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-sceneiso-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            string singleScene = Path.Combine(directory, "single.scene.json");
            string multiScene = Path.Combine(directory, "multi.scene.json");
            string compact = Path.Combine(directory, "compact.json");
            string full = Path.Combine(directory, "full.json");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            var (exit1, _, _) = Run(rvt, "--scene", singleScene);
            Assert.Equal(0, exit1);

            var (exit2, _, _) = Run(rvt, "--scene", multiScene, "--compact-json", compact, "--full-json", full);
            Assert.Equal(0, exit2);

            byte[] singleBytes = File.ReadAllBytes(singleScene);
            byte[] multiBytes = File.ReadAllBytes(multiScene);
            Assert.Equal(singleBytes, multiBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultiOutput_ReportCollision_ExitsWithCode1()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            string ifc = Path.Combine(directory, "model.ifc");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            var (exit, err, _) = Run(rvt, "--ifc", ifc, "--report", ifc);
            Assert.Equal(1, exit);
            Assert.Contains("--report must not name the input file or an output of the run", err);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultiOutput_IfcPhaseWithoutIfc_ExitsWithCode1()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-phase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            string scene = Path.Combine(directory, "model.scene.json");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            var (exit, err, _) = Run(rvt, "--scene", scene, "--ifc-phase", "last");
            Assert.Equal(1, exit);
            Assert.Contains("--ifc-phase requires IFC output", err);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultiOutput_CompactSolidQuantitiesFrom_MatchesRead()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-quantities-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            var options = new RvtReaderOptions { R22Layout = true };
            var reader = new RvtFileReader(options);
            var info = reader.Read(rvt);
            var state = reader.LastParsingState!;

            var fromQuantities = RvtIO.Compact.CompactSolidQuantities.From(info, state, null);
            var readQuantities = RvtIO.Compact.CompactSolidQuantities.Read(rvt, options, null);

            Assert.Equal(readQuantities.AllCurveLengths.OrderBy(kv => kv.Key), fromQuantities.AllCurveLengths.OrderBy(kv => kv.Key));
            Assert.Equal(readQuantities.AllSolids.OrderBy(kv => kv.Key), fromQuantities.AllSolids.OrderBy(kv => kv.Key));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultiOutput_PositionalOutput_BackwardCompatible()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            string ifc = Path.Combine(directory, "model.ifc");
            string json = Path.Combine(directory, "model.json");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            var (exitIfc, errIfc, _) = Run(rvt, ifc, "--ifc-timestamp", "2026-01-01T00:00:00Z");
            Assert.Equal(0, exitIfc);
            Assert.True(File.Exists(ifc));

            var (exitJson, errJson, _) = Run(rvt, json, "--compact-json");
            Assert.Equal(0, exitJson);
            Assert.True(File.Exists(json));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultiOutput_CompactAndFullJson_RunsInSingleRead()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-multi-c-f-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "model.rvt");
            string compact = Path.Combine(directory, "compact.json");
            string full = Path.Combine(directory, "full.json");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            var (exit, err, _) = Run(rvt, "--compact-json", compact, "--full-json", full);
            Assert.Equal(0, exit);
            Assert.True(File.Exists(compact));
            Assert.True(File.Exists(full));
            // Only a single Timing: read should be printed, never two reads
            int readTimingCount = err.Split(new[] { "Timing: read" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(1, readTimingCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
