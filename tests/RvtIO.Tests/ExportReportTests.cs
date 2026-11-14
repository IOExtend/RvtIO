using System.Text.Json;
using OpenMcdf;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The export report, the exit codes and the reader limits (docs/export-report.md).</summary>
public class ExportReportTests
{
    [Fact]
    public void SkipLogGroupsByClassAndReasonLargestFirst()
    {
        var log = new SkipLog();
        for (int id = 100; id < 108; id++) log.Add(id, "OST_Walls", "type-element");
        log.Add(7, "OST_Doors", "hosted-element-without-host");
        log.Add(8, "OST_Doors", "hosted-element-without-host");
        log.Add(9, "OST_Abc", "type-element");
        log.Remove(8);

        Assert.Equal(10, log.Total);
        var groups = log.Groups();
        Assert.Equal(["OST_Walls", "OST_Abc", "OST_Doors"], groups.Select(g => g.Class));
        Assert.Equal([8, 1, 1], groups.Select(g => g.Count));
        Assert.Equal([100, 101, 102, 103, 104], groups[0].Examples);
        Assert.Equal([7], groups[2].Examples);
    }

    [Fact]
    public void SkipLogRemoveOfAnUnknownElementChangesNothing()
    {
        var log = new SkipLog();
        log.Add(1, "A", "r");
        log.Remove(2);
        Assert.Equal(1, log.Total);
    }

    [SkippableFact]
    public void SameFileComparesTheFullPathsAndTellsDifferentFilesApart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rvt-samefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string input = Path.Combine(folder, "a.rvt"), other = Path.Combine(folder, "b.json");
            File.WriteAllText(input, "x");
            string viaDots = Path.Combine(folder, "sub", "..", "a.rvt");

            Assert.True(ExportReport.SameFile(input, input));
            Assert.True(ExportReport.SameFile(input, viaDots));
            Assert.False(ExportReport.SameFile(input, other));
            if (OperatingSystem.IsWindows())
                Assert.True(ExportReport.SameFile(input, input.ToUpperInvariant()));

