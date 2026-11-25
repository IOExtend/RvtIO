namespace RvtIO.Parsing;

/// <summary>
/// Anchored walker over the CONTINUOUS partition stream (chunks concatenated).
/// Blocks and records cross chunk boundaries, so per-chunk walking loses most blocks.
/// When unaligned, only anchor on an element id from the known-id set AND require the following item to parse too;
/// once aligned run free (any 0&lt;eid&lt;10M); on failure resync byte-by-byte. Records are skipped; blocks are validated by
/// classId in the schema table and dataSize bounds.
/// </summary>
internal static partial class TfStreamWalker
{
    public delegate void BlockVisitor(byte[] segment, int blockStart, uint dataSize, ushort classId, uint eid);

    internal delegate void LocatedBlockVisitor(byte[] segment, int blockStart, uint dataSize, ushort classId, uint eid,
        PartitionChunks.Block block);

    /// <summary>Called for every record ([eid][bodySize][0x053C or 0x04F8][body]) the walk accepts, with the segment and the record start.</summary>
    public delegate void RecordVisitor(byte[] segment, int recordStart, uint eid);

    private const int MaxBlock = 1_100_000; // > any real block/record size we accept
    private const long DefaultSegmentTarget = 1_400_000_000;

    /// <summary>Walks the concatenated chunks in segments of at most <paramref name="segTarget"/> bytes (tests pass a small one to
    /// reach the second segment). <paramref name="progress"/> gets the offset in the concatenated stream and its length.</summary>
    public static void Walk(IReadOnlyList<byte[]> chunks, HashSet<uint> knownIds, BlockVisitor visit,
        RecordVisitor? visitRecord = null, Action<long, long>? progress = null, long segTarget = DefaultSegmentTarget)
        => WalkLocated(chunks, knownIds, (data, start, size, cls, eid, _) => visit(data, start, size, cls, eid),
            visitRecord, progress, segTarget);

    internal static void WalkLocated(IReadOnlyList<byte[]> chunks, HashSet<uint> knownIds, LocatedBlockVisitor visit,
        RecordVisitor? visitRecord = null, Action<long, long>? progress = null, long segTarget = DefaultSegmentTarget)
    {
        // The carried overlap and two maximum-sized items must fit in a segment.
        if (segTarget <= 3 * MaxBlock) throw new ArgumentOutOfRangeException(nameof(segTarget));
        if (chunks is PartitionChunks { Store: not null } stored)
            WalkWindows(stored, knownIds, visit, visitRecord, progress, segTarget);
        else
            WalkResident(chunks, knownIds, (data, start, size, cls, eid) =>
                visit(data, start, size, cls, eid, new(data, start, checked((int)size + 16), null)),
                visitRecord, progress, segTarget);
    }

    private static void WalkResident(IReadOnlyList<byte[]> chunks, HashSet<uint> knownIds, BlockVisitor visit,
        RecordVisitor? visitRecord, Action<long, long>? progress, long segTarget)
    {
        long total = 0;
        foreach (var c in chunks) total += c.Length;
        progress?.Invoke(0, total);
        long segmentOffset = 0;
        Action<int>? reportPosition = progress is null ? null : pos => progress(segmentOffset + pos, total);

        int i = 0;
        var carry = Array.Empty<byte>(); // overlap tail from the previous segment
        while (i < chunks.Count)
        {
            long size = carry.Length;
            int start = i;
            while (i < chunks.Count && size + chunks[i].Length <= segTarget)
            {
                size += chunks[i].Length;
                i++;
            }
            if (start == i && i < chunks.Count) { size += chunks[i].Length; i++; } // oversized single chunk
            var seg = new byte[size];
            int off = carry.Length;
            Buffer.BlockCopy(carry, 0, seg, 0, carry.Length);
            for (int k = start; k < i; k++)
            {
                Buffer.BlockCopy(chunks[k], 0, seg, off, chunks[k].Length);
                off += chunks[k].Length;
            }
            bool last = i >= chunks.Count;
            // Blocks starting in the final MaxBlock bytes may be cut: the next segment
            // re-walks them via the carried overlap (skip them here unless last segment).
            int visitLimit = last ? seg.Length : Math.Max(0, seg.Length - MaxBlock);
            WalkSegment(seg, knownIds, visit, visitLimit, visitRecord, reportPosition);
            segmentOffset += visitLimit;
            if (!last)
            {
                int tail = Math.Min(MaxBlock, seg.Length);
                carry = new byte[tail];
                Buffer.BlockCopy(seg, seg.Length - tail, carry, 0, tail);
            }
        }
        if (total > 0) progress?.Invoke(total, total);
    }

