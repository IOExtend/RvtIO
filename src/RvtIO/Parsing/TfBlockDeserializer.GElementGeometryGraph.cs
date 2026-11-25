using System.Runtime.CompilerServices;

namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record GElementGraphPointer(uint Handle, ushort? ClassId, int? QueueIndex,
        ushort? HandToken, IReadOnlyList<long> HandBits, int Start, int End);
    internal sealed record GElementGraphItem(int Start, int End, IReadOnlyList<long> Values,
        GElementGraphPointer? Pointer, IReadOnlyList<GElementGraphField> Fields);
    internal sealed record GElementGraphField(int Declaring, string Name, uint? Count, int Start, int End,
        IReadOnlyList<GElementGraphItem> Items);
    internal sealed record GElementGraphNode(int QueueIndex, ushort ClassId, uint Handle,
        int Start, int End, IReadOnlyList<GElementGraphField> Fields);
    internal sealed record GElementFaceSurfaceLink(int FaceQueueIndex, int SurfaceQueueIndex);
    internal sealed record OwnGElementGeometryGraph(int OwnerId, uint OpaqueHeader,
        IReadOnlyList<GElementGraphNode> Nodes, IReadOnlyList<GElementFaceSurfaceLink> FaceSurfaces, int End);

    private static GElementGraphField RequireUniqueGraphField(GElementGraphNode node, int declaring, string name,
        int? itemCount = null)
    {
        GElementGraphField? found = null;
        for (int i = 0; i < node.Fields.Count; i++)
        {
            var field = node.Fields[i];
            if (field.Declaring != declaring || field.Name != name) continue;
            if (found != null || itemCount is int count && field.Items.Count != count) throw Fail;
            found = field;
        }
        return found ?? throw Fail;
    }

    // Winding and nesting revisit these immutable inputs several times for the same graph.
    // The cache follows the graph instance, including for failures and copies made with "with".
    private static readonly ConditionalWeakTable<OwnGElementGeometryGraph, GElementGraphAnalysis> GElementAnalyses = new();

    private sealed class GElementGraphAnalysis(OwnGElementGeometryGraph graph)
    {
        internal Lazy<(OwnGElementFreshArchiveReferences? Value, string? Failure)> Archive { get; } = new(() =>
            (ReadOwnGElementFreshArchiveReferences(graph, out var failure), failure));

        internal Lazy<(OwnGElementFreshLoopTopology? Value, string? Failure)> Topology { get; } = new(() =>
            (ReadOwnGElementLoopTopology(graph, out var failure), failure));

        internal Lazy<(OwnGElementFreshLoopNestingInputs? Value, string? Failure)> LegacyNesting { get; } = new(() =>
            (ReadOwnGElementLoopNestingInputs(graph, 0, out var failure), failure));

        internal Lazy<(OwnGElementFreshLoopNestingInputs? Value, string? Failure)> PositiveNesting { get; } = new(() =>
            (ReadOwnGElementLoopNestingInputs(graph, 1, out var failure), failure));
    }

    private static GElementGraphAnalysis AnalyzeGElement(OwnGElementGeometryGraph graph) =>
        GElementAnalyses.GetValue(graph, static key => new(key));

    /// <summary>Complete, footer-exact FIFO for the physical node kinds of the r22 layout. Owned Face -> Surface links
    /// use queue indices, never handles, order or shape matching. Non-owning references and flags remain raw: this does
    /// not reconstruct runtime pointers, document lookup, active faces or extrusion validity.</summary>
    internal static OwnGElementGeometryGraph? ExtractOwnGElementGeometryGraph(
        byte[] data, int start, uint size, ushort classId, out string? failure)
    {
        failure = "frame";
        if (classId != 2007 || start < 0 || start > data.Length - 16 || size < 4 || size > int.MaxValue
            || (long)start + 16 + size > data.Length
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || BitConverter.ToUInt32(data, start + 12 + (int)size) != size) return null;
        failure = "owner";
        int owner = BitConverter.ToInt32(data, start);
        if (owner <= 0) return null;
        failure = "schema";
        if (!HasGElementGraphSchemas()) return null;
        int position = start + 14, end = start + 12 + (int)size, itemCount = 0;
        var queue = new List<(ushort ClassId, uint Handle)>();
        var nodes = new List<GElementGraphNode>();
        string context = "root";
        int contextIndex = 0, contextOffset = 0;
        ushort contextClass = 0;
        TfField? contextField = null;
        try
        {
            nodes.Add(Node(-1, 2007, (uint)owner));
            for (int index = 0; index < queue.Count; index++)
            {
                var entry = queue[index];
                context = "fifo";
                contextIndex = index;
                contextClass = entry.ClassId;
                if (entry.ClassId is not (2007 or 2015 or 2009 or 2092 or 1996 or 1589 or 1243 or 1254
                    or 2014 or 522 or 1850 or 1848 or 1736 or 1999 or 1257 or 1974 or 983 or 1991 or 2238))
                    throw Fail;
                nodes.Add(Node(index, entry.ClassId, entry.Handle));
            }
            context = "tail";
            if (position != end) throw Fail;
            var surfaces = new List<GElementFaceSurfaceLink>();
            // A missing, external or hand pointer stays as stored in its field.
            // Only owned links to admitted surface kinds are resolved here.
            foreach (var node in nodes.Where(n => n.ClassId == 1589))
            {
                var pointer = node.Fields.Single(f => f.Declaring == 1589 && f.Name == "m_pSurf")
                    .Items.Single().Pointer!;
                if (pointer.QueueIndex is not int surface) continue;
                context = "face-surface";
                contextIndex = node.QueueIndex;
                contextOffset = surface;
                if (nodes[surface + 1].ClassId is not (522 or 983)) throw Fail;
                surfaces.Add(new(node.QueueIndex, surface));
            }
            failure = null;
            return new(owner, BitConverter.ToUInt32(data, start + 4), nodes, surfaces, position - start);
        }
        catch (DecodeFail)
        {
            failure = context switch
            {
                "fifo" => $"fifo:{contextIndex}:{contextClass}",
                "field" => $"field:{contextField!.Declaring}:{contextField.Name}:{contextOffset}",
                "face-surface" => $"face-surface:{contextIndex}:{contextOffset}",
                _ => context,
            };
            return null;
        }

        GElementGraphNode Node(int index, ushort cls, uint handle)
        {
            int first = position - start;
            var fields = ReadFields(cls, 0);
            return new(index, cls, handle, first, position - start, fields);
        }

        IReadOnlyList<GElementGraphField> ReadFields(ushort cls, int depth)
        {
            if (depth >= 32 || !GElementGraphFieldChains.TryGetValue(cls, out var fields)) throw Fail;
            var result = new GElementGraphField[fields.Length];
            for (int i = 0; i < fields.Length; i++) result[i] = Field(fields[i], depth);
            return result;
        }

        GElementGraphField Field(TfField field, int depth)
        {
            int first = position - start;
            context = "field";
            contextField = field;
            contextOffset = first;
            int high = field.Mods >> 4;
            uint? count = high == 5 ? (uint)Raw(4, false) : high == 1 ? (uint)field.Cnt!.Value : null;
            uint length = count ?? 1;
            IReadOnlyList<GElementGraphItem> items = [];
            if (length != uint.MaxValue)
            {
                if (length > 100_000 || length > (uint)((end - position) / Minimum(field))) throw Fail;
                if (length == 1)
                    items = new[] { Item(field, depth) };
                else if (length > 1)
                {
                    var repeatedItems = new List<GElementGraphItem>();
                    for (uint i = 0; i < length; i++) repeatedItems.Add(Item(field, depth));
                    items = repeatedItems;
                }
            }
            return new(field.Declaring, field.Name, count, first, position - start, items);
        }

        GElementGraphItem Item(TfField field, int depth)
        {
            if (++itemCount > 1_000_000) throw Fail;
            int first = position - start;
            long[] values = [];
            GElementGraphPointer? pointer = null;
            IReadOnlyList<GElementGraphField> children = [];
            if (field.Type is 1 or 2) values = [Raw(1, false)];
            else if (field.Type == 4) values = [Raw(4, true)];
            else if (field.Type == 5) values = [Raw(4, false)];
            else if (field.Type == 7) values = [Raw(8, true)];
            else if (field.Type == 13)
            {
                int dimensions = (field.Declaring, field.Name) switch
                {
                    (1248, "uv") or (524, "m_corners") => 2,
                    (2008, "m_bBox") or (2008, "m_tightbBox") or (78, "m_3x3") => 3,
                    _ => throw Fail
                };
                values = new long[dimensions];
                for (int i = 0; i < dimensions; i++) values[i] = Raw(8, true);
            }
            else if (field.Type == 14)
            {
                int low = field.Mods & 15;
                if (field.Declaring == 1246 && field.Name == "m_GInfo")
                {
                    values = [Raw(4, true), Raw(4, true), Raw(4, true), Raw(4, false)];
                }
                else if (low == 3) values = [Raw(4, false)];
                else if (low is 1 or 2) pointer = Pointer();
                else
                {
                    ushort cls = (field.Declaring, field.Name) switch
                    {
                        (20, "m_id") => 0,
                        (1244, "m_interiorEdgePnts") => 1248,
                        (523, "m_Envelope") => 524,
                        _ => (ushort)(field.Ref ?? throw Fail)
                    };
                    if (cls == 0) values = [Raw(4, true)]; // ElementId's own scalar, not a polymorphic pointer
                    else children = ReadFields(cls, depth + 1);
                }
            }
            else throw Fail;
            return new(first, position - start, values, pointer, children);
        }

        int Minimum(TfField field) => field.Type switch
        {
            1 or 2 => 1, 4 or 5 => 4, 7 => 8, 13 => 16,
            14 => 4, _ => throw Fail
        };

        long Raw(int width, bool signed)
        {
            if (width > end - position) throw Fail;
            long value = width switch
            {
                1 => data[position],
                2 => BitConverter.ToUInt16(data, position),
                4 => signed ? BitConverter.ToInt32(data, position) : BitConverter.ToUInt32(data, position),
                8 => BitConverter.ToInt64(data, position),
                _ => throw Fail
            };
            position += width;
            return value;
        }

        GElementGraphPointer Pointer()
        {
            int first = position - start;
            uint handle = (uint)Raw(4, false);
            ushort? cls = handle == 0 ? null : (ushort)Raw(2, false);
            int? index = null;
            ushort? token = null;
            long[] bits = [];
            if (cls == 4)
            {
                token = (ushort)Raw(2, false);
                if ((token & 255) != 8 || token >> 8 is not (0 or 1)) throw Fail;
                if (token >> 8 == 1)
                {
                    bits = new long[8];
                    for (int i = 0; i < 6; i++) bits[i] = Raw(8, true);
                    for (int i = 6; i < bits.Length; i++) bits[i] = Raw(4, false);
                }
            }
            else if (cls is > 0)
            {
                if (!GElementGraphSchemas.ContainsKey(cls.Value) || queue.Count >= 100_000) throw Fail;
                index = queue.Count;
                queue.Add((cls.Value, handle));
            }
            return new(handle, cls, index, token, bits, first, position - start);
        }
    }
}
