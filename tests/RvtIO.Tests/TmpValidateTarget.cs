using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpValidateTarget
{
    private readonly ITestOutputHelper _out;
    public TmpValidateTarget(ITestOutputHelper output) => _out = output;

    private void Run(string label, string profilePath, int targetWidth)
    {
        const string srcPath = @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt";
        using var srcRoot = RootStorage.OpenRead(srcPath);
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        var srcFormats = new FormatsSchema(srcDoc);
        var tags = PartitionFraming.TagsOf(srcFormats)!.Value;
        using var profRoot = RootStorage.OpenRead(profilePath);
        var profDoc = TfSchemaReader.Read(profRoot)!;
        var schemas = EntitySchemaReader.Read(srcRoot, srcDoc);
        var transcoder = new R22Transcoder(srcDoc, profDoc, 8, schemas);
        var walker = new WideBlockWalker(profDoc, (_, _) => null, []);
        var writtenSchemas = EntitySchemaReader.Read(profRoot, profDoc);
        if (writtenSchemas is not null) walker.UseEntitySchemas(writtenSchemas, passUnknown: true);

        var bad = new Dictionary<string, int>();
        var reasons = new Dictionary<string, int>();
        int ok = 0, fail = 0, shown = 0;
        using var s = srcRoot.OpenStorage("Partitions").OpenStream("86");
        var raw = new byte[s.Length];
        s.ReadExactly(raw);
        PartitionFraming.Walk(raw, tags, 8, r =>
        {
            if (r.Channel != 102 || r.Body.Length < 2) return;
            var body = r.Body.ToArray();
            var ours = transcoder.Payload(body, 0, body.Length, out _);
            if (ours is null) return;
            ushort rootTag = BinaryPrimitives.ReadUInt16LittleEndian(ours);
            var walk = walker.Run(ours, 0, ours.Length, rootTag);
            if (walk.Exact && walk.Error is null) { ok++; return; }
            fail++;
            string cls = srcDoc.ById.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(body))?.Name ?? "?";
            bad[cls] = bad.GetValueOrDefault(cls) + 1;
            string why = walk.Error ?? "not exact";
            reasons[why] = reasons.GetValueOrDefault(why) + 1;
            if (shown < 10)
            {
                _out.WriteLine($"  owner={r.Owner} class={cls} len={ours.Length} exact={walk.Exact} err={walk.Error} stop={walk.StopField}");
                shown++;
            }
        }, out _, out _);
        _out.WriteLine($"== {label} ok={ok} fail={fail}");
        foreach (var (k, v) in bad.OrderByDescending(kv => kv.Value).Take(15)) _out.WriteLine($"  bad {k}: {v}");
        foreach (var (k, v) in reasons.OrderByDescending(kv => kv.Value).Take(10)) _out.WriteLine($"  why {k}: {v}");
    }

    [Fact]
    public void Validate22() => Run("22", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic22\rstbasic22.rvt", 4);

    [Fact]
    public void Validate20() => Run("20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt", 4);
}
