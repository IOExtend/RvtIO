using RvtIO.Parsing;

namespace RvtIO.Internal;

/// <summary>Decoded sources of validated element names and stored JSON parameter values.</summary>
internal class JsonReadFacts
{
    /// <summary>First block class ID (low 16 bits) seen per element in the continuous walk.</summary>
    public Dictionary<uint, ushort> ElemClassIdx { get; } = new();
    /// <summary>The file's own schema, or null for the shipped 2022 layout.</summary>
    public FileSchema? WideSchema { get; set; }
    /// <summary>The element's own name from its root in the file's schema.</summary>
    public Dictionary<uint, string> WideNames { get; } = new();
    /// <summary>Authoritative name-table entries, indexed by named element (not holder).</summary>
    public Dictionary<uint, string> ElementNameTable { get; set; } = new();
    /// <summary>Own FamilySymbol SymbolInfo name; every Symbol subclass root counts.</summary>
    public Dictionary<uint, (int BlockCount, string? Name)> FamilySymbolNames { get; } = new();
    /// <summary>Own RoofAttributes/StairsRailingAttr names; all Symbol roots count.</summary>
    public Dictionary<uint, (int BlockCount, ushort ClassId, string? Name)> RoofOrRailingTypeNames { get; } = new();
    /// <summary>Every Symbol root counts; duplicates invalidate the class and discriminator.</summary>
    public Dictionary<uint, (int BlockCount, ushort ClassId, int? Discriminator)> SystemTypeRoots { get; } = new();
    /// <summary>Own CompoundStructure fields and SymbolInfo name; all HostObjAttr roots count.</summary>
    public Dictionary<uint, (int BlockCount, ushort ClassId, RawCompoundStructureLayer[]? Layers, string? TypeName)>
        HostTypeCompoundStructures { get; } = new();
    /// <summary>Native m_pParamValueSet stores and captured fields by element.</summary>
    public Dictionary<uint, TfBlockDeserializer.NativeParams> NativeParams { get; set; } = new();

    internal readonly record struct JsonStores(
        TfBlockDeserializer.NativeRows<(int ParamId, double Value)> Doubles,
        TfBlockDeserializer.NativeRows<TfBlockDeserializer.NamedParamRecord> NamedParams, string? LevelName);
    private readonly Dictionary<uint, JsonStores>? _stores;

    internal JsonStores StoresOf(uint id) => _stores != null ? _stores.GetValueOrDefault(id)
        : NativeParams.TryGetValue(id, out var native) ? Stores(native, true) : default;

    private static JsonStores Stores(TfBlockDeserializer.NativeParams native, bool withValues) =>
        new(withValues ? native.Doubles : default, withValues ? native.NamedParams : default,
            native.Fields.ContainsKey("__isLevel") ? native.StringFields.GetValueOrDefault("m_text") : null);

    protected JsonReadFacts() { }

    // Keep these maps, but not the partition buffers, the record index or the geometry decoder.
    internal JsonReadFacts(JsonReadFacts facts, bool withValues, bool debugNative = false)
    {
        ElemClassIdx = facts.ElemClassIdx;
        WideSchema = facts.WideSchema;
        WideNames = facts.WideNames;
        ElementNameTable = facts.ElementNameTable;
        FamilySymbolNames = facts.FamilySymbolNames;
        RoofOrRailingTypeNames = facts.RoofOrRailingTypeNames;
        SystemTypeRoots = facts.SystemTypeRoots;
        HostTypeCompoundStructures = facts.HostTypeCompoundStructures;
        if (debugNative)
            NativeParams = facts.NativeParams;
        else
        {
            _stores = new();
            foreach (var (id, native) in facts.NativeParams)
            {
                var held = Stores(native, withValues);
                if (held.Doubles.Count != 0 || held.NamedParams.Count != 0 || held.LevelName != null)
                    _stores.Add(id, held);
            }
        }
    }
}
