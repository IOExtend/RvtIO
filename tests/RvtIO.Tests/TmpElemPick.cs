using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpElemPick
{
    private readonly ITestOutputHelper _out;
    public TmpElemPick(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
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
            _out.WriteLine($"== {label}");
            foreach (var name in new[] { "ElemPick", "Pick", "SubPick", "PickData", "ElemSubPick" })
            {
                var c = doc.Classes.FirstOrDefault(x => x.Name == name);
                if (c is null) { _out.WriteLine($"  {name}: MISSING"); continue; }
                _out.WriteLine($"  -- {name} id={c.Id} base={(c.Base == 0 ? "0" : doc.ById.GetValueOrDefault(c.Base)?.Name ?? c.Base.ToString())}");
                for (int id = c.Id, guard = 0; id != 0 && guard < 8; guard++)
                {
                    var cc = doc.ById[id];
                    foreach (var f in cc.Fields.Where(f => f.Stored))
                    {
                        string rn = f.Ref is int rr ? doc.ById.GetValueOrDefault(rr)?.Name ?? rr.ToString() : "-";
                        string args = f.Args is null ? "" : $" args=[{string.Join(",", f.Args.Select(a => a.Ref is int ar ? doc.ById.GetValueOrDefault(ar)?.Name ?? ar.ToString() : a.Fixed?.ToString() ?? "?"))}]";
                        _out.WriteLine($"    {cc.Name}.{f.Name} t={f.Type} m=0x{f.Mods:X2} ref={rn}{args}");
                    }
                    id = cc.Base;
                }
            }
        }
    }
}
