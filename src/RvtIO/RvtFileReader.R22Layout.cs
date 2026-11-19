using OpenMcdf;
using RvtIO.Parsing;

namespace RvtIO;

public partial class RvtFileReader
{
    /// <summary>The partition chunks of a file with the class schema of a later release (<paramref name="schema"/>) rewritten into the
    /// Revit 2022 layout (see <see cref="R22Transcoder"/>); the chunks after the partition streams are kept. Also the number of rewritten
    /// partition chunks and the framed streams with their records of the project's own block in that layout (null for a stream without
    /// a framing or with another id width). Null when the file's schema table does not parse a second time or its id width is not 4 or 8 bytes.</summary>
    private static (List<byte[]> Chunks, int PartitionChunkCount, List<PartitionFraming.Framed?> Framed)? RewriteToR22Layout(RootStorage root,
        FileSchema schema, List<byte[]> chunks, IReadOnlyList<IReadOnlyList<byte[]>> partitionStreams,
        IReadOnlyList<PartitionFraming.Framed?> framedStreams, TextWriter? diag, ElementIdMap? ids)
    {
        if (WideElementRecords.IdWidth(schema) is not int width
            || PagedGzipStream.Read(root, "Formats", "Latest") is not { } schemaBytes)
            return null;
        SchemaDocument source;
        try { source = TfSchemaReader.Parse(schemaBytes); }
        catch (FormatException) { return null; }
        using var shipped = typeof(RvtFileReader).Assembly.GetManifestResourceStream("RvtIO.Resources.r22_schemas.json");
        if (shipped == null) return null;
        var transcoder = new R22Transcoder(source, TfSchemaReader.ReadShipped2022(shipped), width, ids);
        var rewritten = new List<byte[]>(chunks.Count);
        foreach (var stream in partitionStreams)
        {
            rewritten.AddRange(transcoder.Stream(stream, out var stats));
            diag?.WriteLine($"Revit 2022 layout: {stats.Records} records ({stats.RecordsRewritten} rewritten), "
                + $"{stats.RootBlocks} root blocks ({stats.RootBlocksRewritten}), {stats.OtherBlocks} other blocks ({stats.OtherBlocksRewritten}), "
                + $"{stats.UnparsedBytes} bytes outside the section grammar");
            if (stats.StoppedBytes != null)
                diag?.WriteLine($"  section grammar stopped at offset {stats.StoppedAt}: {stats.StoppedBytes}");
            foreach (var (reason, count) in stats.Failures.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(12))
                diag?.WriteLine($"  not rewritten: {count} x {reason}");
        }
        int partitionChunkCount = rewritten.Count;
        rewritten.AddRange(chunks.Skip(partitionStreams.Sum(s => s.Count)));
        var framed = new List<PartitionFraming.Framed?>(framedStreams.Count);
        foreach (var frame in framedStreams)
        {
            if (frame == null) { framed.Add(null); continue; }
            var rewrittenFrame = transcoder.Framed(frame, out var stats);
            framed.Add(rewrittenFrame);
            if (rewrittenFrame != null)
                diag?.WriteLine($"Revit 2022 layout of the project block: {stats.RootBlocks} root blocks ({stats.RootBlocksRewritten} rewritten), "
                    + $"{stats.OtherBlocks} other blocks ({stats.OtherBlocksRewritten})");
        }
        return (rewritten, partitionChunkCount, framed);
    }
}
