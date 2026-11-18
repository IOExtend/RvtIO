using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpIncrSchema
{
    private readonly ITestOutputHelper _out;
    public TmpIncrSchema(ITestOutputHelper output) => _out = output;

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
            foreach (var name in new[] { "DocumentIncrementTable", "DocumentIncrement", "DocumentIncrementVersion", "EpisodeId" })
            {
                var c = doc.Classes.FirstOrDefault(x => x.Name == name);
                if (c is null) continue;
                _out.WriteLine($"  -- {name} id={c.Id}");
                for (int id = c.Id, guard = 0; id != 0 && guard < 6; guard++)
                {
                    var cc = doc.ById[id];
                    foreach (var f in cc.Fields.Where(f => f.Stored))
                    {
                        string rn = f.Ref is int rr ? doc.ById.GetValueOrDefault(rr)?.Name ?? rr.ToString() : "-";
                        _out.WriteLine($"    {cc.Name}.{f.Name} t={f.Type} m=0x{f.Mods:X2} ref={rn}");
                    }
                    id = cc.Base;
                }
            }
        }
    }
}
