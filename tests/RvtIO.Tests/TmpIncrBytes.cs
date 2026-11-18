using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpIncrBytes
{
    private readonly ITestOutputHelper _out;
    public TmpIncrBytes(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt"),
            ("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_low20.rvt"),
            ("rst20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var data = PagedGzipStream.Read(root, "Global", "DocumentIncrementTable")!;
            _out.WriteLine($"== {label}: len={data.Length}");
            for (int at = 11080; at < 11360; at += 16)
                _out.WriteLine($"  [{at}]: {Convert.ToHexString(data.AsSpan(at, 16))}");
        }
    }
}
