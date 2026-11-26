using System.Buffers.Binary;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>One record replacement inside a single partition stream, addressed by block index (<c>0</c> = the project or family's own
/// element map, <c>&gt; 0</c> = a loaded-family keyed block), channel (<c>101..103</c>), and owner element identifier.</summary>
internal sealed record PartitionRecordEdit(int Block, int Channel, ulong Owner, ReadOnlyMemory<byte> Body);

internal sealed record PartitionOwnerAddition(int Partition, ulong Owner, byte[][] Bodies);

/// <summary>
/// Lossless editor of a stored <c>Partitions/&lt;n&gt;</c> stream (<see cref="PartitionFraming"/>).
///
/// Untouched blocks (including loaded-family keyed blocks) and untouched segment chains within an edited block are copied byte-for-byte
/// from the page-stripped stream, preserving their original deflate payloads, segment flags, opaque dwords, and block signatures.
/// Only segment chains affected by a record replacement, addition, or removal are rebuilt. Untouched records are copied byte-for-byte, edited records
/// receive updated <c>[size]...[size]</c> framing and channel 102/103 Adler-32 checksums, and the chain is emitted in 128 KB-bounded
/// segments (<c>flags &amp; 3</c> = <c>0</c> for batches up to 131,072 body bytes, or <c>2 -&gt; 3 -&gt; 1</c> slices for larger bodies) with the
/// upper flag bits and opaque dword of the original chain preserved. An edited block carries an empty <c>SignatureMarker</c> (<c>n = 0, m = 0</c>)
/// because its bytes are no longer signed by Revit.
///
/// Every output stream is sealed with <see cref="PageCodec.EncodePadded"/> and verified with both <see cref="PageCodec.Verify"/> and
/// <see cref="PartitionFraming.Walk"/> before returning: block count, record count, keys, channels, owners, Adler-32 checksums, and
/// every unedited record body must match the input byte-for-byte.
/// </summary>
internal static class PartitionStreamEditor
{
    /// <summary>Maximum record body bytes placed in one segment (matches Revit's 128 KB segment buffer).</summary>
    internal const int MaxSegmentBodyBytes = 131_072;

    /// <summary>Applies <paramref name="edits"/> to <paramref name="raw"/> without mutating the input array. Without edits, returns a
    /// copy of <paramref name="raw"/> once its parity and framing are verified.</summary>
    /// <exception cref="ArgumentException">An edit has an invalid block, channel, terminator owner, tagless body, duplicate target, or missing target.</exception>
    /// <exception cref="InvalidDataException">The input stream fails page parity or partition framing, or post-write verification detects any divergence.</exception>
    internal static byte[] Apply(byte[] raw, PartitionFraming.Tags tags, int idWidth, IReadOnlyList<PartitionRecordEdit> edits,
        IReadOnlyList<PartitionOwnerAddition>? additions = null, IReadOnlySet<ulong>? removals = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(edits);
        if (idWidth is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(idWidth), idWidth, "Element identifier byte width must be 4 or 8.");

        var pageCheck = PageCodec.Verify(raw);
        if (pageCheck.Status is PageCheckStatus.BadPage or PageCheckStatus.BadTail)
            throw new InvalidDataException($"Partition stream fails page parity at page {pageCheck.Page} ({pageCheck.Status}).");

        var before = new List<SnapshotRecord>();
        if (!PartitionFraming.Walk(raw, tags, idWidth, r => before.Add(SnapshotRecord.Capture(r)), out string? failure, out int blocks))
            throw new InvalidDataException($"Partition stream framing is invalid: {failure}.");

        ulong terminator = idWidth == 8 ? ulong.MaxValue : uint.MaxValue;
        VerifyChecksums(before, terminator);
        var editMap = ValidateEdits(before, blocks, idWidth, terminator, edits);
        additions ??= [];
        if (edits.Count == 0 && additions.Count == 0 && removals is not { Count: > 0 }) return [.. raw];
        if (removals is { Count: > 0 })
        {
            var requested = before.Where(record => record.Block == 0 && removals.Contains(record.Owner))
                .ToLookup(record => record.Owner);
            if (removals.Contains(terminator) || removals.Any(owner =>
                    !requested[owner].Select(record => record.Channel).Order().SequenceEqual([101, 102, 103]))
                || editMap.Keys.Any(key => key.Block == 0 && removals.Contains(key.Owner)))
                throw new InvalidDataException("Removing a project owner requires exactly one record in each channel and no replacement edit.");
        }
        var newRecords = new Dictionary<TargetKey, byte[]>();
        if (additions.Count != 0)
        {
            var local = before.Where(record => record.Block == 0).ToArray();
            if (local.GroupBy(record => record.Channel).Any(channel => channel.SkipLast(1).Any(record => record.Owner == terminator)))
                throw new InvalidDataException("A project channel has records after its terminator.");
            var owners = local.Select(record => record.Owner).ToHashSet();
            foreach (var addition in additions)
            {
                if (addition.Owner == 0 || addition.Owner >= terminator || !owners.Add(addition.Owner) || addition.Bodies.Length != 3
                    || addition.Bodies.Any(bytes => bytes.Length < sizeof(ushort)))
                    throw new InvalidDataException("An added project owner requires a distinct ID and three tagged records.");
                for (int channel = 101; channel <= 103; channel++)
                    newRecords.Add(new TargetKey(0, channel, addition.Owner), addition.Bodies[channel - 101]);
            }
        }

        byte[] body = PartitionFraming.RemoveTails(raw)
            ?? throw new InvalidDataException("Partition stream page tails could not be removed.");

        var editedBlocks = editMap.Keys.Select(k => k.Block).ToHashSet();
        if (newRecords.Count != 0 || removals is { Count: > 0 })
            editedBlocks.Add(0);
        using var rebuiltStream = new MemoryStream(body.Length);
        rebuiltStream.Write(body, 0, 8); // format 9, 0

        Span<byte> unsignedSig = stackalloc byte[10];
        unsignedSig.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(unsignedSig, tags.SignatureMarker);

        int pos = 8;
        for (int blockIndex = 0; ; blockIndex++)
        {
            int blockStart = pos;
            pos += 2; // ContentMarker
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0) pos += 2; // ContentKey class
            uint expectedOwners = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0) pos += 16; // 16-byte key

            if (expectedOwners == uint.MaxValue)
            {
                rebuiltStream.Write(body, blockStart, pos - blockStart);
                break;
            }

            int headerEnd = pos;
            var chains = ReadBlockChains(body, ref pos);
            int emptySegmentStart = pos - 18;

            pos += 2; // SignatureMarker
            uint sigBytes = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4 + checked((int)sigBytes);
            uint sigChars = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4 + checked((int)(2L * sigChars));
            int blockEnd = pos;

            if (!editedBlocks.Contains(blockIndex))
            {
                rebuiltStream.Write(body, blockStart, blockEnd - blockStart);
                continue;
            }

            var appendChains = new Dictionary<int, ChainSpan>();
            var removedHere = blockIndex == 0 ? removals : null;
            if (blockIndex == 0 && (additions.Count != 0 || removedHere is { Count: > 0 }))
            {
                if (pointer != 0 || chains.Select(chain => chain.Channel).Distinct().Count() != 3)
                    throw new InvalidDataException("Changing project owners requires all three channels in the local block.");
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(blockStart + sizeof(ushort) + sizeof(uint)),
                    checked(expectedOwners - (uint)(removedHere?.Count ?? 0) + (uint)additions.Count));
                if (additions.Count != 0)
                    foreach (var group in chains.GroupBy(chain => chain.Channel))
                        appendChains.Add(group.Key, group.Last());
            }
            rebuiltStream.Write(body, blockStart, headerEnd - blockStart);
            foreach (var chain in chains)
            {
                if (ReferenceEquals(appendChains.GetValueOrDefault(chain.Channel), chain))
                {
                    var appended = AppendOwners(chain, idWidth, terminator, additions);
                    WriteEditedChain(rebuiltStream, appended, blockIndex, tags, idWidth, editMap, removedHere);
                }
                else if (!ChainHasEdit(chain, blockIndex, idWidth, editMap, removedHere))
                {
                    rebuiltStream.Write(body, chain.SegmentStart, chain.SegmentEnd - chain.SegmentStart);
                }
                else
                    WriteEditedChain(rebuiltStream, chain, blockIndex, tags, idWidth, editMap, removedHere);
            }

            rebuiltStream.Write(body, emptySegmentStart, 18);
            rebuiltStream.Write(unsignedSig);
        }

        byte[] encoded = PageCodec.EncodePadded(rebuiltStream.ToArray());
        if (removals is { Count: > 0 })
            before.RemoveAll(record => record.Block == 0 && removals.Contains(record.Owner));
        VerifyOutput(encoded, tags, idWidth, terminator, blocks, before, editMap, newRecords);
        return encoded;
    }

    private static ChainSpan AppendOwners(ChainSpan chain, int idWidth, ulong terminator, IReadOnlyList<PartitionOwnerAddition> additions)
    {
        byte[] input = chain.Inflated;
        int sizeOffset = idWidth + (chain.Channel == 101 ? 0 : sizeof(uint));
        int at = 0;
        while (at < input.Length)
        {
            var record = input.AsSpan(at);
            ulong owner = idWidth == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(record) : BinaryPrimitives.ReadUInt32LittleEndian(record);
            if (owner == terminator)
            {
                if (record.Length != sizeOffset + 2 * sizeof(uint) || BinaryPrimitives.ReadUInt32LittleEndian(record[sizeOffset..]) != 0)
                    throw new InvalidDataException("Unexpected project channel terminator.");
                break;
            }
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record[sizeOffset..]));
            at += checked(sizeOffset + size + 2 * sizeof(uint));
        }

        using var buffer = new MemoryStream();
        buffer.Write(input.AsSpan(0, at));
        var header = new byte[20];
        var adler = new Adler32();
        foreach (var addition in additions)
        {
            byte[] record = addition.Bodies[chain.Channel - 101];
            int length = BuildRecordHeader(header, idWidth, chain.Channel, addition.Owner, record, adler);
            buffer.Write(header.AsSpan(0, length));
            buffer.Write(record);
            buffer.Write(header.AsSpan(length - sizeof(uint), sizeof(uint)));
        }
        buffer.Write(input.AsSpan(at));
        return chain with { Inflated = buffer.ToArray() };
    }

    private readonly record struct TargetKey(int Block, int Channel, ulong Owner);

    private readonly record struct SnapshotRecord(int Block, string Key, int Channel, ulong Owner, uint Extra, byte[] Body)
    {
        internal static SnapshotRecord Capture(PartitionFraming.Record record) =>
            new(record.Block, record.Key, record.Channel, record.Owner, record.Extra, record.Body.ToArray());
    }

    private sealed record ChainSpan(int SegmentStart, int SegmentEnd, int Channel, uint UpperFlags, uint FirstOpaque, byte[] Inflated);

    private static void VerifyChecksums(List<SnapshotRecord> records, ulong terminator)
    {
        var adler = new Adler32();
        foreach (var record in records)
        {
            if (record.Owner == terminator && record.Body.Length == 0) continue;
            if (record.Channel == 101) continue;
            adler.Reset();
            adler.Update(record.Body);
            if ((uint)adler.Value != record.Extra)
                throw new InvalidDataException($"Input record (block {record.Block}, channel {record.Channel}, owner {record.Owner}) has a mismatched Adler-32 checksum.");
        }
    }

    private static Dictionary<TargetKey, byte[]> ValidateEdits(
        List<SnapshotRecord> before,
        int blocks,
        int idWidth,
        ulong terminator,
        IReadOnlyList<PartitionRecordEdit> edits)
    {
        var counts = new Dictionary<TargetKey, int>();
        foreach (var record in before)
        {
            if (record.Owner == terminator && record.Body.Length == 0) continue;
            var key = new TargetKey(record.Block, record.Channel, record.Owner);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var map = new Dictionary<TargetKey, byte[]>(edits.Count);
        foreach (var edit in edits)
        {
            if (edit.Block < 0 || edit.Block >= blocks)
                throw new ArgumentException($"Block index {edit.Block} is outside the 0..{blocks - 1} range of this partition stream.", nameof(edits));
            if (edit.Channel is < 101 or > 103)
                throw new ArgumentException($"Channel {edit.Channel} is invalid; expected 101, 102, or 103.", nameof(edits));
            if (edit.Owner == terminator)
                throw new ArgumentException("The chain terminator record (-1) cannot be edited.", nameof(edits));
            if (idWidth == 4 && edit.Owner > uint.MaxValue)
                throw new ArgumentException($"Owner identifier {edit.Owner} exceeds 32-bit element identifier width.", nameof(edits));
            if (edit.Body.Length < 2)
                throw new ArgumentException($"Replacement body for owner {edit.Owner} on channel {edit.Channel} is shorter than the 2-byte class tag.", nameof(edits));

            var target = new TargetKey(edit.Block, edit.Channel, edit.Owner);
            if (!map.TryAdd(target, edit.Body.ToArray()))
                throw new ArgumentException($"Record (block {edit.Block}, channel {edit.Channel}, owner {edit.Owner}) is edited twice.", nameof(edits));

            int occurrences = counts.GetValueOrDefault(target);
            if (occurrences == 0)
                throw new ArgumentException($"Record (block {edit.Block}, channel {edit.Channel}, owner {edit.Owner}) does not exist in this partition stream.", nameof(edits));
            if (occurrences > 1)
                throw new ArgumentException($"Record (block {edit.Block}, channel {edit.Channel}, owner {edit.Owner}) occurs {occurrences} times and cannot be targeted unambiguously.", nameof(edits));
        }
        return map;
    }

    private static List<ChainSpan> ReadBlockChains(byte[] body, ref int pos)
    {
        var chains = new List<ChainSpan>();
        using var inflated = new MemoryStream();
        int chainStart = -1;
        uint firstFlags = 0, firstOpaque = 0;
        int channel = 0;

        while (true)
        {
            int segStart = pos;
            pos += 2; // SegmentMarker
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
            pos += 4; // count
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
            pos += 4; // unc
            if (size == 0) break;

            uint index = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
            uint opaque = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
            int payloadLength = checked((int)size - 8);
            InflateGzipMember(body, pos, payloadLength, inflated, resetFirst: (flags & 3) is 0 or 2);
            pos += payloadLength + 6; // SegmentCheckback + size

            uint mode = flags & 3;
            if (mode is 0 or 2)
            {
                chainStart = segStart;
                firstFlags = flags & ~3u;
                firstOpaque = opaque;
                channel = (int)index;
            }
            if (mode is 0 or 1)
                chains.Add(new ChainSpan(chainStart, pos, channel, firstFlags, firstOpaque, inflated.ToArray()));
        }
        return chains;
    }

    private static void InflateGzipMember(byte[] source, int offset, int length, MemoryStream destination, bool resetFirst)
    {
        if (resetFirst)
        {
            destination.SetLength(0);
            destination.Position = 0;
        }
        var inflater = new Inflater(noHeader: true);
        inflater.SetInput(source, offset + 10, length - 18);
        byte[] buffer = new byte[32_768];
        while (!inflater.IsFinished)
        {
            int count = inflater.Inflate(buffer);
            if (count == 0 && !inflater.IsFinished)
                throw new InvalidDataException("Unexpected end of deflate member while reading partition segment.");
            destination.Write(buffer, 0, count);
        }
    }

    private static bool ChainHasEdit(ChainSpan chain, int block, int idWidth, Dictionary<TargetKey, byte[]> editMap, IReadOnlySet<ulong>? removals)
    {
        ReadOnlySpan<byte> inf = chain.Inflated;
        int at = 0;
        while (at < inf.Length)
        {
            ulong owner = idWidth == 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(inf.Slice(at, 8))
                : BinaryPrimitives.ReadUInt32LittleEndian(inf.Slice(at, 4));
            at += idWidth + (chain.Channel != 101 ? 4 : 0);
            int bodySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(inf.Slice(at, 4)));
            at += 4 + bodySize + 4;
            if (removals?.Contains(owner) == true || editMap.ContainsKey(new TargetKey(block, chain.Channel, owner))) return true;
        }
        return false;
    }

    private static void WriteEditedChain(
        MemoryStream output,
        ChainSpan chain,
        int block,
        PartitionFraming.Tags tags,
        int idWidth,
        Dictionary<TargetKey, byte[]> editMap,
        IReadOnlySet<ulong>? removals)
    {
        var adler = new Adler32();
        using var batch = new MemoryStream();
        uint batchCount = 0;
        long batchBodyBytes = 0;

        void FlushBatch()
        {
            if (batchCount == 0) return;
            WriteSegment(output, tags, chain.UpperFlags, batchCount, checked((uint)batchBodyBytes), (uint)chain.Channel, chain.FirstOpaque, batch.ToArray());
            batch.SetLength(0);
            batch.Position = 0;
            batchCount = 0;
            batchBodyBytes = 0;
        }

        byte[] inf = chain.Inflated;
        byte[] headerBuffer = new byte[20];
        int at = 0;
        while (at < inf.Length)
        {
            int itemStart = at;
            ulong owner = idWidth == 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(inf.AsSpan(at, 8))
                : BinaryPrimitives.ReadUInt32LittleEndian(inf.AsSpan(at, 4));
            at += idWidth + (chain.Channel != 101 ? 4 : 0);
            int storedBodySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(inf.AsSpan(at, 4)));
            int storedBodyAt = at + 4;
            at = storedBodyAt + storedBodySize + 4;

            if (removals?.Contains(owner) == true)
                continue;
            if (!editMap.TryGetValue(new TargetKey(block, chain.Channel, owner), out byte[]? replacement))
            {
                int itemLength = at - itemStart;
                if (storedBodySize > MaxSegmentBodyBytes)
                {
                    FlushBatch();
                    WriteSplitRecord(output, tags, chain, idWidth, inf.AsSpan(itemStart, storedBodyAt - itemStart), inf.AsSpan(storedBodyAt, storedBodySize));
                }
                else
                {
                    if (batchCount > 0 && batch.Length + itemLength > MaxSegmentBodyBytes) FlushBatch();
                    batch.Write(inf, itemStart, itemLength);
                    batchCount++;
                    batchBodyBytes += storedBodySize;
                }
                continue;
            }

            int hdrLen = BuildRecordHeader(headerBuffer, idWidth, chain.Channel, owner, replacement, adler);
            Span<byte> footer = headerBuffer.AsSpan(hdrLen - 4, 4);
            int newItemLength = checked(hdrLen + replacement.Length + 4);
            if (replacement.Length > MaxSegmentBodyBytes)
            {
                FlushBatch();
                WriteSplitRecord(output, tags, chain, idWidth, headerBuffer.AsSpan(0, hdrLen), replacement);
            }
            else
            {
                if (batchCount > 0 && batch.Length + newItemLength > MaxSegmentBodyBytes) FlushBatch();
                batch.Write(headerBuffer, 0, hdrLen);
                batch.Write(replacement, 0, replacement.Length);
                batch.Write(footer);
                batchCount++;
                batchBodyBytes += replacement.Length;
            }
        }
        FlushBatch();
    }

    private static int BuildRecordHeader(byte[] buffer, int idWidth, int channel, ulong owner, byte[] body, Adler32 adler)
    {
        int pos = 0;
        if (idWidth == 8)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(0, 8), owner);
            pos = 8;
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)owner);
            pos = 4;
        }
        if (channel != 101)
        {
            adler.Reset();
            adler.Update(body);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos, 4), (uint)adler.Value);
            pos += 4;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos, 4), (uint)body.Length);
        return pos + 4;
    }

    private static void WriteSplitRecord(
        MemoryStream output,
        PartitionFraming.Tags tags,
        ChainSpan chain,
        int idWidth,
        ReadOnlySpan<byte> recordHeader,
        ReadOnlySpan<byte> recordBody)
    {
        _ = idWidth;
        // First segment (mode 2 = start of multi-segment chain): record header + first 128 KB of body.
        byte[] firstSlice = new byte[recordHeader.Length + MaxSegmentBodyBytes];
        recordHeader.CopyTo(firstSlice);
        recordBody[..MaxSegmentBodyBytes].CopyTo(firstSlice.AsSpan(recordHeader.Length));
        WriteSegment(output, tags, chain.UpperFlags | 2u, 1u, MaxSegmentBodyBytes, (uint)chain.Channel, chain.FirstOpaque, firstSlice);

        int offset = MaxSegmentBodyBytes;
        while (recordBody.Length - offset > MaxSegmentBodyBytes)
        {
            byte[] middleSlice = recordBody.Slice(offset, MaxSegmentBodyBytes).ToArray();
            WriteSegment(output, tags, chain.UpperFlags | 3u, 0u, MaxSegmentBodyBytes, (uint)chain.Channel, chain.FirstOpaque, middleSlice);
            offset += MaxSegmentBodyBytes;
        }

        // Closing segment (mode 1 = end of multi-segment chain): remaining 1..131,072 body bytes + 4-byte closing size.
        int remaining = recordBody.Length - offset;
        byte[] lastSlice = new byte[remaining + 4];
        recordBody[offset..].CopyTo(lastSlice);
        BinaryPrimitives.WriteUInt32LittleEndian(lastSlice.AsSpan(remaining, 4), (uint)recordBody.Length);
        WriteSegment(output, tags, chain.UpperFlags | 1u, 0u, (uint)remaining, (uint)chain.Channel, chain.FirstOpaque, lastSlice);
    }

    private static void WriteSegment(
        MemoryStream output,
        PartitionFraming.Tags tags,
        uint flags,
        uint recordCount,
        uint uncompressedBodyLength,
        uint channel,
        uint opaque,
        ReadOnlySpan<byte> inflatedBytes)
    {
        byte[] gzPayload = GzipMember.Write(inflatedBytes);
        uint segmentSize = checked((uint)(gzPayload.Length + 8));

        Span<byte> header = stackalloc byte[26];
        BinaryPrimitives.WriteUInt16LittleEndian(header[0..], tags.SegmentMarker);
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[6..], recordCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[10..], segmentSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[14..], uncompressedBodyLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[18..], channel);
        BinaryPrimitives.WriteUInt32LittleEndian(header[22..], opaque);
        output.Write(header);
        output.Write(gzPayload);

        Span<byte> footer = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(footer[0..], tags.SegmentCheckback);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[2..], segmentSize);
        output.Write(footer);
    }

    private static void VerifyOutput(
        byte[] encoded,
        PartitionFraming.Tags tags,
        int idWidth,
        ulong terminator,
        int expectedBlocks,
        List<SnapshotRecord> before,
        Dictionary<TargetKey, byte[]> editMap,
        Dictionary<TargetKey, byte[]> newRecords)
    {
        var pageCheck = PageCodec.Verify(encoded);
        if (pageCheck.Status != PageCheckStatus.Valid)
            throw new InvalidDataException($"Rewritten partition stream failed page parity ({pageCheck.Status} at page {pageCheck.Page}).");

        var after = new List<SnapshotRecord>(before.Count);
        if (!PartitionFraming.Walk(encoded, tags, idWidth, r => after.Add(SnapshotRecord.Capture(r)), out string? failure, out int actualBlocks))
            throw new InvalidDataException($"Rewritten partition stream failed framing verification: {failure}.");

        if (newRecords.Count != 0)
        {
            VerifyChecksums(after, terminator);
            after.RemoveAll(record =>
            {
                if (!newRecords.Remove(new TargetKey(record.Block, record.Channel, record.Owner), out byte[]? expected))
                    return false;
                if (!record.Body.AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException("An added project record differs from its verified body.");
                return true;
            });
            if (newRecords.Count != 0)
                throw new InvalidDataException("An added project record is missing after rewriting.");
        }
        if (actualBlocks != expectedBlocks)
            throw new InvalidDataException($"Block count changed from {expectedBlocks} to {actualBlocks}.");
        if (after.Count != before.Count)
            throw new InvalidDataException($"Record count changed from {before.Count} to {after.Count}.");

        var adler = new Adler32();
        for (int i = 0; i < before.Count; i++)
        {
            SnapshotRecord was = before[i], now = after[i];
            if (now.Block != was.Block || now.Key != was.Key || now.Channel != was.Channel || now.Owner != was.Owner)
                throw new InvalidDataException($"Record {i} header changed from ({was.Block}, '{was.Key}', {was.Channel}, {was.Owner}) to ({now.Block}, '{now.Key}', {now.Channel}, {now.Owner}).");

            bool isTerminator = was.Owner == terminator && was.Body.Length == 0;
            byte[] expectedBody = !isTerminator && editMap.TryGetValue(new TargetKey(was.Block, was.Channel, was.Owner), out byte[]? rep)
                ? rep
                : was.Body;

            if (!now.Body.AsSpan().SequenceEqual(expectedBody))
                throw new InvalidDataException($"Record (block {was.Block}, channel {was.Channel}, owner {was.Owner}) body differs from expected bytes.");

            if (isTerminator || now.Channel == 101)
            {
                if (now.Extra != (isTerminator ? was.Extra : 0u))
                    throw new InvalidDataException($"Record (block {was.Block}, channel {was.Channel}, owner {was.Owner}) carries unexpected extra value {now.Extra}.");
            }
            else
            {
                adler.Reset();
                adler.Update(expectedBody);
                if (now.Extra != (uint)adler.Value)
                    throw new InvalidDataException($"Record (block {was.Block}, channel {was.Channel}, owner {was.Owner}) carries invalid Adler-32 checksum.");
            }
        }
    }
}
