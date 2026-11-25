using RvtIO.Parsing;

namespace RvtIO.Internal;

internal record BipEntry(int BipId, string? BipName, int FieldType, int? Value);

internal record ElementRecord
{
    private byte[]? _body;
    private PartitionChunks.Block? _storedBody;
    private ParsedFields? _fields;
    private ElementRecordFieldStore? _fieldStore;
    private ElementRecordFieldStore.Slice _storedFields;

    internal sealed class ParsedFields
    {
        internal List<uint> References = new();
        internal List<BipEntry> BipParams = new();
        internal HashSet<int> WritableParamIds = new();
        internal List<int> EntryValues = new();
    }

    private ParsedFields? Fields => _fieldStore?.Read(_storedFields) ?? _fields;

    internal ParsedFields EditFields() => _fieldStore == null
        ? _fields ??= new()
        : throw new InvalidOperationException("Archived record fields are read-only.");

    internal void ArchiveFields(ElementRecordFieldStore store)
    {
        if (_fields == null) return;
        _storedFields = store.Add(_fields);
        _fieldStore = store;
        _fields = null;
    }

    public uint ElemId { get; set; }
    public byte[] Body
    {
        get => _body ?? _storedBody?.Read().Data ?? Array.Empty<byte>();
        set { _body = value; _storedBody = null; }
    }

    internal void RetainBodyInScratch(PartitionChunks.Block stored)
    {
        _storedBody = stored;
        _body = null;
    }
    public IReadOnlyList<uint> References => Fields?.References ?? (IReadOnlyList<uint>)Array.Empty<uint>();
    public IReadOnlyList<BipEntry> BipParams
    {
        get => Fields?.BipParams ?? (IReadOnlyList<BipEntry>)Array.Empty<BipEntry>();
        init => EditFields().BipParams = value.ToList();
    }
    public double[]? Geometry { get; set; }
    public int? CategoryId { get; set; }
    public ushort Subclass { get; set; }
    internal bool IsWritable(int parameterId) => Fields?.WritableParamIds.Contains(parameterId) == true;

    /// <summary>All schema entry values (int32) in order. Index = field position in class hierarchy.</summary>
    public IReadOnlyList<int> EntryValues => Fields?.EntryValues ?? (IReadOnlyList<int>)Array.Empty<int>();
}

internal record BaseElementFields
{
    public int AssocLevelId { get; set; } = -1;
    public int FamId { get; set; } = -1;
    public int CreatedPhaseId { get; set; } = -1;
    public int DemolishedPhaseId { get; set; } = -1;
    public int BaseDesignOptionId { get; set; } = -1;
}
