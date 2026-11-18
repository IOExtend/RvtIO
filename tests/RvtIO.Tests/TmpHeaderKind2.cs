using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpHeaderKind2
{
    private readonly ITestOutputHelper _out;
    public TmpHeaderKind2(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        const string path = @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_big4_20.rvt";
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(doc))!.Value;
        using var s = root.OpenStorage("Partitions").OpenStream("0");
        var raw = new byte[s.Length];
        s.ReadExactly(raw);
        var headers = new Dictionary<ulong, int>();
        var data = new Dictionary<ulong, int>();
        PartitionFraming.Walk(raw, tags, 4, r =>
        {
            if (r.Body.Length < 2 || r.Owner == uint.MaxValue) return;
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
            if (r.Channel == 101)
            {
                var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, []);
                var walk = walker.Run(r.Body.ToArray(), 0, r.Body.Length, tag);
                var cap = walk.Captures.FirstOrDefault(c => c.Key == "ClassDefinitionRef.m_ref");
                if (cap.Key is not null) headers[r.Owner] = (int)cap.Integer;
            }
            else if (r.Channel == 102) data[r.Owner] = tag;
        }, out _, out _);
        _out.WriteLine($"headers={headers.Count} data={data.Count}");
        int mismatches = 0;
        var reasons = new Dictionary<string, int>();
        foreach (var (owner, headerTag) in headers)
        {
            if (!data.TryGetValue(owner, out int dataTag)) continue;
            if (headerTag == dataTag) continue;
            mismatches++;
            string key = $"header={doc.ById.GetValueOrDefault(headerTag)?.Name}({headerTag}) data={doc.ById.GetValueOrDefault(dataTag)?.Name}({dataTag})";
            reasons[key] = reasons.GetValueOrDefault(key) + 1;
        }
        _out.WriteLine($"mismatches={mismatches}");
        foreach (var (k, v) in reasons.OrderByDescending(kv => kv.Value).Take(20))
            _out.WriteLine($"  {v}: {k}");
        // header tag itself unknown?
        int unknownHeader = headers.Count(h => !doc.ById.ContainsKey(h.Value));
        int unknownData = data.Count(kv => !doc.ById.ContainsKey(kv.Value));
        _out.WriteLine($"unknownHeaderTags={unknownHeader} unknownDataTags={unknownData}");
    }
}
