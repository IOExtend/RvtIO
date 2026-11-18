using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpWrapSchema
{
    private readonly ITestOutputHelper _out;
    public TmpWrapSchema(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        using var root = RootStorage.OpenRead(@"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt");
        var doc = TfSchemaReader.Read(root)!;
        foreach (var name in new[] { "ElementIdArrWrap", "IckyExcludedCategoriesSetPtrWrapper", "ElementIdPlusIndex", "ConnectorId" })
        {
            var c = doc.Classes.FirstOrDefault(x => x.Name == name);
            if (c is null) { _out.WriteLine($"{name}: MISSING"); continue; }
            _out.WriteLine($"-- {name} id={c.Id}");
            for (int id = c.Id, guard = 0; id != 0 && guard < 6; guard++)
            {
                var cc = doc.ById[id];
                foreach (var f in cc.Fields)
                {
                    string rn = f.Ref is int rr ? doc.ById.GetValueOrDefault(rr)?.Name ?? rr.ToString() : "-";
                    _out.WriteLine($"   {cc.Name}.{f.Name} t={f.Type} m=0x{f.Mods:X2} ref={rn} stored={f.Stored}");
                }
                id = cc.Base;
            }
        }
    }
}
