using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using static RvtIO.Tests.CoverageCensusTests;

namespace RvtIO.Tests;

/// <summary>How the reader cuts a partition stream into chunks (<see cref="RvtFileReader.CutPartition"/>): by its segments when the framing
/// holds, else by a search for gzip members.</summary>
public class PartitionCutTests
{
    private static (PartitionFraming.Tags Tags, int IdWidth) Framing => (PartitionFraming.TagsOf(new FormatsSchema(Schema))!.Value, 4);

    private static byte[] TwoBlocks() => Partition(Block(false, R(1, Body("Plain", 5, 0, 0, 0))), Block(true, R(2, Body("Plain", 5, 0, 0, 0))));

    [Fact]
    public void AStreamWhoseFramingHolds_IsCutIntoItsSegments()
    {
        byte[] raw = TwoBlocks();

        var chunks = RvtFileReader.CutPartition(raw, Framing, new ChunkDecompressor(), out var stats, out string? failure, out var framed);

        Assert.Null(failure);
        Assert.Equal((6, 6, 0, 0, 0), stats);
        Assert.Equal(new ChunkDecompressor().DecompressAll(raw), chunks);
        // the records of the project's block, one per channel, each item at its place in the inflated stream; the family's block is not listed
        Assert.NotNull(framed);
        Assert.Same(chunks, framed!.Segments);
        Assert.Equal([101, 102, 103], framed.Items.Select(i => i.Channel));
        Assert.All(framed.Items, i => Assert.Equal(1ul, i.Owner));
        byte[] inflated = [.. chunks.SelectMany(c => c)];
        Assert.All(framed.Items, i => Assert.Equal(1u, BitConverter.ToUInt32(inflated, (int)i.Offset)));
        // the item ends with the size of its body: owner, the checksum on channels 102 and 103, size and closing size around it
        Assert.All(framed.Items, i => Assert.Equal((uint)(i.Length - (i.Channel == 101 ? 12 : 16)), BitConverter.ToUInt32(inflated, (int)i.Offset + i.Length - 4)));
    }

    [Fact]
    public void AStreamWhoseFramingDoesNotHold_IsSearchedForGzipMembers()
    {
        byte[] raw = TwoBlocks();
        raw[^10] ^= 0xFF;   // the class tag of the terminal marker: the framing does not end, the members are intact

        var search = new ChunkDecompressor();
        var chunks = RvtFileReader.CutPartition(raw, Framing, search, out var stats, out string? failure, out var framed);

        Assert.NotNull(failure);
        Assert.Null(framed);
        Assert.Equal(search.LastStats, stats);
        Assert.Equal(new ChunkDecompressor().DecompressAll(raw), chunks);
        // without the framing classes the stream is searched as well, and the reason says so
        RvtFileReader.CutPartition(TwoBlocks(), null, new ChunkDecompressor(), out _, out failure, out _);
        Assert.Equal("the class table names no framing classes or the id width is not known", failure);
    }

    [Fact]
    public void TheSegmentsOfAChain_AreTheOutputOfEachOfItsMembers()
    {
        // one block whose channel 101 is a chain of two segments (the first continues into the second), channels 102 and 103 one segment each
        // each channel closes with the chain terminator, owner all ones and no body, which the framing does not list as a record of the block
        byte[] first = Records(101, R(1, Body("Plain", 5, 0, 0, 0))), second = Records(101, R(2, Body("Plain", 5, 0, 0, 0)), R(uint.MaxValue, []));
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(9ul);
            w.Write(Id("ContentMarker")); w.Write(0u); w.Write(2u);
            Segment(w, 101, 2u, first, 1);
            Segment(w, 101, 1u, second, 2);
            Segment(w, 102, 0u, Records(102, R(1, Body("Plain", 5, 0, 0, 0)), R(2, Body("Plain", 5, 0, 0, 0)), R(uint.MaxValue, [])), 3);
            Segment(w, 103, 0u, Records(103, R(1, Body("Plain", 5, 0, 0, 0)), R(2, Body("Plain", 5, 0, 0, 0)), R(uint.MaxValue, [])), 3);
            w.Write(Id("SegmentMarker")); w.Write(new byte[16]);
            w.Write(Id("SignatureMarker")); w.Write(0u); w.Write(0u);
            w.Write(Id("ContentMarker")); w.Write(0u); w.Write(uint.MaxValue);
        }
        byte[] raw = stream.ToArray();

        var chunks = RvtFileReader.CutPartition(raw, Framing, new ChunkDecompressor(), out _, out string? failure, out var framed);

        Assert.Null(failure);
        Assert.Equal([(101, 1ul), (101, 2ul), (102, 1ul), (102, 2ul), (103, 1ul), (103, 2ul)], framed!.Items.Select(i => (i.Channel, i.Owner)));
        Assert.Equal(4, chunks.Count);
        Assert.Equal(first, chunks[0]);
        Assert.Equal(second, chunks[1]);
        Assert.Equal(new ChunkDecompressor().DecompressAll(raw), chunks);
    }

    // the items of records as a segment holds them inflated: owner, the checksum on channels 102 and 103, size, body, size
    private static byte[] Records(uint channel, params ElementCensusTests.Rec[] records)
    {
        using var items = new MemoryStream();
        using var item = new BinaryWriter(items);
        foreach (var record in records)
        {
            item.Write(checked((uint)record.Owner));
            if (channel != 101) item.Write(record.Extra);
            item.Write((uint)record.Body.Length);
            item.Write(record.Body);
            item.Write((uint)record.Body.Length);
        }
        item.Flush();
        return items.ToArray();
    }

    private static void Segment(BinaryWriter w, uint channel, uint flags, byte[] records, uint count)
    {
        byte[] payload = GzipMember.Write(records);
        uint size = (uint)payload.Length + 8;
        w.Write(Id("SegmentMarker"));
        w.Write(flags);
        w.Write(count);
        w.Write(size);
        w.Write((uint)(records.Length - count * (channel == 101 ? 12u : 16u)));
        w.Write(channel);
        w.Write(19u);
        w.Write(payload);
        w.Write(Id("SegmentCheckback"));
        w.Write(size);
    }
}
