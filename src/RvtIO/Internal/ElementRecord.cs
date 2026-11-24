using RvtIO.Parsing;

namespace RvtIO.Internal;

internal record BipEntry(int BipId, string? BipName, int FieldType, int? Value);

internal record ElementRecord
{
    private byte[]? _body;
    private PartitionChunks.Block? _storedBody;

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
    public List<uint> References { get; set; } = new();
    public List<BipEntry> BipParams { get; set; } = new();
    public double[]? Geometry { get; set; }
    public int? CategoryId { get; set; }
    public ushort Subclass { get; set; }
    public HashSet<int> WritableParamIds { get; set; } = new();

    /// <summary>All schema entry values (int32) in order. Index = field position in class hierarchy.</summary>
    public List<int> EntryValues { get; set; } = new();
}

internal record BaseElementFields
{
    public int AssocLevelId { get; set; } = -1;
    public int FamId { get; set; } = -1;
    public int CreatedPhaseId { get; set; } = -1;
    public int DemolishedPhaseId { get; set; } = -1;
    public int BaseDesignOptionId { get; set; } = -1;
}
