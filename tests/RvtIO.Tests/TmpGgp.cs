using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpGgp
{
    private readonly ITestOutputHelper _out;
    public TmpGgp(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        var guid = new Guid("1b022a2b-5722-4787-928a-4f29ca9d8811");
        foreach (var (label, path) in new[]
        {
            ("ggp20", @"D:\IOExtend-samples\RvtIO\downloads\test_files\test_files\2020\GGP_1077-BFU_KPP1_AR_R20.rvt"),
            ("ggp27", @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt"),
            ("ggp23ref", @"D:\IOExtend-samples\RvtIO\downloads\2023\GGP_1077-BFU_KPP1_AR_R20_R2023.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root);
            if (doc is null) { _out.WriteLine($"{label}: no schema"); continue; }
            var schemas = EntitySchemaReader.Read(root, doc)!;
            _out.WriteLine($"== {label}: schemas={schemas.Count} hasCoeff={schemas.ContainsKey(guid)}");
            if (schemas.TryGetValue(guid, out var fields) && fields is not null)
                foreach (var f in fields)
                    _out.WriteLine($"  {f.Name} t={f.Type} m=0x{f.Mods:X2} ref={f.Ref}");
        }
    }
}
