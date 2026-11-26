using System.Buffers.Binary;
using System.Text;
using ICSharpCode.SharpZipLib.Checksum;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

public sealed class PartitionStreamEditorTests
{
    private static readonly PartitionFraming.Tags SampleTags = new(
        ContentMarker: 913,
        ContentKey: 912,
        SegmentMarker: 914,
        SegmentCheckback: 915,
        SignatureMarker: 916);

    internal sealed record SpecRecord(ulong Owner, byte[] Body, uint? CustomExtra = null);

    internal sealed record SpecChain(int Channel, SpecRecord[] Records, uint UpperFlags = 4u, uint Opaque = 0u, bool MultiSegment = false);

    internal sealed record SpecBlock(bool Keyed, SpecChain[] Chains, byte[]? SignatureBytes = null, string? SignatureText = null);

    internal static uint ComputeAdler(ReadOnlySpan<byte> bytes)
    {
        var adler = new Adler32();
        adler.Update(bytes.ToArray());
        return (uint)adler.Value;
    }

    internal static byte[] TaggedBody(ushort classTag, ReadOnlySpan<byte> payload)
    {
        byte[] result = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, classTag);
        payload.CopyTo(result.AsSpan(2));
        return result;
    }

    internal static byte[] BuildPartitionStream(int idWidth, PartitionFraming.Tags tags, params SpecBlock[] blocks)
    {
        ulong terminator = idWidth == 8 ? ulong.MaxValue : uint.MaxValue;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(9u);
        writer.Write(0u);

        for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        {
            var block = blocks[blockIndex];
            uint ownerCount = (uint)block.Chains
                .Where(c => c.Channel == 101)
                .SelectMany(c => c.Records)
                .Count(r => r.Owner != terminator);

            writer.Write(tags.ContentMarker);
            writer.Write(block.Keyed ? uint.MaxValue : 0u);
            if (block.Keyed) writer.Write(tags.ContentKey);
            writer.Write(ownerCount);
            if (block.Keyed)
            {
                byte[] key = new byte[16];
                key[0] = (byte)(blockIndex + 1);
                key[15] = 0xAA;
                writer.Write(key);
            }

            foreach (var chain in block.Chains)
            {
                using var inflated = new MemoryStream();
                using var itemWriter = new BinaryWriter(inflated);
                long bodySum = 0;
                foreach (var rec in chain.Records)
                {
                    if (idWidth == 8) itemWriter.Write(rec.Owner);
                    else itemWriter.Write((uint)rec.Owner);

                    if (chain.Channel != 101)
                    {
                        uint extra = rec.CustomExtra ?? (rec.Owner == terminator && rec.Body.Length == 0 ? 0u : ComputeAdler(rec.Body));
                        itemWriter.Write(extra);
                    }
                    itemWriter.Write((uint)rec.Body.Length);
                    itemWriter.Write(rec.Body);
                    itemWriter.Write((uint)rec.Body.Length);
                    bodySum += rec.Body.Length;
                }

                byte[] allItems = inflated.ToArray();
                if (!chain.MultiSegment || allItems.Length < 6)
                {
                    EmitRawSegment(writer, tags, chain.UpperFlags | 0u, (uint)chain.Records.Length, (uint)bodySum, (uint)chain.Channel, chain.Opaque, allItems);
                }
                else
                {
                    int third = allItems.Length / 3;
                    EmitRawSegment(writer, tags, chain.UpperFlags | 2u, (uint)chain.Records.Length, (uint)bodySum, (uint)chain.Channel, chain.Opaque, allItems.AsSpan(0, third));
                    EmitRawSegment(writer, tags, chain.UpperFlags | 3u, 0u, 0u, (uint)chain.Channel, chain.Opaque, allItems.AsSpan(third, third));
                    EmitRawSegment(writer, tags, chain.UpperFlags | 1u, 0u, 0u, (uint)chain.Channel, chain.Opaque, allItems.AsSpan(2 * third));
                }
            }

            // Empty segment closing the block's segment list
            writer.Write(tags.SegmentMarker);
            writer.Write(new byte[16]);

            // SignatureMarker
            writer.Write(tags.SignatureMarker);
            byte[] sigBytes = block.SignatureBytes ?? [];
            string sigText = block.SignatureText ?? "";
            byte[] sigUtf16 = Encoding.Unicode.GetBytes(sigText);
            writer.Write((uint)sigBytes.Length);
            writer.Write(sigBytes);
            writer.Write((uint)sigText.Length);
            writer.Write(sigUtf16);
        }

        // Terminal marker
        writer.Write(tags.ContentMarker);
        writer.Write(0u);
        writer.Write(uint.MaxValue);

        return PageCodec.EncodePadded(stream.ToArray());
    }

    private static void EmitRawSegment(
        BinaryWriter writer,
        PartitionFraming.Tags tags,
        uint flags,
        uint count,
        uint unc,
        uint channel,
        uint opaque,
        ReadOnlySpan<byte> inflatedSlice)
    {
        byte[] gz = GzipMember.Write(inflatedSlice);
        uint size = (uint)gz.Length + 8;
        writer.Write(tags.SegmentMarker);
        writer.Write(flags);
        writer.Write(count);
        writer.Write(size);
        writer.Write(unc);
        writer.Write(channel);
        writer.Write(opaque);
        writer.Write(gz);
        writer.Write(tags.SegmentCheckback);
        writer.Write(size);
    }

    internal static SpecBlock MakeStandardBlock(
        int idWidth,
        bool keyed,
        (ulong Owner, ushort ClassTag, byte[] Payload)[] elements,
        uint upperFlags = 4u,
        uint opaque = 0x12345678u,
        bool splitChannel102IntoTwoChains = false,
        bool multiSegmentChannel102 = false)
    {
        ulong terminator = idWidth == 8 ? ulong.MaxValue : uint.MaxValue;
        var ch101 = elements.Select(e => new SpecRecord(e.Owner, TaggedBody(1, [(byte)(e.Owner & 0xFF)]))).Append(new SpecRecord(terminator, [])).ToArray();
        var ch103 = elements.Select(e => new SpecRecord(e.Owner, TaggedBody(3, [9, 8, 7]))).Append(new SpecRecord(terminator, [])).ToArray();

        SpecChain[] chains;
        if (splitChannel102IntoTwoChains && elements.Length >= 2)
        {
            int mid = elements.Length / 2;
            var first102 = elements[..mid].Select(e => new SpecRecord(e.Owner, TaggedBody(e.ClassTag, e.Payload))).ToArray();
            var second102 = elements[mid..].Select(e => new SpecRecord(e.Owner, TaggedBody(e.ClassTag, e.Payload))).Append(new SpecRecord(terminator, [])).ToArray();
            chains =
            [
                new SpecChain(101, ch101, upperFlags, opaque),
                new SpecChain(102, first102, upperFlags, opaque),
                new SpecChain(102, second102, upperFlags, opaque, multiSegmentChannel102),
                new SpecChain(103, ch103, upperFlags, opaque),
            ];
        }
        else
        {
            var ch102 = elements.Select(e => new SpecRecord(e.Owner, TaggedBody(e.ClassTag, e.Payload))).Append(new SpecRecord(terminator, [])).ToArray();
            chains =
            [
                new SpecChain(101, ch101, upperFlags, opaque),
                new SpecChain(102, ch102, upperFlags, opaque, multiSegmentChannel102),
                new SpecChain(103, ch103, upperFlags, opaque),
            ];
        }

        byte[] sig = new byte[64];
        Array.Fill(sig, (byte)0x5A);
        return new SpecBlock(keyed, chains, sig, "Data generated by Autodesk\u00AE Revit\u00AE");
    }

    [Fact]
    public void PagedStreamCodec_PreservesZeroAndEightBytePrefixes_AndVerifiesParity()
    {
        byte[] formatsOriginal = PageCodec.EncodePadded(GzipMember.Write([1, 2, 3, 4]));
        Assert.Empty(PagedStreamCodec.ReadPrefix(formatsOriginal));

        byte[] newFormatsPayload = Encoding.UTF8.GetBytes("updated-formats-payload");
        byte[] reencodedFormats = PagedStreamCodec.Reencode(formatsOriginal, newFormatsPayload);
        Assert.Equal(PageCheckStatus.Valid, PageCodec.Verify(reencodedFormats).Status);
        Assert.Equal(newFormatsPayload, PagedGzipStream.Decompress(reencodedFormats)!);

        byte[] prefix = [5, 0, 0, 0, 0, 0, 0, 0];
        byte[] globalOriginal = PagedStreamCodec.Encode(prefix, [10, 20, 30]);
        Assert.Equal(prefix, PagedStreamCodec.ReadPrefix(globalOriginal));

        byte[] newGlobalPayload = [99, 100, 101, 102, 103];
        byte[] reencodedGlobal = PagedStreamCodec.Reencode(globalOriginal, newGlobalPayload);
        Assert.Equal(PageCheckStatus.Valid, PageCodec.Verify(reencodedGlobal).Status);
        Assert.Equal(prefix, reencodedGlobal[..8]);
        Assert.Equal(newGlobalPayload, PagedGzipStream.Decompress(reencodedGlobal)!);
    }

    [Fact]
    public void Apply_WithEmptyEdits_ReturnsExactByteCopyAfterVerifyingFraming()
    {
        byte[] raw = BuildPartitionStream(
            8,
            SampleTags,
            MakeStandardBlock(8, keyed: false, [(100UL, 42, [1, 2, 3]), (200UL, 5000, [9, 9, 9, 9])]));

        byte[] output = PartitionStreamEditor.Apply(raw, SampleTags, 8, []);

        Assert.Equal(raw, output);
        Assert.NotSame(raw, output);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Apply_RewritesTargetedRecords_PreservesUntouchedBlocksChainsFlagsOpaqueAndUnknownPayloads(int idWidth)
    {
        byte[] opaqueUnknownPayload = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03];
        ulong largeId = idWidth == 8 ? 0x0000_0001_0000_0042UL : 4200UL;
        var projectBlock = MakeStandardBlock(
            idWidth,
            keyed: false,
            [
                (10UL, 50, [1, 2, 3, 4]),
                (20UL, 5000, opaqueUnknownPayload),
                (largeId, 51, [7, 8]),
            ],
            upperFlags: 4u,
            opaque: 0x0BAD_F00Du,
            splitChannel102IntoTwoChains: true,
            multiSegmentChannel102: true);

        var familyBlock = MakeStandardBlock(
            idWidth,
            keyed: true,
            [
                (10UL, 60, [55, 66, 77]),
                (99UL, 61, [88, 99]),
            ],
            upperFlags: 4u,
            opaque: 0x1122_3344u);

        byte[] raw = BuildPartitionStream(idWidth, SampleTags, projectBlock, familyBlock);
        byte[] replacementBody = TaggedBody(51, Encoding.Unicode.GetBytes("Expanded synthetic record payload!"));

        byte[] edited = PartitionStreamEditor.Apply(
            raw,
            SampleTags,
            idWidth,
            [new PartitionRecordEdit(Block: 0, Channel: 102, Owner: largeId, Body: replacementBody)]);

        Assert.Equal(PageCheckStatus.Valid, PageCodec.Verify(edited).Status);

        var records = new List<PartitionFraming.Record>();
        Assert.True(
            PartitionFraming.Walk(
                edited,
                SampleTags,
                idWidth,
                r => records.Add(r with { Body = r.Body.ToArray(), Item = r.Item.ToArray() }),
                out string? failure,
                out int blocks),
            failure);
        Assert.Equal(2, blocks);

        // Edited record updated with fresh Adler-32
        var target = records.Single(r => r.Block == 0 && r.Channel == 102 && r.Owner == largeId);
        Assert.Equal(replacementBody, target.Body.ToArray());
        Assert.Equal(ComputeAdler(replacementBody), target.Extra);

        // Unknown class payload (#5000) in project block preserved byte-for-byte
        var unknown = records.Single(r => r.Block == 0 && r.Channel == 102 && r.Owner == 20UL);
        Assert.Equal(TaggedBody(5000, opaqueUnknownPayload), unknown.Body.ToArray());

        // Same owner ID (10) in loaded-family keyed block (Block 1) is completely untouched
        var familyOwner10 = records.Single(r => r.Block == 1 && r.Channel == 102 && r.Owner == 10UL);
        Assert.Equal(TaggedBody(60, [55, 66, 77]), familyOwner10.Body.ToArray());

        // Inspect raw segment flags, opaque dword, and block signatures:
        // Block 0 (edited) has unsigned SignatureMarker (n=0, m=0) and keeps upper flag 0x04 + opaque 0x0BADF00D;
        // Block 1 (unedited family) keeps its 64-byte signature and text verbatim!
        byte[] stripped = PartitionFraming.RemoveTails(edited)!;
        InspectBlockMetadata(stripped, out var block0Meta, out var block1Meta);
        Assert.Equal(4u, block0Meta.FirstUpperFlags);
        Assert.Equal(0x0BAD_F00Du, block0Meta.FirstOpaque);
        Assert.Equal(0u, block0Meta.SignatureBytes);
        Assert.Equal(0u, block0Meta.SignatureChars);

        Assert.Equal(64u, block1Meta.SignatureBytes);
        Assert.Equal(34u, block1Meta.SignatureChars);
    }

    [Fact]
    public void Apply_SplitsReplacementBodyLargerThan128KiB_IntoMultiSegmentChain()
    {
        byte[] raw = BuildPartitionStream(
            8,
            SampleTags,
            MakeStandardBlock(8, keyed: false, [(1UL, 42, [1, 2]), (2UL, 43, [3, 4])]));

        // 300,000 bytes > 2 * 131,072 => must emit mode 2 (131,072) -> mode 3 (131,072) -> mode 1 (37,856)
        byte[] bigPayload = new byte[299_998];
        new Random(2026).NextBytes(bigPayload);
        byte[] bigBody = TaggedBody(42, bigPayload);
        Assert.Equal(300_000, bigBody.Length);

        byte[] edited = PartitionStreamEditor.Apply(
            raw,
            SampleTags,
            8,
            [new PartitionRecordEdit(Block: 0, Channel: 102, Owner: 1UL, Body: bigBody)]);

        var segmentLengths = new List<int>();
        var records = new List<PartitionFraming.Record>();
        Assert.True(
            PartitionFraming.Walk(
                edited,
                SampleTags,
                8,
                r => records.Add(r with { Body = r.Body.ToArray(), Item = r.Item.ToArray() }),
                out string? failure,
                out _,
                segmentLengths),
            failure);

        var updated = records.Single(r => r.Block == 0 && r.Channel == 102 && r.Owner == 1UL);
        Assert.Equal(bigBody, updated.Body.ToArray());
        Assert.Equal(ComputeAdler(bigBody), updated.Extra);
        // ch101 (1) + split ch102 record 1 (3 segments) + ch102 remaining batch (1) + ch103 (1) = 6 segments
        Assert.Equal(6, segmentLengths.Count);
    }

    [Fact]
    public void Apply_SupportsChannel101AndChannel103Rewrites()
    {
        byte[] raw = BuildPartitionStream(
            4,
            SampleTags,
            MakeStandardBlock(4, keyed: false, [(7UL, 42, [10, 20, 30])]));

        byte[] new101 = TaggedBody(1, [0xAA, 0xBB, 0xCC, 0xDD]);
        byte[] new103 = TaggedBody(3, [0x11, 0x22]);

        byte[] edited = PartitionStreamEditor.Apply(
            raw,
            SampleTags,
            4,
            [
                new PartitionRecordEdit(0, 101, 7UL, new101),
                new PartitionRecordEdit(0, 103, 7UL, new103),
            ]);

        var records = new List<PartitionFraming.Record>();
        Assert.True(
            PartitionFraming.Walk(
                edited,
                SampleTags,
                4,
                r => records.Add(r with { Body = r.Body.ToArray(), Item = r.Item.ToArray() }),
                out _,
                out _));

        var rec101 = records.Single(r => r.Channel == 101 && r.Owner == 7UL);
        Assert.Equal(0u, rec101.Extra);
        Assert.Equal(new101, rec101.Body.ToArray());

        var rec103 = records.Single(r => r.Channel == 103 && r.Owner == 7UL);
        Assert.Equal(ComputeAdler(new103), rec103.Extra);
        Assert.Equal(new103, rec103.Body.ToArray());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Apply_AddsOwnerAcrossAllProjectChannels(int idWidth)
    {
        ulong owner = idWidth == 4 ? 12UL : (ulong)uint.MaxValue + 12;
        ulong terminator = idWidth == 4 ? uint.MaxValue : ulong.MaxValue;
        var project = MakeStandardBlock(idWidth, keyed: false, [(5UL, 20, [1, 2]), (6UL, 21, [4])], splitChannel102IntoTwoChains: true);
        var family = MakeStandardBlock(idWidth, keyed: true, [(owner, 40, [3, 4])]);
        byte[] raw = BuildPartitionStream(idWidth, SampleTags, project, family);
        byte[] snapshot = [.. raw];
        byte[][] bodies = [TaggedBody(51, [5]), TaggedBody(52, [6]), TaggedBody(53, [7])];
        var addition = new PartitionOwnerAddition(0, owner, bodies);
        byte[] replacement = TaggedBody(20, [8]);

        byte[] updated = PartitionStreamEditor.Apply(raw, SampleTags, idWidth, [new(0, 102, 5, replacement)], [addition]);

        var records = new List<PartitionFraming.Record>();
        Assert.True(PartitionFraming.Walk(updated, SampleTags, idWidth,
            record => records.Add(record with { Body = record.Body.ToArray() }), out string? failure, out int blocks), failure);
        Assert.Equal(2, blocks);
        foreach (int channel in new[] { 101, 102, 103 })
        {
            var local = records.Where(record => record.Block == 0 && record.Channel == channel).ToArray();
            Assert.Equal([5UL, 6UL, owner, terminator], local.Select(record => record.Owner));
            var added = local[2];
            Assert.Equal(bodies[channel - 101], added.Body.ToArray());
            Assert.Equal(channel == 101 ? 0u : ComputeAdler(bodies[channel - 101]), added.Extra);
            Assert.Equal(family.Chains.Single(chain => chain.Channel == channel).Records[0].Body,
                Assert.Single(records, record => record.Block == 1 && record.Channel == channel && record.Owner == owner).Body.ToArray());
        }
        Assert.Equal(replacement, Assert.Single(records, record => record is { Block: 0, Channel: 102, Owner: 5 }).Body.ToArray());

        Assert.Throws<InvalidDataException>(() => PartitionStreamEditor.Apply(raw, SampleTags, idWidth, [], [addition, addition]));
        Assert.Throws<InvalidDataException>(() => PartitionStreamEditor.Apply(raw, SampleTags, idWidth, [], [addition with { Owner = 5 }]));
        Assert.Throws<InvalidDataException>(() => PartitionStreamEditor.Apply(raw, SampleTags, idWidth, [], [addition with { Bodies = [bodies[0]] }]));
        Assert.Equal(snapshot, raw);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Apply_RemovesProjectOwnerAndPreservesMatchingFamilyOwner(int idWidth)
    {
        var project = MakeStandardBlock(idWidth, keyed: false, [(5UL, 20, [1, 2]), (6UL, 21, [4])],
            splitChannel102IntoTwoChains: true);
        var family = MakeStandardBlock(idWidth, keyed: true, [(5UL, 40, [3, 4])]);
        byte[] raw = BuildPartitionStream(idWidth, SampleTags, project, family);
        byte[] snapshot = [.. raw];
        byte[][] bodies = [TaggedBody(51, [5]), TaggedBody(52, [6]), TaggedBody(53, [7])];
        var replacement = new PartitionRecordEdit(0, 102, 6, TaggedBody(21, [8]));
        byte[] updated = PartitionStreamEditor.Apply(raw, SampleTags, idWidth, [replacement],
            [new(0, 7, bodies)], new HashSet<ulong> { 5 });

        var records = new List<PartitionFraming.Record>();
        Assert.True(PartitionFraming.Walk(updated, SampleTags, idWidth,
            record => records.Add(record with { Body = record.Body.ToArray() }), out string? failure, out _), failure);
        Assert.DoesNotContain(records, record => record.Block == 0 && record.Owner == 5);
        Assert.Equal(3, records.Count(record => record.Block == 1 && record.Owner == 5));
        Assert.Equal(3, records.Count(record => record.Block == 0 && record.Owner == 7));
        Assert.Equal(replacement.Body.ToArray(),
            Assert.Single(records, record => record is { Block: 0, Channel: 102, Owner: 6 }).Body.ToArray());
        Assert.Throws<InvalidDataException>(() => PartitionStreamEditor.Apply(raw, SampleTags, idWidth,
            [], removals: new HashSet<ulong> { 99 }));
        Assert.Throws<InvalidDataException>(() => PartitionStreamEditor.Apply(raw, SampleTags, idWidth,
            [replacement], removals: new HashSet<ulong> { 6 }));
        Assert.Equal(snapshot, raw);
    }

    [Fact]
    public void Apply_RefusesInvalidEditsAndDamagedInputs()
    {
        byte[] valid = BuildPartitionStream(
            4,
            SampleTags,
            MakeStandardBlock(4, keyed: false, [(5UL, 42, [1, 2, 3])]));
        byte[] validBody = TaggedBody(42, [9, 9]);

        // Invalid block
        Assert.Throws<ArgumentException>(() => PartitionStreamEditor.Apply(valid, SampleTags, 4, [new(1, 102, 5UL, validBody)]));
        // Invalid channel
        Assert.Throws<ArgumentException>(() => PartitionStreamEditor.Apply(valid, SampleTags, 4, [new(0, 104, 5UL, validBody)]));
        // Terminator owner (-1 for 4-byte IDs)
        Assert.Throws<ArgumentException>(() => PartitionStreamEditor.Apply(valid, SampleTags, 4, [new(0, 102, uint.MaxValue, validBody)]));
        // 64-bit owner on 4-byte ID stream
        Assert.Throws<ArgumentException>(() => PartitionStreamEditor.Apply(valid, SampleTags, 4, [new(0, 102, (ulong)uint.MaxValue + 10UL, validBody)]));
        // Tagless body (< 2 bytes)
        Assert.Throws<ArgumentException>(() => PartitionStreamEditor.Apply(valid, SampleTags, 4, [new(0, 102, 5UL, new byte[] { 0x01 })]));
        // Missing owner
        Assert.Throws<ArgumentException>(() => PartitionStreamEditor.Apply(valid, SampleTags, 4, [new(0, 102, 999UL, validBody)]));
        // Duplicate target
        Assert.Throws<ArgumentException>(() => PartitionStreamEditor.Apply(valid, SampleTags, 4, [new(0, 102, 5UL, validBody), new(0, 102, 5UL, validBody)]));

        // Corrupt page parity
        byte[] badParity = [.. valid];
        badParity[100] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => PartitionStreamEditor.Apply(badParity, SampleTags, 4, [new(0, 102, 5UL, validBody)]));

        // Mismatched input Adler-32 checksum
        var badCrcBlock = new SpecBlock(
            false,
            [
                new SpecChain(101, [new SpecRecord(5UL, TaggedBody(1, [1])), new SpecRecord(uint.MaxValue, [])]),
                new SpecChain(102, [new SpecRecord(5UL, TaggedBody(42, [1, 2]), CustomExtra: 0xDEAD_BEEFu), new SpecRecord(uint.MaxValue, [])]),
                new SpecChain(103, [new SpecRecord(5UL, TaggedBody(3, [1])), new SpecRecord(uint.MaxValue, [])]),
            ]);
        byte[] badAdlerStream = BuildPartitionStream(4, SampleTags, badCrcBlock);
        Assert.Throws<InvalidDataException>(() => PartitionStreamEditor.Apply(badAdlerStream, SampleTags, 4, [new(0, 102, 5UL, validBody)]));
    }

    private static void InspectBlockMetadata(
        byte[] strippedBody,
        out (uint FirstUpperFlags, uint FirstOpaque, uint SignatureBytes, uint SignatureChars) block0,
        out (uint FirstUpperFlags, uint FirstOpaque, uint SignatureBytes, uint SignatureChars) block1)
    {
        int pos = 8;
        block0 = ReadOneBlock(strippedBody, ref pos);
        block1 = ReadOneBlock(strippedBody, ref pos);
    }

    private static (uint FirstUpperFlags, uint FirstOpaque, uint SignatureBytes, uint SignatureChars) ReadOneBlock(byte[] body, ref int pos)
    {
        pos += 2; // ContentMarker
        uint ptr = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
        if (ptr != 0) pos += 2;
        pos += 4; // owner count
        if (ptr != 0) pos += 16;

        uint firstUpperFlags = 0, firstOpaque = 0;
        bool seenFirst = false;
        while (true)
        {
            pos += 2;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
            pos += 4;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
            pos += 4;
            if (size == 0) break;
            pos += 4; // channel
            uint opq = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
            if (!seenFirst)
            {
                firstUpperFlags = flags & ~3u;
                firstOpaque = opq;
                seenFirst = true;
            }
            pos += (int)size - 8 + 6;
        }
        pos += 2; // SignatureMarker
        uint sigBytes = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
        pos += 4 + (int)sigBytes;
        uint sigChars = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
        pos += 4 + (int)(2 * sigChars);
        return (firstUpperFlags, firstOpaque, sigBytes, sigChars);
    }
}
