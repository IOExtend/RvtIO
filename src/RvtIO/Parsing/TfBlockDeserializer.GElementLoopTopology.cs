namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record GElementEdgeUvPoint(int Start, int End, IReadOnlyList<long> Side0Bits,
        IReadOnlyList<long> Side1Bits);
    internal sealed record GElementLoopEdgeVisit(int EdgeQueueIndex, int? FaceSide, byte Flags, int PointCount,
        IReadOnlyList<GElementEdgeUvPoint> FirstAndLastPoints, IReadOnlyList<GElementEdgeUvPoint> InteriorPoints,
        int? NextQueueIndex, string Outcome);
    internal sealed record GElementLoopTopology(int QueueIndex, ushort ClassId, int? FaceQueueIndex,
        int? NextLoopQueueIndex, int? FirstEdgeQueueIndex, int? LastEdgeQueueIndex, byte Open,
        bool GetOuterLoopsCandidate, IReadOnlyList<int> ConstructorSkippedLoops, int? InitialEdgeQueueIndex,
        IReadOnlyList<GElementLoopEdgeVisit> Edges, string EndOutcome);
    internal sealed record GElementFaceLoopTopology(int FaceQueueIndex, int? FirstLoopQueueIndex,
        IReadOnlyList<GElementLoopTopology> Loops);
    internal sealed record OwnGElementFreshLoopTopology(int OwnerId, IReadOnlyList<GElementFaceLoopTopology> Faces);
    private readonly record struct LoopEdgeData(int? Side0, int? Side1, byte Flags,
        IReadOnlyList<GElementEdgeUvPoint> Endpoints, IReadOnlyList<GElementEdgeUvPoint> Interior);

    /// <summary>Face first/next loop order and a fresh ConstGEdgeIter(loop) forward traversal. Uses same-archive key
    /// bindings, the concrete virtual isLoop dispatch, the first matching face side and the flag-based point count.
    /// Both UV sides are kept as raw IEEE bits, including unused payloads. The iterator does not skip open loops or
    /// reverse side 1 UV arrays. It is not shouldBeCCW, a point-in-loop test, runtime topology or an extrusion test.</summary>
    internal static OwnGElementFreshLoopTopology? SelectOwnGElementLoopTopology(
        OwnGElementGeometryGraph graph, out string? failure)
    {
        var analysis = AnalyzeGElement(graph).Topology.Value;
        failure = analysis.Failure;
        return analysis.Value;
    }

    private static OwnGElementFreshLoopTopology? ReadOwnGElementLoopTopology(
        OwnGElementGeometryGraph graph, out string? failure)
    {
        var references = SelectOwnGElementFreshArchiveReferences(graph, out failure);
        if (references == null) return null;
        failure = "loop-topology-incomplete-references";
        if (!references.Complete) return null;
        var nodes = graph.Nodes.ToDictionary(n => n.QueueIndex);
        var links = new Dictionary<(int, int, string, int, int), GElementArchiveReference>();
        var edgeData = new Dictionary<int, LoopEdgeData>();
        string? stage = null;
        int contextOwner = 0, contextTarget = 0;
        try
        {
            foreach (var link in references.References)
                if (!links.TryAdd((link.SourceQueueIndex, link.Declaring, link.Name, link.ItemIndex, link.Start), link)) throw Fail;
            var faces = new List<GElementFaceLoopTopology>();
            foreach (var face in graph.Nodes.Where(n => n.ClassId == 1589))
            {
                int? first = Owned(face, 1590, "m_pFirstLoop", true);
                var loops = new List<GElementLoopTopology>(); var seen = new HashSet<int>();
                int? q = first;
                while (q is int index)
                {
                    stage = "chain";
                    contextOwner = face.QueueIndex;
                    contextTarget = index;
                    if (!seen.Add(index)) throw Fail; // native chain is unbounded; never invent empty geometry
                    var loop = Loop(index);
                    int? next = Owned(loop, 1255, "m_nextLoop", true);
                    int? ownFace = Weak(loop, 1255, "m_pFace", 0, 1, true);
                    int? firstEdge = Weak(loop, 1255, "m_next", 0, 1, false);
                    int? lastEdge = Weak(loop, 1255, "m_prev", 0, 1, false);
                    byte open = Byte(loop, 1255, "m_open");
                    // getOuterLoops rejects only the self sentinel and the open flag here.
                    // A null first edge is not silently reclassified as an empty self sentinel.
                    bool candidate = firstEdge != index && open == 0;
                    List<int>? skipped = null;
                    HashSet<int>? constructorSeen = null;
                    int? initial = firstEdge; var cursor = loop;
                    while (initial == cursor.QueueIndex)
                    {
                        stage = "constructor";
                        contextOwner = index;
                        contextTarget = cursor.QueueIndex;
                        if (!(constructorSeen ??= new()).Add(cursor.QueueIndex)) throw Fail;
                        (skipped ??= new()).Add(cursor.QueueIndex);
                        int? following = Owned(cursor, 1255, "m_nextLoop", true);
                        if (following == null) { initial = null; break; }
                        cursor = Loop(following.Value);
                        initial = Weak(cursor, 1255, "m_next", 0, 1, false);
                    }
                    // The graphics constructor keeps the original loop's face even after skipping empty loops.
                    var visits = new List<GElementLoopEdgeVisit>(); int? current = initial;
                    string end = "no-initial-edge"; int increments = 0;
                    while (current is int edgeIndex)
                    {
                        stage = "edge";
                        contextOwner = index;
                        contextTarget = edgeIndex;
                        if (!nodes.TryGetValue(edgeIndex, out var edge) || edge.ClassId != 1243) throw Fail;
                        if (!edgeData.TryGetValue(edgeIndex, out var data))
                        {
                            var faceField = RequireUniqueGraphField(edge, 1244, "m_pFace", 2);
                            data = new(WeakField(edge, faceField, 0, true), WeakField(edge, faceField, 1, true),
                                Byte(edge, 1244, "m_flags"), Points(edge, "m_firstAndLastEdgePnts", true),
                                Points(edge, "m_interiorEdgePnts", false));
                            edgeData.Add(edgeIndex, data);
                        }
                        int? side = ownFace == data.Side0 ? 0 : ownFace == data.Side1 ? 1 : null;
                        var (flags, endpoints, interior) = (data.Flags, data.Endpoints, data.Interior);
                        int count = (flags & 2) == 0 ? 0 : (flags & 4) == 0 ? 1 : interior.Count + 2;
                        int? target = side is int s ? Weak(edge, 1244, "m_next", s, 2, false) : null;
                        if (side == null) end = "face-mismatch"; // native warning and NULL current, without increment
                        else
                        {
                            end = target == null ? "null-next" : LoopKind(nodes[target.Value].ClassId) ? "loop-sentinel" : "advanced";
                            // The initial-edge sentinel of a fresh loop is null. A loop target always stops,
                            // irrespective of that loop's open flag; no whole-face iteration is requested.
                            if (++increments > 100000) end = "increment-limit";
                        }
                        visits.Add(new(edgeIndex, side, flags, count, endpoints, interior, target, end));
                        current = end == "advanced" ? target : null;
                    }
                    loops.Add(new(index, loop.ClassId, ownFace, next, firstEdge, lastEdge, open,
                        candidate, (IReadOnlyList<int>?)skipped ?? Array.Empty<int>(), initial, visits, end));
                    q = next;
                }
                faces.Add(new(face.QueueIndex, first, loops));
            }
            failure = null; return new(graph.OwnerId, faces);
        }
        catch (DecodeFail)
        {
            failure = stage == null ? "loop-topology" : $"loop-topology-{stage}:{contextOwner}:{contextTarget}";
            return null;
        }

        static bool LoopKind(ushort cls) => cls is 1254 or 1257;
        GElementGraphNode Loop(int q)
        {
            if (!nodes.TryGetValue(q, out var node) || !LoopKind(node.ClassId)) throw Fail;
            return node;
        }
        int? Owned(GElementGraphNode node, int declaring, string name, bool loopTarget)
        {
            var item = RequireUniqueGraphField(node, declaring, name, 1).Items[0]; var p = item.Pointer;
            if (p == null || item.Values.Count != 0 || item.Fields.Count != 0) throw Fail;
            if (p.Handle == 0)
            {
                if (p.QueueIndex != null || p.ClassId != null || p.HandToken != null) throw Fail;
                return null;
            }
            if (p.QueueIndex is not int target || !nodes.TryGetValue(target, out var n)
                || p.ClassId != n.ClassId || p.Handle != n.Handle || p.HandToken != null
                || loopTarget && !LoopKind(n.ClassId)) throw Fail;
            return target;
        }
        int? Weak(GElementGraphNode node, int declaring, string name, int item, int count, bool faceTarget) =>
            WeakField(node, RequireUniqueGraphField(node, declaring, name, count), item, faceTarget);
        int? WeakField(GElementGraphNode node, GElementGraphField field, int item, bool faceTarget)
        {
            if (!links.TryGetValue((node.QueueIndex, field.Declaring, field.Name, item, field.Items[item].Start), out var link)) throw Fail;
            if (link.Outcome == "null") return null;
            if (link.Outcome != "own-node" || link.TargetQueueIndex is not int target
                || !nodes.TryGetValue(target, out var n) || n.ClassId != link.TargetClassId
                || (faceTarget ? n.ClassId != 1589 : n.ClassId != 1243 && !LoopKind(n.ClassId))) throw Fail;
            return target;
        }
        byte Byte(GElementGraphNode node, int declaring, string name)
        {
            var item = RequireUniqueGraphField(node, declaring, name, 1).Items[0];
            if (item.Values.Count != 1 || item.Values[0] is < 0 or > 255 || item.End - item.Start != 1
                || item.Pointer != null || item.Fields.Count != 0) throw Fail;
            return (byte)item.Values[0];
        }
        IReadOnlyList<GElementEdgeUvPoint> Points(GElementGraphNode node, string name, bool endpoints)
        {
            var field = RequireUniqueGraphField(node, 1244, name, endpoints ? 2 : null);
            if (field.Count == null || (field.Count == uint.MaxValue ? field.Items.Count != 0 : field.Count != field.Items.Count)) throw Fail;
            if (field.Items.Count == 0) return [];
            var points = new GElementEdgeUvPoint[field.Items.Count];
            for (int i = 0; i < points.Length; i++)
            {
                var item = field.Items[i];
                if (item.Pointer != null || item.Values.Count != 0 || item.Fields.Count != 1 || item.End - item.Start != 32) throw Fail;
                var uv = item.Fields[0];
                if (uv.Declaring != 1248 || uv.Name != "uv" || uv.Count != 2 || uv.Items.Count != 2) throw Fail;
                foreach (var side in uv.Items)
                    if (side.Values.Count != 2 || side.Pointer != null || side.Fields.Count != 0 || side.End - side.Start != 16) throw Fail;
                points[i] = new(item.Start, item.End, uv.Items[0].Values, uv.Items[1].Values);
            }
            return points;
        }
    }
}
