using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpUnknownWhy
{
    private readonly ITestOutputHelper _out;
    public TmpUnknownWhy(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        const string srcPath = @"D:\IOExtend-samples\RvtIO\downloads\2027\GGP_1077-BFU_KPP1_AR_R20.rvt";
        using var srcRoot = RootStorage.OpenRead(srcPath);
        var srcDoc = TfSchemaReader.Read(srcRoot)!;
        using var profRoot = RootStorage.OpenRead(@"D:\IOExtend-samples\RvtIO\gate-corpus\rstbasic20\rstbasic20.rvt");
        var profDoc = TfSchemaReader.Read(profRoot)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(srcDoc))!.Value;
        var transcoder = new R22Transcoder(srcDoc, profDoc, 8, EntitySchemaReader.Read(srcRoot, srcDoc));
        transcoder.SetElementUniverse(new HashSet<uint>(), 0);
        var wanted = new HashSet<ulong> { 495937, 608769, 929152 };
        foreach (var entry in srcRoot.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = srcRoot.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, 8, r =>
            {
                if (r.Channel != 102 || !wanted.Contains(r.Owner)) return;
                var body = r.Body.ToArray();
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                var result = transcoder.Payload(body, 0, body.Length, out string? failure);
                _out.WriteLine($"owner={r.Owner} class={srcDoc.ById.GetValueOrDefault(tag)?.Name} len={body.Length} ok={result is not null} failure={failure}");
            }, out _, out _);
        }
    }
}
