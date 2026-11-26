using System.Diagnostics;
using System.Text.Json;
using OpenMcdf;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The exit code and the report of the command line tool itself for an input it cannot read (docs/export-report.md).
/// The tool is started as a process of its own, from the copy the test project builds next to the tests.</summary>
public class CliExitCodeTests
{
    internal static string DotnetExecutable =>
        Environment.ProcessPath is { } processPath
        && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : "dotnet";

    private static int Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(DotnetExecutable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RvtIO.Cli.dll"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        // both pipes are drained, or a full one would block the tool
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "The tool did not end within two minutes.");
        Task.WaitAll(output, error);
        return process.ExitCode;
    }

    private static void RunOn(byte[] input, params string[] kinds)
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "input.rvt"), ifc = Path.Combine(directory, "output.ifc"), report = Path.Combine(directory, "report.json");
            File.WriteAllBytes(rvt, input);

            Assert.Equal(RvtExitCodes.InputError, Run(rvt, ifc, "--report", report));

            Assert.False(File.Exists(ifc));
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(RvtExitCodes.InputError, json.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains(json.RootElement.GetProperty("error").GetProperty("kind").GetString(), kinds);
            // without a report the exit code is the same
            Assert.Equal(RvtExitCodes.InputError, Run(rvt, ifc));
            Assert.False(File.Exists(ifc));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>A compound file with one stream of the given size, as bytes.</summary>
    private static byte[] CompoundFile(int streamBytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "rvtio-cli-" + Guid.NewGuid().ToString("N") + ".cfb");
        try
        {
            using (var root = RootStorage.Create(path, OpenMcdf.Version.V3))
            using (var stream = root.CreateStream("Contents"))
                stream.Write(new byte[streamBytes]);
            return File.ReadAllBytes(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static (int ExitCode, string StdErr) RunWithOutput(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RvtIO.Cli.dll"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "The tool did not end within two minutes.");
        Task.WaitAll(output, error);
        return (process.ExitCode, error.Result);
    }

    internal static void WriteSyntheticRvt(string path, byte[]? extraPartition = null, bool standaloneFamily = false)
    {
        using var elemTable = new MemoryStream();
        using (var writer = new BinaryWriter(elemTable))
        {
            writer.Write((ushort)1312);
            writer.Write(1);
            writer.Write(12345); writer.Write(100); writer.Write(64);
            writer.Write(523); writer.Write(999); writer.Write(7); writer.Write(42);
        }
        byte[] intactPartition = CoverageCensusTests.Partition(
            CoverageCensusTests.Block(false, CoverageCensusTests.R(12345, CoverageCensusTests.Body("Plain", 5, 0, 0, 0))));
        var streams = new List<(string Path, byte[] Bytes)>
        {
            ("Global/ElemTable", RvtIO.Writing.PageCodec.EncodePadded(RvtIO.Writing.GzipMember.Write(elemTable.ToArray()))),
            ("Global/History", RvtIO.Writing.PageCodec.EncodePadded(RvtIO.Writing.GzipMember.Write(new byte[64]))),
            ("Formats/Latest", RvtIO.Writing.PageCodec.EncodePadded(RvtIO.Writing.GzipMember.Write(CoverageCensusTests.TableBytes))),
            ("Partitions/1", intactPartition),
        };
        if (extraPartition != null)
            streams.Add(("Partitions/2", extraPartition));
        if (standaloneFamily)
            streams.Add(("PartAtom", "<entry/>"u8.ToArray()));
        CoverageCensusTests.WriteFile(path, streams.ToArray());
    }

    [Fact]
    public void AnIntactFileWithStrictOption_ExitsZeroAndWritesOutputAndCompleteReport()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "intact.rvt");
            string outJson = Path.Combine(directory, "output.json");
            string report = Path.Combine(directory, "report.json");
            WriteSyntheticRvt(rvt);

            var (exit, _) = RunWithOutput(rvt, outJson, "--strict", "--report", report);
            Assert.Equal(RvtExitCodes.Success, exit);
            Assert.True(File.Exists(outJson));
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            Assert.Equal("complete", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(RvtExitCodes.Success, json.RootElement.GetProperty("exitCode").GetInt32());
            Assert.True(json.RootElement.GetProperty("limits").GetProperty("strict").GetBoolean());
            Assert.Equal(0, json.RootElement.GetProperty("issues").GetArrayLength());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(2147483648L)]
    [InlineData(4294967305L)]
    public void AnElementIdOutsideInt32_StopsEveryExportEvenWithExplicitReaderOptions(long id)
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "input.rvt");
            string ifc = Path.Combine(directory, "output.ifc");
            string scene = Path.Combine(directory, "scene.json");
            string compact = Path.Combine(directory, "compact.json");
            string full = Path.Combine(directory, "full.json");
            string report = Path.Combine(directory, "report.json");
            SchemaReleaseTests.WriteSyntheticFile(rvt, wide: true, marker: 891, saved: 2024, FormatsSchemaTests.Rows(true,
                (0, 0, 0, 0, -1), (int.MaxValue, int.MaxValue, 1, 0, 0), (id, id, 2, 0, 0)));

            var failure = Assert.Throws<RvtLimitException>(() => new RvtFileReader(new RvtReaderOptions { RaiseLimits = true }).Read(rvt));
            Assert.Equal(("Global/ElemTable", "element-id-range", (long)int.MaxValue, false),
                (failure.Hit.Stream, failure.Hit.Limit, failure.Hit.Value, failure.Hit.Raisable));
            Assert.Contains(FormattableString.Invariant($"Element ID {id}"), failure.Message);
            Assert.Equal(failure.Hit, Assert.Throws<RvtLimitException>(() => RvtIO.Scene.RvtSceneReader.Read(rvt)).Hit);

            var (exit, error) = RunWithOutput(rvt, "--ifc", ifc, "--scene", scene, "--compact-json", compact, "--full-json", full,
                "--allow-partial", "--raise-limits", "--report", report);
            Assert.Equal(RvtExitCodes.LimitExceeded, exit);
            Assert.Contains("element-id-range", error);
            Assert.Contains(failure.Message, error);
            Assert.All(new[] { ifc, scene, compact, full }, output => Assert.False(File.Exists(output)));
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
            var hit = Assert.Single(json.RootElement.GetProperty("limits").GetProperty("hit").EnumerateArray());
            Assert.Equal(failure.Hit.Limit, hit.GetProperty("limit").GetString());
            Assert.Equal(failure.Message, hit.GetProperty("detail").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AFileWithADamagedMember_ExitsZeroWithoutStrictAndPartialReadCodeWithStrict()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "damaged.rvt");
            byte[] badMember = new byte[512];
            new Random(11).NextBytes(badMember);
            badMember[0] = 0; badMember[1] = 1;
            WriteSyntheticRvt(rvt, extraPartition: badMember);

            string lenientOut = Path.Combine(directory, "lenient.json");
            string lenientReport = Path.Combine(directory, "lenient-report.json");
            Assert.Equal(RvtExitCodes.Success, Run(rvt, lenientOut, "--report", lenientReport));
            Assert.True(File.Exists(lenientOut));
            using (var json = JsonDocument.Parse(File.ReadAllText(lenientReport)))
            {
                var root = json.RootElement;
                Assert.Equal("partial", root.GetProperty("status").GetString());
                Assert.Equal(RvtExitCodes.Success, root.GetProperty("exitCode").GetInt32());
                Assert.False(root.GetProperty("limits").GetProperty("strict").GetBoolean());
                var p2 = root.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Partitions/2");
                Assert.True(p2.GetProperty("failed").GetInt32() > 0);
                var issue = Assert.Single(root.GetProperty("issues").EnumerateArray());
                Assert.Equal("Partitions/2", issue.GetProperty("stream").GetString());
                Assert.Equal("failed-members", issue.GetProperty("code").GetString());
            }

            string strictOut = Path.Combine(directory, "strict.json");
            string strictReport = Path.Combine(directory, "strict-report.json");
            var (strictExit, stderr) = RunWithOutput(rvt, strictOut, "--strict", "--report", strictReport);
            Assert.Equal(RvtExitCodes.PartialRead, strictExit);
            Assert.False(File.Exists(strictOut));
            Assert.Contains("Partitions/2", stderr);
            Assert.Contains("exit code 6", stderr);
            using (var json = JsonDocument.Parse(File.ReadAllText(strictReport)))
            {
                var root = json.RootElement;
                Assert.Equal("partial", root.GetProperty("status").GetString());
                Assert.Equal(RvtExitCodes.PartialRead, root.GetProperty("exitCode").GetInt32());
                Assert.True(root.GetProperty("limits").GetProperty("strict").GetBoolean());
                Assert.False(root.TryGetProperty("export", out _));
                var issue = Assert.Single(root.GetProperty("issues").EnumerateArray());
                Assert.Equal("Partitions/2", issue.GetProperty("stream").GetString());
                Assert.Equal("failed-members", issue.GetProperty("code").GetString());
            }

            // --allow-partial and --strict exclude each other
            Assert.Equal(RvtExitCodes.Usage, Run(rvt, strictOut, "--allow-partial", "--strict"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AnInputThatIsNoCompoundFile_EndsWithTheInputErrorCodeAndAFailedReport() =>
        RunOn("this is not a Revit file"u8.ToArray(), "not-a-compound-file");

    [Fact]
    public void AnEmptyInput_EndsWithTheInputErrorCode() => RunOn([], "empty-file");

    [Fact]
    public void ACompoundFileCutInHalf_EndsWithTheInputErrorCodeAndAFailedReport()
    {
        byte[] whole = CompoundFile(400_000);
        RunOn(whole[..(whole.Length / 2)], "truncated-or-damaged", "not-a-compound-file");
    }
}
