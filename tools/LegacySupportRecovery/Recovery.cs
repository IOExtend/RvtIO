using System.Buffers.Binary;
using OpenMcdf;
using RvtIO;
using RvtIO.Parsing;
using RvtIO.Writing;

internal static class Recovery
{
    private const int H = ElemTableLayout.HeaderLength;
    private sealed record Group(long Owner, long Template, long Model, long Support, long Marker, long OriginalModel, long OriginalSupport, long OriginalMarker);
    internal static void Run(string[] args)
    {
        using var final = new Archive(args[0]);
        using var source = new Archive(args[1]);
        using var donor = new Archive(args[2]);
        string output = args[4];
        var mapping = File.ReadLines(args[3]).ToArray();
        const string header = "physical\tdonor_physical\tdonor_host\tdonor_support\tdonor_instance\tsource_host\tsource_support\tsource_instance";
        if (mapping.Length == 0 || mapping[0] != header)
            throw new InvalidDataException("The mapping must begin with the documented tab-separated header.");
        var groups = mapping.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split('\t').Select(value => long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray())
            .Select(v => v is [> 0, > 0, > 0, > 0, > 0, > 0, > 0, > 0]
                ? new Group(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7])
                : throw new InvalidDataException("Each mapping row must contain eight positive element IDs."))
            .ToArray();
        if (groups.Length == 0 || groups.Select(g => g.Owner).Distinct().Count() != groups.Length)
            throw new InvalidDataException("Provide one mapping per original physical owner.");
        if (final.Schema.Release != 2021 || donor.Schema.Release != 2021 || final.Width != 4 ||
            donor.Width != 4 || source.Schema.Release < 2023)
            throw new InvalidDataException("Recovery requires a Revit 2023+ original and two Revit 2021 archives.");
        var byPhysical = groups.ToDictionary(g => g.Owner);
        var map = groups.ToDictionary(g => g.Template, g => g.Owner);
        var originalIds = groups.SelectMany(g => new[] { (g.Model, g.OriginalModel), (g.Support, g.OriginalSupport), (g.Marker, g.OriginalMarker) })
            .ToDictionary(p => p.Item1, p => p.Item2);
        var selected = originalIds.Keys.ToHashSet();
        if (selected.Any(final.Rows.ContainsKey)) throw new InvalidDataException("Restored IDs collide with existing Revit 2021 IDs");

        var baseOwnerIds = groups.Select(g => g.Owner).ToHashSet();
        donor.Collect(donor.Rows.Keys.Where(i => !final.Rows.ContainsKey(i)).Concat(baseOwnerIds).ToHashSet());
        final.Collect(baseOwnerIds);
        foreach (var group in groups)
        {
            string modelClass = donor.Class(group.Model);
            if (modelClass is not ("AnalyticalModelColumn" or "AnalyticalModelSlab") ||
                donor.Class(group.Support) != "BoundaryConditions" || donor.Class(group.Marker) != "FamilyInstance" ||
                donor.Rows[group.Model].OwningId != group.Template || donor.Rows[group.Marker].OwningId != group.Support ||
                !final.Rows.ContainsKey(group.Owner) || !source.Rows.ContainsKey(group.Owner) ||
                source.Rows[group.OriginalMarker].OwningId != group.OriginalSupport)
                throw new InvalidDataException($"Invalid native support group on physical element {group.Owner}.");
        }
        var donorAssets = new Dictionary<long, string>();
        foreach (long id in donor.Records.Keys.Except(baseOwnerIds))
        {
            string name = donor.Class(id);
            donorAssets.Add(id, name);
            if (name.StartsWith("AnalyticalModel", StringComparison.Ordinal) && byPhysical.TryGetValue(donor.Rows[id].OwningId, out var ownerGroup))
                map[id] = ownerGroup.Model;
        }

