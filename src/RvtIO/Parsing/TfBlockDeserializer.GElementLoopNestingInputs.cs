namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record SourceTessEpsControl(int Type, int CurrentVersion, int Version,
        bool RequestedVersionRejected, long TessToleranceBits, long VertexEpsilonBits);
    internal sealed record GElementSurfaceFirstForm(int QueueIndex, ushort ClassId, int Start, int End,
        IReadOnlyList<long> DuBits, IReadOnlyList<long> DvBits, IReadOnlyList<long> MatrixBits);
    internal sealed record GElementLoopNestingInput(int LoopQueueIndex, int? FaceQueueIndex, string Outcome,
        bool? EarlyShouldBeCcw, IReadOnlyList<long>? SampleUvBits, SourceTessEpsControl? Epsilon,
        GElementSurfaceFirstForm? SurfaceMetric);
    internal sealed record GElementFaceLoopNestingInputs(int FaceQueueIndex, IReadOnlyList<GElementLoopNestingInput> Loops);
    internal sealed record OwnGElementFreshLoopNestingInputs(int OwnerId, int CurrentTessEpsVersion,
        IReadOnlyList<GElementFaceLoopNestingInputs> Faces, bool Complete);

    /// <summary>TessEpsCntrl constructor, getTessTol and getVertexEps in an explicitly supplied, supported
    /// current-version context. The image value is 1; this does not assert that external runtime writes cannot change it.
    /// The literal bits are those of the native library, not IFC tolerances.</summary>
    internal static SourceTessEpsControl? SelectSourceTessEpsControl(int type, int currentVersion,
        int? requestedVersion, out string? failure)
    {
        failure = "tess-eps-type";
        if (type is < 0 or > 2) return null; // native dbgCrash path, never fake success
        failure = "tess-eps-current-version";
        if (currentVersion is < 0 or > 1) return null;
        bool rejected = requestedVersion is int r && (r < 0 || r > currentVersion);
        int version = requestedVersion is int v && !rejected ? v : currentVersion;
        long tolerance = (version > 0, type == 1) switch
        {
            (false, false) => 0x3f75555555555555, (false, true) => 0x3f3c71c71c71c71c,
            (true, false) => 0x3f7570124aff017b, (true, true) => 0x3f3c956db95401f9
        };
        double epsilon = BitConverter.Int64BitsToDouble(tolerance) / 10.0;
        if (type == 2) epsilon /= 20.0; // preserve TWO DIVSD operations, not division by200
        failure = null;
        return new(type, currentVersion, version, rejected, tolerance, BitConverter.DoubleToInt64Bits(epsilon));
    }

    /// <summary>Plane (522) and CylSurf (983) evaluate with position, second derivatives and normal = null, followed by the
    /// first fundamental form. No unit-axis or orthogonality assumption and no normalization. The scalar operation order
    /// mirrors the native one; cylinder sin/cos use this runtime's math primitives.</summary>
    internal static GElementSurfaceFirstForm? ComputeOwnGElementSurfaceFirstForm(GElementGraphNode surface,
        IReadOnlyList<long> uvBits, out string? failure)
    {
        failure = "surface-first-form";
        if (surface.ClassId is not (522 or 983) || uvBits.Count != 2) return null;
        try
        {
            double[] x = Vector("m_xVec"), y = Vector("m_yVec"), du, dv;
            if (surface.ClassId == 522) { du = x; dv = y; }
            else
            {
                double angle = BitConverter.Int64BitsToDouble(uvBits[0]);
                if (!double.IsFinite(angle)) throw Fail; // unresolved CRT invalid transcendental input
                double sin = Math.Sin(angle), cos = Math.Cos(angle), radius = Scalar("m_radius");
                double a = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(radius * sin) ^ long.MinValue);
                double b = radius * cos;
                du = [a * x[0] + b * y[0], a * x[1] + b * y[1], a * x[2] + b * y[2]];
                dv = Vector("m_zVec");
            }
            double aa = (du[0] * du[0] + du[1] * du[1]) + du[2] * du[2];
            double ab = (dv[1] * du[1] + du[0] * dv[0]) + dv[2] * du[2];
            double bb = (dv[0] * dv[0] + dv[1] * dv[1]) + dv[2] * dv[2];
            failure = null;
            return new(surface.QueueIndex, surface.ClassId, surface.Start, surface.End,
                du.Select(BitConverter.DoubleToInt64Bits).ToArray(), dv.Select(BitConverter.DoubleToInt64Bits).ToArray(),
                [BitConverter.DoubleToInt64Bits(aa), BitConverter.DoubleToInt64Bits(ab),
                 BitConverter.DoubleToInt64Bits(ab), BitConverter.DoubleToInt64Bits(bb)]);
        }
        catch (DecodeFail) { return null; }

        GElementGraphField Field(string name, int count)
        {
            var fs = surface.Fields.Where(f => f.Declaring == surface.ClassId && f.Name == name).ToArray();
            if (fs.Length != 1 || fs[0].Items.Count != count || count == 3 && fs[0].Count != 3) throw Fail;
            foreach (var item in fs[0].Items)
                if (item.Values.Count != 1 || item.End - item.Start != 8 || item.Pointer != null || item.Fields.Count != 0) throw Fail;
            return fs[0];
        }
        double[] Vector(string name) => Field(name, 3).Items.Select(i => BitConverter.Int64BitsToDouble(i.Values[0])).ToArray();
        double Scalar(string name) => BitConverter.Int64BitsToDouble(Field(name, 1).Items[0].Values[0]);
    }

    /// <summary>getOuterLoops candidate filter and the null-optional-face shouldBeCCW early branches, sample and EPS
    /// preparation. The first form is evaluated at the sample for this own Face's pntWithin inputs. A pending containment
    /// result stays pending (Complete = false), never an outer/inner guess. Needs an explicit captured archive and EPS
    /// version; not a full pntWithin or a complete cut/extrusion/IFC result.</summary>
    internal static OwnGElementFreshLoopNestingInputs? SelectOwnGElementLoopNestingInputs(
        OwnGElementGeometryGraph graph, int currentTessEpsVersion, out string? failure)
    {
        if (currentTessEpsVersion is not (0 or 1))
            return ReadOwnGElementLoopNestingInputs(graph, currentTessEpsVersion, out failure);
        var analysis = AnalyzeGElement(graph);
        var result = currentTessEpsVersion == 0 ? analysis.LegacyNesting.Value : analysis.PositiveNesting.Value;
        failure = result.Failure;
        return result.Value;
    }

    private static OwnGElementFreshLoopNestingInputs? ReadOwnGElementLoopNestingInputs(
        OwnGElementGeometryGraph graph, int currentTessEpsVersion, out string? failure)
    {
        var epsilon = SelectSourceTessEpsControl(0, currentTessEpsVersion, null, out failure);
        if (epsilon == null) return null;
        var topology = SelectOwnGElementLoopTopology(graph, out failure);
        if (topology == null) return null;
        var nodes = graph.Nodes.ToDictionary(n => n.QueueIndex);
        var faces = topology.Faces.ToDictionary(f => f.FaceQueueIndex);
        var result = new List<GElementFaceLoopNestingInputs>(); bool complete = true;
        string context = "loop-nesting-inputs";
        try
        {
            foreach (var face in topology.Faces)
            {
                var loops = new List<GElementLoopNestingInput>();
                foreach (var loop in face.Loops)
                {
                    context = $"loop-nesting-inputs:{face.FaceQueueIndex}:{loop.QueueIndex}";
                    string outcome; bool? early; IReadOnlyList<long>? sample = null;
                    SourceTessEpsControl? eps = null; GElementSurfaceFirstForm? metric = null;
                    if (!loop.GetOuterLoopsCandidate) { outcome = loop.Open != 0 ? "excluded-open" : "excluded-empty"; early = null; }
                    else if (loop.FaceQueueIndex == null) { outcome = "no-own-face"; early = false; }
                    else
                    {
                        if (!faces.TryGetValue(loop.FaceQueueIndex.Value, out var ownFace)) throw Fail;
                        if (ownFace.FirstLoopQueueIndex == null) { outcome = "own-face-no-first-loop"; early = false; }
                        else if (ownFace.Loops[0].NextLoopQueueIndex == null) { outcome = "single-loop-outer"; early = true; }
                        else if (loop.FirstEdgeQueueIndex == null) { outcome = "no-first-edge"; early = true; }
                        else
                        {
                            if (loop.Edges.Count == 0 || loop.Edges[0].EdgeQueueIndex != loop.FirstEdgeQueueIndex) throw Fail;
                            var edge = loop.Edges[0];
                            if (edge.PointCount <= 1) { outcome = "insufficient-edge-points"; early = false; }
                            else if (edge.FaceSide is not int side) { outcome = "face-side-mismatch"; early = false; }
                            else
                            {
                                IReadOnlyList<long> Bits(GElementEdgeUvPoint p) => side == 0 ? p.Side0Bits : p.Side1Bits;
                                if (edge.PointCount == 2)
                                {
                                    var a = Bits(edge.FirstAndLastPoints[0]); var b = Bits(edge.FirstAndLastPoints[1]);
                                    sample = Enumerable.Range(0, 2).Select(i => BitConverter.DoubleToInt64Bits(
                                        0.5 * BitConverter.Int64BitsToDouble(a[i]) + 0.5 * BitConverter.Int64BitsToDouble(b[i]))).ToArray();
                                }
                                else sample = Bits(edge.InteriorPoints[edge.PointCount / 2 - 1]);
                                eps = epsilon; early = null; outcome = "pending-containment"; complete = false;
                                var own = nodes[ownFace.FaceQueueIndex];
                                var fs = own.Fields.Where(f => f.Declaring == 1589 && f.Name == "m_pSurf").ToArray();
                                if (fs.Length != 1 || fs[0].Items.Count != 1 || fs[0].Items[0].Pointer is not { } p) throw Fail;
                                if (p.Handle == 0)
                                {
                                    if (p.ClassId != null || p.QueueIndex != null || p.HandToken != null) throw Fail;
                                }
                                else
                                {
                                    if (p.ClassId is not (522 or 983) || p.QueueIndex is not int q || p.HandToken != null
                                        || !nodes.TryGetValue(q, out var surf) || surf.ClassId != p.ClassId || surf.Handle != p.Handle) throw Fail;
                                    // The virtual24 types 1 and 2 do not enter the shared type 7 path of getSurface.
                                    metric = ComputeOwnGElementSurfaceFirstForm(surf, sample, out _);
                                    if (metric == null) throw Fail;
                                }
                            }
                        }
                    }
                    loops.Add(new(loop.QueueIndex, loop.FaceQueueIndex, outcome, early, sample, eps, metric));
                }
                result.Add(new(face.FaceQueueIndex, loops));
            }
            failure = null; return new(graph.OwnerId, currentTessEpsVersion, result, complete);
        }
        catch (DecodeFail) { failure = context; return null; }
    }
}
