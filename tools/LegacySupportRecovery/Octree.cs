using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;

internal static class Octree
{
    internal static void Initialize(string input, string blank, string output)
    {
        using var current = RootStorage.OpenRead(input);
        using var vanilla = RootStorage.OpenRead(blank);
        var currentDoc = TfSchemaReader.Read(current)!;
        var blankDoc = TfSchemaReader.Read(vanilla)!;
        var currentSchema = FormatsSchema.TryRead(current)!;
        if (currentSchema.Release != 2021 || FormatsSchema.TryRead(vanilla)?.Release != 2021)
            throw new InvalidDataException("Both inputs must be Revit 2021 archives.");
        var (owner, name, raw, body) = ReadOctree(current, currentDoc);
        var (_, _, _, vanillaBody) = ReadOctree(vanilla, blankDoc);
        var trans = new R22Transcoder(blankDoc, currentDoc, 4);
        var encoded = trans.Payload(vanillaBody, 0, vanillaBody.Length, out var failure) ??
            throw new InvalidDataException($"Native empty octree transcode failed: {failure}");
        ushort cls = BitConverter.ToUInt16(body);
        if (BitConverter.ToUInt16(encoded) != cls) throw new InvalidDataException("Unexpected empty octree body class.");
        var walker = new WideBlockWalker(currentDoc, (c, f) => WideBlockWalker.AllItemsKeyPrefix + c.Name + "." + f.Name, []);
        var walk = walker.Run(encoded, 0, encoded.Length, cls);
        var before = walker.Run(body, 0, body.Length, cls);
        if (!walk.Exact || !before.Exact || walk.Captures.Any(f => f.IsElementId && f.Integer > 0 && f.DeclaringClass.Contains("Octree")) ||
            walk.Captures.Single(f => f.DeclaringClass == "AnalyticalOctreeContainer" && f.Field == "m_cellMap").Integer != 0)
            throw new InvalidDataException("Native octree is not empty or cannot be read exactly.");
        var target = PayloadSpan.Find(body, 2, body.Length, currentDoc, cls, "m_oOctreeContainer")!.Value;
        var source = PayloadSpan.Find(encoded, 2, encoded.Length, currentDoc, cls, "m_oOctreeContainer")!.Value;
        if (target.Start != source.Start || before.Captures.Single(f => f.DeclaringClass == "AnalyticalOctreeTracker" && f.Field == "m_oOctreeContainer").TargetQueueIndex != null)
            throw new InvalidDataException("Target octree already has a container or does not match.");
        byte[] merged = [.. body.AsSpan(0, target.Start), .. encoded.AsSpan(source.Start)];
        var verify = walker.Run(merged, 0, merged.Length, cls);
        if (!verify.Exact) throw new InvalidDataException("Merged octree cannot be read exactly.");
        var tags = PartitionFraming.TagsOf(currentSchema)!.Value;
        var rewritten = DowngradePartitionRewriter.Rewrite(raw, tags, 4, tags, 4,
            new(currentDoc, currentDoc, 4, EntitySchemaReader.Read(current, currentDoc)), null, null, out var dropped,
            new([], new Dictionary<(ulong, int), byte[]> { { (owner, 102), merged } }));
        if (dropped.Count != 0) throw new InvalidDataException($"Would omit records: {string.Join(",", dropped)}");
        current.Dispose();
        ContainerRewriter.Rewrite(input, output, new Dictionary<string, byte[]> { { "Partitions/" + name, rewritten } });
        Console.WriteLine($"initialized|{owner}|{name}|{body.Length}|{merged.Length}|{output}");
    }

    static (ulong Owner, string Name, byte[] Raw, byte[] Body) ReadOctree(RootStorage root, SchemaDocument doc)
    {
        var schema = new FormatsSchema(doc);
        var latest = PagedGzipStream.Read(root, "Global", "Latest")!;
        var registry = Recovery.QueuedField(latest, doc, "UniqueElementsTracking", "m_elemIds", readEntities: true)!.Value;
        ulong owner = checked((ulong)BitConverter.ToInt32(latest, registry.Start + 4 + 14 * 4));
        var store = root.OpenStorage("Partitions");
        var tags = PartitionFraming.TagsOf(schema)!.Value;
        foreach (var entry in store.EnumerateEntries().Where(e => e.Type == EntryType.Stream && int.TryParse(e.Name, out _)))
        {
            using var s = store.OpenStream(entry.Name); byte[] raw = new byte[s.Length]; s.ReadExactly(raw);
            byte[]? result = null;
            if (!PartitionFraming.Walk(raw, tags, 4, r =>
            {
                if (r.Block == 0 && r.Channel == 102 && r.Owner == owner)
                    result = result is null ? r.Body.ToArray() : throw new InvalidDataException("Duplicate octree");
            }, out string? error, out _)) throw new InvalidDataException(error);
            if (result is not null) return (owner, entry.Name, raw, result);
        }
        throw new InvalidDataException("Octree record missing");
    }
}
