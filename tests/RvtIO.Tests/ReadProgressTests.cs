using System.Text.Json;
using IOScene;
using RvtIO.Compact;
using RvtIO.Model;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The progress of a read (<see cref="RvtReaderOptions.Progress"/>): the phases and their order on a synthetic project of the
/// Revit 2022 layout and on one of a wide layout, the phase of the rewrite into the Revit 2022 layout, the byte counts of the parameter
/// stream, an observer that throws, the second read of the compact export, and a read that does not change with progress on.</summary>
public class ReadProgressTests
{
    /// <summary>The phases in the order a read can report them; a read skips those it does not need.</summary>
    private static readonly string[] AllPhases =
    [
        "metadata", "partitions", "class-schema", "wide-model", "r22-layout", "element-records", "element-blocks", "parameter-stream",
        "recover-geometry", "element-properties", "build-model", "finalize-model", "complete",
    ];

    /// <summary>The phases of the synthetic project file of the Revit 2022 layout.</summary>
    internal static readonly string[] ProjectPhases = AllPhases.Where(phase => phase is not ("wide-model" or "r22-layout")).ToArray();

    /// <summary>An observer that keeps the updates it gets and can throw on one of them.</summary>
    internal sealed class Log : IProgress<RvtReadProgress>
    {
        private readonly Func<int, RvtReadProgress, Exception?>? _throwAt;

        public Log(Func<int, RvtReadProgress, Exception?>? throwAt = null) => _throwAt = throwAt;

        public List<RvtReadProgress> Updates { get; } = [];

        public void Report(RvtReadProgress value)
        {
            Updates.Add(value);
            if (_throwAt?.Invoke(Updates.Count - 1, value) is { } exception) throw exception;
        }
    }

    /// <summary>The updates arrive as one run per phase in the order of <paramref name="phases"/> (each phase once), the count of a phase
    /// does not decrease and ends at its total, and the last update is <c>complete</c>.</summary>
    internal static void AssertWellFormed(IReadOnlyList<RvtReadProgress> updates, IEnumerable<string> phases)
    {
        var runs = new List<string>();
        foreach (var update in updates)
            if (runs.Count == 0 || runs[^1] != update.Phase) runs.Add(update.Phase);
        Assert.Equal(phases, runs);
        Assert.Equal(new RvtReadProgress("complete", 1, 1), updates[^1]);
        foreach (var phase in updates.GroupBy(update => update.Phase))
        {
            var counts = phase.ToArray();
            Assert.All(counts, update => Assert.True(update.Total == 0 || update.Completed <= update.Total));
            Assert.Equal(counts.Select(update => update.Completed).Order(), counts.Select(update => update.Completed));
            if (counts[^1].Total > 0) Assert.Equal(counts[^1].Total, counts[^1].Completed);
        }
    }

    private static string WriteFile(Action<string> write)
    {
        string path = Path.Combine(Path.GetTempPath(), "rvtio-progress-" + Guid.NewGuid().ToString("N") + ".rvt");
        write(path);
        return path;
    }

    private static void Read(Action<string> write, RvtReaderOptions options)
    {
        string path = WriteFile(write);
        try { new RvtFileReader(options).Read(path); }
        finally { File.Delete(path); }
    }

    private static void WriteProject(string path) => CliExitCodeTests.WriteSyntheticRvt(path);

    [Fact]
    public void ProjectFile_ReportsEveryPhaseOnceInOrderAndTheStreamBytes()
    {
        var progress = new Log();
        Read(WriteProject, new RvtReaderOptions { Progress = progress });

        AssertWellFormed(progress.Updates, ProjectPhases);
        Assert.Equal([0L, 1L], progress.Updates.Where(update => update.Phase == "partitions").Select(update => update.Completed));
        // The bytes of the parameter stream: a marker first, then the walk from its start to its end.
        var stream = progress.Updates.Where(update => update.Phase == "parameter-stream").ToArray();
        Assert.Equal(new RvtReadProgress("parameter-stream"), stream[0]);
        Assert.True(stream.Length >= 3 && stream[^1].Total > 0 && stream[^1].Completed == stream[^1].Total);
        Assert.Equal(new RvtReadProgress("parameter-stream", 0, stream[^1].Total), stream[1]);
    }

    [Fact]
    public void WideFile_ReportsTheRewriteToTheRevit2022LayoutAsItsOwnPhase()
    {
        var progress = new Log();
        var reader = new RvtFileReader(new RvtReaderOptions { R22Layout = true, Progress = progress });
        string path = WriteFile(WideElementRecordsTests.WriteWideProject);
        try { reader.Read(path); }
        finally { File.Delete(path); }

        Assert.True(reader.LastParsingState!.R22View);
        AssertWellFormed(progress.Updates, AllPhases.Where(phase => phase != "wide-model"));
        var rewrite = progress.Updates.Where(update => update.Phase == "r22-layout").ToArray();
        Assert.True(rewrite[0].Total >= 1);
        Assert.Equal(Enumerable.Range(0, (int)rewrite[0].Total + 1).Select(index => (long)index), rewrite.Select(update => update.Completed));
        Assert.All(rewrite, update => Assert.Equal(rewrite[0].Total, update.Total));
    }

