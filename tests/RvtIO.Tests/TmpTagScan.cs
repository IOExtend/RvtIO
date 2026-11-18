using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpTagScan
{
    private readonly ITestOutputHelper _out;
    public TmpTagScan(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Scan()
    {
        foreach (var (label, path) in new[]
        {
            ("ggpnotags", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_notags_20.rvt"),
            ("ggpnotagsfix", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_notags_fix20.rvt"),
            ("ggpdrop", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_drop20.rvt"),
            ("dachlow20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_low20.rvt"),
            ("dachincr20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_incr20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var tags = PartitionFraming.TagsOf(new FormatsSchema(doc))!.Value;
            var counts = new Dictionary<string, int>();
            int unknown = 0, total = 0;
            foreach (var entry in root.OpenStorage("Partitions").EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream) continue;
                using var s = root.OpenStorage("Partitions").OpenStream(entry.Name);
                var raw = new byte[s.Length];
                s.ReadExactly(raw);
                PartitionFraming.Walk(raw, tags, 4, r =>
                {
                    if (r.Channel != 102 || r.Body.Length < 2 || r.Owner == uint.MaxValue) return;
                    total++;
                    ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
                    if (tag == 0xFFFE) { unknown++; return; }
                    string name = doc.ById.GetValueOrDefault(tag)?.Name ?? ("tag" + tag);
                    counts[name] = counts.GetValueOrDefault(name) + 1;
                }, out _, out _);
            }
            _out.WriteLine($"== {label}: total={total} unknownTag={unknown} classes={counts.Count}");
            foreach (var (k, v) in counts.Where(kv => kv.Key.StartsWith("tag")).Take(10))
                _out.WriteLine($"  unresolved {k}: {v}");
            if (unknown > 0)
            {
                foreach (var entry in root.OpenStorage("Partitions").EnumerateEntries())
                {
                    if (entry.Type != EntryType.Stream) continue;
                    using var s = root.OpenStorage("Partitions").OpenStream(entry.Name);
                    var raw = new byte[s.Length];
                    s.ReadExactly(raw);
                    PartitionFraming.Walk(raw, tags, 4, r =>
                    {
                        if (r.Body.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span) == 0xFFFE)
                            _out.WriteLine($"  unknown: block={r.Block} ch={r.Channel} owner={r.Owner} len={r.Body.Length}");
                    }, out _, out _);
                }
            }
        }
    }
}
