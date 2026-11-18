using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpCompareTargets
{
    private readonly ITestOutputHelper _out;
    public TmpCompareTargets(ITestOutputHelper output) => _out = output;

    private static Dictionary<ulong, byte[]> Load(string path, out SchemaDocument doc)
    {
        using var root = RootStorage.OpenRead(path);
        doc = TfSchemaReader.Read(root)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(doc))!.Value;
        int width = SchemaRelease.IdWidth(SchemaRelease.FromSchema(new FormatsSchema(doc))) ?? 4;
        var map = new Dictionary<ulong, byte[]>();
        foreach (var entry in root.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = root.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, width, r =>
            {
                if (r.Channel != 102 || r.Owner == uint.MaxValue || r.Body.Length < 2) return;
                if (!map.ContainsKey(r.Owner)) map[r.Owner] = r.Body.ToArray();
            }, out _, out _);
        }
        return map;
    }

    [Fact]
    public void Compare()
    {
        var map23 = Load(@"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\rst27_to23.rvt", out var doc23);
        var map20 = Load(@"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\rst27_to20.rvt", out var doc20);
        _out.WriteLine($"to23 owners={map23.Count} to20 owners={map20.Count}");
        var only23 = map23.Keys.Where(o => !map20.ContainsKey(o)).Take(20).ToList();
        var only20 = map20.Keys.Where(o => !map23.ContainsKey(o)).Take(20).ToList();
        _out.WriteLine($"only23={map23.Keys.Count(o => !map20.ContainsKey(o))} e.g.[{string.Join(",", only23.Take(10))}]");
        _out.WriteLine($"only20={map20.Keys.Count(o => !map23.ContainsKey(o))} e.g.[{string.Join(",", only20.Take(10))}]");
        // class of only23 owners via doc23 tags
        foreach (var o in only23.Take(10))
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(map23[o]);
            _out.WriteLine($"  only23 owner={o} class={doc23.ById.GetValueOrDefault(tag)?.Name}");
        }
        // same-owner payload diff sample: count -1s and length diffs
        int same = 0, lenDiff = 0, shown = 0;
        var minusOneOnly20 = new Dictionary<string, int>();
        foreach (var (owner, b23) in map23)
        {
            if (!map20.TryGetValue(owner, out var b20)) continue;
            same++;
            if (b23.Length != b20.Length) lenDiff++;
            // quick scan for -1 (0xFFFFFFFF) in 2020 body
            if (shown < 5 && b20.Length != b23.Length)
            {
                ushort t23 = BinaryPrimitives.ReadUInt16LittleEndian(b23);
                ushort t20 = BinaryPrimitives.ReadUInt16LittleEndian(b20);
                _out.WriteLine($"  same owner={owner} class23={doc23.ById.GetValueOrDefault(t23)?.Name} class20={doc20.ById.GetValueOrDefault(t20)?.Name} len23={b23.Length} len20={b20.Length}");
                shown++;
            }
        }
        _out.WriteLine($"same={same} lenDiff={lenDiff}");
    }
}
