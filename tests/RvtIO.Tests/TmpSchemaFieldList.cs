using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpSchemaFieldList
{
    private readonly ITestOutputHelper _out;
    public TmpSchemaFieldList(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        var guid = new Guid("1b022a2b-5722-4787-928a-4f29ca9d8811");
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt"),
            ("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_ent20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var schemas = EntitySchemaReader.Read(root, doc)!;
            _out.WriteLine($"== {label}: schemas={schemas.Count} hasTarget={schemas.ContainsKey(guid)}");
            if (schemas.TryGetValue(guid, out var fields) && fields is not null)
                foreach (var f in fields)
                    _out.WriteLine($"  {f.Name} t={f.Type} m=0x{f.Mods:X2} ref={f.Ref}");
            else
                _out.WriteLine($"  fields null or missing");
        }
    }
}
