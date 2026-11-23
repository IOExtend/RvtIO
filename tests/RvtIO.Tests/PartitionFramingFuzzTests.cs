using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Truncated and mutated partition streams are rejected with a reason: no exception escapes, no run takes long.</summary>
public class PartitionFramingFuzzTests
{
    private static PartitionFraming.Tags FramingTags()
    {
        var id = CoverageCensusTests.Id;
        return new PartitionFraming.Tags(id("ContentMarker"), id("ContentKey"), id("SegmentMarker"),
            id("SegmentCheckback"), id("SignatureMarker"));
    }

    // A tail as saved: the zero tag, then bytes of no meaning. The filler makes a walk that reads tail bytes as data fail.
    private static byte[] Tail()
    {
        var tail = new byte[PagedGzipStream.SegmentLength];
        tail.AsSpan(2).Fill(0xA5);
        return tail;
    }

    /// <summary>The stream as a saved partition holds it: a tail after every full page of data (<paramref name="finalTail"/>
    /// follows the last page too when it is given).</summary>
    private static byte[] WithTails(byte[] payload, byte[]? finalTail = null)
    {
        using var paged = new MemoryStream();
        for (int offset = 0; offset < payload.Length;)
        {
            int count = Math.Min(PagedGzipStream.PageDataLength, payload.Length - offset);
            paged.Write(payload, offset, count);
            offset += count;
            if (offset < payload.Length) paged.Write(Tail());
        }
        if (finalTail != null) paged.Write(finalTail);
        return paged.ToArray();
    }

