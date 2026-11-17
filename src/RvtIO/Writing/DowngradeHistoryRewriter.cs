using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>
/// Transcodes a decoded <c>Global/History</c> episode GUID table across releases by
/// remapping its opening class tag from the source <c>DocumentHistory</c> class id to
/// the target's. The rest of the stream (fixed header, GUID slots, extra list, GUID
/// records) is layout-independent and preserved verbatim, so element UniqueIds derived
/// from episode GUIDs and ElemTable histories stay identical to the source file's.
/// </summary>
internal static class DowngradeHistoryRewriter
{
    internal static byte[] Rewrite(byte[] sourceDecompressed, SchemaDocument sourceDoc, SchemaDocument targetDoc)
    {
        int? sourceId = new FormatsSchema(sourceDoc).IdOf("DocumentHistory")
            ?? throw new InvalidDataException("The source class table lacks the DocumentHistory class.");
        int? targetId = new FormatsSchema(targetDoc).IdOf("DocumentHistory")
            ?? throw new InvalidDataException("The profile class table lacks the DocumentHistory class.");
        if (sourceId is < 0 or > ushort.MaxValue || targetId is < 0 or > ushort.MaxValue)
            throw new InvalidDataException("The DocumentHistory class id does not fit in 16 bits.");
        if (sourceDecompressed.Length < 2)
            throw new InvalidDataException("The episode table is cut inside its class tag.");
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(sourceDecompressed) != (ushort)sourceId)
            throw new InvalidDataException("The episode table does not open with the source DocumentHistory class tag.");

        var output = new byte[sourceDecompressed.Length];
        Buffer.BlockCopy(sourceDecompressed, 0, output, 0, sourceDecompressed.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)targetId);
        return output;
    }
}
