using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpGStyleDetail
{
    private readonly ITestOutputHelper _out;
    public TmpGStyleDetail(ITestOutputHelper output) => _out = output;

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
        var srcWalker = new WideBlockWalker(srcDoc, (d, f) => d.Name + "." + f.Name, []);
        foreach (var (owner, body) in bodies.OrderBy(b => b.Owner).Take(8))
        {
            var walk = srcWalker.Run(body, 0, body.Length, BinaryPrimitives.ReadUInt16LittleEndian(body));
            var ints = walk.Captures.Where(c => c.Kind == WideBlockWalker.CaptureKind.Integer).ToList();
            string Get(string field)
            {
                var c = ints.FirstOrDefault(c => c.Field == field);
                return c.Field is null ? field + "?" : field + "=" + c.Integer;
            }
            _out.WriteLine($"src owner={owner} exact={walk.Exact} " + string.Join(" ", new[] { Get("m_categoryId"), Get("m_ownerId"), Get("m_gstyleType"), Get("m_linePatternId"), Get("m_materialElemId"), Get("m_penNumber"), Get("m_color"), Get("m_isScreenSized") }));
            var result = transcoder.Payload(body, 0, body.Length, out string? failure);
            if (result is null) { _out.WriteLine($"  transcode FAIL {failure}"); continue; }
            var tw = new WideBlockWalker(profDoc, (d, f) => d.Name + "." + f.Name, []);
            var t2 = tw.Run(result, 0, result.Length, BinaryPrimitives.ReadUInt16LittleEndian(result));
            var ti = t2.Captures.Where(c => c.Kind == WideBlockWalker.CaptureKind.Integer).ToList();
            string Tg(string field)
            {
                var c = ti.FirstOrDefault(c => c.Field == field);
                return c.Field is null ? field + "?" : field + "=" + c.Integer;
            }
            _out.WriteLine($"  out exact={t2.Exact} " + string.Join(" ", new[] { Tg("m_categoryId"), Tg("m_ownerId"), Tg("m_gstyleType"), Tg("m_linePatternId"), Tg("m_materialElemId"), Tg("m_penNumber"), Tg("m_color"), Tg("m_isScreenSized") }));
        }
        // native 2020 sample for comparison
        using var natRoot = RootStorage.OpenRead(@"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");
        var natDoc = TfSchemaReader.Read(natRoot)!;
        var natTags = PartitionFraming.TagsOf(new FormatsSchema(natDoc))!.Value;
        int natGs = natDoc.Classes.First(c => c.Name == "GStyleElem").Id;
        int shown = 0;
        foreach (var entry in natRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream || shown >= 5) continue;
            using var s = natRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, natTags, 4, r =>
            {
                if (shown >= 5 || r.Channel != 102 || r.Body.Length < 2) return;
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
                if (tag != natGs) return;
                var w = new WideBlockWalker(natDoc, (d, f) => d.Name + "." + f.Name, []);
                var wk = w.Run(r.Body.ToArray(), 0, r.Body.Length, tag);
                var ii = wk.Captures.Where(c => c.Kind == WideBlockWalker.CaptureKind.Integer).ToList();
                string G(string field)
                {
                    var c = ii.FirstOrDefault(c => c.Field == field);
                    return c.Field is null ? field + "?" : field + "=" + c.Integer;
                }
                _out.WriteLine($"nat owner={r.Owner} " + string.Join(" ", new[] { G("m_categoryId"), G("m_ownerId"), G("m_gstyleType"), G("m_linePatternId"), G("m_materialElemId"), G("m_penNumber"), G("m_color"), G("m_isScreenSized") }));
                shown++;
            }, out _, out _);
        }
    }
}
