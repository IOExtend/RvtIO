using System.Diagnostics.CodeAnalysis;
using System.Text;
using RvtIO.Parsing;

namespace RvtIO.Internal;

/// <summary>Preserves resolved value maps in source order while the elements are assembled.</summary>
internal sealed class ElementValueStore<TKey, TValue> where TKey : notnull
{
    private const int PageBytes = 64 * 1024;
    private readonly PartitionChunks.Backing backing;
    private readonly Dictionary<uint, (int Page, int Offset)> offsets;
    private readonly List<PartitionChunks.Chunk> pages = [];
    private readonly Func<BinaryReader, TKey> readKey;
    private readonly Func<BinaryReader, TValue> readValue;
    private uint lastOwner;
    private Dictionary<TKey, TValue>? lastValues;

    internal ElementValueStore(PartitionChunks.Backing storage, Dictionary<uint, Dictionary<TKey, TValue>> source,
        Action<BinaryWriter, TKey> writeKey, Action<BinaryWriter, TValue> writeValue,
        Func<BinaryReader, TKey> readKey, Func<BinaryReader, TValue> readValue)
    {
        backing = storage;
        this.readKey = readKey;
        this.readValue = readValue;
        offsets = new(source.Count);
        using var buffer = new MemoryStream(PageBytes);
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        foreach (var (owner, values) in source)
        {
            offsets.Add(owner, (pages.Count, checked((int)buffer.Position)));
            writer.Write(values.Count);
            foreach (var (key, value) in values)
            {
                writeKey(writer, key);
                writeValue(writer, value);
            }
            if (buffer.Length >= PageBytes) FlushPage();
            // Removing the current entry does not invalidate Dictionary enumeration.
            source.Remove(owner);
        }
        if (buffer.Length > 0) FlushPage();

        void FlushPage()
        {
            pages.Add(backing.Add(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
            buffer.SetLength(0);
            if (buffer.Capacity > 2 * PageBytes) buffer.Capacity = PageBytes;
        }
    }

    internal bool TryGetValue(uint owner, [NotNullWhen(true)] out Dictionary<TKey, TValue>? values)
    {
        if (lastValues != null && owner == lastOwner)
        {
            values = lastValues;
            return true;
        }
        if (!offsets.TryGetValue(owner, out var offset))
        {
            values = null;
            return false;
        }
        byte[] bytes = backing.Read(pages[offset.Page]);
        using var reader = new BinaryReader(new MemoryStream(bytes, offset.Offset, bytes.Length - offset.Offset, writable: false));
        int count = reader.ReadInt32();
        values = new(count);
        for (int i = 0; i < count; i++) values.Add(readKey(reader), readValue(reader));
        lastOwner = owner;
        lastValues = values;
        return true;
    }
}
