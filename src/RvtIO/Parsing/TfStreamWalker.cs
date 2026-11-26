using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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

    /// <summary>Called for every record ([eid][bodySize][0x053C or 0x04F8][body]) the walk accepts, with the segment and the record start.</summary>
    public delegate void RecordVisitor(byte[] segment, int recordStart, uint eid);

    private const int MaxBlock = 1_100_000; // > any real block/record size we accept
    private const int SegmentTarget = 1_400_000_000;
    // Freeform family symbols can exceed the anchored walk's MaxBlock.
    private const int MaxTargetBlock = 8_000_000;

    internal sealed class SegmentSources
    {
        private readonly List<(int Start, int End, byte[] Chunk)> _ranges = [];

        internal void SetSegment(IReadOnlyList<byte[]> chunks, int start, int end, int offset)
        {
            _ranges.Clear();
            int position = offset;
            for (int index = start; index < end; index++)
            {
                byte[] chunk = chunks[index];
                if (chunk.Length == 0) continue;
                _ranges.Add((position, position + chunk.Length, chunk));
                position += chunk.Length;
            }
        }

        // Deferred consumers only need the block. Holding the walk's contiguous buffer
        // would retain up to 1.4 GB for a single candidate.
        internal (byte[] Data, int Start) Retain(byte[] segment, int start, int length)
        {
            int low = 0, high = _ranges.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (_ranges[middle].End <= start) low = middle + 1;
                else high = middle;
            }
            if (low < _ranges.Count)
            {
                var range = _ranges[low];
                if (start >= range.Start && length <= range.End - start)
                    return (range.Chunk, start - range.Start);
            }
            return (segment.AsSpan(start, length).ToArray(), 0);
        }
    }

    /// <summary>Walks the concatenated chunks, reporting positions on the caller thread.</summary>
    public static void Walk(IReadOnlyList<byte[]> chunks, HashSet<uint> knownIds, BlockVisitor visit,
        RecordVisitor? visitRecord = null, Action<long, long>? progress = null, long segTarget = SegmentTarget,
        SegmentSources? sources = null)
    {
        if (segTarget <= 3 * MaxBlock) throw new ArgumentOutOfRangeException(nameof(segTarget));
        long total = 0;
        if (progress != null)
        {
            foreach (var chunk in chunks) total += chunk.Length;
            progress(0, total);
        }
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
            var seg = GC.AllocateUninitializedArray<byte>(checked((int)size));
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
            sources?.SetSegment(chunks, start, i, carry.Length);
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
        WalkWindow(buf, buf.Length, 0, visitLimit, ref aligned, knownIds, visit, visitRecord, reportPosition);
    }

    private static int WalkWindow(byte[] buf, int n, int pos, int visitLimit, ref bool aligned,
        HashSet<uint> knownIds, BlockVisitor visit, RecordVisitor? visitRecord, Action<int>? reportPosition = null)
    {
        const int reportInterval = 1 << 20;
        long nextReport = reportPosition is null ? long.MaxValue : reportInterval;
        while (pos < visitLimit && pos <= n - 12)
        {
            if (pos >= nextReport)
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
                pos = reportPosition == null ? NextPossibleItem(buf, n, pos + 1) : pos + 1;
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

    // Skip only positions where the serialized size and both record markers rule out a header.
    // The anchor and size-echo checks still run on every candidate, including unaligned ones.
    private static int NextPossibleItem(byte[] data, int length, int position)
    {
        if (position <= length - 16 && BitConverter.ToUInt64(data, position) == 0
            && BitConverter.ToUInt64(data, position + 8) == 0)
        {
            // No all-zero header can anchor. Keep starts whose header reaches the first nonzero byte.
            int nonzero = data.AsSpan(position + 16, length - position - 16).IndexOfAnyExcept((byte)0);
            if (nonzero < 0) return length - 15;
            position += nonzero + 1;
        }
        if (!BitConverter.IsLittleEndian || !Vector256.IsHardwareAccelerated) return position;
        int width = Vector256<byte>.Count;
        ref byte first = ref MemoryMarshal.GetArrayDataReference(data);
        for (; position <= length - 11 - width; position += width)
        {
            var low = Vector256.LoadUnsafe(ref first, (nuint)(position + 8));
            var high = Vector256.LoadUnsafe(ref first, (nuint)(position + 9));
            var middleSize = Vector256.LoadUnsafe(ref first, (nuint)(position + 10));
            var highSize = Vector256.LoadUnsafe(ref first, (nuint)(position + 11));
            var block = Vector256.LessThan(middleSize, Vector256.Create((byte)16)) & Vector256.Equals(highSize, Vector256<byte>.Zero);
            var r22 = Vector256.Equals(low, Vector256.Create((byte)0x3C)) & Vector256.Equals(high, Vector256.Create((byte)0x05));
            var r20 = Vector256.Equals(low, Vector256.Create((byte)0xF8)) & Vector256.Equals(high, Vector256.Create((byte)0x04));
            uint candidates = (block | r22 | r20).ExtractMostSignificantBits();
            if (candidates != 0) return position + BitOperations.TrailingZeroCount(candidates);
        }
        return position;
    }

    /// <summary>Targeted scan for GRep blocks (classIdx 0x07D7/0x07D6) of SPECIFIC element
    /// ids the anchored walk missed: family-SYMBOL regions can be unreachable when their
    /// ids never anchor. Vectorized: hunt the classIdx byte, then validate [eid in targets]
    /// [dataSize plausible] at the implied block start. The visitor's parser is the final
    /// validator (returns null on a false positive).</summary>
    public static void ScanBlocks(IReadOnlyList<byte[]> chunks, HashSet<uint> targetIds, BlockVisitor visit,
        SegmentSources? sources = null, int segmentTarget = SegmentTarget)
    {
        if (targetIds.Count == 0) return;
        int i = 0;
        var carry = Array.Empty<byte>();
        var currentGeometry = new List<int>();
        var earlierGeometry = new List<int>();
        var curves = new List<int>();
        while (i < chunks.Count)
        {
            long size = carry.Length;
            int start = i;
            while (i < chunks.Count && size + chunks[i].Length <= segmentTarget) { size += chunks[i].Length; i++; }
            if (start == i && i < chunks.Count) { size += chunks[i].Length; i++; }
            var seg = GC.AllocateUninitializedArray<byte>(checked((int)size));
            int off = carry.Length;
            Buffer.BlockCopy(carry, 0, seg, 0, carry.Length);
            for (int k = start; k < i; k++) { Buffer.BlockCopy(chunks[k], 0, seg, off, chunks[k].Length); off += chunks[k].Length; }
            bool last = i >= chunks.Count;
            int scanLimit = last ? seg.Length : Math.Max(0, seg.Length - MaxTargetBlock);
            sources?.SetSegment(chunks, start, i, carry.Length);
            ScanMarkers(seg, 12, Math.Min(scanLimit + 12, seg.Length - 1), Record);

            // Keep class priority and stream order within each class.
            Emit(currentGeometry);
            Emit(earlierGeometry);
            Emit(curves);
            if (!last)
            {
                int tail = Math.Min(MaxTargetBlock, seg.Length);
                carry = new byte[tail];
                Buffer.BlockCopy(seg, seg.Length - tail, carry, 0, tail);
            }

            void Record(int marker)
            {
                int blockStart = marker - 12;
                uint dataSize = BitConverter.ToUInt32(seg, blockStart + 8);
                if (dataSize < 20 || dataSize >= MaxTargetBlock || blockStart + 16L + dataSize > seg.Length) return;
                var matches = seg[marker] switch
                {
                    0xD7 => currentGeometry,
                    0xD6 => earlierGeometry,
                    _ => curves,
                };
                matches.Add(blockStart);
            }

            void Emit(List<int> matches)
            {
                foreach (int blockStart in matches)
                {
                    uint eid = BitConverter.ToUInt32(seg, blockStart);
                    if (targetIds.Contains(eid))
                        visit(seg, blockStart, BitConverter.ToUInt32(seg, blockStart + 8),
                            BitConverter.ToUInt16(seg, blockStart + 12), eid);
                }
                matches.Clear();
            }
        }
    }

    private static bool IsBlockMarker(byte low, byte high) =>
        high == 0x07 && low is 0xD7 or 0xD6 || high == 0x03 && low == 0x46;

    private static void ScanMarkers(byte[] data, int start, int end, Action<int> record)
    {
        int position = start;
        if (BitConverter.IsLittleEndian && Vector256.IsHardwareAccelerated)
        {
            int width = Vector256<byte>.Count;
            ref byte first = ref MemoryMarshal.GetArrayDataReference(data);
            for (; position <= end - width; position += width)
            {
                var low = Vector256.LoadUnsafe(ref first, (nuint)position);
                var high = Vector256.LoadUnsafe(ref first, (nuint)(position + 1));
                var geometry = Vector256.Equals(low & Vector256.Create((byte)0xFE), Vector256.Create((byte)0xD6))
                    & Vector256.Equals(high, Vector256.Create((byte)0x07));
                var curve = Vector256.Equals(low, Vector256.Create((byte)0x46))
                    & Vector256.Equals(high, Vector256.Create((byte)0x03));
                uint candidates = (geometry | curve).ExtractMostSignificantBits();
                while (candidates != 0)
                {
                    record(position + BitOperations.TrailingZeroCount(candidates));
                    candidates &= candidates - 1;
                }
            }
        }
        for (; position < end; position++)
            if (IsBlockMarker(data[position], data[position + 1])) record(position);
    }

    private static (bool IsBlock, int Size, uint Eid, ushort Cls, uint DataSize)? ParseItem(
        byte[] buf, int pos, int n, HashSet<uint> knownIds)
    {
        if (pos + 12 > n) return null;
        uint eid = BitConverter.ToUInt32(buf, pos);
        if (!ElementIdEncoding.IsSupported(eid) && eid != 0) return null;
        ushort magic = BitConverter.ToUInt16(buf, pos + 8);
        if (magic is 0x053C or 0x04F8)
        {
            uint bodySize = BitConverter.ToUInt32(buf, pos + 4);
            if (bodySize < 500_000 && pos + 12 + bodySize <= n && eid != 0 && knownIds.Contains(eid))
                return (false, 12 + (int)bodySize, eid, 0, 0);
        }
        if (pos + 16 > n) return null;
        uint ds = BitConverter.ToUInt32(buf, pos + 8);
        // Every block needs a size echo. An owner outside ElemTable (including loaded
        // family symbols) can anchor with that echo and a payload of at least eight bytes.
        if (ds >= 1_000_000 || pos + 16 + ds > n
            || BitConverter.ToUInt32(buf, pos + 12 + (int)ds) != ds
            || (ds < 8 && (eid == 0 || !knownIds.Contains(eid))))
            return null;
        ushort ci = BitConverter.ToUInt16(buf, pos + 12);
        return TfBlockDeserializer.Schemas.ContainsKey(ci) ? (true, 16 + (int)ds, eid, ci, ds) : null;
    }
}
