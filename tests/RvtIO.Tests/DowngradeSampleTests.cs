using System.Security.Cryptography;
using System.Text.Json;
using OpenMcdf;
using RvtIO.Cli;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

/// <summary>The downgrade planner on the project files of the private fixture folder (<see cref="TestFiles"/>): one project per release is
/// found by the release of its class table, whatever its file name. Skipped without a project of the release a test needs. The numbers are
/// those measured on the sample projects of Revit 2026 and 2025 (the class tables of all files of one release are identical).</summary>
public class DowngradeSampleTests(ITestOutputHelper output)
{
    private static string? FirstOfRelease(int release)
    {
        foreach (string path in PageCodecSampleTests.ProjectFiles())
        {
            using var root = RootStorage.OpenRead(path);
            if (TfSchemaReader.Read(root) is { } document && SchemaRelease.FromSchema(new FormatsSchema(document)) == release) return path;
        }
        return null;
    }

    private static string Find(int release) => FirstOfRelease(release) ?? throw TestFiles.Missing($"a project of release {release}");

    private static SchemaDocument SchemaOf(string path)
    {
        using var root = RootStorage.OpenRead(path);
        return TfSchemaReader.Read(root)!;
    }

    [SkippableFact]
    public void Plan_2026Source_2025Profile_GivesTheMeasuredNumbers()
    {
        string source = Find(2026), profile = Find(2025);

        var result = DowngradePlanner.Plan(source, profile, GuidRule.Exact);
        var plan = result.Plan;

        output.WriteLine($"classes {plan.SourceClasses}/{plan.TargetClasses}, common {plan.CommonClasses}, only source {plan.ClassesOnlyInSource.Count}, only profile {plan.ClassesOnlyInTarget.Count}, " +
            $"higher {plan.HigherVersions.Count}, field differences {plan.FieldDifferences.Count}, lists {plan.GuidLists.Count}, elements {plan.ElementsInSourceOnlyClasses}");
        Assert.Equal((2026, 2025), (result.SourceRelease, result.TargetRelease));
        Assert.Equal((4690, 4600, 4584), (plan.SourceClasses, plan.TargetClasses, plan.CommonClasses));
        Assert.Equal(106, plan.ClassesOnlyInSource.Count);
        Assert.Equal(16, plan.ClassesOnlyInTarget.Count);
        Assert.Equal(67, plan.HigherVersions.Count);
        Assert.Equal(0, plan.LowerVersions);
        Assert.Equal(69, plan.FieldDifferences.Count);
        Assert.Equal(67, plan.Edits.Count(e => e.Version is not null));
        Assert.Null(result.CountFailure);

        // upgrade-identifier lists: 10 classes, 94 identifiers removed and 14 of the target added back
        Assert.Equal(10, plan.GuidLists.Count);
        Assert.Equal(94, plan.GuidLists.Sum(l => l.Removed));
        Assert.Equal(14, plan.GuidLists.Sum(l => l.Added));
        var document = plan.GuidLists.Single(l => l.Name == "ADocument");
        Assert.Equal((1509, 1439, 1439, 13, 83), (document.Source, document.Target, document.Result, document.Added, document.Removed));
        Assert.All(plan.GuidLists, l => Assert.Equal(l.Target, l.Result));

        // the elements of the classes the profile lacks: 64 in 3 of the 106 classes, counted from the partition streams
        Assert.Equal(64, plan.ElementsInSourceOnlyClasses);
        Assert.Equal(3, plan.ClassesOnlyInSource.Count(c => c.Elements > 0));
        Assert.True(result.EditedStreamBytes < result.SourceStreamBytes);
    }

    [SkippableFact]
    public void Plan_MinimalRule_RemovesTheSameIdentifiers_AndAddsNone()
    {
        var plan = DowngradePlanner.Plan(Find(2026), Find(2025), GuidRule.Minimal).Plan;

        Assert.Equal(10, plan.GuidLists.Count);
        Assert.Equal(94, plan.GuidLists.Sum(l => l.Removed));
        Assert.Equal(0, plan.GuidLists.Sum(l => l.Added));
        var document = plan.GuidLists.Single(l => l.Name == "ADocument");
        Assert.Equal((1426, 83), (document.Result, document.Removed));
        Assert.Equal(67, plan.HigherVersions.Count);
    }

