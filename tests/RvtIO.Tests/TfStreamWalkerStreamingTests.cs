using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class TfStreamWalkerStreamingTests
{
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
    public void BackedWindowsPreserveAnchorsRecordsAndTargetedVisitOrder()
    {
        uint[] ids = Enumerable.Range(101, 24).Select(id => (uint)id).ToArray();
        byte[] bytes = Blocks(ids);
        // Cross the 16 MiB boundary with different targeted classes and keep their visit order.
        const int length = 800_016;
        for (int i = 0; i < ids.Length; i++)
            BitConverter.GetBytes((ushort)(i % 2 == 0 ? 2006 : 2007)).CopyTo(bytes, i * length + 12);
        byte[][] chunks = bytes.Chunk(189_911).ToArray();
        using var scratch = new MemoryStream();
        var backed = new PartitionChunks(new PartitionChunks.Backing(scratch));
        foreach (var chunk in chunks) backed.Add(chunk);

        Assert.Equal(Visits(chunks, false), Visits(backed, false));
        Assert.Equal(Visits(chunks, true), Visits(backed, true));
        Assert.Equal(ids.Length, Visits(backed, true).Count);

        List<(uint Owner, uint Size, ushort Class)> Visits(IReadOnlyList<byte[]> source, bool targeted)
        {
            var result = new List<(uint, uint, ushort)>();
            void Visit(byte[] data, int start, uint size, ushort kind, uint owner)
            {
                Assert.Equal(size, BitConverter.ToUInt32(data, start + 12 + (int)size));
                result.Add((owner, size, kind));
            }
            if (targeted) TfStreamWalker.ScanBlocks(source, ids.ToHashSet(), Visit);
            else TfStreamWalker.Walk(source, [], Visit);
            return result;
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
