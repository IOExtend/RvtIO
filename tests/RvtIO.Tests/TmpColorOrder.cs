using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpColorOrder
{
    private readonly ITestOutputHelper _out;
    public TmpColorOrder(ITestOutputHelper output) => _out = output;

    private void Run(string label, string path)
    {
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var latest = PagedGzipStream.Read(root, "Global", "Latest");
        if (latest is null) { _out.WriteLine($"{label}: no Latest"); return; }
        int rootClass = BinaryPrimitives.ReadUInt16LittleEndian(latest);
        var walker = new WideBlockWalker(doc, (_, _) => null, []);
        var walk = walker.Run(latest, 0, latest.Length, rootClass);
        int classId = doc.Classes.First(c => c.Name == "ColorFillSecondaryData").Id;
        int dataId = doc.Classes.First(c => c.Name == "ColorFillData").Id;
        int objects = 0;
        foreach (var s in walk.QueueStart.Select((start, i) => (start, i)))
        {
            if (walker.Class(walk.Queue[s.i])?.Name != "ColorFillSecondaryData") continue;
            int start = s.start;
            int end = s.i + 1 < walk.QueueStart.Count ? walk.QueueStart[s.i + 1] : latest.Length;
            foreach (var (name, from, to) in PayloadSpan.Fields(latest, start, end, doc, classId))
            {
                if (name != "m_colorFillResults") continue;
                int at = from + 4;
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(latest.AsSpan(from, 4));
                if (count is <= 0 or > 1000) continue;
                objects++;
                var spans = new List<byte[]>();
                for (int i = 0; i < count; i++)
                {
                    var itemFields = PayloadSpan.Fields(latest, at, to, doc, dataId);
                    int itemEnd = itemFields.Count > 0 ? itemFields[^1].End : at;
                    spans.Add(latest[at..itemEnd]);
                    at = itemEnd;
                }
                bool bytesMono = spans.Zip(spans.Skip(1), (a, b) => a.AsSpan().SequenceCompareTo(b) <= 0).All(x => x);
                var captions = spans.Select(b =>
                {
                    var f = PayloadSpan.Fields(b, 0, b.Length, doc, dataId).FirstOrDefault(f => f.Name == "m_caption");
                    return f == default ? "?" : System.Text.Encoding.Unicode.GetString(b, f.Start + 4, Math.Min(24, f.End - f.Start - 4));
                }).ToList();
                bool capsMono = captions.Zip(captions.Skip(1), (a, b) => string.CompareOrdinal(a, b) <= 0).All(x => x);
                _out.WriteLine($"  {label} entries={count} byteMono={bytesMono} captionMono={capsMono} captions=[{string.Join("|", captions.Take(8))}]");
            }
        }
        _out.WriteLine($"== {label}: nonempty ColorFillSecondaryData objects={objects}");
    }

    [Fact]
    public void Check()
    {
        Run("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt");
        Run("ref23", @"C:\Users\maxah\Downloads\DACHsampleproject_R2023.rvt");
        Run("ours23", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_final_es23.rvt");
        Run("ours20", @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\dach_ent20.rvt");
        Run("rst20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");
    }
}
