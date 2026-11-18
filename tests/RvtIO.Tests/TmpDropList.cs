using System.Buffers.Binary;
using System.Reflection;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpDropList
{
    private readonly ITestOutputHelper _out;
    public TmpDropList(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        const string srcPath = @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt";
        const string profilePath = @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt";
        using var srcRoot = RootStorage.OpenRead(srcPath);
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        using var profRoot = RootStorage.OpenRead(profilePath);
        var profDoc = TfSchemaReader.Read(profRoot)!;
        var transcoder = new R22Transcoder(srcDoc, profDoc, 8, EntitySchemaReader.Read(srcRoot, srcDoc));
        var tags = PartitionFraming.TagsOf(new FormatsSchema(srcDoc))!.Value;

        var dropped = new HashSet<ulong>();
        var classOf = new Dictionary<ulong, string>();
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            var state = DowngradePartitionRewriter.Inspect(raw, tags, 8, transcoder);
            dropped.UnionWith(state.Dropped);
            PartitionFraming.Walk(raw, tags, 8, r =>
            {
                if (r.Channel != 102 || r.Body.Length < 2 || classOf.ContainsKey(r.Owner)) return;
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
                classOf[r.Owner] = srcDoc.ById.GetValueOrDefault(tag)?.Name ?? ("tag" + tag);
            }, out _, out _);
        }
        var byClass = new Dictionary<string, int>();
        foreach (var id in dropped)
            byClass[classOf.GetValueOrDefault(id, "?")] = byClass.GetValueOrDefault(classOf.GetValueOrDefault(id, "?")) + 1;
        _out.WriteLine($"dropped owners={dropped.Count}");
        foreach (var (k, v) in byClass.OrderByDescending(kv => kv.Value).Take(30))
            _out.WriteLine($"  {k}: {v}");
    }
}
