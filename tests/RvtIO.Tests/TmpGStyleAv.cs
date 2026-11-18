using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpGStyleAv
{
    private readonly ITestOutputHelper _out;
    public TmpGStyleAv(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        const string srcPath = @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt";
        const string profilePath = @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt";
        using var srcRoot = RootStorage.OpenRead(srcPath);
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        using var profRoot = RootStorage.OpenRead(profilePath);
        var profDoc = TfSchemaReader.Read(profRoot)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(srcDoc))!.Value;
        var transcoder = new R22Transcoder(srcDoc, profDoc, 8, EntitySchemaReader.Read(srcRoot, srcDoc));
        var knownOwners = new HashSet<ulong>();
        var knownDropped = new HashSet<ulong>();
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            var state = RvtIO.Writing.DowngradePartitionRewriter.Inspect(raw, tags, 8, transcoder);
            knownOwners.UnionWith(state.Owners);
            knownDropped.UnionWith(state.Dropped);
        }
        var present = knownOwners.Where(id => id <= uint.MaxValue && !knownDropped.Contains(id)).Select(id => (uint)id).ToHashSet();
        transcoder.SetElementUniverse(present, present.Count == 0 ? 0u : present.Max());
        _out.WriteLine($"owners={knownOwners.Count} dropped={knownDropped.Count} present={present.Count}");

        int gstyleId = srcDoc.Classes.First(c => c.Name == "GStyleElem").Id;
        var bodies = new List<(ulong Owner, byte[] Body)>();
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, 8, r =>
            {
                if (r.Channel != 102 || r.Body.Length < 2) return;
                if (r.Owner <= 557939 || r.Owner > 558243) return;
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
                if (tag == gstyleId) bodies.Add((r.Owner, r.Body.ToArray()));
            }, out _, out _);
        }
        _out.WriteLine($"candidates={bodies.Count}");
        int ok = 0, fail = 0;
        foreach (var (owner, body) in bodies.OrderBy(b => b.Owner))
        {
            var result = transcoder.Payload(body, 0, body.Length, out string? failure);
            if (result is null) { fail++; _out.WriteLine($"owner={owner} FAIL {failure}"); continue; }
            ok++;
            // walk transcoded with target doc
            var walker = new WideBlockWalker(profDoc, (d, f) => d.Name + "." + f.Name, []);
            var walk = walker.Run(result, 0, result.Length, BinaryPrimitives.ReadUInt16LittleEndian(result));
            if (!walk.Exact || walk.Error is not null)
                _out.WriteLine($"owner={owner} len={body.Length}->{result.Length} walkExact={walk.Exact} err={walk.Error} stop={walk.StopField}");
            else if (ok <= 5)
            {
                var vals = walk.Captures.Where(c => c.Kind == WideBlockWalker.CaptureKind.Integer).Take(12).Select(c => c.Field + "=" + c.Integer);
                _out.WriteLine($"owner={owner} len={body.Length}->{result.Length} ok [{string.Join(",", vals)}]");
            }
        }
        _out.WriteLine($"ok={ok} fail={fail} mismatches={transcoder.PointerMismatches.Count}");
        foreach (var (k, v) in transcoder.PointerMismatches.OrderByDescending(kv => kv.Value).Take(10))
            _out.WriteLine($"  mismatch {v}: {k}");
    }
}
