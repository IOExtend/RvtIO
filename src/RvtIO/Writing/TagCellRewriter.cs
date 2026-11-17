using System.Buffers.Binary;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>
/// Moves the tagged-entities cell of an <c>IndependentTag</c> tag from its 2027 place to the
/// 2023 place. In 2027 the tag owns a <c>CellList</c> (<c>Element.m_cellList</c>) whose
/// <c>TagReferencesWithLeaderCell</c> holds the references as <c>TagReferenceWithLeader</c>
/// objects; in 2023 the references are <c>TaggedElem</c> objects of a <c>TaggedEntitiesCell</c>
/// stored inline in <c>IndependentTag.m_taggedEntitiesCell</c> and the cell list is null. The
/// rewriter builds that inline cell, converts the references with the tag class map, drops the
/// cell list from the queue and puts the converted tagged elements before the orientation cell.
/// </summary>
internal static class TagCellRewriter
{
    internal static Func<byte[], byte[], byte[]?>? Create(
        SchemaDocument sourceDoc, SchemaDocument targetDoc, IReadOnlyDictionary<Guid, SchemaField[]?>? entitySchemas)
    {
        var target = new FormatsSchema(targetDoc);
        if (target.Get("IndependentTag") is not { } tag
            || tag.Fields.All(f => f.Name != "m_taggedEntitiesCell")
            || target.Get("TaggedElem") is not { } taggedElem)
            return null;
        if (sourceDoc.Classes.All(c => c.Name != "TagReferencesWithLeaderCell")) return null;
        var transcoder = new R22Transcoder(sourceDoc, targetDoc, 8, entitySchemas, tagMapping: true);
        return (source, generic) => Rewrite(source, generic, sourceDoc, targetDoc, transcoder, (ushort)taggedElem.Id);
    }

    private static byte[]? Fail(string reason) => null;

