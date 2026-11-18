using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpEsFieldBytes
{
    private readonly ITestOutputHelper _out;
    public TmpEsFieldBytes(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt"),
            ("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_low20.rvt"),
            ("ours23", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_final_es23.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var latest = PagedGzipStream.Read(root, "Global", "Latest")!;
            var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, ["ESField"]);
            var walk = walker.Run(latest, 0, latest.Length, BinaryPrimitives.ReadUInt16LittleEndian(latest));
            _out.WriteLine($"== {label}: ESField records={walk.Records.Count}");
            foreach (var rec in walk.Records)
            {
                var name = rec.Values.GetValueOrDefault("m_fieldName") as string;
                if (name != "PipeFittingKFactorTableName") continue;
                foreach (var (k, v) in rec.Values.OrderBy(kv => kv.Key))
                    _out.WriteLine($"  {k}={v}");
                _out.WriteLine($"  queueIndex={rec.QueueIndex}");
                break;
            }
        }
        // ESField class shape in each profile
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt"),
            ("tgt20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
            ("tgt23", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic23\rstbasic23.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var c = doc.Classes.FirstOrDefault(x => x.Name == "ESField");
            if (c is null) { _out.WriteLine($"{label}: ESField MISSING"); continue; }
            _out.WriteLine($"-- {label} ESField id={c.Id}");
            foreach (var f in c.Fields)
            {
                string rn = f.Ref is int rr ? doc.ById.GetValueOrDefault(rr)?.Name ?? rr.ToString() : "-";
                _out.WriteLine($"   {f.Name} t={f.Type} m=0x{f.Mods:X2} ref={rn} stored={f.Stored}");
            }
        }
    }
}
