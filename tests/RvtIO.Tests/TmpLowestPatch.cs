using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpLowestPatch
{
    private readonly ITestOutputHelper _out;
    public TmpLowestPatch(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Patch()
    {
        const string src = @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_ent20.rvt";
        const string dst = @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_ent20_lowest.rvt";
        File.Delete(dst);
        using var root = RootStorage.OpenRead(src);
        var doc = TfSchemaReader.Read(root)!;
        byte[] storedRaw;
        using (var ls = root.OpenStorage("Global").OpenStream("Latest"))
        {
            storedRaw = new byte[ls.Length];
            ls.ReadExactly(storedRaw);
        }
        var prefix = PagedStreamCodec.ReadPrefix(storedRaw);
        var latest = PagedGzipStream.Decompress(storedRaw)!;
        int rootClass = BinaryPrimitives.ReadUInt16LittleEndian(latest);
        var walker = new WideBlockWalker(doc, (_, _) => null, []);
        var walk = walker.Run(latest, 0, latest.Length, rootClass);
        int classId = doc.Classes.First(c => c.Name == "ColorFillSecondaryData").Id;
        int dataId = doc.Classes.First(c => c.Name == "ColorFillData").Id;

        var edits = new List<(int Pos, byte Value)>();
        for (int i = 0; i < walk.Queue.Count; i++)
        {
            if (walker.Class(walk.Queue[i])?.Name != "ColorFillSecondaryData") continue;
            int start = walk.QueueStart[i];
            int end = i + 1 < walk.QueueStart.Count ? walk.QueueStart[i + 1] : latest.Length;
            foreach (var (name, from, to) in PayloadSpan.Fields(latest, start, end, doc, classId))
            {
                if (name != "m_colorFillResults") continue;
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(latest.AsSpan(from, 4));
                if (count <= 0) continue;
                int at = from + 4;
                var firstFields = PayloadSpan.Fields(latest, at, to, doc, dataId);
                foreach (var (fname, ffrom, fto) in firstFields)
                    if (fname == "m_bIsLowest") edits.Add((ffrom, 1));
                break;
            }
        }
        _out.WriteLine($"edits={edits.Count}");
        var data = latest;
        foreach (var (pos, value) in edits.OrderByDescending(e => e.Pos))
        {
            var next = new byte[data.Length];
            Buffer.BlockCopy(data, 0, next, 0, data.Length);
            next[pos] = value;
            data = next;
        }
        var encoded = PagedStreamCodec.Encode(prefix, data);
        var result = ContainerRewriter.Rewrite(src, dst, new Dictionary<string, byte[]> { ["Global/Latest"] = encoded });
        _out.WriteLine($"written {dst}: replaced={result.Replaced}");
    }
}
