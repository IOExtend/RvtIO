using System.Text;
using RvtIO.Parsing;

namespace RvtIO.Internal;

/// <summary>Pages the decoded fields that record consumers read after the initial parse.</summary>
internal sealed class ElementRecordFieldStore(PartitionChunks.Backing backing)
{
    private const int PageBytes = 64 * 1024;
    private readonly MemoryStream output = new(PageBytes);
    private readonly object readLock = new();
    private Page current = new();
    private Slice last;
    private ElementRecord.ParsedFields? lastFields;

    internal sealed class Page
    {
        internal PartitionChunks.Chunk? Chunk;
    }

    internal readonly record struct Slice(Page Page, int Start);

    // The record parser is the only writer. Records already parsed can be read before it seals the page.
    internal Slice Add(ElementRecord.ParsedFields fields)
    {
        var slice = new Slice(current, checked((int)output.Position));
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(fields.References.Count);
        foreach (uint reference in fields.References) writer.Write(reference);
        writer.Write(fields.BipParams.Count);
        foreach (var parameter in fields.BipParams)
        {
            writer.Write(parameter.BipId);
            writer.Write(parameter.BipName != null);
            if (parameter.BipName != null) writer.Write(parameter.BipName);
            writer.Write(parameter.FieldType);
            writer.Write(parameter.Value.HasValue);
            if (parameter.Value is int value) writer.Write(value);
        }
        writer.Write(fields.WritableParamIds.Count);
        foreach (int id in fields.WritableParamIds) writer.Write(id);
        writer.Write(fields.EntryValues.Count);
        foreach (int value in fields.EntryValues) writer.Write(value);
        if (output.Length >= PageBytes) Seal();
        return slice;
    }

    internal void Seal()
    {
        if (output.Length == 0) return;
        current.Chunk = backing.Add(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
        current = new Page();
        output.SetLength(0);
        if (output.Capacity > 2 * PageBytes) output.Capacity = PageBytes;
    }

    internal ElementRecord.ParsedFields Read(Slice slice)
    {
        lock (readLock)
        {
            if (lastFields != null && last == slice) return lastFields;
            byte[] bytes = slice.Page.Chunk is { } stored ? backing.Read(stored) : output.GetBuffer();
            using var reader = new BinaryReader(new MemoryStream(bytes, slice.Start, bytes.Length - slice.Start, writable: false));
            var fields = new ElementRecord.ParsedFields();
            int references = reader.ReadInt32();
            fields.References.EnsureCapacity(references);
            for (int i = 0; i < references; i++) fields.References.Add(reader.ReadUInt32());
            int parameters = reader.ReadInt32();
            fields.BipParams.EnsureCapacity(parameters);
            for (int i = 0; i < parameters; i++)
            {
                int id = reader.ReadInt32();
                string? name = reader.ReadBoolean() ? reader.ReadString() : null;
                int type = reader.ReadInt32();
                int? value = reader.ReadBoolean() ? reader.ReadInt32() : null;
                fields.BipParams.Add(new BipEntry(id, name, type, value));
            }
            int writable = reader.ReadInt32();
            fields.WritableParamIds.EnsureCapacity(writable);
            for (int i = 0; i < writable; i++) fields.WritableParamIds.Add(reader.ReadInt32());
            int entries = reader.ReadInt32();
            fields.EntryValues.EnsureCapacity(entries);
            for (int i = 0; i < entries; i++) fields.EntryValues.Add(reader.ReadInt32());
            last = slice;
            return lastFields = fields;
        }
    }
}
