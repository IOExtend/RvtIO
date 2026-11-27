using RvtIO.Geometry.Meshing;
using RvtIO.Internal;

namespace RvtIO.Parsing;

internal static partial class GRepParserV2
{
    /// <summary>
    /// The Revit face meshes are written for the current profile output; RVTIO_MESH=0 switches
    /// them off (the dense polygons are written instead).
    /// </summary>
    internal static readonly bool MeshEnabled = Environment.GetEnvironmentVariable("RVTIO_MESH") != "0";

    /// <summary>
    /// The Geometry nodes Revit writes as triangle sets: a node with a non-planar face
    /// whose faces are all described in the terms of the Revit face mesher (planes and
    /// cylinders).
    /// </summary>
    private static readonly bool MeshNodeLog = Environment.GetEnvironmentVariable("RVTIO_MESH_LOG") == "1";

    /// <summary>
    /// RVTIO_X_DISPLAYNODE=0 leaves the curved nodes of display surfaces out (Revit writes
    /// them as triangle sets all the same).
    /// </summary>
    private static readonly bool MeshDisplayNodes = Environment.GetEnvironmentVariable("RVTIO_X_DISPLAYNODE") != "0";

    private static List<GeometryNode>? BuildNodes(Cur c, Dictionary<uint, SurfaceRec> surfaces, HashSet<uint> bodyFaces,
        IEnumerable<uint> inactiveBodyFaces, Dictionary<uint, List<int>> loopsByFace, Dictionary<uint, EdgeRec?> edges,
        Dictionary<uint, (int Node, int Style)> nodeByFace, Dictionary<int, long> nodeOrders,
        List<(int Node, int Style, int RenderStyleId)>? displayOnlyNodes = null, List<MeshNodeMeasure>? nodeMeasures = null,
        bool measureSourceNodes = false)
    {
        MeshMarkFreeformEdges(edges, surfaces);
        var inactiveByNode = inactiveBodyFaces.Where(nodeByFace.ContainsKey).GroupBy(h => nodeByFace[h].Node)
            .ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<GeometryNode>();
        MeshVertexTable? vertexTable = null;
        bool meshCurvedPlanarEdges = Environment.GetEnvironmentVariable("RVTIO_X_CURVEDEDGE") == "1";
        // a geometry that carries no node ids at all (the own GRep of a railing or a wall) is the
        // one node 0
        bool withoutNodes = !bodyFaces.Any(nodeByFace.ContainsKey);
        var closedNodes = nodeMeasures != null
            ? ClosedNodes(c.Edges.Where(edge => edge.NextA != 0 || edge.NextB != 0).Select(edge => (edge.FaceA, edge.FaceB)), nodeByFace)
            : [];
        foreach (var group in c.FaceOrder.Where(bodyFaces.Contains).Where(handle => withoutNodes || nodeByFace.ContainsKey(handle))
                     .GroupBy(handle => withoutNodes ? 0 : nodeByFace[handle].Node).Where(g => g.Key > 0 || withoutNodes))
        {
            bool curved = false, usable = true;
            // The surface integrals of the node. A closed node whose faces point into it (negative signed volume, a cutting body) is not a
            // solid the exporter takes: its measure keeps the negative volume, which the quantities read as "not taken"; its geometry is exported as ever.
            bool measureRuled = !measureSourceNodes && closedNodes.Contains(group.Key)
                && group.Any(handle => surfaces.TryGetValue(handle, out var s) && s.Kind == SurfKind.Ruled);
            var measured = MeasureAnalyticSolid(c, group, surfaces, loopsByFace, edges,
                measureSourceNodes || measureRuled, rawCoedges: measureRuled);
            if (measured == null && measureSourceNodes && closedNodes.Contains(group.Key))
                measured = MeasureEllipticalExtrusion(c, group, surfaces, loopsByFace, edges, volumeOnly: false);
            if (nodeMeasures != null && group.Key > 0 && measured is { } integrals
                && (integrals.Volume > 0 || integrals.Volume < 0 && !c.IncludeNonVisible && closedNodes.Contains(group.Key)))
                nodeMeasures.Add(new MeshNodeMeasure(group.Key, integrals.Area, integrals.Volume)
                {
                    SourceFaces = measureSourceNodes ? group.ToHashSet() : null,
                });
            if (MeshNodeLog)
                DiagnosticSink.WriteLine($"[meshnode] eid={c.ElementId} node={group.Key} active {string.Join(",", group.GroupBy(h => surfaces.TryGetValue(h, out var sx) ? sx.Kind.ToString() : "none").Select(g => $"{g.Key}:{g.Count()}"))}"
                    + $" inactive {(inactiveByNode.TryGetValue(group.Key, out var inx) ? string.Join(",", inx.GroupBy(h => c.DirectSurfaceByFace.TryGetValue(h, out var sy) ? sy.Kind.ToString() : "none").Select(g => $"{g.Key}:{g.Count()}")) : "-")}");
            // Planar nodes still contribute the analytic measures above, but emit no mesh.
            if (!MeshNodeLog && !meshCurvedPlanarEdges
                && group.All(h => surfaces.TryGetValue(h, out var surface) && surface.Kind == SurfKind.Plane)
                && (!inactiveByNode.TryGetValue(group.Key, out var inactivePlanar)
                    || inactivePlanar.All(h => c.DirectSurfaceByFace.TryGetValue(h, out var surface) && surface.Kind == SurfKind.Plane)))
                continue;
            if (MeshVertexModel) vertexTable ??= MeshVertexTable.Build(c, edges, surfaces, bodyFaces, loopsByFace);
            var faces = new List<BrFace>();
            foreach (uint handle in group)
            {
                if (!surfaces.TryGetValue(handle, out var surface) || !loopsByFace.TryGetValue(handle, out var loops)
                    || surface.Kind is not (SurfKind.Plane or SurfKind.Cylinder or SurfKind.Cone or SurfKind.Rev or SurfKind.Ruled or SurfKind.Hermite)
                    || BuildBrFace(handle, surface, MeshFlipped(c, handle, surface), loops, c, edges, surfaces, bodyFaces, vertexTable) is not { } face)
                {
                    if (MeshNodeLog)
                        DiagnosticSink.WriteLine($"[meshnode] eid={c.ElementId} node={group.Key} skip: face {handle} kind={(surfaces.TryGetValue(handle, out var failed) ? failed.Kind.ToString() : "none")}"
                            + $"{(surfaces.TryGetValue(handle, out failed) && failed.Kind is SurfKind.Plane or SurfKind.Cylinder or SurfKind.Rev ? " (edges not describable)" : "")}");
                    usable = false; break;
                }
                curved |= surface.Kind != SurfKind.Plane;
                faces.Add(face);
            }
            if (usable && !curved && meshCurvedPlanarEdges
                && faces.Any(f => f.Loops.Any(l => l.Coedges.Any(ce => ce.Curve is { Kind: not CurveKind.Line }))))
                curved = true;
            // Revit's isPlanar looks at every face of the node, the inactive ones included:
            // a node of planar active faces with a curved (or unclassified, so not planar) inactive
            // face is written as triangles all the same
            if (usable && !curved && inactiveByNode.TryGetValue(group.Key, out var inactiveFaces)
                && inactiveFaces.Any(h => !c.DirectSurfaceByFace.TryGetValue(h, out var sk) || sk.Kind != SurfKind.Plane))
                curved = true;
            if (!usable || !curved)
            {
                if (MeshNodeLog && usable)
                    DiagnosticSink.WriteLine($"[meshnode] eid={c.ElementId} node={group.Key} planar only ({faces.Count} faces) inactive "
                        + (inactiveByNode.TryGetValue(group.Key, out var inl) ? string.Join(",", inl.GroupBy(h => c.DirectSurfaceByFace.TryGetValue(h, out var sk0) ? sk0.Kind.ToString() : "none").Select(g => $"{g.Key}:{g.Count()}")) : "-"));
                continue;
            }
            // an inactive face of a kind the decoder does not classify: the node is left alone
            if (inactiveByNode.TryGetValue(group.Key, out var inactive)
                && inactive.Any(h => !c.DirectSurfaceByFace.TryGetValue(h, out var s) || s.Kind == SurfKind.Unknown))
            {
                if (MeshNodeLog) DiagnosticSink.WriteLine($"[meshnode] eid={c.ElementId} node={group.Key} inactive face of unknown kind");
                continue;
            }
            bool freeformNode = faces.Any(FreeformSolid.IsFreeform);
            if (freeformNode) foreach (var f in faces) f.Solid = faces;
            double diagonal = freeformNode ? FreeformSolid.CurveDiagonal(faces) : MeshNodeDiagonal(faces);
            if (!(diagonal > 0)) { if (MeshNodeLog) DiagnosticSink.WriteLine($"[meshnode] eid={c.ElementId} node={group.Key} no diagonal"); continue; }
            var display = displayOnlyNodes?.Where(n => n.Node == group.Key).Select(n => ((int, int)?)(n.Style, n.RenderStyleId)).FirstOrDefault();
            result.Add(new GeometryNode(group.Key, nodeOrders.GetValueOrDefault(group.Key - 1), faces, diagonal)
            {
                Ghost = display != null, Style = display?.Item1 ?? 0, RenderStyleId = display?.Item2 ?? 0,
                SourceMeasures = measured is { } m ? (m.Area, Math.Abs(m.Volume)) : null,
            });
        }
        if (MeshNodeLog)
            DiagnosticSink.WriteLine($"[meshnode] eid={c.ElementId} all nodes: " + string.Join(" ", nodeByFace.GroupBy(kv => kv.Value.Node).OrderBy(g => g.Key)
                .Select(g => $"{g.Key}:{g.Count()}/{g.Count(kv => bodyFaces.Contains(kv.Key))}")));
        if (MeshNodeLog && result.Count == 0)
            DiagnosticSink.WriteLine($"[meshnode] eid={c.ElementId} no node: {bodyFaces.Count} body faces, {bodyFaces.Count(nodeByFace.ContainsKey)} with a node, kinds "
                + string.Join(",", bodyFaces.GroupBy(h => surfaces.TryGetValue(h, out var s0) ? s0.Kind.ToString() : "none").Select(g => $"{g.Key}:{g.Count()}")));
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// The face as the mesher takes it: a planar face needs the sample points of its curved coedges
    /// (the renderer divides straight ones itself), the other kinds sample their edges themselves.
    /// </summary>
    internal static BrFace WithSamples(BrFace face, double lod, double diagonal, FreeformSolid? solid = null)
    {
        if (face.SurfaceKind != SurfaceKind.Plane) return face;
        return new BrFace
        {
            Marker = face.Marker, SurfaceKind = face.SurfaceKind, Flipped = face.Flipped, Plane = face.Plane,
            Loops = face.Loops.Select(l => new Loop
            {
                Coedges = l.Coedges.Select(ce => new MeshCoedge
                {
                    EdgeMarker = ce.EdgeMarker, V1 = ce.V1, V2 = ce.V2, Curve = ce.Curve, Start = ce.Start, End = ce.End,
                    UvStart = ce.UvStart, UvEnd = ce.UvEnd,
                    Samples = solid?.SharedEdgeSamples(ce.EdgeMarker) is { } shared ? Oriented(shared, ce.Start!, ce.End!) : ce.Curve switch
                    {
                        { Kind: CurveKind.Arc, Arc: { } arc } => ArcSampler.SampleCoedge(arc, ce.V1!, ce.V2!, ce.Start!, ce.End!, lod, diagonal),
                        { Kind: CurveKind.Nurbs, Nurbs: { } nurbs } => ArcSampler.SampleNurbsCoedge(nurbs, ce.Start!, ce.End!, lod, diagonal),
                        _ => null,
                    },
                }).ToList(),
            }).ToList(),
        };
    }

    private static double[][] Oriented(double[][] samples, double[] start, double[] end)
    {
        var pts = samples.Select(p => (double[])p.Clone()).ToArray();
        double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
        if (Dist(pts[^1], start) < Dist(pts[0], start)) Array.Reverse(pts);
        pts[0] = (double[])start.Clone(); pts[^1] = (double[])end.Clone();
        return pts;
    }

    /// <summary>
    /// Diagonal of the extents of the edge curves of the faces: end points of every coedge and the
    /// quarter points of the arcs.
    /// </summary>
    internal static double MeshNodeDiagonal(IEnumerable<BrFace> faces)
    {
        double[] min = [double.MaxValue, double.MaxValue, double.MaxValue], max = [double.MinValue, double.MinValue, double.MinValue];
        void Add(double[] p)
        {
            for (int k = 0; k < 3; k++) { min[k] = Math.Min(min[k], p[k]); max[k] = Math.Max(max[k], p[k]); }
        }
        foreach (var face in faces)
            foreach (var loop in face.Loops)
                foreach (var coedge in loop.Coedges)
                {
                    Add(coedge.Start!); Add(coedge.End!);
                    if (coedge.Curve is { Kind: CurveKind.Nurbs, Nurbs: { } nurbs })
                        for (int q = 0; q <= 32; q++)
                            Add(NurbsEval.Point3d(nurbs, nurbs.Knots[nurbs.Degree] + (nurbs.Knots[nurbs.ControlPoints.Length] - nurbs.Knots[nurbs.Degree]) * q / 32));
                    if (coedge.Curve is { Kind: CurveKind.Arc, Arc: { } arc })
                    {
                        var per = new[] { arc.Normal[1] * arc.Ref[2] - arc.Normal[2] * arc.Ref[1], arc.Normal[2] * arc.Ref[0] - arc.Normal[0] * arc.Ref[2], arc.Normal[0] * arc.Ref[1] - arc.Normal[1] * arc.Ref[0] };
                        for (int q = -16; q <= 16; q++)
                        {
                            double t = q * Math.PI / 2;
                            if (t < arc.Start || t > arc.End) continue;
                            Add(Enumerable.Range(0, 3).Select(k => arc.Center[k] + arc.Radius * Math.Cos(t) * arc.Ref[k] + arc.Radius * Math.Sin(t) * per[k]).ToArray());
                        }
                    }
                }
        return min[0] > max[0] ? 0 : Tolerances.ExtentsDiagonal(min, max);
    }
}

/// <summary>The surface integrals of a Geometry node (square feet, cubic feet): its area and its signed volume. A negative volume is that of a closed
/// node whose faces point into it; the exporter takes no solid from it.</summary>
internal sealed record MeshNodeMeasure(int Node, double Area, double Volume)
{
    internal IReadOnlySet<uint>? SourceFaces { get; init; }
}

/// <summary>
/// One Geometry node written as a triangle set: its faces in the terms of the Revit face
/// mesher, where the node stands in Revit's walk of the nodes, and the diagonal of its
/// extents (the maximum edge length of the mesher follows from it).
/// </summary>
internal sealed record GeometryNode(int Node, long NodeOrder, IReadOnlyList<BrFace> Faces, double Diagonal, double[]? Placement = null)
{
    /// <summary>The area and the volume of the source measures of the node; null when the node was not measured.</summary>
    public (double Area, double Volume)? SourceMeasures { get; init; }
    /// <summary>
    /// Revit exports the node as a solid, but the geometry holds no polygon of it: all its
    /// faces are display surfaces (GInfo bit 0x00200000) or its polygons duplicate those of another
    /// node (see GeometryData.GhostNodes).
    /// </summary>
    public bool Ghost { get; init; }
    public int Style { get; init; }
    public int RenderStyleId { get; init; }
}