        var mandatory = new Queue<long>(selected);
        while (mandatory.TryDequeue(out long element))
        {
            var record = donor.Records[element];
            var additions = record.Payloads.SelectMany(donor.Decode).Where(f => f.IsElementId && f.Integer > 0).Select(f => f.Integer);
            var children = donor.Rows.Values.Where(r => r.OwningId == element && donorAssets.TryGetValue(r.Id, out string? n) && n == "ReferencePoint")
                .Select(r => r.Id);
            foreach (long dep in additions.Concat(children))
            {
                if (final.Rows.ContainsKey(dep) || map.ContainsKey(dep) || !donorAssets.TryGetValue(dep, out string? n)) continue;
                if (n is "Hub" or "ReferencePoint" or "Level")
                {
                    if (selected.Add(dep)) mandatory.Enqueue(dep);
                    long parent = donor.Rows[dep].OwningId;
                    if (parent > 0 && !final.Rows.ContainsKey(parent) && !map.ContainsKey(parent) && selected.Add(parent))
                        mandatory.Enqueue(parent);
                }
            }
        }

        var available = final.Rows.Keys.Concat(selected).ToHashSet();
        var dropped = donor.Rows.Keys.Where(id => !available.Contains(id) && id > 0).Select(id => checked((uint)id)).ToHashSet();
        var transcoder = new R22Transcoder(donor.Doc, final.Doc, 4, donor.Entities);
        transcoder.SetDroppedElementIds(dropped);
        var rewriteByPart = new Dictionary<string, Dictionary<(ulong, int), byte[]>>();
        var additionsByPart = new Dictionary<string, List<DowngradePartitionRewriter.Addition>>();
        string targetPart = final.LastPartition;
        string[] parentFields = ["m_deletion", "m_regenOnly", "m_appearanceParents", "m_nonDetermRegenChildren"];
        foreach (var g in groups)
        {
            var owner = final.Records[g.Owner];
            byte[] head = owner.Payloads[0].ToArray();
            byte[] body = BindOwner(owner.Payloads[1], Convert(donor.Records[g.Template].Payloads[1], verifyReferences: false), g.Model);
            Override(owner.Partition, g.Owner, 101, SortParents(head, g.Model));
            Override(owner.Partition, g.Owner, 102, body);
            Console.WriteLine($"host|{g.Owner}|{g.Model}|{g.Support}|{g.Marker}|{g.OriginalModel}|{g.OriginalSupport}|{g.OriginalMarker}");
        }
        foreach (long id in selected.Order())
        {
            var part = donor.Records[id];
            byte[][] bodies = part.Payloads.Select(p => Convert(p)).ToArray();
            bodies[0] = SortParents(bodies[0], null);
            if (!additionsByPart.TryGetValue(targetPart, out var adds)) additionsByPart.Add(targetPart, adds = []);
            adds.Add(new((ulong)id, bodies));
            Console.WriteLine($"record|{id}|{donor.Class(id)}|{donor.Rows[id].OwningId}|{string.Join(',', bodies.Select(b => b.Length))}");
        }
        var replacements = new Dictionary<string, byte[]>();
        foreach (string part in rewriteByPart.Keys.Concat(additionsByPart.Keys).Distinct())
        {
            var framing = new R22Transcoder(final.Doc, final.Doc, 4, final.Entities);
            var rewritten = DowngradePartitionRewriter.Rewrite(final.Partition(part), final.Tags, 4, final.Tags, 4,
                framing, null, null, out var gone, new(additionsByPart.GetValueOrDefault(part) ?? [],
                    rewriteByPart.GetValueOrDefault(part) ?? []));
            if (gone.Count > 0) throw new InvalidDataException($"Unrelated loss in partition {part}: {string.Join(',', gone)}");
            replacements.Add("Partitions/" + part, rewritten);
        }

