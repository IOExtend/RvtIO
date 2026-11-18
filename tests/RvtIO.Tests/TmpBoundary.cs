using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpBoundary
{
    private readonly ITestOutputHelper _out;
    public TmpBoundary(ITestOutputHelper output) => _out = output;

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
            var c = doc.Classes.FirstOrDefault(x => x.Name == "BoundaryConditions");
            if (c is null) { _out.WriteLine($"{label}: BoundaryConditions MISSING"); continue; }
            _out.WriteLine($"== {label} BoundaryConditions id={c.Id}");
            for (int id = c.Id, guard = 0; id != 0 && guard < 6; guard++)
            {
                var cc = doc.ById[id];
                foreach (var f in cc.Fields.Where(f => f.Stored))
                {
                    string rn = f.Ref is int rr ? doc.ById.GetValueOrDefault(rr)?.Name ?? rr.ToString() : "-";
                    _out.WriteLine($"  {cc.Name}.{f.Name} t={f.Type} m=0x{f.Mods:X2} ref={rn}");
                }
                id = cc.Base;
            }
        }
    }
}
