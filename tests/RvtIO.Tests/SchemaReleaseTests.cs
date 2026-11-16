using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The release of the file's own schema, against the release saved in BasicFileInfo.</summary>
public class SchemaReleaseTests
{
    [Theory]
    [InlineData(817, 4307, 2022)]     // both agree
    [InlineData(null, 4307, 2022)]    // class count only
    [InlineData(817, 123, 2022)]      // marker only
    [InlineData(891, 4307, null)]     // the two name different releases: unknown
    [InlineData(null, 123, null)]     // neither is known
    [InlineData(null, 4757, 2027)]
    public void EvidenceMustAgreeOrTheReleaseIsUnknown(int? marker, int classCount, int? expected)
        => Assert.Equal(expected, SchemaRelease.FromEvidence(marker, classCount));

    [Theory]
    [InlineData(2025, 2022, 2022)]    // saved by a newer release, schema of an older one
    [InlineData(2022, 2025, 2025)]    // an older saving year cannot change a known schema's layout
    [InlineData(2024, 2024, 2024)]
    [InlineData(2024, null, 2024)]
    [InlineData(null, 2022, 2022)]
    [InlineData(null, null, null)]
    public void TheSchemaReleaseDecidesTheLayoutWithTheSavingReleaseAsFallback(int? saved, int? schema, int? expected)
        => Assert.Equal(expected, SchemaRelease.Effective(saved, schema));