        const string tableName = "ElemTable", incrementName = "DocumentIncrementTable";
        byte[] rawTable = final.Read("Global", tableName), increments = final.Read("Global", incrementName);
        byte[] rawRows = PagedGzipStream.Decompress(rawTable)!;
        byte[] decoded = PagedGzipStream.Decompress(increments)!;
        var incrementTable = DocumentIncrementTable.TryParse(decoded, final.Schema, out var reason);
        if (incrementTable is null || incrementTable.Local.Count == 0)
            throw new InvalidDataException($"Cannot read the local revision table: {reason}");
        var inc = incrementTable.Local;
        int currentEpisode = checked((int)inc[^1].Greatest);
        int w = ElemTableLayout.RowLength(final.Schema)!.Value;
        var layout = final.Schema.Get("ElemRec")!;
        int Offset(params string[] p) => ElemTableLayout.Offset(final.Schema, layout, p)!.Value;
        int pId = Offset("m_id"), pOriginal = Offset("m_history", "m_originalElementId"), pCreated = Offset("m_history", "m_creationDate"),
            pChanged = Offset("m_history", "m_lastModificationDate"), pUser = Offset("m_history", "m_lastUserModificationDate"),
            pPart = Offset("m_partitionId"), pOwner = Offset("m_OwningElementId");
        var finalHistory = final.History();
        var sourceHistory = source.History();
        foreach (var group in groups)
        {
            var expected = source.Rows[group.Owner];
            var existing = final.Rows[group.Owner];
            if (Uid(sourceHistory, expected.OriginalId, expected.CreationDate) !=
                Uid(finalHistory, existing.OriginalId, existing.CreationDate))
                throw new InvalidDataException($"Physical owner {group.Owner} has a different UniqueId.");
        }
        var merged = new byte[rawRows.Length + w * selected.Count];
        int off = H;
        rawRows.AsSpan(0, H).CopyTo(merged);
        BinaryPrimitives.WriteInt32LittleEndian(merged.AsSpan(2), final.Rows.Count + selected.Count);
        var unique = final.Rows.Values.Select(r => Uid(finalHistory, r.OriginalId, r.CreationDate)).ToHashSet();
        foreach (long id in available.Order())
        {
            if (final.Rows.TryGetValue(id, out var existing))
                rawRows.AsSpan(H + final.Index[id] * w, w).CopyTo(merged.AsSpan(off, w));
            else
            {
                var row = merged.AsSpan(off, w);
                var donorRow = donor.Rows[id];
                long physical = map.GetValueOrDefault(donorRow.OwningId, donorRow.OwningId);
                if (physical > 0 && !available.Contains(physical)) throw new InvalidDataException($"Unknown owning row {id}/{physical}");
                int episode = currentEpisode;
                long original = id;
                if (originalIds.TryGetValue(id, out long requested))
                {
                    var src = source.Rows[requested];
                    original = src.OriginalId;
                    episode = src.CreationDate;
                    if (Uid(sourceHistory, original, episode) != Uid(finalHistory, original, episode))
                        throw new InvalidDataException($"Source history differs for {requested}");
                }
                if (!unique.Add(Uid(finalHistory, original, episode))) throw new InvalidDataException($"Duplicate UniqueId {id}");
                W(row, pId, id); W(row, pOriginal, original); W(row, pCreated, episode);
                W(row, pChanged, currentEpisode); W(row, pUser, currentEpisode);
                W(row, pPart, donorRow.PartitionId); W(row, pOwner, physical);
            }
            off += w;
        }
        rawRows.AsSpan(H + final.Rows.Count * w).CopyTo(merged.AsSpan(off));
        var allocator = QueuedField(merged, final.Doc, "IdentifierSource", "m_last")!.Value;
        W(merged, allocator.Start, Math.Max(available.Max(), BitConverter.ToInt32(merged, allocator.Start)));
        if (ElemTableLayout.TryParse(merged, final.Schema)?.Count != available.Count) throw new InvalidDataException("Invalid merged row table");
        replacements.Add("Global/" + tableName, PagedStreamCodec.Reencode(rawTable, merged));
        replacements.Add("Global/" + incrementName, PagedStreamCodec.Reencode(increments, DocumentIncrementFixup.RefreshTotals(decoded, final.Doc,
            checked((uint)(inc[^1].TotalElements + selected.Count)))));
        final.Dispose();
        ContainerRewriter.Rewrite(args[0], output, replacements);

