using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpAnalytical
{
    private readonly ITestOutputHelper _out;
    public TmpAnalytical(ITestOutputHelper output) => _out = output;

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
            foreach (var name in new[] { "AnalyticalMember", "AnalyticalPanel", "AnalyticalLink", "AnalyticalSupport", "LoadCaseElem", "LoadNatureElem" })
            {
                var c = doc.Classes.FirstOrDefault(x => x.Name == name);
                _out.WriteLine($"{label} {name}: {(c is null ? "MISSING" : $"id={c.Id}")}");
            }
        }
    }
}