    private static void WalkSegment(byte[] buf, HashSet<uint> knownIds, BlockVisitor visit,
        int visitLimit, RecordVisitor? visitRecord = null, Action<int>? reportPosition = null)
    {
        bool aligned = false;
        WalkWindow(buf, 0, ref aligned, knownIds, visit, visitLimit, visitRecord, reportPosition);
    }

    private static int WalkWindow(byte[] buf, int pos, ref bool aligned, HashSet<uint> knownIds, BlockVisitor visit,
        int visitLimit, RecordVisitor? visitRecord, Action<int>? reportPosition)
    {
        int n = buf.Length;
        const int reportInterval = 1 << 20;
        long nextReport = reportPosition is null ? long.MaxValue : pos + (long)reportInterval;
        while (pos < visitLimit && pos + 12 <= n)
        {
            if (pos >= nextReport && pos < visitLimit)
            {
                reportPosition!(pos);
                nextReport = (long)pos + reportInterval;
            }
            var it = ParseItem(buf, pos, n, knownIds);
            if (it.HasValue && !aligned)
            {
                int nxt = pos + it.Value.Size;
                if (nxt < n && ParseItem(buf, nxt, n, knownIds) == null) it = null;
            }
            if (!it.HasValue)
            {
                aligned = false;
                pos++;
                continue;
            }
            var (isBlock, size, eid, cls, dataSize) = it.Value;
            if (isBlock)
                visit(buf, pos, dataSize, cls, eid);
            else
                visitRecord?.Invoke(buf, pos, eid);
            pos += size;
            aligned = true;
        }
        return pos;
    }

    /// <summary>Targeted scan for GRep blocks (classIdx 0x07D7/0x07D6) of SPECIFIC element
    /// ids the anchored walk missed: family-SYMBOL regions can be unreachable when their
    /// ids never anchor. Vectorized: hunt the classIdx byte, then validate [eid in targets]
    /// [dataSize plausible] at the implied block start. The visitor's parser is the final
    /// validator (returns null on a false positive).</summary>
    public static void ScanBlocks(IReadOnlyList<byte[]> chunks, HashSet<uint> targetIds, BlockVisitor visit) =>
        ScanLocated(chunks, targetIds, (data, start, size, cls, eid, _) => visit(data, start, size, cls, eid));

    // Freeform family symbols (imported meshes) reach multi-MB blocks, far above the
    // anchored walk's MaxBlock. The id+classIdx match already gates false positives.
    private const int MaxTargetBlock = 8_000_000;

    internal static void ScanLocated(IReadOnlyList<byte[]> chunks, HashSet<uint> targetIds, LocatedBlockVisitor visit)
    {
        if (targetIds.Count == 0) return;
        if (chunks is PartitionChunks { Store: not null } stored)
            ScanWindows(stored, targetIds, visit, DefaultSegmentTarget, MaxTargetBlock);
        else
            ScanResident(chunks, targetIds, (data, start, size, cls, eid) =>
                visit(data, start, size, cls, eid, new(data, start, checked((int)size + 16), null)));
    }