        byte[] BindOwner(byte[] owner, byte[] template, long model)
        {
            var own = final.Walk(owner);
            var tmpl = final.Walk(template);
            var user = own.Captures.Single(f => f.Field == "m_analyticalModelId" &&
                f.DeclaringClass is "StructFloorUserData" or "StructColumnUserData");
            if (user.Offset < 0 || user.Integer != -1) throw new InvalidDataException("Original owner is already bound");
            var bytes = owner.ToArray();
            W(bytes, user.Offset, model);
            int settings = tmpl.Queue.Select((c, i) => (c, i)).Single(p => final.Walker.Class(p.c)?.Name == "AnalyticalSettingsCell").i;
            int cellList = Slot(own, "CellList");
            int first = own.Captures.First(c => c.QueueIndex == cellList && c.Field == "m_cells" && c.TargetQueueIndex is not null).TargetQueueIndex!.Value;
            var arr = Field(bytes, own, cellList, "m_cells");
            W(bytes, arr.Start, checked(BitConverter.ToInt32(bytes, arr.Start) + 1));
            int insert = own.QueueStart[first];
            var slot = tmpl.QueueStart[settings];
            var settingsBody = template.AsSpan(slot, End(tmpl, settings) - slot);
            byte[] result = [..bytes.AsSpan(0,arr.Start+4),..BitConverter.GetBytes(uint.MaxValue),
                ..BitConverter.GetBytes((ushort)tmpl.Queue[settings]),..bytes.AsSpan(arr.Start+4,insert-arr.Start-4),
                ..settingsBody,..bytes.AsSpan(insert)];
            var bound = final.Walk(result);
            if (!bound.Captures.Any(f => f.DeclaringClass == "AnalyticalSettingsCell" && f.Field == "m_enableAnalyticalModel" && f.Integer == 1) ||
                !bound.Captures.Any(f => f.DeclaringClass == user.DeclaringClass && f.Field == user.Field && f.Integer == model))
                throw new InvalidDataException("Physical owner binding failed");
            return result;
        }

        byte[] Convert(byte[] original, bool verifyReferences = true)
        {
            var walk = donor.Walk(original);
            byte[] b = original.ToArray();
            foreach (var f in walk.Captures)
            {
                if (f.IsElementId && f.Offset >= 0 && map.TryGetValue(f.Integer, out long mapped) && mapped != f.Integer)
                    W(b, f.Offset, mapped);
            }
            var sets = walk.Captures.Where(c => c.DeclaringClass == "AnalyticalChainRef" && c.Field == "m_chainMembers" &&
                c.QueueIndex >= 0 && c.Offset >= 0).Select(c => c.QueueIndex).Distinct()
                .Select(slot => PayloadSpan.Find(original, walk.QueueStart[slot], End(walk, slot), donor.Doc, walk.Queue[slot], "m_chainMembers")!.Value)
                .OrderByDescending(span => span.Start);
            foreach (var span in sets)
            {
                var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(b.AsSpan(span.Start, span.End - span.Start));
                if (values[0] != values.Length - 1) throw new InvalidDataException("Invalid chain member set");
                var ids = values[1..].ToArray().Where(id => !dropped.Contains(unchecked((uint)id))).Distinct().Order().ToArray();
                b = [..b.AsSpan(0,span.Start),..BitConverter.GetBytes(ids.Length),
                    ..System.Runtime.InteropServices.MemoryMarshal.AsBytes(ids.AsSpan()),..b.AsSpan(span.End)];
            }
            if (transcoder.Payload(b, 0, b.Length, out var error) is not { } encoded)
                throw new InvalidDataException($"Native payload {donor.Walker.Class(BitConverter.ToUInt16(b))?.Name} failed: {error}");
            if (verifyReferences) Verify(encoded);
            else final.Walk(encoded);
            return encoded;
        }

