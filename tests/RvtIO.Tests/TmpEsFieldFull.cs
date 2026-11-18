using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpEsFieldFull
{
    private readonly ITestOutputHelper _out;
    public TmpEsFieldFull(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        var guid = new Guid("1b022a2b-5722-4787-928a-4f29ca9d8811");
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt"),
            ("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_ent20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var latest = PagedGzipStream.Read(root, "Global", "Latest")!;
            var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, ["ESSchema", "ESField", "SchemaUsageInfo"]);
            var walk = walker.Run(latest, 0, latest.Length, BinaryPrimitives.ReadUInt16LittleEndian(latest));
            _out.WriteLine($"== {label}: records={walk.Records.Count} queue={walk.Queue.Count}");
            // find ESSchema/SchemaUsageInfo record with the guid, then its queued ESField objects
            for (int i = 0; i < walk.Records.Count; i++)
            {
                var rec = walk.Records[i];
                string? cls = walker.Class(rec.ClassId)?.Name;
                if (cls is not ("ESSchema" or "SchemaUsageInfo")) continue;
                if (!rec.Values.TryGetValue("m_guid", out var g) || (Guid)g != guid) continue;
                _out.WriteLine($"  record {cls} q={rec.QueueIndex} fields={string.Join(",", rec.Values.Keys)}");
                foreach (var (k, v) in rec.Values.Where(kv => kv.Key != "m_fields"))
                    _out.WriteLine($"    {k}={v}");
                // ESField objects queued for this schema: same queue block
                int qi = rec.QueueIndex;
                for (int q = Math.Max(0, qi - 2); q < Math.Min(walk.Queue.Count, qi + 12); q++)
                {
                    if (walker.Class(walk.Queue[q])?.Name != "ESField") continue;
                    var frec = walk.Records.FirstOrDefault(r => r.QueueIndex == q && walker.Class(r.ClassId)?.Name == "ESField");
                    _out.WriteLine($"    queued ESField q={q} start={walk.QueueStart[q]}: " + (frec.ClassId == 0 ? "(no record)" : string.Join(",", frec.Values.Select(kv => kv.Key + "=" + kv.Value))));
                }
            }
        }
    }
}
