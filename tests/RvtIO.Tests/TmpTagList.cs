using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpTagList
{
    private readonly ITestOutputHelper _out;
    public TmpTagList(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic27\rstbasic27.rvt"),
            ("tgt20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
            ("tgt23", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic23\rstbasic23.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var names = doc.Classes.Select(c => c.Name).Where(n => n.Contains("Tag", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n).ToList();
            _out.WriteLine($"== {label}: {names.Count} tag classes");
            foreach (var n in names) _out.WriteLine($"  {n}");
        }
    }
}
