using System.Text;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Internal;

/// <summary>Retains native field-table edits until categories are resolved and their domains can be checked.</summary>
internal sealed class NativeTableEffectsScratch(PartitionChunks.Backing backing)
{
    private const int PageSize = 64 * 1024;
    private readonly MemoryStream output = new(PageSize);
    private readonly Dictionary<Element, Slice> slices = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string[], int> domainIds = new(ReferenceEqualityComparer.Instance);
    private readonly List<string[]> domains = [];
    private Page current = new();

    private sealed class Page
    {
        internal PartitionChunks.Chunk? Chunk;
    }

    private readonly record struct Slice(Page Page, int Start);
    private enum ValueKind : byte { None, Integer, Double, Text, Boolean }

    internal void Archive(Element element)
    {
        if (element.NativeTableAddedParams is not { Count: > 0 } added
            || element.NativeTablePrevious?.Values.Any(value => value.Value is not (null or int or double or string or bool)) == true)
            return;
        var slice = new Slice(current, checked((int)output.Position));
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(added.Count);
        foreach (var row in added)
        {
            if (!domainIds.TryGetValue(row.Domains, out int domainId))
            {
                domainId = domains.Count;
                domains.Add(row.Domains);
                domainIds.Add(row.Domains, domainId);
            }
            writer.Write(row.ParamId);
            writer.Write(domainId);
            writer.Write((int)row.Storage);
            WriteValue(writer, row.Value);
            writer.Write(row.HasNoRevitValue);
        }

        writer.Write(element.NativeTableCreatedParams?.Count ?? 0);
        if (element.NativeTableCreatedParams is { } created)
            foreach (int id in created) writer.Write(id);
        writer.Write(element.NativeTablePrevious?.Count ?? 0);
        if (element.NativeTablePrevious is { } previous)
        {
            foreach (var (id, value) in previous)
            {
                writer.Write(id);
                WriteValue(writer, value.Value);
                writer.Write((int)value.Storage);
                writer.Write(value.HasNoRevitValue);
            }
        }
        slices.Add(element, slice);
        element.NativeTableAddedParams = null;
        element.NativeTableCreatedParams = null;
        element.NativeTablePrevious = null;
        if (output.Length >= PageSize) Seal();
    }

    internal void Seal()
    {
        if (output.Length == 0) return;
        current.Chunk = backing.Add(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
        current = new Page();
        output.SetLength(0);
        if (output.Capacity > 2 * PageSize) output.Capacity = PageSize;
    }

    internal void Restore(Element element)
    {
        if (!slices.Remove(element, out var slice)) return;
        byte[] bytes = backing.Read(slice.Page.Chunk!);
        using var reader = new BinaryReader(new MemoryStream(bytes, slice.Start, bytes.Length - slice.Start, writable: false));
        int count = reader.ReadInt32();
        var added = new List<(int ParamId, string[] Domains, StorageType Storage, object Value, bool HasNoRevitValue)>(count);
        for (int index = 0; index < count; index++)
        {
            int id = reader.ReadInt32();
            var domain = domains[reader.ReadInt32()];
            var storage = (StorageType)reader.ReadInt32();
            object value = ReadValue(reader) ?? throw new InvalidDataException("A native field edit has no value.");
            added.Add((id, domain, storage, value, reader.ReadBoolean()));
        }
        element.NativeTableAddedParams = added;

        count = reader.ReadInt32();
        if (count > 0)
        {
            var created = new HashSet<int>(count);
            for (int index = 0; index < count; index++) created.Add(reader.ReadInt32());
            element.NativeTableCreatedParams = created;
        }
        count = reader.ReadInt32();
        if (count > 0)
        {
            var previous = new Dictionary<int, (object? Value, StorageType Storage, bool HasNoRevitValue)>(count);
            for (int index = 0; index < count; index++)
                previous.Add(reader.ReadInt32(), (ReadValue(reader), (StorageType)reader.ReadInt32(), reader.ReadBoolean()));
            element.NativeTablePrevious = previous;
        }
    }

    private static void WriteValue(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.Write((byte)ValueKind.None); break;
            case int number: writer.Write((byte)ValueKind.Integer); writer.Write(number); break;
            case double number: writer.Write((byte)ValueKind.Double); writer.Write(number); break;
            case string text: writer.Write((byte)ValueKind.Text); NativeParameterStore.WriteString(writer, text); break;
            case bool flag: writer.Write((byte)ValueKind.Boolean); writer.Write(flag); break;
            default: throw new InvalidDataException("Unsupported native field edit value.");
        }
    }

    private static object? ReadValue(BinaryReader reader) => (ValueKind)reader.ReadByte() switch
    {
        ValueKind.None => null,
        ValueKind.Integer => reader.ReadInt32(),
        ValueKind.Double => reader.ReadDouble(),
        ValueKind.Text => NativeParameterStore.ReadString(reader),
        ValueKind.Boolean => reader.ReadBoolean(),
        _ => throw new InvalidDataException("Unknown native field edit value."),
    };
}
