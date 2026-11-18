using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpSchemaUsage
{
    private readonly ITestOutputHelper _out;
    public TmpSchemaUsage(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        var wanted = new Guid("1b022a2b-5722-4787-928a-4f29ca9d8811");
        using var root = RootStorage.OpenRead(@"D:\IOExtend-samples\RvtIO\downloads\2027\DACHsampleproject.rvt");
        var doc = TfSchemaReader.Read(root)!;
        var tags = PartitionFraming.TagsOf(new FormatsSchema(doc))!.Value;
        using var s = root.OpenStorage("Partitions").OpenStream("86");
        var raw = new byte[s.Length];
        s.ReadExactly(raw);
        int uses = 0, entities = 0;
        var users = new Dictionary<string, int>();
        PartitionFraming.Walk(raw, tags, 8, r =>
        {
            if (r.Channel != 102 || r.Body.Length < 2) return;
            var body = r.Body.ToArray();
            for (int i = 0; i + 16 <= body.Length; i++)
            {
                if (new Guid(body.AsSpan(i, 16)) != wanted) continue;
                uses++;
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                string cls = doc.ById.GetValueOrDefault(tag)?.Name ?? "?";
                users[cls] = users.GetValueOrDefault(cls) + 1;
            }
        }, out _, out _);
        _out.WriteLine($"raw guid hits={uses}");
        foreach (var (k, v) in users.OrderByDescending(kv => kv.Value).Take(10)) _out.WriteLine($"  {k}: {v}");
    }
}
