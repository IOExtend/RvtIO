using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpOneCategory
{
    private readonly ITestOutputHelper _out;
    public TmpOneCategory(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        const string srcPath = @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt";
        using var srcRoot = RootStorage.OpenRead(srcPath);
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        using var profRoot = RootStorage.OpenRead(@"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");
        var profDoc = TfSchemaReader.Read(profRoot)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(srcDoc))!.Value;

        // find a payload whose class has m_categoryIds
        var probe = srcDoc.Classes.FirstOrDefault(c => c.Name == "IckyExcludedCategoriesSetPtrWrapper");
        bool chainOk = probe is not null && ChainHas(srcDoc, probe.Id, "m_categoryIds");
        _out.WriteLine($"probe class id={(probe is null ? -1 : probe.Id)} chainHas={chainOk}");
        var probe2 = srcDoc.Classes.Where(c => c.Name.Contains("Icky") || c.Name.Contains("CategoriesSet")).ToList();
        _out.WriteLine("icky-like: " + string.Join(",", probe2.Select(c => c.Name + "#" + c.Id)));
        byte[]? body = null;
        ulong owner = 0;
        string foundIn = "";
        int seen = 0;
        int walked = 0;
        var tagNames = new Dictionary<string, int>();
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, 8, r =>
            {
                if (r.Channel != 102 || r.Body.Length < 2 || body != null) return;
                walked++;
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
                var cls = srcDoc.ById.GetValueOrDefault(tag);
                if (cls is null) { if (seen++ < 3) _out.WriteLine($"  null class for tag {tag}"); }
                else tagNames[cls.Name] = tagNames.GetValueOrDefault(cls.Name) + 1;
                if (cls != null && ChainHas(srcDoc, cls.Id, "m_categoryIds")) { body = r.Body.ToArray(); owner = r.Owner; foundIn = entry.Name; }
            }, out _, out _);
            if (body != null) break;
        }
        _out.WriteLine($"owner={owner} len={body?.Length} stream={foundIn} walked={walked} distinctClasses={tagNames.Count} hasIcky={tagNames.ContainsKey("IckyExcludedCategoriesSetPtrWrapper")}");
        if (body is null) return;

        var transcoder = new R22Transcoder(srcDoc, profDoc, 8, EntitySchemaReader.Read(srcRoot, srcDoc));
        // universe: only this owner present, everything else dropped
        transcoder.SetElementUniverse(new HashSet<uint> { (uint)owner }, (uint)owner);
        var result = transcoder.Payload(body, 0, body.Length, out string? failure);
        _out.WriteLine($"transcoded={result?.Length} failure={failure}");
        if (result is null) return;
        var walker = new WideBlockWalker(profDoc, (d, f) => d.Name + "." + f.Name, []);
        var walk = walker.Run(result, 0, result.Length, BinaryPrimitives.ReadUInt16LittleEndian(result));
        _out.WriteLine($"walk exact={walk.Exact} err={walk.Error}");
        foreach (var cap in walk.Captures.Where(c => c.Field == "m_categoryIds"))
            _out.WriteLine($"  {cap.Key} kind={cap.Kind} val={cap.Integer}");
    }

    private static bool ChainHas(SchemaDocument doc, int classId, string field)
    {
        for (int id = classId, guard = 0; id != 0 && guard < 64; guard++)
        {
            if (!doc.ById.TryGetValue(id, out var c)) return false;
            if (c.Fields.Any(f => f.Stored && f.Name == field)) return true;
            id = c.Base;
        }
        return false;
    }
}
