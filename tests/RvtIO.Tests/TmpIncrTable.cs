using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpIncrTable
{
    private readonly ITestOutputHelper _out;
    public TmpIncrTable(ITestOutputHelper output) => _out = output;

    private void Run(string label, string path)
    {
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var data = PagedGzipStream.Read(root, "Global", "DocumentIncrementTable")!;
        var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, []);
        var walk = walker.Run(data, 0, data.Length, BinaryPrimitives.ReadUInt16LittleEndian(data));
        _out.WriteLine($"== {label}: len={data.Length} exact={walk.Exact} err={walk.Error}");
        foreach (var cap in walk.Captures.Where(c => c.Kind == WideBlockWalker.CaptureKind.Integer).Take(40))
            _out.WriteLine($"  {cap.Key}={cap.Integer}");
        foreach (var rec in walk.Records.Take(10))
            _out.WriteLine($"  record {walker.Class(rec.ClassId)?.Name}: " + string.Join(",", rec.Values.Select(kv => kv.Key + "=" + kv.Value)));
    }

    [Fact]
    public void Check()
    {
        Run("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt");
        Run("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_low20.rvt");
        Run("rst20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");
    }
}
