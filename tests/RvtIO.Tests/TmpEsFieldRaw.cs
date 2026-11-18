using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpEsFieldRaw
{
    private readonly ITestOutputHelper _out;
    public TmpEsFieldRaw(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt"),
            ("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_low20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var latest = PagedGzipStream.Read(root, "Global", "Latest")!;
            var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, ["ESField"]);
            var walk = walker.Run(latest, 0, latest.Length, BinaryPrimitives.ReadUInt16LittleEndian(latest));
            _out.WriteLine($"== {label}: queue={walk.Queue.Count}");
            // find ESField queue objects whose record has our field name
            foreach (var rec in walk.Records)
            {
                if ((rec.Values.GetValueOrDefault("m_fieldName") as string) != "PipeFittingKFactorTableName") continue;
                int qi = rec.QueueIndex;
                int start = walk.QueueStart[qi];
                int end = qi + 1 < walk.QueueStart.Count ? walk.QueueStart[qi + 1] : latest.Length;
                _out.WriteLine($"  ESField bytes [{start}..{end}] len={end - start}:");
                for (int at = start; at < end; at += 32)
                    _out.WriteLine($"    [{at}]: {Convert.ToHexString(latest.AsSpan(at, Math.Min(32, end - at)))}");
                // decode ForgeTypeId / GUIDvalue inline contents
                var fields = PayloadSpan.Fields(latest, start, end, doc, rec.ClassId);
                foreach (var (fname, ffrom, fto) in fields)
                    _out.WriteLine($"    field {fname} [{ffrom}..{fto}]: {Convert.ToHexString(latest.AsSpan(ffrom, Math.Min(48, fto - ffrom)))}");
                break;
            }
        }
    }
}
