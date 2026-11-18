using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpBisectBlocks
{
    private readonly ITestOutputHelper _out;
    public TmpBisectBlocks(ITestOutputHelper output) => _out = output;

    [Fact]
    public void SplitRun()
    {
        SplitBlocks("dach_ent22", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_ent22.rvt", 0.5);
    }

    internal static string SplitBlocks(string label, string srcPath, double keepFraction, int? keepBlocks = null)
    {
        using var root = RootStorage.OpenRead(srcPath);
        var doc = TfSchemaReader.Read(root)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(doc))!.Value;
        using var stream = root.OpenStorage("Partitions").OpenStream("86");
        var stored = new byte[stream.Length];
        stream.ReadExactly(stored);
        var body = PartitionFraming.RemoveTails(stored)!;

        // split into blocks: each block = header + chains + signature
        var blocks = new List<(int Start, int End)>();
        int pos = 8;
        for (; ; )
        {
            int blockStart = pos;
            if (BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2)) != tags.ContentMarker) break;
            pos += 2;
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0) pos += 2;
            uint owners = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0) pos += 16;
            if (owners == uint.MaxValue) { blocks.Add((blockStart, pos)); break; }
            while (true)
            {
                pos += 2;
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
                pos += 4;
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4;
                pos += 4;
                if (size == 0) { if ((flags & 3) != 0) throw new InvalidDataException("chained empty"); break; }
                pos += 8;
                pos += (int)size - 8 + 6;
            }
            pos += 2;
            uint sb = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4 + (int)sb;
            uint sc = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4)); pos += 4 + 2 * (int)sc;
            blocks.Add((blockStart, pos));
        }
        int dataBlocks = blocks.Count - 1;
        int keep = keepBlocks ?? (int)(dataBlocks * keepFraction);
        using var output = new MemoryStream();
        output.Write(body, 0, 8);
        for (int i = 0; i < keep && i < dataBlocks; i++)
            output.Write(body, blocks[i].Start, blocks[i].End - blocks[i].Start);
        var term = blocks[^1];
        output.Write(body, term.Start, term.End - term.Start);
        var encoded = PageCodec.EncodePadded(output.ToArray());
        string dst = Path.Combine(Path.GetDirectoryName(srcPath)!, $"bisect_{label}_{keep}of{dataBlocks}.rvt");
        File.Delete(dst);
        var result = ContainerRewriter.Rewrite(srcPath, dst, new Dictionary<string, byte[]> { ["Partitions/86"] = encoded });
        return dst;
    }
}
