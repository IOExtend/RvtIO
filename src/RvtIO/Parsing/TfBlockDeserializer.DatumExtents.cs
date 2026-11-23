namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record DatumLeader(double[] End, double[] Elbow, double[] Anchor);
    internal sealed record DatumExtent(double FreeEnd, double BubbleEnd, byte Overrides, DatumLeader? BubbleLeader,
        double? FreeEnd3d = null, double? BubbleEnd3d = null, DatumLeader? FreeEndLeader = null);
    internal sealed record ViewDatumExtents(int ViewId, double[] Normal, Dictionary<int, DatumExtent> Datums,
        IReadOnlyDictionary<int, SectionTraceExtent>? Sections = null);
    internal sealed record SectionTraceExtent(double Start, double End, int SegmentCount);
    internal sealed record DatumHead(int SymbolId, int GridBubblePosition, double? EndSegmentLength = null,
        bool? BubbleAtEnd1 = null, bool? BubbleAtEnd2 = null);

    internal static DatumHead? ExtractDatumHead(byte[] data, int start, uint size, ushort classId)
    {
        if (classId is not (2109 or 2301) || start < 0 || size > int.MaxValue
            || (long)start + 16 + size > data.Length || size < 6
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId) return null;
        int end = start + 12 + (int)size;
        if (BitConverter.ToUInt32(data, end) != size) return null;
        var fields = SerializationFields(classId);
        if (fields == null || !HasExactSchemaField(fields, classId, "m_familyTagId", 14, 0, 20)) return null;
        var cursor = new Cursor { D = data, Pos = start + 14, End = end, RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        int owner = -1, symbol = -1, position = -1;
        double? endLength = null;
        bool? end1 = null, end2 = null;
        try
        {
            foreach (var field in fields)
            {
                if (field is { Declaring: 37, Name: "m_id", Type: 14, Mods: 0, Ref: 20 }) owner = ReadI32(cursor);
                else if (field.Declaring == classId && field is { Name: "m_familyTagId", Type: 14, Mods: 0, Ref: 20 })
                    symbol = ReadI32(cursor);
                else if (field is { Declaring: 2109, Name: "m_bubbleLocInElev", Type: 4, Mods: 0 })
                    position = ReadI32(cursor);
                else if (field is { Declaring: 2109, Name: "m_endSegmentsLength", Type: 7, Mods: 0 })
                    endLength = ReadF64(cursor);
                else if (field is { Declaring: 2109, Name: "m_bubbleAtEnd1" or "m_bubbleAtEnd2", Type: 1, Mods: 0 })
                {
                    if (!TryReadFlag(cursor, field, out bool visible)) throw Fail;
                    if (field.Name == "m_bubbleAtEnd1") end1 = visible;
                    else end2 = visible;
                }
                else ReadField(cursor, field, queue, 0, null);
            }
        }
        catch (DecodeFail) { return null; }
        return owner > 0 && (uint)owner == BitConverter.ToUInt32(data, start) && symbol > 0
            ? new DatumHead(symbol, position, endLength, end1, end2) : null;
    }

    internal static ViewDatumExtents? ExtractViewDatumExtents(byte[] data, int start, uint size, ushort classId)
    {
        const ushort extentElement = 1471, datumRecord = 1054, leaderRecord = 622, sectionRecord = 886;
        if (classId != extentElement || start < 0 || size > int.MaxValue
            || (long)start + 16 + size > data.Length || size < 6
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId) return null;
        int end = start + 12 + (int)size;
        if (BitConverter.ToUInt32(data, end) != size) return null;

        var fields = SerializationFields(classId);
        if (fields == null || !HasExactSchemaField(fields, extentElement, "m_idExtents", 14, 80, null)
            || !HasExactSchemaField(fields, extentElement, "m_dbViewId", 14, 0, 20)
            || !HasExactSchemaField(fields, 37, "m_id", 14, 0, 20)) return null;
        var cursor = new Cursor { D = data, Pos = start + 14, End = end, RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        var datumIndices = new Dictionary<int, int>();
        var sectionIndices = new Dictionary<int, int>();
        var sections = new Dictionary<int, SectionTraceExtent>();
        var leaders = new Dictionary<int, (int DatumId, bool FreeEnd)>();
        var records = new Dictionary<int, DatumExtent>();
        double[]? normal = null;
        int viewId = -1, owner = -1;
        try
        {
            foreach (var field in fields)
            {
                if (field is { Declaring: 37, Name: "m_id" }) owner = ReadI32(cursor);
                else if (field is { Declaring: extentElement, Name: "m_dbViewId" }) viewId = ReadI32(cursor);
                else if (field is { Declaring: extentElement, Name: "m_dbViewNorm", Type: 7, Mods: 16, Cnt: 3 })
                    normal = ReadPoint();
                else if (field is { Declaring: extentElement, Name: "m_idExtents" })
                {
                    uint count = ReadU32(cursor);
                    if (count > 10_000) return null;
                    for (int i = 0; i < count; i++)
                    {
                        int datumId = ReadI32(cursor);
                        int index = queue.Count;
                        ReadPoly(cursor, queue);
                        if (queue.Count != index + 1) continue;
                        if (queue[index].Cls == datumRecord) datumIndices.Add(index, datumId);
                        if (queue[index].Cls == sectionRecord) sectionIndices.Add(index, datumId);
                    }
                }
                else ReadField(cursor, field, queue, 0, null);
            }
            if (owner <= 0 || (uint)owner != BitConverter.ToUInt32(data, start)
                || viewId <= 0 || normal == null || !normal.All(double.IsFinite)
                || (datumIndices.Count == 0 && sectionIndices.Count == 0)) return null;

            for (int i = 0; i < queue.Count; i++)
            {
                if (datumIndices.TryGetValue(i, out int id))
                {
                    double free = double.NaN, bubble = double.NaN;
                    double? free3d = null, bubble3d = null;
                    byte overrides = 0;
                    int recordViewId = -1;
                    bool supported = true;
                    foreach (var field in SerializationFields(datumRecord) ?? throw Fail)
                    {
                        if (field is { Name: "m_freeEnd", Type: 7, Mods: 0 }) free = ReadF64(cursor);
                        else if (field is { Name: "m_bubbleEnd", Type: 7, Mods: 0 }) bubble = ReadF64(cursor);
                        else if (field is { Name: "m_freeEnd3d", Type: 7, Mods: 0 }) free3d = ReadF64(cursor);
                        else if (field is { Name: "m_bubbleEnd3d", Type: 7, Mods: 0 }) bubble3d = ReadF64(cursor);
                        else if (field is { Name: "m_dbViewId", Type: 14, Mods: 0, Ref: 20 }) recordViewId = ReadI32(cursor);
                        else if (field is { Name: "m_endBubbleOverrides", Type: 2, Mods: 0 })
                        {
                            if (!cursor.Take(1)) throw Fail;
                            overrides = data[cursor.Pos - 1];
                        }
                        else if (field is { Name: "m_pLeader" or "m_pFreeEndLeader" })
                        {
                            int index = queue.Count;
                            uint pointer = ReadPoly(cursor, queue);
                            if (pointer == 0) continue;
                            if (queue.Count == index + 1 && queue[index].Cls == leaderRecord)
                                leaders.Add(index, (id, field.Name == "m_pFreeEndLeader"));
                            else supported = false;
                        }
                        else ReadField(cursor, field, queue, 1, null);
                    }
                    if (supported && id > 0 && (recordViewId == -1 || recordViewId == viewId)
                        && double.IsFinite(free) && double.IsFinite(bubble)
                        && !records.TryAdd(id, new DatumExtent(free, bubble, overrides, null, free3d, bubble3d))) return null;
                }
                else if (leaders.TryGetValue(i, out var ownership))
                {
                    double[]? tip = null, elbow = null, anchor = null;
                    foreach (var field in SerializationFields(leaderRecord) ?? throw Fail)
                    {
                        if (field is { Name: "m_end", Type: 7, Mods: 16, Cnt: 3 }) tip = ReadPoint();
                        else if (field is { Name: "m_elbow", Type: 7, Mods: 16, Cnt: 3 }) elbow = ReadPoint();
                        else if (field is { Name: "m_anchor", Type: 7, Mods: 16, Cnt: 3 }) anchor = ReadPoint();
                        else ReadField(cursor, field, queue, 1, null);
                    }
                    if (tip == null || elbow == null || anchor == null
                        || !tip.All(double.IsFinite) || !elbow.All(double.IsFinite) || !anchor.All(double.IsFinite)) return null;
                    if (records.TryGetValue(ownership.DatumId, out var record))
                    {
                        var leader = new DatumLeader(tip, elbow, anchor);
                        records[ownership.DatumId] = ownership.FreeEnd
                            ? record with { FreeEndLeader = leader } : record with { BubbleLeader = leader };
                    }
                }
                else if (queue[i].Cls == sectionRecord)
                {
                    uint segments = 0;
                    bool continuous = true, hideLine = true;
                    double startExtent = double.NaN, endExtent = double.NaN;
                    double startUncropped = double.NaN, endUncropped = double.NaN;
                    int head = 0, tail = 0;
                    foreach (var field in SerializationFields(sectionRecord) ?? throw Fail)
                    {
                        if (field is { Name: "m_endVisLengths", Type: 13, Mods: 80 })
                        {
                            segments = ReadU32(cursor);
                            if (segments == uint.MaxValue) segments = 0;
                            if (segments > 10_000) throw Fail;
                            for (int segment = 0; segment < segments; segment++)
                            {
                                double first = ReadF64(cursor), last = ReadF64(cursor);
                                continuous &= first == -1 && last == -1;
                            }
                        }
                        else if (field is { Name: "m_horzExtents", Type: 7, Mods: 16, Cnt: 2 })
                        {
                            startUncropped = ReadF64(cursor);
                            endUncropped = ReadF64(cursor);
                        }
                        else if (field is { Name: "m_croppedHorzExtents", Type: 7, Mods: 16, Cnt: 2 })
                        {
                            startExtent = ReadF64(cursor);
                            endExtent = ReadF64(cursor);
                        }
                        else if (field is { Name: "m_end1Mark", Type: 4, Mods: 0 }) head = ReadI32(cursor);
                        else if (field is { Name: "m_end2Mark", Type: 4, Mods: 0 }) tail = ReadI32(cursor);
                        else if (field is { Name: "m_hideLine", Type: 1, Mods: 0 })
                        {
                            if (!TryReadFlag(cursor, field, out hideLine)) throw Fail;
                        }
                        else ReadField(cursor, field, queue, 1, null);
                    }
                    // A straight trace can supply only the first cropped endpoint. Use its saved uncut
                    // end when the second cropped coordinate is zero and the two start coordinates agree.
                    if (segments == 1 && startExtent == startUncropped && endExtent == 0 && startExtent >= endExtent)
                        endExtent = endUncropped;
                    // Continuous traces with the head on the first leg and the tail on the last.
                    if (sectionIndices.TryGetValue(i, out int sectionId) && sectionId > 0
                        && continuous && !hideLine && head == 1 && tail == 2 && segments > 0
                        && double.IsFinite(startExtent) && double.IsFinite(endExtent) && startExtent < endExtent
                        && !sections.TryAdd(sectionId, new SectionTraceExtent(startExtent, endExtent, (int)segments)))
                        return null;
                }
                else SkipQueuedObjects(cursor, queue, i + 1, i);
            }
        }
        catch (DecodeFail) { return null; }
        return cursor.Pos == end && (records.Count > 0 || sections.Count > 0)
            ? new ViewDatumExtents(viewId, normal, records, sections) : null;

        double[] ReadPoint() => [ReadF64(cursor), ReadF64(cursor), ReadF64(cursor)];
    }
}