    [SkippableFact]
    public void Plan_AgreesWithAnIndependentPerClassComputation()
    {
        string source = Find(2026), profile = Find(2025);
        var s = SchemaOf(source);
        var t = SchemaOf(profile);
        var targetByName = t.Classes.ToDictionary(c => c.Name);
        var plan = DowngradePlanner.Plan(source, profile, GuidRule.Exact).Plan;

        var common = s.Classes.Where(c => targetByName.ContainsKey(c.Name)).ToList();
        Assert.Equal(common.Count, plan.CommonClasses);
        Assert.Equal(common.Where(c => c.Version > targetByName[c.Name].Version).Select(c => c.Name).Order(StringComparer.Ordinal),
            plan.HigherVersions.Select(v => v.Name).Order(StringComparer.Ordinal));
        // the classes with an identifier the target list of the same class lacks are the classes whose list changes by removal
        var unknown = common.Where(c => c.Guids.Any(g => !targetByName[c.Name].Guids.Contains(g))).Select(c => c.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(unknown, plan.GuidLists.Where(l => l.Removed > 0).Select(l => l.Name).Order(StringComparer.Ordinal));
        foreach (var l in plan.GuidLists)
        {
            var sc = s.Classes.Single(c => c.Name == l.Name);
            var tc = targetByName[l.Name];
            Assert.Equal(sc.Guids.Count(g => !tc.Guids.Contains(g)), l.Removed);
            Assert.Equal(tc.Guids.Count(g => !sc.Guids.Contains(g)), l.Added);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheEditedStream_PassesThePredicateAgainstTheProfile_AndChangesNothingElse(bool minimal)
    {
        string source = Find(2026), profile = Find(2025);
        using var root = RootStorage.OpenRead(source);
        byte[] stream = PagedGzipStream.Read(root, "Formats", "Latest")!;
        var before = TfSchemaReader.Parse(stream);
        var target = SchemaOf(profile).Classes.ToDictionary(c => c.Name);
        var plan = DowngradePlanner.Compute(before, SchemaOf(profile), minimal ? GuidRule.Minimal : GuidRule.Exact);

        var after = TfSchemaReader.Parse(SchemaStreamEditor.Apply(stream, plan.Edits));

        Assert.Equal(before.Classes.Count, after.Classes.Count);
        foreach (var c in after.Classes.Where(c => target.ContainsKey(c.Name)))
        {
            Assert.True(c.Version <= target[c.Name].Version, c.Name);
            Assert.All(c.Guids, g => Assert.Contains(g, target[c.Name].Guids));
        }
        int changed = before.Classes.Zip(after.Classes).Count(p => p.First.Version != p.Second.Version || !p.First.Guids.SequenceEqual(p.Second.Guids));
        Assert.Equal(plan.Edits.Count, changed);
    }

    [SkippableFact]
    public void ElementCensus_CountsEveryElementOnce_InEachRelease()
    {
        foreach (int release in new[] { 2020, 2021, 2023, 2024, 2025, 2026 })
        {
            if (FirstOfRelease(release) is not { } path) continue;
            using var root = RootStorage.OpenRead(path);
            var counts = ElementCensus.CountByClass(root, TfSchemaReader.Read(root)!, out string? failure);

            Assert.True(counts is not null, $"release {release}: {failure}");
            output.WriteLine($"release {release}: {counts.Values.Sum()} elements in {counts.Count} classes");
            Assert.True(counts.Values.Sum() > 1000);
            Assert.DoesNotContain(counts.Keys, k => k.StartsWith("(class ", StringComparison.Ordinal));
        }
    }

    [SkippableFact]
    public void ASourceNotNewerThanTheProfile_IsRefused()
    {
        string profile = Find(2025);

        Assert.Equal("profile-not-older", Assert.Throws<DowngradeRefusedException>(() => DowngradePlanner.Plan(profile, profile, GuidRule.Exact)).Kind);
        Assert.Equal("profile-not-older", Assert.Throws<DowngradeRefusedException>(() => DowngradePlanner.Plan(profile, Find(2026), GuidRule.Exact)).Kind);
    }

    [SkippableFact]
    public void ACommandRun_WritesOnlyTheReport_AndTheReportHoldsTheSameNumbers()
    {
        string source = Find(2026), profile = Find(2025);
        string folder = Path.Combine(Path.GetTempPath(), "rvtio-downgrade-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string report = Path.Combine(folder, "report.json");
            string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
            string sourceHash = Hash(source), profileHash = Hash(profile);
            var stdout = new StringWriter();

            int exit = DowngradeCommand.Run([source, "--target-profile", profile, "--check", "--report", report], stdout, new StringWriter());

            Assert.Equal(0, exit);
            Assert.Equal(["report.json"], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
            Assert.Equal(sourceHash, Hash(source));
            Assert.Equal(profileHash, Hash(profile));
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            var root = json.RootElement;
            Assert.Equal(4584, root.GetProperty("commonClasses").GetInt32());
            Assert.Equal(106, root.GetProperty("classesOnlyInSource").GetArrayLength());
            Assert.Equal(16, root.GetProperty("classesOnlyInProfile").GetArrayLength());
            Assert.Equal(67, root.GetProperty("classesWithHigherVersion").GetArrayLength());
            Assert.Equal(69, root.GetProperty("fieldDifferences").GetArrayLength());
            Assert.Equal(10, root.GetProperty("upgradeIdentifierLists").GetArrayLength());
            Assert.Equal(64, root.GetProperty("elementsInClassesOnlyInSource").GetInt32());
            Assert.Contains("elements in them: 64", stdout.ToString());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
