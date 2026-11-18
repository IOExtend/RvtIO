using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpMismatch20
{
    private readonly ITestOutputHelper _out;
    public TmpMismatch20(ITestOutputHelper output) => _out = output;

    private void Run(string label, string srcPath, string profilePath)
    {
        using var srcRoot = RootStorage.OpenRead(srcPath);
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        using var profRoot = RootStorage.OpenRead(profilePath);
        var profDoc = TfSchemaReader.Read(profRoot)!;
        var transcoder = new R22Transcoder(srcDoc, profDoc, 8, EntitySchemaReader.Read(srcRoot, srcDoc));
        var tags = PartitionFraming.TagsOf(new FormatsSchema(srcDoc))!.Value;
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
        _out.WriteLine($"== {label}: owners={knownOwners.Count} dropped={knownDropped.Count} present={present.Count}");
        int converted = 0;
        var byChannel = new Dictionary<int, int>();
        var fails = new Dictionary<string, int>();
        var failWhy = new Dictionary<string, int>();
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, 8, r =>
            {
                if ((r.Channel is not (102 or 103)) || r.Body.Length < 2) return;
                var body = r.Body.ToArray();
                ushort tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body);
                string cls = srcDoc.ById.GetValueOrDefault(tag)?.Name ?? "?";
                byChannel[r.Channel] = byChannel.GetValueOrDefault(r.Channel) + 1;
                if (transcoder.Payload(body, 0, body.Length, out string? failure) is not null) { converted++; return; }
                fails[cls] = fails.GetValueOrDefault(cls) + 1;
                failWhy[failure ?? "?"] = failWhy.GetValueOrDefault(failure ?? "?") + 1;
            }, out _, out _);
        }
        _out.WriteLine($"  converted={converted} dropItem={transcoder.DiagDropItem} minusOne={transcoder.DiagMinusOne}");
        foreach (var (k, v) in fails.OrderByDescending(kv => kv.Value).Take(15))
            _out.WriteLine($"  fail {k}: {v}");
        foreach (var (k, v) in failWhy.OrderByDescending(kv => kv.Value).Take(10))
            _out.WriteLine($"  why: {v}: {k}");
        foreach (var (k, v) in transcoder.PointerMismatches.OrderByDescending(kv => kv.Value).Take(30))
            _out.WriteLine($"  {v}: {k}");
        transcoder.PointerMismatches.Clear();

        var latest = PagedGzipStream.Read(srcRoot, "Global", "Latest")!;
        transcoder.PayloadWithTail(latest, 0, latest.Length, out _);
        _out.WriteLine($"  latest mismatches={transcoder.PointerMismatches.Count}");
        foreach (var (k, v) in transcoder.PointerMismatches.OrderByDescending(kv => kv.Value).Take(15))
            _out.WriteLine($"  latest {v}: {k}");
    }

    [Fact]
    public void CheckDach() => Run("dach", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt",
        @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");

    [Fact]
    public void CheckGgp() => Run("ggp", @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt",
        @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");
}
