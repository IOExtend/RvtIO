using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpByRange
{
    private readonly ITestOutputHelper _out;
    public TmpByRange(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        foreach (var (label, path) in new[]
        {
            ("rst20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
            ("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_ent20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var latest = PagedGzipStream.Read(root, "Global", "Latest")!;
            var walker = new WideBlockWalker(doc, (_, _) => null, []);
            var walk = walker.Run(latest, 0, latest.Length, BinaryPrimitives.ReadUInt16LittleEndian(latest));
            int classId = doc.Classes.First(c => c.Name == "ColorFillSecondaryData").Id;
            int dataId = doc.Classes.First(c => c.Name == "ColorFillData").Id;
            _out.WriteLine($"== {label}");
            foreach (var s in walk.QueueStart.Select((start, i) => (start, i)))
            {
                if (walker.Class(walk.Queue[s.i])?.Name != "ColorFillSecondaryData") continue;
                int start = s.start;
                int end = s.i + 1 < walk.QueueStart.Count ? walk.QueueStart[s.i + 1] : latest.Length;
                foreach (var (name, from, to) in PayloadSpan.Fields(latest, start, end, doc, classId))
                {
                    if (name != "m_colorFillResults") continue;
                    int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(latest.AsSpan(from, 4));
                    if (count <= 0) continue;
                    int at = from + 4;
                    var byRange = new List<int>();
                    var lowest = new List<int>();
                    for (int e = 0; e < count; e++)
                    {
                        var itemFields = PayloadSpan.Fields(latest, at, to, doc, dataId);
                        int itemEnd = at;
                        foreach (var (fname, ffrom, fto) in itemFields)
                        {
                            if (fname == "m_bByRange") byRange.Add(latest[ffrom]);
                            if (fname == "m_bIsLowest") lowest.Add(latest[ffrom]);
                            itemEnd = fto;
                        }
                        at = itemEnd;
                    }
                    _out.WriteLine($"  entries={count} byRange=[{string.Join(",", byRange)}] lowest=[{string.Join(",", lowest)}]");
                }
            }
        }
    }
}
