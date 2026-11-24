using System.Collections;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Scene;

/// <summary>Keeps element parameters in scratch between construction, resolution and tessellation passes.</summary>
internal sealed class SceneParameterScratch(PartitionChunks.Backing storage)
{
    private const int DefinitionLimit = 4096;

    private enum ValueKind : byte { None, Integer, Double, Text, Boolean }

    [Flags]
    private enum ParameterFlags : byte
    {
        ReadOnly = 1, Approximate = 2, Missing = 4, ResolvedZero = 8, Native = 16, Metadata = 32,
    }

    private readonly MemoryStream output = new(64 * 1024);
    private readonly PartitionChunks.Backing backing = storage;
    private readonly object readLock = new();
    private readonly Dictionary<(int Id, string? Name, string? BuiltIn), int> definitionIds = new();
    private readonly List<Parameter.DefinitionTemplate> definitions = new();
    private PartitionChunks.Chunk? cached;
    private Parameter[]? cachedParameters;

    internal void Archive(Element element) => element.ArchiveParameters(Store);

    private IReadOnlyList<Parameter>? Store(List<Parameter> parameters, IReadOnlyList<Parameter>? previous)
    {
        if (parameters.Any(parameter => parameter.Value is not (null or int or double or string or bool)))
            return null;

        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        Span<byte> guidBytes = stackalloc byte[16];
        var zeroReferences = Parameter.ZeroReferenceKinds.None;
        int firstPhase = -1;
        bool quantityPlaceholder = false;
        foreach (var parameter in parameters)
        {
            zeroReferences |= parameter.ClassifyZeroReference();
            if (firstPhase < 0 && parameter.Id == -1012100 && parameter.Value is int phase && phase > 0)
                firstPhase = phase;
            quantityPlaceholder |= Parameter.IsHostQuantityPlaceholder(parameter);
            writer.Write(parameter.Id);
            writer.Write7BitEncodedInt((int)parameter.StorageType);
            ParameterFlags flags = 0;
            if (parameter.IsReadOnly) flags |= ParameterFlags.ReadOnly;
            if (parameter.IsApproximate) flags |= ParameterFlags.Approximate;
            if (parameter.HasNoRevitValue) flags |= ParameterFlags.Missing;
            if (parameter.HasResolvedZeroElementId) flags |= ParameterFlags.ResolvedZero;
            if (parameter.FromNativeFields) flags |= ParameterFlags.Native;
            if (parameter.HasSupplementalMetadata) flags |= ParameterFlags.Metadata;
            writer.Write((byte)flags);
            WriteValue(writer, parameter.Value);
            WriteDefinition(writer, parameter.DefinitionFields);
            if (!parameter.HasSupplementalMetadata) continue;
            writer.Write(parameter.Guid.HasValue);
            if (parameter.Guid is Guid guid)
            {
                guid.TryWriteBytes(guidBytes);
                writer.Write(guidBytes);
            }
            WriteText(writer, parameter.ForgeId);
            WriteText(writer, parameter.SpecType);
            WriteText(writer, parameter.UnitSymbol);
            writer.Write(parameter.ConversionFactor.HasValue);
            if (parameter.ConversionFactor is double factor) writer.Write(factor);
            writer.Write(parameter.NativeElementId.HasValue);
            if (parameter.NativeElementId is long id) writer.Write(id);
        }
        var encoded = output.GetBuffer().AsSpan(0, checked((int)output.Length));
        var archived = previous is ParameterList prior && ReferenceEquals(prior.Owner, this)
            ? prior : new ParameterList(this);
        archived.Save(encoded, parameters, zeroReferences, firstPhase, quantityPlaceholder);
        output.SetLength(0);
        if (output.Capacity > 128 * 1024) output.Capacity = 64 * 1024;
        return archived;
    }

    private Parameter[] Read(PartitionChunks.Chunk block, int count)
    {
        lock (readLock)
        {
            if (ReferenceEquals(cached, block)) return cachedParameters!;
            using var reader = new BinaryReader(new MemoryStream(backing.Read(block), writable: false));
            var parameters = new Parameter[count];
            Span<byte> guidBytes = stackalloc byte[16];
            for (int i = 0; i < parameters.Length; i++)
            {
                int id = reader.ReadInt32();
                StorageType storageType = (StorageType)reader.Read7BitEncodedInt();
                var flags = (ParameterFlags)reader.ReadByte();
                object? value = ReadValue(reader);
                var parameter = ReadParameter(reader);
                parameter.Id = id;
                parameter.StorageType = storageType;
                parameter.IsReadOnly = (flags & ParameterFlags.ReadOnly) != 0;
                parameter.IsApproximate = (flags & ParameterFlags.Approximate) != 0;
                parameter.Value = value;
                parameter.HasNoRevitValue = (flags & ParameterFlags.Missing) != 0;
                parameter.HasResolvedZeroElementId = (flags & ParameterFlags.ResolvedZero) != 0;
                parameter.FromNativeFields = (flags & ParameterFlags.Native) != 0;
                parameters[i] = parameter;
                if ((flags & ParameterFlags.Metadata) == 0) continue;
                if (reader.ReadBoolean())
                {
                    reader.BaseStream.ReadExactly(guidBytes);
                    parameter.Guid = new Guid(guidBytes);
                }
                parameter.ForgeId = ReadText(reader);
                parameter.SpecType = ReadText(reader);
                parameter.UnitSymbol = ReadText(reader);
                if (reader.ReadBoolean()) parameter.ConversionFactor = reader.ReadDouble();
                if (reader.ReadBoolean()) parameter.NativeElementId = reader.ReadInt64();
            }
            cached = block;
            return cachedParameters = parameters;
        }
    }

