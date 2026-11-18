using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpGStyleOwners
{
    private readonly ITestOutputHelper _out;
    public TmpGStyleOwners(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump()
    {
        const string srcPath = @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt";
        using var srcRoot = RootStorage.OpenRead(srcPath);
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(srcDoc))!.Value;
        int gstyleId = srcDoc.Classes.First(c => c.Name == "GStyleElem").Id;
        var owners = new List<ulong>();
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, 8, r =>
            {
                if (r.Channel != 102 || r.Body.Length < 2) return;
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(r.Body.Span);
                if (tag == gstyleId) owners.Add(r.Owner);
            }, out _, out _);
        }
        owners.Sort();
        _out.WriteLine($"count={owners.Count} min={owners.First()} max={owners.Last()} median={owners[owners.Count/2]}");
        _out.WriteLine("p10=" + owners[owners.Count/10] + " p25=" + owners[owners.Count/4] + " p75=" + owners[owners.Count*3/4]);
        for (int pct = 5; pct < 100; pct += 5)
            _out.WriteLine($"q{pct}=" + owners[owners.Count*pct/100]);
        foreach (int pct in new[] { 41, 42, 43, 44, 46, 47, 48, 49 })
            _out.WriteLine($"q{pct}=" + owners[owners.Count*pct/100]);
    }
}
