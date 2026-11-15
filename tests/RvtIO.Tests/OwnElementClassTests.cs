using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class OwnElementClassTests
{
    [Theory]
    [InlineData(3708)] // Run
    [InlineData(3686)] // Landing
    [InlineData(3727)] // Support
    [InlineData(3739)] // Service symbol: class identity does not make it an IFC component
    [InlineData(157)]
    public void OwnElementPrefix_IsNotAFullSubtypeOrDeferredGeometryValidation(ushort cls)
    {
        byte[] bytes = Frame(cls);
        Assert.True(TfBlockDeserializer.TryExtractOwnElementClass(bytes, 0, Size(bytes), cls, out int owner));
        Assert.Equal(123, owner);
    }

    [Theory]
    [InlineData(2007)] // GElement, independent geometry frame
    [InlineData(0)]
    [InlineData(65000)]
    public void NonElementOrUnknownClass_Rejects(ushort cls)
        => Reject(Frame(cls), cls);

    [Theory]
    [InlineData(0)] // header owner
    [InlineData(8)] // size
    [InlineData(12)] // class tag
    [InlineData(46)] // project document reference
    [InlineData(50)] // named Element.m_id
    public void ConflictingFrameOrIdentity_Rejects(int offset)
    {
        byte[] bytes = Frame(3708);
        bytes[offset] ^= 1;
        Reject(bytes, 3708);
    }

    [Fact]
    public void WrongFooter_Rejects()
    {
        byte[] bytes = Frame(3708);
        bytes[^1] ^= 1;
        Reject(bytes, 3708);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x80000000u)]
    public void NonPositiveSignedHeaderOwner_Rejects(uint owner)
    {
        byte[] bytes = Frame(3708);
        BitConverter.GetBytes(owner).CopyTo(bytes, 0);
        Reject(bytes, 3708);
    }

    [Fact]
    public void EveryTruncatedFrame_Rejects()
    {
        byte[] bytes = Frame(3708);
        for (int end = 0; end < bytes.Length; end++)
        {
            Assert.False(TfBlockDeserializer.TryExtractOwnElementClass(bytes[..end], 0, Size(bytes), 3708, out int owner));
            Assert.Equal(-1, owner);
        }
    }

    [Fact]
    public void OversizedConstraintArray_Rejects()
    {
        byte[] bytes = Frame(3708);
        BitConverter.GetBytes(uint.MaxValue - 1).CopyTo(bytes, 38);
        Reject(bytes, 3708);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidStart_Rejects(int start)
    {
        byte[] bytes = Frame(3708);
        Assert.False(TfBlockDeserializer.TryExtractOwnElementClass(bytes, start, Size(bytes), 3708, out int owner));
        Assert.Equal(-1, owner);
    }

    [Fact]
    public void ValidSubspan_DoesNotRequireTheOuterBufferEnd()
    {
        byte[] bytes = Frame(3708);
        byte[] outer = [11, 12, 13, .. bytes, 99];
        Assert.True(TfBlockDeserializer.TryExtractOwnElementClass(outer, 3, Size(bytes), 3708, out int owner));
        Assert.Equal(123, owner);
    }

    [Fact]
    public void TheRootOfAnElement_IsItsRecordInTheProjectBlock_NotACopyOfItsIdInAFamilyDocument()
    {
        // the stream holds the element's root and, behind it, a block of the same id in the document of a loaded family, which the
        // framing does not list as a record of the project
        byte[] root = Frame(3708), copy = Frame(157);
        byte[] stream = [.. root, .. copy];
        var state = new ParsingState
        {
            ElemRecOwningElementIds = new() { [123] = -1 },
            FramedPartitions = [new PartitionFraming.Framed([stream[..10], stream[10..]], [new(102, 123, 0, root.Length)], 4)],
        };

        RvtFileReader.RecordFramedRoots(state);

        Assert.Equal(new StairComponentSource.ElementClassRoot(1, 3708), state.OwnedElementClassRoots[123]);
        Assert.Equal(new StairComponentSource.ElementClassRoot(1, 3708), state.WalkedElementClassRoots[123]);
        // counted per block met, as the stream walk does, the copy makes the root a duplicate and its class unknown
        var walked = new ParsingState { ElemRecOwningElementIds = new() { [123] = -1 } };
        StairComponentSource.Record(walked, stream, 0, Size(root), 3708, 123);
        StairComponentSource.Record(walked, stream, root.Length, Size(copy), 157, 123);
        Assert.Equal(new StairComponentSource.ElementClassRoot(2, null), walked.OwnedElementClassRoots[123]);
    }

    [Fact]
    public void AFileWithItsOwnClassSchema_CountsTheRecordInPlaceOfTheSeededRoot_AndKeepsTheSeedOfARootLeftWithoutA2022Class()
    {
        // both roots are seeded by the element index; the rewrite of the root of 456 failed, which marks it with the unknown tag
        byte[] root = Frame(3708), unknown = Frame(3708);
        BitConverter.GetBytes(456u).CopyTo(unknown, 0);
        BitConverter.GetBytes(R22Transcoder.UnknownTag).CopyTo(unknown, 12);
        byte[] stream = [.. root, .. unknown];
        var state = new ParsingState
        {
            WideSchema = new FileSchema(new SchemaDocument()),
            R22View = true,
            ElemRecOwningElementIds = new() { [123] = -1, [456] = -1 },
            FramedPartitions = [new PartitionFraming.Framed([stream], [new(102, 123, 0, root.Length), new(102, 456, root.Length, unknown.Length)], 4)],
        };
        state.OwnedElementClassRoots[123] = new(1, 3708);
        state.OwnedElementClassRoots[456] = new(1, 3708);

        RvtFileReader.RecordFramedRoots(state);

        Assert.Equal(new StairComponentSource.ElementClassRoot(1, 3708), state.OwnedElementClassRoots[123]);
        Assert.Equal(new StairComponentSource.ElementClassRoot(1, 3708), state.OwnedElementClassRoots[456]);
        Assert.False(state.WalkedElementClassRoots.ContainsKey(456));
    }

    [Fact]
    public void TheBlockParser_ReadsTheRecordsOfTheProjectBlock_EvenAcrossSegments_AndNotTheBlocksOfAFamilyDocument()
    {
        // the project's record of element 123 runs into the next segment; behind it a block of element 456 in the document of a loaded
        // family, which the framing does not list as a record of the project
        byte[] record = Frame(3708), family = Frame(157);
        BitConverter.GetBytes(456u).CopyTo(family, 0);
        byte[] stream = [.. record, .. family];
        var state = new ParsingState
        {
            FramedPartitions = [new PartitionFraming.Framed([stream[..10], stream[10..]], [new(102, 123, 0, record.Length)], 4)],
        };

        RvtFileReader.ParseFramedBlocks(state, collectBlockOnlyIds: true, []);

        Assert.Equal(1, state.BlockClassInventory[3708].Count);
        Assert.False(state.BlockClassInventory.ContainsKey(157));
        Assert.Equal([123u], state.BlockOnlyIds);
    }

    [Fact]
    public void ParseBlocks_OfAFileReadByItsFraming_TakesThePartitionRecords_AndScansOnlyTheOtherChunks()
    {
        // the partition chunk holds the project's record of element 123 and behind it a block of element 456 in the document of a loaded
        // family; the chunk of Global, which has no framing, holds a block of element 789
        byte[] record = Frame(3708), family = Frame(157), global = Frame(2000);
        BitConverter.GetBytes(456u).CopyTo(family, 0);
        BitConverter.GetBytes(789u).CopyTo(global, 0);
        byte[] partition = [.. record, .. family];
        var state = new ParsingState
        {
            AllKnownIds = [123, 789],
            Chunks = [partition, global],
            RecordChunks = [partition],
            NonRecordChunks = [global],
            PartitionChunkCount = 1,
            FramedPartitions = [new PartitionFraming.Framed([partition], [new(102, 123, 0, record.Length)], 4)],
        };

        RvtFileReader.ParseBlocks(state, null);

        Assert.Equal(1, state.BlockClassInventory[3708].Count);
        Assert.False(state.BlockClassInventory.ContainsKey(157));
        Assert.Equal(1, state.BlockClassInventory[2000].Count);
    }

    [Fact]
    public void AFileWithItsOwnClassSchema_IsReadByItsFraming_OnlyInTheRevit2022View()
    {
        var framed = new PartitionFraming.Framed([[]], [], 4);
        var state = new ParsingState { WideSchema = new FileSchema(new SchemaDocument()), FramedPartitions = [framed] };
        Assert.False(state.ReadsFramedRecords);
        state.R22View = true;
        Assert.True(state.ReadsFramedRecords);
        state.FramedPartitions = [framed, null];
        Assert.False(state.ReadsFramedRecords);
    }

    [Fact]
    public void ParseBlocks_InTheRevit2022View_ReadsTheBlockBehindAHeaderLikeRunOfAnUnknownElement()
    {
        // a byte run that looks like the block of element 999, which the element table does not list, lies in front of the record of
        // element 123; the rewritten partition chunk is the one the transcoder wrote, the framed record lies in its own segment
        byte[] run = Frame(3708), record = Frame(3708);
        BitConverter.GetBytes(999u).CopyTo(run, 0);
        byte[] partition = [.. run, .. record];
        var state = new ParsingState
        {
            WideSchema = new FileSchema(new SchemaDocument()),
            R22View = true,
            AllKnownIds = [123],
            Chunks = [partition],
            RecordChunks = [partition],
            PartitionChunkCount = 1,
            FramedPartitions = [new PartitionFraming.Framed([record], [new(102, 123, 0, record.Length)], 4)],
        };

        RvtFileReader.ParseBlocks(state, null);

        Assert.Equal(1, state.BlockClassInventory[3708].Count);
        Assert.DoesNotContain(999u, state.BlockOnlyIds);
    }

    [Fact]
    public void TheGeometryOfARecordThatRunsIntoTheNextSegmentOrIsOverTheSizeLimit_IsNotDecoded()
    {
        byte[] crossing = Frame(0x07D7), whole = Frame(0x07D7);
        BitConverter.GetBytes(456u).CopyTo(whole, 0);
        // [owner][checksum][size][class][data][size] with data over the limit of the scan
        uint size = BlockParser.MaxBlockSize + 4;
        byte[] large = new byte[16 + size];
        BitConverter.GetBytes(789u).CopyTo(large, 0);
        BitConverter.GetBytes(size).CopyTo(large, 8);
        BitConverter.GetBytes((ushort)0x07D7).CopyTo(large, 12);
        BitConverter.GetBytes(size).CopyTo(large, 16 + (int)size - 4);
        byte[] stream = [.. crossing, .. whole, .. large];
        var state = new ParsingState
        {
            AllKnownIds = [123, 456, 789],
            FramedPartitions = [new PartitionFraming.Framed([stream[..10], stream[10..]],
                [new(103, 123, 0, crossing.Length), new(103, 456, crossing.Length, whole.Length),
                 new(103, 789, crossing.Length + whole.Length, large.Length)], 4)],
        };
        var pending = new List<BlockParser.PendingGRep>();

        RvtFileReader.ParseFramedBlocks(state, collectBlockOnlyIds: true, pending);

        Assert.Equal(3, state.BlockClassInventory[0x07D7].Count);
        Assert.Equal([456u], pending.Select(p => p.Eid));
    }

    // TESTFILE.rvt is saved in five increments; reading it with and without the removal of the superseded copies changes the records around
    // the elements that one partition holds, but not their roots.
    [SkippableFact]
    public void TheClassOfAnElementHeldOnce_DoesNotDependOnTheRemovalOfOtherCopies()
    {
        string path = TestFiles.Find("TESTFILE.rvt");
        var (_, resolved) = new RvtFileReader(new RvtReaderOptions { ResolveCurrentCopies = true }).ReadWithState(path);
        var (_, everyCopy) = new RvtFileReader(new RvtReaderOptions { ResolveCurrentCopies = false }).ReadWithState(path);
        Assert.True(resolved.StaleCopiesRemoved);
        var heldOnce = everyCopy.WalkedElementClassRoots.Where(r => r.Value.BlockCount == 1).ToList();
        Assert.True(heldOnce.Count > 8000);
        Assert.All(heldOnce, r => Assert.Equal(r.Value, resolved.WalkedElementClassRoots[r.Key]));
        // the root of this arc wall is a record of the project's block that the stream walk does not reach
        Assert.Equal(new StairComponentSource.ElementClassRoot(1, 361), resolved.WalkedElementClassRoots[290112]);
    }

    private static uint Size(byte[] bytes) => BitConverter.ToUInt32(bytes, 8);

    private static void Reject(byte[] bytes, ushort cls)
    {
        Assert.False(TfBlockDeserializer.TryExtractOwnElementClass(bytes, 0, Size(bytes), cls, out int owner));
        Assert.Equal(-1, owner);
    }

    private static byte[] Frame(ushort cls)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(123u); writer.Write(0u); writer.Write(0u); writer.Write(cls);
        for (int index = 0; index < 6; index++) writer.Write(0u);
        writer.Write(0u); // constraints
        writer.Write(0u); // cell list
        writer.Write(1u); // project document
        writer.Write(123);
        for (int index = 1; index < 8; index++) writer.Write(-1);
        writer.Write(new byte[3]);
        uint size = (uint)stream.Length - 12;
        writer.Write(size);
        stream.Position = 8; writer.Write(size);
        return stream.ToArray();
    }
}
