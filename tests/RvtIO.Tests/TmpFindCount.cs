using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpFindCount
{
    private readonly ITestOutputHelper _out;
    public TmpFindCount(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Find()
    {
        const string path = @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_low20.rvt";
        uint wanted = 569639;
        using var root = RootStorage.OpenRead(path);
        foreach (var storageName in new[] { "Global", "Partitions", "Formats" })
        {
            if (!root.TryOpenStorage(storageName, out var storage)) continue;
            foreach (var entry in storage.EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream) continue;
                using var s = storage.OpenStream(entry.Name);
                var raw = new byte[s.Length];
                s.ReadExactly(raw);
                var datas = new List<(string, byte[])> { ("stored", raw) };
                try
                {
                    var dec = PagedGzipStream.Decompress(raw);
                    if (dec is not null) datas.Add(("dec", dec));
                }
                catch { }
                foreach (var (kind, data) in datas)
                {
                    var hits = new List<int>();
                    for (int i = 0; i + 4 <= data.Length; i++)
                        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4)) == wanted) hits.Add(i);
                    if (hits.Count > 0)
                        _out.WriteLine($"{storageName}/{entry.Name}[{kind}]: len={data.Length} hits={hits.Count} at [{string.Join(",", hits.Take(10))}]");
                }
            }
        }
        foreach (var name in new[] { "Contents", "BasicFileInfo" })
        {
            try
            {
                using var s = root.OpenStream(name);
                var raw = new byte[s.Length];
                s.ReadExactly(raw);
                var hits = new List<int>();
                for (int i = 0; i + 4 <= raw.Length; i++)
                    if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i, 4)) == wanted) hits.Add(i);
                if (hits.Count > 0)
                    _out.WriteLine($"{name}: len={raw.Length} hits={hits.Count} at [{string.Join(",", hits.Take(10))}]");
            }
            catch { }
        }
    }
}
