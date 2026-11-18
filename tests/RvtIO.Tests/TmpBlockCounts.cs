using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpBlockCounts
{
    private readonly ITestOutputHelper _out;
    public TmpBlockCounts(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        const string path = @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_low20.rvt";
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(doc))!.Value;
        using var s = root.OpenStorage("Partitions").OpenStream("86");
        var stored = new byte[s.Length];
        s.ReadExactly(stored);
        var body = PartitionFraming.RemoveTails(stored)!;
        int pos = 8;
        long headerSum = 0;
        int blocks = 0;
        var perChannel = new Dictionary<int, int>();
        for (; ; )
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2)) != tags.ContentMarker) break;
            pos += 2;
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0) pos += 2;
            uint ownersCount = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0) pos += 16;
            if (ownersCount == uint.MaxValue) break;
            headerSum += ownersCount;
            blocks++;
            // skip chains
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
        }
        var actual = new Dictionary<int, int>();
        PartitionFraming.Walk(stored, tags, 4, r =>
        {
            if (r.Owner == uint.MaxValue) return;
            actual[r.Channel] = actual.GetValueOrDefault(r.Channel) + 1;
        }, out _, out _);
        _out.WriteLine($"blocks={blocks} headerOwnerSum={headerSum}");
        foreach (var (ch, n) in actual.OrderBy(kv => kv.Key)) _out.WriteLine($"  channel {ch}: records={n}");
    }
}
