using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpGgpCompare
{
    private readonly ITestOutputHelper _out;
    public TmpGgpCompare(ITestOutputHelper output) => _out = output;

    private static Dictionary<ulong, byte[]> Load(string path, out SchemaDocument doc, int width)
    {
        using var root = RootStorage.OpenRead(path);
        doc = TfSchemaReader.Read(root)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(doc))!.Value;
        var storage = root.OpenStorage("Partitions");
        var map = new Dictionary<ulong, byte[]>();
        foreach (var entry in storage.EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = storage.OpenStream(entry.Name);
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
        var ours = Load(@"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_ours23.rvt", out var ourDoc, 4);
        var reference = Load(@"D:\IOExtend-samples\RvtIO\downloads\2023\GGP_1077-BFU_KPP1_AR_R20_R2023.rvt", out _, 4);
        int same = 0, diff = 0, missing = 0, shown = 0;
        var byClass = new Dictionary<string, (int Same, int Diff)>();
        foreach (var (owner, body) in ours)
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
            string cls = ourDoc.ById.GetValueOrDefault(tag)?.Name ?? "?";
            if (!reference.TryGetValue(owner, out var refBody)) { missing++; continue; }
            if (body.AsSpan().SequenceEqual(refBody)) { same++; var s = byClass.GetValueOrDefault(cls); byClass[cls] = (s.Same + 1, s.Diff); continue; }
            diff++;
            var d = byClass.GetValueOrDefault(cls);
            byClass[cls] = (d.Same, d.Diff + 1);
            if (shown < 10)
            {
                int n = Math.Min(body.Length, refBody.Length), first = -1;
                for (int i = 0; i < n; i++) if (body[i] != refBody[i]) { first = i; break; }
                _out.WriteLine($"  owner={owner} class={cls} ours={body.Length} ref={refBody.Length} first={first}");
                shown++;
            }
        }
        _out.WriteLine($"same={same} diff={diff} missingRef={missing}");
        foreach (var (cls, st) in byClass.OrderByDescending(kv => kv.Value.Diff).Take(30))
            _out.WriteLine($"  {cls}: same={st.Same} diff={st.Diff}");
    }
}
