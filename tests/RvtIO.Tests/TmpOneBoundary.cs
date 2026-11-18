using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpOneBoundary
{
    private readonly ITestOutputHelper _out;
    public TmpOneBoundary(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        const string srcPath = @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic27\rstbasic27.rvt";
        using var srcRoot = RootStorage.OpenRead(srcPath);
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        var srcTags = PartitionFraming.TagsOf(new FormatsSchema(srcDoc))!.Value;
        // collect bodies for owners 1404740 (BoundaryConditions) + a FamilyInstance child
        var wanted = new HashSet<ulong> { 1404740, 1404741 };
        var bodies = new Dictionary<ulong, byte[]>();
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, srcTags, 8, r =>
            {
                if (r.Channel != 102 || !wanted.Contains(r.Owner) || r.Body.Length < 2) return;
                if (!bodies.ContainsKey(r.Owner)) bodies[r.Owner] = r.Body.ToArray();
            }, out _, out _);
        }
        foreach (var (owner, body) in bodies)
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
            _out.WriteLine($"src owner={owner} class={srcDoc.ById.GetValueOrDefault(tag)?.Name} len={body.Length}");
        }
        foreach (var (label, profilePath) in new[]
        {
            ("tgt20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
            ("tgt23", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic23\rstbasic23.rvt"),
        })
        {
            using var profRoot = RootStorage.OpenRead(profilePath);
            var profDoc = TfSchemaReader.Read(profRoot)!;
            var tc = new R22Transcoder(srcDoc, profDoc, 8, EntitySchemaReader.Read(srcRoot, srcDoc));
            // replicate writer universe: inspect all partitions (no forced drops), set universe, no closure (first order)
            var tags = srcTags;
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
            _out.WriteLine($"== {label}: owners={owners.Count} dropped={dropped.Count} present={present.Count}");
            foreach (var (owner, body) in bodies.OrderBy(kv => kv.Key))
            {
                var res = tc.Payload(body, 0, body.Length, out string? failure);
                _out.WriteLine($"  {label} owner={owner} ok={res is not null} len={res?.Length} failure={failure} dropItem={tc.DiagDropItem} minusOne={tc.DiagMinusOne}");
            }
        }
    }
}
