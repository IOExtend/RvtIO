using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpRangeEntries
{
    private readonly ITestOutputHelper _out;
    public TmpRangeEntries(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        foreach (var (label, path) in new[]
        {
            ("src27", @"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt"),
            ("rst20", @"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt"),
        })
        {
            using var root = RootStorage.OpenRead(path);
            var doc = TfSchemaReader.Read(root)!;
            var latest = PagedGzipStream.Read(root, "Global", "Latest")!;
            var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, []);
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
                    if (count != 8 && count != 10 && count != 12) continue;
                    _out.WriteLine($"  container entries={count}");
                    int at = from + 4;
                    for (int e = 0; e < count; e++)
                    {
                        var itemFields = PayloadSpan.Fields(latest, at, to, doc, dataId);
                        var map = new Dictionary<string, string>();
                        int itemEnd = at;
                        foreach (var (fname, ffrom, fto) in itemFields)
                        {
                            int keep = fname == "m_caption" ? Math.Min(64, fto - ffrom) : Math.Min(8, fto - ffrom);
                            map[fname] = Convert.ToHexString(latest.AsSpan(ffrom, keep));
                            itemEnd = fto;
                        }
                        string cap = map.GetValueOrDefault("m_caption", "?");
                        string capText = "?";
                        if (cap != "?")
                        {
                            var cbytes = Convert.FromHexString(cap);
                            int clen = cbytes.Length >= 4 ? BitConverter.ToInt32(cbytes, 0) : 0;
                            int avail = Math.Min(cbytes.Length - 4, 24);
                            if (avail > 0) capText = System.Text.Encoding.Unicode.GetString(cbytes, 4, avail & ~1);
                            capText += $" (len={clen})";
                        }
                        _out.WriteLine($"    [{e}] color={map.GetValueOrDefault("m_color")} lowest={map.GetValueOrDefault("m_bIsLowest")} byRange={map.GetValueOrDefault("m_bByRange")} int={map.GetValueOrDefault("m_int", map.GetValueOrDefault("m_paramStorage"))} cap={capText}");
                        at = itemEnd;
                    }
                }
            }
        }
    }
}
