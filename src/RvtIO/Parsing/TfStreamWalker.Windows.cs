namespace RvtIO.Parsing;

internal static partial class TfStreamWalker
{
    private const int WindowStep = 16 * 1024 * 1024;

    private static void WalkWindows(PartitionChunks chunks, HashSet<uint> knownIds, LocatedBlockVisitor visit,
        RecordVisitor? visitRecord, Action<long, long>? progress, long segmentTarget)
    {
        var source = new PartitionChunks.WindowSource(chunks);
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
                long windowStart = start + from;
                var bytes = source.Read(windowStart, end - from);
                Action<int>? report = progress == null ? null : offset => progress(windowStart + offset, source.Length);
                position = from + WalkWindow(bytes, position - from, ref aligned, knownIds,
                    (data, offset, size, cls, eid) => visit(data, offset, size, cls, eid,
                        new(source, windowStart + offset, checked((int)size + 16))),
                    stop - from, visitRecord, report);
            }
        }
        if (source.Length > 0) progress?.Invoke(source.Length, source.Length);
    }

    private static void ScanWindows(PartitionChunks chunks, HashSet<uint> targetIds, LocatedBlockVisitor visit,
        long segmentTarget, int maxBlock)
    {
        var source = new PartitionChunks.WindowSource(chunks);
        // Preserve the marker order of the concatenated segment, also when it spans several windows.
        foreach (var (start, length, limit) in source.Segments(segmentTarget, maxBlock))
            foreach (byte marker in new byte[] { 0xD7, 0xD6, 0x46 })
                for (int position = 0; position < limit;)
                {
                    int stop = (int)Math.Min((long)position + WindowStep, limit);
                    int end = (int)Math.Min((long)stop + maxBlock + 16, length);
                    long windowStart = start + position;
                    var bytes = source.Read(windowStart, end - position);
                    ScanWindow(bytes, 0, stop - position, marker, maxBlock, targetIds,
                        (data, offset, size, cls, eid) => visit(data, offset, size, cls, eid,
                            new(source, windowStart + offset, checked((int)size + 16))));
                    position = stop;
                }
    }
}
