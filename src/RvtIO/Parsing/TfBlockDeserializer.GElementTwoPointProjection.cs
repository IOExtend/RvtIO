using RvtIO.Geometry.Meshing;

namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record SourceTwoPointProjection(long ParameterBits, long DistanceBits,
        IReadOnlyList<long> DirectionBits, IReadOnlyList<long> ResidualBits);
    internal sealed record GElementProjectedEdge(int EdgeQueueIndex, int? DefaultFaceQueueIndex, string Outcome,
        IReadOnlyList<long>? FirstUvBits, IReadOnlyList<long>? LastUvBits,
        IReadOnlyList<long>? FirstXyzBits, IReadOnlyList<long>? LastXyzBits, SourceTwoPointProjection? Projection);
    internal sealed record GElementOtherLoopTwoPointProjection(int LoopQueueIndex, bool AfterUnresolvedCheck,
        string Outcome, int? PointWithinState, IReadOnlyList<long>? QueryXyzBits, IReadOnlyList<GElementProjectedEdge> Edges);
    internal sealed record GElementLoopTwoPointProjections(int LoopQueueIndex, string Outcome,
        bool? ResolvedShouldBeCcw, IReadOnlyList<GElementOtherLoopTwoPointProjection> Checks);
    internal sealed record GElementFaceTwoPointProjections(int FaceQueueIndex, IReadOnlyList<GElementLoopTwoPointProjections> Loops);
    internal sealed record OwnGElementFreshTwoPointProjections(int OwnerId, int CurrentTessEpsVersion,
        IReadOnlyList<GElementFaceTwoPointProjections> Faces, bool Complete);

    internal readonly record struct SourcePlaneBasis(Pt3 Origin, Pt3 X, Pt3 Y)
    {
        internal bool TryEvaluate(double u, double v, out Pt3 point)
        {
            point = (u * X + v * Y) + Origin;
            return double.IsFinite(u) && double.IsFinite(v) && point.IsFinite;
        }
    }

    internal static bool TryReadSourcePlaneBasis(GElementGraphNode plane, out SourcePlaneBasis basis)
    {
        basis = default;
        if (plane.ClassId != 522) return false;
        try
        {
            basis = new(Vector("m_origin"), Vector("m_xVec"), Vector("m_yVec"));
            return true;
        }
        catch (DecodeFail) { return false; }

        Pt3 Vector(string name)
        {
            var field = RequireUniqueGraphField(plane, 522, name, 3);
            if (field.Count != 3) throw Fail;
            foreach (var item in field.Items)
                if (item.Values.Count != 1 || item.End - item.Start != 8 || item.Pointer != null || item.Fields.Count != 0) throw Fail;
            var vector = new Pt3(BitConverter.Int64BitsToDouble(field.Items[0].Values[0]),
                BitConverter.Int64BitsToDouble(field.Items[1].Values[0]),
                BitConverter.Int64BitsToDouble(field.Items[2].Values[0]));
            return vector.IsFinite ? vector : throw Fail;
        }
    }

    /// <summary>Plane virtual 8 position output: (u*x + v*y) + origin, with no unit basis, normal,
    /// first/second derivative outputs or orientation flip.</summary>
    internal static IReadOnlyList<long>? EvaluateSourceOwnPlanePoint(GElementGraphNode plane,
        IReadOnlyList<long> uvBits, out string? failure)
    {
        failure = "plane-point";
        if (uvBits.Count != 2 || !TryReadSourcePlaneBasis(plane, out var basis)
            || !basis.TryEvaluate(BitConverter.Int64BitsToDouble(uvBits[0]),
                BitConverter.Int64BitsToDouble(uvBits[1]), out var point)) return null;
        failure = null;
        return new[] { BitConverter.DoubleToInt64Bits(point.X), BitConverter.DoubleToInt64Bits(point.Y),
            BitConverter.DoubleToInt64Bits(point.Z) };
    }

    /// <summary>Two-endpoint branch with a null EdgePnt output. Uses the denominator max(1e-18, d.d), the Y+X+Z dot
    /// order, the 1e-9 endpoint parameter snap, residual1*delta + (-t)*direction, then the unscaled XYZ length.
    /// Non-finite arithmetic and the warning policy are explicitly unsupported.</summary>
    internal static SourceTwoPointProjection? ProjectSourceTwoPointSegment(IReadOnlyList<long> firstBits,
        IReadOnlyList<long> lastBits, IReadOnlyList<long> queryBits, out string? failure)
    {
        failure = "two-point-projection";
        if (firstBits.Count != 3 || lastBits.Count != 3 || queryBits.Count != 3) return null;
        var a = firstBits.Select(BitConverter.Int64BitsToDouble).ToArray();
        var z = lastBits.Select(BitConverter.Int64BitsToDouble).ToArray();
        var p = queryBits.Select(BitConverter.Int64BitsToDouble).ToArray();
        if (a.Concat(z).Concat(p).Any(v => !double.IsFinite(v))) return null;
        double[] d = [z[0] - a[0], z[1] - a[1], z[2] - a[2]], delta = [p[0] - a[0], p[1] - a[1], p[2] - a[2]];
        double denominator = Math.Max(BitConverter.Int64BitsToDouble(0x3c32725dd1d243ac),
            (d[1] * d[1] + d[0] * d[0]) + d[2] * d[2]);
        double t = ((d[1] * delta[1] + d[0] * delta[0]) + d[2] * delta[2]) / denominator;
        if (!double.IsFinite(t) || !double.IsFinite(denominator)) return null;
        if (BitConverter.Int64BitsToDouble(0x3e112e0be826d695) >= t) t = 0;
        else if (t >= BitConverter.Int64BitsToDouble(0x3fefffffff768fa1)) t = 1;
        double minus = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(t) ^ long.MinValue);
        double[] residual = [1.0 * delta[0] + minus * d[0], 1.0 * delta[1] + minus * d[1], 1.0 * delta[2] + minus * d[2]];
        double squared = (residual[0] * residual[0] + residual[1] * residual[1]) + residual[2] * residual[2];
        if (!double.IsFinite(squared) || squared < 0) return null;
        failure = null;
        return new(BitConverter.DoubleToInt64Bits(t), BitConverter.DoubleToInt64Bits(Math.Sqrt(squared)),
            d.Select(BitConverter.DoubleToInt64Bits).ToArray(), residual.Select(BitConverter.DoubleToInt64Bits).ToArray());
    }

    /// <summary>Projection phase after first-sample envelope acceptance. The query XYZ uses the target loop Face; each
    /// edge endpoint uses the first non-null edge face, not necessarily that loop Face. Only ordinary/no-chain two-point
    /// own Plane paths are admitted. Stops at an unsupported edge or the first distance at or below the EPS boundary (2).
    /// No polygon, curve or shouldBeCCW retry verdict is guessed.</summary>
    internal static OwnGElementFreshTwoPointProjections? SelectOwnGElementLoopTwoPointProjections(
        OwnGElementGeometryGraph graph, int currentTessEpsVersion, out string? failure)
    {
        var envelopes = SelectOwnGElementLoopEnvelopeFirstPass(graph, currentTessEpsVersion, out failure);
        if (envelopes == null) return null;
        var inputs = SelectOwnGElementLoopNestingInputs(graph, currentTessEpsVersion, out failure);
        if (inputs == null) return null;
        var topo = SelectOwnGElementLoopTopology(graph, out failure);
        if (topo == null) return null;
        var refs = SelectOwnGElementFreshArchiveReferences(graph, out failure);
        if (refs == null || !refs.Complete) return null;
        var nodes = graph.Nodes.ToDictionary(n => n.QueueIndex);
        var loopsByQ = topo.Faces.SelectMany(f => f.Loops).GroupBy(l => l.QueueIndex).ToDictionary(g => g.Key, g => g.First());
        var samples = inputs.Faces.SelectMany(f => f.Loops).GroupBy(l => l.LoopQueueIndex).ToDictionary(g => g.Key, g => g.First());
        var links = refs.References.Where(r => r.Declaring == 1244 && r.Name == "m_pFace")
            .ToDictionary(r => (r.SourceQueueIndex, r.ItemIndex));
        var faces = new List<GElementFaceTwoPointProjections>(); string context = "two-point-loop-projections";
        try
        {
            foreach (var face in envelopes.Faces)
            {
                var loops = new List<GElementLoopTwoPointProjections>();
                foreach (var current in face.Loops)
                {
                    var checks = new List<GElementOtherLoopTwoPointProjection>();
                    foreach (var check in current.Checks)
                    {
                        context = $"two-point-check:{current.LoopQueueIndex}:{check.LoopQueueIndex}";
                        var edges = new List<GElementProjectedEdge>(); string outcome = check.Outcome;
                        int? state = check.PointWithinState; IReadOnlyList<long>? query = null;
                        if (outcome == "accepted-envelope-pending")
                        {
                            var target = loopsByQ[check.LoopQueueIndex]; var sample = samples[current.LoopQueueIndex];
                            var tn = nodes[target.QueueIndex];
                            bool noChains = tn.ClassId == 1254;
                            if (tn.ClassId == 1257)
                            {
                                var chains = Field(tn, 1257, "m_chainsWithEnvelopes", null);
                                noChains = chains.Items.Count == 0 && chains.Count is 0 or uint.MaxValue;
                            }
                            if (!noChains || target.EndOutcome != "loop-sentinel" || target.Edges.Count == 0
                                || target.Edges[^1].NextQueueIndex != target.QueueIndex)
                                outcome = "unsupported-projection-topology";
                            else if (target.FaceQueueIndex is not int tq || sample.SampleUvBits == null || sample.Epsilon == null)
                                throw Fail;
                            else if (PlaneForFace(tq) is not { } queryPlane)
                                outcome = "unsupported-query-surface";
                            else
                            {
                                query = EvaluateSourceOwnPlanePoint(queryPlane, sample.SampleUvBits, out _);
                                if (query == null) throw Fail;
                                outcome = "pending-polygon";
                                foreach (var edge in target.Edges)
                                {
                                    int? defaultFace = null; string why; IReadOnlyList<long>? uv0 = null, uv1 = null, xyz0 = null, xyz1 = null;
                                    SourceTwoPointProjection? projection = null;
                                    if (edge.PointCount != 2) why = "unsupported-point-count";
                                    else
                                    {
                                        int? a = FaceLink(edge.EdgeQueueIndex, 0), z = FaceLink(edge.EdgeQueueIndex, 1);
                                        defaultFace = a ?? z; int side = a == null ? 1 : 0;
                                        if (defaultFace is not int eq || PlaneForFace(eq) is not { } plane) why = "unsupported-endpoint-surface";
                                        else
                                        {
                                            // evalxyz and evalUVAlongFaceLoop each flip when side differs
                                            // from flag 0. For endpoint parameters 0 and 1 the two flips cancel.
                                            var first = side == 0 ? edge.FirstAndLastPoints[0].Side0Bits : edge.FirstAndLastPoints[0].Side1Bits;
                                            var last = side == 0 ? edge.FirstAndLastPoints[1].Side0Bits : edge.FirstAndLastPoints[1].Side1Bits;
                                            uv0 = Interpolate(first, last, 0); uv1 = Interpolate(first, last, 1);
                                            xyz0 = EvaluateSourceOwnPlanePoint(plane, uv0, out _); xyz1 = EvaluateSourceOwnPlanePoint(plane, uv1, out _);
                                            if (xyz0 == null || xyz1 == null) throw Fail;
                                            projection = ProjectSourceTwoPointSegment(xyz0, xyz1, query, out _);
                                            if (projection == null) throw Fail;
                                            double distance = BitConverter.Int64BitsToDouble(projection.DistanceBits);
                                            why = BitConverter.Int64BitsToDouble(sample.Epsilon.VertexEpsilonBits) >= distance ? "boundary" : "projected";
                                        }
                                    }
                                    edges.Add(new(edge.EdgeQueueIndex, defaultFace, why, uv0, uv1, xyz0, xyz1, projection));
                                    if (why == "boundary") { state = 2; outcome = "boundary-first-sample"; break; }
                                    if (why != "projected") { outcome = why; break; }
                                }
                            }
                        }
                        checks.Add(new(check.LoopQueueIndex, check.AfterUnresolvedCheck, outcome, state, query, edges));
                    }
                    loops.Add(new(current.LoopQueueIndex, current.Outcome, current.ResolvedShouldBeCcw, checks));
                }
                faces.Add(new(face.FaceQueueIndex, loops));
            }
            failure = null; return new(graph.OwnerId, currentTessEpsVersion, faces, envelopes.Complete);
        }
        catch (DecodeFail) { failure = context; return null; }

        static IReadOnlyList<long> Interpolate(IReadOnlyList<long> a, IReadOnlyList<long> z, double t)
            => Enumerable.Range(0, 2).Select(i => BitConverter.DoubleToInt64Bits((1.0 - t) * BitConverter.Int64BitsToDouble(a[i]) + t * BitConverter.Int64BitsToDouble(z[i]))).ToArray();
        int? FaceLink(int edge, int side)
        {
            if (!links.TryGetValue((edge, side), out var r)) throw Fail;
            if (r.Outcome == "null") return null;
            if (r.Outcome != "own-node" || r.TargetQueueIndex is not int q || nodes[q].ClassId != 1589) throw Fail;
            return q;
        }
        GElementGraphNode? PlaneForFace(int face)
        {
            var p = Field(nodes[face], 1589, "m_pSurf", 1).Items[0].Pointer;
            if (p == null || p.HandToken != null || p.Handle == 0 || p.QueueIndex is not int q || !nodes.TryGetValue(q, out var sn)
                || p.ClassId != sn.ClassId || p.Handle != sn.Handle) return null;
            return sn.ClassId == 522 ? sn : null;
        }
        static GElementGraphField Field(GElementGraphNode node, int declaring, string name, int? count)
        {
            var fs = node.Fields.Where(f => f.Declaring == declaring && f.Name == name).ToArray();
            if (fs.Length != 1 || count is int c && fs[0].Items.Count != c) throw Fail; return fs[0];
        }
    }
}
