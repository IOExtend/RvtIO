namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record WallHistoryEntry(int Id, IReadOnlyList<int> Keys, int Start, int End);
    internal sealed record WallHistoryTable(string Name, uint Count, int Start, int End,
        IReadOnlyList<WallHistoryEntry> Entries);
    internal sealed record WallGeometryStepPrefix(int QueueIndex, ushort ClassId, uint Handle,
        int Start, int End, IReadOnlyList<WallHistoryTable> Tables,
        int Id, int Version, int Flags, uint OwnerReference);
    internal sealed record OwnWallGeometryHistories(OwnWallGeometryPhases List,
        IReadOnlyList<WallPhaseDeferredObject> PriorObjects,
        IReadOnlyList<WallGeometryStepPrefix> Steps, int TargetQueueIndex,
        int End, bool Complete, string? Failure, IReadOnlyList<WallSimpleOwnTail> SimpleOwnTails,
        IReadOnlyList<WallLayerExtensionOwnTail> LayerExtensionOwnTails,
        IReadOnlyList<WallSweepIdentityOwnTail> SweepIdentityOwnTails,
        IReadOnlyList<WallMemberGeometryOwnTail> MemberGeometryOwnTails,
        WallPhasePointer GeometryTablePointer, IReadOnlyList<WallOwnGeometryTable> GeometryTables);

    /// <summary>Own FIFO through the GeomStep base prefixes in phases 1..5. Keeps the counted GeomGenerator tables,
    /// including all rows, order, duplicates and null sentinels. A later unconfirmed body stops the walk and leaves an
    /// explicit incomplete result. The own tail of the last step, snapshots, phase 6, post-read canonicalization,
    /// active-face resolution and the extrusion predicate are outside this scope.</summary>
    internal static OwnWallGeometryHistories? ExtractOwnWallGeometryHistories(
        byte[] data, int start, uint size, ushort classId, out string? failure)
        => ExtractOwnWallGeometryHistoriesCore(data, start, size, classId, false,
            out failure, out _, out _, out _, out _, out _);

    private static OwnWallGeometryHistories? ExtractOwnWallGeometryHistoriesCore(
        byte[] data, int start, uint size, ushort classId, bool sixPhases, out string? failure,
        out List<WallTweakOwnTail> tweakTails, out List<WallGridOwnTail> gridTails,
        out List<WallCutoutSketchOwnTail> cutoutTails,
        out Cursor? continuation, out List<(ushort Cls, uint Id)>? deferred)
    {
        continuation = null; deferred = null;
        tweakTails = new(); gridTails = new(); cutoutTails = new();
        var list = ExtractOwnWallGeometryPhasesCore(data, start, size, classId,
            out failure, out var cursor, out var queue, out var geometryTablePointer);
        if (list == null) return null;
        var prior = new List<WallPhaseDeferredObject>();
        var steps = new List<WallGeometryStepPrefix>();
        var simpleTails = new List<WallSimpleOwnTail>();
        var layerTails = new List<WallLayerExtensionOwnTail>();
        var sweepTails = new List<WallSweepIdentityOwnTail>();
        var memberTails = new List<WallMemberGeometryOwnTail>();
        var geometryTables = new List<WallOwnGeometryTable>();
        int target = list.ListPointer.QueueIndex!.Value;
        if (!HasCountedWallHistorySchema())
        { failure = "history-schema"; return Result(failure); }
        foreach (var phase in list.Phases.Take(sixPhases ? 6 : 5))
            foreach (var pointer in phase.Members)
            {
                if (pointer is not { Handle: uint.MaxValue, QueueIndex: int index, ClassId: ushort cls }
                    || !IsSubclassOf(cls, 96))
                { failure = $"phase-pointer:{phase.Name}:{pointer.Start}"; return Result(failure); }
                target = Math.Max(target, index);
            }
        try
        {
            for (int index = list.ListPointer.QueueIndex.Value + 1; index <= target; index++)
            {
                var (cls, handle) = queue![index];
                int objectStart = cursor!.Pos;
                failure = $"fifo:{index}:{cls}";
                if (IsSubclassOf(cls, 96))
                {
                    var tables = new List<WallHistoryTable>();
                    string[] names = ["m_faceHistTable", "m_curveHistTableSet",
                        "m_edgeHistTable", "m_edgeHistTableReverse"];
                    int[] keys = [3, 5, 4, 4];
                    for (int table = 0; table < names.Length; table++)
                    {
                        failure = $"fifo:{index}:{cls}:{names[table]}";
                        int tableStart = cursor.Pos;
                        uint count = ReadU32(cursor);
                        var entries = new List<WallHistoryEntry>();
                        if (count != uint.MaxValue)
                        {
                            int width = (keys[table] + 1) * 4;
                            if (count > 100_000 || count > (cursor.End - cursor.Pos) / width) throw Fail;
                            for (uint row = 0; row < count; row++)
                            {
                                int entryStart = cursor.Pos;
                                int id = ReadI32(cursor);
                                var values = new int[keys[table]];
                                for (int key = 0; key < values.Length; key++) values[key] = ReadI32(cursor);
                                entries.Add(new(id, values, entryStart - start, cursor.Pos - start));
                            }
                        }
                        tables.Add(new(names[table], count, tableStart - start, cursor.Pos - start, entries));
                    }
                    failure = $"fifo:{index}:{cls}:GeomStep-base";
                    int generatorId = ReadI32(cursor), version = ReadI32(cursor), flags = ReadI32(cursor);
                    uint ownerReference = ReadU32(cursor);
                    steps.Add(new(index, cls, handle, objectStart - start, cursor.Pos - start,
                        tables, generatorId, version, flags, ownerReference));
                    // A prefix needs no guess about the versioned fields of the last class.
                    if (index == target && !sixPhases) break;
                    failure = $"step-tail:{index}:{cls}";
                    var fields = SerializationFields(cls) ?? throw Fail;
                    if (fields.Count == 8) continue; // no own persisted fields in this concrete schema
                    if (sixPhases && (cls is 4147 or 677 or 590 or 982))
                    {
                        if (!(cls == 590 ? HasWallGridOwnTailSchema()
                            : cls == 982 ? HasWallCutoutSketchOwnTailSchema() : HasWallTweakOwnTailSchema(cls)))
                        { failure += ":own-schema"; return Result(failure); }
                        var own = new Cursor { D = data, Pos = cursor.Pos, End = cursor.End };
                        if (cls == 590) gridTails.Add(ReadWallGridOwnTail(own, start, index));
                        else if (cls == 982) cutoutTails.Add(ReadWallCutoutSketchOwnTail(own, start, index));
                        else tweakTails.Add(ReadWallTweakOwnTail(own, cls, start, index));
                        cursor.Pos = own.Pos;
                        continue;
                    }
                    if (cls is 500 or 4165)
                    {
                        if (!HasSimpleWallOwnTailSchema(cls))
                        { failure += ":own-schema"; return Result(failure); }
                        // Atomic continuation: malformed own fields leave the completed
                        // base prefix boundary intact, with no partial tail promoted.
                        var own = new Cursor { D = data, Pos = cursor.Pos, End = cursor.End };
                        var value = ReadSimpleWallOwnTail(own, cls, queue, start, index);
                        simpleTails.Add(value);
                        cursor.Pos = own.Pos;
                        continue;
                    }
                    if (cls is 150 or 4098 or 4166 or 4167)
                    {
                        if (!HasDerivedWallOwnTailSchema(cls))
                        { failure += ":own-schema"; return Result(failure); }
                        var own = new Cursor { D = data, Pos = cursor.Pos, End = cursor.End };
                        if (cls == 150) memberTails.Add(ReadMemberGeometryOwnTail(own, start, index));
                        else if (cls == 4098) layerTails.Add(ReadLayerExtensionOwnTail(own, start, index));
                        else sweepTails.Add(ReadSweepIdentityOwnTail(own, cls, start, index));
                        cursor.Pos = own.Pos;
                        continue;
                    }
                    var tail = new Cursor { D = data, Pos = objectStart, End = cursor.End };
                    if (!TryReadExactWallGeomStep(tail, cls, out _, out _, out string? why, out _))
                    { failure += $":{why}"; return Result(failure); }
                    if (tail.Pos < cursor.Pos) throw Fail;
                    cursor.Pos = tail.Pos;
                }
                else
                {
                    if (cls == 2087)
                    {
                        failure = $"geom-table:{index}:schema";
                        if (!HasOwnWallGeometryTableSchema()) return Result(failure);
                        var own = new Cursor { D = data, Pos = cursor.Pos, End = cursor.End };
                        var ownQueue = new List<(ushort, uint)>(queue);
                        failure = $"geom-table:{index}:fields";
                        var table = ReadOwnWallGeometryTable(own, ownQueue, start, index, handle);
                        geometryTables.Add(table);
                        cursor.Pos = own.Pos;
                        queue.AddRange(ownQueue.Skip(queue.Count));
                        prior.Add(new(index, cls, handle, objectStart - start, cursor.Pos - start));
                        continue;
                    }
                    // The Graphics GNode descriptor uses an inline GInfo (four 32-bit fields),
                    // not a polymorphic pointer. This correction stays local to this walk.
                    foreach (var field in SerializationFields(cls) ?? throw Fail)
                        if (field is { Declaring: 1246, Name: "m_GInfo", Type: 14, Mods: 0, Ref: null })
                        { if (!cursor.Take(16)) throw Fail; }
                        else ReadField(cursor, field, queue, 1, null);
                    prior.Add(new(index, cls, handle, objectStart - start, cursor.Pos - start));
                }
                if (queue.Count > 100_000) throw Fail;
            }
            failure = null;
        }
        catch (DecodeFail) { failure ??= "fifo"; }
        if (failure == null) { continuation = cursor; deferred = queue; }
        return Result(failure);

        OwnWallGeometryHistories Result(string? why) => new(list, prior, steps, target,
            cursor!.Pos - start, why == null, why, simpleTails, layerTails, sweepTails, memberTails,
            geometryTablePointer!, geometryTables);
    }

    private static bool HasCountedWallHistorySchema()
    {
        var fields = SerializationFields(96);
        if (fields is not { Count: 8 }) return false;
        string[] tables = ["m_faceHistTable", "m_curveHistTableSet", "m_edgeHistTable", "m_edgeHistTableReverse"];
        for (int index = 0; index < tables.Length; index++)
            if (fields[index] is not { Declaring: 97, Type: 14, Mods: 80, Cnt: null }
                || fields[index].Name != tables[index] || fields[index].Ref != (index == 3 ? 102 : null))
                return false;
        if (fields[4] is not { Declaring: 97, Name: "m_id", Type: 4, Mods: 0, Ref: null, Cnt: null }
            || fields[5] is not { Declaring: 96, Name: "m_version", Type: 4, Mods: 0, Ref: null, Cnt: null }
            || fields[6] is not { Declaring: 96, Name: "m_flags", Type: 4, Mods: 0, Ref: null, Cnt: null }
            || fields[7] is not { Declaring: 96, Name: "m_pElem", Type: 14, Mods: 3, Ref: null, Cnt: null })
            return false;
        foreach (var (entry, history, name, count) in new[]
            { (98, 99, "m_faceHist", 3), (100, 101, "m_curveHist", 5), (102, 103, "m_edgeHist", 4) })
        {
            var row = SerializationFields(entry);
            var values = SerializationFields(history);
            if (row is not { Count: 2 } || row[0] is not { Name: "m_id", Type: 4, Mods: 0, Ref: null, Cnt: null }
                || row[0].Declaring != entry || row[1] is not { Type: 14, Mods: 0, Ref: null, Cnt: null }
                || row[1].Declaring != entry || row[1].Name != name || values is not { Count: 1 }
                || values[0] is not { Name: "m_keys", Type: 4, Mods: 16 }
                || values[0].Declaring != history || values[0].Cnt != count || values[0].Ref != entry)
                return false;
        }
        return true;
    }
}
