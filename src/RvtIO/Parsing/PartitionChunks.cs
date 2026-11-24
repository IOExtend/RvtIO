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
                    stream.Position = offset;
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
                    stream.Position = chunk.Offset;
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
        private readonly byte[]? data;
        private readonly int start;
        private readonly Backing? backing;
        private readonly Chunk? stored;

        internal Block(byte[] data, int start, int length, Backing? backing)
        {
            if (backing == null)
            {
                this.data = data;
                this.start = start;
            }
            else
            {
                this.backing = backing;
                stored = backing.Add(data.AsSpan(start, length));
            }
        }

        internal (byte[] Data, int Start) Read() => data != null ? (data, start) : (backing!.Read(stored!), 0);
    }

    private readonly List<Chunk> entries = [];
    internal Backing? Store => backing;
    internal Chunk Reference(int index) => entries[index];
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
