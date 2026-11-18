using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpOwningCheck
{
    private readonly ITestOutputHelper _out;
    public TmpOwningCheck(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        foreach (var (label, path) in new[]
        {
            ("ggpnotags", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_notags_20.rvt"),
            ("ggpsrc", @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var data = PagedGzipStream.Read(root, "Global", "ElemTable")!;
            var rows = ElemTableLayout.TryParse(data, new FormatsSchema(doc))!;
            var ids = rows.Select(r => r.Id).ToHashSet();
            var dangling = rows.Where(r => r.OwningId > 0 && !ids.Contains(r.OwningId)).Take(10).ToList();
            var missingOwners = rows.Count(r => r.OwningId > 0 && !ids.Contains(r.OwningId));
            _out.WriteLine($"{label}: rows={rows.Count} danglingOwning={missingOwners}");
            foreach (var r in dangling) _out.WriteLine($"  row {r.Id} owned by missing {r.OwningId}");
        }
    }
}
