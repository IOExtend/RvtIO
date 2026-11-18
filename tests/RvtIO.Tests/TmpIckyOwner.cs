using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpIckyOwner
{
    private readonly ITestOutputHelper _out;
    public TmpIckyOwner(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        const string srcPath = @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt";
        using var srcRoot = RootStorage.OpenRead(srcPath);
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        using var profRoot = RootStorage.OpenRead(@"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");
        var profDoc = TfSchemaReader.Read(profRoot)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(srcDoc))!.Value;
        var transcoder0 = new R22Transcoder(srcDoc, profDoc, 8, EntitySchemaReader.Read(srcRoot, srcDoc));
        // replicate DowngradeWriter: all channel-101 owners minus dropped
        var knownOwners = new HashSet<ulong>();
        var knownDropped = new HashSet<ulong>();
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            var state = RvtIO.Writing.DowngradePartitionRewriter.Inspect(raw, tags, 8, transcoder0);
            knownOwners.UnionWith(state.Owners);
            knownDropped.UnionWith(state.Dropped);
        }
        var present = knownOwners.Where(id => id <= uint.MaxValue && !knownDropped.Contains(id)).Select(id => (uint)id).ToHashSet();
        _out.WriteLine($"owners={knownOwners.Count} dropped={knownDropped.Count} present={present.Count}");
        _out.WriteLine($"inspect diagDrop={transcoder0.DiagDropItem} diagMinusOne={transcoder0.DiagMinusOne}");
        byte[]? body = null;
        ulong owner = 0;
        string rootName = "";
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, 8, r =>
            {
                if (r.Channel != 102 || r.Body.Length < 2 || body != null) return;
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
                var walker = new WideBlockWalker(srcDoc, (_, _) => null, []);
                var walk = walker.Run(r.Body.ToArray(), 0, r.Body.Length, tag);
                if (walk.Queue.Any(t => walker.Class(t)?.Name == "IckyExcludedCategoriesSetPtrWrapper"))
                { body = r.Body.ToArray(); owner = r.Owner; rootName = walker.Class(tag)?.Name ?? "?"; }
            }, out _, out _);
            if (body != null) break;
        }
        _out.WriteLine($"owner={owner} root={rootName} len={body?.Length}");
        if (body is null) return;

        var transcoder = new R22Transcoder(srcDoc, profDoc, 8, EntitySchemaReader.Read(srcRoot, srcDoc));
        transcoder.SetElementUniverse(present, present.Count == 0 ? 0u : present.Max());
        // find a root whose m_categoryIds holds a POSITIVE id missing from present
        body = null;
        var missing = new List<long>();
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream || body != null) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, 8, r =>
            {
                if (r.Channel != 102 || r.Body.Length < 2 || body != null) return;
                var probe = r.Body.ToArray();
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(probe);
                var walker = new WideBlockWalker(srcDoc, (d, f) => d.Name + "." + f.Name, []);
                var walk = walker.Run(probe, 0, probe.Length, tag);
                foreach (var cap in walk.Captures.Where(c => c.Field == "m_categoryIds" && c.Kind == WideBlockWalker.CaptureKind.Integer && c.Integer > 0))
                {
                    uint id = (uint)cap.Integer;
                    if (id <= present.Max() && !present.Contains(id))
                    { body = probe; owner = r.Owner; rootName = walker.Class(tag)?.Name ?? "?"; missing.Add(cap.Integer); break; }
                }
            }, out _, out _);
        }
        _out.WriteLine($"owner={owner} root={rootName} len={body?.Length} missingSample=[{string.Join(",", missing.Take(5))}]");
        // where is 67?
        var probeId = missing.Count > 0 ? (ulong)missing[0] : 0;
        _out.WriteLine($"  67 in knownOwners={knownOwners.Contains(probeId)} in dropped={knownDropped.Contains(probeId)}");
        using (var s = srcRoot.OpenStorage("Partitions").OpenStream("0"))
        {
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            var channels = new Dictionary<int, int>();
            PartitionFraming.Walk(raw, tags, 8, r =>
            {
                if (r.Owner == probeId) channels[r.Channel] = channels.GetValueOrDefault(r.Channel) + 1;
            }, out _, out _);
            _out.WriteLine("  67 source records by channel: " + string.Join(",", channels.Select(kv => kv.Key + "x" + kv.Value)));
        }
        if (body is null) return;
        var result = transcoder.Payload(body, 0, body.Length, out string? failure);
        _out.WriteLine($"transcoded={result?.Length} failure={failure}");
        _out.WriteLine($"payload diagDrop={transcoder.DiagDropItem} diagMinusOne={transcoder.DiagMinusOne}");
        if (result is null) return;
        var walker2 = new WideBlockWalker(profDoc, (d, f) => d.Name + "." + f.Name, []);
        var walk2 = walker2.Run(result, 0, result.Length, BinaryPrimitives.ReadUInt16LittleEndian(result));
        _out.WriteLine($"walk exact={walk2.Exact} err={walk2.Error}");
        foreach (var cap in walk2.Captures.Where(c => c.Field == "m_categoryIds"))
            _out.WriteLine($"  {cap.Key} kind={cap.Kind} val={cap.Integer}");
        // count check: source by walker with entity schemas vs transcoded items
        var srcWalker = new WideBlockWalker(srcDoc, (d, f) => d.Name + "." + f.Name, []);
        var srcSchemas = EntitySchemaReader.Read(srcRoot, srcDoc);
        if (srcSchemas is not null) srcWalker.UseEntitySchemas(srcSchemas, passUnknown: true);
        var srcWalk = srcWalker.Run(body, 0, body.Length, BinaryPrimitives.ReadUInt16LittleEndian(body));
        _out.WriteLine($"  src walk exact={srcWalk.Exact} err={srcWalk.Error}");
        _out.WriteLine("  src counts: " + string.Join(",", srcWalk.Captures.Where(c => c.Field == "m_categoryIds#").Select(c => c.Integer)));
        var srcVals = srcWalk.Captures.Where(c => c.Key.StartsWith("IckyExcludedCategoriesSetPtrWrapper.m_categoryIds@")).Select(c => c.Integer).ToList();
        _out.WriteLine($"  src items found={srcVals.Count} first=[{string.Join(",", srcVals.Take(8))}]");
        var outVals = walk2.Captures.Where(c => c.Key.StartsWith("IckyExcludedCategoriesSetPtrWrapper.m_categoryIds@")).Select(c => c.Integer).ToList();
        _out.WriteLine($"  out items found={outVals.Count} first=[{string.Join(",", outVals.Take(8))}]");
        // dump ALL items of the transcoded array via spans
        var outRoot = BinaryPrimitives.ReadUInt16LittleEndian(result);
        var outFields = RvtIO.Writing.PayloadSpan.Fields(result, 2, walk2.QueueStart.Count > 0 ? walk2.QueueStart[0] : result.Length, profDoc, outRoot);
        _out.WriteLine("  out root fields: " + string.Join(",", outFields.Select(f => f.Name)));
        // find the queued Icky object and dump its array items
        for (int i = 0; i < walk2.Queue.Count; i++)
        {
            string? cls = null;
            try { cls = profDoc.ById[walk2.Queue[i]].Name; } catch { }
            if (cls != "IckyExcludedCategoriesSetPtrWrapper") continue;
            int start = walk2.QueueStart[i];
            int end = i + 1 < walk2.QueueStart.Count ? walk2.QueueStart[i + 1] : result.Length;
            int ickyId = profDoc.Classes.First(c => c.Name == "IckyExcludedCategoriesSetPtrWrapper").Id;
            foreach (var (fname, ffrom, fto) in RvtIO.Writing.PayloadSpan.Fields(result, start, end, profDoc, ickyId))
            {
                if (fname != "m_categoryIds") continue;
                int n = BitConverter.ToInt32(result, ffrom);
                _out.WriteLine($"  transcoded m_categoryIds count={n} at {ffrom}");
                var vals = new List<long>();
                for (int k = 0; k < Math.Min(n, 200); k++)
                    vals.Add(BitConverter.ToInt32(result, ffrom + 4 + 4 * k));
                _out.WriteLine($"  vals=[{string.Join(",", vals)}] has67={vals.Contains(67)}");
            }
        }
    }
}