    // What follows the terminal marker is no part of the stream, so padding moves the tails without changing the records.
    private static byte[] Padded(byte[] payload, int length)
    {
        Array.Resize(ref payload, length);
        return payload;
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // Three gzip members of two pages each, one per channel, so that the tails cut through members.
    private static byte[] LargePayload() => CoverageCensusTests.Partition(
        CoverageCensusTests.Block(false, CoverageCensusTests.R(1001, RandomBytes(2 * PagedGzipStream.PageDataLength, 42))));

    // Seven tails; the last one lies behind the records and is checked all the same.
    private static byte[] PaddedLargeStream() => WithTails(Padded(LargePayload(), 7 * PagedGzipStream.PageDataLength + 700));

    private static int[] TailOffsets(int length)
    {
        var offsets = new List<int>();
        for (long offset = PagedGzipStream.PageDataLength; offset < length; offset += PagedGzipStream.PageStride) offsets.Add((int)offset);
        return [.. offsets];
    }

    private static (bool Ok, string? Failure, int Records) Frame(byte[] raw)
    {
        int records = 0;
        bool ok = PartitionFraming.Walk(raw, FramingTags(), 4, _ => records++, out var failure, out _);
        return (ok, failure, records);
    }

    private static void AssertRejectedByTheTails(byte[] raw, string what)
    {
        var (ok, failure, records) = Frame(raw);
        Assert.False(ok, what);
        Assert.True(failure == "page-framing", $"{what}: {failure}");
        Assert.True(records == 0, $"{what}: {records} records before the rejection");
    }

    [Fact]
    public void Walk_ReadsGzipMembersAcrossPageTailsAndRejectsCorruptTails()
    {
        var body = RandomBytes(2 * PagedGzipStream.PageDataLength, 42);
        byte[] raw = WithTails(LargePayload());
        var channels = new List<int>();
        Assert.True(PartitionFraming.Walk(raw, FramingTags(), 4, record =>
        {
            Assert.True(body.AsSpan().SequenceEqual(record.Body.Span));
            channels.Add(record.Channel);
        }, out var failure, out _), failure);
        Assert.Equal(new[] { 101, 102, 103 }, channels);

        raw[PagedGzipStream.PageDataLength] = 1;
        AssertRejectedByTheTails(raw, "tag of the first tail");
    }

    [Fact]
    public void Walk_ChecksTheTagOfEveryPageTailBeforeItReadsARecord()
    {
        byte[] clean = PaddedLargeStream();
        int[] tails = TailOffsets(clean.Length);
        Assert.Equal(7, tails.Length);
        Assert.Equal((true, null, 3), Frame(clean));
        foreach (int tail in tails)
        {
            // the first tag byte must be zero and the low nibble of the second too
            foreach (var (at, value) in new[] { (0, (byte)0x01), (0, (byte)0xFF), (1, (byte)0x01), (1, (byte)0x0F) })
            {
                byte[] raw = (byte[])clean.Clone();
                raw[tail + at] = value;
                AssertRejectedByTheTails(raw, $"tail at {tail}, byte {at} = {value:X2}");
            }
            // the high nibble of the second byte and the rest of the tail are free
            foreach (var (at, value) in new[] { (1, (byte)0x10), (1, (byte)0xF0), (2, (byte)0x00), (PagedGzipStream.SegmentLength - 1, (byte)0xFF) })
            {
                byte[] raw = (byte[])clean.Clone();
                raw[tail + at] = value;
                Assert.True(Frame(raw) == (true, null, 3), $"tail at {tail}, byte {at} = {value:X2}");
            }
        }
    }

    [Fact]
    public void Walk_RejectsAStreamThatEndsInsideAPageTail()
    {
        byte[] clean = PaddedLargeStream();
        foreach (int tail in TailOffsets(clean.Length))
        {
            foreach (int inside in new[] { 1, 100, PagedGzipStream.SegmentLength - 1 })
                AssertRejectedByTheTails(clean[..(tail + inside)], $"stream cut {inside} bytes into the tail at {tail}");
            // the complete tail is framed; what the cut took away is found missing by the records, not by the tails
            var (ok, failure, _) = Frame(clean[..(tail + PagedGzipStream.SegmentLength)]);
            Assert.True(ok || failure == "bounds", $"stream cut behind the tail at {tail}: {failure}");
        }
    }

    [Fact]
    public void Walk_AcceptsAnyTagOnACompleteFinalTailButDemandsTheTagOfTheOthers()
    {
        // measured on a saved partition: the stream ends at a tail position and the last tail does not start with the zero tag
        byte[] finalTail = Tail();
        finalTail[0] = 0x60;
        finalTail[1] = 0xA5;
        byte[] raw = WithTails(Padded(LargePayload(), 7 * PagedGzipStream.PageDataLength), finalTail);
        Assert.Equal(7 * PagedGzipStream.PageStride, raw.Length);
        Assert.Equal((true, null, 3), Frame(raw));

        // one byte more and it is an ordinary tail again; one byte less and it is no tail
        AssertRejectedByTheTails([.. raw, 0], "final tail followed by a byte");
        AssertRejectedByTheTails(raw[..^1], "final tail cut by a byte");
        // the exemption is the last tail's alone
        foreach (int tail in TailOffsets(raw.Length)[..^1])
        {
            byte[] corrupt = (byte[])raw.Clone();
            corrupt[tail + 1] = 0x01;
            AssertRejectedByTheTails(corrupt, $"tail at {tail} before an exempt final tail");
        }
    }

    [Fact]
    public void Walk_AndRemoveTailsAcceptTheSameTails()
    {
        // The tag rule is PagedGzipStream.HoldsSegment (also behind RemoveSegments); the exemption of a complete final tail is
        // written twice, in Cursor and in RemoveTails, which the stream editor uses: they must not drift apart.
        byte[] exemptTail = Tail();
        exemptTail[0] = 0x60;
        exemptTail[1] = 0xA5;
        var streams = new[]
        {
            PaddedLargeStream(),
            WithTails(Padded(LargePayload(), 7 * PagedGzipStream.PageDataLength), exemptTail),
        };
        int cases = 0, rejected = 0;
        foreach (byte[] stream in streams)
        {
            var candidates = new List<byte[]> { stream };
            foreach (int tail in TailOffsets(stream.Length))
            {
                foreach (var (at, value) in new[] { (0, (byte)0x01), (1, (byte)0x01), (1, (byte)0x10) })
                {
                    byte[] raw = (byte[])stream.Clone();
                    raw[tail + at] = value;
                    candidates.Add(raw);
                }
                foreach (int cut in new[] { -1, 0, 1, 100, PagedGzipStream.SegmentLength - 1, PagedGzipStream.SegmentLength, PagedGzipStream.SegmentLength + 1 })
                    if (tail + cut <= stream.Length) candidates.Add(stream[..(tail + cut)]);
                candidates.Add([.. stream[..(tail + PagedGzipStream.SegmentLength)], 0]);
            }
            foreach (byte[] raw in candidates)
            {
                bool walked = Frame(raw).Failure != "page-framing";
                bool removed = PartitionFraming.RemoveTails(raw) != null;
                Assert.True(walked == removed, $"stream of {raw.Length} bytes: Walk framed it = {walked}, RemoveTails = {removed}");
                if (PagedGzipStream.RemoveSegments(raw) != null) Assert.True(walked, $"stream of {raw.Length} bytes");
                cases++;
                if (!walked) rejected++;
            }
        }
        Assert.True(rejected > 40 && cases - rejected > 40, $"{rejected} of {cases} rejected");
    }

    [Fact]
    public void Walk_ReadsEveryRecordWhateverFieldAPageTailSplits()
    {
        var split = new HashSet<int>();
        // One record whose body size moves the fixed fields behind a segment across the first two boundaries, and a keyed second
        // block (with its 16-byte key) behind a first block of such a size that the key meets one of them.
        foreach (int size in Enumerable.Range(64_730, 120).Concat(Enumerable.Range(129_605, 120)))
            AssertWalkReads(split, (false, size));
        foreach (int size in Enumerable.Range(21_485, 100).Concat(Enumerable.Range(43_112, 100)))
            AssertWalkReads(split, (false, size), (true, 40));
        // the sizes must have put a 2-byte, a 4-byte and the 16-byte field across a tail (the deflate encoder is free to move them)
        Assert.Contains(2, split);
        Assert.Contains(4, split);
        Assert.Contains(16, split);
    }

    private static void AssertWalkReads(HashSet<int> split, params (bool Keyed, int Size)[] blocks)
    {
        string what = string.Join("+", blocks.Select(b => (b.Keyed ? "keyed " : "") + b.Size));
        var bodies = blocks.Select((b, i) => RandomBytes(b.Size, 1000 * i + b.Size)).ToArray();
        byte[] payload = CoverageCensusTests.Partition([.. blocks.Select((b, i) =>
            CoverageCensusTests.Block(b.Keyed, CoverageCensusTests.R((uint)(1001 + i), bodies[i])))]);
        split.UnionWith(FieldsAcrossAPageBoundary(payload));
        byte[] raw = WithTails(payload);
        Assert.True(payload.AsSpan().SequenceEqual(PartitionFraming.RemoveTails(raw)), $"{what}: the stream was not built as expected");

        // the reference is what was written: per block its channels in order, each holding the one record
        var expected = blocks.SelectMany((b, i) => Enumerable.Range(101, 3).Select(channel =>
            (Block: i, Key: b.Keyed ? Convert.ToHexString(new[] { (byte)i }.Concat(new byte[15]).ToArray()) : "", Channel: channel, Owner: (ulong)(1001 + i), Body: bodies[i]))).ToList();
        int seen = 0;
        bool ok = PartitionFraming.Walk(raw, FramingTags(), 4, record =>
        {
            var (block, key, channel, owner, body) = expected[seen++];
            Assert.True(record.Block == block && record.Key == key && record.Channel == channel && record.Owner == owner
                && body.AsSpan().SequenceEqual(record.Body.Span), $"{what}: record {seen}");
        }, out var failure, out int blocksRead);
        Assert.True(ok, $"{what}: {failure}");
        Assert.Equal(expected.Count, seen);
        Assert.Equal(blocks.Length, blocksRead);
    }

    // The widths of the fixed-size fields of the stream (markers, counts, sizes, the key) that lie across a page boundary of the data.
    private static HashSet<int> FieldsAcrossAPageBoundary(byte[] payload)
    {
        var split = new HashSet<int>();
        int at = 0;
        uint Field(int width)
        {
            if (at / PagedGzipStream.PageDataLength != (at + width - 1) / PagedGzipStream.PageDataLength) split.Add(width);
            uint value = width switch
            {
                2 => BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(at)),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at)),
                _ => 0,
            };
            at += width;
            return value;
        }

        Field(4); // format 9
        Field(4);
        while (true)
        {
            Field(2); // content marker
            uint pointer = Field(4);
            if (pointer != 0) Field(2);
            uint owners = Field(4);
            if (pointer != 0) Field(16);
            if (owners == uint.MaxValue) return split;
            while (true)
            {
                Field(2); // segment marker, flags, record count
                Field(4);
                Field(4);
                uint size = Field(4);
                Field(4);
                if (size == 0) break;
                Field(4); // channel, opaque
                Field(4);
                at += (int)size - 8;
                Field(2); // checkback marker and size
                Field(4);
            }
            Field(2); // signature marker and the two empty lists
            Field(4);
            Field(4);
        }
    }

    [Fact]
    public void Walk_RejectsEveryCutOfAMultiPageStreamWithoutAnException()
    {
        byte[] clean = WithTails(LargePayload());
        var lengths = Enumerable.Range(0, 300).ToList();
        foreach (int tail in TailOffsets(clean.Length)[..3]) lengths.AddRange(Enumerable.Range(tail - 3, 360));
        foreach (int length in lengths)
        {
            byte[] raw = clean[..length];
            (bool Ok, string? Failure, int Records) framed = default;
            var thrown = Record.Exception(() => framed = Frame(raw));
            Assert.True(thrown == null, $"cut at {length}: {thrown}");
            Assert.False(framed.Ok, $"cut at {length}");
            // a cut inside a tail is a tail fault; every other cut leaves the walk short of bytes
            string expected = PartitionFraming.RemoveTails(raw) == null ? "page-framing" : "bounds";
            Assert.True(framed.Failure == expected, $"cut at {length}: {framed.Failure}, expected {expected}");
            if (expected == "page-framing") Assert.Equal(0, framed.Records);
        }
    }

    [Fact]
    public void Walk_DoesNotCopyTheStreamBeforeItReadsIt()
    {
        // 200 pages with valid tails, rejected at the first marker: only a copy of the stream without its tails could allocate much
        var raw = new byte[200 * PagedGzipStream.PageStride];
        raw[0] = 9;
        Frame(raw); // type loading allocates on first use
        long before = GC.GetAllocatedBytesForCurrentThread();
        var (ok, failure, _) = Frame(raw);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(ok);
        Assert.Equal("content-marker", failure);
        Assert.True(allocated < 256 * 1024, $"Walk allocated {allocated} bytes for a stream of {raw.Length}.");
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
