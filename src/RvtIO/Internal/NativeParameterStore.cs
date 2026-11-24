using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using RvtIO.Model;
using RvtIO.Parsing;
using NativeParams = RvtIO.Parsing.TfBlockDeserializer.NativeParams;
using NamedParam = RvtIO.Parsing.TfBlockDeserializer.NamedParamRecord;

namespace RvtIO.Internal;

/// <summary>Stores decoded parameter records individually in the partition scratch stream when available.</summary>
internal sealed class NativeParameterStore : IReadOnlyDictionary<uint, NativeParams>
{
    private readonly Dictionary<uint, Entry> entries = [];
    private readonly PartitionChunks.Backing? backing;
    private readonly MemoryStream? writeBuffer;
    private readonly BinaryWriter? bufferWriter;

    internal NativeParameterStore(PartitionChunks.Backing? backing)
    {
        this.backing = backing;
        if (backing == null) return;
        writeBuffer = new MemoryStream(1024);
        bufferWriter = new BinaryWriter(writeBuffer, Encoding.UTF8, leaveOpen: true);
    }

    private readonly record struct Entry(NativeParams? Resident, PartitionChunks.Chunk? Stored,
        IReadOnlyList<ExtensibleStorageEntity>? Entities);

    public NativeParams this[uint id]
    {
        get => Read(entries[id]);
        set => entries[id] = Store(value);
    }

    public int Count => entries.Count;
    public IEnumerable<uint> Keys => entries.Keys;
    public IEnumerable<NativeParams> Values => entries.Values.Select(Read);
    public bool ContainsKey(uint id) => entries.ContainsKey(id);
    internal void Clear() => entries.Clear();

    public bool TryGetValue(uint id, [NotNullWhen(true)] out NativeParams? value)
    {
        value = entries.TryGetValue(id, out var entry) ? Read(entry) : null;
        return value != null;
    }

    public IEnumerator<KeyValuePair<uint, NativeParams>> GetEnumerator()
    {
        foreach (var (id, entry) in entries) yield return new(id, Read(entry));
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private Entry Store(NativeParams value)
    {
        if (backing == null) return new(value, null, null);
        var bytes = writeBuffer!;
        lock (bytes)
        {
            bytes.SetLength(0);
            WriteRecord(bufferWriter!, value);
            var chunk = backing.Add(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
            if (bytes.Capacity > 128 * 1024)
            {
                bytes.SetLength(0);
                bytes.Capacity = 64 * 1024;
            }
            return new(null, chunk, value.ExtensibleStorage);
        }
    }

    private static void WriteRecord(BinaryWriter writer, NativeParams value)
    {
        writer.Write(value.RootParameterStoresComplete);
        writer.Write(value.TailWalkComplete);
        WriteNullableInts(writer, value.CellClasses);
        WriteNullableInts(writer, value.RoofExtrusionFaceTags);
        WritePairs(writer, value.Doubles, static (w, v) => w.Write(v));
        WritePairs(writer, value.Ints, static (w, v) => w.Write(v));
        WritePairs(writer, value.Strings, WriteString);
        WritePairs(writer, value.ElementIds, static (w, v) => w.Write(v));
        WriteFields(writer, value.Fields, static (w, v) => w.Write(v));
        WriteFields(writer, value.DoubleFields, static (w, v) => w.Write(v));
        WriteFields(writer, value.StringFields, WriteString);
        writer.Write(value.NamedParams.Count);
        foreach (var entry in value.NamedParams)
        {
            writer.Write(entry.ParamId);
            writer.Write(entry.Str != null);
            if (entry.Str != null) WriteString(writer, entry.Str);
            writer.Write(entry.Value);
            writer.Write(entry.ElemId);
            writer.Write(entry.Int);
            writer.Write(entry.Instance);
            writer.Write(entry.Reporting);
        }
    }

    private NativeParams Read(Entry entry)
    {
        if (entry.Resident is { } resident) return resident;
        using var reader = new BinaryReader(new MemoryStream(backing!.Read(entry.Stored!), writable: false));
        var parameters = new NativeParams
        {
            RootParameterStoresComplete = reader.ReadBoolean(),
            TailWalkComplete = reader.ReadBoolean(),
            CellClasses = ReadNullableInts(reader),
            ExtensibleStorage = entry.Entities,
        };
        if (ReadNullableInts(reader) is { } tags) parameters.RoofExtrusionFaceTags = tags.ToHashSet();
        ReadPairs(reader, parameters.Doubles, static r => r.ReadDouble());
        ReadPairs(reader, parameters.Ints, static r => r.ReadInt32());
        ReadPairs(reader, parameters.Strings, ReadString);
        ReadPairs(reader, parameters.ElementIds, static r => r.ReadInt32());
        ReadFields(reader, parameters.Fields, static r => r.ReadInt32());
        ReadFields(reader, parameters.DoubleFields, static r => r.ReadDouble());
        ReadFields(reader, parameters.StringFields, ReadString);
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
            parameters.NamedParams.Add(new NamedParam(reader.ReadInt32(), reader.ReadBoolean() ? ReadString(reader) : null,
                reader.ReadDouble(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadBoolean(), reader.ReadBoolean()));
        return parameters;
    }

    private static void WriteNullableInts(BinaryWriter writer, IReadOnlyCollection<int>? values)
    {
        writer.Write(values?.Count ?? -1);
        if (values == null) return;
        foreach (int value in values) writer.Write(value);
    }

    private static List<int>? ReadNullableInts(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == -1) return null;
        var values = new List<int>(count);
        for (int i = 0; i < count; i++) values.Add(reader.ReadInt32());
        return values;
    }

    private static void WritePairs<T>(BinaryWriter writer, List<(int ParamId, T Value)> values, Action<BinaryWriter, T> writeValue)
    {
        writer.Write(values.Count);
        foreach (var (id, value) in values)
        {
            writer.Write(id);
            writeValue(writer, value);
        }
    }

    private static void ReadPairs<T>(BinaryReader reader, List<(int ParamId, T Value)> values, Func<BinaryReader, T> readValue)
    {
        int count = reader.ReadInt32();
        values.EnsureCapacity(count);
        for (int i = 0; i < count; i++) values.Add((reader.ReadInt32(), readValue(reader)));
    }

    private static void WriteFields<T>(BinaryWriter writer, Dictionary<string, T> fields, Action<BinaryWriter, T> writeValue)
    {
        writer.Write(fields.Count);
        foreach (var (name, value) in fields)
        {
            WriteString(writer, name);
            writeValue(writer, value);
        }
    }

    private static void ReadFields<T>(BinaryReader reader, Dictionary<string, T> fields, Func<BinaryReader, T> readValue)
    {
        int count = reader.ReadInt32();
        fields.EnsureCapacity(count);
        for (int i = 0; i < count; i++) fields.Add(ReadString(reader), readValue(reader));
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        writer.Write(value.Length);
        // Preserve UTF-16 code units exactly, including unpaired surrogates.
        writer.Write(MemoryMarshal.AsBytes(value.AsSpan()));
    }

    private static string ReadString(BinaryReader reader) => string.Create(reader.ReadInt32(), reader.BaseStream,
        static (chars, stream) => stream.ReadExactly(MemoryMarshal.AsBytes(chars)));
}
