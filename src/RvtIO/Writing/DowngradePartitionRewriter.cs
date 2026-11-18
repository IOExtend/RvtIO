using System.Buffers.Binary;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>
/// Rewrites a stored <c>Partitions/&lt;n&gt;</c> stream from a newer release into an older
/// release layout for cross-width downgrades (8-byte element ids to 4-byte, Revit 2024+
/// to 2020-2023): framing marker tags source to target, owner ids narrowed with range
/// check, record bodies transcoded by class and field name via <see cref="R22Transcoder"/>
/// (tags renumbered, fields cut/inserted, 64-bit scalars narrowed), Adler-32 recomputed,
/// block keys/pointer/owner counts and chain flags/opaque preserved, signatures emptied.
/// Owners whose data class the target table does not define are left out of their block
/// (Revit refuses an unknown class tag), and their ids are reported back so that the
/// element table can drop their rows too.
/// </summary>
internal static class DowngradePartitionRewriter
{
    internal const int MaxSegmentBodyBytes = 131_072;

    internal static byte[] Rewrite(
        byte[] rawStored,
        PartitionFraming.Tags sourceTags, int sourceWidth,
        PartitionFraming.Tags targetTags, int targetWidth,
        R22Transcoder transcoder,
        IReadOnlySet<ulong>? knownDropped,
        Func<byte[], byte[], byte[]?>? tagFixer,
        out IReadOnlySet<ulong> droppedOwners)
    {
        if (sourceWidth is not (4 or 8) || targetWidth is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "Element identifier byte width must be 4 or 8.");

        var pageCheck = PageCodec.Verify(rawStored);
        if (pageCheck.Status is PageCheckStatus.BadPage or PageCheckStatus.BadTail)
            throw new InvalidDataException($"Partition stream fails page parity at page {pageCheck.Page} ({pageCheck.Status}).");

        // Validate source framing first (names failure reasons for diagnostics).
        var probe = new List<PartitionFraming.Record>();
        if (!PartitionFraming.Walk(rawStored, sourceTags, sourceWidth, r => probe.Add(r), out string? probeFailure, out _))
            throw new InvalidDataException($"Partition stream framing is invalid: {probeFailure}.");

        byte[] body = PartitionFraming.RemoveTails(rawStored)
            ?? throw new InvalidDataException("Partition stream page tails could not be removed.");

        if (body.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4)) != 9)
            throw new InvalidDataException("Partition stream has no format-9 header.");

        using var rebuilt = new MemoryStream(body.Length);
        rebuilt.Write(body, 0, 8);

        ulong sourceTerminator = sourceWidth == 8 ? ulong.MaxValue : uint.MaxValue;
        ulong targetTerminator = targetWidth == 8 ? ulong.MaxValue : uint.MaxValue;
        var allDropped = knownDropped is HashSet<ulong> known ? known : knownDropped is null ? [] : new HashSet<ulong>(knownDropped);

        int pos = 8;
        for (; ; )
        {
            int blockStart = pos;
            if (pos + 2 > body.Length) throw new InvalidDataException("Partition stream ends inside a block header.");
            ushort marker = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2));
            if (marker != sourceTags.ContentMarker) throw new InvalidDataException("Partition stream has no content-marker.");
            pos += 2;
            if (pos + 4 > body.Length) throw new InvalidDataException("Partition stream ends inside a block header.");
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0)
            {
                if (pos + 2 > body.Length) throw new InvalidDataException("Partition stream ends inside a block header.");
                if (BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2)) != sourceTags.ContentKey)
                    throw new InvalidDataException("Partition stream has no content-key class.");
                pos += 2;
            }
            if (pos + 4 > body.Length) throw new InvalidDataException("Partition stream ends inside a block header.");
            uint expectedOwners = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            byte[]? key = null;
            if (pointer != 0)
            {
                if (pos + 16 > body.Length) throw new InvalidDataException("Partition stream ends inside a block key.");
                key = body.AsSpan(pos, 16).ToArray();
                pos += 16;
            }

            if (expectedOwners == uint.MaxValue)
            {
                // Terminal: rewrite marker with target tag, copy pointer/owner count verbatim.
                var term = new byte[2 + 4 + 4];
                BinaryPrimitives.WriteUInt16LittleEndian(term.AsSpan(0, 2), targetTags.ContentMarker);
                BinaryPrimitives.WriteUInt32LittleEndian(term.AsSpan(2, 4), pointer);
                BinaryPrimitives.WriteUInt32LittleEndian(term.AsSpan(6, 4), expectedOwners);
                rebuilt.Write(term);
                break;
            }

            var chains = ReadBlockChains(body, ref pos, sourceTags);
            int emptySegmentStart = pos - 18;

            // Signature: skip source, write empty target signature.
            if (pos + 2 > body.Length) throw new InvalidDataException("Partition stream ends inside a signature.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2)) != sourceTags.SignatureMarker)
                throw new InvalidDataException("Partition stream has no signature-marker.");
            pos += 2;
            if (pos + 4 > body.Length) throw new InvalidDataException("Partition stream ends inside a signature.");
            uint sigBytes = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4 + checked((int)sigBytes);
            if (pos + 4 > body.Length) throw new InvalidDataException("Partition stream ends inside a signature.");
            uint sigChars = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4 + checked((int)(2L * sigChars));

            // Owners whose data class the target table does not define, or whose payload cannot be
            // rewritten in the target grammar, are left out of the block: Revit refuses an unknown
            // class tag and a record body left as 0xFFFE ("can't find PersistentClass"), so the
            // element's header, data and geometry records all go and the block owner count follows.
            // A payload that only fails once the element universe is set (dangling-id rule)
            // is also left out: the known set was built before the universe existed.
            var freshDropped = DetectDroppedOwners(chains, transcoder, sourceTerminator, sourceWidth);
            HashSet<ulong> dropOwners;
            if (knownDropped is null) dropOwners = freshDropped;
            else { dropOwners = new HashSet<ulong>(knownDropped); dropOwners.UnionWith(freshDropped); }
            uint writeOwners = expectedOwners;
            if (dropOwners.Count > 0)
            {
                allDropped.UnionWith(dropOwners);
                var kept = new HashSet<ulong>();
                foreach (var chain in chains)
                    if (chain.Channel == 101)
                        foreach (var (owner, _) in ReadRecords(chain.Inflated, 101, sourceWidth))
                            if (owner != sourceTerminator && !dropOwners.Contains(owner)) kept.Add(owner);
                writeOwners = (uint)kept.Count;
            }

            // Write block header with target tags, the surviving owner count, pointer and key unchanged.
            {
                var header = new MemoryStream();
                var tag = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(tag, targetTags.ContentMarker);
                header.Write(tag);
                var ptr = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(ptr, pointer);
                header.Write(ptr);
                if (pointer != 0)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(tag, targetTags.ContentKey);
                    header.Write(tag);
                }
                var own = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(own, writeOwners);
                header.Write(own);
                if (key != null) header.Write(key);
                rebuilt.Write(header.ToArray());
            }

            foreach (var chain in chains)
                WriteConvertedChain(rebuilt, chain, targetTags, sourceWidth, targetWidth, transcoder, sourceTerminator, targetTerminator, dropOwners, tagFixer);

            // Empty segment closing the block.
            {
                var empty = new byte[18];
                BinaryPrimitives.WriteUInt16LittleEndian(empty.AsSpan(0, 2), targetTags.SegmentMarker);
                rebuilt.Write(empty);
            }
            // Empty signature (bytes no longer signed by Revit).
            {
                var sig = new byte[2 + 4 + 4];
                BinaryPrimitives.WriteUInt16LittleEndian(sig.AsSpan(0, 2), targetTags.SignatureMarker);
                rebuilt.Write(sig);
            }

            _ = emptySegmentStart;
            _ = blockStart;
        }

        byte[] encoded = PageCodec.EncodePadded(rebuilt.ToArray());

        // Verify with target framing, widths and checksums.
        var pageVerify = PageCodec.Verify(encoded);
        if (pageVerify.Status != PageCheckStatus.Valid)
            throw new InvalidDataException($"Rewritten partition stream failed page parity ({pageVerify.Status} at page {pageVerify.Page}).");
        var adler = new Adler32();
        bool ok = PartitionFraming.Walk(encoded, targetTags, targetWidth, record =>
        {
            if (record.Owner == targetTerminator && record.Body.Length == 0) return;
            if (record.Channel == 101) return;
            adler.Reset();
            adler.Update(record.Body.ToArray());
            if ((uint)adler.Value != record.Extra)
                throw new InvalidDataException($"Rewritten record (block {record.Block}, channel {record.Channel}, owner {record.Owner}) has a mismatched Adler-32 checksum.");
        }, out string? failure, out _);
        if (!ok) throw new InvalidDataException($"Rewritten partition stream failed framing verification: {failure}.");
        droppedOwners = allDropped;
        return encoded;
    }

    /// <summary>The owners whose records cannot be carried into the target: their class has no
    /// counterpart, or a data or geometry payload cannot be rewritten in the target grammar.</summary>
    private static HashSet<ulong> DetectDroppedOwners(List<ChainSpan> chains, R22Transcoder transcoder, ulong sourceTerminator, int sourceWidth)
    {
        var dropOwners = new HashSet<ulong>();
        var forcedSpecs = Environment.GetEnvironmentVariable("RVTIO_FORCE_DROP_CLASSES")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        bool ForceDrop(string? className, ulong owner)
        {
            if (forcedSpecs is null || forcedSpecs.Length == 0) return false;
            if (className is null) return false;
            foreach (var spec in forcedSpecs)
            {
                if (spec.Equals(className, StringComparison.Ordinal)) return true;
                if (spec.StartsWith(className + ">=", StringComparison.Ordinal)
                    && ulong.TryParse(spec.AsSpan(className.Length + 2), out ulong ge) && owner >= ge) return true;
                if (spec.StartsWith(className + "<=", StringComparison.Ordinal)
                    && ulong.TryParse(spec.AsSpan(className.Length + 2), out ulong le) && owner <= le) return true;
                if (spec.StartsWith(className + ">", StringComparison.Ordinal)
                    && ulong.TryParse(spec.AsSpan(className.Length + 1), out ulong gt) && owner > gt) return true;
                if (spec.StartsWith(className + "<", StringComparison.Ordinal)
                    && ulong.TryParse(spec.AsSpan(className.Length + 1), out ulong lt) && owner < lt) return true;
                if (spec.StartsWith(className + "=", StringComparison.Ordinal)
                    && ulong.TryParse(spec.AsSpan(className.Length + 1), out ulong eq) && owner == eq) return true;
                int colon = spec.IndexOf(':');
                if (colon > 0 && spec.AsSpan(0, colon).Equals(className, StringComparison.Ordinal))
                {
                    var range = spec.AsSpan(colon + 1);
                    int dash = range.IndexOf('-');
                    if (dash > 0 && ulong.TryParse(range[..dash], out ulong lo) && ulong.TryParse(range[(dash + 1)..], out ulong hi)
                        && owner >= lo && owner <= hi) return true;
                }
            }
            return false;
        }
        foreach (var chain in chains)
        {
            if (chain.Channel is not (102 or 103)) continue;
            foreach (var (owner, recordBody) in ReadRecords(chain.Inflated, chain.Channel, sourceWidth))
            {
                if (owner == sourceTerminator || recordBody.Length < 2 || dropOwners.Contains(owner)) continue;
                int classTag = BinaryPrimitives.ReadUInt16LittleEndian(recordBody.Span);
                if (chain.Channel == 102 && ForceDrop(transcoder.SourceClassName(classTag), owner))
                {
                    dropOwners.Add(owner);
                    continue;
                }
                if (!transcoder.HasSourceClass(classTag))
                {
                    // A data record must name a class of the file; an unknown geometry tag is
                    // opaque bytes of a synthetic fixture and keeps the old copy-through.
                    if (chain.Channel == 102) dropOwners.Add(owner);
                    continue;
                }
                if (transcoder.TargetId(classTag) == 0)
                {
                    dropOwners.Add(owner);
                    continue;
                }
                var rewritten = recordBody.ToArray();
                if (transcoder.Payload(rewritten, 0, rewritten.Length, out _) is null) dropOwners.Add(owner);
            }
        }
        return dropOwners;
    }

    /// <summary>The owners a stored stream holds and the ones a rewrite would leave out.</summary>
    internal sealed record PartitionState(HashSet<ulong> Owners, HashSet<ulong> Dropped);

    /// <summary>
    /// The element ids a stored stream holds (the channel-101 owners) and the ids the rewrite would
    /// leave out, without rewriting it. The writer uses the two sets to know which references in the
    /// written file would point at an element that is gone.
    /// </summary>
    internal static PartitionState Inspect(
        byte[] rawStored, PartitionFraming.Tags sourceTags, int sourceWidth, R22Transcoder transcoder)
    {
        byte[] body = PartitionFraming.RemoveTails(rawStored)
            ?? throw new InvalidDataException("Partition stream page tails could not be removed.");
        if (body.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4)) != 9)
            throw new InvalidDataException("Partition stream has no format-9 header.");

        ulong sourceTerminator = sourceWidth == 8 ? ulong.MaxValue : uint.MaxValue;
        var owners = new HashSet<ulong>();
        var dropped = new HashSet<ulong>();
        int pos = 8;
        for (; ; )
        {
            if (pos + 10 > body.Length) throw new InvalidDataException("Partition stream ends inside a block header.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2)) != sourceTags.ContentMarker)
                throw new InvalidDataException("Partition stream has no content-marker.");
            pos += 2;
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0)
            {
                if (pos + 2 > body.Length || BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2)) != sourceTags.ContentKey)
                    throw new InvalidDataException("Partition stream has no content-key class.");
                pos += 2;
            }
            if (pos + 4 > body.Length) throw new InvalidDataException("Partition stream ends inside a block header.");
            uint ownerCount = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0)
            {
                if (pos + 16 > body.Length) throw new InvalidDataException("Partition stream ends inside a block key.");
                pos += 16;
            }
            if (ownerCount == uint.MaxValue) break;

            var chains = ReadBlockChains(body, ref pos, sourceTags);
            foreach (var chain in chains)
            {
                if (chain.Channel is not (101 or 102 or 103)) continue;
                foreach (var (owner, _) in ReadRecords(chain.Inflated, chain.Channel, sourceWidth))
                    if (owner != sourceTerminator) owners.Add(owner);
            }
            dropped.UnionWith(DetectDroppedOwners(chains, transcoder, sourceTerminator, sourceWidth));

            // Signature: skip it as the rewrite does.
            if (pos + 2 > body.Length || BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2)) != sourceTags.SignatureMarker)
                throw new InvalidDataException("Partition stream has no signature-marker.");
            pos += 2;
            if (pos + 4 > body.Length) throw new InvalidDataException("Partition stream ends inside a signature.");
            pos += 4 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)));
            if (pos + 4 > body.Length) throw new InvalidDataException("Partition stream ends inside a signature.");
            pos += 4 + checked((int)(2L * BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4))));
        }
        return new PartitionState(owners, dropped);
    }

    private sealed record ChainSpan(int Channel, uint UpperFlags, uint Opaque, byte[] Inflated);

    /// <summary>The records of an inflated chain: owner and body, in stored order.</summary>
    private static IEnumerable<(ulong Owner, ReadOnlyMemory<byte> Body)> ReadRecords(byte[] inflated, int channel, int width)
    {
        int at = 0;
        while (at + width + 4 <= inflated.Length)
        {
            ulong owner = width == 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(inflated.AsSpan(at, 8))
                : BinaryPrimitives.ReadUInt32LittleEndian(inflated.AsSpan(at, 4));
            at += width + (channel != 101 ? 4 : 0);
            if (at + 4 > inflated.Length) yield break;
            int size = BinaryPrimitives.ReadUInt32LittleEndian(inflated.AsSpan(at, 4)) is var raw && raw <= int.MaxValue ? (int)raw : -1;
            if (size < 0) yield break;
            at += 4;
            if (at + (long)size + 4 > inflated.Length) yield break;
            yield return (owner, new ReadOnlyMemory<byte>(inflated, at, size));
            at += size + 4;
        }
    }

    private static List<ChainSpan> ReadBlockChains(byte[] body, ref int pos, PartitionFraming.Tags tags)
    {
        var chains = new List<ChainSpan>();
        using var inflated = new MemoryStream();
        int channel = 0;
        uint firstFlags = 0, firstOpaque = 0;
        bool inChain = false;

        while (true)
        {
            if (pos + 2 > body.Length) throw new InvalidDataException("Partition stream ends inside a segment.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2)) != tags.SegmentMarker)
                throw new InvalidDataException("Partition stream has no segment-marker.");
            pos += 2;
            if (pos + 4 + 4 + 4 + 4 > body.Length) throw new InvalidDataException("Partition stream ends inside a segment header.");
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
            pos += 4; // count (verified on rewrite, not needed for parsing)
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
            pos += 4; // unc
            if (size == 0)
            {
                if ((flags & 3) != 0) throw new InvalidDataException("Partition stream empty segment is chained.");
                break;
            }
            if (pos + 8 > body.Length) throw new InvalidDataException("Partition stream ends inside a segment header.");
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
            uint opaque = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
            int payloadLength = checked((int)size - 8);
            if (pos + payloadLength + 6 > body.Length) throw new InvalidDataException("Partition stream ends inside a segment.");
            InflateGzipMember(body, pos, payloadLength, inflated, resetFirst: (flags & 3) is 0 or 2);
            pos += payloadLength + 6; // SegmentCheckback + size (validated on rewrite)

            uint mode = flags & 3;
            if (mode is 0 or 2)
            {
                if (inChain) throw new InvalidDataException("Partition stream chain starts inside a chain.");
                inChain = true;
                firstFlags = flags & ~3u;
                firstOpaque = opaque;
                channel = (int)index;
            }
            else if (!inChain) throw new InvalidDataException("Partition stream chain continuation starts no chain.");
            if (mode is 0 or 1)
            {
                inChain = false;
                chains.Add(new ChainSpan(channel, firstFlags, firstOpaque, inflated.ToArray()));
            }
        }
        if (inChain) throw new InvalidDataException("Partition stream ends inside a chain.");
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

    private static void WriteConvertedChain(
        MemoryStream output,
        ChainSpan chain,
        PartitionFraming.Tags targetTags,
        int sourceWidth,
        int targetWidth,
        R22Transcoder transcoder,
        ulong sourceTerminator,
        ulong targetTerminator,
        IReadOnlySet<ulong> dropOwners,
        Func<byte[], byte[], byte[]?>? tagFixer)
    {
        var adler = new Adler32();
        using var batch = new MemoryStream();
        uint batchCount = 0;
        long batchBodyBytes = 0;

        void FlushBatch()
        {
            if (batchCount == 0) return;
            WriteSegment(output, targetTags, chain.UpperFlags, batchCount, checked((uint)batchBodyBytes), (uint)chain.Channel, chain.Opaque, batch.ToArray());
            batch.SetLength(0);
            batch.Position = 0;
            batchCount = 0;
            batchBodyBytes = 0;
        }

        byte[] inf = chain.Inflated;
        int at = 0;
        while (at < inf.Length)
        {
            // The owner is written in the width of the whole stream, source and target alike.
            if (at + sourceWidth > inf.Length) throw new InvalidDataException("Partition record ends inside its owner.");
            ulong owner = sourceWidth == 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(inf.AsSpan(at, 8))
                : BinaryPrimitives.ReadUInt32LittleEndian(inf.AsSpan(at, 4));
            at += sourceWidth;
            uint extra = 0;
            bool hasExtra = chain.Channel != 101;
            if (hasExtra)
            {
                if (at + 4 > inf.Length) throw new InvalidDataException("Partition record ends inside its checksum.");
                extra = BinaryPrimitives.ReadUInt32LittleEndian(inf.AsSpan(at, 4));
                at += 4;
            }
            if (at + 4 > inf.Length) throw new InvalidDataException("Partition record ends inside its length.");
            int bodySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(inf.AsSpan(at, 4)));
            int bodyAt = at + 4;
            if (bodyAt + bodySize + 4 > inf.Length) throw new InvalidDataException("Partition record ends inside its body.");
            byte[] body = new byte[bodySize];
            Buffer.BlockCopy(inf, bodyAt, body, 0, bodySize);
            at = bodyAt + bodySize + 4;
            _ = extra;

            bool isTerminator = owner == sourceTerminator && bodySize == 0;
            ulong newOwner;
            byte[] newBody;
            uint newExtra;
            if (isTerminator)
            {
                newOwner = targetTerminator;
                newBody = [];
                newExtra = 0;
            }
            else if (dropOwners.Contains(owner))
            {
                // The element's class has no counterpart in the target table: leave the record out.
                continue;
            }
            else
            {
                if (targetWidth == 4 && owner > uint.MaxValue)
                    throw new DowngradeRefusedException("id-beyond-32-bits",
                        $"Element id {owner} does not fit in 32 bits and cannot be downgraded to a 4-byte release.");
                newOwner = owner;
                byte[]? converted = bodySize >= 2 ? transcoder.Payload(body, 0, bodySize, out _) : null;
                if (converted is not null && tagFixer is not null && tagFixer(body, converted) is { } fixedBody)
                    converted = fixedBody;
                if (converted == null)
                {
                    // A body that cannot be rewritten on its own (an unknown nested structure) keeps its bytes
                    // behind an unknown class tag; the element stays framed and is left out by readers.
                    converted = new byte[bodySize >= 2 ? bodySize : 2];
                    if (bodySize >= 2) Buffer.BlockCopy(body, 2, converted, 2, bodySize - 2);
                    BinaryPrimitives.WriteUInt16LittleEndian(converted.AsSpan(0, 2), R22Transcoder.UnknownTag);
                }
                newBody = converted;
                if (chain.Channel == 101) newExtra = 0;
                else
                {
                    adler.Reset();
                    adler.Update(newBody);
                    newExtra = (uint)adler.Value;
                }
            }

            byte[] header = new byte[targetWidth + (hasExtra ? 4 : 0) + 4];
            int hp = 0;
            if (targetWidth == 8) { BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(hp, 8), newOwner); hp += 8; }
            else { BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(hp, 4), (uint)newOwner); hp += 4; }
            if (hasExtra) { BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(hp, 4), newExtra); hp += 4; }
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(hp, 4), (uint)newBody.Length);
            byte[] footer = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(footer, (uint)newBody.Length);

            int itemLength = header.Length + newBody.Length + 4;
            if (newBody.Length > MaxSegmentBodyBytes)
            {
                FlushBatch();
                WriteSplitRecord(output, targetTags, chain, header, newBody);
            }
            else
            {
                if (batchCount > 0 && batch.Length + itemLength > MaxSegmentBodyBytes) FlushBatch();
                batch.Write(header);
                batch.Write(newBody);
                batch.Write(footer);
                batchCount++;
                batchBodyBytes += newBody.Length;
            }
        }
        FlushBatch();
    }

    private static int DetectSourceWidth(ChainSpan chain, ulong sourceTerminator) => sourceTerminator == ulong.MaxValue ? 8 : 4;

    private static void WriteSplitRecord(
        MemoryStream output,
        PartitionFraming.Tags tags,
        ChainSpan chain,
        ReadOnlySpan<byte> recordHeader,
        ReadOnlySpan<byte> recordBody)
    {
        byte[] firstSlice = new byte[recordHeader.Length + MaxSegmentBodyBytes];
        recordHeader.CopyTo(firstSlice);
        recordBody[..MaxSegmentBodyBytes].CopyTo(firstSlice.AsSpan(recordHeader.Length));
        WriteSegment(output, tags, chain.UpperFlags | 2u, 1u, MaxSegmentBodyBytes, (uint)chain.Channel, chain.Opaque, firstSlice);

        int offset = MaxSegmentBodyBytes;
        while (recordBody.Length - offset > MaxSegmentBodyBytes)
        {
            byte[] middleSlice = recordBody.Slice(offset, MaxSegmentBodyBytes).ToArray();
            WriteSegment(output, tags, chain.UpperFlags | 3u, 0u, MaxSegmentBodyBytes, (uint)chain.Channel, chain.Opaque, middleSlice);
            offset += MaxSegmentBodyBytes;
        }

        int remaining = recordBody.Length - offset;
        byte[] lastSlice = new byte[remaining + 4];
        recordBody[offset..].CopyTo(lastSlice);
        BinaryPrimitives.WriteUInt32LittleEndian(lastSlice.AsSpan(remaining, 4), (uint)recordBody.Length);
        WriteSegment(output, tags, chain.UpperFlags | 1u, 0u, (uint)remaining, (uint)chain.Channel, chain.Opaque, lastSlice);
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
}
