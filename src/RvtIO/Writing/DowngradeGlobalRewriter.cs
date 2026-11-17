using System.Buffers.Binary;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>
/// Transcodes the single-object <c>Global</c> streams of a cross-width downgrade
/// (<c>Latest</c>, <c>PartitionTable</c>, <c>DocumentIncrementTable</c>) with the same
/// class-and-field-name machinery as the partition records
/// (<c>R22Transcoder.Payload(byte[], int, int, out string?)</c>): the root class tag and every nested class tag
/// are renumbered to the target schema, fields are cut or inserted, and 64-bit scalars
/// are narrowed. A stream whose object cannot be rewritten is refused, because the older
/// release would read the newer class tags.
/// </summary>
internal static class DowngradeGlobalRewriter
{
    /// <summary>The rewritten object, or an exception naming the stream and the reason. Bytes after the object
    /// (Global/Latest holds the class object followed by opaque data) are copied unchanged. A stream whose first
    /// 16 bits are not a class of the source schema is not a class object and is returned unchanged.</summary>
    internal static byte[] Rewrite(string streamName, byte[] sourceDecompressed, SchemaDocument sourceDoc, R22Transcoder transcoder)
    {
        if (!IsClassObject(sourceDecompressed, sourceDoc)) return sourceDecompressed;
        byte[]? rewritten = transcoder.PayloadWithTail(sourceDecompressed, 0, sourceDecompressed.Length, out string? failure);
        if (rewritten is null)
            throw new DowngradeRefusedException("global-transcode-failed",
                $"The class object in Global/{streamName} cannot be rewritten for the target release: {failure}");
        return rewritten;
    }

    /// <summary>
    /// A tag-only patch of a block stream whose layout is not a single class object:
    /// the 16-bit class tag at <paramref name="tagOffset"/> is renumbered when the class
    /// exists in both tables, and the stream is otherwise unchanged.
    /// </summary>
    internal static byte[] PatchTag(byte[] data, int tagOffset, SchemaDocument sourceDoc, SchemaDocument targetDoc, string streamName)
    {
        if (!IsClassObject(data, sourceDoc, tagOffset)) return data;
        if (tagOffset < 0 || tagOffset + 2 > data.Length)
            throw new DowngradeRefusedException("global-transcode-failed",
                $"Global/{streamName} is cut inside its class tag.");
        ushort sourceTag = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(tagOffset, 2));
        var sourceByName = sourceDoc.Classes.ToDictionary(c => c.Id, c => c.Name);
        var targetById = targetDoc.Classes.ToDictionary(c => c.Id, c => c.Id);
        var targetByName = targetDoc.Classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().Id);
        if (!sourceByName.TryGetValue(sourceTag, out string? className) || !targetByName.TryGetValue(className, out int targetTag))
            throw new DowngradeRefusedException("global-transcode-failed",
                $"Global/{streamName} opens with class {sourceTag}, which the profile class table does not define.");
        if (targetTag is < 0 or > ushort.MaxValue)
            throw new DowngradeRefusedException("global-transcode-failed",
                $"The profile class id {targetTag} of {className} does not fit in 16 bits.");
        var patched = new byte[data.Length];
        Buffer.BlockCopy(data, 0, patched, 0, data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(patched.AsSpan(tagOffset, 2), (ushort)targetTag);
        _ = targetById;
        return patched;
    }

    /// <summary>True when the 16 bits at <paramref name="tagOffset"/> name a class of the source schema.</summary>
    private static bool IsClassObject(byte[] data, SchemaDocument sourceDoc, int tagOffset = 0)
    {
        if (tagOffset < 0 || tagOffset + 2 > data.Length) return false;
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(tagOffset, 2));
        return sourceDoc.Classes.Any(c => c.Id == tag);
    }
}
