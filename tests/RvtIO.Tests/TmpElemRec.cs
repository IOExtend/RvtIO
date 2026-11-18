using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpElemRec
{
    private readonly ITestOutputHelper _out;
    public TmpElemRec(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        var docs = new (string Label, string Path)[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt"),
            ("tgt20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
        };
        var maps = new Dictionary<string, List<string>>();
        foreach (var (label, path) in docs)
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var map = new List<string>();
            foreach (var name in new[] { "ElemRec", "ElemTable", "ElementHistory", "GraveyardRec", "IdentifierSource" })
            {
                var c = doc.Classes.FirstOrDefault(x => x.Name == name);
                if (c is null) { map.Add(name + ": MISSING"); continue; }
                for (int id = c.Id, guard = 0; id != 0 && guard < 8; guard++)
                {
                    var cc = doc.ById[id];
                    foreach (var f in cc.Fields)
                    {
                        string rn = f.Ref is int rr ? doc.ById.GetValueOrDefault(rr)?.Name ?? rr.ToString() : "-";
                        map.Add($"{cc.Name}.{f.Name} t={f.Type} m=0x{f.Mods:X2} x={f.Extra} ref={rn} stored={f.Stored}");
                    }
                    id = cc.Base;
                }
            }
            maps[label] = map;
        }
        _out.WriteLine("-- only src27:");
        foreach (var s in maps["src27"].Except(maps["tgt20"])) _out.WriteLine("  " + s);
        _out.WriteLine("-- only tgt20:");
        foreach (var s in maps["tgt20"].Except(maps["src27"])) _out.WriteLine("  " + s);
    }
}
