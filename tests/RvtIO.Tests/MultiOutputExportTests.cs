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
}
