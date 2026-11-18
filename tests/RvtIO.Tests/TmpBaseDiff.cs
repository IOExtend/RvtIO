using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpBaseDiff
{
    private readonly ITestOutputHelper _out;
    public TmpBaseDiff(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        using var srcRoot = RootStorage.OpenRead(@"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt");
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        foreach (var (label, profilePath) in new[]
        {
            ("20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
            ("22", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic22\rstbasic22.rvt"),
        })
        {
            using var profRoot = RootStorage.OpenRead(profilePath);
            var profDoc = TfSchemaReader.Read(profRoot)!;
            var srcByName = srcDoc.Classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
            var profByName = profDoc.Classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
            int diffBase = 0;
            foreach (var (name, sc) in srcByName)
            {
                if (!profByName.TryGetValue(name, out var tc)) continue;
                string srcBase = srcDoc.ById.GetValueOrDefault(sc.Base)?.Name ?? sc.Base.ToString();
                string tgtBase = profDoc.ById.GetValueOrDefault(tc.Base)?.Name ?? tc.Base.ToString();
                if (srcBase != tgtBase)
                {
                    diffBase++;
                    if (diffBase <= 40) _out.WriteLine($"  {label} {name}: srcBase={srcBase} tgtBase={tgtBase}");
                }
            }
            _out.WriteLine($"== {label}: classes with different direct base: {diffBase}");
        }
    }
}
