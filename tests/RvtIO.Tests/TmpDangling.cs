using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

public sealed class TmpDangling
{
    private readonly ITestOutputHelper _out;
    public TmpDangling(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Check()
    {
        const string path = @"D:\IOExtend-samples\RvtIO\work\rvtio\dg2-scratch\ggp_drop20.rvt";
        using var root = RootStorage.OpenRead(path);
        var doc = TfSchemaReader.Read(root)!;
        var formats = new FormatsSchema(doc);
        var tags = PartitionFraming.TagsOf(formats)!.Value;
        var schemas = EntitySchemaReader.Read(root, doc);
        var owners = new HashSet<ulong>();
        var bodies = new List<byte[]>();
        foreach (var entry in root.OpenStorage("Partitions").EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream) continue;
            using var s = root.OpenStorage("Partitions").OpenStream(entry.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, 4, r =>
            {
                if (r.Owner == uint.MaxValue || r.Body.Length < 2) return;
                owners.Add(r.Owner);
                if (r.Channel == 102) bodies.Add(r.Body.ToArray());
            }, out _, out _);
        }
        _out.WriteLine($"owners={owners.Count} bodies={bodies.Count}");
        uint maxId = owners.Count == 0 ? 0 : (uint)owners.Max();

        var dangling = new Dictionary<string, int>();
        int elementIdClass = doc.Classes.First(c => c.Name == "ElementId").Id;
        var fieldType = new Dictionary<(string, string), bool>();
        bool IsElementId(string declaring, string field)
        {
            var key = (declaring, field);
            if (!fieldType.TryGetValue(key, out bool isId))
            {
                isId = false;
                var c = doc.Classes.FirstOrDefault(x => x.Name == declaring);
                var f = c?.Fields.FirstOrDefault(x => x.Name == field);
                if (f is not null && f.Type == 14 && (f.Mods & 15) == 0 && f.Ref == elementIdClass) isId = true;
                fieldType[key] = isId;
            }
            return isId;
        }
        var values = new Dictionary<string, List<long>>();
        foreach (var body in bodies)
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
            if (!doc.ById.ContainsKey(tag)) continue;
            var walker = new WideBlockWalker(doc, (d, f) => d.Name + "." + f.Name, []);
            if (schemas is not null) walker.UseEntitySchemas(schemas, passUnknown: true);
            var walk = walker.Run(body, 0, body.Length, tag);
            foreach (var cap in walk.Captures.Where(c => c.Kind == WideBlockWalker.CaptureKind.Integer && c.Integer > 0 && c.Integer <= maxId))
            {
                if (!IsElementId(cap.DeclaringClass, cap.Field)) continue;
                if (owners.Contains((ulong)cap.Integer)) continue;
                string key = cap.DeclaringClass + "." + cap.Field;
                dangling[key] = dangling.GetValueOrDefault(key) + 1;
                if (!values.TryGetValue(key, out var list)) values[key] = list = [];
                if (list.Count < 5) list.Add(cap.Integer);
            }
        }
        _out.WriteLine($"danglingKeys={dangling.Count}");
        foreach (var (k, v) in dangling.OrderByDescending(kv => kv.Value).Take(30))
            _out.WriteLine($"  {v}: {k} e.g.[{string.Join(",", values.GetValueOrDefault(k, []))}]");
        _out.WriteLine($"maxOwner={maxId}");
    }
}