    [Fact]
    public void WideFile_ReportsTheModelItKeepsBeforeTheRewrite()
    {
        var progress = new Log();
        var reader = new RvtFileReader(new RvtReaderOptions { R22Layout = true, PreserveWideLayout = true, Progress = progress });
        string path = WriteFile(WideElementRecordsTests.WriteWideProject);
        try { reader.Read(path); }
        finally { File.Delete(path); }

        Assert.NotNull(reader.WideModel);
        AssertWellFormed(progress.Updates, AllPhases);
        Assert.Equal(new RvtReadProgress("wide-model"), Assert.Single(progress.Updates, update => update.Phase == "wide-model"));
    }

    [Fact]
    public void WideFile_ReadByItsOwnLayoutReportsNoRewrite()
    {
        var progress = new Log();
        Read(WideElementRecordsTests.WriteWideProject, new RvtReaderOptions { Progress = progress });

        Assert.DoesNotContain(progress.Updates, update => update.Phase is "r22-layout" or "wide-model");
        Assert.Equal(new RvtReadProgress("complete", 1, 1), progress.Updates[^1]);
    }

    [Fact]
    public void SceneReader_ReportsTheRewriteOfAWideFileThroughItsOwnOption()
    {
        var progress = new Log();
        string path = WriteFile(WideElementRecordsTests.WriteWideProject);
        try { RvtSceneReader.Read(path, new RvtSceneOptions { ModelKey = "wide", ReadProgress = progress }); }
        finally { File.Delete(path); }

        AssertWellFormed(progress.Updates, AllPhases.Where(phase => phase != "wide-model"));
    }

    [Fact]
    public void AnObserverThatThrows_EndsTheReadAtThatUpdateWithTheSameException()
    {
        var counting = new Log();
        Read(WriteProject, new RvtReaderOptions { Progress = counting });
        int updates = counting.Updates.Count;
        Assert.True(updates > ProjectPhases.Length);

        for (int stop = 0; stop < updates; stop++)
        {
            var thrown = new InvalidOperationException("stop at " + stop);
            int at = stop;
            var progress = new Log((index, _) => index == at ? thrown : null);
            string path = WriteFile(WriteProject);
            try
            {
                var caught = Assert.Throws<InvalidOperationException>(() => new RvtFileReader(new RvtReaderOptions { Progress = progress }).Read(path));
                Assert.Same(thrown, caught);
            }
            finally { File.Delete(path); }
            // Nothing is reported after the update that threw, and its phase is the one the unthrowing read reported at that index.
            Assert.Equal(stop + 1, progress.Updates.Count);
            Assert.Equal(counting.Updates[stop], progress.Updates[stop]);
        }
    }

    [Fact]
    public void CompactExport_ReportsTheSecondReadOfAWideFileAfterTheFirstOneCompleted()
    {
        foreach (bool wide in new[] { true, false })
        {
            var progress = new Log();
            string path = WriteFile(wide ? WideElementRecordsTests.WriteWideProject : WriteProject);
            string output = path + ".json";
            try { CompactJsonExporter.Export(path, output, new RvtReaderOptions { Progress = progress }); }
            finally { File.Delete(path); File.Delete(output); }

            var phases = progress.Updates.Select(update => update.Phase).ToArray();
            Assert.Equal(1, phases.Count(phase => phase == "complete"));
            if (wide)
            {
                Assert.Equal(["complete", "quantities"], phases[^2..]);
                Assert.Equal(new RvtReadProgress("quantities"), progress.Updates[^1]);
            }
            else
                Assert.DoesNotContain("quantities", phases);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProgressDoesNotChangeTheModelRead(bool wide)
    {
        string path = WriteFile(wide ? WideElementRecordsTests.WriteWideProject : WriteProject);
        try
        {
            var options = new JsonSerializerOptions { Converters = { new ParameterListConverter() } };
            string Model(IProgress<RvtReadProgress>? progress)
            {
                var document = RvtDocument.Open(path, new RvtReaderOptions { R22Layout = wide, PreserveWideLayout = wide, Progress = progress });
                Assert.NotEmpty(document.RawData.Elements);
                return JsonSerializer.Serialize(document.RawData, options);
            }
            Assert.Equal(Model(null), Model(new Log()));
        }
        finally { File.Delete(path); }
    }

    /// <summary>The scene of a real file is not empty, so this compares meshes, properties and levels, which the synthetic files have none of.</summary>
    [SkippableFact]
    public void Test2025_SceneIsTheSameWithProgressOn()
    {
        string path = TestFiles.Find("Test_2025.rvt");
        var progress = new Log();
        var baseline = RvtSceneReader.Read(path);
        var observed = RvtSceneReader.Read(path, new RvtSceneOptions { ReadProgress = progress });

        Assert.NotEmpty(baseline.Document.Elements);
        Assert.Equal(baseline.Diagnostics, observed.Diagnostics);
        Assert.Equal(SceneJson.Write(baseline.Document), SceneJson.Write(observed.Document));
        AssertWellFormed(progress.Updates, AllPhases.Where(phase => phase != "wide-model"));
    }
}
