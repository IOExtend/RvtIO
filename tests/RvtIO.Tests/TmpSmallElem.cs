using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpSmallElem
{
    private readonly ITestOutputHelper _out;
    public TmpSmallElem(ITestOutputHelper output) => _out = output;

    private void Check(string label, string path)
    {
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var formats = new FormatsSchema(doc);
        var tags = PartitionFraming.TagsOf(formats)!.Value;
        int width = SchemaRelease.IdWidth(SchemaRelease.FromSchema(formats)) ?? 4;
        var owners = new HashSet<ulong>();
        foreach (var entry in root.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = root.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            var body = PartitionFraming.RemoveTails(raw);
            if (body is null) continue;
            // reuse Inspect for owner collection (no transcoder needed for owners? use null-transcoder walk)
            PartitionFraming.Walk(raw, tags, width, r =>
            {
                ulong term = width == 8 ? ulong.MaxValue : uint.MaxValue;
                if (r.Owner != term) owners.Add(r.Owner);
            }, out _, out _);
        }
        var data = PagedGzipStream.Read(root, "Global", "ElemTable")!;
        var rows = ElemTableLayout.TryParse(data, formats)!;
        var rowIds = rows.Select(r => (ulong)r.Id).ToHashSet();
        var noRow = owners.Where(o => o != (width == 8 ? ulong.MaxValue : uint.MaxValue) && !rowIds.Contains(o)).Take(20).ToList();
        var noOwner = rows.Where(r => r.Id > 0 && !owners.Contains((ulong)r.Id)).Take(20).ToList();
        var dangOwn = rows.Where(r => r.OwningId > 0 && !rowIds.Contains((ulong)r.OwningId)).Take(20).ToList();
        _out.WriteLine($"{label}: owners={owners.Count} rows={rows.Count} ownersWithoutRow={owners.Count(o => !rowIds.Contains(o))} rowsWithoutOwner={rows.Count(r => r.Id <= 0 || !owners.Contains((ulong)r.Id))} danglingOwning={rows.Count(r => r.OwningId > 0 && !rowIds.Contains((ulong)r.OwningId))}");
        foreach (var o in noRow) _out.WriteLine($"  owner without row: {o}");
        foreach (var r in noOwner) _out.WriteLine($"  row without owner: id={r.Id} owning={r.OwningId}");
        foreach (var r in dangOwn) _out.WriteLine($"  dangling owning: row {r.Id} owned by missing {r.OwningId}");
    }

    [Fact]
    public void CheckSmall()
    {
        Check("rst27_to20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\rst27_to20.rvt");
        Check("rst20native", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");
    }
}
