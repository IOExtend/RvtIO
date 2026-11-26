using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class TfStreamWalkerStreamingTests
{
    [Fact]
    public void TargetedScanKeepsClassPriorityAndStreamOrderAcrossChunks()
    {
        byte[] Chunk(params (uint Id, ushort Class, uint Size)[] specs)
        {
            var bytes = new byte[48 * specs.Length];
            for (int index = 0; index < specs.Length; index++)
            {
                var (id, cls, size) = specs[index];
                int start = index * 48;
                BitConverter.GetBytes(id).CopyTo(bytes, start);
                BitConverter.GetBytes(size).CopyTo(bytes, start + 8);
                BitConverter.GetBytes(cls).CopyTo(bytes, start + 12);
            }
            return bytes;
        }
        byte[] bytes = Chunk((10, 0x0346, 20), (11, 0x07D6, 20), (12, 0x07D7, 20),
            (13, 0x07D7, 20), (14, 0x07D7, 20), (15, 0x07D7, 19), (16, 0x0346, 100));
        var visited = new List<(uint, ushort)>();

        TfStreamWalker.ScanBlocks([bytes[..108], [], bytes[108..]], [10, 11, 12, 13, 15, 16],
            (_, _, _, cls, id) => visited.Add((id, cls)));

        Assert.Equal([(12u, (ushort)0x07D7), (13u, (ushort)0x07D7),
            (11u, (ushort)0x07D6), (10u, (ushort)0x0346)], visited);
    }

    [Fact]
    public void DeferredBlocksReuseContainingChunksAndCopyOnlyBytesAcrossBoundaries()
    {
        byte[] bytes = Blocks([101, 102]);
        int boundary = 1_000_000;
        byte[] first = bytes[..boundary], second = bytes[boundary..];
        var sources = new TfStreamWalker.SegmentSources();
        var retained = new List<(byte[] Data, int Start)>();

        TfStreamWalker.Walk([first, [], second], [], (segment, start, size, _, _) =>
            retained.Add(sources.Retain(segment, start, (int)size + 16)), sources: sources);

        Assert.Equal(2, retained.Count);
        Assert.Same(first, retained[0].Data);
        Assert.Equal(0, retained[0].Start);
        Assert.NotSame(first, retained[1].Data);
        Assert.NotSame(second, retained[1].Data);
        Assert.Equal(0, retained[1].Start);
        Assert.Equal(bytes[800_016..], retained[1].Data);

        sources.SetSegment([second], 0, 1, boundary);
        var carried = sources.Retain(bytes, boundary - 3, 6);
        Assert.Equal(bytes[(boundary - 3)..(boundary + 3)], carried.Data);
    }

    [Theory]
    [InlineData(3_700_000, 516_019)]
    [InlineData(12_000_000, 700_001)]
    public void BoundedWalkMatchesStitchedWalkAndRetainsDeferredBlocks(int segmentSize, int windowSize)
    {
        byte[] blocks = Blocks([0, 102, 103, 104, 102, 106, 107]);
        const int leading = 516_013, stride = 629_013;
        byte[] stream = new byte[leading + blocks.Length + 950_000];
        blocks.CopyTo(stream, leading);
        int record = leading + blocks.Length;
        BitConverter.GetBytes(123u).CopyTo(stream, record);
        BitConverter.GetBytes((ushort)0x053C).CopyTo(stream, record + 8);
        // A following record anchors the first record after a logical boundary.
        BitConverter.GetBytes(124u).CopyTo(stream, record + 12);
        BitConverter.GetBytes((ushort)0x053C).CopyTo(stream, record + 20);
        byte[][] chunks = [[], ..Enumerable.Range(0, (stream.Length + stride - 1) / stride)
            .Select(i => stream[(i * stride)..Math.Min((i + 1) * stride, stream.Length)]), []];

        var expected = new List<(uint Owner, byte[] Bytes)>();
        var actual = new List<(uint Owner, byte[] Data, int Start, int Length)>();
        var sources = new TfStreamWalker.SegmentSources();
        TfStreamWalker.Walk(chunks, [123, 124], (data, start, size, _, id) =>
            expected.Add((id, data.AsSpan(start, (int)size + 16).ToArray())),
            (data, start, id) => expected.Add((id, data.AsSpan(start, 12).ToArray())), segTarget: segmentSize);
        TfStreamWalker.WalkBounded(chunks, [123, 124], (data, start, size, _, id) =>
        {
            Assert.InRange(data.Length, 1, windowSize + 2_200_000);
            int length = (int)size + 16;
            var retained = sources.Retain(data, start, length);
            actual.Add((id, retained.Data, retained.Start, length));
        }, (data, start, id) =>
        {
            var retained = sources.Retain(data, start, 12);
            actual.Add((id, retained.Data, retained.Start, 12));
        }, sources, segmentSize, windowSize);

        Assert.Contains(expected, item => item.Owner == 123);
        Assert.Equal(expected.Select(item => item.Owner), actual.Select(item => item.Owner));
        for (int i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i].Bytes, actual[i].Data.AsSpan(actual[i].Start, actual[i].Length).ToArray());
    }

    [Theory]
    [InlineData(12_000_000)]
    [InlineData(9_500_000)]
    public void TargetedSlicesMatchStitchedScanAcrossPhysicalAndLogicalBoundaries(int segmentTarget)
    {
        const int chunkSize = 4_000_000, size = 32;
        byte[] bytes = new byte[chunkSize * 4];
        Write(28, 101, 0x0346);
        Write(chunkSize - 13, 102, 0x07D7); // the two marker bytes straddle a chunk
        Write(chunkSize + 45, 103, 0x07D6);
        Write(2 * chunkSize - 22, 104, 0x0346); // the header fits and the payload straddles
        Write(2 * chunkSize + 54, 102, 0x07D7);
        Write(3 * chunkSize - 7, 105, 0x07D7); // the size field straddles a chunk
        Write(3 * chunkSize + 57, 106, 0x0346);
        Write(bytes.Length - 17, 107, 0x07D7); // truncated at the end
        byte[][] chunks = [[], bytes[..chunkSize], [], bytes[chunkSize..(2 * chunkSize)],
            bytes[(2 * chunkSize)..(3 * chunkSize)], [], bytes[(3 * chunkSize)..], []];
        HashSet<uint> ids = [101, 102, 103, 104, 105, 106, 107];
        var expected = new List<(uint, ushort, string)>();
        var actual = new List<(uint, ushort, string)>();

        TfStreamWalker.ScanBlocks(chunks, ids, (data, start, count, cls, id) =>
            expected.Add((id, cls, Convert.ToHexString(data.AsSpan(start, (int)count + 16)))), segmentTarget: segmentTarget);
        TfStreamWalker.ScanBlockSlices(chunks, ids, (data, start, count, cls, id) =>
        {
            if (id == 101) Assert.Same(chunks[1], data);
            if (id is 104 or 105) Assert.Equal(size + 16, data.Length);
            actual.Add((id, cls, Convert.ToHexString(data.AsSpan(start, (int)count + 16))));
        }, segmentTarget);

        Assert.Equal(7, expected.Count);
        Assert.Equal(expected, actual);

        void Write(int offset, uint id, ushort cls)
        {
            BitConverter.GetBytes(id).CopyTo(bytes, offset);
            BitConverter.GetBytes((uint)size).CopyTo(bytes, offset + 8);
            BitConverter.GetBytes(cls).CopyTo(bytes, offset + 12);
        }
    }

    [Fact]
    public void OverlappingCrossChunkBlocksRetainTheirOwnBytesAfterTheScan()
    {
        byte[] bytes = new byte[768];
        (int Start, uint Owner, uint Size)[] blocks = [(32, 1, 240), (60, 2, 20), (97, 3, 300), (500, 4, 80)];
        foreach (var (start, owner, size) in blocks)
        {
            BitConverter.GetBytes(owner).CopyTo(bytes, start);
            BitConverter.GetBytes(size).CopyTo(bytes, start + 8);
            BitConverter.GetBytes((ushort)0x07D7).CopyTo(bytes, start + 12);
        }
        byte[][] chunks = bytes.Chunk(128).ToArray();
        var retained = new List<(byte[] Data, int Start)>();
        TfStreamWalker.ScanBlockSlices(chunks, [1, 2, 3, 4], (data, start, size, _, owner) =>
        {
            Assert.Equal((blocks[retained.Count].Owner, blocks[retained.Count].Size), (owner, size));
            retained.Add((data, start));
        });
        Assert.Equal(blocks.Length, retained.Count);
        Assert.Same(retained[0].Data, retained[2].Data);
        Assert.Same(chunks[0], retained[1].Data);
        for (int i = 0; i < blocks.Length; i++)
        {
            int length = (int)blocks[i].Size + 16;
            Assert.True(bytes.AsSpan(blocks[i].Start, length).SequenceEqual(retained[i].Data.AsSpan(retained[i].Start, length)));
        }
    }

    [Theory]
    [InlineData(97_003, 3_400_000)] [InlineData(300_011, 3_700_000)]
    [InlineData(4_000_000, 3_400_000)] [InlineData(400_001, 1_400_000_000)]
    public void ContinuousStreamRetainsCrossingBlocksAndDuplicates(int chunkSize, int segmentTarget)
    {
        uint[] ids = [101, 102, 101, 104, 105, 106, 107];
        byte[] bytes = Blocks(ids);
        var visits = new List<uint>();
        int enumerations = 0; bool disposed = false;
        TfStreamWalker.WalkStreaming(Chunks(), [], (segment, start, size, cls, id) =>
        {
            Assert.Equal(3524, cls);
            Assert.Equal(800_000u, size);
            Assert.Equal(size, BitConverter.ToUInt32(segment, start + 12 + (int)size));
            visits.Add(id);
        }, segmentTarget);
        Assert.Equal(ids, visits);
        Assert.Equal(1, enumerations);
        Assert.True(disposed);
        IEnumerable<byte[]> Chunks()
        {
            Assert.Equal(1, ++enumerations);
            try
            {
                for (int start = 0; start < bytes.Length; start += chunkSize)
                    yield return bytes[start..Math.Min(bytes.Length, start + chunkSize)];
            }
            finally { disposed = true; }
        }
    }

    [Fact]
    public void VisitorFailurePropagatesAndDisposesInput()
    {
        bool disposed = false;
        Assert.Throws<InvalidDataException>(() => TfStreamWalker.WalkStreaming(Chunks(), [],
            (_, _, _, _, _) => throw new InvalidDataException("test visitor")));
        Assert.True(disposed);
        IEnumerable<byte[]> Chunks()
        {
            try { yield return Blocks([101, 102]); }
            finally { disposed = true; }
        }
    }

    [Fact]
    public void EmptyAndEmptyChunkStreamsHaveNoVisits()
    {
        TfStreamWalker.WalkStreaming([], [], (_, _, _, _, _) => Assert.Fail("Unexpected visit"));
        TfStreamWalker.WalkStreaming([[], []], [], (_, _, _, _, _) => Assert.Fail("Unexpected visit"));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1_100_000)] [InlineData(3_300_000)]
    public void SegmentCannotBeSmallerThanRequiredOverlap(int target)
        => Assert.Throws<ArgumentOutOfRangeException>(() => TfStreamWalker.WalkStreaming([], [],
            (_, _, _, _, _) => { }, target));

    [Fact]
    public void BlockShapedBytesOfAKnownIdWithoutTheFooterEchoAreNotABlock()
    {
        // A header with a known id whose size spans the first real block exactly: the next item parses,
        // so only the missing footer echo tells it from a block.
        byte[] real = Blocks([102, 103]);
        byte[] bytes = new byte[16 + real.Length];
        BitConverter.GetBytes(101u).CopyTo(bytes, 0);
        BitConverter.GetBytes(real.Length / 2).CopyTo(bytes, 8);
        BitConverter.GetBytes((ushort)3524).CopyTo(bytes, 12);
        real.CopyTo(bytes, 16);
        var visits = new List<uint>();
        TfStreamWalker.WalkStreaming([bytes], [101], (_, _, _, _, id) => visits.Add(id));
        Assert.Equal([102u, 103u], visits);
    }

    [Fact]
    public void ZeroPaddingStillFindsAnAnchorWhoseOwnerIsZero()
    {
        const int padding = 100_003;
        byte[] blocks = Blocks([0, 102]);
        byte[] stream = new byte[padding + blocks.Length];
        blocks.CopyTo(stream, padding);
        var visits = new List<(int Offset, uint Owner)>();

        TfStreamWalker.Walk([stream], [], (_, offset, _, _, owner) => visits.Add((offset, owner)));

        Assert.Equal([(padding, 0u), (padding + 800_016, 102u)], visits);
    }

    private static byte[] Blocks(uint[] ids)
    {
        const int size = 800_000, length = size + 16;
        byte[] result = new byte[ids.Length * length];
        for (int index = 0; index < ids.Length; index++)
        {
            int start = index * length;
            BitConverter.GetBytes(ids[index]).CopyTo(result, start);
            BitConverter.GetBytes(size).CopyTo(result, start + 8);
            BitConverter.GetBytes((ushort)3524).CopyTo(result, start + 12);
            BitConverter.GetBytes(size).CopyTo(result, start + length - 4);
        }
        return result;
    }
}