    private static byte[]? Rewrite(
        byte[] source, byte[] generic, SchemaDocument sourceDoc, SchemaDocument targetDoc, R22Transcoder transcoder, ushort taggedElemClass)
    {
        if (source.Length < 2 || generic.Length < 2) return Fail("short");
        int rootClass = BinaryPrimitives.ReadUInt16LittleEndian(source);
        var sourceWalker = new WideBlockWalker(sourceDoc, (d, f) => d.Name + "." + f.Name, []);
        if (!sourceWalker.IsA(rootClass, "IndependentTag")) return Fail("not a tag");
        var sourceWalk = sourceWalker.Run(source, 0, source.Length, rootClass);
        if (!sourceWalk.RootRead) return Fail("tag walk");

        int cellAt = -1;
        var referenceAt = new List<int>();
        for (int i = 0; i < sourceWalk.Queue.Count; i++)
        {
            string? name = sourceWalker.Class(sourceWalk.Queue[i])?.Name;
            if (name == "TagReferencesWithLeaderCell") cellAt = i;
            else if (name == "TagReferenceWithLeader") referenceAt.Add(i);
        }
        if (cellAt < 0) return Fail("no tagged cell");
        if (referenceAt.Count == 0) return Fail("no references");

        static long Value(WideBlockWalker.Walk walk, string key, long fallback) =>
            walk.Captures.Where(c => c.Key == key && c.Kind == WideBlockWalker.CaptureKind.Integer)
                .Select(c => c.Integer).FirstOrDefault(fallback);

        if (Value(sourceWalk, "TagReferencesWithLeaderCell.m_targetToCategoryForAutoTagging#", 0) != 0)
            return Fail("category map not empty");
        if (Value(sourceWalk, "TagReferencesWithLeaderCell.m_leaderEndCondition", 0) != 0)
            return Fail("leader end condition");
        uint leadersMode = (uint)Value(sourceWalk, "TagReferencesWithLeaderCell.m_leadersPresentationMode", 0);
        byte mergeElbows = (byte)Value(sourceWalk, "TagReferencesWithLeaderCell.m_mergeElbows", 0);

        // the references, converted to the 2023 TaggedElem layout
        var children = new List<byte[]>();
        var starts = sourceWalk.QueueStart;
        foreach (int qi in referenceAt)
        {
            int start = starts[qi];
            int next = int.MaxValue;
            foreach (int other in starts) if (other > start && other < next) next = other;
            int end = next == int.MaxValue ? source.Length : next;
            if (start < 2 || start >= end || end > source.Length) return Fail("reference range");
            var payload = new byte[2 + (end - start)];
            BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)sourceWalk.Queue[qi]);
            Buffer.BlockCopy(source, start, payload, 2, end - start);
            var converted = transcoder.Payload(payload, 0, payload.Length, out string? failure);
            if (converted is null) return Fail("TaggedElem: " + failure);
            children.Add(converted);
        }

        // the inline TaggedEntitiesCell
        using var cell = new MemoryStream();
        WriteU32(cell, (uint)children.Count);
        foreach (var _ in children)
        {
            WriteU32(cell, uint.MaxValue);
            WriteU16(cell, taggedElemClass);
        }
        WriteU32(cell, 0);
        WriteU32(cell, leadersMode);
        cell.WriteByte(mergeElbows);
        byte[] cellBytes = cell.ToArray();

        int targetRoot = BinaryPrimitives.ReadUInt16LittleEndian(generic);
        var targetWalker = new WideBlockWalker(targetDoc, (_, _) => null, []);
        var targetWalk = targetWalker.Run(generic, 0, generic.Length, targetRoot);
        int genericRootEnd = targetWalk.QueueStart.Count > 0 ? targetWalk.QueueStart[0] : generic.Length;
        var cellSpan = PayloadSpan.Find(generic, 2, genericRootEnd, targetDoc, targetRoot, "m_taggedEntitiesCell");
        var cellListSpan = PayloadSpan.Find(generic, 2, genericRootEnd, targetDoc, targetRoot, "m_cellList");
        if (cellSpan is null) return Fail("no inline cell field");
        if (cellListSpan is null) return Fail("no cell list field");

        // generic root with the inline cell and a null cell-list pointer
        var root = new MemoryStream(genericRootEnd + cellBytes.Length);
        int at = 0;
        foreach (var (start, end, replacement) in new[]
        {
            (cellSpan.Value.Start, cellSpan.Value.End, cellBytes),
            (cellListSpan.Value.Start, cellListSpan.Value.End, new byte[4]),
        }.OrderBy(x => x.Start))
        {
            if (start < at || end < start || end > genericRootEnd) return Fail("overlapping fields");
            root.Write(generic, at, start - at);
            root.Write(replacement);
            at = end;
        }
        root.Write(generic, at, genericRootEnd - at);

        // queue: the generic objects without the cell list, with the converted tagged elements
        // inserted where the inline cell queues them (before the tag orientation cell, the next pointer field)
        using var queue = new MemoryStream();
        int genericQueue = genericRootEnd;
        var classNames = targetDoc.ById;
        int cellListIndex = targetWalk.Queue.FindIndex(t => classNames.GetValueOrDefault(t)?.Name == "CellList");
        int cellListStart = cellListIndex >= 0 ? targetWalk.QueueStart[cellListIndex] : -1;
        int cellListEnd = cellListIndex >= 0
            ? (cellListIndex + 1 < targetWalk.QueueStart.Count ? targetWalk.QueueStart[cellListIndex + 1] : generic.Length)
            : -1;
        int orientationIndex = targetWalk.Queue.FindIndex(t => classNames.GetValueOrDefault(t)?.Name == "TagOrientationCell");
        int insertAt = orientationIndex >= 0 ? targetWalk.QueueStart[orientationIndex] : generic.Length;

        void Copy(int from, int to)
        {
            if (to > from) queue.Write(generic, from, to - from);
        }

        if (cellListIndex < 0)
        {
            Copy(genericQueue, insertAt);
            foreach (var child in children) queue.Write(child, 2, child.Length - 2);
            Copy(insertAt, generic.Length);
        }
        else if (cellListStart < insertAt)
        {
            Copy(genericQueue, cellListStart);
            Copy(cellListEnd, insertAt);
            foreach (var child in children) queue.Write(child, 2, child.Length - 2);
            Copy(insertAt, generic.Length);
        }
        else
        {
            Copy(genericQueue, insertAt);
            foreach (var child in children) queue.Write(child, 2, child.Length - 2);
            Copy(insertAt, cellListStart);
            Copy(cellListEnd, generic.Length);
        }

        byte[] result = [.. root.ToArray(), .. queue.ToArray()];
        var check = new WideBlockWalker(targetDoc, (_, _) => null, []).Run(result, 0, result.Length, targetRoot);
        if (!check.Exact || check.Error != null) return Fail("result walk: " + check.Error);
        return result;
    }

    private static void WriteU32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }

    private static void WriteU16(Stream s, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        s.Write(b);
    }
}

