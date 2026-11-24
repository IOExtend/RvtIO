namespace RvtIO.Parsing;

internal static partial class TfStreamWalker
{
    private const int WindowStep = 16 * 1024 * 1024;

    private static void WalkWindows(PartitionChunks chunks, HashSet<uint> knownIds, BlockVisitor visit,
        RecordVisitor? visitRecord, Action<long, long>? progress, long segmentTarget)
    {
        var source = new WindowSource(chunks);
        progress?.Invoke(0, source.Length);
        foreach (var (start, length, limit) in source.Segments(segmentTarget, MaxBlock))
        {
            bool aligned = false;
            int position = 0;
            while (position < limit && position + 12L <= length)
            {
                int stop = (int)Math.Min((long)position + WindowStep, limit);
                int end = (int)Math.Min((long)stop + 2 * MaxBlock, length);
                int from = Math.Max(0, position - 16);
                var bytes = source.Read(start + from, end - from);
                Action<int>? report = progress == null ? null : offset => progress(start + from + offset, source.Length);
                position = from + WalkWindow(bytes, position - from, ref aligned, knownIds, visit, stop - from, visitRecord, report);
            }
        }
        if (source.Length > 0) progress?.Invoke(source.Length, source.Length);
    }

    private static void ScanWindows(PartitionChunks chunks, HashSet<uint> targetIds, BlockVisitor visit,
        long segmentTarget, int maxBlock)
    {
        var source = new WindowSource(chunks);
        // Preserve the marker order of the concatenated segment, also when it spans several windows.
        foreach (var (start, length, limit) in source.Segments(segmentTarget, maxBlock))
            foreach (byte marker in new byte[] { 0xD7, 0xD6, 0x46 })
                for (int position = 0; position < limit;)
                {
                    int stop = (int)Math.Min((long)position + WindowStep, limit);
                    int end = (int)Math.Min((long)stop + maxBlock + 16, length);
                    var bytes = source.Read(start + position, end - position);
                    ScanWindow(bytes, 0, stop - position, marker, maxBlock, targetIds, visit);
                    position = stop;
                }
    }

    private sealed class WindowSource
    {
        private readonly PartitionChunks chunks;
        private readonly long[] starts;

        internal WindowSource(PartitionChunks chunks)
        {
            this.chunks = chunks;
            starts = new long[chunks.Count + 1];
            for (int i = 0; i < chunks.Count; i++) starts[i + 1] = starts[i] + PartitionChunks.LengthAt(chunks, i);
        }

        internal long Length => starts[^1];

        // Use the same logical segment and overlap boundaries as the in-memory scanner.
        internal IEnumerable<(long Start, int Length, int Limit)> Segments(long target, int overlap)
        {
            int index = 0;
            long start = 0;
            while (index < chunks.Count)
            {
                int first = index;
                while (index < chunks.Count && starts[index + 1] - start <= target) index++;
                if (index == first) index++;
                int length = checked((int)(starts[index] - start));
                int limit = index == chunks.Count ? length : Math.Max(0, length - overlap);
                yield return (start, length, limit);
                start += limit;
            }
        }

        internal byte[] Read(long offset, int count)
        {
            var result = new byte[count];
            int index = Array.BinarySearch(starts, offset);
            if (index < 0) index = ~index - 1;
            int written = 0;
            while (written < count)
            {
                var chunk = chunks[index];
                int from = (int)(offset - starts[index]);
                int copy = Math.Min(count - written, chunk.Length - from);
                chunk.AsSpan(from, copy).CopyTo(result.AsSpan(written));
                offset += copy;
                written += copy;
                index++;
            }
            return result;
        }
    }
}
