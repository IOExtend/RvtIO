using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpEtRows
{
    private readonly ITestOutputHelper _out;
    public TmpEtRows(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        foreach (var (label, path) in new[]
        {
            ("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_low20.rvt"),
            ("ours23", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_final_es23.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var dec = PagedGzipStream.Read(root, "Global", "ElemTable")!;
            var rows = ElemTableLayout.TryParse(dec, new FormatsSchema(doc));
            _out.WriteLine($"{label}: dec={dec.Length} rows={rows?.Count}");
        }
    }
}
