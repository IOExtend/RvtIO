using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpIncrWalk
{
    private readonly ITestOutputHelper _out;
    public TmpIncrWalk(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt"),
            ("rst20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var data = PagedGzipStream.Read(root, "Global", "DocumentIncrementTable")!;
            var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, ["DocumentIncrement", "DocumentIncrementVersion"]);
            var walk = walker.Run(data, 0, data.Length, BinaryPrimitives.ReadUInt16LittleEndian(data));
            _out.WriteLine($"== {label}: len={data.Length} exact={walk.Exact} pos={walk.Position} err={walk.Error} stop={walk.StopField} records={walk.Records.Count} queue={walk.Queue.Count}");
            foreach (var rec in walk.Records.TakeLast(6))
                _out.WriteLine($"  record {walker.Class(rec.ClassId)?.Name} q={rec.QueueIndex}: " + string.Join(",", rec.Values.Take(6).Select(kv => kv.Key + "=" + kv.Value)));
        }
    }
}
