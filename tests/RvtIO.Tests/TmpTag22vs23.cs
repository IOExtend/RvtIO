using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpTag22vs23
{
    private readonly ITestOutputHelper _out;
    public TmpTag22vs23(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        var docs = new (string Label, string Path)[]
        {
            ("tgt22", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic22\rstbasic22.rvt"),
            ("tgt23", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic23\rstbasic23.rvt"),
        };
        var maps = new Dictionary<string, Dictionary<string, List<string>>>();
        var names = new[] { "IndependentTag", "TaggedEntitiesCell", "TaggedElem", "TaggedEntity", "TagOrientationCell", "CellList", "Cell", "CellInterface", "Element" };
        foreach (var (label, path) in docs)
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var map = new Dictionary<string, List<string>>();
            foreach (var name in names)
            {
                var c = doc.Classes.FirstOrDefault(x => x.Name == name);
                if (c is null) continue;
                var list = new List<string>();
                for (int id = c.Id, guard = 0; id != 0 && guard < 8; guard++)
                {
                    var cc = doc.ById[id];
                    foreach (var f in cc.Fields.Where(f => f.Stored))
                    {
                        string rn = f.Ref is int rr ? doc.ById.GetValueOrDefault(rr)?.Name ?? rr.ToString() : "-";
                        list.Add($"{cc.Name}.{f.Name} t={f.Type} m=0x{f.Mods:X2} ref={rn}");
                    }
                    id = cc.Base;
                }
                map[name] = list;
            }
            maps[label] = map;
        }
        foreach (var name in names)
        {
            if (!maps["tgt22"].TryGetValue(name, out var a) || !maps["tgt23"].TryGetValue(name, out var b)) continue;
            var only22 = a.Except(b).ToList();
            var only23 = b.Except(a).ToList();
            if (only22.Count == 0 && only23.Count == 0) continue;
            _out.WriteLine($"== {name} differs");
            foreach (var s in only22) _out.WriteLine("  22-only " + s);
            foreach (var s in only23) _out.WriteLine("  23-only " + s);
        }
        _out.WriteLine("done");
    }
}
