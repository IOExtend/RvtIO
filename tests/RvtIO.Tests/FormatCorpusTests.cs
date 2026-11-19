using System.Security.Cryptography;
using System.Text.Json;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class FormatCorpusTests(ITestOutputHelper output)
{
    private sealed record Sample(string Path, string Sha256, int PartitionStreams, int PagedStreams,
        long Records, long PayloadBytes, int ElemTableRows);

    [SkippableFact]
    public void EachFileMatchesItsStructuralBaseline()
    {
        string? configured = Environment.GetEnvironmentVariable("RVTIO_FORMAT_CORPUS");
        if (string.IsNullOrWhiteSpace(configured))
            throw new SkipException("Set RVTIO_FORMAT_CORPUS to the format baseline JSON file.");

        string manifest = Path.GetFullPath(configured);
        var samples = JsonSerializer.Deserialize<Sample[]>(File.ReadAllText(manifest),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(samples);
        Assert.NotEmpty(samples);

        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            string path = Path.GetFullPath(sample.Path, Path.GetDirectoryName(manifest)!);
            output.WriteLine(path);
            Assert.True(paths.Add(path), $"Duplicate sample path: {path}");
            using (var input = File.OpenRead(path))
                Assert.Equal(sample.Sha256.ToUpperInvariant(), Convert.ToHexString(SHA256.HashData(input)));

            var census = CoverageCensus.Read(path);
            Assert.Null(census.RecordsFailure);
            Assert.True(sample.PartitionStreams > 0 && sample.PagedStreams > 0 && sample.Records > 0 && sample.ElemTableRows > 0);
            var partitions = census.Streams.Where(stream => stream.Kind == "partition").ToList();
            var paged = census.Streams.Where(stream => stream.Kind == "paged").ToList();
            Assert.Equal(sample.PartitionStreams, partitions.Count);
            Assert.Equal(sample.PagedStreams, paged.Count);
            Assert.All(partitions, stream => Assert.True(stream.Framing == "ok", $"{stream.Path}: {stream.Framing}"));
            Assert.All(paged, stream => Assert.True(stream.Decoded == true, $"{stream.Path}: failed to inflate"));
            Assert.Equal(sample.Records, census.Totals.Items);
            Assert.Equal(sample.Records, census.Totals.Exact);
            Assert.Equal(sample.PayloadBytes, census.Totals.PayloadBytes);
            Assert.Equal(sample.PayloadBytes, census.Totals.ExactBytes);
            Assert.Equal(0, census.Totals.ChecksumMismatches);

            using var root = RootStorage.OpenRead(path);
            var table = ElemTableReader.Read(root, out _, out _, out bool exact);
            Assert.True(exact, "The production element table reader did not read the table exactly.");
            Assert.Equal(sample.ElemTableRows, table.Count);
        }
    }
}