    private static void ScanResident(IReadOnlyList<byte[]> chunks, HashSet<uint> targetIds, BlockVisitor visit)
    {
        const int maxTargetBlock = MaxTargetBlock;
        int i = 0;
        var carry = Array.Empty<byte>();
        while (i < chunks.Count)
        {
            long size = carry.Length;
            int start = i;
            while (i < chunks.Count && size + chunks[i].Length <= DefaultSegmentTarget) { size += chunks[i].Length; i++; }
            if (start == i && i < chunks.Count) { size += chunks[i].Length; i++; }
            var seg = new byte[size];
            int off = carry.Length;
            Buffer.BlockCopy(carry, 0, seg, 0, carry.Length);
            for (int k = start; k < i; k++) { Buffer.BlockCopy(chunks[k], 0, seg, off, chunks[k].Length); off += chunks[k].Length; }
            bool last = i >= chunks.Count;
            int scanLimit = last ? seg.Length : Math.Max(0, seg.Length - maxTargetBlock);

            foreach (byte marker in (ReadOnlySpan<byte>)[0xD7, 0xD6, 0x46])
                ScanWindow(seg, 0, scanLimit, marker, maxTargetBlock, targetIds, visit);
            if (!last)
            {
                int tail = Math.Min(maxTargetBlock, seg.Length);
                carry = new byte[tail];
                Buffer.BlockCopy(seg, seg.Length - tail, carry, 0, tail);
            }
        }
    }

    private static void ScanWindow(byte[] bytes, int start, int stop, byte marker, int maxBlock,
        HashSet<uint> targetIds, BlockVisitor visit)
    {
        byte hi = marker == 0x46 ? (byte)0x03 : (byte)0x07;
        int end = Math.Min(stop, bytes.Length - 13) + 12;
        var span = bytes.AsSpan();
        for (int p = start + 12; p < end; p++)
        {
            int rel = span[p..end].IndexOf(marker);
            if (rel < 0) break;
            p += rel;
            int bs = p - 12;
            if (span[p + 1] != hi) continue;
            uint eid = BitConverter.ToUInt32(span[bs..]);
            if (!targetIds.Contains(eid)) continue;
            uint ds = BitConverter.ToUInt32(span[(bs + 8)..]);
            if (ds >= 20 && ds < maxBlock && bs + 16L + ds <= bytes.Length)
                visit(bytes, bs, ds, BitConverter.ToUInt16(span[p..]), eid);
        }
    }

    private static (bool IsBlock, int Size, uint Eid, ushort Cls, uint DataSize)? ParseItem(
        byte[] buf, int pos, int n, HashSet<uint> knownIds)
    {
        if (pos + 12 > n) return null;
        uint eid = BitConverter.ToUInt32(buf, pos);
        if (!ElementIdEncoding.IsSupported(eid) && eid != 0) return null;
        // Ids outside the known set (family-SYMBOL elements live in the family doc's own
        // numbering and are absent from ElemTable) or above the plain-id ceiling may still
        // anchor, but only on a BLOCK whose 4-byte footer echoes dataSize (strong
        // signature; well-formed blocks always carry it). Without this, whole family-
        // symbol regions (door leafs, fixture bodies) were skipped byte-by-byte.
        bool needFooter = eid == 0 || !knownIds.Contains(eid);
        if (!needFooter)
        {
            ushort magic = BitConverter.ToUInt16(buf, pos + 8);
            uint bodySize = BitConverter.ToUInt32(buf, pos + 4);
            if ((magic == 0x053C || magic == 0x04F8) && bodySize < 500_000 && pos + 12 + bodySize <= n)
                return (false, 12 + (int)bodySize, eid, 0, 0);
        }
        if (pos + 16 > n) return null;
        uint ds = BitConverter.ToUInt32(buf, pos + 8);
        ushort ci = BitConverter.ToUInt16(buf, pos + 12);
        if (ds < 1_000_000 && pos + 16 + ds <= n && TfBlockDeserializer.Schemas.ContainsKey(ci))
        {
            // The footer echo is required for every block: an id of the known set alone let item-shaped bytes
            // inside another block pass as a block and hide the items behind them.
            if (BitConverter.ToUInt32(buf, pos + 12 + (int)ds) != ds || (needFooter && ds < 8))
                return null;
            return (true, 16 + (int)ds, eid, ci, ds);
        }
        return null;
    }
}