    private void WriteDefinition(BinaryWriter writer, (int Id, string? Name, string? BuiltIn)? fields)
    {
        if (fields is not { } definition)
        {
            writer.Write7BitEncodedInt(0);
            return;
        }
        if (definitionIds.TryGetValue(definition, out int index))
        {
            writer.Write7BitEncodedInt(index + 2);
            return;
        }
        if (definitions.Count < DefinitionLimit
            && (definition.Name?.Length ?? 0) + (definition.BuiltIn?.Length ?? 0) <= 512)
        {
            writer.Write7BitEncodedInt(definitions.Count + 2);
            definitionIds.Add(definition, definitions.Count);
            definitions.Add(new Parameter.DefinitionTemplate(definition.Id, definition.Name, definition.BuiltIn));
            return;
        }
        writer.Write7BitEncodedInt(1);
        writer.Write(definition.Id);
        WriteText(writer, definition.Name);
        WriteText(writer, definition.BuiltIn);
    }

    private Parameter ReadParameter(BinaryReader reader)
    {
        int tag = reader.Read7BitEncodedInt();
        return tag switch
        {
            0 => new Parameter((Parameter.DefinitionTemplate?)null),
            1 => new Parameter(new Definition { Id = reader.ReadInt32(), Name = ReadText(reader), BuiltInParameter = ReadText(reader) }),
            _ => new Parameter(definitions[tag - 2]),
        };
    }

    private sealed class ParameterList(SceneParameterScratch store) : Element.IArchivedParameters
    {
        private PartitionChunks.Chunk? block;
        private int capacity;
        internal SceneParameterScratch Owner => store;
        public int Count { get; private set; }
        public int FamilyTypeId { get; private set; }
        public int FamilyAndTypeReferenceId { get; private set; }
        public Parameter.ZeroReferenceKinds ZeroReferences { get; private set; }
        public int FirstPositiveCreatedPhase { get; private set; }
        public bool HasHostQuantityPlaceholder { get; private set; }
        public Parameter this[int index] => store.Read(block!, Count)[index];
        public IEnumerator<Parameter> GetEnumerator() => ((IEnumerable<Parameter>)store.Read(block!, Count)).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal void Save(ReadOnlySpan<byte> data, List<Parameter> parameters, Parameter.ZeroReferenceKinds zeroReferences,
            int firstPhase, bool quantityPlaceholder)
        {
            if (block != null && data.Length <= capacity)
            {
                // An unchanged edit keeps its previous block and decoded cache entry.
                if (Count == parameters.Count && data.SequenceEqual(store.backing.Read(block))) return;
                block = store.backing.Overwrite(block, data, capacity);
            }
            else
            {
                // Grow geometrically so repeated edits keep one slot while they fit.
                int nextCapacity = (int)Math.Min((uint)int.MaxValue, BitOperations.RoundUpToPowerOf2((uint)Math.Max(256, data.Length)));
                block = store.backing.Add(data, nextCapacity);
                capacity = nextCapacity;
            }
            Count = parameters.Count;
            FamilyTypeId = Element.FindTypeReferenceId(parameters, includeFamilyAndType: false);
            FamilyAndTypeReferenceId = Element.FindTypeReferenceId(parameters, includeFamilyAndType: true);
            ZeroReferences = zeroReferences;
            FirstPositiveCreatedPhase = firstPhase;
            HasHostQuantityPlaceholder = quantityPlaceholder;
        }
    }

    private static void WriteValue(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.Write((byte)ValueKind.None); break;
            case int number: writer.Write((byte)ValueKind.Integer); writer.Write(number); break;
            case double number: writer.Write((byte)ValueKind.Double); writer.Write(number); break;
            case string text: writer.Write((byte)ValueKind.Text); WriteText(writer, text); break;
            case bool flag: writer.Write((byte)ValueKind.Boolean); writer.Write(flag); break;
            default: throw new InvalidDataException("Unsupported archived parameter value.");
        }
    }

    private static object? ReadValue(BinaryReader reader) => (ValueKind)reader.ReadByte() switch
    {
        ValueKind.None => null,
        ValueKind.Integer => reader.ReadInt32(),
        ValueKind.Double => reader.ReadDouble(),
        ValueKind.Text => ReadText(reader),
        ValueKind.Boolean => reader.ReadBoolean(),
        _ => throw new InvalidDataException("Unknown archived parameter value."),
    };

    private static void WriteText(BinaryWriter writer, string? value)
    {
        writer.Write(value?.Length ?? -1);
        if (value != null) writer.Write(MemoryMarshal.AsBytes(value.AsSpan()));
    }

    private static string? ReadText(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        return length == -1 ? null : string.Create(length, reader.BaseStream,
            static (chars, stream) => stream.ReadExactly(MemoryMarshal.AsBytes(chars)));
    }
}
