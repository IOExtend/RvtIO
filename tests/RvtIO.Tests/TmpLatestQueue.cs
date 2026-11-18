using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpLatestQueue
{
    private readonly ITestOutputHelper _out;
    public TmpLatestQueue(ITestOutputHelper output) => _out = output;

    private void Run(string label, string path)
    {
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var schemas = EntitySchemaReader.Read(root, doc);
        var latest = PagedGzipStream.Read(root, "Global", "Latest")!;
        var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, []);
        if (schemas is not null) walker.UseEntitySchemas(schemas, passUnknown: true);
        var walk = walker.Run(latest, 0, latest.Length, BinaryPrimitives.ReadUInt16LittleEndian(latest));
        _out.WriteLine($"== {label}: len={latest.Length} exact={walk.Exact} pos={walk.Position} err={walk.Error} queue={walk.Queue.Count}");
        return;
        int bad = 0, shown = 0;
        var badClasses = new Dictionary<string, int>();
        for (int i = 0; i < walk.Queue.Count; i++)
        {
            int start = walk.QueueStart[i];
            int end = i + 1 < walk.QueueStart.Count ? walk.QueueStart[i + 1] : latest.Length;
            var cls = walker.Class(walk.Queue[i]);
            if (cls is null) continue;
            // walk the object bytes with its own class (no tag in stream)
            var w2 = new WideBlockWalker(doc, (_, _) => null, []);
            if (schemas is not null) w2.UseEntitySchemas(schemas, passUnknown: true);
            // simulate: run fields only — use a fresh walk on the slice with a synthetic tag prefix
            var slice = new byte[2 + (end - start)];
            BinaryPrimitives.WriteUInt16LittleEndian(slice, (ushort)cls.Id);
            Buffer.BlockCopy(latest, start, slice, 2, end - start);
            var w = w2.Run(slice, 0, slice.Length, cls.Id);
            if (w.Exact && w.Error is null) continue;
            bad++;
            badClasses[cls.Name] = badClasses.GetValueOrDefault(cls.Name) + 1;
            if (shown < 8)
            {
                _out.WriteLine($"  q{i} {cls.Name} [{start}..{end}] exact={w.Exact} err={w.Error} stop={w.StopField}");
                shown++;
            }
        }
        _out.WriteLine($"  bad={bad}");
        foreach (var (k, v) in badClasses.OrderByDescending(kv => kv.Value).Take(15))
            _out.WriteLine($"  badclass {k}: {v}");
    }

    [Fact]
    public void Check()
    {
        Run("ggp20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_drop20.rvt");
        Run("rst20native", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");
        Run("ggp23", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_ours23.rvt");
    }
}
