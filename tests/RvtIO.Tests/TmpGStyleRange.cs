using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpGStyleRange
{
    private readonly ITestOutputHelper _out;
    public TmpGStyleRange(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        const string srcPath = @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt";
        using var srcRoot = RootStorage.OpenRead(srcPath);
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(srcDoc))!.Value;
        int gstyleId = srcDoc.Classes.First(c => c.Name == "GStyleElem").Id;
        var owners = new List<ulong>();
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, 8, r =>
            {
                if (r.Channel != 102 || r.Body.Length < 2) return;
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
                if (tag == gstyleId && r.Owner > 554404 && r.Owner <= 558835) owners.Add(r.Owner);
            }, out _, out _);
        }
        owners.Sort();
        _out.WriteLine($"count={owners.Count} min={owners.FirstOrDefault()} max={owners.LastOrDefault()}");
        if (owners.Count > 0)
        {
            _out.WriteLine($"mid={owners[owners.Count/2]} q25={owners[owners.Count/4]} q75={owners[owners.Count*3/4]}");
            _out.WriteLine("first10=" + string.Join(",", owners.Take(10)));
        }
    }
}
