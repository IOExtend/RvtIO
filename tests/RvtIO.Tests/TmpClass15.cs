using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpClass15
{
    private readonly ITestOutputHelper _out;
    public TmpClass15(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        foreach (var (label, path) in new[]
        {
            ("tgt20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
            ("tgt22", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic22\rstbasic22.rvt"),
            ("tgt23", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic23\rstbasic23.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            foreach (var id in new[] { 15, 37 })
            {
                var c = doc.ById.GetValueOrDefault(id);
                _out.WriteLine($"{label} id={id}: {(c is null ? "MISSING" : c.Name + " base=" + (doc.ById.GetValueOrDefault(c.Base)?.Name ?? c.Base.ToString()))}");
            }
            var ind = doc.Classes.FirstOrDefault(x => x.Name == "IndependentTag");
            if (ind is not null)
            {
                _out.WriteLine($"  IndependentTag fields: " + string.Join(",", ind.Fields.Where(f => f.Stored).Select(f => f.Name)));
            }
        }
    }
}
