using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpSchemaFields
{
    private readonly ITestOutputHelper _out;
    public TmpSchemaFields(ITestOutputHelper output) => _out = output;

    private void Run(string label, string path, Guid wanted)
    {
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var latest = PagedGzipStream.Read(root, "Global", "Latest")!;
        var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, ["ESSchemaStorage", "ESSchema", "ESField", "SchemaUsageInfo"]);
        var walk = walker.Run(latest, 0, latest.Length, BinaryPrimitives.ReadUInt16LittleEndian(latest));
        var schemas = walk.Records.Where(r => walker.Class(r.ClassId)?.Name is "ESSchema" or "SchemaUsageInfo").ToList();
        _out.WriteLine($"== {label}: schema records={schemas.Count}");
        int shown = 0;
        foreach (var rec in schemas)
        {
            if (!rec.Values.TryGetValue("m_guid", out var g) || (Guid)g != wanted) continue;
            foreach (var (k, v) in rec.Values.OrderBy(kv => kv.Key))
                _out.WriteLine($"  {k}={v}");
            if (++shown >= 3) break;
        }
        // ESField objects queued right after each schema record
        _out.WriteLine($"  queue total={walk.Queue.Count}");
        int esFields = walk.Queue.Count(t => walker.Class(t)?.Name == "ESField");
        _out.WriteLine($"  ESField queued={esFields}");
    }

    [Fact]
    public void Check()
    {
        var guid = new Guid("1b022a2b-5722-4787-928a-4f29ca9d8811");
        Run("src27usage", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt", guid);
        Run("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_ent20.rvt", guid);
        Run("rst20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt", guid);
    }
}
