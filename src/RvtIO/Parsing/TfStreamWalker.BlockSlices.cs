namespace RvtIO.Parsing;

internal static partial class TfStreamWalker
{
    private readonly record struct BlockSlice(long Start, int Chunk, uint Size, ushort Class, uint Owner);

    // The logical segment and class order match ScanBlocks. Only a selected block spanning
    // physical chunks is copied. The visitor receives the offset in its actual backing array.
    internal static void ScanBlockSlices(IReadOnlyList<byte[]> chunks, HashSet<uint> targetIds, BlockVisitor visit,
        int segmentTarget = SegmentTarget)
    {
        if (targetIds.Count == 0 || chunks.Count == 0) return;
        var offsets = new long[chunks.Count + 1];
        for (int i = 0; i < chunks.Count; i++) offsets[i + 1] = offsets[i] + chunks[i].Length;

        var currentGeometry = new List<BlockSlice>();
        var earlierGeometry = new List<BlockSlice>();
        var curves = new List<BlockSlice>();
        int next = 0;
        long carry = 0;
        while (next < chunks.Count)
        {
            long segmentStart = offsets[next] - carry;
            int first = next;
            while (next < chunks.Count && offsets[next + 1] - segmentStart <= segmentTarget) next++;
            if (next == first) next++;
            long segmentEnd = offsets[next];
            bool last = next == chunks.Count;
            long scanEnd = last ? segmentEnd : Math.Max(segmentStart, segmentEnd - MaxTargetBlock);
            long markerStart = segmentStart + 12;
            long markerEnd = Math.Min(scanEnd + 12, segmentEnd - 1);

            int chunkIndex = markerStart < markerEnd ? ChunkAt(markerStart) : next;
            Action<int> recordMarker = RecordMarker;
            for (; chunkIndex < next && offsets[chunkIndex] < markerEnd; chunkIndex++)
            {
                byte[] data = chunks[chunkIndex];
                if (data.Length == 0) continue;
                int localStart = (int)Math.Max(0, markerStart - offsets[chunkIndex]);
                int localEnd = (int)Math.Min(data.Length, markerEnd - offsets[chunkIndex]);
                ScanMarkers(data, localStart, Math.Min(localEnd, data.Length - 1), recordMarker);
                if (localEnd == data.Length && IsBlockMarker(data[^1], chunks[ChunkAt(offsets[chunkIndex + 1])][0]))
                    RecordMarker(data.Length - 1);
            }

            Emit(currentGeometry);
            Emit(earlierGeometry);
            Emit(curves);
            carry = Math.Min(MaxTargetBlock, segmentEnd - segmentStart);

            void RecordMarker(int marker)
            {
                long blockStart = offsets[chunkIndex] + marker - 12;
                int source = marker >= 12 ? chunkIndex : ChunkAt(blockStart);
                scoped ReadOnlySpan<byte> header;
                Span<byte> crossChunkHeader = stackalloc byte[14];
                if (marker >= 12 && marker + 1 < chunks[chunkIndex].Length)
                    header = chunks[chunkIndex].AsSpan(marker - 12, 14);
                else
                {
                    CopyBlock(source, blockStart, crossChunkHeader);
                    header = crossChunkHeader;
                }

                uint size = BitConverter.ToUInt32(header[8..]);
                if (size < 20 || size >= MaxTargetBlock || blockStart + 16 + size > segmentEnd) return;
                uint owner = BitConverter.ToUInt32(header);
                if (!targetIds.Contains(owner)) return;
                var matches = header[12] switch
                {
                    0xD7 => currentGeometry,
                    0xD6 => earlierGeometry,
                    _ => curves,
                };
                matches.Add(new(blockStart, source, size, BitConverter.ToUInt16(header[12..]), owner));
            }
        }

        int ChunkAt(long position)
        {
            int low = 0, high = chunks.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (offsets[middle + 1] <= position) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        void CopyBlock(int chunk, long start, Span<byte> destination)
        {
            int offset = (int)(start - offsets[chunk]);
            while (!destination.IsEmpty)
            {
                var source = chunks[chunk++].AsSpan(offset);
                int length = Math.Min(destination.Length, source.Length);
                source[..length].CopyTo(destination);
                destination = destination[length..];
                offset = 0;
            }
        }

        void Emit(List<BlockSlice> matches)
        {
            foreach (var match in matches)
            {
                byte[] data = chunks[match.Chunk];
                int start = (int)(match.Start - offsets[match.Chunk]);
                int length = (int)match.Size + 16;
                if (length > data.Length - start)
                {
                    data = GC.AllocateUninitializedArray<byte>(length);
                    CopyBlock(match.Chunk, match.Start, data);
                    start = 0;
                }
                visit(data, start, match.Size, match.Class, match.Owner);
            }
            matches.Clear();
        }
    }
}
