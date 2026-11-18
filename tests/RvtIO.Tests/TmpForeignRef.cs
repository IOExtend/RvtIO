using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpForeignRef
{
    private readonly ITestOutputHelper _out;
    public TmpForeignRef(ITestOutputHelper output) => _out = output;

    private static void DumpDoc(ITestOutputHelper out_, SchemaDocument doc, string label, string[] names)
    {
        out_.WriteLine($"== {label}");
        foreach (var name in names)
        {
            var c = doc.Classes.FirstOrDefault(x => x.Name == name);
            if (c is null) { out_.WriteLine($"  {name}: MISSING"); continue; }
            out_.WriteLine($"  -- {name} id={c.Id}");
            for (int id = c.Id, guard = 0; id != 0 && guard < 10; guard++)
            {
                if (!doc.ById.TryGetValue(id, out var cc)) break;
                foreach (var f in cc.Fields.Where(f => f.Stored))
                {
                    string rn = f.Ref is int rr ? doc.ById.GetValueOrDefault(rr)?.Name ?? rr.ToString() : "-";
                    out_.WriteLine($"    {cc.Name}.{f.Name} t={f.Type} m=0x{f.Mods:X2} ref={rn}");
                }
                id = cc.Base;
            }
        }
    }

    [Fact]
    public void Dump()
    {
        var names = new[] { "ForeignElemRef", "ElemSubPick", "SubPick", "XYZ", "Trf" };
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic27\rstbasic27.rvt"),
            ("tgt20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
            ("tgt22", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic22\rstbasic22.rvt"),
            ("tgt23", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic23\rstbasic23.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            DumpDoc(_out, doc, label, names);
        }
    }
}