    [Theory]
    [InlineData("2025", 2025)]
    [InlineData("25", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheSavedReleaseIsAFourDigitYear(string? text, int? expected)
        => Assert.Equal(expected, SchemaRelease.Saved(text));

    [Fact]
    public void TheDiagnosticsLineNamesBothReleases()
    {
        Assert.Equal("Release: saved by 2025, schema of 2022, the layout of release 2022 is used", SchemaRelease.Describe(2025, 2022));
        Assert.Equal("Release: saved by 2022, schema of 2025, the layout of release 2025 is used", SchemaRelease.Describe(2022, 2025));
        Assert.Equal("Release: saved by 2024, schema of 2024", SchemaRelease.Describe(2024, 2024));
        Assert.Equal("Release: saved by unknown, schema of unknown", SchemaRelease.Describe(null, null));
    }

    /// <summary>Public Autodesk and own sample files: the schema release equals the saved release and the element table rows are
    /// the size the file's schema declares (28 bytes up to 2023, 40 from 2024).</summary>
    [SkippableTheory]
    [InlineData("Test_2020.rvt", 2020, 28)]
    [InlineData("Test_2022.rvt", 2022, 28)]
    [InlineData("Test_2025.rvt", 2025, 40)]
    public void SampleFilesAgreeAndDecodeWithTheirOwnRowSize(string name, int release, int rowLength)
    {
        string path = TestFiles.Find(name);
        using var root = RootStorage.OpenRead(path);
        var schema = FormatsSchema.TryRead(root)!;
        Assert.Equal(release, SchemaRelease.FromSchema(schema));
        Assert.Equal(release, SchemaRelease.Saved(BasicFileInfoReader.Read(root).GetValueOrDefault("RevitVersion")));
        Assert.Equal(rowLength, ElemTableLayout.RowLength(schema));
        Assert.NotNull(ElemTableLayout.TryParse(PagedGzipStream.Read(root, "Global", "ElemTable")!, schema));
    }

    /// <summary>Change only the labelled saving year. The class schema, build, paths and all model streams stay untouched.</summary>
    internal static string CopyWithSavedRelease(string sample, int savedRelease)
    {
        string copy = Path.Combine(Path.GetTempPath(), $"schema-release-{Guid.NewGuid():N}.rvt");
        File.Copy(sample, copy);
        using (var root = RootStorage.Open(copy, FileMode.Open))
        {
            var info = BasicFileInfoReader.Read(root);
            using var stream = root.OpenStream("BasicFileInfo");
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            bool replaced = false;
            foreach (string separator in new[] { ": ", ":\t" })
            {
                var label = System.Text.Encoding.Unicode.GetBytes("Format" + separator);
                int at = data.AsSpan().IndexOf(label);
                if (at < 0) continue;
                Assert.Equal(info["RevitVersion"], System.Text.Encoding.Unicode.GetString(data, at + label.Length, 8));
                System.Text.Encoding.Unicode.GetBytes(savedRelease.ToString()).CopyTo(data, at + label.Length);
                replaced = true;
                break;
            }
            Assert.True(replaced, "The sample must have a labelled Format year in BasicFileInfo.");
            stream.Position = 0;
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }
        return copy;
    }

    [Theory]
    [InlineData(false, 817, 2025, 2022)]
    [InlineData(true, 891, 2022, 2024)]
    [InlineData(true, 913, 2020, 2025)]
    [InlineData(false, 817, 2020, 2022)]
    [InlineData(false, 817, 2022, 2022)]
    [InlineData(true, 891, 2024, 2024)]
    [InlineData(true, 500, 2024, 2024)] // unknown schema release: use the saving release
    [InlineData(true, 891, null, 2024)] // no saving year: use the schema
    public void SyntheticFilesKeepTheDeclaredTableAndRoutingWidths(bool wide, int marker, int? saved, int expected)
    {
        string path = TestFiles.Scratch($"schema-layout-{Guid.NewGuid():N}.rvt");
        try
        {
            WriteSyntheticFile(path, wide, marker, saved);
            using (var root = RootStorage.OpenRead(path))
            {
                var formats = FormatsSchema.TryRead(root)!;
                var schema = FileSchema.Read(root)!;
                Assert.Equal(expected, formats.Release);
                Assert.Equal(expected, schema.Release);
                Assert.Equal(wide ? 40 : 28, ElemTableLayout.RowLength(formats));
                Assert.Equal(wide ? 8 : 4, ElemTableLayout.IdWidth(formats));
                Assert.Equal(wide ? 8 : 4, WideElementRecords.IdWidth(schema));
                Assert.Equal(wide ? 8 : 4, RecordRouting.Begin(formats, out _)!.IdWidth);
                var rows = ElemTableLayout.TryParse(PagedGzipStream.Read(root, "Global", "ElemTable")!, formats)!;
                Assert.Equal(new ElemTableLayout.Row(700, 500, 64, 7, 1, 65), rows[1]);
                var elements = ElemTableReader.Read(root, out var worksets, out var dates, out bool exact);
                Assert.True(exact);
                Assert.Equal(new uint[] { 0, 700 }, elements.Keys.Order().ToArray());
                Assert.Equal(7u, worksets[700]);
                Assert.Equal(64, dates[700]);
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, 891, 2022)] // known wide schema release, but narrow declared fields
    [InlineData(true, 817, 2024)]  // known narrow schema release, but wide declared fields
    [InlineData(true, 500, 2022)]  // unknown schema release: declared fields contradict the saving-year fallback
    public void ContradictoryDeclaredWidthsRemainUndecoded(bool wide, int marker, int saved)
    {
        string path = TestFiles.Scratch($"schema-layout-{Guid.NewGuid():N}.rvt");
        try
        {
            WriteSyntheticFile(path, wide, marker, saved);
            using var root = RootStorage.OpenRead(path);
            var formats = FormatsSchema.TryRead(root)!;
            Assert.True(ElemTableLayout.DisagreesWithRelease(formats));
            Assert.Null(ElemTableLayout.IdWidth(formats));
            Assert.Null(ElemTableLayout.RowLength(formats));
            Assert.Null(WideElementRecords.IdWidth(FileSchema.Read(root)!));
            Assert.Null(RecordRouting.Begin(formats, out _));
            Assert.Empty(ElemTableReader.Read(root, out _, out _, out bool exact));
            Assert.False(exact);
        }
        finally { File.Delete(path); }
    }

    private static void WriteSyntheticFile(string path, bool wide, int marker, int? saved)
    {
        using var root = RootStorage.Create(path, OpenMcdf.Version.V3);
        using var formats = root.CreateStorage("Formats").CreateStream("Latest");
        formats.Write(PageCodec.EncodePadded(GzipMember.Write(FormatsSchemaTests.ElemTableSchema(wide, marker))));
        using var table = root.CreateStorage("Global").CreateStream("ElemTable");
        table.Write(PageCodec.EncodePadded(GzipMember.Write(FormatsSchemaTests.Rows(wide,
            (0, 0, 0, 0, -1), (700, 500, 64, 7, 1)))));
        using var info = root.CreateStream("BasicFileInfo");
        info.Write(RvtWriterTests.BuildBasicFileInfoBytes(saved));
    }

    /// <summary>Every layout choice keeps the schema's layout when the saving release is changed in either direction.</summary>
    [SkippableTheory]
    [InlineData("Test_2025.rvt", 2025, 2025, true)]    // saved == schema: decoded as before
    [InlineData("Test_2025.rvt", 2022, 2025, true)]    // saved by an older release: retain the schema's 8-byte ids
    [InlineData("TESTFILE.rvt", 2025, 2022, true)]     // saved by a newer release: the schema's older layout is in force
    [InlineData("TESTFILE.rvt", 2020, 2022, true)]     // same 4-byte family
    public void TheReleaseInForceDecidesTheElementTableLayout(string name, int saved, int inForce, bool decodes)
    {
        string copy = CopyWithSavedRelease(TestFiles.Find(name), saved);
        try
        {
            using var root = RootStorage.OpenRead(copy);
            Assert.Equal(saved, SchemaRelease.Saved(BasicFileInfoReader.Read(root).GetValueOrDefault("RevitVersion")));
            var schema = FormatsSchema.TryRead(root)!;
            Assert.Equal(inForce, schema.Release);
            Assert.Equal(inForce, FileSchema.Read(root)!.Release);
            Assert.Equal(decodes, ElemTableLayout.RowLength(schema) is not null);
            Assert.Equal(decodes, ElemTableLayout.IdWidth(schema) is not null);
            Assert.Equal(decodes, ElemTableLayout.TryParse(PagedGzipStream.Read(root, "Global", "ElemTable")!, schema) is not null);
            Assert.Equal(decodes, WideElementRecords.IdWidth(FileSchema.Read(root)!) is not null);
            Assert.Equal(decodes, RecordRouting.Begin(schema, out _) is not null);
            var elements = ElemTableReader.Read(root, out _, out _, out bool exact);
            Assert.Equal(decodes, exact);
            if (decodes) Assert.NotEmpty(elements);
        }
        finally { File.Delete(copy); }
    }
}
