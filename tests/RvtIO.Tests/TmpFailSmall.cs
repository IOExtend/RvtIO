using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpFailSmall
{
    private readonly ITestOutputHelper _out;
    public TmpFailSmall(ITestOutputHelper output) => _out = output;

    private void Run(string label, string srcPath, string profilePath)
    {
        using var srcRoot = RootStorage.OpenRead(srcPath);
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        using var profRoot = RootStorage.OpenRead(profilePath);
        var profDoc = TfSchemaReader.Read(profRoot)!;
        var tc = new R22Transcoder(srcDoc, profDoc, 8, EntitySchemaReader.Read(srcRoot, srcDoc));
        var tags = PartitionFraming.TagsOf(new FormatsSchema(srcDoc))!.Value;
        var owners = new HashSet<ulong>();
        var dropped = new HashSet<ulong>();
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            var st = RvtIO.Writing.DowngradePartitionRewriter.Inspect(raw, tags, 8, tc);
            owners.UnionWith(st.Owners);
            dropped.UnionWith(st.Dropped);
        }
        var present = owners.Where(id => id <= uint.MaxValue && !dropped.Contains(id)).Select(id => (uint)id).ToHashSet();
        var droppedU = dropped.Where(id => id <= uint.MaxValue).Select(id => (uint)id).ToHashSet();
        tc.SetElementUniverse(present, present.Count == 0 ? 0u : present.Max(), droppedU);
        int converted = 0;
        var fails = new Dictionary<string, int>();
        var why = new Dictionary<string, int>();
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
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                string cls = srcDoc.ById.GetValueOrDefault(tag)?.Name ?? "?";
                if (tc.Payload(body, 0, body.Length, out string? failure) is not null) { converted++; return; }
                fails[cls] = fails.GetValueOrDefault(cls) + 1;
                why[(failure ?? "?").Split(" at ")[0]] = why.GetValueOrDefault((failure ?? "?").Split(" at ")[0]) + 1;
            }, out _, out _);
        }
        _out.WriteLine($"== {label}: converted={converted} dropItem={tc.DiagDropItem} minusOne={tc.DiagMinusOne}");
        foreach (var (k, v) in fails.OrderByDescending(kv => kv.Value).Take(15))
            _out.WriteLine($"  fail {k}: {v}");
        foreach (var (k, v) in why.OrderByDescending(kv => kv.Value).Take(10))
            _out.WriteLine($"  why: {v}: {k}");
    }

    [Fact]
    public void Check20() => Run("rst27to20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic27\rstbasic27.rvt",
        @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");

    [Fact]
    public void Check23() => Run("rst27to23", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic27\rstbasic27.rvt",
        @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic23\rstbasic23.rvt");
}
