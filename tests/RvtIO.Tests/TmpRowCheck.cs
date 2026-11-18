using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpRowCheck
{
    private readonly ITestOutputHelper _out;
    public TmpRowCheck(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        const string path = @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt";
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var data = PagedGzipStream.Read(root, "Global", "ElemTable")!;
        var rows = ElemTableLayout.TryParse(data, new FormatsSchema(doc))!;
        var byId = rows.ToDictionary(r => r.Id);
        _out.WriteLine($"rows={rows.Count}");
        foreach (var id in new long[] { 27, 28, 67, 83, 85, 92, 98, 215, 1065 })
            _out.WriteLine($"  {id}: {(byId.TryGetValue(id, out var r) ? r.ToString() : "no row")}");
    }
}
