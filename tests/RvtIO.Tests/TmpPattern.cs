using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpPattern
{
    private readonly ITestOutputHelper _out;
    public TmpPattern(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Find()
    {
        const string path = @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_drop20.rvt";
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(doc))!.Value;
        // pattern: count=5, [67,67,67,28,28] as u32 LE
        var pattern = new byte[] { 5, 0, 0, 0, 67, 0, 0, 0, 67, 0, 0, 0, 67, 0, 0, 0, 28, 0, 0, 0, 28, 0, 0, 0 };
        foreach (var entry in root.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = root.OpenStorage("Partitions").OpenStream(entry.Name);
            var stored = new byte[s.Length];
            s.ReadExactly(stored);
            var body = PartitionFraming.RemoveTails(stored)!;
            for (int i = 0; i + pattern.Length <= body.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++) if (body[i + j] != pattern[j]) { match = false; break; }
                if (match) _out.WriteLine($"stream {entry.Name}: pattern at stored offset {i}");
            }
        }
        // and in source?
        using var srcRoot = RootStorage.OpenRead(@"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt");
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var stored = new byte[s.Length];
            s.ReadExactly(stored);
            var body = PartitionFraming.RemoveTails(stored)!;
            // source is 8-byte ids: count=5 then 8-byte ids 67,67,67,28,28
            var pattern8 = new byte[] { 5, 0, 0, 0, 67, 0, 0, 0, 0, 0, 0, 0, 67, 0, 0, 0, 0, 0, 0, 0, 67, 0, 0, 0, 0, 0, 0, 0, 28, 0, 0, 0, 0, 0, 0, 0, 28, 0, 0, 0, 0, 0, 0, 0 };
            for (int i = 0; i + pattern8.Length <= body.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern8.Length; j++) if (body[i + j] != pattern8[j]) { match = false; break; }
                if (match) { _out.WriteLine($"SOURCE stream {entry.Name}: pattern8 at stored offset {i}"); break; }
            }
        }
        _out.WriteLine("done");
    }
}
