using OpenMcdf;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpPartList
{
    private readonly ITestOutputHelper _out;
    public TmpPartList(ITestOutputHelper output) => _out = output;

    [Fact]
    public void List()
    {
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt"),
            ("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_drop20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            _out.WriteLine($"== {label}");
            foreach (var entry in root.OpenStorage("Partitions").EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream) continue;
                using var s = root.OpenStorage("Partitions").OpenStream(entry.Name);
                _out.WriteLine($"  stream {entry.Name}: {s.Length} bytes");
            }
        }
    }
}
