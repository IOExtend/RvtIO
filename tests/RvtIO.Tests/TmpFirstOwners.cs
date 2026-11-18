using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpFirstOwners
{
    private readonly ITestOutputHelper _out;
    public TmpFirstOwners(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        foreach (var (label, path) in new[]
        {
            ("ggp20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_drop20.rvt"),
            ("dach20big4", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\drop20_big4.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var formats = new FormatsSchema(doc);
            var tags = PartitionFraming.TagsOf(formats)!.Value;
            var first = new Dictionary<int, List<(ulong, string)>>();
            foreach (var entry in root.OpenStorage("Partitions").EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream) continue;
                using var s = root.OpenStorage("Partitions").OpenStream(entry.Name);
                var raw = new byte[s.Length];
                s.ReadExactly(raw);
                PartitionFraming.Walk(raw, tags, 4, r =>
                {
                    if (r.Owner == uint.MaxValue || r.Body.Length < 2) return;
                    if (!first.TryGetValue(r.Channel, out var list)) first[r.Channel] = list = [];
                    if (list.Count < 8)
                    {
                        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
                        list.Add((r.Owner, doc.ById.GetValueOrDefault(tag)?.Name ?? ("tag" + tag)));
                    }
                }, out _, out _);
            }
            _out.WriteLine($"== {label}");
            foreach (var (ch, list) in first.OrderBy(kv => kv.Key))
                _out.WriteLine($"  ch{ch}: " + string.Join(",", list.Select(x => $"{x.Item1}={x.Item2}")));
        }
    }
}
