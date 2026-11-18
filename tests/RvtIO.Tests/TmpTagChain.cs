using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpTagChain
{
    private readonly ITestOutputHelper _out;
    public TmpTagChain(ITestOutputHelper output) => _out = output;

    private static List<string> Chain(SchemaDocument doc, int id)
    {
        var names = new List<string>();
        for (int guard = 0; id != 0 && guard < 16; guard++)
        {
            if (!doc.ById.TryGetValue(id, out var c)) break;
            names.Add(c.Name + "#" + id);
            id = c.Base;
        }
        names.Reverse();
        return names;
    }

    [Fact]
    public void Dump()
    {
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt"),
            ("tgt20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
            ("tgt22", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic22\rstbasic22.rvt"),
            ("tgt23", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic23\rstbasic23.rvt"),
            ("ggp27", @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            _out.WriteLine($"== {label}");
            foreach (var name in new[] { "IndependentTag", "TaggedEntitiesCell", "TaggedElem", "TaggedEntity", "TagOrientationCell", "CellList", "TagReferencesWithLeaderCell", "TagHeadCell", "TagReferenceWithLeader" })
            {
                var c = doc.Classes.FirstOrDefault(x => x.Name == name);
                if (c is null) { _out.WriteLine($"  {name}: MISSING"); continue; }
                _out.WriteLine($"  {name}: " + string.Join(">", Chain(doc, c.Id)));
            }
        }
    }
}
