namespace RvtIO.Internal;

internal readonly record struct BipEntry(int BipId, string? BipName, int FieldType, int? Value);

internal record ElementRecord
{
    public uint ElemId { get; set; }
    public ReadOnlyMemory<byte> Body { get; set; }
    public List<uint> References { get; set; } = new();
    public List<BipEntry> BipParams { get; set; } = new();
    public double[]? Geometry { get; set; }
    public int? CategoryId { get; set; }
    public ushort Subclass { get; set; }
    public HashSet<int>? WritableParamIds { get; set; }
}

internal record BaseElementFields
{
    public int AssocLevelId { get; set; } = -1;
    public int FamId { get; set; } = -1;
    public int CreatedPhaseId { get; set; } = -1;
    public int DemolishedPhaseId { get; set; } = -1;
    public int BaseDesignOptionId { get; set; } = -1;
}