            string link = Path.Combine(folder, "link.rvt");
            try { File.CreateSymbolicLink(link, input); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new SkipException("symbolic links cannot be created here"); }
            Assert.True(ExportReport.SameFile(link, input));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rvt-samefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    // mklink /J needs no privilege; the junction is removed without touching its target.
    private static string Junction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) throw new SkipException("junctions exist on Windows only");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link)) throw new SkipException("junctions cannot be created here");
        return link;
    }

    private static string DirectorySymlink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new SkipException("symbolic links cannot be created here"); }
        return link;
    }

    private static void Cleanup(string folder)
    {
        // remove the links first: a recursive delete must not follow them into the real directory
        foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories("*", SearchOption.AllDirectories).Where(d => d.LinkTarget != null).ToList())
            dir.Delete();
        Directory.Delete(folder, true);
    }

    [SkippableFact]
    public void SameFileSeesThroughAJunctionToTheParentDirectory()
    {
        string folder = NewFolder();
        try
        {
            string real = Path.Combine(folder, "real");
            Directory.CreateDirectory(real);
            string input = Path.Combine(real, "model.rvt");
            File.WriteAllText(input, "x");
            string alias = Junction(Path.Combine(folder, "alias"), real);

            Assert.True(ExportReport.SameFile(Path.Combine(alias, "model.rvt"), input));
            Assert.True(ExportReport.SameFile(input, Path.Combine(alias, "model.rvt")));
            // a report that does not exist yet is resolved through its existing parent
            Assert.True(ExportReport.SameFile(Path.Combine(alias, "out.ifc"), Path.Combine(real, "out.ifc")));
            Assert.False(ExportReport.SameFile(Path.Combine(alias, "report.json"), input));
            // a junction to a junction
            string nested = Junction(Path.Combine(folder, "nested"), alias);
            Assert.True(ExportReport.SameFile(Path.Combine(nested, "model.rvt"), input));
        }
        finally { Cleanup(folder); }
    }

    [SkippableFact]
    public void SameFileSeesThroughADirectorySymbolicLinkToTheParentDirectory()
    {
        string folder = NewFolder();
        try
        {
            string real = Path.Combine(folder, "real");
            Directory.CreateDirectory(real);
            string input = Path.Combine(real, "model.rvt");
            File.WriteAllText(input, "x");
            string alias = DirectorySymlink(Path.Combine(folder, "alias"), real);

            Assert.True(ExportReport.SameFile(Path.Combine(alias, "model.rvt"), input));
            Assert.True(ExportReport.SameFile(Path.Combine(alias, "out.ifc"), Path.Combine(real, "out.ifc")));
            Assert.False(ExportReport.SameFile(Path.Combine(alias, "report.json"), input));
        }
        finally { Cleanup(folder); }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinuxLink(string existing, string link);

    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "link", SetLastError = true)]
    private static extern int DarwinLink(string existing, string link);

    private static void CreateHardLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(link))
                throw new SkipException("hard links cannot be created here");
            return;
        }

        int rc = OperatingSystem.IsMacOS() ? DarwinLink(target, link) : LinuxLink(target, link);
        if (rc != 0 || !File.Exists(link))
            throw new SkipException("hard links cannot be created here");
    }

    [SkippableFact]
    public void SameFileSeesAHardLinkAndRefusesReportPath()
    {
        string folder = NewFolder();
        try
        {
            string input = Path.Combine(folder, "model.rvt"), hard = Path.Combine(folder, "hard.rvt"), copy = Path.Combine(folder, "copy.rvt");
            File.WriteAllText(input, "x");
            File.Copy(input, copy);
            CreateHardLink(hard, input);

            Assert.True(ExportReport.SameFile(hard, input));
            Assert.True(ExportReport.SameFile(input, hard));
            Assert.False(ExportReport.SameFile(copy, input));

            var coverageError = new StringWriter();
            Assert.Equal(RvtExitCodes.Usage, RvtIO.Cli.CoverageCommand.Run([input, "--report", hard], TextWriter.Null, coverageError));
            Assert.Contains("--report must not name the input file.", coverageError.ToString());

            string dest = Path.Combine(folder, "saved.rvt");
            var saveError = new StringWriter();
            Assert.Equal(RvtExitCodes.Usage, RvtIO.Cli.SaveAsCommand.Run([input, dest, "--report", hard], TextWriter.Null, saveError));
            Assert.Contains("--report must not name the source or output file.", saveError.ToString());
            Assert.Equal("x", File.ReadAllText(input));
        }
        finally { Cleanup(folder); }
    }

    [Fact]
    public void RealPathKeepsAPathWithoutLinksAndTheComponentsThatDoNotExist()
    {
        string tempRoot = ExportReport.RealPath(Path.GetFullPath(Path.GetTempPath()));
        string folder = Path.Combine(tempRoot, "rvt-realpath-" + Guid.NewGuid().ToString("N"), "a", "b.json");
        Assert.Equal(folder, ExportReport.RealPath(folder));
        string root = Path.GetPathRoot(folder)!;
        Assert.Equal(root, ExportReport.RealPath(root));
    }

    [Fact]
    public void FailedReportDropsSourcePathsFromTheStackTrace()
    {
        var report = ExportReport.Failed("a.rvt", 1, "decode-failure", "m", RvtExitCodes.DecodeError, null, false, false,
            "System.Exception: x\n   at A.B() in /build/work dir/A.cs:line 12\n   at C.D()");
        Assert.Equal("System.Exception: x\n   at A.B()\n   at C.D()", report.Error!.Detail);
    }

    [Fact]
    public void FailedReportSerializesTheDocumentedFields()
    {
        var diagnostics = new RvtReadDiagnostics();
        diagnostics.LimitHits.Add(new ReaderLimitHit("Global/ElemTable", "elem-table-recovery-bytes", 1000, true, "cut"));
        var report = ExportReport.Failed("a.rvt", 12, "truncated-or-damaged", "it is cut", RvtExitCodes.InputError, diagnostics, raised: false, partialAllowed: true);

        using var json = JsonDocument.Parse(report.ToJson());
        var root = json.RootElement;
        Assert.Equal("rvtio-report/1", root.GetProperty("schema").GetString());
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("a.rvt", root.GetProperty("input").GetProperty("file").GetString());
        Assert.Equal("truncated-or-damaged", root.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("it is cut", root.GetProperty("error").GetProperty("message").GetString());
        Assert.True(root.GetProperty("limits").GetProperty("partialAllowed").GetBoolean());
        var hit = root.GetProperty("limits").GetProperty("hit")[0];
        Assert.Equal("elem-table-recovery-bytes", hit.GetProperty("limit").GetString());
        Assert.Equal(1000, hit.GetProperty("value").GetInt64());
        Assert.False(root.TryGetProperty("export", out _));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    public void ALimitHitExitsWithTheLimitCodeUnlessPartialOutputIsAllowed(bool allowPartial, int expected)
    {
        var diagnostics = new RvtReadDiagnostics();
        Assert.Equal(0, RvtExitCodes.ForRead(diagnostics, allowPartial));
        diagnostics.LimitHits.Add(new ReaderLimitHit("Global/ElemTable", "elem-table-recovery-bytes", 1000, true, "cut"));
        Assert.Equal(expected, RvtExitCodes.ForRead(diagnostics, allowPartial));
    }

    [Fact]
    public void ExportPartComputesFractionsAndKeepsEverySkippedClass()
    {
        var report = ExportReport.Failed("a.rvt", 1, "x", "x", 2, null, false, false);
        var skipped = new SkipLog();
        skipped.Add(1, "OST_Walls", "type-element");
        skipped.Add(2, "OST_Doors", "hosted-element-without-host");
        skipped.Add(3, "OST_Doors", "type-element");
        report.SetExport("ifc", 1, skipped);

        Assert.Equal(4, report.Export!.Considered);
        Assert.Equal(0.25, report.Export.ExportedFraction);
        Assert.Equal(3, report.Export.SkippedByClass.Count);
        Assert.Equal(3, report.Export.SkippedByClass.Sum(g => g.Count));
        Assert.All(report.Export.SkippedByClass, g => Assert.False(string.IsNullOrEmpty(g.Reason)));
    }

    [Fact]
    public void ExportOfNothingIsFullyExported()
    {
        var report = ExportReport.Failed("a.rvt", 1, "x", "x", 2, null, false, false);
        report.SetExport("json", 0, new SkipLog());
        Assert.Equal(1.0, report.Export!.ExportedFraction);
    }

    [Fact]
    public void LimitsFollowTheDocumentedDefaultsAndRaiseToThePlatformLength()
    {
        Assert.Equal((256 * 1024 * 1024, 4096), ElemTableRecover.Limits(false));
        Assert.Equal((Array.MaxLength, int.MaxValue), ElemTableRecover.Limits(true));
    }

    [Fact]
    public void AMissingFileIsAnInputError()
    {
        string missing = Path.Combine(Path.GetTempPath(), "rr-missing-" + Guid.NewGuid() + ".rvt");
        var error = Assert.Throws<RvtInputException>(() => RvtContainerCheck.Verify(missing));
        Assert.Equal("cannot-open", error.Kind);
        // the message goes into the report: the file name, never the directory
        Assert.DoesNotContain(Path.GetTempPath(), error.Message);
        Assert.Contains(Path.GetFileName(missing), error.Message);
    }

    [Fact]
    public void AnEmptyFileIsAnInputError()
    {
        using var file = new TempFile([]);
        Assert.Equal("empty-file", Assert.Throws<RvtInputException>(() => RvtContainerCheck.Verify(file.Path)).Kind);
    }

    [Fact]
    public void ATextFileIsNotACompoundFile()
    {
        using var file = new TempFile("hello"u8.ToArray());
        Assert.Equal("not-a-compound-file", Assert.Throws<RvtInputException>(() => RvtContainerCheck.Verify(file.Path)).Kind);
        Assert.Throws<RvtInputException>(() => new RvtFileReader().Read(file.Path));
    }

    [SkippableTheory]
    [InlineData(2)] [InlineData(3)] [InlineData(10)]
    public void ASampleCutToAFractionIsATruncatedFile(int divisor)
    {
        byte[] whole = File.ReadAllBytes(TestFiles.Find("TESTFILE.rvt"));
        using var file = new TempFile(whole[..(whole.Length / divisor)]);
        var error = Assert.Throws<RvtInputException>(() => RvtContainerCheck.Verify(file.Path));
        Assert.Contains(error.Kind, new[] { "truncated-or-damaged", "not-a-compound-file" });
        Assert.DoesNotContain("   at ", error.Message);
        // the reader raises the same error and does not start a decoder
        Assert.IsType<RvtInputException>(Record.Exception(() => new RvtFileReader().Read(file.Path)));
    }

    [SkippableTheory]
    [InlineData(4096)] [InlineData(65536)] [InlineData(1_000_000)]
    public void ASampleWithItsTailCutOffDoesNotPassTheCheck(int cut)
    {
        byte[] whole = File.ReadAllBytes(TestFiles.Find("TESTFILE.rvt"));
        using var file = new TempFile(whole[..(whole.Length - cut)]);
        Assert.IsType<RvtInputException>(Record.Exception(() => RvtContainerCheck.Verify(file.Path)));
    }

    [SkippableFact]
    public void ASampleWithADamagedHeaderIsNotACompoundFile()
    {
        byte[] bytes = File.ReadAllBytes(TestFiles.Find("TESTFILE.rvt"));
        Array.Clear(bytes, 0, 512);
        using var file = new TempFile(bytes);
        Assert.Equal("not-a-compound-file", Assert.Throws<RvtInputException>(() => RvtContainerCheck.Verify(file.Path)).Kind);
    }

    [SkippableFact]
    public void ASampleWithAZeroedMiddleDoesNotEndInAnUnhandledException()
    {
        byte[] bytes = File.ReadAllBytes(TestFiles.Find("TESTFILE.rvt"));
        Array.Clear(bytes, bytes.Length / 2, 4096);
        using var file = new TempFile(bytes);
        // zeroed payload is the decoders' business (recovery); the container itself stays readable
        var failure = Record.Exception(() => RvtContainerCheck.Verify(file.Path));
        Assert.True(failure == null || failure is RvtInputException, failure?.ToString());
    }

    [SkippableFact]
    public void AnIntactSampleReportsEveryPartitionStreamDecodedInFull()
    {
        string path = TestFiles.Find("TESTFILE.rvt");
        var diagnostics = new RvtReadDiagnostics();
        var reader = new RvtFileReader(new RvtReaderOptions { Diagnostics = diagnostics });
        var info = reader.Read(path);

        Assert.NotEmpty(diagnostics.Streams);
        Assert.All(diagnostics.Streams, s =>
        {
            Assert.True(s.Members > 0);
            Assert.Equal(s.Members, s.Decoded);
            Assert.Equal(0, s.Failed);
            Assert.Equal(0, s.Recovered);
            Assert.Equal(0, s.Unverified);
        });
        Assert.Contains(diagnostics.Streams, s => s.Name.StartsWith("Partitions/"));
        Assert.Contains(diagnostics.Streams, s => s.Name == "Global/ElemTable");
        Assert.Contains(diagnostics.Streams, s => s.Name == "Global/History");
        Assert.Contains(diagnostics.Streams, s => s.Name == "Formats/Latest");
        Assert.Empty(diagnostics.LimitHits);
        var report = ExportReport.ForRead("TESTFILE.rvt", new FileInfo(path).Length, diagnostics, false, false,
            reader.LastParsingState!.ElemTable.Keys.ToList(), info);
        Assert.Equal("complete", report.Status);
        Assert.Equal(0, report.ExitCode);
        Assert.Empty(report.Issues);
        Assert.Equal(1.0, report.Elements!.DecodedFraction);
        report.SetJsonExport(info, full: false);
        Assert.Equal(info.Elements.Count, report.Export!.Considered);
        Assert.Equal(report.Export.Skipped, report.Export.SkippedByClass.Sum(g => g.Count));
    }

    [SkippableTheory]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    public void AReadCutByALimitIsPartialAndExitsByTheOption(bool allowPartial, int expectedExit)
    {
        var diagnostics = new RvtReadDiagnostics();
        var reader = new RvtFileReader(new RvtReaderOptions { Diagnostics = diagnostics });
        var info = reader.Read(TestFiles.Find("TESTFILE.rvt"));
        diagnostics.LimitHits.Add(new ReaderLimitHit("Global/ElemTable", "elem-table-recovery-bytes", 1000, true, "cut"));

        var report = ExportReport.ForRead("TESTFILE.rvt", 1, diagnostics, false, allowPartial, reader.LastParsingState!.ElemTable.Keys.ToList(), info);

        Assert.Equal("partial", report.Status);
        Assert.Equal(expectedExit, report.ExitCode);
        Assert.Single(report.Limits.Hit);
        Assert.Equal(allowPartial, report.Limits.PartialAllowed);
    }

    [SkippableFact]
    public void AnElementTableWithoutRowsIsAPartialRead()
    {
        var diagnostics = new RvtReadDiagnostics();
        var info = new RvtFileReader(new RvtReaderOptions { Diagnostics = diagnostics }).Read(TestFiles.Find("TESTFILE.rvt"));

        var report = ExportReport.ForRead("TESTFILE.rvt", 1, diagnostics, false, false, [], info);

        Assert.Equal("partial", report.Status);
        Assert.Equal(0, report.ExitCode);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(("Global/ElemTable", "no-rows"), (issue.Stream, issue.Code));
        Assert.Equal(1.0, report.Elements!.DecodedFraction);
    }

    [SkippableFact]
    public void TheElemTableRecoveryStopsAtItsLimitsAndSaysWhich()
    {
        byte[] data;
        using (var source = RootStorage.OpenRead(TestFiles.Find("Test_2020.rvt")))
        using (var stream = source.OpenStorage("Global").OpenStream("ElemTable"))
        {
            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }
        int gzStart = Array.FindIndex(data, i => i == 0x1F);
        while (data[gzStart + 1] != 0x8B) gzStart = Array.FindIndex(data, gzStart + 1, i => i == 0x1F);

        Assert.Null(ElemTableRecover.Recover(data, gzStart, out var bytesCut, (1000, 4096)));
        Assert.Equal(("elem-table-recovery-bytes", 1000L), bytesCut.LimitHit);

        ElemTableRecover.Recover(data, gzStart, out var iterationsCut, (ElemTableRecover.DefaultMaxRecoveredBytes, 0));
        Assert.Equal(("elem-table-recovery-iterations", 0L), iterationsCut.LimitHit);

        ElemTableRecover.Recover(data, gzStart, out var inLimits);
        Assert.Null(inLimits.LimitHit);

        // A container whose ElemTable fails the exact paged decode (one flipped byte in the middle) is read through the recovery tier.
        byte[] damaged = (byte[])data.Clone();
        damaged[damaged.Length / 2] ^= 0xFF;
        if (PagedGzipStream.Decompress(damaged) != null) throw new SkipException("the damaged copy still decodes exactly");
        var container = new MemoryStream();
        using (var created = RootStorage.Create(container, OpenMcdf.Version.V3, StorageModeFlags.LeaveOpen))
        {
            using (var table = created.CreateStorage("Global").CreateStream("ElemTable")) table.Write(damaged);
        }
        container.Position = 0;
        using var root = RootStorage.Open(container, StorageModeFlags.LeaveOpen);
        var diagnostics = new RvtReadDiagnostics();
        ElemTableReader.Read(root, out _, out _, out _, (1000, 4096), diagnostics);
        var hit = Assert.Single(diagnostics.LimitHits);
        Assert.Equal(("Global/ElemTable", "elem-table-recovery-bytes", 1000L, true), (hit.Stream, hit.Limit, hit.Value, hit.Raisable));

        var inLimitsDiagnostics = new RvtReadDiagnostics();
        ElemTableReader.Read(root, out _, out _, out _, null, inLimitsDiagnostics);
        Assert.Empty(inLimitsDiagnostics.LimitHits);
        var elemTableStream = Assert.Single(inLimitsDiagnostics.Streams, s => s.Name == "Global/ElemTable");
        Assert.True(elemTableStream.Recovered > 0 || elemTableStream.Unverified > 0);
        var recoveredReport = ExportReport.ForRead("damaged.rvt", 1, inLimitsDiagnostics, false, false, [1], new RvtIO.Model.RevitFileInfo());
        Assert.Equal("partial", recoveredReport.Status);
    }

    /// <summary>A container whose Global and Formats storages hold the given streams, as a damaged file would: the bytes of a stream
    /// whose gzip header is gone, next to one that is intact.</summary>
    private static RootStorage ContainerWith(params (string Storage, string Stream, byte[] Bytes)[] streams)
    {
        var container = new MemoryStream();
        using (var created = RootStorage.Create(container, OpenMcdf.Version.V3, StorageModeFlags.LeaveOpen))
            foreach (var (storage, stream, bytes) in streams)
            {
                var parent = created.TryOpenStorage(storage, out var existing) ? existing : created.CreateStorage(storage);
                using var written = parent.CreateStream(stream);
                written.Write(bytes);
            }
        container.Position = 0;
        return RootStorage.Open(container, StorageModeFlags.LeaveOpen);
    }

    private static byte[] NoGzipMember(int length)
    {
        byte[] bytes = new byte[length];
        new Random(5).NextBytes(bytes);
        bytes[0] = 0; bytes[1] = 1;
        return bytes;
    }

    private static byte[] IntactMember()
    {
        using var stream = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(new byte[3000].Select((_, i) => (byte)(i * 7)).ToArray());
        byte[] member = stream.ToArray();
        member[8] = 0;
        member[9] = 0x0B;   // the RVT member header
        return member;
    }

    private static T InvokeReader<T>(RvtFileReader reader, string method, params object?[] arguments) =>
        (T)typeof(RvtFileReader).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(reader, arguments)!;

    private static ExportReport ReportOf(RvtReadDiagnostics diagnostics) =>
        ExportReport.ForRead("damaged.rvt", 1, diagnostics, false, false, [1], new RevitFileInfo());

    [Fact]
    public void AStorageStreamWithoutAGzipHeaderIsAFailedStreamAndThePartialReport()
    {
        // The Global storage is read stream by stream (DecompressStorage): a stream that lost its gzip header decodes to nothing
        // and is no stream without members.
        using var root = ContainerWith(("Global", "History", NoGzipMember(14146)), ("Global", "PartitionTable", IntactMember()));
        var diagnostics = new RvtReadDiagnostics();

        InvokeReader<List<byte[]>>(new RvtFileReader(new RvtReaderOptions { Diagnostics = diagnostics }), "DecompressStorage", root, "Global", null);

        var history = Assert.Single(diagnostics.Streams, s => s.Name == "Global/History");
        Assert.Equal((14146L, 1, 0, 1), (history.RawBytes, history.Members, history.Decoded, history.Failed));
        var intact = Assert.Single(diagnostics.Streams, s => s.Name == "Global/PartitionTable");
        Assert.Equal((1, 1, 0), (intact.Members, intact.Decoded, intact.Failed));
        var report = ReportOf(diagnostics);
        Assert.Equal("partial", report.Status);
        Assert.Equal(0, report.Streams.Single(s => s.Name == "Global/History").DecodedFraction);
        Assert.Equal(1, report.Streams.Single(s => s.Name == "Global/PartitionTable").DecodedFraction);
    }

    [Fact]
    public void ANamedStreamWithoutAGzipHeaderIsAFailedStreamAndThePartialReport()
    {
        // The episode table is read by name (ReadEpisodeTable, then DecompressNamedStream when the exact decode rejects it).
        using var root = ContainerWith(("Global", "History", NoGzipMember(14146)));
        var diagnostics = new RvtReadDiagnostics();

        InvokeReader<object>(new RvtFileReader(new RvtReaderOptions { Diagnostics = diagnostics }), "ReadEpisodeTable", root, null, null);

        var history = Assert.Single(diagnostics.Streams, s => s.Name == "Global/History");
        Assert.Equal((14146L, 1, 0, 1), (history.RawBytes, history.Members, history.Decoded, history.Failed));
        var report = ReportOf(diagnostics);
        Assert.Equal("partial", report.Status);
        Assert.Equal(0, Assert.Single(report.Streams).DecodedFraction);
    }
    [Fact]
    public void RebuiltMembersTreatStreamAsUnverifiedInReportStatus()
    {
        var diagnostics = new RvtReadDiagnostics();
        diagnostics.Streams.Add(new StreamRecord("Partitions/0", 1000, 10, 10, 0, 1, 0));
        var report = ExportReport.ForRead("test.rvt", 1000, diagnostics, false, false, [1], new RvtIO.Model.RevitFileInfo());
        Assert.Equal("partial", report.Status);
    }

    [Theory]
    [InlineData(1, 0, 0, true, "Partitions/0", "failed-members")]
    [InlineData(0, 1, 1, true, "Partitions/0", "recovered-members")]
    [InlineData(0, 0, 1, true, "Partitions/0", "unverified-members")]
    [InlineData(0, 0, 0, false, "Global/ElemTable", "no-rows")]
    public void StrictModeExitsWithPartialReadCodeAndListsTheStreamAndReason(
        int failed, int recovered, int unverified, bool hasRows, string expectedStream, string expectedCode)
    {
        var diagnostics = new RvtReadDiagnostics();
        diagnostics.Streams.Add(new StreamRecord("Partitions/0", 1000, 4, 4 - failed, failed, recovered, unverified));
        uint[] rows = hasRows ? [1u] : [];

        var lenient = ExportReport.ForRead("test.rvt", 1000, diagnostics, false, false, rows, new RevitFileInfo(), strict: false);
        Assert.Equal("partial", lenient.Status);
        Assert.Equal(RvtExitCodes.Success, lenient.ExitCode);
        Assert.False(lenient.Limits.Strict);
        var lenientIssue = Assert.Single(lenient.Issues);
        Assert.Equal((expectedStream, expectedCode), (lenientIssue.Stream, lenientIssue.Code));

        var strictReport = ExportReport.ForRead("test.rvt", 1000, diagnostics, false, false, rows, new RevitFileInfo(), strict: true);
        Assert.Equal("partial", strictReport.Status);
        Assert.Equal(RvtExitCodes.PartialRead, strictReport.ExitCode);
        Assert.True(strictReport.Limits.Strict);
        var strictIssue = Assert.Single(strictReport.Issues);
        Assert.Equal((expectedStream, expectedCode), (strictIssue.Stream, strictIssue.Code));
    }

    [Fact]
    public void StrictModeKeepsExitCodeZeroOnCompleteReadAndLimitCodeOnUnacceptedLimitHit()
    {
        var complete = new RvtReadDiagnostics();
        complete.Streams.Add(new StreamRecord("Partitions/0", 1000, 4, 4, 0, 0, 0));
        var completeReport = ExportReport.ForRead("test.rvt", 1000, complete, false, false, [1u], new RevitFileInfo(), strict: true);
        Assert.Equal("complete", completeReport.Status);
        Assert.Equal(RvtExitCodes.Success, completeReport.ExitCode);
        Assert.Empty(completeReport.Issues);

        var limitCut = new RvtReadDiagnostics();
        limitCut.LimitHits.Add(new ReaderLimitHit("Global/ElemTable", "elem-table-recovery-bytes", 1000, true, "cut"));
        var limitReport = ExportReport.ForRead("test.rvt", 1000, limitCut, false, partialAllowed: false, [1u], new RevitFileInfo(), strict: true);
        Assert.Equal("partial", limitReport.Status);
        Assert.Equal(RvtExitCodes.LimitExceeded, limitReport.ExitCode);
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rr-report-" + Guid.NewGuid() + ".rvt");

        public TempFile(byte[] content) => File.WriteAllBytes(Path, content);

        public void Dispose() => File.Delete(Path);
    }
}
