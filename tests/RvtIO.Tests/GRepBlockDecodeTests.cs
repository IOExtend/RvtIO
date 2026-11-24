using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Some tests below hold one decode until another one runs, which needs a second thread of the pool at once; a pool busy with the tests of
/// another class could start it later than the wait of the test allows.</summary>
[CollectionDefinition("GRepBlockDecode", DisableParallelization = true)]
public sealed class GRepBlockDecodeCollection;

/// <summary>Decoding the GRep blocks of a parse pass and applying the results: the apply order and thread, the browser path and the
/// first-wins rules of the apply steps.</summary>
[Collection("GRepBlockDecode")]
public class GRepBlockDecodeTests
{
    /// <summary>A hang guard: the longest a decode waits for another one, so that a regression fails instead of hanging.</summary>
    private static readonly TimeSpan DecodeWait = TimeSpan.FromSeconds(20);

    private static GRepData?[] SyntheticBlocks(int count) =>
        Enumerable.Range(0, count).Select(i => i % 3 == 0 ? null : new GRepData()).ToArray();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(300)]
    public void DecodeGRepBlocks_AppliesEachBlockOnceOnTheCallingThreadInIndexOrder(int count)
    {
        var blocks = SyntheticBlocks(count);
        var applied = new List<int>();
        int caller = Environment.CurrentManagedThreadId;
        // Block 0 is held until every other block is decoded, so it completes last: a result applied when its decode
        // completes instead of in index order is caught without relying on timing. One processor cannot decode two
        // blocks at once, so the hold is skipped there.
        bool holdFirst = count > 1 && Environment.ProcessorCount > 1;
        using var othersDecoded = new CountdownEvent(Math.Max(count - 1, 0));

        BlockParser.DecodeGRepBlocksInOrder(count,
            index =>
            {
                if (index != 0) { othersDecoded.Signal(); return blocks[index]; }
                if (holdFirst)
                {
                    Assert.True(othersDecoded.Wait(DecodeWait), "The other blocks were not decoded while block 0 was held.");
                    // Lets a regression that applies on completion apply the last decoded block before block 0 is released.
                    Thread.Sleep(50);
                }
                return blocks[index];
            },
            (index, geometry) =>
            {
                Assert.Equal(caller, Environment.CurrentManagedThreadId);
                Assert.Same(blocks[index], geometry);
                applied.Add(index);
            });

        Assert.Equal(Enumerable.Range(0, count), applied);
    }

    [SkippableFact]
    public void DecodeGRepBlocks_DecodesBlocksInParallelOutsideABrowser()
    {
        if (Environment.ProcessorCount < 2) throw new SkipException("Decoding two blocks at once needs two processors.");
        // The first two decodes wait for each other, so they pass only when two blocks are decoded at the same time.
        using var meeting = new Barrier(2);
        int arrived = 0;

        BlockParser.DecodeGRepBlocksInOrder(16,
            index =>
            {
                if (Interlocked.Increment(ref arrived) <= 2)
                    Assert.True(meeting.SignalAndWait(DecodeWait), "No second block was decoded at the same time.");
                return null;
            },
            (index, geometry) => { });
    }

    [Fact]
    public void DecodeGRepBlocks_InABrowserDecodesAndAppliesOneBlockAtATime()
    {
        const int count = 300;
        var blocks = SyntheticBlocks(count);
        var applied = new List<int>();
        int caller = Environment.CurrentManagedThreadId;
        int retained = 0;

        BlockParser.DecodeGRepBlocksInOrder(count,
            index =>
            {
                Assert.Equal(caller, Environment.CurrentManagedThreadId);
                Assert.Equal(0, retained);
                retained++;
                return blocks[index];
            },
            (index, geometry) =>
            {
                Assert.Same(blocks[index], geometry);
                retained--;
                applied.Add(index);
            },
            sequential: true);

        Assert.Equal(0, retained);
        Assert.Equal(Enumerable.Range(0, count), applied);
    }

    [Fact]
    public void DecodeGRepBlocks_AFailedDecodeOutsideABrowserAppliesNothingAndStaysAMalformedInputFailure()
    {
        var applied = new List<int>();

        var failure = Assert.ThrowsAny<AggregateException>(() => BlockParser.DecodeGRepBlocksInOrder(50,
            index => index == 5 ? throw new InvalidDataException("bad block") : new GRepData(),
            (index, geometry) => applied.Add(index)));

        Assert.IsType<InvalidDataException>(Assert.Single(failure.InnerExceptions));
        Assert.True(MalformedInput.Is(failure));
        Assert.Empty(applied);
    }

    [Fact]
    public void DecodeGRepBlocks_AFailedDecodeInABrowserPassesThroughAfterTheEarlierBlocksWereApplied()
    {
        var applied = new List<int>();

        var failure = Assert.Throws<InvalidDataException>(() => BlockParser.DecodeGRepBlocksInOrder(50,
            index => index == 5 ? throw new InvalidDataException("bad block") : new GRepData(),
            (index, geometry) => applied.Add(index),
            sequential: true));

        Assert.True(MalformedInput.Is(failure));
        Assert.Equal(Enumerable.Range(0, 5), applied);
    }

    private static GRepData AuthoredFamilyCutter() => new()
    {
        IsFamilyComplexCut = true,
        Faces = [new GRepFace()],
        Solids = [new GRepSolid { IsClosed = true, FaceIndices = [0] }],
    };

    [Fact]
    public void ApplyPendingGReps_ReportsStartAndCompletionOfItsPass()
    {
        var state = new ParsingState();
        var updates = new List<(int Completed, int Total)>();
        var pending = new List<BlockParser.PendingGRep>
        {
            new(new byte[16], 7, 0, 0, IsFamilyCut: false),
            new(new byte[16], 8, 0, 0, IsFamilyCut: false),
        };

        BlockParser.ApplyPendingGReps(state, pending, (completed, total) => updates.Add((completed, total)));

        Assert.Equal((0, 2), updates[0]);
        Assert.Equal((2, 2), updates[^1]);
        Assert.All(updates, update => Assert.Equal(2, update.Total));
        Assert.Empty(state.GrepBlocks);
    }

    [Fact]
    public void ApplyPendingGRep_KeepsTheFirstAuthoredFamilyCutterOfAnElement()
    {
        var state = new ParsingState();
        var job = new BlockParser.PendingGRep([], 7, 0, 0, IsFamilyCut: true);
        var first = AuthoredFamilyCutter();

        BlockParser.ApplyPendingGRep(state, job, null);
        BlockParser.ApplyPendingGRep(state, job, new GRepData());
        Assert.Empty(state.FamilyCutGreps);

        BlockParser.ApplyPendingGRep(state, job, first);
        BlockParser.ApplyPendingGRep(state, job, AuthoredFamilyCutter());

        Assert.Same(first, state.FamilyCutGreps[7]);
        Assert.Empty(state.GrepBlocks);
    }

    [Fact]
    public void ApplyPendingGRep_MergesABlockIntoTheGeometryOfItsElementInArrivalOrder()
    {
        var state = new ParsingState();
        var job = new BlockParser.PendingGRep([], 9, 0, 0, IsFamilyCut: false);
        var empty = new GRepData();
        var withFace = new GRepData { Faces = [new GRepFace()] };

        BlockParser.ApplyPendingGRep(state, job, null);
        Assert.Empty(state.GrepBlocks);

        BlockParser.ApplyPendingGRep(state, job, empty);
        Assert.Same(empty, state.GrepBlocks[9]);

        // A block with faces replaces one without; a later block without faces does not replace it.
        BlockParser.ApplyPendingGRep(state, job, withFace);
        BlockParser.ApplyPendingGRep(state, job, new GRepData());
        Assert.Same(withFace, state.GrepBlocks[9]);
        Assert.Empty(state.FamilyCutGreps);
    }

    [Fact]
    public void StoreFirstDecodableGRep_StoresTheFirstDecodableBlockOfAnElement()
    {
        var state = new ParsingState();
        var first = new GRepData();

        Assert.False(RvtFileReader.StoreFirstDecodableGRep(state, 3, null));
        Assert.Empty(state.GrepBlocks);

        Assert.True(RvtFileReader.StoreFirstDecodableGRep(state, 3, first));
        Assert.False(RvtFileReader.StoreFirstDecodableGRep(state, 3, new GRepData()));
        Assert.Same(first, state.GrepBlocks[3]);

        Assert.True(RvtFileReader.StoreFirstDecodableGRep(state, 4, new GRepData()));
        Assert.Equal(2, state.GrepBlocks.Count);
    }
}
