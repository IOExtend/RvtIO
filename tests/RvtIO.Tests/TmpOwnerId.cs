using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpOwnerId
{
    private readonly ITestOutputHelper _out;
    public TmpOwnerId(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt"),
            ("tgt20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            _out.WriteLine($"== {label}");
            foreach (var c in doc.Classes)
                foreach (var f in c.Fields)
                    if (f.Name is "m_ownerId" or "m_ownerDBViewId" or "m_famId" or "m_assocLevelId")
                    {
                        string rn = f.Ref is int rr ? doc.ById.GetValueOrDefault(rr)?.Name ?? rr.ToString() : "-";
                        _out.WriteLine($"  {c.Name}.{f.Name} t={f.Type} m=0x{f.Mods:X2} ref={rn}");
                    }
        }
    }
}
