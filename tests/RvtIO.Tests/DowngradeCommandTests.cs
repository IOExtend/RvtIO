using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenMcdf;
using RvtIO.Cli;
using RvtIO.Writing;
using Xunit;
using Cls = RvtIO.Tests.SchemaStreamEditorTests.Cls;
using NoRef = RvtIO.Tests.SchemaStreamEditorTests.NoRef;

namespace RvtIO.Tests;

/// <summary>The downgrade command (<c>rvtio downgrade ... --check</c>) on small synthetic project files whose class tables carry the
/// marker class of a release, so that no private file is needed: exit codes, the one-line refusals, the summary, the shape of the JSON
/// report, and that nothing but the report is written.</summary>
public sealed class DowngradeCommandTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvtio-downgrade-" + Guid.NewGuid().ToString("N"));

    public DowngradeCommandTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string P(string name) => Path.Combine(_folder, name);

    private static string G(int n) => SchemaStreamEditorTests.Guid(n);

    private static Cls C(string name, uint version, params string[] guids) => new(name, new NoRef(), version, [], guids);

    /// <summary>The class ids of the framing marker per release (the same numbers the schema reader uses to name the release).</summary>
    private const int Marker2025 = 913, Marker2026 = 931, Marker2027 = 951;

    /// <summary>A class table whose ContentMarker has the given class id (no marker when it is negative), followed by the given classes.</summary>
    private static byte[] Table(int markerId, params Cls[] classes)
    {
        var writer = new SchemaStreamEditorTests.Writer();
        if (markerId >= 12)
        {
            for (int id = 12; id < markerId; id++) writer.Top(C("Filler" + id, 1));
            writer.Top(C("ContentMarker", 1));
        }
        foreach (var c in classes) writer.Top(c);
        return writer.Build();
    }

    /// <summary>Writes a compound file with Formats/Latest as a paged gzip stream and, when given, a BasicFileInfo that names the saving release.</summary>
    private string File_(string name, int markerId, Cls[] classes, int? savedRelease = null, bool withTable = true, bool damageTable = false, byte[]? partition = null)
    {
        string path = P(name);
        using var root = RootStorage.Create(path, OpenMcdf.Version.V3);
        if (withTable)
        {
            byte[] stored = PageCodec.EncodePadded(GzipMember.Write(Table(markerId, classes)));
            if (damageTable) stored[20] ^= 0xFF; // inside the deflate data, behind the 10-byte gzip header
            using var stream = root.CreateStorage("Formats").CreateStream("Latest");
            stream.Write(stored);
        }
        if (partition != null)
        {
            using var stream = root.CreateStorage("Partitions").CreateStream("1");
            stream.Write(partition);
        }
        if (savedRelease is int year)
        {
            using var info = root.CreateStream("BasicFileInfo");
            info.Write([.. BitConverter.GetBytes(1), .. BitConverter.GetBytes(4), .. Encoding.Unicode.GetBytes(year.ToString())]);
        }
        return path;
    }

    // the filler classes between the two marker ids exist in the 2026 table only, besides Gone
    private const int OnlyInSource = Marker2026 - Marker2025 + 1;

    private string Source(string name = "source.rvt") => File_(name, Marker2026,
        [C("Changed", 9, G(1), G(2)), C("Stays", 3), C("Gone", 1)], savedRelease: 2026);

    private string Profile(string name = "profile.rvt") => File_(name, Marker2025,
        [C("Changed", 7, G(2), G(3)), C("Stays", 3), C("New", 1)], savedRelease: 2025);

    private static (int Exit, string Out, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = DowngradeCommand.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private string[] Listing() => [.. Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private static string[] WithReport(string[] before) => [.. before.Append("report.json").Order(StringComparer.Ordinal)];

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ---- a plan ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Check_PrintsTheSummary_WritesOnlyTheReport_AndLeavesBothFilesAlone()
    {
        string source = Source(), profile = Profile();
        string sourceHash = Hash(source), profileHash = Hash(profile);
        string[] before = Listing();
        string report = P("report.json");

        var (exit, output, error) = Run(source, "--target-profile", profile, "--check", "--report", report);

        Assert.Equal(0, exit);
        Assert.Contains("release 2026", output);
        Assert.Contains("release 2025", output);
        Assert.Contains("class versions to lower: 1", output);
        Assert.Contains("upgrade-identifier lists to change: 1", output);
        Assert.Contains($"Classes only in the source: {OnlyInSource}", output);
        Assert.Contains("Classes only in the profile: 1", output);
        Assert.Contains("no file was written", output);
        // the synthetic file has no partition streams, so the elements are not counted; that is said, not shown as zero
        Assert.Contains("elements not counted", output);
        Assert.Contains("warning: the elements were not counted", error);
        Assert.Equal(WithReport(before), Listing());
        Assert.Equal(sourceHash, Hash(source));
        Assert.Equal(profileHash, Hash(profile));
    }

    [Fact]
    public void Report_HasTheDocumentedShape()
    {
        string report = P("report.json");
        Run(Source(), "--target-profile", Profile(), "--check", "--report", report);

        using var json = JsonDocument.Parse(File.ReadAllText(report));
        var root = json.RootElement;
        Assert.Equal("rvtio-downgrade-check/1", root.GetProperty("schema").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("exact", root.GetProperty("guidRule").GetString());
        Assert.Equal(("source.rvt", 2026), (root.GetProperty("source").GetProperty("file").GetString(), root.GetProperty("source").GetProperty("release").GetInt32()));
        Assert.Equal(("profile.rvt", 2025), (root.GetProperty("profile").GetProperty("file").GetString(), root.GetProperty("profile").GetProperty("release").GetInt32()));
        Assert.Equal(Marker2025 - 12 + 3, root.GetProperty("commonClasses").GetInt32()); // the filler classes, ContentMarker, Changed and Stays
        Assert.False(root.TryGetProperty("elementsInClassesOnlyInSource", out _));
        Assert.True(root.TryGetProperty("elementCountFailure", out _));

        var onlyInSource = root.GetProperty("classesOnlyInSource").EnumerateArray().ToList();
        Assert.Equal(OnlyInSource, onlyInSource.Count);
        var gone = onlyInSource.Single(c => c.GetProperty("name").GetString() == "Gone");
        Assert.All(onlyInSource, c => Assert.False(c.TryGetProperty("elements", out _)));
        Assert.Equal(["New"], root.GetProperty("classesOnlyInProfile").EnumerateArray().Select(e => e.GetString()));

        var version = Assert.Single(root.GetProperty("classesWithHigherVersion").EnumerateArray());
        Assert.Equal(("Changed", 9, 7), (version.GetProperty("name").GetString(), version.GetProperty("source").GetInt32(), version.GetProperty("profile").GetInt32()));
        Assert.Equal(0, root.GetProperty("classesWithLowerVersion").GetInt32());
        Assert.Empty(root.GetProperty("fieldDifferences").EnumerateArray());

        var list = Assert.Single(root.GetProperty("upgradeIdentifierLists").EnumerateArray());
        Assert.Equal(("Changed", 2, 2, 2, 1, 1),
            (list.GetProperty("name").GetString(), list.GetProperty("source").GetInt32(), list.GetProperty("profile").GetInt32(),
             list.GetProperty("result").GetInt32(), list.GetProperty("added").GetInt32(), list.GetProperty("removed").GetInt32()));

        var edits = root.GetProperty("edits");
        Assert.Equal((1, 1), (edits.GetProperty("classVersions").GetInt32(), edits.GetProperty("upgradeIdentifierLists").GetInt32()));
        Assert.True(edits.GetProperty("classTableBytesBefore").GetInt32() > 0);
        Assert.NotEqual(0, edits.GetProperty("classTableBytesAfter").GetInt32());
        // names only: no path of the machine reaches the report
        Assert.DoesNotContain(_folder, File.ReadAllText(report));
    }

    [Fact]
    public void MinimalRule_IsSelectedByTheOption_AndNamedInTheReport()
    {
        string report = P("report.json");

        var (exit, output, _) = Run(Source(), "--target-profile", Profile(), "--check", "--guid-rule", "minimal", "--report", report);

        Assert.Equal(0, exit);
        Assert.Contains("rule: minimal", output);
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("minimal", json.RootElement.GetProperty("guidRule").GetString());
        var list = Assert.Single(json.RootElement.GetProperty("upgradeIdentifierLists").EnumerateArray());
        Assert.Equal((1, 0, 1), (list.GetProperty("result").GetInt32(), list.GetProperty("added").GetInt32(), list.GetProperty("removed").GetInt32()));
    }

    [Fact]
    public void Check_WithoutReport_WritesNothingAtAll()
    {
        string source = Source(), profile = Profile();
        string[] before = Listing();

        var (exit, _, _) = Run(source, "--target-profile", profile, "--check");

        Assert.Equal(0, exit);
        Assert.Equal(before, Listing());
    }

    // ---- refusals ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Marker2025, Marker2025, "profile-not-older")]
    [InlineData(Marker2026, Marker2026, "profile-not-older")]
    [InlineData(Marker2025, Marker2026, "profile-not-older")]
    [InlineData(Marker2026, -1, "profile-release")]
    [InlineData(-1, Marker2025, "source-release")]
    public void ReleaseOutOfScope_IsRefused_WithOneLineAndExitCode5_AndNothingIsWritten(int sourceMarker, int profileMarker, string kind)
    {
        string source = File_("source.rvt", sourceMarker, [C("A", 2)]);
        string profile = File_("profile.rvt", profileMarker, [C("A", 1)]);
        string[] before = Listing();
        string report = P("report.json");

        var (exit, output, error) = Run(source, "--target-profile", profile, "--check", "--report", report);

        Assert.Equal(5, exit);
        Assert.Equal("", output);
        Assert.StartsWith("error: ", error);
        Assert.Contains("Nothing was written (exit code 5).", error);
        Assert.Single(error.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => l.StartsWith("error", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal(("refused", kind), (json.RootElement.GetProperty("status").GetString(), json.RootElement.GetProperty("kind").GetString()));
        Assert.Equal(WithReport(before), Listing());
    }

    [Fact]
    public void ASourceWhoseBasicFileInfoNamesAnotherRelease_IsRefused()
    {
        string source = File_("source.rvt", Marker2026, [C("A", 2)], savedRelease: 2025);

        var (exit, _, error) = Run(source, "--target-profile", Profile(), "--check");

        Assert.Equal(5, exit);
        Assert.Contains("saved by release 2025", error);
    }

    [Fact]
    public void AProfileMayBeAnyOlderRelease_NotOnlyTheNextOne()
    {
        string profile = File_("profile24.rvt", 891, [C("Changed", 5, G(2))]);

        var (exit, output, _) = Run(Source(), "--target-profile", profile, "--check");

        Assert.Equal(0, exit);
        Assert.Contains("release 2024", output);
    }

    [Fact]
    public void AStreamThatCannotBeRead_IsAnInputError_WithExitCode2()
    {
        string profile = Profile();
        string garbage = P("garbage.rvt");
        File.WriteAllText(garbage, "not a compound file at all");
        string empty = P("empty.rvt");
        File.WriteAllBytes(empty, []);

        var cases = new (string Source, string Kind)[]
        {
            (P("missing.rvt"), "cannot-open"),
            (empty, "empty-file"),
            (garbage, "not-a-compound-file"),
            (File_("notable.rvt", Marker2026, [C("A", 2)], withTable: false), "truncated-or-damaged"),
            (File_("damaged.rvt", Marker2026, [C("A", 2)], damageTable: true), "truncated-or-damaged"),
        };
        foreach (var (source, kind) in cases)
        {
            string report = P("report-" + kind + ".json");
            var (exit, output, error) = Run(source, "--target-profile", profile, "--check", "--report", report);

            Assert.True(exit == 2, $"{Path.GetFileName(source)}: exit {exit}");
            Assert.Equal("", output);
            Assert.Contains("Nothing was written (exit code 2).", error);
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            Assert.Equal(("unreadable", kind), (json.RootElement.GetProperty("status").GetString(), json.RootElement.GetProperty("kind").GetString()));
        }
    }

    [Fact]
    public void AProfileThatCannotBeRead_IsAnInputError_WithExitCode2()
    {
        string profile = File_("profile.rvt", Marker2025, [C("A", 1)], damageTable: true);

        var (exit, _, error) = Run(Source(), "--target-profile", profile, "--check");

        Assert.Equal(2, exit);
        Assert.Contains("profile file 'profile.rvt'", error);
    }

    // A source whose class table names the framing classes, so that the elements are counted; the partition stream is what the test gives.
    private string CountableSource(byte[]? partition) => File_("countable.rvt", Marker2026,
        [C("ContentKey", 1), C("SegmentMarker", 1), C("SegmentCheckback", 1), C("SignatureMarker", 1), C("Stays", 3)], savedRelease: 2026, partition: partition);

    [Fact]
    public void ADamagedPartitionStream_IsAnInputError_NotAWarning()
    {
        string profile = Profile();
        string report = P("damaged-report.json");
        string source = CountableSource([0x42, 0x42, 0x42, 0x42, 0x42, 0x42, 0x42, 0x42]);

        Assert.Equal("truncated-or-damaged", Assert.Throws<RvtInputException>(() => DowngradePlanner.Plan(source, profile, GuidRule.Exact)).Kind);
        var (exit, output, error) = Run(source, "--target-profile", profile, "--check", "--report", report);

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Contains("Partition stream 1 is damaged", error);
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal(("unreadable", "truncated-or-damaged"), (json.RootElement.GetProperty("status").GetString(), json.RootElement.GetProperty("kind").GetString()));
    }

    [Fact]
    public void ALayoutThatCannotBeCounted_IsStillAWarning_NotDamage()
    {
        // no partition stream at all: the count is not made, the plan is
        var (exit, _, error) = Run(CountableSource(null), "--target-profile", Profile(), "--check");

        Assert.Equal(0, exit);
        Assert.Contains("warning: the elements were not counted", error);
    }

    [Fact]
    public void AnUnusableReportOrInputPath_IsAUsageError_WithExitCode1_AndNothingIsWritten()
    {
        string source = Source(), profile = Profile();
        Directory.CreateDirectory(P("folder"));
        string[] before = Listing();
        string[][] bad =
        [
            [source, "--target-profile", profile, "--check", "--report", ""],
            [source, "--target-profile", profile, "--check", "--report", "   "],
            [source, "--target-profile", profile, "--check", "--report", P("a\u0000b.json")],
            [source, "--target-profile", profile, "--check", "--report", P("folder")],
            ["", "--target-profile", profile, "--check"],
            [source, "--target-profile", " ", "--check"],
            [source, "--target-profile", P("a\u0000b.rvt"), "--check"],
            [P("a\u0000b.rvt"), "--target-profile", profile, "--check"],
        ];
        foreach (var args in bad)
        {
            var (exit, output, error) = Run(args);

            Assert.True(exit == 1, $"{string.Join(' ', args.Select(a => a.Length == 0 ? "''" : a))}: exit {exit}");
            Assert.Equal("", output);
            Assert.Contains("Nothing was read or written.", error);
            Assert.DoesNotContain('\n', error.TrimEnd());
        }
        Assert.Equal(before, Listing());
    }

    // ---- usage -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void UsageErrors_ExitWith1_AndWriteNothing()
    {
        string source = Source(), profile = Profile();
        string[] before = Listing();
        string[][] bad =
        [
            [],
            [source, "--check"],
            [source, "--target-profile", profile],
            [source, "--target-profile", profile, "--check", "--guid-rule", "other"],
            [source, "--target-profile", profile, "--check", "--guid-rule"],
            [source, "--target-profile", "--check"],
            [source, "--target-profile", profile, "--check", "--unknown"],
            [source, source, "--target-profile", profile, "--check"],
            [source, "--target-profile", profile, "--target-profile", profile, "--check"],
            [source, "--target-profile", profile, "--check", "--report", source],
            [source, "--target-profile", profile, "--check", "--report", profile],
            ["--target-profile", profile, "--check"],
        ];
        foreach (var args in bad)
        {
            var (exit, output, error) = Run(args);

            Assert.True(exit == 1, $"{string.Join(' ', args)}: exit {exit}");
            Assert.Equal("", output);
            Assert.Contains("Usage: rvtio downgrade", error);
        }
        Assert.Equal(before, Listing());
    }

    [Fact]
    public void WithoutCheckOrOutput_NoFileCanBeWritten_AndTheMessageSaysSo()
    {
        var (exit, _, error) = Run(Source(), "--target-profile", Profile());

        Assert.Equal(1, exit);
        Assert.Contains("--check", error);
        Assert.Contains("--output", error);
    }
}
