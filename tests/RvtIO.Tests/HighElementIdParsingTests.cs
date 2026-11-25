using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class HighElementIdParsingTests
{
    private const uint ElementId = 10_571_225;

    [Fact]
    public void RecordParser_AcceptsRepresentableElementIdAboveLegacyCeiling()
    {
        var state = new ParsingState();
        byte[] chunk = new byte[16];
        BitConverter.GetBytes(ElementId).CopyTo(chunk, 0);
        BitConverter.GetBytes(4u).CopyTo(chunk, 4);
        BitConverter.GetBytes((ushort)0x053C).CopyTo(chunk, 8);

        RecordParser.ParseChunk(chunk, state);

        Assert.True(state.Records.ContainsKey(ElementId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordParser_RetainsTheExactBodyInItsAlreadyStoredSegment(bool r25)
    {
        byte[] body = [0xAD, 0x14, 0x02, 0xF8];
        int start = r25 ? 16 : 12;
        byte[] chunk = new byte[start + body.Length + (r25 ? 4 : 0)];
        if (r25)
        {
            BitConverter.GetBytes(ElementId).CopyTo(chunk, 0);
            BitConverter.GetBytes(body.Length).CopyTo(chunk, 12);
            BitConverter.GetBytes(body.Length).CopyTo(chunk, start + body.Length);
        }
        else WriteRecord(chunk, 0, ElementId);
        body.CopyTo(chunk, start);

        using var scratch = new MemoryStream();
        var chunks = new PartitionChunks(new PartitionChunks.Backing(scratch));
        chunks.Add(chunk);
        var state = new ParsingState();
        state.AllKnownIds.Add(ElementId);

        if (r25) RecordParser.ParseChunkR25(chunk, state, chunks);
        else RecordParser.ParseChunk(chunk, state, chunks);

        var record = state.Records[ElementId];
        byte[] first = record.Body;
        Assert.Equal(body, first);
        Assert.NotSame(first, record.Body);
        Assert.Equal(chunk.Length, scratch.Length);
    }

    [Fact]
    public void RecordParser_AcceptsStructurallyAdjacentUnknownHighIds()
    {
        var state = new ParsingState();
        byte[] chunk = new byte[32];
        WriteRecord(chunk, 0, ElementId);
        WriteRecord(chunk, 16, ElementId + 1);

        RecordParser.ParseChunk(chunk, state);

        Assert.True(state.Records.ContainsKey(ElementId));
        Assert.True(state.Records.ContainsKey(ElementId + 1));
    }

    [Fact]
    public void RecordParser_RejectsUnanchoredMagicInsideArbitraryBytes()
    {
        var state = new ParsingState();
        byte[] chunk = new byte[40];
        WriteRecord(chunk, 7, ElementId);

        RecordParser.ParseChunk(chunk, state);

        Assert.Empty(state.Records);
    }

    [Fact]
    public void BlockParser_AcceptsKnownElementIdAboveLegacyCeiling()
    {
        var state = new ParsingState();
        state.AllKnownIds.Add(ElementId);
        byte[] chunk = new byte[24];
        BitConverter.GetBytes(ElementId).CopyTo(chunk, 0);
        BitConverter.GetBytes(8u).CopyTo(chunk, 8);
        BitConverter.GetBytes(1u).CopyTo(chunk, 12);
        BitConverter.GetBytes(8u).CopyTo(chunk, 20);

        Assert.True(BlockParser.ParseChunk(chunk, state));
        Assert.True(state.BlockClassInventory.TryGetValue(1, out var stats));
        Assert.Contains(ElementId, stats!.SampleElemIds);
    }

    private static void WriteRecord(byte[] chunk, int offset, uint elementId)
    {
        BitConverter.GetBytes(elementId).CopyTo(chunk, offset);
        BitConverter.GetBytes(4u).CopyTo(chunk, offset + 4);
        BitConverter.GetBytes((ushort)0x053C).CopyTo(chunk, offset + 8);
    }
}
