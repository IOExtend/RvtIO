using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static class WallOpeningWarnings
{
    internal static Dictionary<long, long> OpeningOwners(SchemaDocument schema, IReadOnlyDictionary<long, byte[]> warnings)
    {
        var owners = new Dictionary<long, long>();
        if (warnings.Count == 0)
            return owners;
        var formats = new FormatsSchema(schema);
        ushort warningTag = Tag(formats, "PostedWarningElem");
        ushort rectWarningTag = Tag(formats, "WallRectOpeningWarning");
        var walker = WarningWalker(schema);
        foreach (var (id, body) in warnings)
        {
            if (!HasTag(body, warningTag))
                continue;
            var walk = walker.Run(body, 0, body.Length, warningTag);
            if (walk.Exact && walk.Queue is [var kind] && kind == rectWarningTag
                && walk.Captures.Where(c => c.AtRoot && c.DeclaringClass == "Element" && c.Field == "m_id")
                    .Select(c => c.Integer).SequenceEqual([id])
                && walk.Captures.Where(c => c.DeclaringClass == "WallRectOpeningWarning" && c.Field == "m_elemIds" && c.Key.Contains('@'))
                    .Select(c => c.Integer).ToArray() is [> 0 and var opening])
                owners.Add(id, opening);
        }
        return owners;
    }

    internal static void Stage(RootStorage root, SchemaDocument schema, int idWidth, int partition, long wall,
        IReadOnlyList<long> openings, ulong highestStoredOwner, ParameterWriter.Pending pending)
    {
        if (openings.Count == 0)
            throw Layout();
        const string tableName = "ElemTable";
        const string latestName = "Latest";
        byte[] tableStream = ReadStored(root, pending, tableName);
        byte[] latestStream = ReadStored(root, pending, latestName);
        var formats = new FormatsSchema(schema);
        if (PagedGzipStream.Decompress(tableStream) is not { } table
            || ElemTableLayout.TryParse(table, formats) is not { Count: > 0 } rows
            || ElemTableLayout.RowLength(formats) is not int rowWidth
            || PagedGzipStream.Decompress(latestStream) is not { Length: >= 6 } latest
            || PagedGzipStream.Read(root, "Global", "DocumentIncrementTable") is not { } incrementBytes
            || DocumentIncrementTable.TryParse(incrementBytes, formats, out _) is not { } increments)
            throw Layout();

        var byId = rows.ToDictionary(row => row.Id);
        if (!byId.TryGetValue(wall, out var host) || host.LastModificationDate is not int episode || episode < 0
            || host.PartitionId != 0 || DocumentIncrementTable.Route(increments, episode) != partition
            || openings.Distinct().Count() != openings.Count
            || openings.Any(opening => !byId.TryGetValue(opening, out var row) || row.OwningId != wall || row.PartitionId != 0))
            throw Layout();

        int rowsEnd = checked(ElemTableLayout.HeaderLength + rows.Count * rowWidth);
        long lastId = ReadIdentifierCounter(table.AsSpan(rowsEnd), formats, idWidth, out int identifierOffset);
        long maxId = idWidth == sizeof(int) ? int.MaxValue : long.MaxValue;
        if (lastId < rows[^1].Id || (ulong)lastId < highestStoredOwner || maxId - lastId < openings.Count)
            throw Layout();

        var newRows = openings.Select((_, i) =>
        {
            long id = lastId + i + 1;
            return new ElemTableLayout.Row(id, id, episode, 0, -1, episode);
        }).ToArray();

        var walker = WarningWalker(schema);
        long[] existing = ReadRegistry(latest, walker, formats, idWidth, out int registryOffset);
        if (existing.Any(id => !byId.ContainsKey(id)))
            throw Layout();

        var additions = newRows.Zip(openings, (row, opening) =>
            new PartitionOwnerAddition(partition, (ulong)row.Id, SerializeWarning(row.Id, opening))).ToArray();
        long[] expectedIds = [.. existing, .. newRows.Select(row => row.Id)];
        byte[] newLatest = ReplaceRegistry(latest, registryOffset, idWidth, existing.Length, expectedIds);
        if (!ReadRegistry(newLatest, walker, formats, idWidth, out _).SequenceEqual(expectedIds))
            throw Layout();

        byte[] appended = Encode(writer =>
        {
            foreach (var row in newRows)
            {
                // The row places identity first in the narrow layout and history first in the wide layout.
                if (idWidth == sizeof(int)) WriteId(writer, row.Id);
                WriteId(writer, row.OriginalId);
                writer.Write(episode);
                writer.Write(episode);
                writer.Write(episode);
                if (idWidth == sizeof(long)) WriteId(writer, row.Id);
                if (idWidth == sizeof(int)) writer.Write(row.PartitionId);
                WriteId(writer, row.OwningId);
                if (idWidth == sizeof(long)) writer.Write(row.PartitionId);
            }
        });
        if (appended.Length != rowWidth * newRows.Length)
            throw Layout();
        byte[] newTable = ParameterWriter.ReplaceValue(table, rowsEnd, 0, appended);
        BinaryPrimitives.WriteInt32LittleEndian(newTable.AsSpan(sizeof(ushort)), rows.Count + newRows.Length);
        var counter = newTable.AsSpan(rowsEnd + appended.Length + identifierOffset);
        if (idWidth == sizeof(int)) BinaryPrimitives.WriteInt32LittleEndian(counter, (int)newRows[^1].Id);
        else BinaryPrimitives.WriteInt64LittleEndian(counter, newRows[^1].Id);
        if (ElemTableLayout.TryParse(newTable, formats) is not { } checkedRows || !checkedRows.SequenceEqual(rows.Concat(newRows)))
            throw Layout();

        byte[] encodedTable = PagedStreamCodec.Reencode(tableStream, newTable);
        byte[] encodedLatest = PagedStreamCodec.Reencode(latestStream, newLatest);
        pending.Metadata["Global/" + tableName] = encodedTable;
        pending.Metadata["Global/" + latestName] = encodedLatest;
        pending.AddedProjectOwners.AddRange(additions);

        byte[][] SerializeWarning(long id, long opening)
        {
            const int invisibleViewFlags = -32768;
            const int warningHeaderFlags = 8202;
            ushort warningTag = Tag("PostedWarningElem");
            ushort rectWarningTag = Tag("WallRectOpeningWarning");
            ushort headerTag = Tag("ElementHeader");
            byte[] header = Encode(writer =>
            {
                writer.Write(headerTag);
                writer.Write(0); // Empty regeneration history.
                for (int i = 0; i < 6; i++) WriteId(writer, -1);
                writer.Write(invisibleViewFlags);
                writer.Write(warningHeaderFlags);
                writer.Write(warningTag);
                writer.Write(0); // No outline.
                writer.Write(uint.MaxValue);
                writer.Write(Tag("ElementParents"));
                writer.Write(2);
                WriteId(writer, opening);
                WriteId(writer, id);
                var parents = formats.Get("ElementParents")!;
                for (int i = 0; i < parents.Fields.Count - 2; i++) writer.Write(0);
                writer.Write(false);
            });
            byte[] element = Encode(writer =>
            {
                writer.Write(warningTag);
                for (int i = 0; i < 8; i++) writer.Write(0); // No parameters or geometry.
                writer.Write(1); // Local document handle.
                WriteId(writer, id);
                for (int i = 0; i < 7; i++) WriteId(writer, -1);
                writer.Write(false);
                writer.Write(false);
                writer.Write(false);
                writer.Write(uint.MaxValue);
                writer.Write(rectWarningTag);
                writer.Write(1);
                WriteId(writer, opening);
            });
            byte[] geometry = Encode(writer => writer.Write(Tag("SerializedDummy")));
            var headerWalk = walker.Run(header, 0, header.Length, headerTag);
            var elementWalk = walker.Run(element, 0, element.Length, warningTag);
            if (!headerWalk.Exact || !elementWalk.Exact || elementWalk.Queue is not [var kind] || kind != rectWarningTag
                || !headerWalk.Captures.Where(c => c.Field == "m_deletion" && c.Key.Contains('@')).Select(c => c.Integer).SequenceEqual([opening, id])
                || !elementWalk.Captures.Where(c => c.DeclaringClass == "Element" && c.Field == "m_id").Select(c => c.Integer).SequenceEqual([id])
                || !elementWalk.Captures.Where(c => c.Field == "m_elemIds" && c.Key.Contains('@')).Select(c => c.Integer).SequenceEqual([opening]))
                throw Layout();
            return [header, element, geometry];
        }

        ushort Tag(string name) => WallOpeningWarnings.Tag(formats, name);

        void WriteId(BinaryWriter writer, long id)
        {
            if (idWidth == sizeof(int)) writer.Write(checked((int)id));
            else writer.Write(id);
        }
    }

    internal static void StageRemoval(RootStorage root, SchemaDocument schema, PartitionFraming.Tags tags, int idWidth,
        long wall, IReadOnlyList<long> openings, ParameterWriter.Pending pending)
    {
        var formats = new FormatsSchema(schema);
        byte[] tableStream = ReadStored(root, pending, "ElemTable");
        byte[] latestStream = ReadStored(root, pending, "Latest");
        if (openings.Count == 0 || openings.Distinct().Count() != openings.Count
            || PagedGzipStream.Decompress(tableStream) is not { } table
            || ElemTableLayout.TryParse(table, formats) is not { Count: > 0 } rows
            || ElemTableLayout.RowLength(formats) is not int rowWidth
            || PagedGzipStream.Decompress(latestStream) is not { Length: >= 6 } latest
            || PagedGzipStream.Read(root, "Global", "DocumentIncrementTable") is not { } incrementBytes
            || DocumentIncrementTable.TryParse(incrementBytes, formats, out _) is not { } increments)
            throw Layout();
        var byId = rows.ToDictionary(row => row.Id);
        if (openings.Any(id => !byId.TryGetValue(id, out var row) || row.OwningId != wall || row.PartitionId != 0))
            throw Layout();

        int rowsEnd = checked(ElemTableLayout.HeaderLength + rows.Count * rowWidth);
        _ = ReadIdentifierCounter(table.AsSpan(rowsEnd), formats, idWidth, out _);
        var walker = WarningWalker(schema);
        long[] existing = ReadRegistry(latest, walker, formats, idWidth, out int registryOffset);
        if (existing.Any(id => !byId.ContainsKey(id)))
            throw Layout();

        var registered = existing.Select(id => (ulong)id).ToHashSet();
        var records = new Dictionary<(ulong Owner, int Channel), (int Partition, byte[] Body)>();
        ParameterWriter.ScanPartitions(root, tags, idWidth, (partition, record) =>
        {
            if (record.Block == 0 && registered.Contains(record.Owner)
                && !records.TryAdd((record.Owner, record.Channel), (partition, record.Body.ToArray())))
                throw Layout();
        }, pending);
        foreach (var addition in pending.AddedProjectOwners)
        {
            if (!registered.Contains(addition.Owner))
                continue;
            for (int channel = 101; channel <= 103; channel++)
                if (!records.TryAdd((addition.Owner, channel), (addition.Partition, addition.Bodies[channel - 101])))
                    throw Layout();
        }

        var removed = new Dictionary<long, (int Partition, ulong Owner)>();
        int warningTag = Tag(formats, "PostedWarningElem");
        int rectWarningTag = Tag(formats, "WallRectOpeningWarning");
        foreach (long id in existing)
        {
            ulong owner = (ulong)id;
            if (!records.TryGetValue((owner, 102), out var element) || !HasTag(element.Body, warningTag))
                throw Layout();
            var elementWalk = walker.Run(element.Body, 0, element.Body.Length, warningTag);
            if (!elementWalk.Exact)
                throw Layout();
            if (!elementWalk.Queue.Contains(rectWarningTag))
                continue;

            var failing = elementWalk.Captures.Where(c => c.DeclaringClass == "WallRectOpeningWarning"
                && c.Key.StartsWith(WideBlockWalker.AllItemsKeyPrefix + "m_elemIds@", StringComparison.Ordinal)).Select(c => c.Integer).ToArray();
            if (failing.All(id => !openings.Contains(id)))
                continue;
            if (failing is not [var opening]
                || !elementWalk.Captures.Where(c => c.AtRoot && c.DeclaringClass == "Element" && c.Field == "m_id")
                    .Select(c => c.Integer).SequenceEqual([id])
                || byId[id] is not { OwningId: -1, PartitionId: 0, LastModificationDate: int episode } row
                || row.OriginalId != id || DocumentIncrementTable.Route(increments, episode) != element.Partition
                || !records.TryGetValue((owner, 101), out var header) || header.Partition != element.Partition
                || !records.TryGetValue((owner, 103), out var geometry) || geometry.Partition != element.Partition
                || geometry.Body.Length != sizeof(ushort) || !HasTag(geometry.Body, Tag(formats, "SerializedDummy"))
                || !HasTag(header.Body, Tag(formats, "ElementHeader")))
                throw Layout();
            var headerWalk = walker.Run(header.Body, 0, header.Body.Length, Tag(formats, "ElementHeader"));
            if (!headerWalk.Exact || !headerWalk.Captures.Where(c => c.DeclaringClass == "ElementParents"
                    && c.Key.StartsWith(WideBlockWalker.AllItemsKeyPrefix + "m_deletion@", StringComparison.Ordinal))
                    .Select(c => c.Integer).SequenceEqual([opening, id])
                || !removed.TryAdd(opening, (element.Partition, owner)))
                throw Layout();
        }
        if (removed.Count != openings.Count)
            throw Layout();
        var removedIds = removed.Values.Select(item => (long)item.Owner).ToHashSet();
        long[] keptIds = existing.Where(id => !removedIds.Contains(id)).ToArray();
        byte[] newLatest = ReplaceRegistry(latest, registryOffset, idWidth, existing.Length, keptIds);
        if (!ReadRegistry(newLatest, walker, formats, idWidth, out _).SequenceEqual(keptIds))
            throw Layout();

        byte[] newTable = new byte[table.Length - removedIds.Count * rowWidth];
        table.AsSpan(0, ElemTableLayout.HeaderLength).CopyTo(newTable);
        BinaryPrimitives.WriteInt32LittleEndian(newTable.AsSpan(sizeof(ushort)), rows.Count - removedIds.Count);
        int output = ElemTableLayout.HeaderLength;
        for (int i = 0; i < rows.Count; i++)
        {
            if (removedIds.Contains(rows[i].Id))
                continue;
            table.AsSpan(ElemTableLayout.HeaderLength + i * rowWidth, rowWidth).CopyTo(newTable.AsSpan(output));
            output += rowWidth;
        }
        // A removed warning's ID remains allocated. Copy the entire table trailer.
        table.AsSpan(rowsEnd).CopyTo(newTable.AsSpan(output));
        if (ElemTableLayout.TryParse(newTable, formats) is not { } checkedRows
            || !checkedRows.SequenceEqual(rows.Where(row => !removedIds.Contains(row.Id))))
            throw Layout();

        pending.Metadata["Global/ElemTable"] = PagedStreamCodec.Reencode(tableStream, newTable);
        pending.Metadata["Global/Latest"] = PagedStreamCodec.Reencode(latestStream, newLatest);
        foreach (var entry in removed.Values)
        {
            if (pending.AddedProjectOwners.RemoveAll(owner => owner.Partition == entry.Partition && owner.Owner == entry.Owner) == 0)
                pending.RemovedProjectOwners.Add(entry);
        }
    }

    private static byte[] ReadStored(RootStorage root, ParameterWriter.Pending pending, string name)
    {
        if (pending.Metadata.TryGetValue("Global/" + name, out byte[]? staged))
            return staged;
        if (!root.TryOpenStorage("Global", out var global) || !global.TryOpenStream(name, out var stream))
            throw Layout();
        using (stream)
        {
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }

    private static WideBlockWalker WarningWalker(SchemaDocument schema) => new(schema, (type, field) =>
        type.Name is "ADocWarnings" or "WallRectOpeningWarning" or "ElementParents" or "Element"
            ? WideBlockWalker.AllItemsKeyPrefix + field.Name : null, []);

    private static long[] ReadRegistry(byte[] body, WideBlockWalker walker, FormatsSchema formats, int idWidth, out int offset)
    {
        ushort documentTag = Tag(formats, "ADocument");
        if (!HasTag(body, documentTag))
            throw Layout();
        // Global/Latest ends with a four-byte trailer outside the object archive.
        var walk = walker.Run(body, 0, body.Length - sizeof(int), documentTag);
        if (!walk.Exact || walk.Queue.Count(tag => tag == Tag(formats, "ADocWarnings")) != 1
            || walk.Captures.Where(c => c.Key == WideBlockWalker.AllItemsKeyPrefix + "m_warningIds#").ToArray() is not [var list])
            throw Layout();

        offset = list.ValueOffset;
        int first = offset + sizeof(int);
        var items = walk.Captures.Where(c => c.Field == "m_warningIds" && c.Key.Contains('@')).ToArray();
        if (list.Integer != items.Length || offset < sizeof(ushort)
            || items.Where((item, i) => item.ValueOffset != first + i * idWidth || item.Integer <= 0).Any()
            || items.Select(item => item.Integer).Distinct().Count() != items.Length)
            throw Layout();
        return items.Select(item => item.Integer).ToArray();
    }

    private static byte[] ReplaceRegistry(byte[] body, int offset, int idWidth, int oldCount, IReadOnlyList<long> ids)
    {
        byte[] registry = Encode(writer =>
        {
            writer.Write(ids.Count);
            foreach (long id in ids)
            {
                if (idWidth == sizeof(int)) writer.Write(checked((int)id));
                else writer.Write(id);
            }
        });
        return ParameterWriter.ReplaceValue(body, offset, sizeof(int) + idWidth * oldCount, registry);
    }

    private static long ReadIdentifierCounter(ReadOnlySpan<byte> tail, FormatsSchema formats, int idWidth, out int offset)
    {
        offset = idWidth == sizeof(int) ? 11 : 12;
        if (formats.Get("IdentifierSource") is not { Fields: [{ Name: "m_last", Type: 14, Mods: 0 } identifier] }
            || identifier.Ref != Tag(formats, "Identifier")
            || tail.Length != offset + idWidth + sizeof(int)
            || BinaryPrimitives.ReadUInt32LittleEndian(tail) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]) != uint.MaxValue
            || BinaryPrimitives.ReadUInt16LittleEndian(tail[8..]) != Tag(formats, "IdentifierSource")
            || tail[10..offset].IndexOfAnyExcept((byte)0) >= 0
            || tail[(offset + idWidth)..].IndexOfAnyExcept((byte)0) >= 0)
            throw Layout();
        return idWidth == sizeof(int) ? BinaryPrimitives.ReadInt32LittleEndian(tail[offset..])
            : BinaryPrimitives.ReadInt64LittleEndian(tail[offset..]);
    }

    private static bool HasTag(byte[] body, int tag) => body.Length >= sizeof(ushort)
        && BinaryPrimitives.ReadUInt16LittleEndian(body) == tag;

    private static ushort Tag(FormatsSchema formats, string name) =>
        formats.IdOf(name) is > 0 and <= ushort.MaxValue and var id ? (ushort)id : throw Layout();

    private static byte[] Encode(Action<BinaryWriter> write)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        write(writer);
        return output.ToArray();
    }

    private static RvtWriteRefusedException Layout() => ParameterWriter.Unverified("The wall opening warning layout could not be verified.");
}
