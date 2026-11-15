using System.Buffers.Binary;
using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The viewport record (BlockParser.ExtractViewport): the offset of the view on the sheet and, as a fallback
/// for a view without a decoded scale, the reciprocal of the view scale.
/// </summary>
public class ViewportScaleTests
{
    private static byte[] ChunkWithViewport(uint eid, double x, double y, double z, int viewId, double reciprocal)
    {
        int payload = 160;
        var chunk = new byte[16 + payload + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, eid);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8), payload);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(12), 0x000007C6);
        // The reader takes the record to start two bytes before the block payload.
        int start = 16 - 2;
        void Double(int offset, double value) =>
            BinaryPrimitives.WriteDoubleLittleEndian(chunk.AsSpan(start + offset), value);
        Double(0x47, x);
        Double(0x4F, y);
        Double(0x57, z);
        Double(0x87, reciprocal);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(start + 0x8F), viewId);
        return chunk;
    }

    [Fact]
    public void ExtractViewport_ReadsTheOffsetAndTheScale()
    {
        var state = new ParsingState();
        var chunk = ChunkWithViewport(700, 1.5, 2.5, 0, 900, 0.05);

        // The block header is at 0; the reader is given its element id and data size.
        BlockParser.ParseBlock(chunk, 0, 700, 160, knownOwner: true, state, collectBlockOnlyIds: false,
            pendingAll: null);

        var placement = Assert.Contains(700u, state.ViewportPlacements);
        Assert.Equal(900, placement.ViewId);
        Assert.Equal(1.5, placement.X, 6);
        Assert.Equal(2.5, placement.Y, 6);
        Assert.Equal(20, Assert.Contains(700u, state.ViewportScales));
    }
}
