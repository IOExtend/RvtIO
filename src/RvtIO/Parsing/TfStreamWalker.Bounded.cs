namespace RvtIO.Parsing;

internal static partial class TfStreamWalker
{
    // A physical window has enough lookahead to validate both the candidate and its
    // next item. Only the original logical segment boundaries reset alignment.
    internal static void WalkBounded(IReadOnlyList<byte[]> chunks, HashSet<uint> knownIds,
        BlockVisitor visit, RecordVisitor? visitRecord, SegmentSources sources,
        int segmentTarget = SegmentTarget, int windowSize = 64 * 1024 * 1024)
    {
        if (segmentTarget <= 3 * MaxBlock) throw new ArgumentOutOfRangeException(nameof(segmentTarget));
        if (windowSize <= 0) throw new ArgumentOutOfRangeException(nameof(windowSize));

        var offsets = new long[chunks.Count + 1];
        for (int i = 0; i < chunks.Count; i++) offsets[i + 1] = offsets[i] + chunks[i].Length;
        if (offsets[^1] == 0) return;
        const int lookahead = 2 * MaxBlock;
        var buffer = GC.AllocateUninitializedArray<byte>((int)Math.Min(offsets[^1], (long)windowSize + lookahead));

        long segmentStart = 0;
        int next = 0, source = 0;
        while (next < chunks.Count)
        {
            int first = next;
            while (next < chunks.Count && offsets[next + 1] - segmentStart <= segmentTarget) next++;
            if (next == first) next++;
            long segmentEnd = offsets[next];
            long visitEnd = next == chunks.Count ? segmentEnd : Math.Max(segmentStart, segmentEnd - MaxBlock);
            bool aligned = false;
            long resume = segmentStart;
            for (long start = segmentStart; start < visitEnd; start += windowSize)
            {
                long end = Math.Min(start + windowSize, visitEnd);
                if (resume >= end) continue;
                int length = (int)(Math.Min(end + lookahead, segmentEnd) - start);
                while (offsets[source + 1] <= start) source++;
                int offset = (int)(start - offsets[source]);
                int copied = 0, chunk = source;
                while (copied < length)
                {
                    ReadOnlySpan<byte> bytes = chunks[chunk++].AsSpan(offset);
                    int count = Math.Min(bytes.Length, length - copied);
                    bytes[..count].CopyTo(buffer.AsSpan(copied));
                    copied += count;
                    offset = 0;
                }
                sources.SetSegment(chunks, source, chunk, (int)(offsets[source] - start));
                resume = start + WalkWindow(buffer, length, (int)Math.Max(0, resume - start),
                    (int)(end - start), ref aligned, knownIds, visit, visitRecord);
            }
            segmentStart = visitEnd;
        }
    }
}
