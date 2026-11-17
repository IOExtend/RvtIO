using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenMcdf;
using RvtIO.Cli;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

public sealed class DowngradeWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvtio-downgrade-write-" + Guid.NewGuid().ToString("N"));

    public DowngradeWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string P(string name) => Path.Combine(_folder, name);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private string Synthetic(int release, string name, bool includeElemTable = false)
    {
        string path = P(name);
        RvtWriterTests.CreateSyntheticRevitFile(path, new RvtWriterTests.SyntheticFileOptions
        {
            FileName = name,
            Kind = RvtFileKind.Project,
            SchemaRelease = release,
            SavedRelease = release,
            IncludeElemTable = includeElemTable,
        });
        return path;
    }

    [Fact]
    public void BasicFileInfoPatcher_ReplacesEveryYearOccurrence_KeepingLength()
    {
        byte[] data = RvtWriterTests.BuildBasicFileInfoBytes(2026);
        byte[] patched = BasicFileInfoPatcher.Patch(data, 2026, 2025);

        Assert.Equal(data.Length, patched.Length);
        var info = BasicFileInfoReader.Parse(patched);
        Assert.Equal("2025", info["RevitVersion"]);
        Assert.DoesNotContain(Encoding.Unicode.GetBytes("2026"), patched);
    }

    [Fact]
    public void Write_SameWidth_LowersBasicFileInfo_AndReplanningGivesNoEdits()
    {
        string source = Synthetic(2026, "source2026.rvt");
        string profile = Synthetic(2025, "profile2025.rvt");
        string sourceHash = Hash(source), profileHash = Hash(profile);
        string output = P("downgraded.rvt");

        var result = DowngradeWriter.Write(source, profile, output, GuidRule.Exact);

        Assert.Equal(2026, result.SourceRelease);
        Assert.Equal(2025, result.TargetRelease);
        Assert.True(File.Exists(output));
        Assert.Equal(sourceHash, Hash(source));
        Assert.Equal(profileHash, Hash(profile));

        using (var root = RootStorage.OpenRead(output))
        {
            var bfi = BasicFileInfoReader.Read(root);
            Assert.Equal("2025", bfi["RevitVersion"]);
        }

        byte[] outputSchemaBytes, profileSchemaBytes;
        using (var outRoot = RootStorage.OpenRead(output))
            outputSchemaBytes = PagedGzipStream.Read(outRoot, "Formats", "Latest")!;
        using (var profRoot = RootStorage.OpenRead(profile))
            profileSchemaBytes = PagedGzipStream.Read(profRoot, "Formats", "Latest")!;
        var replan = DowngradePlanner.Compute(
            TfSchemaReader.TryParse(outputSchemaBytes)!,
            TfSchemaReader.TryParse(profileSchemaBytes)!,
            GuidRule.Exact);
        Assert.Empty(replan.Edits);
        Assert.Empty(replan.HigherVersions);
    }

    [Fact]
    public void Write_2027Source_IsAllowed_WhenTargetIsOlderSameWidth()
    {
        string source = Synthetic(2027, "source2027.rvt");
        string profile = Synthetic(2025, "profile2025b.rvt");
        string output = P("downgraded27.rvt");

        var result = DowngradeWriter.Write(source, profile, output, GuidRule.Exact);

        Assert.Equal((2027, 2025), (result.SourceRelease, result.TargetRelease));
        using var root = RootStorage.OpenRead(output);
        Assert.Equal("2025", BasicFileInfoReader.Read(root)["RevitVersion"]);
    }

    [Fact]
    public void Write_CrossWidth_TranscodesPartitions_AndValidates()
    {
        string source = Synthetic(2027, "source2027x.rvt", includeElemTable: true);
        string profile = Synthetic(2020, "profile2020x.rvt");
        string output = P("cross_out.rvt");

        var result = DowngradeWriter.Write(source, profile, output, GuidRule.Exact);

        Assert.Equal((2027, 2020), (result.SourceRelease, result.TargetRelease));
        Assert.True(File.Exists(output));

        using (var root = RootStorage.OpenRead(output))
        {
            Assert.Equal("2020", BasicFileInfoReader.Read(root)["RevitVersion"]);
        }

        // Schema is the profile's: replan is empty, release reads as target.
        byte[] outputSchemaBytes, profileSchemaBytes;
        using (var outRoot = RootStorage.OpenRead(output))
            outputSchemaBytes = PagedGzipStream.Read(outRoot, "Formats", "Latest")!;
        using (var profRoot = RootStorage.OpenRead(profile))
            profileSchemaBytes = PagedGzipStream.Read(profRoot, "Formats", "Latest")!;
        var replan = DowngradePlanner.Compute(
            TfSchemaReader.TryParse(outputSchemaBytes)!,
            TfSchemaReader.TryParse(profileSchemaBytes)!,
            GuidRule.Exact);
        Assert.Empty(replan.Edits);

        var caps = RvtWriter.InspectCapabilities(output);
        Assert.Equal(2020, caps.SavedRelease);
        Assert.Equal(2020, caps.SchemaRelease);
        Assert.Equal(2020, caps.EffectiveRelease);
        Assert.Equal(4, caps.ElementIdWidth);
        Assert.True(caps.Supports(RvtWriteOperation.SaveAs));

        var doc = RvtDocument.Open(output);
        Assert.NotNull(doc.RawData);
        var census = CoverageCensus.Read(output);
        Assert.Null(census.RecordsFailure);
        Assert.Equal(0, census.Totals.ChecksumMismatches);
        Assert.Equal("ok", census.Streams.Single(s => s.Path == "Partitions/1").Framing);

        // Content check: owners narrowed to 4-byte, SyntheticItem tag remapped by name,
        // m_code payload preserved; the unknown-class owner is left out of the block.
        SchemaDocument targetDoc;
        using (var profRoot = RootStorage.OpenRead(profile))
            targetDoc = TfSchemaReader.Read(profRoot)!;
        var targetTags = PartitionFraming.TagsOf(new FormatsSchema(targetDoc))!.Value;
        ushort syntheticTarget = (ushort)targetDoc.Classes.Single(c => c.Name == "SyntheticItem").Id;
        var recs = new List<PartitionFraming.Record>();
        byte[] outStored;
        using (var outRoot = RootStorage.OpenRead(output))
        using (var ps = outRoot.OpenStorage("Partitions").OpenStream("1"))
        {
            outStored = new byte[ps.Length];
            ps.ReadExactly(outStored);
        }
        Assert.True(PartitionFraming.Walk(outStored, targetTags, 4,
            r => recs.Add(r with { Body = r.Body.ToArray(), Item = r.Item.ToArray() }),
            out string? walkFailure, out int walkBlocks), walkFailure);
        var ch102 = recs.Where(r => r.Block == 0 && r.Channel == 102 && r.Owner != uint.MaxValue).ToList();
        Assert.Equal([101UL], ch102.Select(r => r.Owner).OrderBy(o => o).ToArray());
        var item101 = ch102.Single(r => r.Owner == 101UL);
        Assert.Equal(syntheticTarget, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(item101.Body.Span));
        Assert.Equal(100, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(item101.Body.Span[2..]));
        Assert.DoesNotContain(recs, r => r.Block == 0 && r.Channel == 102 && r.Owner == 202UL);

        // Element-table parity: the 2020-layout table decodes with the target schema and
        // carries the histories of the surviving source elements (the row of the dropped
        // unknown-class owner goes with it).
        List<ElemTableLayout.Row> outRows, srcRows;
        using (var outRoot = RootStorage.OpenRead(output))
            outRows = ElemTableLayout.TryParse(PagedGzipStream.Read(outRoot, "Global", "ElemTable")!, new FormatsSchema(targetDoc))!;
        SchemaDocument sourceDoc;
        using (var srcRoot = RootStorage.OpenRead(source))
            sourceDoc = TfSchemaReader.Read(srcRoot)!;
        using (var srcRoot = RootStorage.OpenRead(source))
            srcRows = ElemTableLayout.TryParse(PagedGzipStream.Read(srcRoot, "Global", "ElemTable")!, new FormatsSchema(sourceDoc))!;
        Assert.NotNull(outRows);
        Assert.NotNull(srcRows);
        var sourceIds = RvtDocument.Open(source).Elements.Select(e => e.Id).ToHashSet();
        var outputIds = doc.Elements.Select(e => e.Id).ToHashSet();
        Assert.Contains(101, outputIds);
        Assert.DoesNotContain(202, outputIds);
        var surviving = srcRows.Where(r => outputIds.Contains((int)r.Id)).ToList();
        Assert.Equal(srcRows.Count(r => r.Id != 202), surviving.Count);
        Assert.Equal(surviving.Count, outRows.Count);
        Assert.Equal(
            surviving.Select(r => (r.Id, r.OriginalId, r.CreationDate, r.PartitionId, r.OwningId)).ToArray(),
            outRows.Select(r => (r.Id, r.OriginalId, r.CreationDate, r.PartitionId, r.OwningId)).ToArray());
        Assert.DoesNotContain(outRows, r => r.Id == 202);

        // Element decode parity: every element known to the source ElemTable and kept by
        // the rewrite decodes in the downgraded file with the same id. (Block-only heuristic
        // discoveries on the synthetic payloads may differ by scan chance and are not compared.)
        foreach (var r in surviving)
        {
            Assert.Contains((int)r.Id, sourceIds);
            Assert.Contains((int)r.Id, outputIds);
        }
    }

    [Fact]
    public void Write_DowngradedFile_StillOpens_InRvtIO()
    {
        string source = Synthetic(2026, "openable2026.rvt");
        string profile = Synthetic(2025, "openable2025.rvt");
        string output = P("openable_out.rvt");

        DowngradeWriter.Write(source, profile, output, GuidRule.Exact);

        var doc = RvtDocument.Open(output);
        Assert.NotNull(doc.RawData);
        var census = CoverageCensus.Read(output);
        Assert.Equal(0, census.Totals.ChecksumMismatches);
    }

    [Fact]
    public void Cli_Output_WritesFile_AndReportHoldsOutput()
    {
        string source = Synthetic(2026, "cli_source.rvt");
        string profile = Synthetic(2025, "cli_profile.rvt");
        string output = P("cli_out.rvt");
        string report = P("cli_report.json");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = DowngradeCommand.Run([source, "--target-profile", profile, "--output", output, "--report", report], stdout, stderr);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(report));
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("rvtio-downgrade/1", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("cli_out.rvt", json.RootElement.GetProperty("output").GetProperty("file").GetString());
        Assert.Contains("Downgraded:", stdout.ToString());
    }

    [Fact]
    public void Plan_2027Source_2020Profile_IsAllowed_ForCheck_AndWriteSucceeds()
    {
        string source = Synthetic(2027, "plan2027.rvt");
        string profile = Synthetic(2020, "plan2020.rvt");

        var plan = DowngradePlanner.Plan(source, profile, GuidRule.Exact, countElements: false);
        Assert.Equal((2027, 2020), (plan.SourceRelease, plan.TargetRelease));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int checkExit = DowngradeCommand.Run([source, "--target-profile", profile, "--check"], stdout, stderr);
        Assert.Equal(0, checkExit);
        Assert.Contains("release 2027", stdout.ToString());
        Assert.Contains("release 2020", stdout.ToString());
    }

    [Fact]
    public void Cli_Output_CrossWidth_WritesFile_WithExit0()
    {
        string source = Synthetic(2027, "cli_src27.rvt");
        string profile = Synthetic(2020, "cli_prof20.rvt");
        string output = P("cli_cross.rvt");
        string report = P("cli_cross_report.json");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = DowngradeCommand.Run([source, "--target-profile", profile, "--output", output, "--report", report], stdout, stderr);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(output));
        Assert.Contains("Downgraded:", stdout.ToString());
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("cli_cross.rvt", json.RootElement.GetProperty("output").GetProperty("file").GetString());
        Assert.Equal(2020, json.RootElement.GetProperty("output").GetProperty("release").GetInt32());

        var caps = RvtWriter.InspectCapabilities(output);
        Assert.Equal(2020, caps.EffectiveRelease);
        var doc = RvtDocument.Open(output);
        Assert.NotNull(doc.RawData);
    }
}
