using System.Collections;

namespace RvtIO.Parsing;

/// <summary>Ordered references to inflated partition chunks. A filtered list shares the same references.</summary>
internal sealed class PartitionChunks(PartitionChunks.Backing? backing = null) : IReadOnlyList<byte[]>
{
    internal sealed record Chunk(int Length, byte[]? Memory, long Offset);

    internal sealed class Backing(Stream stream)
    {
        private const int CachedChunks = 16;
        private const int CacheLimit = 8 * 1024 * 1024;
        private readonly Dictionary<long, byte[]> cache = [];
        private readonly Queue<long> cacheOrder = new(CachedChunks);
        private int cacheBytes;
        private long end;

        internal Chunk Add(ReadOnlySpan<byte> data)
        {
            lock (stream)
            {
                long offset = end;
                try
                {
                    if (stream.Position != offset) stream.Position = offset;
                    stream.Write(data);
                }
                catch (Exception failure) when (MalformedInput.Is(failure))
                {
                    throw new StorageFailure("Could not write the partition scratch stream.", failure);
                }
                end += data.Length;
                return new Chunk(data.Length, null, offset);
            }
        }

        internal byte[] Read(Chunk chunk)
        {
            if (chunk.Length == 0) return [];
            lock (stream)
            {
                if (cache.TryGetValue(chunk.Offset, out var data)) return data;
                data = new byte[chunk.Length];
                try
                {
                    if (stream.Position != chunk.Offset) stream.Position = chunk.Offset;
                    stream.ReadExactly(data);
                }
                catch (Exception failure) when (MalformedInput.Is(failure))
                {
                    throw new StorageFailure("Could not read the partition scratch stream.", failure);
                }
                if (data.Length <= CacheLimit)
                {
                    while (cache.Count == CachedChunks || cacheBytes + data.Length > CacheLimit)
                    {
                        long offset = cacheOrder.Dequeue();
                        cacheBytes -= cache[offset].Length;
                        cache.Remove(offset);
                    }
                    cache.Add(chunk.Offset, data);
                    cacheOrder.Enqueue(chunk.Offset);
                    cacheBytes += data.Length;
                }
                return data;
            }
        }
    }

    // A storage error must never be treated as a malformed record and skipped.
    private sealed class StorageFailure(string message, Exception cause) : Exception(message, cause);

    internal readonly struct Block
    {
        private readonly object? source;
        private readonly Chunk? stored;
        private readonly long start;
        private readonly int length;

        internal Block(byte[] data, int start, int length, Backing? backing)
        {
            if (backing == null)
            {
                source = data;
                this.start = start;
            }
            else
            {
                source = backing;
                stored = backing.Add(data.AsSpan(start, length));
            }
        }

        internal Block(Backing backing, Chunk stored, int start, int length)
        {
            source = backing;
            this.stored = stored;
            this.start = start;
            this.length = length;
        }

        internal Block(WindowSource source, long start, int length)
        {
            this.source = source;
            this.start = start;
            this.length = length;
        }

        internal (byte[] Data, int Start) Read()
        {
            if (source is byte[] resident) return (resident, (int)start);
            if (source is WindowSource window) return (window.Read(start, length), 0);
            var bytes = ((Backing)source!).Read(stored!);
            return (length == 0 ? bytes : bytes.AsSpan((int)start, length).ToArray(), 0);
        }
    }

    /// <summary>A bounded view over the concatenation of the original inflated chunks.</summary>
    internal sealed class WindowSource
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

    private readonly List<Chunk> entries = [];
    internal Backing? Store => backing;
    internal Chunk Reference(int index) => entries[index];
    internal Block? StoredBlock(int index, int start, int length)
        => backing != null && entries[index].Memory == null ? new Block(backing, entries[index], start, length) : null;
    internal void Add(Chunk chunk) => entries.Add(chunk);
    internal void Add(byte[] data) => Add(backing?.Add(data) ?? new Chunk(data.Length, data, 0));
    internal void AddRange(PartitionChunks chunks) => entries.AddRange(chunks.entries);

    internal PartitionChunks Select(IEnumerable<int> indices)
    {
        var result = new PartitionChunks(backing);
        foreach (int index in indices) result.Add(entries[index]);
        return result;
    }

    public int Count => entries.Count;
    public byte[] this[int index]
    {
        get
        {
            var chunk = entries[index];
            return chunk.Memory ?? backing!.Read(chunk);
        }
    }

    public IEnumerator<byte[]> GetEnumerator()
    {
        for (int i = 0; i < entries.Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static int LengthAt(IReadOnlyList<byte[]> chunks, int index)
        => chunks is PartitionChunks partition ? partition.entries[index].Length : chunks[index].Length;
}
