using RvtIO.Geometry.Meshing;
using RvtIO.Internal;
using MeshArcModel = RvtIO.Geometry.Meshing.Arc;

namespace RvtIO.Parsing;

internal static partial class GRepParserV2
{
    /// <summary>
    /// Marks the edges next to a ruled or Hermite face (<see cref="EdgeRec.FreeformShared"/>).
    /// </summary>
    internal static void MeshMarkFreeformEdges(Dictionary<uint, EdgeRec?> edges, Dictionary<uint, SurfaceRec> surfaces)
    {
        bool Freeform(uint face) => surfaces.TryGetValue(face, out var s) && s.Kind is SurfKind.Ruled or SurfKind.Hermite;
        foreach (var e in edges.Values)
            if (e != null) e.FreeformShared = Freeform(e.FaceA) || Freeform(e.FaceB);
    }

    private static BrFace? BuildBrFace(uint face, SurfaceRec surface, bool flipped, List<int> loopIndices,
        Cur c, Dictionary<uint, EdgeRec?> edges, Dictionary<uint, SurfaceRec> surfaces, HashSet<uint>? activeFaces = null, MeshVertexTable? vertices = null)
    {
        if (surface.Kind is not (SurfKind.Plane or SurfKind.Cylinder or SurfKind.Cone or SurfKind.Rev or SurfKind.Ruled or SurfKind.Hermite)) return null;
        bool cone = surface.Kind == SurfKind.Cone;
        // a cone is meshed by the cylinder rules with the cone frame; from here on `cylinder`
        // stands for both
        bool cylinder = surface.Kind is SurfKind.Cylinder or SurfKind.Cone, revolved = surface.Kind == SurfKind.Rev;
        bool freeform = surface.Kind is SurfKind.Ruled or SurfKind.Hermite;
        RuledSurface? ruledSurface = null; NurbsSurface? nurbsSurface = null; Func<double, double, double[]>? freeEval = null;
        if (freeform)
        {
            if (surface.Env is not { Length: 4 } || MeshFreeformEvaluator(c, face, surface) is not { } built) return null;
            (ruledSurface, nurbsSurface, freeEval) = built;
        }
        if (cylinder && (!(surface.R > 0.0) || surface.Env is not { Length: 4 } || (cone && !(surface.R < 1.56)))) return null;
        // a cylinder whose frame is left handed (Z against X x Y) is the surface of a hole: the
        // Revit takes the axis X x Y, the heights with the opposite sign and a negative axial
        // scale (the angles keep their meaning); the stored heights and uv keep the Z of the file
        bool holeSurface = cylinder && !cone && Dot3(MeshCross(surface.X, surface.Y), surface.Z) < 0;
        double zs = holeSurface ? -1.0 : 1.0;
        // a cone with a left handed frame: the same surface about the right handed axis X x Y with
        // the supplementary half angle
        var coneSurface = surface;
        bool holeCone = false;
        if (cone && Dot3(MeshCross(surface.X, surface.Y), surface.Z) < 0)
        {
            coneSurface = RightHanded(surface);
            coneSurface.R = Math.PI - surface.R;
            holeCone = true;
        }
        ArcRec profile = default;
        if (revolved && (surface.Env is not { Length: 4 } || !c.Curves.TryGetValue(surface.ProfileHandle, out profile) || !MeshProfileSupported(profile)))
        {
            if (MeshNodeLog && c.Curves.TryGetValue(surface.ProfileHandle, out var pr0))
                DiagnosticSink.WriteLine($"[meshrev] face {face}: profile not usable (env={surface.Env?.Length.ToString() ?? "null"}, R={pr0.R:G4} RY={pr0.RY:G4} nurbs={pr0.NurbControlPoints?.Count ?? 0} hermite={pr0.HermiteNodes?.Count ?? 0})");
            return null;
        }
            // the uv of the points of an edge on this Face and their 3D points: the Face's own
            // surface, then the Edge's first active Face, then the shared vertices; null for a
            // point that is not finite
            (List<double[]> Uv, List<double[]> Points)? EdgePointsOf(EdgeRec e, bool sideA, List<EdgeUV> uvs)
            {
                var uv = new List<double[]>(uvs.Count);
                foreach (var p in uvs)
                {
                    double u = sideA ? p.UA : p.UB, v = sideA ? p.VA : p.VB;
                    if (!double.IsFinite(u) || !double.IsFinite(v)) return null;
                    uv.Add([u, v]);
                }

                // Both paths prefer the edge's first active face. The current face is the fallback.
                List<double[]>? points = null;
                if (activeFaces != null && (vertices != null || surface.Kind == SurfKind.Plane)
                    && MeshEdgeEvaluationFace(e, face, activeFaces, surfaces) is { } evalFace && evalFace != face
                    && surfaces.TryGetValue(evalFace, out var evalSurface))
                {
                    bool evalA = e.FaceA == evalFace;
                    var evalPoints = new List<double[]>(uvs.Count);
                    for (int i = 0; i < uvs.Count; i++)
                    {
                        double eu = evalA ? uvs[i].UA : uvs[i].UB, ev = evalA ? uvs[i].VA : uvs[i].VB;
                        var evaluated = MeshEvaluate(c, evalFace, evalSurface, eu, ev);
                        if (evaluated == null) { evalPoints = null!; break; }
                        evalPoints.Add(evaluated);
                    }
                    if (evalPoints != null) points = evalPoints;
                }
                if (points == null)
                {
                    points = new List<double[]>(uv.Count);
                    foreach (var p in uv)
                        points.Add(freeform ? freeEval!(p[0], p[1]) : revolved ? MeshRevolvedPoint(surface, profile, p[0], p[1])
                            : cone ? MeshConePoint(surface, p[0], p[1])
                            : cylinder ? MeshCylinderPoint(surface, p[0], p[1])
                            : MeshPlanePoint(surface, p[0], p[1]));
                }
                if (vertices != null && activeFaces != null)
                {
                    if (vertices.TryGet(e.Handle, 0, out var vertexFirst)) points[0] = vertexFirst;
                    if (vertices.TryGet(e.Handle, 1, out var vertexLast)) points[^1] = vertexLast;
                }
                return (uv, points);
            }
        var loops = new List<Loop>();
        double outerArea = double.NaN;
        // a planar loop made of closed-curve halves (no end decides its direction) runs the way its
        // nesting depth says: an outer loop counter-clockwise in the plane of an unflipped face, a
        // hole the other way round
        var loopDepth = surface.Kind == SurfKind.Plane && loopIndices.Count > 1 ? MeshLoopDepths(c, edges, face, loopIndices) : null;
        int loopNumber = -1;
        foreach (int index in loopIndices)
        {
            loopNumber++;
            var loop = c.SpaceEdgeLoops[index];
            uint sentinel = (uint)c.Queue[index].V, current = loop.Next;
            var raw = new List<(EdgeRec Edge, bool SideA, List<double[]> Points, List<double[]> Uv, Func<List<double[]>?>? Legacy)>();
            int guard = 0;
            while (current != sentinel && guard++ < 100000)
            {
                if (!edges.TryGetValue(current, out var e) || e == null || !e.HasEndpoints)
                {
                    if (MeshNodeLog) DiagnosticSink.WriteLine($"[meshrev] face {face} kind={surface.Kind}: edge {current} has no endpoints");
                    return null;
                }
                bool sideA = e.FaceA == face;
                if (EdgePointsOf(e, sideA, e.AuthoredPoints()) is not { } built) return null;
                var (uv, points) = built;
                Func<List<double[]>?>? legacy = MeshLegacyCylinderSplines && e.RawInterior != null && !e.FreeformShared
                    && (surfaces.TryGetValue(e.FaceA, out var sa) && sa.Kind == SurfKind.Cylinder || surfaces.TryGetValue(e.FaceB, out var sb) && sb.Kind == SurfKind.Cylinder)
                    ? () => EdgePointsOf(e, sideA, e.FreeformPoints())?.Points : null;
                if (MeshPointsDump)
                    DiagnosticSink.WriteLine($"[meshpts] eid={c.ElementId} face={face} kind={surface.Kind} edge={e.Handle} flags={e.Flags} other={(sideA ? e.FaceB : e.FaceA)} sideA={sideA} pts="
                        + string.Join(";", points.Select(q => string.Join(",", q.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))))));
                raw.Add((e, sideA, points, uv, legacy));
                current = sideA ? e.NextA : e.NextB;
            }
            if (raw.Count == 0) { if (MeshNodeLog) DiagnosticSink.WriteLine($"[meshrev] face {face} kind={surface.Kind}: empty loop"); return null; }
            // walking direction of every coedge: the end that meets the neighbouring coedge
            var coedges = new List<MeshCoedge>();
            var forward = new bool[raw.Count];
            // The Edge flag bit 0 says which way the Edge runs in the loop of each of its Faces
            // (at the last end the next Edge of side A and the
            // previous one of side B follow, with the bit set the ends swap).
            if (MeshCoedgeFlagDirection)
            {
                for (int i = 0; i < raw.Count; i++) forward[i] = MeshCoedgeForward(raw[i].Edge, raw[i].SideA);
            }
            else
            {
                // anchor: a coedge whose direction the neighbour decides unambiguously; the others
                // follow the previous end
                int anchor = -1;
                for (int i = 0; i < raw.Count && anchor < 0; i++)
                {
                    var a = raw[i].Points; var next = raw[(i + 1) % raw.Count].Points;
                    double fwd = Math.Min(Dist3(a[^1], next[0]), Dist3(a[^1], next[^1]));
                    double bwd = Math.Min(Dist3(a[0], next[0]), Dist3(a[0], next[^1]));
                    if (Math.Abs(fwd - bwd) > 1e-9) { forward[i] = fwd < bwd; anchor = i; }
                }
                bool ambiguous = anchor < 0;
                if (ambiguous) { anchor = 0; forward[0] = raw[0].SideA; }
                for (int step = 1; step < raw.Count; step++)
                {
                    int i = (anchor + step) % raw.Count, prev = (i + raw.Count - 1) % raw.Count;
                    var prevEnd = forward[prev] ? raw[prev].Points[^1] : raw[prev].Points[0];
                    forward[i] = Dist3(raw[i].Points[0], prevEnd) <= Dist3(raw[i].Points[^1], prevEnd);
                }
                if (ambiguous && surface.Kind == SurfKind.Plane)
                {
                    // the two ends of every coedge are shared (a closed curve in halves): the outer
                    // loop of an unflipped planar face runs counter-clockwise in the plane, that of
                    // a flipped face clockwise
                    double area = 0;
                    var walkUv = new List<double[]>();
                    for (int i = 0; i < raw.Count; i++)
                        walkUv.AddRange((forward[i] ? raw[i].Uv : Enumerable.Reverse(raw[i].Uv).ToList()).Skip(walkUv.Count == 0 ? 0 : 1));
                    for (int i = 0; i < walkUv.Count; i++)
                    {
                        var q0 = walkUv[i]; var q1 = walkUv[(i + 1) % walkUv.Count];
                        area += q0[0] * q1[1] - q1[0] * q0[1];
                    }
                    bool hole = loopDepth != null ? loopDepth[loopNumber] % 2 == 1 : loops.Count > 0;
                    bool wantPositive = hole ? flipped : !flipped;
                    if (Math.Abs(area) > 1e-18 && (area > 0) != wantPositive) for (int i = 0; i < raw.Count; i++) forward[i] = !forward[i];
                }
            }
            for (int i = 0; i < raw.Count; i++)
            {
                var (e, sideA, pts, uv, legacy) = raw[i];
                var start = forward[i] ? pts[0] : pts[^1];
                var end = forward[i] ? pts[^1] : pts[0];
                var uvStart = forward[i] ? uv[0] : uv[^1];
                var uvEnd = forward[i] ? uv[^1] : uv[0];
                MeshCurve curve;
                double paramV1 = 0, paramV2 = 1;
                ParamCurve? paramCurve = null;
                if (revolved && Arc3Mode >= 1 && (e.Flags & 8) != 0 && TryCylinderNeighbourArc(e, sideA, surfaces, activeFaces, out var revCylArc)
                    && (MeshRevolvedCylinderArc || uv.All(q => Math.Abs(q[0] - uv[0][0]) < 1e-9)))
                {
                    curve = new MeshCurve { Kind = CurveKind.Arc, Lo = revCylArc.Start, Hi = revCylArc.End, P0 = pts[0], P1 = pts[^1], Arc = revCylArc };
                    paramV1 = revCylArc.Start; paramV2 = revCylArc.End;
                }
                else if (revolved && Arc3Mode >= 1 && TryThreePointArc(e, sideA, pts, surfaces, activeFaces, out var revArc3)
                    && (Arc3Mode >= 2 || uv.All(q => Math.Abs(q[0] - uv[0][0]) < 1e-9)))
                {
                    curve = new MeshCurve { Kind = CurveKind.Arc, Lo = revArc3.Start, Hi = revArc3.End, P0 = pts[0], P1 = pts[^1], Arc = revArc3 };
                    paramV1 = revArc3.Start; paramV2 = revArc3.End;
                }
                else if (revolved)
                {
                    if (!TryRevolvedCurve(surface, profile, uv, pts, out curve, out paramV1, out paramV2)
                        && !TryFreeformEdge(e, pts, uv, out curve, out paramCurve, out paramV1, out paramV2))
                    {
                        if (MeshNodeLog)
                            DiagnosticSink.WriteLine($"[meshedge] face kind=Rev edge flags={e.Flags} samples={pts.Count} uvrot={(uv.Max(q => q[0]) - uv.Min(q => q[0])):G3} uvprof={(uv.Max(q => q[1]) - uv.Min(q => q[1])):G3} profileR={profile.R:G3}");
                        return null;
                    }
                }
                else if (freeform)
                {
                    if (!TryFreeformEdge(e, pts, uv, out curve, out paramCurve, out paramV1, out paramV2)) return null;
                }
                else if (IsStraight(pts))
                    curve = new MeshCurve { Kind = CurveKind.Line, Lo = 0, Hi = Dist3(pts[0], pts[^1]), P0 = pts[0], P1 = pts[^1] };
                // the Faces of the Edge are asked in order (FaceA first) for their isoparametric
                // circle: a cone asks no circle, so the cylinder beside it answers, and so does the
                // cylinder that is FaceA of a second cylinder
                else if (cylinder && MeshFirstCylinderArc && (e.Flags & 8) != 0 && (cone || !sideA)
                    && TryCylinderNeighbourArc(e, sideA, surfaces, activeFaces, out var firstCylinderArc))
                {
                    curve = new MeshCurve { Kind = CurveKind.Arc, Lo = firstCylinderArc.Start, Hi = firstCylinderArc.End, P0 = pts[0], P1 = pts[^1], Arc = firstCylinderArc };
                    paramV1 = firstCylinderArc.Start; paramV2 = firstCylinderArc.End;
                }
                // a cone is not cylindrical for Revit: its arc edge
                // is the circle through three points, like any other
                else if (cone && Arc3Mode >= 2 && (e.Flags & 8) != 0 && TryThreePointArc(e, sideA, pts, surfaces, activeFaces, out var coneArc3))
                {
                    curve = new MeshCurve { Kind = CurveKind.Arc, Lo = coneArc3.Start, Hi = coneArc3.End, P0 = pts[0], P1 = pts[^1], Arc = coneArc3 };
                    paramV1 = coneArc3.Start; paramV2 = coneArc3.End;
                }
                else if (cylinder && (e.Flags & 8) != 0 && (cone ? TryConeArc(coneSurface, uv, out var carc)
                    : holeSurface ? TryCylinderArc(RightHanded(surface), uv.Select(q => new[] { q[0], -q[1] }).ToList(), out carc) : TryCylinderArc(surface, uv, out carc)))
                {
                    curve = new MeshCurve { Kind = CurveKind.Arc, Lo = carc.Start, Hi = carc.End, P0 = pts[0], P1 = pts[^1], Arc = carc };
                    paramV1 = carc.Start; paramV2 = carc.End;
                }
                else if (!cylinder && !revolved && !freeform && Arc3Mode >= 1 && !(MeshIsCylinderArc(e, sideA, surface, surfaces, face)) && TryThreePointArc(e, sideA, pts, surfaces, activeFaces, out var planeArc3))
                    curve = new MeshCurve { Kind = CurveKind.Arc, Lo = planeArc3.Start, Hi = planeArc3.End, P0 = pts[0], P1 = pts[^1], Arc = planeArc3 };
                else if (!cylinder && !revolved && !freeform && TryArcOfRevolvedNeighbour(c, e, sideA, surfaces, pts, out curve, out paramV1, out paramV2)) { }
                else if (!cylinder && TryArc(e, sideA, surface, surfaces, face, out var arc))
                    curve = new MeshCurve { Kind = CurveKind.Arc, Lo = arc.Start, Hi = arc.End, P0 = pts[0], P1 = pts[^1], Arc = arc };
                else if (!cylinder && !revolved && !freeform && TryArcOfConeNeighbour(e, sideA, surface, surfaces, out var coneNeighbourArc))
                    curve = new MeshCurve { Kind = CurveKind.Arc, Lo = coneNeighbourArc.Start, Hi = coneNeighbourArc.End, P0 = pts[0], P1 = pts[^1], Arc = coneNeighbourArc };
                else if (!cylinder && TryFittedArc(e, pts, out var fitted))
                    curve = new MeshCurve { Kind = CurveKind.Arc, Lo = fitted.Start, Hi = fitted.End, P0 = pts[0], P1 = pts[^1], Arc = fitted };
                // the samples of the edge are too coarse for a circle (a half circle in a few
                // chords): the circle of the revolved neighbour with a line or circle profile
                else if (!cylinder && !revolved && !freeform && TryArcOfRevolvedNeighbour(c, e, sideA, surfaces, pts, out curve, out paramV1, out paramV2, simpleProfile: true)) { }
                else if (TryNurbsCurve(legacy?.Invoke() ?? pts, out curve, out paramV1, out paramV2)) { }
                else
                {
                    if (MeshNodeLog)
                    {
                        uint other = sideA ? e.FaceB : e.FaceA;
                        DiagnosticSink.WriteLine($"[meshedge] face kind={surface.Kind} edge flags={e.Flags} samples={pts.Count} other={(surfaces.TryGetValue(other, out var os) ? os.Kind.ToString() : "?")}");
                    }
                    return null;
                }
                if (MeshNodeLog && Environment.GetEnvironmentVariable("RVTIO_DBGFACE") == face.ToString())
                    DiagnosticSink.WriteLine($"[meshedge] face {face} edge {e.Handle} flags={e.Flags} samples={pts.Count} curve={curve.Kind}"
                        + (curve.Arc is { } dbgArc ? $" R={dbgArc.Radius:G6} start={dbgArc.Start:G17} end={dbgArc.End:G17}" : ""));
                if (cylinder && curve.Kind == CurveKind.Line) curve = new MeshCurve { Kind = CurveKind.Line, Lo = 0, Hi = 1, P0 = pts[0], P1 = pts[^1] };
                coedges.Add(new MeshCoedge
                {
                    EdgeMarker = (int)e.Handle, V1 = pts[0], V2 = pts[^1], Curve = curve, ParamV1 = paramV1, ParamV2 = paramV2, OrientToLoop = forward[i],
                    Start = start, End = end, UvStart = uvStart, UvEnd = uvEnd,
                    UvFit = revolved ? DistinctUv(uv) : null, ParamCurve = paramCurve,
                });
            }
            if (double.IsNaN(outerArea))
            {
                // signed uv area of the first (outer) loop in walking order: negative = the face
                // normal is opposite the surface normal
                var walk = new List<double[]>();
                for (int i = 0; i < raw.Count; i++)
                {
                    var part = forward[i] ? raw[i].Uv : Enumerable.Reverse(raw[i].Uv).ToList();
                    walk.AddRange(part.Skip(walk.Count == 0 ? 0 : 1));
                }
                outerArea = 0;
                for (int i = 0; i < walk.Count; i++)
                {
                    var p = walk[i]; var q = walk[(i + 1) % walk.Count];
                    outerArea += p[0] * q[1] - q[0] * p[1];
                }
            }
            loops.Add(new Loop { Coedges = coedges });
        }
        if (freeform)
            return new BrFace
            {
                Marker = (int)face, SurfaceKind = ruledSurface != null ? SurfaceKind.Ruled : SurfaceKind.NurbsSurface, Flipped = flipped, OrientToSurface = !flipped,
                Envelope = surface.Env, Ruled = ruledSurface, Nurbs = nurbsSurface, Loops = loops,
            };
        if (revolved)
            return new BrFace
            {
                Marker = (int)face, SurfaceKind = SurfaceKind.Revolved, Flipped = flipped, OrientToSurface = !flipped,
                Revolved = MeshRevolvedSurfaceOf(surface, profile, MeshNormalReversed),
                Loops = loops,
            };
        if (cylinder)
            return new BrFace
            {
                Marker = (int)face, SurfaceKind = cone ? SurfaceKind.Cone : SurfaceKind.Cylinder, Flipped = flipped, OrientToSurface = holeCone ? flipped : !flipped,
                Cylinder = new CylinderSurface
                {
                    Radius = cone ? 1.0 : surface.R, Origin = surface.O, Axis = holeSurface ? MeshUnit(MeshCross(surface.X, surface.Y)) : cone ? coneSurface.Z : surface.Z, Ref = surface.X,
                    // the cone of a hole: Revit measures the height from the base towards
                    // the apex, so the axial parameter runs against the slant distance
                    UScale = cone ? (holeCone ? -1.0 : 1.0) : zs * surface.R,
                    AngleLo = surface.Env![0], HeightLo = holeCone ? surface.Env[3] : zs * surface.Env[1],
                    AngleHi = surface.Env[2], HeightHi = holeCone ? surface.Env[1] : zs * surface.Env[3], Outer = !holeSurface && !holeCone,
                    ConeHalfAngle = cone ? coneSurface.R : 0.0,
                },
                Loops = loops,
            };
        return new BrFace
        {
            Marker = (int)face, SurfaceKind = SurfaceKind.Plane, Flipped = flipped,
            Plane = new PlaneSurface { Origin = surface.O, U = surface.X, V = surface.Y },
            Loops = loops,
        };
    }

    /// <summary>
    /// The nesting depth of every loop of a planar face (how many other loops enclose it), from the
    /// uv polygons of the loops as the Edges list them; null when a loop cannot be assembled.
    /// </summary>
    private static int[]? MeshLoopDepths(Cur c, Dictionary<uint, EdgeRec?> edges, uint face, List<int> loopIndices)
    {
        var polygons = new List<List<double[]>>();
        foreach (int index in loopIndices)
        {
            var loop = c.SpaceEdgeLoops[index];
            uint sentinel = (uint)c.Queue[index].V, current = loop.Next;
            var chains = new List<List<double[]>>();
            int guard = 0;
            while (current != sentinel && guard++ < 100000)
            {
                if (!edges.TryGetValue(current, out var e) || e == null || !e.HasEndpoints) return null;
                bool sideA = e.FaceA == face;
                var uvs = e.AuthoredPoints();
                chains.Add(uvs.Select(p => sideA ? new[] { p.UA, p.VA } : new[] { p.UB, p.VB }).ToList());
                current = sideA ? e.NextA : e.NextB;
            }
            if (chains.Count == 0) return null;
            if (chains.Count > 1)
            {
                // the first chain runs towards the second one
                static double Gap(double[] a, double[] b) => Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]);
                var first = chains[0]; var second = chains[1];
                double forward = Math.Min(Gap(first[^1], second[0]), Gap(first[^1], second[^1]));
                double backward = Math.Min(Gap(first[0], second[0]), Gap(first[0], second[^1]));
                if (backward < forward) chains[0] = Enumerable.Reverse(first).ToList();
            }
            var polygon = new List<double[]>(chains[0]);
            for (int i = 1; i < chains.Count; i++)
            {
                var next = chains[i];
                var end = polygon[^1];
                double dFirst = Math.Abs(next[0][0] - end[0]) + Math.Abs(next[0][1] - end[1]);
                double dLast = Math.Abs(next[^1][0] - end[0]) + Math.Abs(next[^1][1] - end[1]);
                if (dLast < dFirst) next = Enumerable.Reverse(next).ToList();
                polygon.AddRange(next.Skip(1));
            }
            if (polygon.Any(p => !double.IsFinite(p[0]) || !double.IsFinite(p[1]))) return null;
            polygons.Add(polygon);
        }
        static bool Inside(List<double[]> polygon, double[] p)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                if ((polygon[i][1] > p[1]) != (polygon[j][1] > p[1])
                    && p[0] < (polygon[j][0] - polygon[i][0]) * (p[1] - polygon[i][1]) / (polygon[j][1] - polygon[i][1]) + polygon[i][0])
                    inside = !inside;
            return inside;
        }
        var depth = new int[polygons.Count];
        for (int i = 0; i < polygons.Count; i++)
            for (int j = 0; j < polygons.Count; j++)
                if (i != j && Inside(polygons[j], polygons[i][0])) depth[i]++;
        return depth;
    }

    /// <summary>
    /// The Face is flipped against its surface when bit 1 of GFace.m_faceFlags_v9 equals
    /// Surface.m_orientFlag (the orientation of the face is the two differing); false for a Face
    /// the flags of which were not read.
    /// </summary>
    private static bool MeshFlipped(Cur c, uint face, SurfaceRec surface) =>
        c.FaceFlagsV9.TryGetValue(face, out uint flags) && MeshFlippedBits(flags, surface.Orient);

    internal static bool MeshFlippedBits(uint faceFlags, byte orient) => (((faceFlags >> 1) & 1) == orient);

    /// <summary>
    /// The Face an Edge is evaluated on: the first of its two Faces (FaceA, face[0]) that is an active body Face with a surface the decoder evaluates, else the second.
    /// </summary>
    internal static uint? MeshEdgeEvaluationFace(EdgeRec e, uint face, HashSet<uint> activeFaces, Dictionary<uint, SurfaceRec> surfaces)
    {
        foreach (uint candidate in new[] { e.FaceA, e.FaceB })
            if (activeFaces.Contains(candidate) && surfaces.TryGetValue(candidate, out var s)
                && s.Kind is SurfKind.Plane or SurfKind.Cylinder or SurfKind.Rev or SurfKind.Cone or SurfKind.Ruled or SurfKind.Hermite)
                return candidate;
        return null;
    }

    /// <summary>
    /// The point of any surface kind the decoder takes, in the formula of the Face's own points (a
    /// cone through the slant distance, the free form faces through their evaluator).
    /// </summary>
    private static double[]? MeshEvaluate(Cur c, uint face, SurfaceRec s, double u, double v)
    {
        switch (s.Kind)
        {
            case SurfKind.Cone:
                return MeshConePoint(s, u, v);
            case SurfKind.Ruled:
            case SurfKind.Hermite:
                return s.Env is { Length: 4 } && MeshFreeformEvaluator(c, face, s) is { } built ? built.Item3(u, v) : null;
            default:
                return MeshEvaluate(c, s, u, v);
        }
    }

    private static double[]? MeshEvaluate(Cur c, SurfaceRec s, double u, double v)
    {
        switch (s.Kind)
        {
            case SurfKind.Plane:
                return MeshPlanePoint(s, u, v);
            case SurfKind.Cylinder:
                return MeshCylinderPoint(s, u, v);
            case SurfKind.Rev:
                return c.Curves.TryGetValue(s.ProfileHandle, out var profile) && MeshProfileSupported(profile) ? MeshRevolvedPoint(s, profile, u, v) : null;
            default:
                return null;
        }
    }

    internal static double[] MeshPlanePoint(SurfaceRec s, double u, double v) =>
        [s.O[0] + u * s.X[0] + v * s.Y[0],
         s.O[1] + u * s.X[1] + v * s.Y[1],
         s.O[2] + u * s.X[2] + v * s.Y[2]];

    /// <summary>
    /// RVTIO_X_EVAL=0 returns the point formulas of the surfaces to the plain sums.
    /// </summary>
    private static readonly bool CompactEvaluation = Environment.GetEnvironmentVariable("RVTIO_X_EVAL") != "0";

    /// <summary>
    /// The point of a cylinder at (angle u, height v) in the rounding order of Revit's
    /// surface evaluation: the unit radial direction cos u X + sin u Y first, scaled by R, then the
    /// origin, then v Z.
    /// </summary>
    internal static double[] MeshCylinderPoint(SurfaceRec s, double u, double v)
    {
        double c = Math.Cos(u), sn = Math.Sin(u);
        if (!CompactEvaluation)
            return [s.O[0] + s.R * c * s.X[0] + s.R * sn * s.Y[0] + v * s.Z[0], s.O[1] + s.R * c * s.X[1] + s.R * sn * s.Y[1] + v * s.Z[1],
                    s.O[2] + s.R * c * s.X[2] + s.R * sn * s.Y[2] + v * s.Z[2]];
        return [((c * s.X[0] + sn * s.Y[0]) * s.R + s.O[0]) + v * s.Z[0],
                ((c * s.X[1] + sn * s.Y[1]) * s.R + s.O[1]) + v * s.Z[1],
                ((c * s.X[2] + sn * s.Y[2]) * s.R + s.O[2]) + v * s.Z[2]];
    }

    /// <summary>
    /// The point of a cone (apex O, half angle R) at (angle u, slant distance v): the unit
    /// direction of the generator (cos u X + sin u Y) sin a + cos a Z times v, plus the apex.
    /// </summary>
    internal static double[] MeshConePoint(SurfaceRec s, double u, double v)
    {
        double c = Math.Cos(u), sn = Math.Sin(u), sa = Math.Sin(s.R), ca = Math.Cos(s.R);
        if (!CompactEvaluation)
            return [s.O[0] + v * sa * (c * s.X[0] + sn * s.Y[0]) + v * ca * s.Z[0], s.O[1] + v * sa * (c * s.X[1] + sn * s.Y[1]) + v * ca * s.Z[1],
                    s.O[2] + v * sa * (c * s.X[2] + sn * s.Y[2]) + v * ca * s.Z[2]];
        return [(((c * s.X[0] + sn * s.Y[0]) * sa + ca * s.Z[0]) * v) + s.O[0],
                (((c * s.X[1] + sn * s.Y[1]) * sa + ca * s.Z[1]) * v) + s.O[1],
                (((c * s.X[2] + sn * s.Y[2]) * sa + ca * s.Z[2]) * v) + s.O[2]];
    }

    private static double Dist3(double[] a, double[] b) =>
        Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

    private static bool IsStraight(List<double[]> points)
    {
        double length = Dist3(points[0], points[^1]);
        if (points.Count == 2) return true;
        if (!(length > 0)) return false;
        foreach (var p in points)
        {
            double t = ((p[0] - points[0][0]) * (points[^1][0] - points[0][0]) + (p[1] - points[0][1]) * (points[^1][1] - points[0][1])
                + (p[2] - points[0][2]) * (points[^1][2] - points[0][2])) / (length * length);
            for (int k = 0; k < 3; k++)
                if (Math.Abs(points[0][k] + t * (points[^1][k] - points[0][k]) - p[k]) > 1e-9) return false;
        }
        return true;
    }

    /// <summary>
    /// The circle of an edge between a plane and a cylinder square to it (other side), as P(t) = C
    /// + R cos t X + R sin t Y.
    /// </summary>
    internal static bool TryArc(EdgeRec e, bool sideA, SurfaceRec plane, Dictionary<uint, SurfaceRec> surfaces, uint face, out MeshArcModel arc)
    {
        arc = null!;
        uint other = sideA ? e.FaceB : e.FaceA;
        if (!surfaces.TryGetValue(other, out var cyl) || cyl.Kind != SurfKind.Cylinder) return false;
        if (Math.Abs(Dot3(plane.X, cyl.Z)) > 1e-10 || Math.Abs(Dot3(plane.Y, cyl.Z)) > 1e-10) return false;
        var uvs = e.AuthoredPoints();
        double U(EdgeUV p) => sideA ? p.UB : p.UA;
        double V(EdgeUV p) => sideA ? p.VB : p.VA;
        double first = U(uvs[0]), last = U(uvs[^1]), height = V(uvs[0]);
        if (!double.IsFinite(first) || !double.IsFinite(last) || !double.IsFinite(height) || first == last) return false;
        if (uvs.Any(p => Math.Abs(V(p) - height) > 1e-10)) return false;
        var normal = new[] { cyl.X[1] * cyl.Y[2] - cyl.X[2] * cyl.Y[1], cyl.X[2] * cyl.Y[0] - cyl.X[0] * cyl.Y[2], cyl.X[0] * cyl.Y[1] - cyl.X[1] * cyl.Y[0] };
        double n = Math.Sqrt(Dot3(normal, normal));
        if (!(n > 0)) return false;
        double radius = Math.Abs(cyl.R);
        arc = new MeshArcModel
        {
            Center = Enumerable.Range(0, 3).Select(k => cyl.O[k] + height * cyl.Z[k]).ToArray(),
            // the circle runs from the first to the last point of the edge with a growing parameter
            // (a falling angle: mirrored circle, t = 2 pi - angle, the reversed arc)
            Radius = radius, Normal = normal.Select(v => last > first ? v / n : -v / n).ToArray(), Ref = (double[])cyl.X.Clone(),
            Start = last > first ? first : ArcSampler.TwoPi - first, End = last > first ? last : ArcSampler.TwoPi - last,
        };
        return true;
    }

    /// <summary>
    /// The edge is the circle of an active cylinder beside the face (an edge of constant height on
    /// the cylinder): Revit takes the curve of that cylinder face for the edge, whatever
    /// kind of face the edge is walked on.
    /// </summary>
    internal static bool TryCylinderNeighbourArc(EdgeRec e, bool sideA, Dictionary<uint, SurfaceRec> surfaces, HashSet<uint>? activeFaces, out MeshArcModel arc)
    {
        arc = null!;
        uint other = sideA ? e.FaceB : e.FaceA;
        if (!surfaces.TryGetValue(other, out var cyl) || cyl.Kind != SurfKind.Cylinder || (activeFaces != null && !activeFaces.Contains(other))) return false;
        var uvs = e.AuthoredPoints();
        var uv = uvs.Select(p => sideA ? new[] { p.UB, p.VB } : new[] { p.UA, p.VA }).ToList();
        if (uv.Any(p => !double.IsFinite(p[0]) || !double.IsFinite(p[1]))) return false;
        bool hole = Dot3(MeshCross(cyl.X, cyl.Y), cyl.Z) < 0;
        return hole ? TryCylinderArc(RightHanded(cyl), uv.Select(q => new[] { q[0], -q[1] }).ToList(), out arc) : TryCylinderArc(cyl, uv, out arc);
    }

    /// <summary>
    /// The direction of an Edge in the loop of one of its Faces:
    /// along its points (first end to last end) for side A with bit 0 of the Edge flags clear and
    /// for side B with it set, against them otherwise.
    /// </summary>
    internal static bool MeshCoedgeForward(EdgeRec e, bool sideA) => sideA ? (e.Flags & 1) == 0 : (e.Flags & 1) != 0;

    /// <summary>
    /// RVTIO_X_COEDGEDIR=0 walks every coedge the way the continuity of its end points says instead
    /// of the way the Edge flag says.
    /// </summary>
    private static readonly bool MeshCoedgeFlagDirection = Environment.GetEnvironmentVariable("RVTIO_X_COEDGEDIR") != "0";

    /// <summary>
    /// RVTIO_X_NURBSCYL=0 fits the spline of every edge of a cylinder to the stored list (see the
    /// comment where it is used).
    /// </summary>
    private static readonly bool MeshLegacyCylinderSplines = Environment.GetEnvironmentVariable("RVTIO_X_NURBSCYL") != "0";

    private static readonly bool MeshPointsDump = Environment.GetEnvironmentVariable("RVTIO_PTS") == "1";

    /// <summary>
    /// A revolved face beside an active cylinder takes the circle of the cylinder for the shared
    /// Edge whatever its own parameters do along the Edge (the first
    /// active cylindrical Face of the Edge for its isoparametric circle).
    /// </summary>
    private static readonly bool MeshRevolvedCylinderArc = Environment.GetEnvironmentVariable("RVTIO_X_REVCYL") != "0";

    /// <summary>
    /// The circle of a cylinder takes the unit x vector and the cross product of the unit x and y
    /// vectors for its frame; RVTIO_X_UFRAME=0 keeps the stored vectors.
    /// </summary>
    private static readonly bool MeshUnitFrame = Environment.GetEnvironmentVariable("RVTIO_X_UFRAME") != "0";

    /// <summary>
    /// A cone, or the second of two cylinders, takes the isoparametric circle of the active
    /// cylinder that comes first on the Edge; RVTIO_X_CYL1=0 switches it off.
    /// </summary>
    private static readonly bool MeshFirstCylinderArc = Environment.GetEnvironmentVariable("RVTIO_X_CYL1") != "0";

    /// <summary>
    /// RVTIO_X_FITARC=0 lets the generic circle fit take every edge whose samples lie on a circle.
    /// </summary>
    private static readonly bool MeshFitNeedsArcFlag = Environment.GetEnvironmentVariable("RVTIO_X_FITARC") != "0";

    /// <summary>
    /// RVTIO_X_ARC3: 0 the circles of the first port, 1 the circle of an arc edge of a revolved
    /// face only when the edge is a meridian (the rotation does not change along it).
    /// </summary>
    private static readonly int Arc3Mode = int.TryParse(Environment.GetEnvironmentVariable("RVTIO_X_ARC3"), out var arc3Mode) ? arc3Mode : 2;

    /// <summary>
    /// The edge is the isoparametric circle of an active cylinder beside the plane (the plane
    /// square to the cylinder axis): Revit takes the circle of that cylinder face for it.
    /// </summary>
    private static bool MeshIsCylinderArc(EdgeRec e, bool sideA, SurfaceRec plane, Dictionary<uint, SurfaceRec> surfaces, uint face) =>
        TryArc(e, sideA, plane, surfaces, face, out _);

    /// <summary>
    /// An arc edge (flag 8) with no active cylinder beside it: the circular arc is set from (first point,
    /// first interior point, last point), see <see cref="ThreePointArc"/>.
    /// </summary>
    private static bool TryThreePointArc(EdgeRec e, bool sideA, List<double[]> pts, Dictionary<uint, SurfaceRec> surfaces, HashSet<uint>? activeFaces,
        out MeshArcModel arc)
    {
        arc = null!;
        if ((e.Flags & 8) == 0 || pts.Count < 3)
        {
            if (MeshNodeLog && (e.Flags & 8) != 0) DiagnosticSink.WriteLine($"[mesharc3] refused: flags={e.Flags} samples={pts.Count}");
            return false;
        }
        uint other = sideA ? e.FaceB : e.FaceA;
        if (surfaces.TryGetValue(other, out var neighbour) && neighbour.Kind == SurfKind.Cylinder && (activeFaces == null || activeFaces.Contains(other)))
        {
            if (MeshNodeLog) DiagnosticSink.WriteLine("[mesharc3] refused: active cylinder beside");
            return false;
        }
        var made = ThreePointArc.Create(pts[0], pts[1], pts[^1]);
        if (made == null)
        {
            if (MeshNodeLog) DiagnosticSink.WriteLine("[mesharc3] refused: no circle through the points");
            return false;
        }
        arc = made;
        return true;
    }

    /// <summary>
    /// An edge of a plane square to the axis of the cone on its other side: the circle of the cone
    /// at the slant distance of the edge.
    /// </summary>
    private static bool TryArcOfConeNeighbour(EdgeRec e, bool sideA, SurfaceRec plane, Dictionary<uint, SurfaceRec> surfaces, out MeshArcModel arc)
    {
        arc = null!;
        uint other = sideA ? e.FaceB : e.FaceA;
        if (!surfaces.TryGetValue(other, out var cone) || cone.Kind != SurfKind.Cone || !(cone.R > 0.0 && cone.R < 1.56)) return false;
        var axis = MeshUnit(cone.Z);
        if (Math.Abs(Math.Abs(Dot3(MeshUnit(MeshCross(plane.X, plane.Y)), axis)) - 1.0) > 1e-9) return false;
        var coneSurface = cone;
        if (Dot3(MeshCross(cone.X, cone.Y), cone.Z) < 0)
        {
            coneSurface = RightHanded(cone);
            coneSurface.R = Math.PI - cone.R;
        }
        var uvs = e.AuthoredPoints();
        var uv = uvs.Select(p => sideA ? new[] { p.UB, p.VB } : new[] { p.UA, p.VA }).ToList();
        return TryConeArc(coneSurface, uv, out arc);
    }

    /// <summary>
    /// An edge of a plane shared with a revolved face whose profile is neither a line nor a circle:
    /// the circle around the axis at the constant profile parameter of the edge on the revolved
    /// face (the revolved face's own curve of that edge).
    /// </summary>
    private static bool TryArcOfRevolvedNeighbour(Cur c, EdgeRec e, bool sideA, Dictionary<uint, SurfaceRec> surfaces, List<double[]> pts,
        out MeshCurve curve, out double paramV1, out double paramV2, bool simpleProfile = false)
    {
        curve = null!; paramV1 = 0; paramV2 = 1;
        uint other = sideA ? e.FaceB : e.FaceA;
        if (!surfaces.TryGetValue(other, out var rev) || rev.Kind != SurfKind.Rev || !c.Curves.TryGetValue(rev.ProfileHandle, out var profile)
            || (MeshProfileIsLineOrCircle(profile) && !simpleProfile)) return false;
        var uvs = e.AuthoredPoints();
        var uv = uvs.Select(p => sideA ? new[] { p.UB, p.VB } : new[] { p.UA, p.VA }).ToList();
        if (uv.Any(p => !double.IsFinite(p[0]) || !double.IsFinite(p[1]))) return false;
        return TryRevolvedCurve(rev, profile, uv, pts, out curve, out paramV1, out paramV2);
    }

    /// <summary>
    /// The circle of a cylinder edge at constant height, from the edge's own (angle, height)
    /// samples: P(t) = C + R cos t X + R sin t (N x X) with N = axis for a growing angle and -axis
    /// for a falling one (t = 2 pi - angle, the reversed arc of Revit), so that the arc runs from Start to End in the direction of growing t.
    /// </summary>
    internal static bool TryCylinderArc(SurfaceRec cyl, List<double[]> uv, out MeshArcModel arc)
    {
        arc = null!;
        double a0 = uv[0][0], a1 = uv[^1][0], h = uv[0][1];
        if (!double.IsFinite(a0) || !double.IsFinite(a1) || !double.IsFinite(h) || Math.Abs(a1 - a0) < 1e-9) return false;
        if (uv.Any(p => Math.Abs(p[1] - h) > 1e-10)) return false;
        bool grows = a1 > a0;
        // Iso-parameter curve of a cylinder: the frame of the circle is that of the cylinder,
        // whose reference is the unit x vector and whose axis is the cross product of the unit x
        // and y vectors
        var unitX = MeshUnit(cyl.X);
        var axis = MeshCross(unitX, MeshUnit(cyl.Y));
        arc = new MeshArcModel
        {
            Center = Enumerable.Range(0, 3).Select(k => cyl.O[k] + h * cyl.Z[k]).ToArray(),
            Radius = cyl.R, Normal = !MeshUnitFrame ? (grows ? (double[])cyl.Z.Clone() : cyl.Z.Select(v => -v).ToArray()) : grows ? axis : axis.Select(v => -v).ToArray(),
            Ref = MeshUnitFrame ? unitX : (double[])cyl.X.Clone(),
            Start = grows ? a0 : ArcSampler.TwoPi - a0, End = grows ? a1 : ArcSampler.TwoPi - a1,
        };
        return true;
    }

    /// <summary>
    /// The curve of an edge that is neither a line nor a circle: the natural cubic interpolation of
    /// its distinct samples (NurbsFit).
    /// </summary>
    internal static bool TryNurbsCurve(List<double[]> pts, out MeshCurve curve, out double paramV1, out double paramV2)
    {
        curve = null!; paramV1 = 0; paramV2 = 1;
        var samples = NurbsFit.Distinct(pts);
        if (NurbsFit.Fit(samples) is not { } nurbs) return false;
        paramV1 = nurbs.Knots[nurbs.Degree]; paramV2 = nurbs.Knots[nurbs.ControlPoints.Length];
        curve = new MeshCurve { Kind = CurveKind.Nurbs, Lo = paramV1, Hi = paramV2, P0 = pts[0], P1 = pts[^1], Nurbs = nurbs };
        return true;
    }

    /// <summary>
    /// The circle through the samples of an arc edge (flag 8); an edge of any other flags is the
    /// interpolating spline whatever its shape (see <see cref="MeshFitNeedsArcFlag"/>).
    /// </summary>
    internal static bool TryFittedArc(EdgeRec e, List<double[]> pts, out MeshArcModel arc)
    {
        arc = null!;
        return (!MeshFitNeedsArcFlag || (e.Flags & 8) != 0) && TryFitCircle(pts, out arc);
    }

    /// <summary>
    /// The circle of a cone edge at a constant slant distance v from the apex: centre at the axial
    /// distance v cos(a), radius v sin(a); the parameter grows from the first to the last sample
    /// like the cylinder circle.
    /// </summary>
    internal static bool TryConeArc(SurfaceRec cone, List<double[]> uv, out MeshArcModel arc)
    {
        arc = null!;
        double a0 = uv[0][0], a1 = uv[^1][0], v = uv[0][1];
        if (!double.IsFinite(a0) || !double.IsFinite(a1) || !double.IsFinite(v) || Math.Abs(a1 - a0) < 1e-9 || !(Math.Abs(v) > 1e-12)) return false;
        if (uv.Any(p => Math.Abs(p[1] - v) > 1e-10)) return false;
        bool grows = a1 > a0;
        double radius = v * Math.Sin(cone.R);
        bool negative = radius < 0;
        // a negative slant distance is the mirrored nappe: the circle point at the angle t is on
        // the opposite side
        arc = new MeshArcModel
        {
            Center = Enumerable.Range(0, 3).Select(k => cone.O[k] + v * Math.Cos(cone.R) * cone.Z[k]).ToArray(),
            Radius = Math.Abs(radius), Normal = grows ? (double[])cone.Z.Clone() : cone.Z.Select(x => -x).ToArray(),
            Ref = negative ? cone.X.Select(x => -x).ToArray() : (double[])cone.X.Clone(),
            Start = (negative ? Math.PI : 0) + (grows ? a0 : -a0), End = (negative ? Math.PI : 0) + (grows ? a1 : -a1),
            EdgeRange = ((negative ? Math.PI : 0) + (grows ? a0 : -a0), (negative ? Math.PI : 0) + (grows ? a1 : -a1)),
        };
        return true;
    }

    /// <summary>
    /// The circle through the samples of an edge (first, a middle one, last), checked against all
    /// of them; it runs from the first to the last sample with a growing parameter (Start = 0 at
    /// the first sample).
    /// </summary>
    internal static bool TryFitCircle(List<double[]> pts, out MeshArcModel arc)
    {
        arc = null!;
        int n = pts.Count;
        if (n < 3) return false;
        var a = pts[0]; var b = pts[n / 2]; var d = pts[^1];
        var ab = new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] }; var ad = new[] { d[0] - a[0], d[1] - a[1], d[2] - a[2] };
        var nrm = MeshCross(ab, ad);
        double nn = Dot3(nrm, nrm);
        if (!(nn > 1e-24)) return false;
        double ab2 = Dot3(ab, ab), ad2 = Dot3(ad, ad);
        // circumcentre: a + ((|ad|^2 (ab x ad) x ab + |ab|^2 ad x (ab x ad)) / (2 |ab x ad|^2))
        var t1 = MeshCross(MeshCross(ab, ad), ab); var t2 = MeshCross(ad, MeshCross(ab, ad));
        var centre = Enumerable.Range(0, 3).Select(k => a[k] + (ad2 * t1[k] + ab2 * t2[k]) / (2 * nn)).ToArray();
        double radius = Dist3(a, centre);
        if (!(radius > 1e-9)) return false;
        double tol = 1e-8 * Math.Max(1.0, radius);
        if (pts.Any(p => Math.Abs(Dist3(p, centre) - radius) > tol))
        {
            if (MeshNodeLog) DiagnosticSink.WriteLine($"[meshfit] n={n} radius={radius:G6} maxdev={pts.Max(p => Math.Abs(Dist3(p, centre) - radius)):G3}");
            return false;
        }
        var normal = MeshUnit(nrm);
        var reference = MeshUnit(new[] { a[0] - centre[0], a[1] - centre[1], a[2] - centre[2] });
        var per = MeshCross(normal, reference);
        double previous = 0;
        foreach (var p in pts.Skip(1))
        {
            var v = new[] { p[0] - centre[0], p[1] - centre[1], p[2] - centre[2] };
            double angle = Math.Atan2(Dot3(v, per), Dot3(v, reference));
            while (angle < previous - 1e-9) angle += 2 * Math.PI;
            previous = angle;
        }
        if (!(previous > 1e-9)) return false;
        arc = new MeshArcModel { Center = centre, Radius = radius, Normal = normal, Ref = reference, Start = 0, End = previous };
        return true;
    }

    // ---- ruled and Hermite (free form) surfaces

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Cur, Dictionary<uint, (RuledSurface?, NurbsSurface?, Func<double, double, double[]>)?>> FreeformSurfaces = new();

    /// <summary>
    /// The model of a ruled or Hermite surface and its evaluator (u, v) -> world point; null when a
    /// profile or the node grid is not described.
    /// </summary>
    private static (RuledSurface?, NurbsSurface?, Func<double, double, double[]>)? MeshFreeformEvaluator(Cur c, uint face, SurfaceRec s)
    {
        var cache = FreeformSurfaces.GetOrCreateValue(c);
        if (cache.TryGetValue(face, out var known)) return known;
        (RuledSurface?, NurbsSurface?, Func<double, double, double[]>)? result = null;
        if (s.Kind == SurfKind.Ruled)
        {
            MeshCurve? Profile(uint handle) => handle != 0 && handle != 0xFFFFFFFF && c.Curves.TryGetValue(handle, out var a) ? MeshProfileCurve(a) : null;
            var p0 = Profile(s.ProfileHandle); var p1 = Profile(s.ProfileHandle2);
            if (MeshNodeLog && (p0 == null || p1 == null))
            {
                string Why(uint h, MeshCurve? p) => p != null ? "ok" : h == 0 || h == 0xFFFFFFFF ? "none" : !c.Curves.TryGetValue(h, out var a) ? "missing"
                    : $"unsupported(nurbs={a.NurbControlPoints?.Count ?? 0} rational={a.NurbRational} hermite={a.HermiteNodes?.Count ?? 0} R={a.R:G4} RY={a.RY:G4} t={a.T0:G4}..{a.T1:G4})";
                DiagnosticSink.WriteLine($"[meshruled] face {face}: profile0 {Why(s.ProfileHandle, p0)}, profile1 {Why(s.ProfileHandle2, p1)}, P1={(s.P1 != null)} P2={(s.P2 != null)}");
            }
            if ((p0 != null || s.P1 != null) && (p1 != null || s.P2 != null)
                && !(s.ProfileHandle != 0 && s.ProfileHandle != 0xFFFFFFFF && p0 == null) && !(s.ProfileHandle2 != 0 && s.ProfileHandle2 != 0xFFFFFFFF && p1 == null))
            {
                var ruled = new RuledSurface { Profile0 = p0, Profile1 = p1, Point0 = p0 == null ? s.P1 : null, Point1 = p1 == null ? s.P2 : null };
                if (FfSurfaces.Ruled(ruled) is { } eval) result = (ruled, null, (u, v) => eval(u, v).ToArray());
            }
        }
        else if (s.HermiteNodes is { Count: > 0 } nodes && s.UParams is { Length: >= 2 } us && s.VParams is { Length: >= 2 } vs && nodes.Count == us.Length * vs.Length
            && Hermite.Surface(us, vs, nodes.Select(n => n.Point).ToArray(), nodes.Select(n => n.Du).ToArray(), nodes.Select(n => n.Dv).ToArray(), nodes.Select(n => n.Duv).ToArray()) is { } surface
            && FfSurfaces.Nurbs(surface) is { } nurbsEval)
            result = (null, surface, (u, v) => nurbsEval(u, v).ToArray());
        cache[face] = result;
        return result;
    }

    /// <summary>
    /// A profile curve of a ruled surface in the terms of the freeform track; the parameter
    /// interval is [T0, T1]. Null for a curve of another kind (rational B-spline).
    /// </summary>
    internal static MeshCurve? MeshProfileCurve(ArcRec a)
    {
        if (a.NurbControlPoints is { Count: > 0 } controls && a.NurbKnots is { Length: > 0 } knots)
        {
            bool rational = a.NurbRational || controls.Any(p => p.Weight != 1.0);
            if (rational && controls.Any(p => !(p.Weight > 0.0))) return null;
            var nurbs = new Nurbs
            {
                Degree = a.NurbDegree, Knots = knots, ControlPoints = controls.Select(p => p.Point).ToArray(),
                Weights = rational ? controls.Select(p => p.Weight).ToArray() : null,
            };
            if (knots.Length != controls.Count + a.NurbDegree + 1) return null;
            return new MeshCurve { Kind = CurveKind.Nurbs, Lo = knots[a.NurbDegree], Hi = knots[controls.Count], Nurbs = nurbs };
        }
        if (a.HermiteNodes is { Count: >= 2 } hermite)
        {
            var curve = Hermite.Curve(hermite.Select(n => n.T).ToArray(), hermite.Select(n => n.Point).ToArray(), hermite.Select(n => n.Tangent).ToArray());
            return curve == null ? null : new MeshCurve { Kind = CurveKind.Nurbs, Lo = curve.Knots[curve.Degree], Hi = curve.Knots[curve.ControlPoints.Length], Nurbs = curve };
        }
        if (!(a.T1 > a.T0)) return null;
        if (a.R == 0)
            return new MeshCurve { Kind = CurveKind.Line, Lo = a.T0, Hi = a.T1, P0 = a.Eval(a.T0), P1 = a.Eval(a.T1) };
        if (!(a.R > 0)) return null;
        if (a.RY == 0 || Math.Abs(a.RY - a.R) < 1e-12)
            return new MeshCurve
            {
                Kind = CurveKind.Arc, Lo = a.T0, Hi = a.T1,
                Arc = new MeshArcModel { Center = a.C, Radius = a.R, Normal = MeshUnit(MeshCross(a.X, a.Y)), Ref = MeshUnit(a.X), Start = a.T0, End = a.T1 },
            };
        return new MeshCurve
        {
            Kind = CurveKind.Ellipse, Lo = a.T0, Hi = a.T1,
            Ellipse = new Ellipse { Center = a.C, MajorAxis = MeshUnit(a.X), MinorAxis = MeshUnit(a.Y), MajorRadius = a.R, MinorRadius = a.RY, Start = a.T0, End = a.T1 },
        };
    }

    /// <summary>
    /// The 3D curve and the parameter curve of an edge on a free form face: a line or a circle with
    /// the straight uv segment between the first and the last sample, else the interpolating spline
    /// of the samples with the uv interpolated on the same knots.
    /// </summary>
    internal static bool TryFreeformEdge(EdgeRec e, List<double[]> pts, List<double[]> uv, out MeshCurve curve, out ParamCurve? paramCurve, out double paramV1, out double paramV2)
    {
        curve = null!; paramCurve = null; paramV1 = 0; paramV2 = 1;
        if (IsStraight(pts))
        {
            curve = new MeshCurve { Kind = CurveKind.Line, Lo = 0, Hi = 1, P0 = pts[0], P1 = pts[^1] };
            paramCurve = new ParamCurve { Lo = 0, Hi = 1, P0 = uv[0], P1 = uv[^1] };
            return true;
        }
        if ((e.Flags & 8) != 0 && TryFitCircle(pts, out var arc))
        {
            curve = new MeshCurve { Kind = CurveKind.Arc, Lo = arc.Start, Hi = arc.End, P0 = pts[0], P1 = pts[^1], Arc = arc };
            paramCurve = new ParamCurve { Lo = arc.Start, Hi = arc.End, P0 = uv[0], P1 = uv[^1] };
            paramV1 = arc.Start; paramV2 = arc.End;
            return true;
        }
        var distinctPts = new List<double[]>(); var distinctUv = new List<double[]>();
        for (int i = 0; i < pts.Count; i++)
            if (distinctPts.Count == 0 || Math.Abs(pts[i][0] - distinctPts[^1][0]) > 1e-9 || Math.Abs(pts[i][1] - distinctPts[^1][1]) > 1e-9 || Math.Abs(pts[i][2] - distinctPts[^1][2]) > 1e-9)
            { distinctPts.Add(pts[i]); distinctUv.Add(uv[i]); }
        var chord = NurbsFit.ChordLengths(distinctPts);
        if (NurbsFit.FitValues(distinctPts, chord) is not { } nurbs || NurbsFit.FitValues(distinctUv, chord) is not { } uvNurbs) return false;
        paramV1 = nurbs.Knots[nurbs.Degree]; paramV2 = nurbs.Knots[nurbs.ControlPoints.Length];
        curve = new MeshCurve { Kind = CurveKind.Nurbs, Lo = paramV1, Hi = paramV2, P0 = pts[0], P1 = pts[^1], Nurbs = nurbs };
        paramCurve = new ParamCurve { Lo = paramV1, Hi = paramV2, Nurbs = uvNurbs };
        return true;
    }

    // ---- surfaces of revolution

    /// <summary>
    /// The authored samples of an edge list a point twice where two tessellation pieces meet; the
    /// fit curve needs them once.
    /// </summary>
    internal static double[][] DistinctUv(List<double[]> uv)
    {
        var result = new List<double[]> { uv[0] };
        foreach (var p in uv.Skip(1))
            if (Math.Abs(p[0] - result[^1][0]) > 1e-9 || Math.Abs(p[1] - result[^1][1]) > 1e-9) result.Add(p);
        if (result.Count == 1) result.Add(uv[^1]);
        return result.ToArray();
    }

    /// <summary>
    /// A reversed surface normal has not been observed to hold for a revolved face (it needs a
    /// flipped face).
    /// </summary>
    private const bool MeshNormalReversed = false;

    /// <summary>
    /// A profile of a straight line or a circular arc (the ones the revolved mesher models).
    /// </summary>
    internal static bool MeshProfileIsLineOrCircle(ArcRec profile) =>
        profile.NurbControlPoints is not { Count: > 0 } && profile.HermiteNodes is not { Count: > 0 }
        && (profile.R == 0 || (profile.R > 0 && (profile.RY == 0 || Math.Abs(profile.RY - profile.R) < 1e-12)));

    /// <summary>
    /// Every profile curve the GRep stores can be evaluated (the line and the circle analytically,
    /// the rest through the curve itself).
    /// </summary>
    internal static bool MeshProfileSupported(ArcRec profile) =>
        MeshProfileIsLineOrCircle(profile) || profile.NurbControlPoints is { Count: > 1 } || profile.HermiteNodes is { Count: > 1 } || (profile.R > 0 && profile.RY > 0);

    /// <summary>
    /// World point of the revolved surface: O + r cos(u) X + r sin(u) Y + a Z with (r,., a) the
    /// profile point at v.
    /// </summary>
    internal static double[] MeshRevolvedPoint(SurfaceRec s, ArcRec profile, double u, double v)
    {
        if (!CompactEvaluation)
        {
            var q = profile.Eval(v);
            return Enumerable.Range(0, 3).Select(k => s.O[k] + q[0] * Math.Cos(u) * s.X[k] + q[0] * Math.Sin(u) * s.Y[k] + q[2] * s.Z[k]).ToArray();
        }
        double[] p;
        if (profile.NurbControlPoints != null || profile.HermiteNodes != null) p = profile.Eval(v);
        else if (profile.R == 0) p = [profile.C[0] + v * profile.X[0], 0.0, profile.C[2] + v * profile.X[2]];
        else
        {
            // the profile circle in the rounding order of Revit: centre + (R (cos t X) + RY
            // (sin t Y))
            double ry = profile.RY == 0 ? profile.R : profile.RY, ct = Math.Cos(v), st = Math.Sin(v);
            p = [profile.C[0] + (profile.R * (ct * profile.X[0]) + ry * (st * profile.Y[0])), 0.0,
                 profile.C[2] + (profile.R * (ct * profile.X[2]) + ry * (st * profile.Y[2]))];
        }
        double c = Math.Cos(u), sn = Math.Sin(u);
        return [(((c * s.X[0] + sn * s.Y[0]) * p[0]) + s.O[0]) + p[2] * s.Z[0],
                (((c * s.X[1] + sn * s.Y[1]) * p[0]) + s.O[1]) + p[2] * s.Z[1],
                (((c * s.X[2] + sn * s.Y[2]) * p[0]) + s.O[2]) + p[2] * s.Z[2]];
    }

    /// <summary>The same cylinder with the right handed axis X x Y.</summary>
    private static SurfaceRec RightHanded(SurfaceRec s)
    {
        var copy = s;
        copy.Z = MeshUnit(MeshCross(s.X, s.Y));
        return copy;
    }

    private static double[] MeshCross(double[] a, double[] b) =>
        [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

    private static double[] MeshUnit(double[] v)
    {
        double n = Math.Sqrt(Dot3(v, v));
        return [v[0] / n, v[1] / n, v[2] / n];
    }

    /// <summary>
    /// A vector (x,., z) of the profile plane as a world vector at the given rotation: x along the
    /// radial direction, z along Z.
    /// </summary>
    private static double[] MeshMeridian(SurfaceRec s, double[] local, double rotation = 0)
    {
        double c = Math.Cos(rotation), sn = Math.Sin(rotation);
        return Enumerable.Range(0, 3).Select(k => local[0] * (c * s.X[k] + sn * s.Y[k]) + local[2] * s.Z[k]).ToArray();
    }

    internal static RevolvedSurface MeshRevolvedSurfaceOf(SurfaceRec s, ArcRec profile, bool normalReversed)
    {
        // a left handed frame (Z against X x Y): Revit turns about Z and sees the rotation
        // mirrored
        bool leftHanded = Dot3(MeshCross(s.X, s.Y), s.Z) < 0;
        var axis = leftHanded ? MeshUnit(s.Z) : MeshUnit(MeshCross(s.X, s.Y));
        normalReversed |= leftHanded;
        var env = s.Env!;
        // renderer order: [profile lo, profile hi, rotation lo, rotation hi]; a reversed normal
        // mirrors the rotation
        double rotLo = normalReversed ? -env[2] : env[0], rotHi = normalReversed ? -env[0] : env[2];
        var centre = Enumerable.Range(0, 3).Select(k => s.O[k] + profile.C[0] * s.X[k] + profile.C[2] * s.Z[k]).ToArray();
        if (!MeshProfileIsLineOrCircle(profile))
        {
            // the profile point (x,., z) of the local frame: x along the radial direction at the
            // rotation 0, z along the axis
            var local = profile;
            return new RevolvedSurface
            {
                Origin = s.O, Axis = axis, V0 = 0, U0 = 0, GeEnvelope = [env[1], env[3], rotLo, rotHi], NormalReversed = normalReversed,
                ProfileAt = u => MeshMeridian(s, local.Eval(u)).Select((x, k) => x + s.O[k]).ToArray(),
            };
        }
        return profile.R == 0
            ? new RevolvedSurface
            {
                Origin = s.O, Axis = axis, V0 = 0, U0 = 0, ProfileStart = centre, ProfileDir = MeshMeridian(s, profile.X),
                GeEnvelope = [env[1], env[3], rotLo, rotHi], NormalReversed = normalReversed,
            }
            : new RevolvedSurface
            {
                Origin = s.O, Axis = axis, V0 = 0, U0 = 0, ArcCenter = centre, ArcE1 = MeshMeridian(s, profile.X), ArcE2 = MeshMeridian(s, profile.Y),
                ArcRadius = profile.R, ArcEps = 1.0, GeEnvelope = [env[1], env[3], rotLo, rotHi], NormalReversed = normalReversed,
            };
    }

    /// <summary>
    /// The 3D curve of an edge of a revolved face: the profile curve at a fixed rotation (line or
    /// arc) or a circle around the axis at a fixed profile parameter.
    /// </summary>
    internal static bool TryRevolvedCurve(SurfaceRec s, ArcRec profile, List<double[]> uv, List<double[]> pts, out MeshCurve curve, out double paramV1, out double paramV2)
    {
        curve = null!; paramV1 = 0; paramV2 = 1;
        double u0 = uv[0][0], v0 = uv[0][1];
        bool rotationConstant = uv.All(p => Math.Abs(p[0] - u0) < 1e-9), profileConstant = uv.All(p => Math.Abs(p[1] - v0) < 1e-9);
        if (rotationConstant == profileConstant) return false;
        double first = rotationConstant ? uv[0][1] : uv[0][0], last = rotationConstant ? uv[^1][1] : uv[^1][0];
        bool grows = last > first;
        if (Math.Abs(last - first) < 1e-12) return false;
        if (rotationConstant)
        {
            if (!MeshProfileIsLineOrCircle(profile)) return false;
            if (profile.R == 0)
            {
                curve = new MeshCurve { Kind = CurveKind.Line, Lo = 0, Hi = 1, P0 = pts[0], P1 = pts[^1] };
                return true;
            }
            var e1 = MeshMeridian(s, profile.X, u0); var e2 = MeshMeridian(s, profile.Y, u0);
            var centre = Enumerable.Range(0, 3).Select(k => s.O[k] + profile.C[0] * (Math.Cos(u0) * s.X[k] + Math.Sin(u0) * s.Y[k]) + profile.C[2] * s.Z[k]).ToArray();
            var normal = MeshUnit(MeshCross(e1, e2));
            var arc = new MeshArcModel
            {
                Center = centre, Radius = profile.R, Normal = grows ? normal : normal.Select(v => -v).ToArray(), Ref = MeshUnit(e1),
                Start = grows ? first : -first, End = grows ? last : -last,
            };
            curve = new MeshCurve { Kind = CurveKind.Arc, Lo = arc.Start, Hi = arc.End, P0 = pts[0], P1 = pts[^1], Arc = arc };
            paramV1 = arc.Start; paramV2 = arc.End;
            return true;
        }
        // circle around the axis at the profile parameter v0: radius |r| at the axial coordinate a
        var pr = profile.Eval(v0);
        double r = pr[0];
        if (!(Math.Abs(r) > 1e-12)) return false;
        var axisNormal = MeshUnit(MeshCross(s.X, s.Y));
        var circle = new MeshArcModel
        {
            Center = Enumerable.Range(0, 3).Select(k => s.O[k] + pr[2] * s.Z[k]).ToArray(), Radius = Math.Abs(r),
            Normal = grows ? axisNormal : axisNormal.Select(v => -v).ToArray(), Ref = r > 0 ? MeshUnit(s.X) : MeshUnit(s.X).Select(v => -v).ToArray(),
            Start = (r > 0 ? 0 : Math.PI) + (grows ? first : -first), End = (r > 0 ? 0 : Math.PI) + (grows ? last : -last),
        };
        circle = new MeshArcModel { Center = circle.Center, Radius = circle.Radius, Normal = circle.Normal, Ref = circle.Ref, Start = circle.Start, End = circle.End, EdgeRange = (circle.Start, circle.End) };
        curve = new MeshCurve { Kind = CurveKind.Arc, Lo = circle.Start, Hi = circle.End, P0 = pts[0], P1 = pts[^1], Arc = circle };
        paramV1 = circle.Start; paramV2 = circle.End;
        return true;
    }

    private static double Dot3(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}
