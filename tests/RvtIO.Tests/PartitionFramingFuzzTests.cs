using OpenMcdf;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Truncated and mutated partition streams are rejected with a reason: no exception escapes, no run takes long.</summary>
public class PartitionFramingFuzzTests
{
    [Fact]
    public void Walk_ReadsGzipMembersAcrossPageTailsAndRejectsCorruptTails()
    {
        var id = CoverageCensusTests.Id;
        var tags = new PartitionFraming.Tags(id("ContentMarker"), id("ContentKey"), id("SegmentMarker"),
            id("SegmentCheckback"), id("SignatureMarker"));
        var body = new byte[2 * PagedGzipStream.PageDataLength];
        new Random(42).NextBytes(body);
        byte[] payload = CoverageCensusTests.Partition(CoverageCensusTests.Block(false, CoverageCensusTests.R(1001, body)));
        using var paged = new MemoryStream();
        for (int offset = 0; offset < payload.Length;)
        {
            int count = Math.Min(PagedGzipStream.PageDataLength, payload.Length - offset);
            paged.Write(payload, offset, count);
            offset += count;
            if (offset < payload.Length) paged.Write(new byte[PagedGzipStream.SegmentLength]);
        }

        byte[] raw = paged.ToArray();
        var channels = new List<int>();
        Assert.True(PartitionFraming.Walk(raw, tags, 4, record =>
        {
            Assert.True(body.AsSpan().SequenceEqual(record.Body.Span));
            channels.Add(record.Channel);
        }, out var failure, out _), failure);
        Assert.Equal(new[] { 101, 102, 103 }, channels);

        raw[PagedGzipStream.PageDataLength] = 1;
        Assert.False(PartitionFraming.Walk(raw, tags, 4, _ => { }, out failure, out _));
        Assert.Equal("page-framing", failure);
    }


    [SkippableFact]
    public void MutatedAndTruncatedStreamsAreRejectedWithoutAnException()
    {
        string path = TestFiles.Find("TESTFILE.rvt");
        using var root = RootStorage.OpenRead(path);
        var schema = FormatsSchema.TryRead(root)!;
        var storage = root.OpenStorage("Partitions");
        var random = new Random(12345);
        foreach (string name in new[] { "1", "3" })
        {
            using var stream = storage.OpenStream(name);
            var raw = new byte[stream.Length];
            stream.ReadExactly(raw);
            for (int i = 0; i < 400; i++)
            {
                var data = (byte[])raw.Clone();
                switch (i % 4)
                {
                    case 0: data = data[..random.Next(0, data.Length)]; break;
                    case 1: for (int k = 0; k <= random.Next(4); k++) data[random.Next(data.Length)] ^= (byte)(1 + random.Next(255)); break;
                    case 2: BitConverter.GetBytes(random.Next()).CopyTo(data, random.Next(0, Math.Min(data.Length - 4, 2000))); break;
                    default: BitConverter.GetBytes(uint.MaxValue).CopyTo(data, random.Next(0, data.Length - 4)); break;
                }
                var census = RecordRouting.Begin(schema, out _)!;
                long allocBefore = GC.GetAllocatedBytesForCurrentThread();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool accepted = RecordRouting.Collect(census, 1, data, out string? failure);
                long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
                Assert.True(watch.ElapsedMilliseconds < 5000);
                Assert.True(allocDelta < 64L * 1024 * 1024, $"Iteration {i} allocated {allocDelta} bytes.");
                Assert.True(accepted || failure != null);
            }
        }
    }

    [Fact]
    public void SyntheticPartitionStreams_MutatedAndTruncatedAreRejectedWithinTimeAndMemoryBounds()
    {
        var writer = new SchemaStreamEditorTests.Writer();
        writer.Top(new SchemaStreamEditorTests.Cls("Identifier", new SchemaStreamEditorTests.NoRef(), 1, [new SchemaStreamEditorTests.Fld("m_id", 4)], []));
        for (int id = 13; id < 816; id++) writer.Top(new SchemaStreamEditorTests.Cls("Filler" + id, new SchemaStreamEditorTests.NoRef(), 1, [], []));
        foreach (string name in new[] { "ContentKey", "ContentMarker", "SegmentMarker", "SegmentCheckback", "SignatureMarker" })
            writer.Top(new SchemaStreamEditorTests.Cls(name, new SchemaStreamEditorTests.NoRef(), 1, [], []));
        var schema = FormatsSchema.TryParse(writer.Build())!;
        Assert.NotNull(RecordRouting.Begin(schema, out _));

        byte[] raw = CoverageCensusTests.Partition(
            CoverageCensusTests.Block(false,
                CoverageCensusTests.R(1001, CoverageCensusTests.Body("Plain", 5, 0, 0, 0)),
                CoverageCensusTests.R(1002, CoverageCensusTests.Body("Listed", 3, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0))),
            CoverageCensusTests.Block(true,
                CoverageCensusTests.R(2001, CoverageCensusTests.Body("Store", 9, 0, 0, 0)),
                CoverageCensusTests.R(2002, CoverageCensusTests.Body("Empty"))));

        var random = new Random(20260115);
        var totalWatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 600; i++)
        {
            byte[] data = (byte[])raw.Clone();
            switch (i % 5)
            {
                case 0:
                    data = data[..random.Next(0, data.Length + 1)];
                    break;
                case 1:
                    for (int k = 0; k <= random.Next(6); k++)
                        data[random.Next(data.Length)] ^= (byte)(1 + random.Next(255));
                    break;
                case 2:
                    if (data.Length >= 4)
                        BitConverter.GetBytes(random.Next()).CopyTo(data, random.Next(0, data.Length - 3));
                    break;
                case 3:
                    if (data.Length >= 4)
                        BitConverter.GetBytes(uint.MaxValue).CopyTo(data, random.Next(0, data.Length - 3));
                    break;
                default:
                    int zeroStart = random.Next(0, data.Length);
                    int zeroLen = random.Next(0, data.Length - zeroStart + 1);
                    Array.Clear(data, zeroStart, zeroLen);
                    break;
            }

            var census = RecordRouting.Begin(schema, out _)!;
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool accepted = RecordRouting.Collect(census, 1, data, out string? failure);
            long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Assert.True(watch.ElapsedMilliseconds < 2000, $"Iteration {i} took {watch.ElapsedMilliseconds} ms.");
            Assert.True(allocDelta < 32L * 1024 * 1024, $"Iteration {i} allocated {allocDelta} bytes.");
            Assert.True(accepted || failure != null);
        }
        Assert.True(totalWatch.ElapsedMilliseconds < 15_000, $"Total fuzz run took {totalWatch.ElapsedMilliseconds} ms.");
    }
}
