using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpLowest
{
    private readonly ITestOutputHelper _out;
    public TmpLowest(ITestOutputHelper output) => _out = output;

    private void Run(string label, string path)
    {
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var latest = PagedGzipStream.Read(root, "Global", "Latest")!;
        int rootClass = BinaryPrimitives.ReadUInt16LittleEndian(latest);
        var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, []);
        var walk = walker.Run(latest, 0, latest.Length, rootClass);
        int classId = doc.Classes.First(c => c.Name == "ColorFillSecondaryData").Id;
        int dataId = doc.Classes.First(c => c.Name == "ColorFillData").Id;
        int objects = 0;
        for (int i = 0; i < walk.Queue.Count; i++)
        {
            if (walker.Class(walk.Queue[i])?.Name != "ColorFillSecondaryData") continue;
            objects++;
            int start = walk.QueueStart[i];
            int end = i + 1 < walk.QueueStart.Count ? walk.QueueStart[i + 1] : latest.Length;
            foreach (var (name, from, to) in PayloadSpan.Fields(latest, start, end, doc, classId))
            {
                if (name != "m_colorFillResults") continue;
                int at = from + 4;
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(latest.AsSpan(from, 4));
                var lowest = new List<int>();
                for (int e = 0; e < count; e++)
                {
                    var itemFields = PayloadSpan.Fields(latest, at, to, doc, dataId);
                    int itemEnd = itemFields.Count > 0 ? itemFields[^1].End : at;
                    foreach (var (fname, ffrom, fto) in itemFields)
                        if (fname == "m_bIsLowest") lowest.Add(latest[ffrom]);
                    at = itemEnd;
                }
                _out.WriteLine($"  {label} obj{i}: entries={count} lowest=[{string.Join(",", lowest)}]");
            }
        }
        _out.WriteLine($"== {label}: ColorFillSecondaryData objects={objects}");
    }

    [Fact]
    public void Check()
    {
        Run("rst20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");
        Run("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_ent20.rvt");
    }
}