/// <summary>Byte spans of a named field at the root of a class object, by the class grammar.</summary>
internal static class PayloadSpan
{
    internal static List<(string Name, int Start, int End)> Fields(byte[] data, int start, int end, SchemaDocument doc, int classId)
    {
        var classes = doc.ById;
        var list = new List<(string, int, int)>();
        int at = start;
        foreach (var c in Chain(classes, classId))
        {
            foreach (var f in c.Fields.Where(f => f.Stored))
            {
                int from = at;
                at = SkipField(data, at, end, classes, f);
                if (at < 0 || at > end) return list;
                list.Add((f.Name, from, at));
            }
        }
        return list;
    }

    internal static (int Start, int End)? Find(byte[] data, int start, int end, SchemaDocument doc, int classId, string field)
    {
        var classes = doc.ById;
        int at = start;
        foreach (var c in Chain(classes, classId))
        {
            foreach (var f in c.Fields.Where(f => f.Stored))
            {
                int from = at;
                at = SkipField(data, at, end, classes, f);
                if (at < 0 || at > end) return null;
                if (f.Name == field) return (from, at);
            }
        }
        return null;
    }

    private static List<SchemaClass> Chain(IReadOnlyDictionary<int, SchemaClass> classes, int classId)
    {
        var order = new List<SchemaClass>();
        for (int id = classId, guard = 0; id != 0 && guard < 64; guard++)
        {
            if (!classes.TryGetValue(id, out var c)) break;
            order.Add(c);
            id = c.Base;
        }
        order.Reverse();
        return order;
    }

    private static int SkipField(byte[] d, int p, int end, IReadOnlyDictionary<int, SchemaClass> classes, SchemaField f)
    {
        int hi = f.Mods >> 4;
        if (hi == 6) return SkipScalar(d, p, end, classes, f);
        if (hi == 1)
        {
            for (int i = 0; i < (f.Fixed ?? 0); i++) p = SkipScalar(d, p, end, classes, f);
            return p;
        }
        if (hi == 5)
        {
            if (p + 4 > end) return -1;
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));
            if (n == 0xFFFFFFFF) n = 0;
            p += 4;
            if (f.Type is 1 or 2 && (f.Mods & 15) == 0)
            {
                if (p + (long)n > end) return -1;
                return p + (int)n;
            }
            if (n > 5_000_000) return -1;
            for (uint i = 0; i < n; i++) p = SkipScalar(d, p, end, classes, f);
            return p;
        }
        return SkipScalar(d, p, end, classes, f);
    }

    private static int SkipScalar(byte[] d, int p, int end, IReadOnlyDictionary<int, SchemaClass> classes, SchemaField f)
    {
        switch (f.Type)
        {
            case 1: case 2: return p + 1;
            case 3: return p + 2;
            case 4: case 5: case 6: return p + 4;
            case 7: case 11: return p + 8;
            case 10: return p + 2;
            case 9: return p + 16;
            case 8:
            {
                if (p + 4 > end) return -1;
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));
                if (2L * len > end - p - 4) return -1;
                return p + 4 + 2 * (int)len;
            }
            case 13:
            {
                if (f.Args is not { Count: 1 } args || args[0].Fixed is not int count) return -1;
                var item = args[0] with { Mods = (byte)(args[0].Mods & 15) };
                for (int i = 0; i < count; i++) p = SkipScalar(d, p, end, classes, item);
                return p;
            }
            case 14:
            {
                int lo = f.Mods & 15;
                if (lo == 3) return p + 4;
                if (lo is 1 or 2 or 4)
                {
                    if (p + 4 > end) return -1;
                    uint handle = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));
                    p += 4;
                    if (handle != 0)
                    {
                        if (p + 2 > end) return -1;
                        p += 2;
                    }
                    return p;
                }
                if (f.Ref is int reference)
                {
                    int at = p;
                    foreach (var c in Chain(classes, reference))
                    {
                        foreach (var nested in c.Fields.Where(x => x.Stored))
                        {
                            at = SkipField(d, at, end, classes, nested);
                            if (at < 0 || at > end) return -1;
                        }
                    }
                    return at;
                }
                if (p + 4 > end) return -1;
                uint raw = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));
                p += 4;
                if (raw == 0xFFFFFFFF)
                {
                    if (p + 2 > end) return -1;
                    p += 2;
                }
                return p;
            }
            default: return -1;
        }
    }
}
