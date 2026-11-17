using System.Buffers.Binary;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>
/// Transcodes a decoded <c>Global/ElemTable</c> stream between element-id widths
/// (40-byte rows with 8-byte ids to 28-byte rows with 4-byte ids, Revit 2024+ to
/// 2020-2023). The stream is the <c>ElemTable</c> class object (the row array followed
/// by the graveyard array, the identifier-source pointer and the flags) and the bytes
/// after it; the object is rewritten by <see cref="R22Transcoder"/> by class and field
/// name, so the row layout, the source identifier and every pointer tag follow the
/// target class table. Rows of owners the partition rewrite left out (their class has
/// no target counterpart) are dropped and the row count follows.
/// </summary>
internal static class DowngradeElemTableRewriter
{
    internal static byte[] Rewrite(
        byte[] sourceDecompressed, SchemaDocument sourceDoc, SchemaDocument targetDoc,
        IReadOnlySet<ulong>? droppedOwners = null)
    {
        var source = new FormatsSchema(sourceDoc);
        var target = new FormatsSchema(targetDoc);
        int? sourceWidth = ElemTableLayout.IdWidth(source);
        int? targetWidth = ElemTableLayout.IdWidth(target);
        if (sourceWidth is null || targetWidth is null)
            throw new InvalidDataException("The element-id width of the source or the target schema is not known.");

        byte[]? rewritten = new R22Transcoder(sourceDoc, targetDoc, sourceWidth.Value)
            .PayloadWithTail(sourceDecompressed, 0, sourceDecompressed.Length, out string? failure);
        if (rewritten is null)
        {
            if (failure?.Contains("beyond 32 bits", StringComparison.Ordinal) == true)
                throw new DowngradeRefusedException("id-beyond-32-bits",
                    "An element-table id does not fit in 32 bits and cannot be downgraded to a 4-byte release.");
            throw new InvalidDataException($"The element table cannot be rewritten: {failure}.");
        }
        byte[] transcoded = rewritten;

        var rows = ElemTableLayout.TryParse(transcoded, target)
            ?? throw new InvalidDataException("The transcoded element table does not decode by the target schema.");
        int targetRowLength = ElemTableLayout.RowLength(target)
            ?? throw new InvalidDataException("The target element-table row length is not known.");

        if (droppedOwners is null || droppedOwners.Count == 0) return transcoded;

        var keep = rows.Select(r => !droppedOwners.Contains(unchecked((ulong)r.Id))).ToArray();
        int kept = keep.Count(k => k);
        if (kept == rows.Count) return transcoded;

        int restAt = ElemTableLayout.HeaderLength + rows.Count * targetRowLength;
        var output = new byte[ElemTableLayout.HeaderLength + kept * targetRowLength + (transcoded.Length - restAt)];
        transcoded.AsSpan(0, 2).CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(2, 4), (uint)kept);
        int at = ElemTableLayout.HeaderLength;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!keep[i]) continue;
            transcoded.AsSpan(ElemTableLayout.HeaderLength + i * targetRowLength, targetRowLength).CopyTo(output.AsSpan(at));
            at += targetRowLength;
        }
        transcoded.AsSpan(restAt).CopyTo(output.AsSpan(at));

        var check = ElemTableLayout.TryParse(output, target);
        if (check is null || check.Count != kept)
            throw new InvalidDataException("The transcoded element table does not decode by the target schema.");
        return output;
    }
}
