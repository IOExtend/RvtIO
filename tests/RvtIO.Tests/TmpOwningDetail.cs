using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpOwningDetail
{
    private readonly ITestOutputHelper _out;
    public TmpOwningDetail(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        DumpFile("ours", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\rst27_to20.rvt", 30);
        DumpFile("native", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt", 0);
    }

    private void DumpFile(string label, string path, int showDangling)
    {
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var formats = new FormatsSchema(doc);
        var data = PagedGzipStream.Read(root, "Global", "ElemTable")!;
        var rows = ElemTableLayout.TryParse(data, formats)!;
        var ids = rows.Select(r => r.Id).ToHashSet();
        var byOwning = rows.GroupBy(r => r.OwningId).OrderBy(g => g.Key).Take(8);
        _out.WriteLine($"== {label}: rows={rows.Count}");
        foreach (var g in byOwning)
            _out.WriteLine($"  owning={g.Key} count={g.Count()} sampleId={g.First().Id}");
        var dang = rows.Where(r => r.OwningId > 0 && !ids.Contains(r.OwningId)).Take(showDangling).ToList();
        if (showDangling > 0)
        {
            // class of each dangling row via partitions ch102
            var tags = PartitionFraming.TagsOf(formats)!.Value;
            int width = SchemaRelease.IdWidth(SchemaRelease.FromSchema(formats)) ?? 4;
            var classOf = new Dictionary<long, string>();
            foreach (var entry in root.OpenStorage("Partitions").EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream) continue;
                using var s = root.OpenStorage("Partitions").OpenStream(entry.Name);
                var raw = new byte[s.Length];
                s.ReadExactly(raw);
                PartitionFraming.Walk(raw, tags, width, r =>
                {
                    if (r.Channel != 102 || r.Body.Length < 2) return;
                    long oid = unchecked((long)r.Owner);
                    if (classOf.Count < 40000 && !classOf.ContainsKey(oid))
                    {
                        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
                        classOf[oid] = doc.ById.GetValueOrDefault(tag)?.Name ?? ("tag" + tag);
                    }
                }, out _, out _);
            }
            foreach (var r in dang)
                _out.WriteLine($"  dangling row id={r.Id} ({classOf.GetValueOrDefault(r.Id, "?")}) owning={r.OwningId} part={r.PartitionId}");
        }
    }
}
