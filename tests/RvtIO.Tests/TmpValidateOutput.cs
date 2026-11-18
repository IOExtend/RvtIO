using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpValidateOutput
{
    private readonly ITestOutputHelper _out;
    public TmpValidateOutput(ITestOutputHelper output) => _out = output;

    private void Run(string label, string path, int width)
    {
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var formats = new FormatsSchema(doc);
        var tags = PartitionFraming.TagsOf(formats)!.Value;
        var schemas = EntitySchemaReader.Read(root, doc);
        var walker = new WideBlockWalker(doc, (_, _) => null, []);
        if (schemas is not null) walker.UseEntitySchemas(schemas, passUnknown: true);

        var bad = new Dictionary<string, int>();
        var reasons = new Dictionary<string, int>();
        int ok = 0, fail = 0, shown = 0;
        using var s = root.OpenStorage("Partitions").OpenStream("86");
        var raw = new byte[s.Length];
        s.ReadExactly(raw);
        PartitionFraming.Walk(raw, tags, width, r =>
        {
            if (r.Owner == uint.MaxValue) return;
            if ((r.Channel is not (101 or 102 or 103)) || r.Body.Length < 2) return;
            var body = r.Body.ToArray();
            ushort rootTag = BinaryPrimitives.ReadUInt16LittleEndian(body);
            string channel = "ch" + r.Channel;
            if (doc.ById.GetValueOrDefault(rootTag) is null) { fail++; reasons[channel + " unknown root tag " + rootTag] = reasons.GetValueOrDefault(channel + " unknown root tag " + rootTag) + 1; return; }
            var walk = walker.Run(body, 0, body.Length, rootTag);
            if (walk.Exact && walk.Error is null) { ok++; return; }
            fail++;
            string cls = doc.ById[rootTag].Name;
            bad[channel + " " + cls] = bad.GetValueOrDefault(channel + " " + cls) + 1;
            string why = walk.Error ?? "not exact";
            reasons[channel + " " + why] = reasons.GetValueOrDefault(channel + " " + why) + 1;
            if (shown < 12)
            {
                _out.WriteLine($"  {channel} owner={r.Owner} class={cls} len={body.Length} exact={walk.Exact} err={walk.Error} stop={walk.StopField} pos={walk.Position}/{body.Length}");
                shown++;
            }
        }, out _, out _);
        _out.WriteLine($"== {label} partitions ok={ok} fail={fail}");
        foreach (var (k, v) in bad.OrderByDescending(kv => kv.Value).Take(15)) _out.WriteLine($"  bad {k}: {v}");
        foreach (var (k, v) in reasons.OrderByDescending(kv => kv.Value).Take(10)) _out.WriteLine($"  why {k}: {v}");

        foreach (var name in new[] { "Latest", "ContentDocuments", "PartitionTable", "DocumentIncrementTable", "ElemTable", "History" })
        {
            var data = PagedGzipStream.Read(root, "Global", name);
            if (data is null) continue;
            var walk = walker.Run(data, 0, data.Length, BinaryPrimitives.ReadUInt16LittleEndian(data));
            _out.WriteLine($"  global {name}: len={data.Length} exact={walk.Exact} err={walk.Error} stop={walk.StopField}");
        }
    }

    [Fact]
    public void Validate22() => Run("ours22", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_ent22.rvt", 4);

    [Fact]
    public void Validate20() => Run("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_ent20.rvt", 4);

    [Fact]
    public void Validate23() => Run("ours23", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_final_es23.rvt", 4);
}
