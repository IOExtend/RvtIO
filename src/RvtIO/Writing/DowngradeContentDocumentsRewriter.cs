using System.Buffers.Binary;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>
/// Transcodes <c>Global/ContentDocuments</c>, the block stream that holds one ADocument per loaded family:
/// <c>block := u16 ContentMarker, u32 pointer, [u16 ContentKey], u32 owners, [16-byte key], u32 length,
/// ADocument (length bytes), u32 length</c>. The framing tags become the profile's and each ADocument is
/// transcoded as a class object (<see cref="R22Transcoder.PayloadWithTail"/>), so the contained family
/// documents carry the target class tags and the older release can read them.
/// </summary>
internal static class DowngradeContentDocumentsRewriter
{
    internal static byte[] Rewrite(
        byte[] sourceDecompressed,
        PartitionFraming.Tags sourceTags, PartitionFraming.Tags targetTags,
        R22Transcoder transcoder)
    {
        using var output = new MemoryStream(sourceDecompressed.Length);
        var word = new byte[4];
        int pos = 0;
        while (pos < sourceDecompressed.Length)
        {
            // ContentMarker
            Require(pos + 2, sourceDecompressed, "content-marker");
            if (BinaryPrimitives.ReadUInt16LittleEndian(sourceDecompressed.AsSpan(pos, 2)) != sourceTags.ContentMarker)
                throw new InvalidDataException("ContentDocuments has no content-marker.");
            pos += 2;
            // pointer
            Require(pos + 4, sourceDecompressed, "content-key-pointer");
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(sourceDecompressed.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0)
            {
                Require(pos + 2, sourceDecompressed, "content-key-class");
                if (BinaryPrimitives.ReadUInt16LittleEndian(sourceDecompressed.AsSpan(pos, 2)) != sourceTags.ContentKey)
                    throw new InvalidDataException("ContentDocuments has no content-key class.");
                pos += 2;
            }
            // owners
            Require(pos + 4, sourceDecompressed, "owner-count");
            uint owners = BinaryPrimitives.ReadUInt32LittleEndian(sourceDecompressed.AsSpan(pos, 4));
            pos += 4;
            if (owners == uint.MaxValue && pointer == 0)
            {
                // Terminal block: marker, a null pointer and the all-ones owner count close the stream.
                BinaryPrimitives.WriteUInt16LittleEndian(word, targetTags.ContentMarker);
                output.Write(word, 0, 2);
                BinaryPrimitives.WriteUInt32LittleEndian(word, pointer);
                output.Write(word);
                BinaryPrimitives.WriteUInt32LittleEndian(word, owners);
                output.Write(word);
                break;
            }
            byte[]? key = null;
            if (pointer != 0)
            {
                Require(pos + 16, sourceDecompressed, "block-key");
                key = sourceDecompressed.AsSpan(pos, 16).ToArray();
                pos += 16;
            }
            // length, document, length
            Require(pos + 4, sourceDecompressed, "document-length");
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(sourceDecompressed.AsSpan(pos, 4)));
            pos += 4;
            Require(pos + (long)length + 4, sourceDecompressed, "document");
            byte[] document = sourceDecompressed.AsSpan(pos, length).ToArray();
            pos += length;
            if (BinaryPrimitives.ReadUInt32LittleEndian(sourceDecompressed.AsSpan(pos, 4)) != (uint)length)
                throw new InvalidDataException("ContentDocuments block length does not close.");
            pos += 4;

            byte[] rewritten = transcoder.PayloadWithTail(document, 0, document.Length, out string? failure)
                ?? throw new DowngradeRefusedException("contentdocument-transcode-failed",
                    $"The family document in Global/ContentDocuments cannot be rewritten for the target release: {failure}");

            BinaryPrimitives.WriteUInt16LittleEndian(word, targetTags.ContentMarker);
            output.Write(word[..2]);
            BinaryPrimitives.WriteUInt32LittleEndian(word, pointer);
            output.Write(word);
            if (pointer != 0)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(word, targetTags.ContentKey);
                output.Write(word[..2]);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(word, owners);
            output.Write(word);
            if (key is not null) output.Write(key);
            BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)rewritten.Length);
            output.Write(word);
            output.Write(rewritten);
            BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)rewritten.Length);
            output.Write(word);
        }
        return output.ToArray();
    }

    private static void Require(long end, byte[] data, string what)
    {
        if (end > data.Length) throw new InvalidDataException($"ContentDocuments is cut inside its {what}.");
    }
}