        void Verify(byte[] bytes)
        {
            var wlk = final.Walk(bytes);
            var missing = wlk.Captures.Where(f => f.IsElementId && f.Integer > 0 && !available.Contains(f.Integer)).ToArray();
            if (missing.Length > 0) throw new InvalidDataException($"{final.Walker.Class(BitConverter.ToUInt16(bytes))?.Name} unresolved refs: " +
                string.Join(',', missing.Select(f => $"{f.Key}={f.Integer}")));
        }

        int Slot(WideBlockWalker.Walk wlk, string className) => wlk.Queue.Select((c, i) => (c, i))
            .Single(p => final.Walker.Class(p.c)?.Name == className).i;
        (int Start, int End) Field(byte[] b, WideBlockWalker.Walk walk, int slot, string f) =>
            PayloadSpan.Find(b, walk.QueueStart[slot], End(walk, slot), final.Doc, walk.Queue[slot], f)!.Value;
        byte[] SortParents(byte[] b, long? include)
        {
            foreach (string field in parentFields)
            {
                var wlk = final.Walk(b);
                int p = Slot(wlk, "ElementParents");
                if (PayloadSpan.Find(b, wlk.QueueStart[p], End(wlk, p), final.Doc, wlk.Queue[p], field) is not { } range) continue;
                var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(b.AsSpan(range.Start, range.End - range.Start));
                if (values[0] != values.Length - 1) throw new InvalidDataException("Invalid parent list");
                var ids = values[1..].ToArray().AsEnumerable();
                if (include is not null && field == "m_deletion") ids = ids.Append(checked((int)include.Value));
                var result = ids.Distinct().Order().ToArray();
                if (!values[1..].SequenceEqual(result))
                    b = [..b.AsSpan(0,range.Start),..BitConverter.GetBytes(result.Length),
                        ..System.Runtime.InteropServices.MemoryMarshal.AsBytes(result.AsSpan()),..b.AsSpan(range.End)];
            }
            Verify(b);
            return b;
        }
        void Override(string partition, long owner, int channel, byte[] bytes)
        {
            if (!rewriteByPart.TryGetValue(partition, out var result)) rewriteByPart.Add(partition, result = []);
            result.Add(((ulong)owner, channel), bytes);
        }
    }


    internal static (int Start, int End)? QueuedField(byte[] data, SchemaDocument schema, string typeName,
        string field, bool readEntities = false)
    {
        if (schema.Classes.FirstOrDefault(c => c.Name == typeName) is not { } type || data.Length < 2)
            return null;
        var walker = new WideBlockWalker(schema, (_, _) => null, []) { StepBudget = 16L * data.Length + 65536 };
        if (readEntities && EntitySchemaReader.Read(data, schema) is { } entities)
            walker.UseEntitySchemas(entities, passUnknown: true);
        var walk = walker.Run(data, 0, data.Length, BitConverter.ToUInt16(data));
        int slot = walk.Queue.IndexOf(type.Id);
        if (slot < 0 || !walk.RootRead || walk.Error is not null) return null;
        return PayloadSpan.Find(data, walk.QueueStart[slot], End(walk, slot), schema, type.Id, field);
    }
    private static int End(WideBlockWalker.Walk w, int i) => i + 1 < w.QueueStart.Count ? w.QueueStart[i + 1] : w.Position;
    private static void W(Span<byte> b, int o, long n) => BinaryPrimitives.WriteInt32LittleEndian(b[o..], checked((int)n));
    private static string Uid(RvtFileReader.EpisodeTableParseResult history, long id, int ep)
    {
        var guid = ElementExportIds.EpisodeGuid(new(unchecked((uint)id), ep), history.Guids, history.X);
        return guid.HasValue ? ElementExportIds.UniqueId(guid.Value, unchecked((uint)id))
            : throw new InvalidDataException($"No GUID for creation episode {ep}.");
    }

    private sealed class Archive : IDisposable
    {
        public readonly RootStorage Root;
        public readonly SchemaDocument Doc;
        public readonly FormatsSchema Schema;
        public readonly WideBlockWalker Walker;
        public readonly PartitionFraming.Tags Tags;
        public readonly IReadOnlyDictionary<long, ElemTableLayout.Row> Rows;
        public readonly IReadOnlyDictionary<long, int> Index;
        public readonly int Width;
        public readonly string LastPartition;
        public readonly Dictionary<long, (string Partition, byte[][] Payloads)> Records = [];
        public readonly Dictionary<Guid, SchemaField[]?> Entities;

        public Archive(string path)
        {
            Root = RootStorage.OpenRead(path);
            Doc = TfSchemaReader.Read(Root) ?? throw new InvalidDataException("Cannot read Formats/Latest.");
            Schema = FormatsSchema.TryRead(Root) ?? throw new InvalidDataException("Cannot read the file schema.");
            Width = ElemTableLayout.IdWidth(Schema)!.Value;
            Tags = PartitionFraming.TagsOf(Schema)!.Value;
            var ordered = ElemTableLayout.TryParse(PagedGzipStream.Read(Root, "Global", "ElemTable")!, Schema)!;
            Rows = ordered.ToDictionary(r => r.Id);
            Index = ordered.Select((r, i) => (r.Id, i)).ToDictionary(r => r.Id, r => r.i);
            var p = Root.OpenStorage("Partitions");
            LastPartition = p.EnumerateEntries().Where(e => e.Type == EntryType.Stream && int.TryParse(e.Name, out _)).MaxBy(e => int.Parse(e.Name)).Name;
            Entities = EntitySchemaReader.Read(Root, Doc);
            Walker = new WideBlockWalker(Doc, (c, f) => WideBlockWalker.AllItemsKeyPrefix + c.Name + "." + f.Name, []);
            Walker.UseEntitySchemas(Entities, passUnknown: true);
        }
        public string Class(long id) => Walker.Class(BitConverter.ToUInt16(Records[id].Payloads[1]))!.Name;
        public byte[] Partition(string name) => Read("Partitions", name);
        public byte[] Read(string store, string key)
        {
            using var s = Root.OpenStorage(store).OpenStream(key); byte[] b = new byte[s.Length]; s.ReadExactly(b); return b;
        }
        public void Collect(HashSet<long> ids)
        {
            var parts = Root.OpenStorage("Partitions").EnumerateEntries().Where(e => e.Type == EntryType.Stream && int.TryParse(e.Name, out _)).Select(e => e.Name);
            foreach (string part in parts)
            {
                var bytes = Partition(part);
                if (!PartitionFraming.Walk(bytes, Tags, Width, r =>
                {
                    if (r.Block != 0 || !ids.Contains((long)r.Owner)) return;
                    if (!Records.TryGetValue((long)r.Owner, out var element)) Records.Add((long)r.Owner, element = (part, new byte[3][]));
                    if (element.Payloads[r.Channel - 101] != null) throw new InvalidDataException($"Duplicate record {r.Owner}");
                    element.Payloads[r.Channel - 101] = r.Body.ToArray();
                }, out var e, out _)) throw new InvalidDataException(e);
            }
            foreach (var v in Records.Values)
                if (v.Payloads.Any(p => p is null)) throw new InvalidDataException($"Incomplete archival payload: {v.Partition}");
        }
        public WideBlockWalker.Walk Walk(byte[] b)
        {
            int cls = BitConverter.ToUInt16(b);
            Walker.StepBudget = 65536L + b.Length * 16L;
            var w = Walker.Run(b, 0, b.Length, cls);
            if (!w.Exact) throw new InvalidDataException($"{Walker.Class(cls)?.Name}: {w.Error}");
            return w;
        }
        public IEnumerable<WideBlockWalker.Capture> Decode(byte[] b) => Walk(b).Captures;
        public RvtFileReader.EpisodeTableParseResult History() => RvtFileReader.ParseEpisodeGuids(
            [PagedGzipStream.Read(Root, "Global", "History")!], null, new FileSchema(Doc));
        public void Dispose() => Root.Dispose();
    }
}
