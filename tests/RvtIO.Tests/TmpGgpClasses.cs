using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpGgpClasses
{
    private readonly ITestOutputHelper _out;
    public TmpGgpClasses(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Histogram()
    {
        using var root = RootStorage.OpenRead(@"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt");
        var doc = TfSchemaReader.Read(root)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(doc))!.Value;
        var counts = new Dictionary<string, int>();
        foreach (var entry in root.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = root.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, 8, r =>
            {
                if (r.Channel != 102 || r.Body.Length < 2) return;
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
                string name = doc.ById.GetValueOrDefault(tag)?.Name ?? "?";
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }, out _, out _);
        }
        _out.WriteLine($"total={counts.Values.Sum()}");
        foreach (var (k, v) in counts.OrderByDescending(kv => kv.Value).Take(40))
            _out.WriteLine($"  {k}: {v}");
    }
}
