using RvtIO.Internal;
using Pt3 = RvtIO.Geometry.Meshing.Pt3;

namespace RvtIO.Parsing;

/// <summary>
/// Schema-driven decoder for G-tree geometry blocks (classIdx low16 = 0x07D7 GElement). The grammar is
/// documented in the file format notes.
/// </summary>
internal static partial class GRepParserV2
{
    // Geometric approximation target used for analytic and spline surfaces. RVT geometry is stored
    // in feet; 0.25 mm keeps sub-millimetre chord error without tying mesh density to any family,
    // category, or absolute model size.
    private const double TessellationChordToleranceFeet = 0.25 / 304.8;

    /// <summary>
    /// Faces of one representation after which its remaining surfaces are tessellated coarsely.
    /// </summary>
    private const int CoarseTessellationFaceBudget = 100000;

    [ThreadStatic] private static bool t_coarseTessellation;

    /// <summary>Faces of the geometry being built that were left out because their trim triangulation ran out of its budget.</summary>
    [ThreadStatic] private static int t_untriangulatedFaces;

    private static double ChordTolerance => TessellationChordToleranceFeet * (t_coarseTessellation ? 16 : 1);

    private sealed class DecodeStop : Exception { }
    [ThreadStatic] private static DecodeStop? t_stop;
    private static DecodeStop Stop => t_stop ??= new();

    /// <summary>
    /// Serialized class ids of the Face and Edge objects whose per-block counts feed <see
    /// cref="GRepData.QueueSummary"/>.
    /// </summary>
    internal const ushort GFaceClass = 1589;
    internal const ushort EdgeClass = 1243;
    /// <summary>Class "Geometry" (a GBRep): one stored solid.</summary>
    internal const ushort SolidClass = 2092;

    private sealed record InstanceRec(
        int SymbolElemId, double[] Transform, int OwnerQueueIndex);

    private sealed class Cur
    {
        public byte[] D = [];
        public int Pos, End;
        public int ElementId;
        public ushort RootClass;
        public bool Debug;
        public bool NativeWallBody;
        public bool IncludeNonVisible;
        public bool SawDetailCondition; // a node was judged below a detail-level condition
        public Dictionary<int, ModelFilter>? ModelFilters;
        public bool? HasRootMappedSource;
        public int BlockStart, BlockEnd; // this block's payload bounds (plane scan)
        public int LastEdgeEnd; // pos after the last decoded Edge
        public List<(ushort Cls, int V)> Queue = new();

        // Collected geometry.
        public int FaceCount; // faces seen (schema-walked)
        public List<uint> FaceOrder = new(); // face handle (queue v) in processing order
        public Dictionary<uint, int> FaceRenderStyle = new(); // GFace.m_renderStyleId (the face material) by face handle
        public Dictionary<uint, uint> FaceFlagsV9 = new(); // GFace.m_faceFlags_v9 by face handle
        public List<ushort> SurfClasses = new(); // surface classId per face, in queue order
        public List<EdgeRec> Edges = new();
        // Serialized polygon meshes. The owning GPolyMesh queue index is retained so its GInfo
        // state can be applied without relying on non-unique pointer handles.
        public List<PolyMeshRec> PolyMeshes = new();
        // RVTIO_MESH_DIAG: FacetedTopology classes walked, GPolyMesh nodes met and why a mesh was
        // not drawn.
        public Dictionary<ushort, int> FtWalked = new();
        public List<string> FtNotes = new();
        public int MeshDropOwner, MeshDropBranch, MeshDropFlags;
        public string? StopNote;
        // GInstance capture: family-symbol reference + placement.
        public int SymbolId;
        public double[]? InstTrf;
        public List<InstanceRec> Instances = new();
        // Authoritative bounds serialized on the root GRep (schema class 2008). These are not
        // inferred from recovered faces: m_tightbBox is Revit's exact geometry extent, while m_bBox
        // is the broader display extent.
        public double[]? RootBoundingBox;
        public double[]? RootTightBoundingBox;
        public double[]? FamilyClosurePlaneOrigin;
        public double[]? FamilyClosurePlaneNormal;

        // Surfaces captured DURING the walk (their byte positions are exact there), in arrival
        // order - pairs with FaceOrder when the walk completes.
        public List<SurfaceRec> WalkedSurfaces = new();

        // A Face queues its m_pSurf object by handle. Preserve that relationship instead of relying
        // on surface arrival order: after a queue resync the order can contain gaps, while the
        // handles remain authoritative.
        public Dictionary<uint, uint> SurfaceOwnerByHandle = new();
        public Dictionary<int, uint> SurfaceOwnerByQueueIndex = new();
        public Dictionary<uint, SurfaceRec> DirectSurfaceByFace = new();

        // Profile curves captured during the walk, keyed by object handle (queue v). SurfRev
        // references its profile GArc/GLine by handle.
        public Dictionary<uint, ArcRec> Curves = new();

        // Blob tags are per-class sequential ordinals: tag(k-th item of class) = base + k.
        public Dictionary<ushort, int> ClassWalked = new();
        public Dictionary<ushort, int> TagBase = new();
        public List<int> DebugItemStarts = new();
        public ResyncUndo? ProbeUndo;

        // Grammar-first tail harvest: face-handle -> surface pairs recovered from unwalked regions
        // (post-gap objects belong to queue entries that never got enqueued).
        public Dictionary<uint, SurfaceRec> HarvestSurf = new();

        // Exact deferred-pointer graph. QueueLinks is also needed by production geometry ownership;
        // QueueChildren retains only m_oConditions outside targeted diagnostics.
        public int CurrentObjectHandle;
        public ushort CurrentObjectClass;
        public int CurrentQueueIndex = -1;
        // Queue entries, rather than serialized handles, are the object identities here.
        public List<(int ParentQueueIndex, string Field)> QueueLinks = new();
        public Dictionary<int, List<int>> QueueChildren = new();
        public Dictionary<int, Dictionary<string, int>> QueueInts = new();
        public Dictionary<int, Dictionary<string, List<double>>> QueueDoubles = new();
        public Dictionary<int, Dictionary<string, List<byte>>> QueueBytes = new();
        public Dictionary<int, (int Tag, int Control, int Category, uint Flags)> ObjectGInfo = new();

        public EdgeRec? CapEdge; // non-null while walking an Edge
        public bool ExactPlanarSpaceBoundary;
        /// <summary>
        /// An exact space boundary whose Faces may include analytic cylinders (a room with a round
        /// wall): such a body is written from the Revit face meshes of its Geometry node.
        /// </summary>
        public bool CurvedSpaceBoundary;

        /// <summary>
        /// Distance below which the world vertices of the planar Faces of an exact space boundary
        /// are one vertex (0: none).
        /// </summary>
        public double WeldFeet;
        private readonly List<double[]> _weldSeen = new();
        private readonly List<int> _weldSeenCluster = new();
        private readonly List<double[]> _weldRepresentatives = new();

        /// <summary>The representative of the vertex cluster of <paramref name="point"/>.</summary>
        public double[] Weld(double[] point)
        {
            int cluster = -1;
            for (int i = 0; i < _weldSeen.Count; i++)
            {
                var known = _weldSeen[i];
                if (Math.Abs(known[0] - point[0]) > WeldFeet || Math.Abs(known[1] - point[1]) > WeldFeet
                    || Math.Abs(known[2] - point[2]) > WeldFeet)
                    continue;
                int other = _weldSeenCluster[i];
                if (cluster < 0) cluster = other;
                else if (cluster != other)
                {
                    Array.Copy(_weldRepresentatives[cluster], _weldRepresentatives[other], 3);
                    for (int j = 0; j < _weldSeenCluster.Count; j++)
                        if (_weldSeenCluster[j] == other) _weldSeenCluster[j] = cluster;
                }
            }
            if (cluster < 0)
            {
                cluster = _weldRepresentatives.Count;
                _weldRepresentatives.Add((double[])point.Clone());
            }
            _weldSeen.Add(point);
            _weldSeenCluster.Add(cluster);
            return _weldRepresentatives[cluster];
        }
        public Dictionary<int, SpaceEdgeLoopRec> SpaceEdgeLoops = new();
        /// <summary>
        /// Faces of the loop fallback that collapsed into a sliver under the weld and are not part
        /// of the shell.
        /// </summary>
        public int CollapsedFaces;
        // Face handle -> Face.m_pFirstLoop was serialized as a null pointer (a dead Face: it has no
        // boundary and is not part of the shell). EdgeLoop queue indices whose m_nextLoop is null
        // end their Face's loop list.
        public Dictionary<uint, bool> FaceHasFirstLoop = new();
        public HashSet<int> LastLoops = new();

        public int U32() { if (Pos + 4 > End) throw Stop; int v = BitConverter.ToInt32(D, Pos); Pos += 4; return v; }
        public double F64() { if (Pos + 8 > End) throw Stop; double v = BitConverter.ToDouble(D, Pos); Pos += 8; return v; }
        public void Take(int n) { if (Pos + n > End) throw Stop; Pos += n; }
        public bool CanRead(int n) => Pos + n <= End;
    }

    /// <summary>A point's UV in both adjacent faces: (uA,vA) and (uB,vB).</summary>
    internal struct EdgeUV { public double UA, VA, UB, VB; }

    internal struct PlaneRec { public double[] O = new double[3], X = new double[3], Y = new double[3]; public PlaneRec() { } }

    internal enum SurfKind { Unknown, Plane, Cylinder, Cone, Rev, Ruled, Hermite }

    /// <summary>
    /// A face's surface: plane (O,X,Y), cylinder (O,X,Y,Z frame + R), cone (O,X,Y,Z + half-angle)
    /// or surface of revolution (frame + profile-curve handle).
    /// </summary>
    internal struct SurfaceRec
    {
        public SurfKind Kind;
        public double[] O = new double[3], X = new double[3], Y = new double[3], Z = new double[3];
        public double R; // radius (cyl) or halfAngle (cone)
        public double[]? Env; // uv bounds [umin,vmin,umax,vmax] from Surface base
        public byte Orient; // Surface.m_orientFlag, the u8 after the envelope
        public uint ProfileHandle; // SurfRev: profile / RuledSurf: curve1
        public uint ProfileHandle2; // RuledSurf: curve2 (0xFFFFFFFF = use point)
        public double[]? P1, P2; // RuledSurf: fallback apex points
        public List<HermiteSurfaceNode>? HermiteNodes;
        public double[]? UParams, VParams;
        public SurfaceRec() { }
        public bool IsCyl => Kind is SurfKind.Cylinder or SurfKind.Cone;
    }

    internal sealed class HermiteSurfaceNode
    {
        public double[] Point = new double[3];
        public double[] Du = new double[3];
        public double[] Dv = new double[3];
        public double[] Duv = new double[3];
    }

    internal sealed class PolyMeshRec
    {
        public int OwnerQueueIndex;
        public List<double[]> Points = new();
        public List<(int A, int B, int C)> Triangles = new();
        public List<byte> EdgeVisibility = new();
    }

    /// <summary>
    /// A profile curve for surfaces of revolution / ruled surfaces. R != 0: arc - circle of radius
    /// R about C in the (X,Y) plane; R == 0: line - C + t*X.
    /// </summary>
    internal struct ArcRec
    {
        public double[] C = new double[3], X = new double[3], Y = new double[3];
        public double R, RY, T0, T1;
        public List<HermiteNode>? HermiteNodes;
        public int NurbDegree;
        public bool NurbClosed;
        public bool NurbRational;
        public List<NurbControlPoint>? NurbControlPoints;
        public double[]? NurbKnots;
        public ArcRec() { }
        public double[] Eval(double t)
        {
            if (NurbControlPoints is { Count: > 0 } controls
                && NurbKnots is { Length: > 0 } knots)
                return EvaluateNurbSpline(t, NurbDegree, NurbClosed,
                    NurbRational, controls, knots);
            if (HermiteNodes is { Count: > 0 } nodes)
            {
                if (nodes.Count == 1 || t <= nodes[0].T) return (double[])nodes[0].Point.Clone();
                if (t >= nodes[^1].T) return (double[])nodes[^1].Point.Clone();
                int segment = 0;
                while (segment + 1 < nodes.Count && t > nodes[segment + 1].T) segment++;
                var a = nodes[segment];
                var b = nodes[segment + 1];
                double dt = b.T - a.T;
                if (Math.Abs(dt) < 1e-15) return (double[])a.Point.Clone();
                double u = (t - a.T) / dt;
                double u2 = u * u, u3 = u2 * u;
                double h00 = 2 * u3 - 3 * u2 + 1;
                double h10 = u3 - 2 * u2 + u;
                double h01 = -2 * u3 + 3 * u2;
                double h11 = u3 - u2;
                return new[]
                {
                    h00 * a.Point[0] + h10 * dt * a.Tangent[0] + h01 * b.Point[0] + h11 * dt * b.Tangent[0],
                    h00 * a.Point[1] + h10 * dt * a.Tangent[1] + h01 * b.Point[1] + h11 * dt * b.Tangent[1],
                    h00 * a.Point[2] + h10 * dt * a.Tangent[2] + h01 * b.Point[2] + h11 * dt * b.Tangent[2],
                };
            }
            return R == 0
                ? new[] { C[0] + t * X[0], C[1] + t * X[1], C[2] + t * X[2] }
                : new[]
            {
                C[0] + R * Math.Cos(t) * X[0] + (RY == 0 ? R : RY) * Math.Sin(t) * Y[0],
                C[1] + R * Math.Cos(t) * X[1] + (RY == 0 ? R : RY) * Math.Sin(t) * Y[1],
                C[2] + R * Math.Cos(t) * X[2] + (RY == 0 ? R : RY) * Math.Sin(t) * Y[2],
            };
        }
    }

    internal sealed class NurbControlPoint
    {
        public double Weight;
        public double[] Point = new double[3];
    }

    internal sealed class HermiteNode
    {
        public double T;
        public double[] Point = new double[3];
        public double[] Tangent = new double[3];
    }

    /// <summary>
    /// Revit reads the points of an edge as the file stores them (see <see
    /// cref="EdgeRec.AuthoredPoints"/>); RVTIO_X_RAWUV=0 uses the resampled points of the legacy
    /// sampling.
    /// </summary>
    private static readonly bool MeshStoredEdgePoints = Environment.GetEnvironmentVariable("RVTIO_X_RAWUV") != "0";

    /// <summary>
    /// RVTIO_X_RAWPTS=0 hands the Revit decoder the point lists the dense path has rewritten
    /// (see <see cref="EdgeRec.AuthoredPoints"/>).
    /// </summary>
    private static readonly bool MeshRawEdgePoints = Environment.GetEnvironmentVariable("RVTIO_X_RAWPTS") != "0";

    internal sealed class EdgeRec
    {
        public uint Handle;
        public int QueueIndex = -1;
        public uint FaceA, FaceB;
        public uint NextA, NextB, PrevA, PrevB;
        public EdgeUV Start, EndP; // UV in both faces at each endpoint
        public List<EdgeUV> Interior = new(); // tessellation points along a curved edge
        public byte Flags;
        public bool HasEndpoints;
        public List<double[]>? SharedBoundaryPoints;
        /// <summary>
        /// The points as the file stores them, for an edge whose Start, Interior and EndP the
        /// legacy sampling replaced (see <see cref="ResampleCircularEdge"/>): Revit reads
        /// the stored points, and the last bits of the end points decide the arcs of the faces.
        /// </summary>
        public EdgeUV? AuthoredStart, AuthoredEndP;
        public List<EdgeUV>? AuthoredInterior;
        /// <summary>
        /// The points exactly as the file stores them (Revit's edge point list), captured
        /// before the dense path rewrites Start, Interior and EndP (arc resampling, the periodic
        /// unwrap and the refinement by the trim grids); null for an edge that was created later.
        /// </summary>
        public EdgeUV RawStart, RawEndP;
        public List<EdgeUV>? RawInterior;
        /// <summary>
        /// A Face of the edge is a ruled or Hermite surface: the free form track samples the edge
        /// once for all its users, from the list the dense path has refined with the grids of the
        /// free form faces (see <see cref="FreeformPoints"/>).
        /// </summary>
        public bool FreeformShared;
        public bool HasAuthoredInterior => (MeshRawEdgePoints && RawInterior != null && !FreeformShared
            ? RawInterior : FreeformInterior).Count > 0;
        private List<EdgeUV> FreeformInterior => MeshStoredEdgePoints ? AuthoredInterior ?? Interior : Interior;
        /// <summary>
        /// The points of the edge for the Revit decoder in order: start, interior points, end.
        /// </summary>
        public List<EdgeUV> AuthoredPoints()
        {
            if (MeshRawEdgePoints && RawInterior != null && !FreeformShared)
            {
                var raw = new List<EdgeUV>(RawInterior.Count + 2) { RawStart };
                raw.AddRange(RawInterior);
                raw.Add(RawEndP);
                return raw;
            }
            return FreeformPoints();
        }
        /// <summary>
        /// The points the free form track works on: a shared edge is sampled once and refined with
        /// the grids of the free form faces next to it.
        /// </summary>
        public List<EdgeUV> FreeformPoints()
        {
            var points = new List<EdgeUV>(Interior.Count + 2) { MeshStoredEdgePoints ? AuthoredStart ?? Start : Start };
            points.AddRange(FreeformInterior);
            points.Add(MeshStoredEdgePoints ? AuthoredEndP ?? EndP : EndP);
            return points;
        }
        public EdgeUV StoredStart => MeshRawEdgePoints && RawInterior != null && !FreeformShared ? RawStart : MeshStoredEdgePoints ? AuthoredStart ?? Start : Start;
        public EdgeUV StoredEndP => MeshRawEdgePoints && RawInterior != null && !FreeformShared ? RawEndP : MeshStoredEdgePoints ? AuthoredEndP ?? EndP : EndP;
    }

    /// <summary>Diagnostic counters (RVTIO_GREPV2_DBG): elements V2 produced world faces for.</summary>
    public static long DbgWorldElems, DbgWorldFaces;

    /// <summary>
    /// Reads the complete, suppressed planar boundary cache of a RoomElem. The caller must
    /// independently confirm the unique RoomElem owner and OST_Rooms category. This is not physical
    /// product geometry.
    /// </summary>
    internal static GRepData? ParseExactPlanarSpaceBoundary(
        byte[] data, int start, uint size, uint owner) =>
        ParseExactPlanarSpaceBoundary(data, start, size, owner, 0)
        ?? ParseExactPlanarSpaceBoundary(data, start, size, owner, LoopVertexTolerance)
        ?? ParseExactPlanarSpaceBoundary(data, start, size, owner, ShortCurveToleranceFt);

    /// <summary>
    /// The cache of a room whose boundary also holds analytic cylinders (a round wall), read like
    /// the planar cache; the Geometry node of the room then carries the Revit face meshes the
    /// body is written from.
    /// </summary>
    internal static GRepData? ParseCurvedSpaceBoundary(byte[] data, int start, uint size, uint owner)
    {
        var result = ParseExactPlanarSpaceBoundary(data, start, size, owner, 0, curved: true)
            ?? ParseExactPlanarSpaceBoundary(data, start, size, owner, LoopVertexTolerance, curved: true)
            ?? ParseExactPlanarSpaceBoundary(data, start, size, owner, ShortCurveToleranceFt, curved: true);
        if (result != null) result.CurvedSpaceBoundary = true;
        return result;
    }

    /// <summary>Revit's short-curve tolerance, inch.</summary>
    internal const double ShortCurveToleranceFt = 1.0 / 32 / 12;

    private static GRepData? ParseExactPlanarSpaceBoundary(
        byte[] data, int start, uint size, uint owner, double weldFeet, bool curved = false, bool literal = false)
    {
        long end = (long)start + 16 + size;
        if (start < 0 || start > data.Length - 16 || size < 4 || end > data.Length
            || owner == 0 || owner > int.MaxValue
            || BitConverter.ToUInt32(data, start) != owner
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != 2007
            || BitConverter.ToUInt32(data, (int)end - 4) != size)
            return null;
        var fields = TfBlockDeserializer.SerializationFields(2007);
        if (fields == null) return null;
        var c = new Cur
        {
            D = data, Pos = start + 14, End = (int)end - 4,
            BlockStart = start + 14, BlockEnd = (int)end,
            RootClass = 2007, ElementId = (int)owner,
            CurrentObjectHandle = (int)owner, CurrentObjectClass = 2007,
            ExactPlanarSpaceBoundary = true, WeldFeet = weldFeet, CurvedSpaceBoundary = curved,
        };
        try
        {
            foreach (var field in fields) ReadField(c, field, 0);
            for (int qi = 0; qi < c.Queue.Count; qi++)
            {
                // The cache holds one direct Geometry, with planar Faces,
                // Edges/EdgeLoops and optional face fill graphics only.
                if (c.Queue.Count > 100_000
                    || c.Queue[qi].Cls is not (2092 or 1589 or 1243 or 1254
                        or 522 or 2014 or 1848 or 1850) && !(curved && c.Queue[qi].Cls == 983))
                    return null;
                WalkQueueItem(c, qi);
            }
            if (c.Pos != c.End || c.Queue.Count == 0
                || c.Queue[0].Cls != 2092 || c.QueueLinks[0] != (-1, "m_subNodes")
                || c.Queue.Count(item => item.Cls == 2092) != 1
                || !c.ObjectGInfo.TryGetValue(c.Queue[0].V, out var geometryInfo)
                || geometryInfo.Flags != 0x0008C004u)
                return null;
            var faces = c.Queue.Select((item, index) => (item, index))
                .Where(entry => entry.item.Cls == 1589).ToList();
            if (faces.Count < 4 || c.FaceOrder.Count != faces.Count
                || c.DirectSurfaceByFace.Count != faces.Count
                || c.WalkedSurfaces.Count != faces.Count
                || c.Edges.Count < 6)
                return null;
            var handles = new HashSet<int>();
            foreach (var (item, index) in c.Queue.Select((item, index) => (item, index)))
            {
                if (item.Cls is not (2092 or 1589 or 1243)) continue;
                if (item.V <= 0 || !handles.Add(item.V)) return null;
                if (item.Cls == 2092) continue;
                if (c.QueueLinks[index] != (0, item.Cls == 1589 ? "m_pFaces" : "m_pEdges"))
                    return null;
            }
            var faceHandles = faces.Select(entry => (uint)entry.item.V).ToHashSet();
            foreach (var (item, _) in faces)
                if (!c.ObjectGInfo.TryGetValue(item.V, out var info)
                    || info.Flags != 0x40088204u
                    || !c.DirectSurfaceByFace.TryGetValue((uint)item.V, out var surface)
                    || surface.Kind != SurfKind.Plane && !(curved && surface.Kind == SurfKind.Cylinder))
                    return null;
            // (a cache of planes only is the planar reader's)
            if (curved && !faces.Any(entry => c.DirectSurfaceByFace[(uint)entry.item.V].Kind == SurfKind.Cylinder))
                return null;
            var edgeCounts = new Dictionary<uint, int>(faceHandles.Count);
            foreach (var edge in c.Edges)
            {
                if (!edge.HasEndpoints || edge.FaceA == edge.FaceB
                    || !faceHandles.Contains(edge.FaceA) || !faceHandles.Contains(edge.FaceB))
                    return null;
                edgeCounts[edge.FaceA] = edgeCounts.GetValueOrDefault(edge.FaceA) + 1;
                edgeCounts[edge.FaceB] = edgeCounts.GetValueOrDefault(edge.FaceB) + 1;
            }
            if (edgeCounts.Count != faceHandles.Count || edgeCounts.Values.Any(count => count < 3))
                return null;
            if (literal) return BuildLiteralSpaceBoundary(c);
            if (weldFeet > 0) RegisterSpaceWeldPoints(c);
            var result = BuildGeometry(c) ?? (curved ? null : BuildExactPlanarSpaceLoops(c));
            // BuildGeometry confirms coverage of every authored Face, including disjoint coplanar
            // regions. Export independently checks coordinate-edge closure and winding; the number
            // of output polygons is not the number of source Faces.
            if (result == null || result.Faces.Count + c.CollapsedFaces < faces.Count
                || result.Solids.Count != 1 || !result.Solids[0].IsClosed
                || result.Solids[0].FaceIndices.Distinct().Count() != result.Faces.Count
                || result.Faces.Any(face => face.Vertices is not { Count: >= 3 }
                    || face.Vertices.Concat((face.InnerVertices ?? []).SelectMany(ring => ring))
                        .Any(point => point.Length != 3 || point.Any(value => !double.IsFinite(value)))))
                return null;
            return result;
        }
        catch (DecodeStop) { return null; }
        catch (ArgumentException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Puts every vertex of every Edge into the weld clusters before any Face is built, so that a
    /// cluster merged late (a third point bridging two earlier ones) cannot change a segment that
    /// was already computed.
    /// </summary>
    private static void RegisterSpaceWeldPoints(Cur c)
    {
        foreach (uint face in c.FaceOrder)
        {
            if (!c.DirectSurfaceByFace.TryGetValue(face, out var surface) || surface.Kind != SurfKind.Plane) continue;
            var plane = ToPlane(surface);
            foreach (var edge in c.Edges)
            {
                if (edge.FaceA != face && edge.FaceB != face) continue;
                var points = new List<EdgeUV> { edge.Start };
                points.AddRange(edge.Interior);
                points.Add(edge.EndP);
                foreach (var point in points)
                    if ((edge.FaceA == face ? World(plane, point.UA, point.VA) : World(plane, point.UB, point.VB)) is { } world)
                        c.Weld(world);
            }
        }
    }

    private sealed class SpaceEdgeLoopRec
    {
        public uint Face, Next, Prev;
        public byte Open;
    }

    // Fallback for distinct authored loops that touch at a vertex. A coordinate-only graph merges
    // those loops and can lose a cycle.
    private static GRepData? BuildExactPlanarSpaceLoops(Cur c)
    {
        var loopIndices = Enumerable.Range(0, c.Queue.Count)
            .Where(index => c.Queue[index].Cls == 1254).ToList();
        if (loopIndices.Count == 0 || c.SpaceEdgeLoops.Count != loopIndices.Count) return null;
        var handles = c.Queue.Where(item => item.Cls is 2092 or 1589 or 1243)
            .Select(item => item.V).ToHashSet();
        var loopsByFace = new Dictionary<uint, List<int>>();
        foreach (int index in loopIndices)
        {
            int handle = c.Queue[index].V;
            if (handle <= 0 || !handles.Add(handle)
                || !c.ObjectGInfo.TryGetValue(handle, out var info)
                || info.Flags != 0x00080004u) return null;
            int current = index;
            while (c.Queue[current].Cls == 1254)
            {
                var link = c.QueueLinks[current];
                if (link.ParentQueueIndex < 0 || link.ParentQueueIndex >= current) return null;
                ushort parentClass = c.Queue[link.ParentQueueIndex].Cls;
                if (!(parentClass == 1254 && link.Field == "m_nextLoop")
                    && !(parentClass == 1589 && link.Field == "m_pFirstLoop")) return null;
                current = link.ParentQueueIndex;
            }
            uint face = (uint)c.Queue[current].V;
            if (!c.SpaceEdgeLoops.TryGetValue(index, out var loop)
                || loop.Face != face || loop.Open != 0) return null;
            if (!loopsByFace.TryGetValue(face, out var indices)) loopsByFace[face] = indices = [];
            indices.Add(index);
        }
        if (!c.FaceOrder.ToHashSet().SetEquals(loopsByFace.Keys)) return null;
        var result = new GRepData();
        var faceHandles = new List<uint>();
        var bounds = ValidBox(c.RootTightBoundingBox) ? c.RootTightBoundingBox : c.RootBoundingBox;
        foreach (uint face in c.FaceOrder)
        {
            var plane = ToPlane(c.DirectSurfaceByFace[face]);
            var owned = c.Edges.Where(edge => edge.FaceA == face || edge.FaceB == face)
                .ToDictionary(edge => edge.Handle);
            uint Next(EdgeRec edge) => edge.FaceA == face ? edge.NextA : edge.NextB;
            uint Prev(EdgeRec edge) => edge.FaceA == face ? edge.PrevA : edge.PrevB;
            var used = new HashSet<uint>();
            var rings = new List<List<double[]>>();
            var allSegments = new List<(double[] A, double[] B)>();
            foreach (int index in loopsByFace[face])
            {
                var loop = c.SpaceEdgeLoops[index];
                uint sentinel = (uint)c.Queue[index].V;
                uint previous = sentinel, current = loop.Next;
                var segments = new List<(double[] A, double[] B)>();
                while (current != sentinel)
                {
                    if (!owned.TryGetValue(current, out var edge)
                        || !used.Add(current) || Prev(edge) != previous) return null;
                    double[]? Point(EdgeUV point) => edge.FaceA == face
                        ? World(plane, point.UA, point.VA) : World(plane, point.UB, point.VB);
                    var points = new List<EdgeUV> { edge.Start };
                    points.AddRange(edge.Interior); points.Add(edge.EndP);
                    for (int i = 0; i + 1 < points.Count; i++)
                    {
                        var a = Point(points[i]); var b = Point(points[i + 1]);
                        if (a == null || b == null) return null;
                        if (c.WeldFeet > 0)
                        {
                            a = c.Weld(a);
                            b = c.Weld(b);
                            if (a[0] == b[0] && a[1] == b[1] && a[2] == b[2]) continue; // an edge shorter than the weld tolerance has collapsed
                        }
                        segments.Add((a, b));
                    }
                    previous = current;
                    current = Next(edge);
                }
                if (previous != loop.Prev) return null;
                // A loop whose vertices all fall into one or two weld clusters is a sliver of the
                // cache with no area.
                if (c.WeldFeet > 0 && segments.SelectMany(s => new[] { s.A, s.B })
                        .Select(point => (point[0], point[1], point[2])).Distinct().Count() < 3)
                    continue;
                var found = OrderRingsW(segments);
                if (found.Count != 1 || !HasExactPlanarTrimCoverage(segments,
                    [new GRepFace { Vertices = found[0] }])) return null;
                rings.Add(found[0]);
                allSegments.AddRange(segments);
            }
            if (!used.SetEquals(owned.Keys)) return null;
            if (rings.Count == 0)
            {
                c.CollapsedFaces++;
                continue;
            }
            var regions = ClassifyCoplanarRings(rings, exactPredicates: true).Select(region => new GRepFace
            {
                Vertices = region.Outer,
                InnerVertices = region.Holes.Count > 0 ? region.Holes : null,
                Origin = region.Outer[0], XVector = region.Normal,
                PlaneX = (double[])plane.X.Clone(), PlaneY = (double[])plane.Y.Clone(),
                SourceFace = face,
                SourceLoops = rings,
                PlaneSurface = true,
            }).ToList();
            // Degree two was confirmed separately for each serialized loop. Different loops may share
            // vertices, but never segments or partially emitted rings.
            if (!HasExactPlanarTrimCoverage(allSegments, regions, requireDisjointCycles: false)
                || regions.Any(region => !ReferenceEquals(region, ClipToSerializedBounds(region, bounds))))
                return null;
            result.Faces.AddRange(regions);
            faceHandles.AddRange(Enumerable.Repeat(face, regions.Count));
        }
        result.Solids = BuildTopologicalSolids(result.Faces, faceHandles,
            c.Edges.Select(edge => (edge.FaceA, edge.FaceB)));
        var vertices = result.Faces.SelectMany(face => face.Vertices!
            .Concat((face.InnerVertices ?? []).SelectMany(ring => ring))).ToList();
        if (vertices.Count == 0) return null;
        result.BoundingBox = bounds is { Length: 6 } ? (double[])bounds.Clone()
            : Enumerable.Range(0, 3).Select(axis => vertices.Min(point => point[axis]))
                .Concat(Enumerable.Range(0, 3).Select(axis => vertices.Max(point => point[axis]))).ToArray();
        return result;
    }

    /// <summary>
    /// Face handle to its EdgeLoop queue indices in serialized order (Face.m_pFirstLoop, then the
    /// m_nextLoop chain), for the Faces whose whole loop list was walked: every loop is closed and
    /// the last one ends the chain.
    /// </summary>
    private static Dictionary<uint, List<int>> CompleteFaceLoops(Cur c)
    {
        var byFace = new Dictionary<uint, List<(int Depth, int Index)>>();
        foreach (var (index, loop) in c.SpaceEdgeLoops)
        {
            if (index >= c.Queue.Count || index >= c.QueueLinks.Count || c.Queue[index].Cls is not (1254 or 1257)) continue;
            int depth = 0, current = index;
            uint face = 0;
            while (current < c.QueueLinks.Count)
            {
                var (parent, field) = c.QueueLinks[current];
                if (parent < 0 || parent >= current) break;
                if (c.Queue[parent].Cls is 1254 or 1257 && field == "m_nextLoop") { depth++; current = parent; continue; }
                if (c.Queue[parent].Cls == 1589 && field == "m_pFirstLoop") face = (uint)c.Queue[parent].V;
                break;
            }
            if (face == 0) continue;
            if (!byFace.TryGetValue(face, out var list)) byFace[face] = list = [];
            list.Add((depth, index));
        }
        var result = new Dictionary<uint, List<int>>();
        foreach (var (face, list) in byFace)
        {
            list.Sort();
            var indices = list.Select(item => item.Index).ToList();
            if (!c.FaceHasFirstLoop.GetValueOrDefault(face)
                || list.Where((item, position) => item.Depth != position).Any()
                || !c.LastLoops.Contains(indices[^1])
                || indices.Any(index => c.SpaceEdgeLoops[index].Open != 0 || c.SpaceEdgeLoops[index].Face != face))
                continue;
            result[face] = indices;
        }
        return result;
    }

    /// <summary>Edge handle to the Edge record; null marks a handle that two Edges claim.</summary>
    private static Dictionary<uint, EdgeRec?> EdgesByHandle(Cur c)
    {
        var map = new Dictionary<uint, EdgeRec?>();
        foreach (var edge in c.Edges)
            if (edge.Handle != 0 && !map.TryAdd(edge.Handle, edge)) map[edge.Handle] = null;
        return map;
    }

    private const double LoopVertexTolerance = 0.0005233832795;

    /// <summary>
    /// Boundary rings of a planar Face from its serialized EdgeLoops: each loop is the doubly
    /// linked list Loop.m_next, Edge.m_next[side]... back to the loop, so the Edges that name the
    /// Face as a neighbour but belong to no loop of it (leftovers of a cut) never reach the ring
    /// search.
    /// </summary>
    private static List<List<double[]>>? TryOrderLoopRings(Cur c, uint face, List<int> loopIndices,
        Dictionary<uint, EdgeRec?> edges, PlaneRec plane, List<(double[] A, double[] B)> allSegments,
        HashSet<uint> loopEdges)
    {
        const double Tol = 1e-5 / 0.3048; // the OrderRingsW lattice
        static (long X, long Y, long Z) Key(double[] p) =>
            ((long)Math.Round(p[0] / Tol), (long)Math.Round(p[1] / Tol), (long)Math.Round(p[2] / Tol));
        var rings = new List<List<double[]>>();
        var used = new HashSet<uint>();
        foreach (int index in loopIndices)
        {
            var loop = c.SpaceEdgeLoops[index];
            uint sentinel = (uint)c.Queue[index].V;
            uint previous = sentinel, current = loop.Next;
            var segments = new List<(double[] A, double[] B)>();
            var lines = new List<List<double[]>>();
            while (current != sentinel)
            {
                if (!edges.TryGetValue(current, out var edge) || edge == null || !used.Add(current)
                    || edge.FaceA == edge.FaceB || edge.FaceA != face && edge.FaceB != face) return null;
                bool sideA = edge.FaceA == face;
                if ((sideA ? edge.PrevA : edge.PrevB) != previous) return null;
                var points = new List<EdgeUV>(edge.Interior.Count + 2) { edge.Start };
                points.AddRange(edge.Interior);
                points.Add(edge.EndP);
                var line = new List<double[]>(points.Count);
                for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                {
                    var point = points[pointIndex];
                    var world = edge.SharedBoundaryPoints is { } shared && shared.Count == points.Count
                        ? shared[pointIndex]
                        : sideA ? World(plane, point.UA, point.VA) : World(plane, point.UB, point.VB);
                    if (world == null) return null;
                    line.Add(c.WeldFeet > 0 ? c.Weld(world) : world);
                }
                for (int i = 0; i + 1 < line.Count; i++) segments.Add((line[i], line[i + 1]));
                lines.Add(line);
                previous = current;
                current = sideA ? edge.NextA : edge.NextB;
            }
            if (previous != loop.Prev || !TryJoinLoopEdges(lines, out var ring)) return null;
            var distinct = new HashSet<((long, long, long), (long, long, long))>();
            for (int i = 0; i < ring.Count; i++)
            {
                var ka = Key(ring[i]); var kb = Key(ring[(i + 1) % ring.Count]);
                distinct.Add(ka.CompareTo(kb) < 0 ? (ka, kb) : (kb, ka));
            }
            if (distinct.Count != ring.Count) return null; // a spur or a repeated side: not one simple cycle
            rings.Add(ring);
            allSegments.AddRange(segments);
        }
        loopEdges.UnionWith(used);
        return rings;

        // Points of one OrderRingsW lattice bin that follow each other collapse into one vertex; a
        // ring needs three.
        static bool TryJoinLoopEdges(List<List<double[]>> lines, out List<double[]> ring)
        {
            ring = [];
            static double Distance(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0])
                + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
            if (lines.Count == 0 || lines.Any(line => line.Count < 2)) return false;
            var first = lines[0];
            if (lines.Count > 1)
            {
                // The first Edge ends where the second one starts or ends.
                var next = lines[1];
                double Reach(double[] point) => Math.Min(Distance(point, next[0]), Distance(point, next[^1]));
                if (Reach(first[0]) < Reach(first[^1])) first = Enumerable.Reverse(first).ToList();
            }
            var joined = new List<double[]>(first);
            for (int i = 1; i < lines.Count; i++)
            {
                var line = lines[i];
                double toStart = Distance(joined[^1], line[0]), toEnd = Distance(joined[^1], line[^1]);
                if (Math.Min(toStart, toEnd) > LoopVertexTolerance) return false;
                if (toEnd < toStart) line = Enumerable.Reverse(line).ToList();
                joined.AddRange(line.Skip(1));
            }
            if (Distance(joined[^1], joined[0]) > LoopVertexTolerance) return false;
            joined.RemoveAt(joined.Count - 1);
            foreach (var point in joined)
                if (ring.Count == 0 || Key(point) != Key(ring[^1])) ring.Add(point);
            if (ring.Count > 1 && Key(ring[0]) == Key(ring[^1])) ring.RemoveAt(ring.Count - 1);
            return ring.Count >= 3;
        }
    }

    internal enum BodyMeasure { None, VisibleVolume, RawBodies, DirectionalRawSurface }

    /// <summary>
    /// Decodes a GRep block at the Fine detail level, and also at the Coarse level when the block
    /// holds detail-level alternatives that differ (<see cref="GRepData.Coarse"/>).
    /// </summary>
    /// <param name="nativeWallBody">Use native Low edge sampling only after confirming a unique own wall root and GRep.</param>
    public static GRepData? Parse(byte[] d, int blockStart, uint dataSize, bool nativeWallBody = false, bool requireCompleteWalk = false,
        bool measureSourceNodes = false, bool measureLighting = false, bool measureFills = false, BodyMeasure bodyMeasure = BodyMeasure.None)
    {
        if (!requireCompleteWalk && !measureSourceNodes && !measureLighting && !measureFills && bodyMeasure == BodyMeasure.None)
            return ParseCore(d, blockStart, dataSize, nativeWallBody, includeDetailLevels: true);
        var fine = ParseCore(d, blockStart, dataSize, nativeWallBody, requireCompleteWalk: requireCompleteWalk, measureSourceNodes: measureSourceNodes,
            measureLighting: measureLighting, measureFills: measureFills, bodyMeasure: bodyMeasure);
        if (fine is not { HasDetailLevelAlternatives: true }) return fine;
        try
        {
            t_detailLevel = CoarseDetailLevel;
            var coarse = ParseCore(d, blockStart, dataSize, nativeWallBody, requireCompleteWalk: requireCompleteWalk, measureSourceNodes: measureSourceNodes,
                measureLighting: measureLighting, measureFills: measureFills, bodyMeasure: bodyMeasure);
            if (coarse == null && requireCompleteWalk) return null;
            coarse ??= new GRepData();
            if (!IsSameDetailLevelGeometry(fine, coarse)) fine.Coarse = coarse;
            t_detailLevel = 2;
            var medium = ParseCore(d, blockStart, dataSize, nativeWallBody, requireCompleteWalk: requireCompleteWalk, measureSourceNodes: measureSourceNodes,
                measureLighting: measureLighting, measureFills: measureFills, bodyMeasure: bodyMeasure);
            if (medium == null && requireCompleteWalk) return null;
            medium ??= new GRepData();
            if (!IsSameDetailLevelGeometry(fine, medium)) fine.Medium = medium;
        }
        finally { t_detailLevel = 0; }
        return fine;
    }

    private static bool SameVertices(List<double[]>? a, List<double[]>? b)
    {
        if ((a?.Count ?? 0) != (b?.Count ?? 0)) return false;
        if (a == null || b == null) return true;
        for (int i = 0; i < a.Count; i++)
            for (int k = 0; k < 3 && k < a[i].Length && k < b[i].Length; k++)
                if (Math.Abs(a[i][k] - b[i][k]) > 1e-9) return false;
        return true;
    }

    /// <summary>
    /// True when the two levels hold the same faces and the same nested instances, so that the
    /// Coarse level need not be kept.
    /// </summary>
    internal static bool IsSameDetailLevelGeometry(GRepData fine, GRepData coarse) =>
        fine.LightingBodyMeasures == coarse.LightingBodyMeasures
        && fine.SourceBodyMeasures == coarse.SourceBodyMeasures && fine.SourceCapAreas == coarse.SourceCapAreas
        && fine.TrimmedBodyMeasures == coarse.TrimmedBodyMeasures
        && fine.FilledArea == coarse.FilledArea
        && fine.Faces.Count == coarse.Faces.Count
        && fine.Faces.Zip(coarse.Faces).All(pair => SameVertices(pair.First.Vertices, pair.Second.Vertices))
        && fine.Instances.Count == coarse.Instances.Count
        && fine.Instances.Zip(coarse.Instances).All(pair => pair.First.SymbolElemId == pair.Second.SymbolElemId
            && pair.First.Transform.SequenceEqual(pair.Second.Transform));

    internal static GRepData? ParseNativeWallBodyCandidate(byte[] data, int start, uint size)
        => ParseCore(data, start, size, nativeWallBody: true, includeDetailLevels: true, requireCylinderEdgeBounds: true);

    private static GRepData? ParseCore(byte[] d, int blockStart, uint dataSize, bool nativeWallBody, bool includeNonVisible = false,
        bool requireCompleteWalk = false, bool measureSourceNodes = false, bool measureLighting = false, bool measureFills = false,
        BodyMeasure bodyMeasure = BodyMeasure.None, bool includeDetailLevels = false, bool requireCylinderEdgeBounds = false)
    {
        int payloadStart = blockStart + 14;
        int payloadEnd = (int)Math.Min((long)blockStart + 16 + dataSize, d.Length);
        if (payloadEnd - payloadStart < 8) return null;
        ushort classId = BitConverter.ToUInt16(d, blockStart + 12);
        if (TfBlockDeserializer.SerializationFields(classId) is not { } rootFields) return null;

        int elementId = blockStart + 4 <= d.Length ? BitConverter.ToInt32(d, blockStart) : 0;
        bool targeted = (Environment.GetEnvironmentVariable("RVTIO_GREPV2_DBG_ID") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => int.TryParse(token, out int dbgId) && dbgId == elementId);
        bool dbg = Environment.GetEnvironmentVariable("RVTIO_GREPV2_DBG") == "1" || targeted;
        var c = new Cur
        {
            D = d, Pos = payloadStart, End = payloadEnd - 4,
            BlockStart = payloadStart, BlockEnd = payloadEnd,
            ElementId = elementId, Debug = dbg,
            NativeWallBody = nativeWallBody,
            IncludeNonVisible = includeNonVisible,
            RootClass = classId,
            CurrentObjectHandle = elementId,
            CurrentObjectClass = classId,
        };
        if (targeted)
        {
            var raw = new byte[payloadEnd - blockStart];
            Array.Copy(d, blockStart, raw, 0, raw.Length);
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"grep_schema_{elementId}.bin"), raw);
        }
        try
        {
            foreach (var f in rootFields) ReadField(c, f, 0);
            if (dbg)
                DiagnosticSink.WriteLine($"  V2 eid={elementId} root bbox=[{string.Join(',', c.RootBoundingBox ?? [])}] tight=[{string.Join(',', c.RootTightBoundingBox ?? [])}]");
            int resyncs = 0;
            for (int qi = 0; qi < c.Queue.Count; qi++)
            {
                int itemStart = c.Pos;
                if (targeted) c.DebugItemStarts.Add(itemStart);
                try { WalkQueueItem(c, qi); }
                catch (DecodeStop)
                {
                    if (requireCompleteWalk) return null;
                    if (dbg)
                    {
                        var failed = qi < c.Queue.Count ? c.Queue[qi] : default;
                        var previous = qi > 0 && qi - 1 < c.Queue.Count ? c.Queue[qi - 1] : default;
                        int hexStart = Math.Max(c.BlockStart, itemStart);
                        int hexLen = Math.Min(192, c.BlockEnd - hexStart);
                        DiagnosticSink.WriteLine($"  V2 decode-stop qi={qi} cls={failed.Cls} handle={failed.V} prevCls={previous.Cls} prevHandle={previous.V} start={itemStart} pos={c.Pos} bytes={BitConverter.ToString(c.D, hexStart, hexLen)}");
                        var failedSchema = TfBlockDeserializer.SerializationFields(failed.Cls);
                        if (failedSchema != null)
                            DiagnosticSink.WriteLine($"    schema {failed.Cls}: {string.Join(" | ", failedSchema.Select(f => $"{f.Declaring}.{f.Name}:t{f.Type:X2}/m{f.Mods:X2}/r{f.Ref}/n{f.Cnt}"))}");
                        File.WriteAllLines(Path.Combine(Path.GetTempPath(), $"grep_schema_{elementId}_queue.txt"),
                            c.Queue.Select((x, index) => $"{index}\t{x.Cls}\t{x.V}\t{(index < c.DebugItemStarts.Count ? c.DebugItemStarts[index] : -1)}"));
                    }
                    if (MeshDiagPath != null && c.StopNote == null && qi < c.Queue.Count)
                        c.StopNote = $"qi={qi}/{c.Queue.Count} cls={c.Queue[qi].Cls}"
                            + $"({R22ClassDefs.Names.GetValueOrDefault(c.Queue[qi].Cls)}) pos={itemStart - c.BlockStart}";
                    // Format-variant boundary or a zero gap left by desync recovery: scan forward
                    // for the next GInfoBlob anchor and relock the queue there.
                    if (++resyncs > 64 || !TryResync(c, ref qi)) throw;
                }
            }
        }
        catch (DecodeStop) { if (requireCompleteWalk) return null; if (dbg) DiagnosticSink.WriteLine($"  V2 stop at pos={c.Pos}/{c.End} faces={c.FaceCount} edges={c.Edges.Count} queue={c.Queue.Count}"); }
        catch (Exception ex) { if (requireCompleteWalk) return null; if (dbg) DiagnosticSink.WriteLine($"  V2 exc {ex.GetType().Name}: {ex.Message} at pos={c.Pos} faces={c.FaceCount} edges={c.Edges.Count}"); }

        if (requireCompleteWalk && c.Pos != c.End) return null;

        // Grammar-first harvest of a large unwalked tail: post-gap objects belong to queue entries
        // whose parents were never walked (never enqueued) - the queue can't reach them, but the
        // object grammar identifies them directly.
        if (c.BlockEnd - c.Pos > 4096) HarvestTail(c);

        if (dbg) DiagnosticSink.WriteLine($"  V2 eid={elementId} done pos={c.Pos}/{c.End} faces={c.FaceCount} edges={c.Edges.Count} direct={c.DirectSurfaceByFace.Count} harvested={c.HarvestSurf.Count}");
        if (targeted)
        {
            foreach (var conditionGroup in c.Queue
                .Select((item, index) => (item.Cls, item.V, Index: index))
                .Where(item => item.Cls is >= 1992 and <= 2003)
                .GroupBy(item => item.Cls).OrderBy(group => group.Key))
            {
                var valueGroups = conditionGroup.Select(item => c.QueueInts.GetValueOrDefault(item.Index))
                    .Where(values => values != null)
                    .Select(values => string.Join(',', values!.OrderBy(pair => pair.Key)
                        .Select(pair => $"{pair.Key}={pair.Value}")))
                    .GroupBy(value => value).OrderByDescending(group => group.Count())
                    .Select(group => $"[{group.Key}]x{group.Count()}");
                DiagnosticSink.WriteLine($"    condition-class {conditionGroup.Key}: count={conditionGroup.Count()} values={string.Join(' ', valueGroups)}");
            }
            var graphPath = Path.Combine(Path.GetTempPath(), $"grep_schema_{elementId}_{classId}_graph.tsv");
            File.WriteAllLines(graphPath, new[] { "queue\tclass\tname\thandle\tparent\tfield\tginfo\tints\tdoubles\tbytes" }
                .Concat(c.Queue.Select((item, index) =>
                {
                    var link = index < c.QueueLinks.Count ? c.QueueLinks[index] : (-2, "<missing>");
                    string ints = c.QueueInts.TryGetValue(index, out var iv)
                        ? string.Join(';', iv.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")) : "";
                    string doubles = c.QueueDoubles.TryGetValue(index, out var dv)
                        ? string.Join(';', dv.OrderBy(x => x.Key).Select(x => $"{x.Key}=[{string.Join(',', x.Value.Select(v => v.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)))}]")) : "";
                    string bytes = c.QueueBytes.TryGetValue(index, out var bv)
                        ? string.Join(';', bv.OrderBy(x => x.Key).Select(x => $"{x.Key}=[{string.Join(',', x.Value)}]")) : "";
                    string name = R22ClassDefs.Names.GetValueOrDefault(item.Cls) ?? "";
                    string ginfo = c.ObjectGInfo.TryGetValue(item.V, out var gi)
                        ? $"tag={gi.Tag};control={gi.Control};category={gi.Category};flags=0x{gi.Flags:X8}" : "";
                    return $"{index}\t{item.Cls}\t{name}\t{item.V}\t{link.Item1}\t{link.Item2}\t{ginfo}\t{ints}\t{doubles}\t{bytes}";
                })));
            DiagnosticSink.WriteLine($"    object graph: {graphPath}");
            var edgePath = Path.Combine(Path.GetTempPath(), $"grep_schema_{elementId}_{classId}_edges.tsv");
            File.WriteAllLines(edgePath,
                new[] { "queue\thandle\tfaceA\tfaceB\tnextA\tnextB\tprevA\tprevB\tflags\tginfo\tinterior" }
                    .Concat(c.Edges.Select(edge =>
                    {
                        string ginfo = c.ObjectGInfo.TryGetValue((int)edge.Handle, out var gi)
                            ? $"tag={gi.Tag};control={gi.Control};category={gi.Category};flags=0x{gi.Flags:X8}"
                            : "";
                        return $"{edge.QueueIndex}\t{edge.Handle}\t{edge.FaceA}\t{edge.FaceB}"
                            + $"\t{edge.NextA}\t{edge.NextB}\t{edge.PrevA}\t{edge.PrevB}"
                            + $"\t0x{edge.Flags:X2}\t{ginfo}\t{edge.Interior.Count}";
                    })));
            var edgePointPath = Path.Combine(Path.GetTempPath(), $"grep_schema_{elementId}_{classId}_edge_points.tsv");
            var edgePointLines = new List<string>
            {
                "queue\thandle\tpoint\tkind\tfaceA\tuA\tvA\tfaceB\tuB\tvB",
            };
            string Number(double value) => value.ToString("G17",
                System.Globalization.CultureInfo.InvariantCulture);
            foreach (var edge in c.Edges)
            {
                var points = new List<(string Kind, EdgeUV Point)> { ("start", edge.Start) };
                points.AddRange(edge.Interior.Select(point => ("interior", point)));
                points.Add(("end", edge.EndP));
                for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                {
                    var point = points[pointIndex];
                    edgePointLines.Add($"{edge.QueueIndex}\t{edge.Handle}\t{pointIndex}\t{point.Kind}"
                        + $"\t{edge.FaceA}\t{Number(point.Point.UA)}\t{Number(point.Point.VA)}"
                        + $"\t{edge.FaceB}\t{Number(point.Point.UB)}\t{Number(point.Point.VB)}");
                }
            }
            File.WriteAllLines(edgePointPath, edgePointLines);
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), $"grep_schema_{elementId}_loops.tsv"),
                new[] { "queue\thandle\tparentFaceOrLoop\tfield\tpFace\tnext\tprev\topen" }
                    .Concat(c.SpaceEdgeLoops.OrderBy(pair => pair.Key).Select(pair =>
                    {
                        var (parent, field) = pair.Key < c.QueueLinks.Count ? c.QueueLinks[pair.Key] : (-1, "");
                        string owner = parent >= 0 && parent < c.Queue.Count ? $"{c.Queue[parent].Cls}/{c.Queue[parent].V}" : "-";
                        return $"{pair.Key}\t{c.Queue[pair.Key].V}\t{owner}\t{field}\t{pair.Value.Face}\t{pair.Value.Next}\t{pair.Value.Prev}\t{pair.Value.Open}";
                    })));
            DiagnosticSink.WriteLine($"    authored edges: {edgePath}; points: {edgePointPath}");
            foreach (var filter in c.Queue.Select((item, index) => (item.Cls, item.V, Index: index))
                .Where(item => item.Cls == 2015))
            {
                var childIndices = c.QueueChildren.GetValueOrDefault(filter.Index) ?? [];
                var conditions = childIndices.Where(childIndex => c.QueueLinks[childIndex].Field == "m_oConditions").ToList();
                if (conditions.Count == 0) continue;
                DiagnosticSink.WriteLine($"    filter q{filter.Index}/h{filter.V}: "
                    + string.Join(" | ", childIndices.Select(childIndex =>
                    {
                        var child = c.Queue[childIndex];
                        return $"{c.QueueLinks[childIndex].Field}->q{childIndex}/h{child.V}/{child.Cls}";
                    })));
                foreach (int conditionIndex in conditions)
                {
                    var condition = c.Queue[conditionIndex];
                    var values = c.QueueInts.GetValueOrDefault(conditionIndex);
                    var vectors = c.QueueDoubles.GetValueOrDefault(conditionIndex);
                    var bytes = c.QueueBytes.GetValueOrDefault(conditionIndex);
                    DiagnosticSink.WriteLine($"      condition q{conditionIndex}/h{condition.V}/{condition.Cls}: "
                        + (values == null ? "" : string.Join(',', values.Select(pair => $"{pair.Key}={pair.Value}")))
                        + (vectors == null ? "" : " " + string.Join(',', vectors.Select(pair => $"{pair.Key}=[{string.Join(',', pair.Value.Select(v => v.ToString("G8")))}]")))
                        + (bytes == null ? "" : " " + string.Join(',', bytes.Select(pair => $"{pair.Key}=[{string.Join(',', pair.Value)}]"))));
                }
            }
        }
        // BuildGeometry computes MeshEdgeBounds only when a directly decoded cylinder is present.
        // The wall candidate must have these bounds on every face.
        if (requireCylinderEdgeBounds && !c.DirectSurfaceByFace.Values.Any(surface => surface.Kind == SurfKind.Cylinder)
            && !IsDecodeTracingEnabled())
            return null;
        var built = BuildRepresentation(c, requireCompleteWalk, measureSourceNodes, measureLighting, measureFills, bodyMeasure);
        if (built != null && !includeNonVisible && t_detailLevel == 0
            && c.Queue.Any(node => (node.Cls == SolidClass || ShouldAttachSymbolGeometry(node.Cls))
                && c.ObjectGInfo.TryGetValue(node.V, out var info) && IsHiddenInstanceVariant(info.Flags)))
        {
            // Retain only this block, not the decompressed partition; decode quantities only when a consumer needs them.
            var source = d.AsSpan(blockStart, payloadEnd - blockStart).ToArray();
            built.SetCoarseQuantityFactory(() =>
            {
                int previousDetailLevel = t_detailLevel;
                try
                {
                    t_detailLevel = CoarseDetailLevel;
                    return ParseCore(source, 0, dataSize, nativeWallBody, includeNonVisible: true);
                }
                finally { t_detailLevel = previousDetailLevel; }
            });
        }
        if (MeshDiagPath != null) WriteMeshDiag(c, built, payloadEnd - blockStart);
        if (includeDetailLevels && built is { HasDetailLevelAlternatives: true })
        {
            // Detail conditions filter the decoded graph during geometry building.
            // Full diagnostics also report the field walk for each representation.
            bool replayDiagnostics = c.Debug || MeshDiagPath != null
                || Environment.GetEnvironmentVariable("RVTIO_TOPO") is { Length: > 0 };
            try
            {
                var coarse = BuildAtLevel(CoarseDetailLevel);
                if (!ReferenceEquals(built, coarse) && !IsSameDetailLevelGeometry(built, coarse)) built.Coarse = coarse;
                var medium = BuildAtLevel(2);
                if (!ReferenceEquals(built, medium) && !IsSameDetailLevelGeometry(built, medium)) built.Medium = medium;
            }
            finally { t_detailLevel = 0; }

            GRepData BuildAtLevel(int level)
            {
                // A walk can first diverge only at a filter it already reached at Fine.
                if (!replayDiagnostics && c.ModelFilters != null
                    && c.ModelFilters.Values.All(filter => filter.Allows(FineDetailLevel) == filter.Allows(level)))
                    return built!;
                t_detailLevel = level;
                c.SawDetailCondition = false;
                if (!replayDiagnostics)
                    foreach (var edge in c.Edges)
                        if (edge.RawInterior is { } raw)
                        {
                            // Tessellation rewrites the working points and their cached samples.
                            edge.Start = edge.RawStart;
                            edge.EndP = edge.RawEndP;
                            edge.Interior = raw;
                            edge.AuthoredStart = edge.AuthoredEndP = null;
                            edge.AuthoredInterior = null;
                            edge.SharedBoundaryPoints = null;
                            edge.FreeformShared = false;
                        }
                return (replayDiagnostics ? ParseCore(d, blockStart, dataSize, nativeWallBody) : BuildRepresentation(c))
                    ?? new GRepData();
            }
        }
        return built;
    }

    private static GRepData? BuildRepresentation(Cur c, bool requireCompleteWalk = false,
        bool measureSourceNodes = false, bool measureLighting = false, bool measureFills = false,
        BodyMeasure bodyMeasure = BodyMeasure.None)
    {
        var built = BuildGeometry(c, requireCompleteWalk, measureSourceNodes, measureLighting, measureFills, bodyMeasure);
        if (built == null) return null;
        int faces = 0, edges = 0, solids = 0;
        var counts = KeepStoredClassCounts ? new Dictionary<ushort, int>() : null;
        foreach (var (cls, _) in c.Queue)
        {
            switch (cls)
            {
                case GFaceClass: faces++; break;
                case EdgeClass: edges++; break;
                case SolidClass: solids++; break;
            }
            if (counts != null) counts[cls] = counts.GetValueOrDefault(cls) + 1;
        }
        built.QueueSummary = (faces, edges, solids);
        built.QueueClassCounts = counts;
        if (faces == 0)
        {
            if (measureLighting) built.LightingBodyMeasures = (0, 0, 0);
            if (bodyMeasure != BodyMeasure.None) built.SourceBodyMeasures = (0, 0, 0);
        }
        built.HasDetailLevelAlternatives = c.SawDetailCondition;
        built.RootBoundingBox = ValidBox(c.RootBoundingBox) ? c.RootBoundingBox : null;
        return built;
    }

    /// <summary>Keep per-class object counts for the IFC type key diagnostics.</summary>
    private static readonly bool KeepStoredClassCounts = Environment.GetEnvironmentVariable("RVTIO_TYPEKEY_DIAG") is { Length: > 0 };

    private static readonly string? MeshDiagPath =
        Environment.GetEnvironmentVariable("RVTIO_MESH_DIAG") is { Length: > 0 } meshDiag ? meshDiag : null;

    internal static bool IsDecodeTracingEnabled() => MeshDiagPath != null
        || Environment.GetEnvironmentVariable("RVTIO_GREPV2_DBG") == "1"
        || Environment.GetEnvironmentVariable("RVTIO_GREPV2_DBG_ID") is { Length: > 0 }
        || Environment.GetEnvironmentVariable("RVTIO_TOPO") is { Length: > 0 }
        || Environment.GetEnvironmentVariable("RVTIO_GREP_SLOW") is { Length: > 0 };
    private static readonly object MeshDiagLock = new();
    private static readonly HashSet<string> MeshDiagSeen = new();
    private static readonly HashSet<ushort> FacetedTopologyClasses = MeshDiagPath == null ? []
        : TfBlockDeserializer.Schemas.Keys.Where(id => TfBlockDeserializer.IsSubclassOf(id, 1207))
            .Select(id => (ushort)id).ToHashSet();

    private static void WriteMeshDiag(Cur c, GRepData? built, int blockSize)
    {
        int polyMeshNodes = c.Queue.Count(item => item.Cls == 2038);
        // Blocks that hold only curves (detail lines, model lines) are not geometry candidates.
        if (polyMeshNodes == 0 && c.FtWalked.Count == 0
            && (built != null || !c.Queue.Any(item => item.Cls is 2092 or 1589 or 1243))
            && !c.FtNotes.Any(note => note.StartsWith("instance-", StringComparison.Ordinal)))
            return;
        string forms = string.Join(',', c.FtWalked.OrderBy(pair => pair.Key)
            .Select(pair => $"{pair.Key}({R22ClassDefs.Names.GetValueOrDefault(pair.Key)})x{pair.Value}"));
        string queue = string.Join(',', c.Queue.GroupBy(item => item.Cls).OrderByDescending(group => group.Count())
            .Take(8).Select(group => $"{group.Key}({R22ClassDefs.Names.GetValueOrDefault(group.Key)})x{group.Count()}"));
        string line = $"eid={c.ElementId} size={blockSize} root={c.RootClass} result={(built == null ? "null" : "geometry")} "
            + $"polymeshNodes={polyMeshNodes} forms=[{forms}] decoded={c.PolyMeshes.Count} "
            + $"dropped(owner={c.MeshDropOwner},branch={c.MeshDropBranch},flags={c.MeshDropFlags}) "
            + $"notes=[{string.Join(';', c.FtNotes.Take(12))}] stop={c.StopNote ?? "-"} pos={c.Pos - c.BlockStart}/{c.End - c.BlockStart} "
            + $"queue={c.Queue.Count} top=[{queue}] edges={c.Edges.Count} faces={built?.Faces.Count ?? -1} "
            + $"instances={built?.Instances.Count ?? c.Instances.Count}\n";
        // The same block is decoded by several passes (chunk scan, anchored walk, targeted scan).
        lock (MeshDiagLock)
            if (MeshDiagSeen.Add(line)) File.AppendAllText(MeshDiagPath!, line);
    }

    /// <summary>Anchor-to-anchor structural parse of an unwalked region.</summary>
    private static void HarvestTail(Cur c)
    {
        var d = c.D;
        var anchors = new List<int>();
        for (int off = c.Pos; off + 16 <= c.BlockEnd; off++)
        {
            if (BitConverter.ToInt32(d, off + 4) != 0) continue;
            uint fl = BitConverter.ToUInt32(d, off + 12);
            if (fl is not (0x80004 or 0x88004 or 0x88204 or 0x1080004 or 0x1088004)) continue;
            int gs = BitConverter.ToInt32(d, off + 8);
            if (gs != -1 && (gs <= 0 || gs >= 0x1000000)) continue;
            int tag = BitConverter.ToInt32(d, off);
            if (tag < -1 || tag >= 0x100000) continue;
            anchors.Add(off);
        }
        if (anchors.Count < 4) return;

        int edges0 = c.Edges.Count, surfs0 = c.HarvestSurf.Count;
        // Overlapping scan windows can leak into the NEXT triplet when a GFilling is degenerate,
        // pairing the same surface with several face handles - keep only the first claim per
        // surface position.
        var claimed = new HashSet<(long, long, long, long)>();
        for (int i = 0; i < anchors.Count; i++)
        {
            int a = anchors[i];
            int next = i + 1 < anchors.Count ? anchors[i + 1] : c.BlockEnd;
            int span = next - a;
            uint fl = BitConverter.ToUInt32(d, a + 12);

            // Edge: [blob][faceA][faceB][nextx2][prevx2][nInterior][32xn][EdgePntx2 = 64][flags]
            if (span >= 109 && a + 44 <= c.BlockEnd)
            {
                uint fa = BitConverter.ToUInt32(d, a + 16), fb = BitConverter.ToUInt32(d, a + 20);
                int nInt = BitConverter.ToInt32(d, a + 40);
                if (fa > 0 && fa < 0x100000 && fb > 0 && fb < 0x100000 && nInt >= 0 && nInt < 512
                    && 109 + 32 * nInt <= span && span <= 109 + 32 * nInt + 4)
                {
                    int interiorPos = a + 44;
                    int fp = interiorPos + 32 * nInt;
                    var e = new EdgeRec { FaceA = fa, FaceB = fb, HasEndpoints = true };
                    bool fin = true;
                    for (int pointIndex = 0; pointIndex < nInt; pointIndex++)
                    {
                        int pointPos = interiorPos + 32 * pointIndex;
                        var point = new EdgeUV
                        {
                            UA = BitConverter.ToDouble(d, pointPos),
                            VA = BitConverter.ToDouble(d, pointPos + 8),
                            UB = BitConverter.ToDouble(d, pointPos + 16),
                            VB = BitConverter.ToDouble(d, pointPos + 24),
                        };
                        if (!IsFinite(point.UA) || !IsFinite(point.VA)
                            || !IsFinite(point.UB) || !IsFinite(point.VB))
                        { fin = false; break; }
                        e.Interior.Add(point);
                    }
                    var vals = new double[8];
                    for (int k = 0; k < 8; k++)
                    {
                        vals[k] = BitConverter.ToDouble(d, fp + 8 * k);
                        if (!IsFinite(vals[k])) { fin = false; break; }
                    }
                    if (fin)
                    {
                        e.Start = new EdgeUV { UA = vals[0], VA = vals[1], UB = vals[2], VB = vals[3] };
                        e.EndP = new EdgeUV { UA = vals[4], VA = vals[5], UB = vals[6], VB = vals[7] };
                        c.Edges.Add(e);
                        continue;
                    }
                }
            }

            // EdgeLoop: face handle at +20; its surface follows in the triplet
            // [EdgeLoop][GFilling][Surface].
            if (fl is 0x80004 or 0x88204 && span >= 60 && span <= 400)
            {
                uint faceH = BitConverter.ToUInt32(d, a + 20);
                if (faceH == 0 || faceH >= 0x100000 || c.HarvestSurf.ContainsKey(faceH)) continue;
                int scanEnd = Math.Min(a + 420, c.BlockEnd);
                // Rev before cyl: a Rev payload's frame would false-match the cyl signature.
                foreach (ushort scls in (ReadOnlySpan<ushort>)[522, 3881, 983, 763])
                {
                    var found = ScanSurfacesGuided(d, a + 65, scanEnd, new List<ushort> { scls }, c.Curves);
                    if (found.Count == 1 && found[0].Kind != SurfKind.Unknown)
                    {
                        var sfound = found[0];
                        var key = ((long)Math.Round(sfound.O[0] * 1e4), (long)Math.Round(sfound.O[1] * 1e4),
                                   (long)Math.Round(sfound.O[2] * 1e4), (long)Math.Round((sfound.Env?[0] ?? 0) * 1e6));
                        if (!claimed.Add(key)) break; // surface already paired
                        c.HarvestSurf[faceH] = sfound;
                        break;
                    }
                }
            }
        }
        System.Threading.Interlocked.Add(ref DbgHarvestEdges, c.Edges.Count - edges0);
        System.Threading.Interlocked.Add(ref DbgHarvestSurfs, c.HarvestSurf.Count - surfs0);
    }

    public static long DbgHarvestEdges, DbgHarvestSurfs;

    /// <summary>
    /// Decode the flattened topology payload declared by FacetedTopology0/8. FacetedTopology0 ends
    /// after the facet array; FacetedTopology8 alone appends the edge-visibility array.
    /// </summary>
    internal static bool TryDecodeFacetedTopology(byte[] data, int start, int end,
        bool hasEdgeVisibility, int ownerQueueIndex, out PolyMeshRec mesh,
        out bool valid, out int consumed)
    {
        mesh = new PolyMeshRec { OwnerQueueIndex = ownerQueueIndex };
        valid = false;
        consumed = 0;
        int p = start;
        if (start < 0 || end < start || end > data.Length || p + 20 > end) return false;
        p += 4; // normalsFlag
        p += 12; // commonNormal (3*f32)

        bool TryReadCount(out uint count)
        {
            count = 0;
            if (p + 4 > end) return false;
            count = BitConverter.ToUInt32(data, p);
            p += 4;
            return true;
        }

        if (!TryReadCount(out uint normalCount)) return false;
        if (normalCount != 0xFFFFFFFF)
        {
            if (normalCount > 10_000_000 || p + 12L * normalCount > end) return false;
            p += checked(12 * (int)normalCount);
        }

        if (!TryReadCount(out uint pointCount)
            || pointCount == 0xFFFFFFFF || pointCount > 10_000_000
            || p + 12L * pointCount > end)
            return false;
        mesh.Points = new List<double[]>((int)pointCount);
        valid = true;
        for (int pointIndex = 0; pointIndex < pointCount; pointIndex++, p += 12)
        {
            var point = new[]
            {
                (double)BitConverter.ToSingle(data, p),
                (double)BitConverter.ToSingle(data, p + 4),
                (double)BitConverter.ToSingle(data, p + 8),
            };
            if (point.Any(value => !IsFinite(value))) valid = false;
            mesh.Points.Add(point);
        }

        if (!TryReadCount(out uint facetCount)
            || facetCount == 0xFFFFFFFF || facetCount > 20_000_000
            || p + 6L * facetCount > end)
            return false;
        mesh.Triangles = new List<(int A, int B, int C)>((int)facetCount);
        for (int facetIndex = 0; facetIndex < facetCount; facetIndex++, p += 6)
        {
            int a = BitConverter.ToUInt16(data, p);
            int b = BitConverter.ToUInt16(data, p + 2);
            int c = BitConverter.ToUInt16(data, p + 4);
            if (a >= pointCount || b >= pointCount || c >= pointCount) valid = false;
            mesh.Triangles.Add((a, b, c));
        }

        if (hasEdgeVisibility)
        {
            if (!TryReadCount(out uint edgeFlagCount)) return false;
            if (edgeFlagCount == 0xFFFFFFFF) edgeFlagCount = 0;
            if (edgeFlagCount > 20_000_000 || p + (long)edgeFlagCount > end) return false;
            mesh.EdgeVisibility = new List<byte>((int)edgeFlagCount);
            for (int flagIndex = 0; flagIndex < edgeFlagCount; flagIndex++)
                mesh.EdgeVisibility.Add(data[p + flagIndex]);
            p += (int)edgeFlagCount;
        }

        consumed = p - start;
        return true;
    }

    private static void WalkQueueItem(Cur c, int qi)
    {
                var (cls, v) = c.Queue[qi];
                c.CurrentQueueIndex = qi;
                c.CurrentObjectHandle = v;
                c.CurrentObjectClass = cls;
                var qf = TfBlockDeserializer.SerializationFields(cls);
                if (qf == null) throw Stop;
                if (c.ProbeUndo is null or { TrackTagBase: true })
                {
                    // Blob-headed items: refresh the class's tag base (tag = base + ordinal).
                    if (qf.Count > 0 && qf[0].Name == "m_GInfo" && c.CanRead(16))
                    {
                        int tag0 = BitConverter.ToInt32(c.D, c.Pos);
                        if (tag0 >= 0 && tag0 < 0x100000)
                            c.TagBase[cls] = tag0 - c.ClassWalked.GetValueOrDefault(cls);
                    }
                    SetProbeValue(c.ClassWalked, cls, c.ClassWalked.GetValueOrDefault(cls) + 1, c.ProbeUndo?.ClassWalked);
                }
                if (MeshDiagPath != null && FacetedTopologyClasses.Contains(cls))
                    c.FtWalked[cls] = c.FtWalked.GetValueOrDefault(cls) + 1;
                if (cls == 1589) // Face -> walk schema, record handle + surface class
                {
                    int qBefore = c.Queue.Count;
                    foreach (var sf in qf)
                    {
                        if (sf.Name == "m_renderStyleId" && sf.Type == 14 && c.CanRead(4))
                            c.FaceRenderStyle[(uint)v] = BitConverter.ToInt32(c.D, c.Pos);
                        if (sf.Name == "m_faceFlags_v9" && sf.Type == 5 && c.CanRead(4))
                            c.FaceFlagsV9[(uint)v] = BitConverter.ToUInt32(c.D, c.Pos);
                        ReadField(c, sf, 1);
                    }
                    c.FaceCount++;
                    c.FaceOrder.Add((uint)v);
                    // The face's m_pSurf pointer queued its surface class during the walk.
                    ushort surf = 0;
                    for (int k = qBefore; k < c.Queue.Count; k++)
                        if (c.Queue[k].Cls is 522 or 983 or 763 or 3489 or 3881 or 2158 or 3576 or 2602 or 3954)
                        {
                            surf = c.Queue[k].Cls;
                            SetProbeValue(c.SurfaceOwnerByQueueIndex, k, (uint)v, c.ProbeUndo?.SurfaceOwnerByQueueIndex);
                            SetProbeValue(c.SurfaceOwnerByHandle, (uint)c.Queue[k].V, (uint)v, c.ProbeUndo?.SurfaceOwnerByHandle);
                            break;
                        }
                    c.SurfClasses.Add(surf);
                }
                else if (cls == 1243) // Edge -> capture endpoints via schema walk
                {
                    if (EdgeHasFixedLayout.Value && TryReadCompleteEdge(c, (uint)v, qi)) return;
                    c.CapEdge = new EdgeRec { Handle = (uint)v, QueueIndex = qi };
                    foreach (var sf in qf) ReadField(c, sf, 1);
                    if (c.CapEdge.HasEndpoints) c.Edges.Add(c.CapEdge);
                    c.CapEdge = null;
                    c.LastEdgeEnd = c.Pos; // surfaces are laid out after the edges
                }
                else if (cls == 522 || cls == 983 || cls == 763) // surfaces: read in place
                {
                    // Layout: [Envelope 4xf64 uv][orient u8] then Plane: [O,X,Y] (105 total) /
                    // CylSurf|ConeSurf: [O,X,Y,Z][R] (137).
                    var env = new double[4];
                    for (int k = 0; k < 4; k++) env[k] = BitConverter.ToDouble(c.D, c.Pos + 8 * k);
                    int fp = c.Pos + 33;
                    var sr = new SurfaceRec
                    {
                        Kind = cls == 522 ? SurfKind.Plane : cls == 983 ? SurfKind.Cylinder : SurfKind.Cone,
                        Env = env, Orient = c.D[c.Pos + 32],
                    };
                    int need = cls == 522 ? 105 : 137;
                    if (!c.CanRead(need)) throw Stop;
                    for (int k = 0; k < 3; k++)
                    {
                        sr.O[k] = BitConverter.ToDouble(c.D, fp + 8 * k);
                        sr.X[k] = BitConverter.ToDouble(c.D, fp + 24 + 8 * k);
                        sr.Y[k] = BitConverter.ToDouble(c.D, fp + 48 + 8 * k);
                        if (cls != 522) sr.Z[k] = BitConverter.ToDouble(c.D, fp + 72 + 8 * k);
                    }
                    if (cls != 522) sr.R = BitConverter.ToDouble(c.D, fp + 96);
                    bool sane = IsFinite(sr.O[0]) && IsFinite(sr.O[1]) && IsFinite(sr.O[2])
                        && IsFinite(sr.X[0]) && IsFinite(sr.Y[0]) && (cls == 522 || IsFinite(sr.R));
                    c.Take(need);
                    if (sane && c.RootClass == 1829 && cls == 522
                        && qi < c.QueueLinks.Count
                        && c.QueueLinks[qi].ParentQueueIndex < 0
                        && c.QueueLinks[qi].Field == "m_closurePlanes")
                    {
                        c.FamilyClosurePlaneOrigin = (double[])sr.O.Clone();
                        c.FamilyClosurePlaneNormal = Normalize(Cross(sr.X, sr.Y));
                    }
                    CaptureSurface(c, qi, (uint)v, sane ? sr : new SurfaceRec { Kind = SurfKind.Unknown });
                }
                else if (cls == 2158) // HermiteSurface
                {
                    // [Envelope 4*f64][orient u8][periodic 2*u8][constructedOK u8] [nodeCount
                    // u32][nodes: point,du,dv,duv = 12*f64] [uCount u32][uParams f64*] [vCount
                    // u32][vParams f64*].
                    if (!c.CanRead(40)) throw Stop;
                    int p = c.Pos;
                    var env = new double[4];
                    for (int k = 0; k < 4; k++) env[k] = BitConverter.ToDouble(c.D, p + 8 * k);
                    p += 36;
                    uint nodeCount = BitConverter.ToUInt32(c.D, p); p += 4;
                    if (nodeCount == 0xFFFFFFFF || nodeCount > 1_000_000
                        || p + 96L * nodeCount > c.End) throw Stop;
                    var surface = new SurfaceRec
                    {
                        Kind = SurfKind.Hermite,
                        Env = env, Orient = c.D[c.Pos + 32],
                        HermiteNodes = new List<HermiteSurfaceNode>((int)nodeCount),
                    };
                    bool valid = true;
                    for (int nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++, p += 96)
                    {
                        var node = new HermiteSurfaceNode();
                        for (int k = 0; k < 3; k++)
                        {
                            node.Point[k] = BitConverter.ToDouble(c.D, p + 8 * k);
                            node.Du[k] = BitConverter.ToDouble(c.D, p + 24 + 8 * k);
                            node.Dv[k] = BitConverter.ToDouble(c.D, p + 48 + 8 * k);
                            node.Duv[k] = BitConverter.ToDouble(c.D, p + 72 + 8 * k);
                        }
                        if (node.Point.Any(x => !IsFinite(x)) || node.Du.Any(x => !IsFinite(x))
                            || node.Dv.Any(x => !IsFinite(x)) || node.Duv.Any(x => !IsFinite(x)))
                            valid = false;
                        surface.HermiteNodes.Add(node);
                    }
                    double[] ReadParameters()
                    {
                        if (p + 4 > c.End) throw Stop;
                        uint count = BitConverter.ToUInt32(c.D, p); p += 4;
                        if (count == 0xFFFFFFFF) return [];
                        if (count > 1_000_000 || p + 8L * count > c.End) throw Stop;
                        var values = new double[(int)count];
                        for (int i = 0; i < values.Length; i++, p += 8)
                        {
                            values[i] = BitConverter.ToDouble(c.D, p);
                            if (!IsFinite(values[i])) valid = false;
                        }
                        return values;
                    }
                    surface.UParams = ReadParameters();
                    surface.VParams = ReadParameters();
                    c.Take(p - c.Pos);
                    if (valid && surface.UParams.Length >= 2 && surface.VParams.Length >= 2
                        && surface.HermiteNodes.Count == surface.UParams.Length * surface.VParams.Length)
                        CaptureSurface(c, qi, (uint)v, surface);
                    else
                        CaptureSurface(c, qi, (uint)v, new SurfaceRec { Kind = SurfKind.Unknown });
                }
                else if (cls == 3881) // SurfRev: frame + profile-curve ptr
                {
                    // [Envelope 4xf64][orient u8][center,X,Y,Z 12xf64][ptr profile 6] = 135b.
                    if (!c.CanRead(135)) throw Stop;
                    var env = new double[4];
                    for (int k = 0; k < 4; k++) env[k] = BitConverter.ToDouble(c.D, c.Pos + 8 * k);
                    int fp = c.Pos + 33;
                    var sr = new SurfaceRec { Kind = SurfKind.Rev, Env = env, Orient = c.D[c.Pos + 32] };
                    for (int k = 0; k < 3; k++)
                    {
                        sr.O[k] = BitConverter.ToDouble(c.D, fp + 8 * k);
                        sr.X[k] = BitConverter.ToDouble(c.D, fp + 24 + 8 * k);
                        sr.Y[k] = BitConverter.ToDouble(c.D, fp + 48 + 8 * k);
                        sr.Z[k] = BitConverter.ToDouble(c.D, fp + 72 + 8 * k);
                    }
                    uint pv = BitConverter.ToUInt32(c.D, fp + 96);
                    ushort pcls = BitConverter.ToUInt16(c.D, fp + 100);
                    sr.ProfileHandle = pv;
                    c.Take(135);
                    // Keep the walk in sync: the profile ptr enqueues its object.
                    if (pv != 0xFFFFFFFF && pcls != 0) Enqueue(c, pcls, (int)pv, "m_pProfile");
                    bool sane = IsFinite(sr.O[0]) && IsFinite(sr.X[0]) && IsFinite(sr.Z[0]);
                    CaptureSurface(c, qi, (uint)v, sane ? sr : new SurfaceRec { Kind = SurfKind.Unknown });
                }
                else if (cls == 1974) // GArc -> capture profile geometry
                {
                    // [GInfo 16][endParams 2xf64][xVec 3][yVec 3][radius][center 3][bFilled] =
                    // 113b.
                    if (!c.CanRead(113)) throw Stop;
                    var ar = new ArcRec
                    {
                        T0 = BitConverter.ToDouble(c.D, c.Pos + 16),
                        T1 = BitConverter.ToDouble(c.D, c.Pos + 24),
                        R = BitConverter.ToDouble(c.D, c.Pos + 80),
                    };
                    for (int k = 0; k < 3; k++)
                    {
                        ar.X[k] = BitConverter.ToDouble(c.D, c.Pos + 32 + 8 * k);
                        ar.Y[k] = BitConverter.ToDouble(c.D, c.Pos + 56 + 8 * k);
                        ar.C[k] = BitConverter.ToDouble(c.D, c.Pos + 88 + 8 * k);
                    }
                    c.Take(113);
                    if (IsFinite(ar.R) && IsFinite(ar.C[0]) && Math.Abs(ar.R) > 1e-9 && Math.Abs(ar.R) < 1e5)
                        c.Curves[(uint)v] = ar;
                }
                else if (cls == 2010) // GEllipse profile geometry
                {
                    // [GInfo 16][endParams 2*f64][center 3][xVec 3][yVec 3] [xLen f64][yLen f64] =
                    // 120 bytes.
                    if (!c.CanRead(120)) throw Stop;
                    var ellipse = new ArcRec
                    {
                        T0 = BitConverter.ToDouble(c.D, c.Pos + 16),
                        T1 = BitConverter.ToDouble(c.D, c.Pos + 24),
                        R = BitConverter.ToDouble(c.D, c.Pos + 104),
                        RY = BitConverter.ToDouble(c.D, c.Pos + 112),
                    };
                    for (int k = 0; k < 3; k++)
                    {
                        ellipse.C[k] = BitConverter.ToDouble(c.D, c.Pos + 32 + 8 * k);
                        ellipse.X[k] = BitConverter.ToDouble(c.D, c.Pos + 56 + 8 * k);
                        ellipse.Y[k] = BitConverter.ToDouble(c.D, c.Pos + 80 + 8 * k);
                    }
                    c.Take(120);
                    if (IsFinite(ellipse.R) && IsFinite(ellipse.RY)
                        && Math.Abs(ellipse.R) > 1e-9 && Math.Abs(ellipse.R) < 1e5
                        && Math.Abs(ellipse.RY) > 1e-9 && Math.Abs(ellipse.RY) < 1e5)
                        c.Curves[(uint)v] = ellipse;
                }
                else if (cls == 2028) // GNurbSpline
                {
                    // Treating those 32-byte records as four-byte handles leaves the cursor in the
                    // middle of the spline.
                    ReadField(c, qf[0], 1); // GNode.m_GInfo
                    if (!TryDecodeNurbSplinePayload(c.D, c.Pos, c.End,
                            out var curve, out int consumed))
                        throw Stop;
                    c.Take(consumed);
                    c.Curves[(uint)v] = curve;
                }
                else if (cls == 1736) // GLine -> capture as degenerate curve (R=0)
                {
                    // [GInfo 16][endParams 2xf64][origin 3][dirVec 3] = 80b.
                    if (!c.CanRead(80)) throw Stop;
                    var ln = new ArcRec
                    {
                        R = 0,
                        T0 = BitConverter.ToDouble(c.D, c.Pos + 16),
                        T1 = BitConverter.ToDouble(c.D, c.Pos + 24),
                    };
                    for (int k = 0; k < 3; k++)
                    {
                        ln.C[k] = BitConverter.ToDouble(c.D, c.Pos + 32 + 8 * k);
                        ln.X[k] = BitConverter.ToDouble(c.D, c.Pos + 56 + 8 * k);
                    }
                    c.Take(80);
                    if (IsFinite(ln.C[0]) && IsFinite(ln.X[0]))
                        c.Curves[(uint)v] = ln;
                }
                else if (cls == 2020) // GHermiteSpline profile curve
                {
                    // [GInfo 16][endParams 2*f64][periodic u8][count u32] followed by count * [t,
                    // point xyz, tangent xyz] (7*f64).
                    if (!c.CanRead(37)) throw Stop;
                    var curve = new ArcRec
                    {
                        T0 = BitConverter.ToDouble(c.D, c.Pos + 16),
                        T1 = BitConverter.ToDouble(c.D, c.Pos + 24),
                        HermiteNodes = new List<HermiteNode>(),
                    };
                    uint count = BitConverter.ToUInt32(c.D, c.Pos + 33);
                    if (count == 0xFFFFFFFF) { c.Take(37); return; }
                    if (count > 1_000_000 || !c.CanRead(37 + 56 * (int)count)) throw Stop;
                    int nodePos = c.Pos + 37;
                    bool valid = true;
                    for (int nodeIndex = 0; nodeIndex < count; nodeIndex++, nodePos += 56)
                    {
                        var node = new HermiteNode { T = BitConverter.ToDouble(c.D, nodePos) };
                        for (int k = 0; k < 3; k++)
                        {
                            node.Point[k] = BitConverter.ToDouble(c.D, nodePos + 8 + 8 * k);
                            node.Tangent[k] = BitConverter.ToDouble(c.D, nodePos + 32 + 8 * k);
                        }
                        if (!IsFinite(node.T) || node.Point.Any(p => !IsFinite(p))
                            || node.Tangent.Any(p => !IsFinite(p)))
                        { valid = false; break; }
                        curve.HermiteNodes.Add(node);
                    }
                    c.Take(37 + 56 * (int)count);
                    if (valid && curve.HermiteNodes.Count >= 2)
                    {
                        curve.HermiteNodes.Sort((a, b) => a.T.CompareTo(b.T));
                        curve.T0 = curve.HermiteNodes[0].T;
                        curve.T1 = curve.HermiteNodes[^1].T;
                        c.Curves[(uint)v] = curve;
                    }
                }
                else if (cls == 3489) // RuledSurf: two curves + apex points
                {
                    // [Envelope 32][orient 1][ptr curve1 6][ptr curve2 6][Point1 3xf64][Point2
                    // 3xf64] = 93b.
                    if (!c.CanRead(89)) throw Stop;
                    var env = new double[4];
                    for (int k = 0; k < 4; k++) env[k] = BitConverter.ToDouble(c.D, c.Pos + 8 * k);
                    int rp = c.Pos + 33;
                    var sr = new SurfaceRec { Kind = SurfKind.Ruled, Env = env, Orient = c.D[c.Pos + 32] };
                    (uint Value, ushort Class) ReadCurvePointer()
                    {
                        if (rp + 4 > c.End) throw Stop;
                        uint value = BitConverter.ToUInt32(c.D, rp); rp += 4;
                        if (value == 0) return (0, 0);
                        if (rp + 2 > c.End) throw Stop;
                        ushort curveClass = BitConverter.ToUInt16(c.D, rp); rp += 2;
                        return (value, curveClass);
                    }
                    var curve1 = ReadCurvePointer();
                    var curve2 = ReadCurvePointer();
                    uint v1 = curve1.Value, v2 = curve2.Value;
                    ushort c1 = curve1.Class, c2 = curve2.Class;
                    sr.ProfileHandle = v1; sr.ProfileHandle2 = v2;
                    if (rp + 48 > c.End) throw Stop;
                    sr.P1 = new double[3]; sr.P2 = new double[3];
                    for (int k = 0; k < 3; k++)
                    {
                        sr.P1[k] = BitConverter.ToDouble(c.D, rp + 8 * k);
                        sr.P2[k] = BitConverter.ToDouble(c.D, rp + 24 + 8 * k);
                    }
                    c.Take(rp + 48 - c.Pos);
                    if (v1 != 0xFFFFFFFF && c1 != 0) Enqueue(c, c1, (int)v1, "m_pCurve1");
                    if (v2 != 0xFFFFFFFF && c2 != 0) Enqueue(c, c2, (int)v2, "m_pCurve2");
                    CaptureSurface(c, qi, (uint)v, sr);
                }
                else if (cls is 1632 or 1663) // FacetedTopology0 / FacetedTopology8
                {
                    // Exact flattened base-class layout: FloatNormalsFacetedTopology: flags, common
                    // normal, normal array FloatFacetedTopology: point array FacetedTopology0:
                    // triangle array FacetedTopology8 only.
                    int ownerQueueIndex = qi < c.QueueLinks.Count
                        ? c.QueueLinks[qi].ParentQueueIndex : -1;
                    if (!TryDecodeFacetedTopology(c.D, c.Pos, c.End,
                        hasEdgeVisibility: cls == 1663, ownerQueueIndex,
                        out var mesh, out bool valid, out int consumed))
                        throw Stop;
                    c.Take(consumed);
                    if (valid && mesh.Points.Count > 0 && mesh.Triangles.Count > 0
                        && mesh.OwnerQueueIndex >= 0
                        && mesh.OwnerQueueIndex < c.Queue.Count
                        && c.Queue[mesh.OwnerQueueIndex].Cls == 2038)
                        c.PolyMeshes.Add(mesh);
                    else if (MeshDiagPath != null)
                        c.FtNotes.Add($"rejected:cls={cls},valid={valid},points={mesh.Points.Count},"
                            + $"facets={mesh.Triangles.Count},owner="
                            + (mesh.OwnerQueueIndex >= 0 && mesh.OwnerQueueIndex < c.Queue.Count
                                ? c.Queue[mesh.OwnerQueueIndex].Cls : -1));
                }
                else if (cls == 2238) // InstanceInfo -> symbol ref + placement
                {
                    // Layout: [m_Trf: 12xf64 = basisX/Y/Z + origin]
                    // [m_symbolId: i32] [m_cda: u32]. Preceded in-stream by the GInstance's own
                    // fields which the generic walk already consumed.
                    if (c.Pos + 104 > c.End) throw Stop;
                    var trf = new double[12];
                    bool ok = true;
                    for (int i = 0; i < 12; i++)
                    {
                        trf[i] = BitConverter.ToDouble(c.D, c.Pos + 8 * i);
                        if (!IsFinite(trf[i])) { ok = false; break; }
                    }
                    int symId = BitConverter.ToInt32(c.D, c.Pos + 96);
                    if (c.Debug)
                        DiagnosticSink.WriteLine($"  V2 InstanceInfo qi={qi} ok={ok} symbol={symId} owner="
                            + (qi < c.QueueLinks.Count && c.QueueLinks[qi].ParentQueueIndex >= 0
                                ? c.Queue[c.QueueLinks[qi].ParentQueueIndex].Cls : -1)
                            + $" trf=[{string.Join(',', trf.Select(value => value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)))}]");
                    c.Take(104);
                    // Element ids are < ~100M in practice; larger values = misparse.
                    if (ok && symId > 0 && symId < 100_000_000)
                    {
                        // The basis rows must form a plausible placement (rigid, or uniformly
                        // scaled).
                        bool plausibleBasis = IsPlausibleInstanceBasis(trf);
                        int ownerQueueIndex = qi < c.QueueLinks.Count
                            ? c.QueueLinks[qi].ParentQueueIndex : -1;
                        ushort ownerClass = ownerQueueIndex >= 0 && ownerQueueIndex < c.Queue.Count
                            ? c.Queue[ownerQueueIndex].Cls : (ushort)0;

                        // GComponentRef records component ownership, not drawable symbol geometry
                        // for the parent.
                        if (MeshDiagPath != null && !plausibleBasis)
                            c.FtNotes.Add($"instance-rejected:symbol={symId}");
                        bool hiddenVariant = ownerQueueIndex >= 0
                            && c.ObjectGInfo.TryGetValue(c.Queue[ownerQueueIndex].V, out var ownerInfo)
                            && IsHiddenInstanceVariant(ownerInfo.Flags);
                        if (MeshDiagPath != null && hiddenVariant)
                            c.FtNotes.Add($"instance-hidden:symbol={symId}");
                        if (plausibleBasis && (!hiddenVariant || c.IncludeNonVisible) && ShouldAttachSymbolGeometry(ownerClass))
                        {
                            if (c.SymbolId == 0)
                            {
                                c.SymbolId = symId;
                                c.InstTrf = trf;
                            }
                            if (!c.Instances.Any(x => x.SymbolElemId == symId
                                && x.Transform.SequenceEqual(trf)
                                && x.OwnerQueueIndex == ownerQueueIndex))
                                c.Instances.Add(new InstanceRec(
                                    symId, trf, ownerQueueIndex));
                        }
                    }
                }
                else
                {
                    foreach (var sf in qf) ReadField(c, sf, 1);
                }
    }

    private static readonly Lazy<bool> EdgeHasFixedLayout = new(() => TfBlockDeserializer.SerializationFields(EdgeClass) is
        [{ Declaring: 1246, Name: "m_GInfo", Type: 14, Mods: 0 },
         { Declaring: 1244, Name: "m_pFace", Type: 14, Mods: 19, Cnt: 2 },
         { Declaring: 1244, Name: "m_next", Type: 14, Mods: 19, Cnt: 2 },
         { Declaring: 1244, Name: "m_prev", Type: 14, Mods: 19, Cnt: 2 },
         { Declaring: 1244, Name: "m_interiorEdgePnts", Type: 14, Mods: 80 },
         { Declaring: 1244, Name: "m_firstAndLastEdgePnts", Type: 14, Mods: 16, Cnt: 2, Ref: 1248 },
         { Declaring: 1244, Name: "m_flags", Type: 2, Mods: 0 }]);

    // GNode.m_GInfo, the three handle pairs, the counted UV samples, two endpoints and flags.
    // Fall back before consuming anything on a short or invalid item: the recovery walk
    // uses the exact position and partial state at which the field reader stops.
    private static bool TryReadCompleteEdge(Cur c, uint handle, int queueIndex)
    {
        const int prefix = 44, sampleSize = 32, tail = 2 * sampleSize + 1;
        int start = c.Pos;
        if (c.End - start < prefix + tail) return false;
        var bytes = c.D.AsSpan(start, c.End - start);
        int category = BitConverter.ToInt32(bytes[8..]);
        if (category < -3_000_000) return false;
        uint count = BitConverter.ToUInt32(bytes[40..]);
        if (count == uint.MaxValue) count = 0;
        if (count > 100_000 || count > (bytes.Length - prefix - tail) / sampleSize) return false;
        int endPoints = prefix + (int)count * sampleSize;
        if (handle != 0)
            c.ObjectGInfo[(int)handle] = (BitConverter.ToInt32(bytes), BitConverter.ToInt32(bytes[4..]),
                category, BitConverter.ToUInt32(bytes[12..]));
        // Dry runs discard the entire edge; its fields do not enqueue any children.
        if (c.ProbeUndo == null)
        {
            var edge = new EdgeRec
            {
                Handle = handle, QueueIndex = queueIndex, HasEndpoints = true,
                FaceA = BitConverter.ToUInt32(bytes[16..]), FaceB = BitConverter.ToUInt32(bytes[20..]),
                NextA = BitConverter.ToUInt32(bytes[24..]), NextB = BitConverter.ToUInt32(bytes[28..]),
                PrevA = BitConverter.ToUInt32(bytes[32..]), PrevB = BitConverter.ToUInt32(bytes[36..]),
                Start = System.Runtime.InteropServices.MemoryMarshal.Read<EdgeUV>(bytes[endPoints..]),
                EndP = System.Runtime.InteropServices.MemoryMarshal.Read<EdgeUV>(bytes[(endPoints + sampleSize)..]),
                Flags = bytes[endPoints + 2 * sampleSize],
            };
            if (count > 0)
            {
                System.Runtime.InteropServices.CollectionsMarshal.SetCount(edge.Interior, (int)count);
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, EdgeUV>(bytes[prefix..endPoints])
                    .CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(edge.Interior));
            }
            c.Edges.Add(edge);
        }
        c.LastEdgeEnd = c.Pos = start + endPoints + tail;
        c.CapEdge = null;
        return true;
    }

    /// <summary>
    /// The serialized control points are homogeneous XYZW records and the full knot vector follows
    /// them inline.
    /// </summary>
    internal static bool TryDecodeNurbSplinePayload(
        byte[] data, int start, int end, out int consumed)
        => TryDecodeNurbSplinePayload(data, start, end, out _, out consumed);

    internal static bool TryDecodeNurbSplinePayload(
        byte[] data, int start, int end, out ArcRec curve, out int consumed)
    {
        curve = default;
        consumed = 0;
        if (start < 0 || end < start || end > data.Length || start + 30 > end)
            return false;
        int p = start;
        double parameter0 = BitConverter.ToDouble(data, p);
        double parameter1 = BitConverter.ToDouble(data, p + 8);
        int degree = BitConverter.ToInt32(data, p + 16);
        byte closed = data[p + 20], rational = data[p + 21];
        p += 22;
        if (!IsFinite(parameter0) || !IsFinite(parameter1)
            || degree <= 0 || degree > 64 || closed > 1 || rational > 1)
            return false;

        uint controlCount = BitConverter.ToUInt32(data, p);
        p += 4;
        if (controlCount == 0xFFFFFFFF || controlCount == 0 || controlCount > 1_000_000
            || p + 32L * controlCount + 4 > end)
            return false;
        var controls = new List<NurbControlPoint>((int)controlCount);
        for (uint controlIndex = 0; controlIndex < controlCount; controlIndex++, p += 32)
        {
            double weight = BitConverter.ToDouble(data, p);
            var point = new NurbControlPoint { Weight = weight };
            for (int component = 0; component < 3; component++)
                point.Point[component] = BitConverter.ToDouble(data, p + 8 * (component + 1));
            if (!IsFinite(weight) || point.Point.Any(value => !IsFinite(value))
                || (rational != 0 && weight == 0))
                return false;
            controls.Add(point);
        }

        uint knotCount = BitConverter.ToUInt32(data, p);
        p += 4;
        // A degree-p spline with n+1 control points has n+p+2 knots.
        if (knotCount != controlCount + (uint)degree + 1
            || p + 8L * knotCount > end)
            return false;
        var knots = new double[(int)knotCount];
        double previous = double.NegativeInfinity;
        for (uint knotIndex = 0; knotIndex < knotCount; knotIndex++, p += 8)
        {
            double knot = BitConverter.ToDouble(data, p);
            if (!IsFinite(knot) || knot < previous) return false;
            knots[knotIndex] = knot;
            previous = knot;
        }
        double domainStart = knots[degree];
        double domainEnd = knots[controls.Count];
        if (!(domainStart < domainEnd)
            || parameter0 < domainStart || parameter1 > domainEnd
            || parameter0 > parameter1)
            return false;
        curve = new ArcRec
        {
            T0 = parameter0,
            T1 = parameter1,
            NurbDegree = degree,
            NurbClosed = closed != 0,
            NurbRational = rational != 0,
            NurbControlPoints = controls,
            NurbKnots = knots,
        };
        consumed = p - start;
        return true;
    }

    internal static double[] EvaluateNurbSpline(double parameter, int degree,
        bool closed, bool rational, IReadOnlyList<NurbControlPoint> controls,
        IReadOnlyList<double> knots)
    {
        int lastControl = controls.Count - 1;
        if (degree < 1 || lastControl < degree
            || knots.Count != controls.Count + degree + 1)
            return [double.NaN, double.NaN, double.NaN];

        double domainStart = knots[degree];
        double domainEnd = knots[lastControl + 1];
        if (!IsFinite(parameter) || !IsFinite(domainStart) || !IsFinite(domainEnd)
            || domainEnd <= domainStart)
            return [double.NaN, double.NaN, double.NaN];
        if (closed)
        {
            double period = domainEnd - domainStart;
            if (parameter < domainStart || parameter > domainEnd)
            {
                parameter = domainStart + (parameter - domainStart) % period;
                if (parameter < domainStart) parameter += period;
            }
        }
        parameter = Math.Clamp(parameter, domainStart, domainEnd);

        int span;
        if (parameter >= domainEnd)
        {
            span = lastControl;
        }
        else
        {
            int low = degree, high = lastControl + 1;
            span = (low + high) / 2;
            while (parameter < knots[span] || parameter >= knots[span + 1])
            {
                if (parameter < knots[span]) high = span;
                else low = span;
                span = (low + high) / 2;
            }
        }

        var basis = new double[degree + 1];
        var left = new double[degree + 1];
        var right = new double[degree + 1];
        basis[0] = 1;
        for (int order = 1; order <= degree; order++)
        {
            left[order] = parameter - knots[span + 1 - order];
            right[order] = knots[span + order] - parameter;
            double saved = 0;
            for (int index = 0; index < order; index++)
            {
                double denominator = right[index + 1] + left[order - index];
                double term = denominator == 0 ? 0 : basis[index] / denominator;
                basis[index] = saved + right[index + 1] * term;
                saved = left[order - index] * term;
            }
            basis[order] = saved;
        }

        var result = new double[3];
        double weightSum = 0;
        for (int index = 0; index <= degree; index++)
        {
            var control = controls[span - degree + index];
            double weight = rational ? control.Weight : 1;
            double coefficient = basis[index] * weight;
            weightSum += coefficient;
            for (int component = 0; component < 3; component++)
                result[component] += coefficient * control.Point[component];
        }
        if (weightSum == 0 || !IsFinite(weightSum))
            return [double.NaN, double.NaN, double.NaN];
        for (int component = 0; component < 3; component++)
            result[component] /= weightSum;
        return result;
    }

    internal static bool ShouldAttachSymbolGeometry(ushort ownerClass)
        => ownerClass == 1976;

    /// <summary>
    /// A GInstance whose GInfo.flags carry all three visibility bits 0xE0 is a variant that is
    /// hidden for the type the symbol belongs to.
    /// </summary>
    internal static bool IsHiddenInstanceVariant(uint nodeFlags) => (nodeFlags & 0xE0u) == 0xE0u;

    /// <summary>
    /// Plausibility gate for the 3x3 part of a serialized GInstance placement (InstanceInfo.m_Trf),
    /// which also rejects a misparsed record.
    /// </summary>
    internal static bool IsPlausibleInstanceBasis(double[] transform)
    {
        if (transform.Length < 9 || transform.Take(9).Any(value => !double.IsFinite(value))) return false;
        double Dot(int first, int second) => transform[first] * transform[second]
            + transform[first + 1] * transform[second + 1] + transform[first + 2] * transform[second + 2];
        double x = Dot(0, 0), y = Dot(3, 3), z = Dot(6, 6);
        if (x > 0.5 && x < 2 && y > 0.5 && y < 2) return true;
        const double Relative = 1e-9;
        return x > 1e-8 && x < 1e8
            && Math.Abs(y - x) <= Relative * x && Math.Abs(z - x) <= Relative * x
            && Math.Abs(Dot(0, 3)) <= Relative * x && Math.Abs(Dot(0, 6)) <= Relative * x
            && Math.Abs(Dot(3, 6)) <= Relative * x;
    }

    /// <summary>
    /// A sole root GInstance is the source of a complete resolved body stored beside it in the same
    /// GRep. Expanding that source again duplicates the occurrence.
    /// </summary>
    internal static bool ShouldAttachMappedInstance(
        bool hasCompleteResolvedBody, int rootMappedInstanceCount,
        int parentQueueIndex)
        => !hasCompleteResolvedBody
            || rootMappedInstanceCount != 1
            || parentQueueIndex >= 0;

    private static void CaptureSurface(Cur c, int queueIndex, uint surfaceHandle, SurfaceRec surface)
    {
        c.WalkedSurfaces.Add(surface);
        if (surface.Kind == SurfKind.Unknown) return;
        if (c.SurfaceOwnerByQueueIndex.TryGetValue(queueIndex, out uint faceHandle)
            || c.SurfaceOwnerByHandle.TryGetValue(surfaceHandle, out faceHandle))
            SetProbeValue(c.DirectSurfaceByFace, faceHandle, surface, c.ProbeUndo?.DirectSurfaceByFace);
    }

    private sealed class ResyncUndo
    {
        public bool TrackTagBase;
        public readonly List<(ushort Key, int Previous, bool Existed)> ClassWalked = [];
        public readonly List<(uint Key, uint Previous, bool Existed)> SurfaceOwnerByHandle = [];
        public readonly List<(int Key, uint Previous, bool Existed)> SurfaceOwnerByQueueIndex = [];
        public readonly List<(uint Key, SurfaceRec Previous, bool Existed)> DirectSurfaceByFace = [];
        public readonly List<(uint Key, bool Previous, bool Existed)> FaceHasFirstLoop = [];
        public readonly List<int> LastLoops = [];

        public void Restore(Cur c)
        {
            RestoreProbeValues(c.ClassWalked, ClassWalked);
            RestoreProbeValues(c.SurfaceOwnerByHandle, SurfaceOwnerByHandle);
            RestoreProbeValues(c.SurfaceOwnerByQueueIndex, SurfaceOwnerByQueueIndex);
            RestoreProbeValues(c.DirectSurfaceByFace, DirectSurfaceByFace);
            RestoreProbeValues(c.FaceHasFirstLoop, FaceHasFirstLoop);
            for (int i = LastLoops.Count - 1; i >= 0; i--)
                c.LastLoops.Remove(LastLoops[i]);
            LastLoops.Clear();
        }
    }

    private static void SetProbeValue<TKey, TValue>(Dictionary<TKey, TValue> map, TKey key, TValue value,
        List<(TKey Key, TValue Previous, bool Existed)>? writes) where TKey : notnull
    {
        if (writes != null)
        {
            bool existed = map.TryGetValue(key, out var previous);
            writes.Add((key, previous!, existed));
        }
        map[key] = value;
    }

    private static void RestoreProbeValues<TKey, TValue>(Dictionary<TKey, TValue> map,
        List<(TKey Key, TValue Previous, bool Existed)> writes) where TKey : notnull
    {
        for (int i = writes.Count - 1; i >= 0; i--)
        {
            var (key, previous, existed) = writes[i];
            if (existed) map[key] = previous;
            else map.Remove(key);
        }
        writes.Clear();
    }

    /// <summary>
    /// Blob-anchored queue relock after a walk failure (format-variant boundary or a zero gap
    /// spliced in by desync recovery).
    /// </summary>
    private static bool TryResync(Cur c, ref int qi)
    {
        // Queue indices per class - the tag-jump target list (tag = TagBase[cls] + ordinal).
        var byClass = new Dictionary<ushort, List<int>>();
        for (int i = 0; i < c.Queue.Count; i++)
        {
            if (!byClass.TryGetValue(c.Queue[i].Cls, out var l)) byClass[c.Queue[i].Cls] = l = new();
            l.Add(i);
        }
        ResyncUndo? undo = null;

        bool DryRun(int off, int qj, bool preserveTagJumpMap = false)
        {
            var snap = (Pos: c.Pos, Q: c.Queue.Count, FO: c.FaceOrder.Count, SC: c.SurfClasses.Count,
                E: c.Edges.Count, PM: c.PolyMeshes.Count, WS: c.WalkedSurfaces.Count, FC: c.FaceCount,
                Sym: c.SymbolId, Trf: c.InstTrf, LE: c.LastEdgeEnd, Ins: c.Instances.Count);
            // Preserve the active tag-jump enumerator. The remaining probes only validate the payload.
            var savedTagBase = preserveTagJumpMap ? new Dictionary<ushort, int>(c.TagBase) : null;
            c.ProbeUndo = undo ??= new ResyncUndo();
            undo.TrackTagBase = preserveTagJumpMap;
            c.Pos = off;
            bool ok = true;
            int want = Math.Min(10, c.Queue.Count - qj);
            try { for (int k = 0; k < want; k++) WalkQueueItem(c, qj + k); }
            catch (DecodeStop) { ok = false; }
            finally { c.ProbeUndo = null; }
            // Roll the dry run back - the caller's loop re-walks for real.
            c.Pos = snap.Pos;
            // Only existing queue entries were visited. Their new children are the appended suffix.
            for (int i = c.QueueLinks.Count - 1; i >= snap.Q; i--)
            {
                var (parent, field) = c.QueueLinks[i];
                if (parent >= 0 && (c.Debug || field == "m_oConditions"))
                {
                    var children = c.QueueChildren[parent];
                    children.RemoveAt(children.Count - 1);
                }
            }
            c.Queue.RemoveRange(snap.Q, c.Queue.Count - snap.Q);
            if (c.QueueLinks.Count > snap.Q)
                c.QueueLinks.RemoveRange(snap.Q, c.QueueLinks.Count - snap.Q);
            c.FaceOrder.RemoveRange(snap.FO, c.FaceOrder.Count - snap.FO);
            c.SurfClasses.RemoveRange(snap.SC, c.SurfClasses.Count - snap.SC);
            c.Edges.RemoveRange(snap.E, c.Edges.Count - snap.E);
            c.PolyMeshes.RemoveRange(snap.PM, c.PolyMeshes.Count - snap.PM);
            c.WalkedSurfaces.RemoveRange(snap.WS, c.WalkedSurfaces.Count - snap.WS);
            c.FaceCount = snap.FC; c.SymbolId = snap.Sym; c.InstTrf = snap.Trf;
            c.Instances.RemoveRange(snap.Ins, c.Instances.Count - snap.Ins);
            c.LastEdgeEnd = snap.LE; c.CapEdge = null;
            if (savedTagBase != null) c.TagBase = savedTagBase;
            undo.Restore(c);
            return ok && want >= 6;
        }

        int anchorsTried = 0;
        int anchorCap = Math.Max(160, (c.BlockEnd - c.BlockStart) / 1024);
        for (int off = c.Pos + 1; off + 16 <= c.BlockEnd && anchorsTried < anchorCap; off++)
        {
            // Every accepted anchor flag ends in 04, at the first flag byte on disk.
            if (BitConverter.IsLittleEndian)
            {
                int skip = c.D.AsSpan(off + 12, c.BlockEnd - off - 15).IndexOf((byte)0x04);
                if (skip < 0) break;
                off += skip;
            }
            // GInfoBlob anchor: [tag][ctrl=0][gstyle][known flags].
            if (BitConverter.ToInt32(c.D, off + 4) != 0) continue;
            uint fl = BitConverter.ToUInt32(c.D, off + 12);
            if (fl is not (0x80004 or 0x88004 or 0x88204 or 0x1080004 or 0x1088004)) continue;
            int gs = BitConverter.ToInt32(c.D, off + 8);
            if (gs != -1 && (gs <= 0 || gs >= 0x1000000)) continue;
            int tag = BitConverter.ToInt32(c.D, off);
            if (tag < -1 || tag >= 0x100000) continue;
            anchorsTried++;

            // Tag-jump first: the anchor's tag maps to a queue ordinal per class.
            if (tag >= 0)
            {
                var tagJumpMap = c.TagBase;
                foreach (var (cls, baseT) in tagJumpMap)
                {
                    if (!byClass.TryGetValue(cls, out var lst)) continue;
                    int ord = tag - baseT;
                    // EXACT ordinal only - +/-slack is a wrong-relock source (mis-pairs
                    // FaceOrder with surfaces for the whole block).
                    if (ord >= 0 && ord < lst.Count)
                    {
                        int qj = lst[ord];
                        if (qj > qi && qj < c.Queue.Count
                            && DryRun(off, qj, ReferenceEquals(tagJumpMap, c.TagBase)))
                        { c.Pos = off; qi = qj - 1; return true; }
                    }
                }
            }

            // Fallback: window scan over the next pending items - near anchors only (distant
            // windows produced wrong relocks; distant recovery is tag-jump's job).
            if (anchorsTried <= 160)
            {
                int qHi = Math.Min(qi + 48, c.Queue.Count);
                for (int qj = qi; qj < qHi; qj++)
                    if (DryRun(off, qj)) { c.Pos = off; qi = qj - 1; return true; }
            }
        }
        return false;
    }


    private static void ReadField(Cur c, TfBlockDeserializer.TfField f, int depth)
    {
        if (depth > 48) throw Stop;

        if (f.Name == "m_GInfo")
        {
            // GInfo is a declared four-field value object, not opaque padding: tag, control
            // command, category ElementId, flags.
            int tag = c.U32();
            int control = c.U32();
            int categoryId = c.U32();
            uint flags = (uint)c.U32();
            if (categoryId < -3_000_000) throw Stop;
            if (c.CurrentObjectHandle != 0)
                c.ObjectGInfo[c.CurrentObjectHandle] = (tag, control, categoryId, flags);
            return;
        }

        // GRep.m_bBox / m_tightbBox are two inline XYZ points. Capture them only on the root
        // object; nested GRep objects have their own coordinate space and must not replace the
        // owning element's extent.
        if (depth == 0 && f.Declaring == 2008
            && f.Name is "m_bBox" or "m_tightbBox"
            && f.Type == 0x0D && (f.Mods >> 4) == 1 && f.Cnt == 2)
        {
            var box = new double[6];
            for (int i = 0; i < 6; i++) box[i] = c.F64();
            if (ValidBox(box))
            {
                if (f.Name == "m_tightbBox") c.RootTightBoundingBox = box;
                else c.RootBoundingBox = box;
            }
            return;
        }

        // Surface(523).m_Envelope has NO ref in the schema but is serialized INLINE as Envelope = 2
        // uv corners x 16 bytes = 32.
        if (f.Name == "m_Envelope" && f.Declaring == 523) { c.Take(32); return; }

        // FamilyDB serializes these POD entries as int[2], as in the exact Tf reader.
        if (c.RootClass == 1829 && f is { Declaring: 1829, Name: "m_dummyHostInsertionFaceTags",
                Type: 13, Mods: 80, Ref: null, Cnt: null })
        {
            uint count = (uint)c.U32();
            if (count == uint.MaxValue) return;
            if (count > 1_000_000 || count > (c.End - c.Pos) / 8) throw Stop;
            c.Take((int)count * 8);
            return;
        }
        if (c.RootClass == 1829 && f is { Declaring: 1763, Name: "second", Type: 14, Mods: 0, Ref: 1763 })
        {
            if (TfBlockDeserializer.Schemas[1763].Name != "std::pair< int, FamSubInstDescriptor >"
                || !TfBlockDeserializer.Schemas.TryGetValue(1724, out var descriptor)
                || descriptor.Name != "FamSubInstDescriptor") throw Stop;
            f = f with { Ref = 1724 };
        }

        // FamilySymbol's embedded complex-cut BRep reaches the geometry walker through
        // FamSymHandleMgr.
        if (f.Declaring == 140 && f.Name == "m_foreignElemIdRef") { c.Take(4); return; }
        if (f.Declaring == 1729 && f.Name == "m_handleMap")
        {
            uint count = (uint)c.U32();
            if (count == 0xFFFFFFFF) return;
            if (count > 100_000) throw Stop;
            var fields = TfBlockDeserializer.SerializationFields(1730) ?? throw Stop;
            for (uint index = 0; index < count; index++)
                foreach (var field in fields) ReadField(c, field, depth + 1);
            return;
        }
        if (f.Declaring == 1730 && f.Name == "second")
        {
            var fields = TfBlockDeserializer.SerializationFields(1727) ?? throw Stop;
            foreach (var field in fields) ReadField(c, field, depth + 1);
            return;
        }
        if (f.Declaring == 1758 && f.Name == "m_flags") { c.Take(3); return; }
        if (f.Declaring == 322 && f.Name == "second") { c.Take(4); return; }
        if (f.Declaring == 714 && f.Name == "m_params")
        {
            ReadInlineContainer(c, 715, depth);
            return;
        }
        if (f.Declaring == 714 && f.Name == "m_geomRefHandles")
        {
            ReadInlineStruct(c, 716, depth);
            return;
        }
        if (f.Declaring == 716 && f.Name == "m_offsetGeomMap")
        {
            ReadInlineContainer(c, 717, depth);
            return;
        }
        if (f.Declaring == 496 && f.Name == "m_PatternPositionMap")
        {
            ReadInlineContainer(c, 497, depth);
            return;
        }
        if (f.Declaring == 496 && f.Name == "m_substituteFaceMap")
        {
            ReadInlineContainer(c, 498, depth);
            return;
        }
        if (f.Declaring == 497 && f.Name == "second")
        {
            ReadInlineStruct(c, 78, depth);
            return;
        }
        if (f.Declaring == 498 && f.Name == "second")
        {
            ReadInlineStruct(c, 91, depth);
            return;
        }

        // GHermiteSpline stores its nodes inline. One node is a 3D point, a 3D tangent and a curve
        // parameter (7 doubles = 56 bytes); the Formats schema exposes the dynamic container but
        // does not carry an item-class reference.
        if (f.Declaring == 2020 && f.Name == "m_NodeArray")
        {
            uint n = (uint)c.U32();
            if (n == 0xFFFFFFFF) return;
            if (n > 1_000_000) throw Stop;
            c.Take(56 * (int)n);
            return;
        }

        // GeomGenerator history entries have class-declared fixed widths: FaceHistEntry=16,
        // CurveHistEntry=24, EdgeHistEntry=20 bytes.
        if (f.Declaring == 97 && f.Name is "m_faceHistTable" or "m_curveHistTableSet"
            or "m_edgeHistTable" or "m_edgeHistTableReverse")
        {
            uint n = (uint)c.U32();
            if (n == 0xFFFFFFFF) return;
            if (n > 100000) throw Stop;
            int recordSize = f.Name switch
            {
                "m_faceHistTable" => 16,
                "m_curveHistTableSet" => 24,
                _ => 20,
            };
            c.Take(recordSize * (int)n);
            return;
        }
        // Exact spatial fallback also needs the sentinel's own face/first/last/open contract, not
        // only the back-references found on adjacent Edge objects.
        if (c.CurrentObjectClass is 1254 or 1257 && f.Declaring == 1255
            && f.Name is "m_pFace" or "m_next" or "m_prev" or "m_open")
        {
            if (!c.SpaceEdgeLoops.TryGetValue(c.CurrentQueueIndex, out var loop))
                c.SpaceEdgeLoops[c.CurrentQueueIndex] = loop = new();
            switch (f.Name)
            {
                case "m_pFace": loop.Face = (uint)c.U32(); break;
                case "m_next": loop.Next = (uint)c.U32(); break;
                case "m_prev": loop.Prev = (uint)c.U32(); break;
                case "m_open":
                    if (!c.CanRead(1)) throw Stop;
                    loop.Open = c.D[c.Pos++]; break;
            }
            return;
        }
        // Edge back-references to its two adjacent faces (2xu32 fixed array).
        if (f.Name == "m_pFace" && c.CapEdge != null)
        {
            uint a = (uint)c.U32(), b = (uint)c.U32();
            c.CapEdge.FaceA = a; c.CapEdge.FaceB = b;
            return;
        }
        if (f.Name == "m_next" && c.CapEdge != null)
        {
            c.CapEdge.NextA = (uint)c.U32();
            c.CapEdge.NextB = (uint)c.U32();
            return;
        }
        if (f.Name == "m_prev" && c.CapEdge != null)
        {
            c.CapEdge.PrevA = (uint)c.U32();
            c.CapEdge.PrevB = (uint)c.U32();
            return;
        }
        if (f.Name == "m_interiorEdgePnts")
        {
            uint n = (uint)c.U32();
            if (n == 0xFFFFFFFF) return;
            if (n > 100000) throw Stop;
            // A probe rolls its edge back. UV samples have fixed width and no value-dependent validation.
            if (c.ProbeUndo != null)
            {
                if (32L * n > c.End - c.Pos) throw Stop;
                c.Pos += (int)n * 32;
                return;
            }
            // Each EdgePnt contains the UV coordinates in both adjacent faces. Every sample is part
            // of the serialized trim polyline; dropping them collapses a closed circular edge to a
            // zero-length chord.
            for (uint i = 0; i < n; i++)
            {
                var point = ReadUV(c);
                c.CapEdge?.Interior.Add(point);
            }
            return;
        }
        if (f.Name == "m_firstAndLastEdgePnts")
        {
            var s = ReadUV(c);
            var e = ReadUV(c);
            if (c.CapEdge != null) { c.CapEdge.Start = s; c.CapEdge.EndP = e; c.CapEdge.HasEndpoints = true; }
            return;
        }
        if (f.Name == "m_flags" && c.CapEdge != null)
        {
            if (!c.CanRead(1)) throw Stop;
            c.CapEdge.Flags = c.D[c.Pos++];
            return;
        }

        int hi = f.Mods >> 4;
        if (hi == 6) { ReadString(c); return; }
        if (hi == 1)
        {
            int n = f.Cnt ?? 0;
            for (int i = 0; i < n; i++) ReadScalar(c, f, depth);
            return;
        }
        if (hi == 5)
        {
            uint n = (uint)c.U32();
            if (n == 0xFFFFFFFF) return;
            if (n > 1_000_000) throw Stop;
            for (uint i = 0; i < n; i++) ReadScalar(c, f, depth);
            return;
        }
        ReadScalar(c, f, depth);
    }

    private static void ReadInlineContainer(Cur c, int classId, int depth)
    {
        uint count = (uint)c.U32();
        if (count == 0xFFFFFFFF) return;
        if (count > 100_000) throw Stop;
        for (uint index = 0; index < count; index++)
            ReadInlineStruct(c, classId, depth);
    }

    private static void ReadInlineStruct(Cur c, int classId, int depth)
    {
        var fields = TfBlockDeserializer.SerializationFields(classId) ?? throw Stop;
        foreach (var field in fields) ReadField(c, field, depth + 1);
    }

    private static void ReadScalar(Cur c, TfBlockDeserializer.TfField f, int depth)
    {
        switch (f.Type)
        {
            case 0x01: case 0x02: case 0x03:
            {
                if (!c.CanRead(1)) throw Stop;
                byte value = c.D[c.Pos++];
                bool captureConditionCut = c.CurrentObjectClass is 1995 or 1996
                    && f.Name == "m_negateDirCondition";
                if (c.CurrentQueueIndex >= 0
                    && ((c.Debug && c.CurrentObjectClass is >= 1992 and <= 2003)
                        || captureConditionCut))
                {
                    if (!c.QueueBytes.TryGetValue(c.CurrentQueueIndex, out var values))
                        c.QueueBytes[c.CurrentQueueIndex] = values = new();
                    if (!values.TryGetValue(f.Name, out var fieldValues))
                        values[f.Name] = fieldValues = new();
                    fieldValues.Add(value);
                }
                return;
            }
            case 0x04:
            {
                int value = c.U32();
                bool captureModelCondition = c.CurrentObjectClass is 1999 or 2002;
                if (c.CurrentQueueIndex >= 0
                    && ((c.Debug && c.CurrentObjectClass is >= 1992 and <= 2015)
                        || captureModelCondition))
                {
                    if (!c.QueueInts.TryGetValue(c.CurrentQueueIndex, out var values))
                        c.QueueInts[c.CurrentQueueIndex] = values = new();
                    values[f.Name] = value;
                }
                return;
            }
            case 0x05: case 0x06: c.Take(4); return;
            case 0x07:
            {
                double value = c.F64();
                if (c.Debug && c.CurrentQueueIndex >= 0 && c.CurrentObjectClass is >= 1992 and <= 2003)
                {
                    if (!c.QueueDoubles.TryGetValue(c.CurrentQueueIndex, out var values))
                        c.QueueDoubles[c.CurrentQueueIndex] = values = new();
                    if (!values.TryGetValue(f.Name, out var fieldValues))
                        values[f.Name] = fieldValues = new();
                    fieldValues.Add(value);
                }
                return;
            }
            case 0x0A: c.Take(8); return;
            case 0x08: ReadString(c); return;
            case 0x09: c.Take(16); return;
            // POD array item: ref 524(Envelope)/1248(EdgePnt) = 2xf64 UV (16b); else 3xf64 point
            // (24b). field already loops cnt times for fixed arrays (hi==1).
            case 0x0D: c.Take((f.Mods >> 4) == 1 ? (f.Ref is 524 or 1248 ? 16 : 24) : 8 * (f.Cnt ?? 3)); return;
            case 0x0E:
            {
                int lo = f.Mods & 0x0F;
                if (lo == 3) { c.Take(4); return; }
                if (lo is 1 or 2) { ReadPtr(c, f.Name); return; }
                if (f.Ref == 20)
                {
                    // GPolyMesh.m_materialID: the material of the mesh (the mesh has no faces to
                    // carry a render style).
                    if (c.CurrentObjectClass == 2038 && f.Name == "m_materialID" && c.CurrentQueueIndex >= 0 && c.CanRead(4))
                    {
                        if (!c.QueueInts.TryGetValue(c.CurrentQueueIndex, out var meshValues))
                            c.QueueInts[c.CurrentQueueIndex] = meshValues = new();
                        meshValues[f.Name] = BitConverter.ToInt32(c.D, c.Pos);
                    }
                    c.Take(4);
                    return;
                }
                if (f.Ref.HasValue)
                {
                    var sub = TfBlockDeserializer.SerializationFields(f.Ref.Value) ?? throw Stop;
                    foreach (var sf in sub) ReadField(c, sf, depth + 1);
                    return;
                }
                ReadPoly(c, f.Name);
                return;
            }
            default: throw Stop;
        }
    }

    private static void ReadString(Cur c)
    {
        uint n = (uint)c.U32();
        if (n > 100000) throw Stop;
        c.Take(2 * (int)n);
    }

    private static void ReadPtr(Cur c, string? field = null)
    {
        int v = c.U32();
        if (field == "m_pFirstLoop" && c.CurrentObjectClass == 1589)
            SetProbeValue(c.FaceHasFirstLoop, (uint)c.CurrentObjectHandle, v != 0, c.ProbeUndo?.FaceHasFirstLoop);
        else if (field == "m_nextLoop" && v == 0 && c.CurrentObjectClass is 1254 or 1257 && c.CurrentQueueIndex >= 0)
        {
            if (c.LastLoops.Add(c.CurrentQueueIndex))
                c.ProbeUndo?.LastLoops.Add(c.CurrentQueueIndex);
        }
        if (v == 0) return;
        if (!c.CanRead(2)) throw Stop;
        ushort cls = BitConverter.ToUInt16(c.D, c.Pos); c.Pos += 2;
        if (cls == 4) { ReadGInfoTail(c); return; }
        if (!TfBlockDeserializer.Schemas.ContainsKey(cls)) throw Stop;
        Enqueue(c, cls, v, field ?? "<pointer>");
    }

    private static void ReadPoly(Cur c, string field)
    {
        uint v = (uint)c.U32();
        if (v == 0 || v != 0xFFFFFFFF) return; // null or plain handle
        if (!c.CanRead(2)) throw Stop;
        ushort cls = BitConverter.ToUInt16(c.D, c.Pos); c.Pos += 2;
        if (cls == 4) { ReadGInfoTail(c); return; }
        if (!TfBlockDeserializer.Schemas.ContainsKey(cls)) throw Stop;
        Enqueue(c, cls, (int)v, field);
    }

    private static void Enqueue(Cur c, ushort cls, int handle, string field)
    {
        int childIndex = c.Queue.Count;
        c.Queue.Add((cls, handle));
        c.QueueLinks.Add((c.CurrentQueueIndex, field));
        if (c.CurrentQueueIndex >= 0 && (c.Debug || field == "m_oConditions"))
        {
            if (!c.QueueChildren.TryGetValue(c.CurrentQueueIndex, out var children))
                c.QueueChildren[c.CurrentQueueIndex] = children = new();
            children.Add(childIndex);
        }
    }

    private static void ReadGInfoTail(Cur c)
    {
        if (!c.CanRead(2)) throw Stop;
        ushort tk = BitConverter.ToUInt16(c.D, c.Pos); c.Pos += 2;
        if ((tk & 0xFF) != 8) throw Stop;
        int kind = tk >> 8;
        if (kind == 0) return;
        if (kind == 1) { c.Take(6 * 8 + 2 * 4); return; }
        throw Stop;
    }

    private static EdgeUV ReadUV(Cur c)
    {
        // EdgePnt = [(uA,vA),(uB,vB)] = 4xf64 = the point's UV in both adjacent faces.
        double ua = c.F64(), va = c.F64(), ub = c.F64(), vb = c.F64();
        return new EdgeUV { UA = ua, VA = va, UB = ub, VB = vb };
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < 1e7;

    private static double[] Cross(double[] first, double[] second) =>
    [
        first[1] * second[2] - first[2] * second[1],
        first[2] * second[0] - first[0] * second[2],
        first[0] * second[1] - first[1] * second[0],
    ];

    private static double[]? Normalize(double[] vector)
    {
        double length = Math.Sqrt(vector.Sum(value => value * value));
        return length > 0 && double.IsFinite(length)
            ? vector.Select(value => value / length).ToArray()
            : null;
    }

    private static bool ValidBox(double[]? box) => box is { Length: 6 }
        && box.All(IsFinite)
        && box[0] <= box[3] && box[1] <= box[4] && box[2] <= box[5];


    private static GRepData? BuildGeometry(Cur c, bool requireCompleteWalk = false, bool measureSourceNodes = false, bool measureLighting = false,
        bool measureFills = false, BodyMeasure bodyMeasure = BodyMeasure.None)
    {
        t_coarseTessellation = false;
        int enclosingUntriangulated = t_untriangulatedFaces;
        t_untriangulatedFaces = 0;
        try
        {
            var geometry = BuildGeometryCore(c, requireCompleteWalk, measureSourceNodes, measureLighting, measureFills, bodyMeasure);
            if (geometry != null) geometry.UntriangulatedFaces = t_untriangulatedFaces;
            return geometry;
        }
        finally
        {
            t_coarseTessellation = false;
            t_untriangulatedFaces = enclosingUntriangulated;
        }
    }

    private static GRepData? BuildGeometryCore(Cur c, bool requireCompleteWalk, bool measureSourceNodes, bool measureLighting, bool measureFills,
        BodyMeasure bodyMeasure)
    {
        if (c.Edges.Count == 0 || (c.FaceOrder.Count == 0 && c.HarvestSurf.Count == 0))
            return PolyMeshGeometry(c) ?? (c.Instances.Count > 0 ? InstanceGeometry(c) : null);

        // Scan the block for Plane(522) signatures - 9 consecutive f64 [O, X, Y] where X,Y are
        // orthonormal - in stream order.
        List<SurfaceRec> surfaces;
        if (c.WalkedSurfaces.Count >= c.FaceOrder.Count && c.FaceOrder.Count > 0)
            surfaces = c.WalkedSurfaces;
        else
        {
            // HYBRID pairing: surfaces captured during the walk are byte-exact for the first N
            // faces; only the remainder (past the walk's stop position) is scanned.
            surfaces = new List<SurfaceRec>(c.WalkedSurfaces);
            if (c.SurfClasses.Count > surfaces.Count)
            {
                var rest = c.SurfClasses.GetRange(surfaces.Count, c.SurfClasses.Count - surfaces.Count);
                int scanStart = Math.Max(c.BlockStart, Math.Min(c.Pos, c.BlockEnd));
                surfaces.AddRange(ScanSurfacesGuided(c.D, scanStart, c.BlockEnd, rest, c.Curves));
            }
        }
        if (surfaces.Count == 0 && c.DirectSurfaceByFace.Count == 0 && c.HarvestSurf.Count == 0)
            return PolyMeshGeometry(c) ?? (c.Instances.Count > 0 ? InstanceGeometry(c) : null);

        int nMap = Math.Min(c.FaceOrder.Count, surfaces.Count);
        var handleToSurf = new Dictionary<uint, SurfaceRec>(c.DirectSurfaceByFace);
        for (int i = 0; i < nMap; i++)
            if (!handleToSurf.ContainsKey(c.FaceOrder[i]) && surfaces[i].Kind != SurfKind.Unknown)
                handleToSurf[c.FaceOrder[i]] = surfaces[i];
        // Grammar-first harvested pairs (unwalked tails) - walked pairs take precedence.
        foreach (var kv in c.HarvestSurf)
            if (!handleToSurf.ContainsKey(kv.Key))
                handleToSurf[kv.Key] = kv.Value;

        if (c.Debug)
        {
            var edgeHandles = c.Edges.SelectMany(e => new[] { e.FaceA, e.FaceB })
                .Where(h => h != 0 && h != 0xFFFFFFFF).Distinct().OrderBy(h => h).ToArray();
            var missing = edgeHandles.Where(h => !handleToSurf.ContainsKey(h)).ToArray();
            DiagnosticSink.WriteLine($"  V2 eid={c.ElementId} surface-map order={c.FaceOrder.Count}/{surfaces.Count} direct={c.DirectSurfaceByFace.Count} total={handleToSurf.Count} edgeHandles={edgeHandles.Length} missing=[{string.Join(',', missing)}]");
            foreach (ushort curveClass in handleToSurf.Values
                .Where(s => s.Kind is SurfKind.Rev or SurfKind.Ruled)
                .Select(s => c.Queue.FirstOrDefault(x => x.V == (int)s.ProfileHandle).Cls)
                .Where(cls => cls != 0 && cls is not (1736 or 1974 or 2020)).Distinct())
            {
                var schema = TfBlockDeserializer.SerializationFields(curveClass);
                if (schema != null)
                    DiagnosticSink.WriteLine($"    curve schema {curveClass}: {string.Join(" | ", schema.Select(f => $"{f.Declaring}.{f.Name}:t{f.Type:X2}/m{f.Mods:X2}/r{f.Ref}/n{f.Cnt}"))}");
            }
            foreach (uint h in edgeHandles.Where(handleToSurf.ContainsKey))
            {
                int ec = c.Edges.Count(e => e.FaceA == h || e.FaceB == h);
                var mappedSurface = handleToSurf[h];
                ushort profileClass = c.Queue.FirstOrDefault(x => x.V == (int)mappedSurface.ProfileHandle).Cls;
                string profile = mappedSurface.Kind is SurfKind.Rev or SurfKind.Ruled
                    ? $", profile={mappedSurface.ProfileHandle}/{profileClass}, curve={c.Curves.ContainsKey(mappedSurface.ProfileHandle)}"
                    : "";
                DiagnosticSink.WriteLine($"    face {h}: {mappedSurface.Kind}, edges={ec}, direct={c.DirectSurfaceByFace.ContainsKey(h)}, harvested={c.HarvestSurf.ContainsKey(h)}{profile}");
            }
        }

        foreach (var rawEdge in c.Edges) { rawEdge.RawStart = rawEdge.Start; rawEdge.RawEndP = rawEdge.EndP; rawEdge.RawInterior = rawEdge.Interior; }
        // Per face handle: collect its edge UV segments (in that face's own surface frame).
        var byFace = new Dictionary<uint, List<(double U1, double V1, double U2, double V2)>>();
        var cylinderLoopEdges = new Dictionary<uint, HashSet<uint>>();
        var sourceEdgesByHandle = EdgesByHandle(c);
        var completeLoops = CompleteFaceLoops(c);
        if (c.NativeWallBody)
            foreach (var (face, indices) in completeLoops)
                if (c.DirectSurfaceByFace.TryGetValue(face, out var sourceSurface) && sourceSurface.Kind == SurfKind.Cylinder
                    && TryGetFaceLoopEdges(c, face, indices, sourceEdgesByHandle) is { } sourceLoopEdges)
                    cylinderLoopEdges[face] = sourceLoopEdges.Select(edge => edge.Handle).ToHashSet();
        var sharedCylinderEdges = cylinderLoopEdges.Values.SelectMany(handles => handles).ToHashSet();
        var meshEdgeBounds = c.DirectSurfaceByFace.Values.Any(surface => surface.Kind == SurfKind.Cylinder)
            ? ReadFaceEdgeBounds(c, completeLoops, sourceEdgesByHandle) : new Dictionary<uint, double[]>();
        var meshArcsByFace = new Dictionary<uint, List<Arc>>();
        var arcEdges = new HashSet<uint>();
        void SampleCircularEdge(EdgeRec edge, SurfaceRec firstSurface, SurfaceRec secondSurface, bool resample)
        {
            if (ResampleCircularEdge(edge, firstSurface, secondSurface, resample) is not { } arc || !arcEdges.Add(edge.Handle)) return;
            uint planeFace = firstSurface.Kind == SurfKind.Plane ? edge.FaceA : edge.FaceB;
            if (!meshArcsByFace.TryGetValue(planeFace, out var arcs)) meshArcsByFace[planeFace] = arcs = [];
            arcs.Add(arc);
        }
        foreach (var edge in c.Edges)
            if (sharedCylinderEdges.Contains(edge.Handle)
                && c.DirectSurfaceByFace.TryGetValue(edge.FaceA, out var firstSurface)
                && c.DirectSurfaceByFace.TryGetValue(edge.FaceB, out var secondSurface))
                SampleCircularEdge(edge, firstSurface, secondSurface, true);
        // Every other circular edge between a vertical-axis cylinder and a plane square to it is
        // sampled the same way (the renderer samples each edge of a Brep with the arc sampler); the
        // file stores its own, finer points.
        foreach (var edge in c.Edges)
            if (!arcEdges.Contains(edge.Handle)
                && handleToSurf.TryGetValue(edge.FaceA, out var anySurfaceA) && handleToSurf.TryGetValue(edge.FaceB, out var anySurfaceB))
                SampleCircularEdge(edge, anySurfaceA, anySurfaceB, !c.NativeWallBody);
        foreach (var edge in c.Edges)
            if (sharedCylinderEdges.Contains(edge.Handle) && (edge.Flags & 6) == 6
                && TryGetEdgePoints(c, edge) is { } shared && shared.Count == edge.Interior.Count + 2)
                edge.SharedBoundaryPoints = shared;
        var authoredWorldEdges = new Dictionary<uint, List<(double[] First, double[] Second)>>();
        var incompleteWorldEdges = new HashSet<uint>();
        void Add(uint h, double u1, double v1, double u2, double v2, EdgeRec edge, int pointIndex, bool captureWorld)
        {
            if (!handleToSurf.ContainsKey(h)) return;
            if (!byFace.TryGetValue(h, out var l)) byFace[h] = l = new();
            l.Add((u1, v1, u2, v2));
            if (captureWorld && cylinderLoopEdges.ContainsKey(h))
            {
                if (edge.SharedBoundaryPoints is not { } shared) incompleteWorldEdges.Add(h);
                else
                {
                    if (!authoredWorldEdges.TryGetValue(h, out var world)) authoredWorldEdges[h] = world = new();
                    world.Add((shared[pointIndex], shared[pointIndex + 1]));
                }
            }
        }
        void CollectFaceSegments(bool captureWorld = false)
        {
            foreach (var e in c.Edges)
            {
                bool firstFace = !cylinderLoopEdges.TryGetValue(e.FaceA, out var firstLoopEdges) || firstLoopEdges.Contains(e.Handle);
                bool secondFace = !cylinderLoopEdges.TryGetValue(e.FaceB, out var secondLoopEdges) || secondLoopEdges.Contains(e.Handle);
                var a = e.Start;
                for (int i = 0; i <= e.Interior.Count; i++)
                {
                    var b = i < e.Interior.Count ? e.Interior[i] : e.EndP;
                    if (firstFace)
                        Add(e.FaceA, a.UA, a.VA, b.UA, b.VA, e, i, captureWorld);
                    if (secondFace)
                        Add(e.FaceB, a.UB, a.VB, b.UB, b.VB, e, i, captureWorld);
                    a = b;
                }
            }
        }
        CollectFaceSegments(captureWorld: true);
        var authoredSamplingEdges = byFace;

        // Analytic face tessellation uses the U/V coordinates of every serialized trim vertex as
        // its interior grid. A grid line can intersect another topological edge between two
        // authored samples.
        var trimGrids = new Dictionary<uint,
            (List<double> U, List<double> V, bool SplitV, bool PeriodicU)>();
        // A Face whose m_pFirstLoop is null has no boundary and is not part of the shell (it is
        // what remains of a Face after a cut: GInfo flags 0x8204, no EdgeLoop and no GFilling); the
        // Edges that still name it as a neighbour belong to no loop of the live Faces.
        var deadFaces = c.ExactPlanarSpaceBoundary
            ? new HashSet<uint>()
            : c.FaceHasFirstLoop.Where(pair => !pair.Value).Select(pair => pair.Key).ToHashSet();
        foreach (var (handle, sourceEdges) in authoredSamplingEdges)
        {
            if (deadFaces.Contains(handle)) continue;
            var surface = handleToSurf[handle];
            if (surface.Kind == SurfKind.Ruled)
            {
                if (!TryEdgeParameterBounds(sourceEdges,
                        out double ruledUMin, out _, out double ruledUMax, out _))
                    continue;
                var ruledSamples = RuledParameterSamples(
                    surface, c.Curves, sourceEdges, ruledUMin, ruledUMax);
                if (ruledSamples != null)
                    trimGrids[handle] = (ruledSamples, [], false, false);
                continue;
            }
            if (surface.Kind is not (SurfKind.Cylinder or SurfKind.Cone or SurfKind.Rev))
                continue;
            var normalized = NormalizePeriodicEdges(sourceEdges, 2 * Math.PI);
            if (surface.Kind == SurfKind.Rev
                && c.Curves.TryGetValue(surface.ProfileHandle, out var profile))
                normalized = CloseRevolutionPoleEdges(normalized, profile);
            if (!TryEdgeParameterBounds(normalized,
                    out double uMin, out double vMin, out double uMax, out double vMax))
                continue;
            var uvSegments = normalized.Select(edge =>
                (new[] { edge.U1, edge.V1, 0d }, new[] { edge.U2, edge.V2, 0d })).ToList();
            int ringCount = OrderRingsW(uvSegments).Count;
            List<double> uSamples;
            List<double> vSamples;
            if (surface.Kind == SurfKind.Rev
                && c.Curves.TryGetValue(surface.ProfileHandle, out var curve)
                && curve.NurbControlPoints is { Count: > 0 })
            {
                (uSamples, vSamples) = RevolutionInteriorParameterSamples(
                    curve, uMin, uMax, vMin, vMax);
            }
            else
            {
                uSamples = ParameterSamples(normalized, true, uMin, uMax);
                vSamples = ParameterSamples(normalized, false, vMin, vMax);
            }
            trimGrids[handle] = (
                uSamples,
                vSamples,
                surface.Kind == SurfKind.Rev || ringCount == 1,
                true);
        }

        List<double>? splitFractions = null;
        foreach (var edge in c.Edges)
        {
            bool periodicA = handleToSurf.TryGetValue(edge.FaceA, out var surfaceA)
                && surfaceA.Kind is SurfKind.Cylinder or SurfKind.Cone or SurfKind.Rev;
            bool periodicB = handleToSurf.TryGetValue(edge.FaceB, out var surfaceB)
                && surfaceB.Kind is SurfKind.Cylinder or SurfKind.Cone or SurfKind.Rev;
            bool splitA = trimGrids.TryGetValue(edge.FaceA, out var gridA);
            bool splitB = trimGrids.TryGetValue(edge.FaceB, out var gridB);
            if (!periodicA && !periodicB && !splitA && !splitB
                && (edge.SharedBoundaryPoints == null || edge.SharedBoundaryPoints.Count == edge.Interior.Count + 2))
                continue;

            var points = new List<EdgeUV>(edge.Interior.Count + 2) { edge.Start };
            points.AddRange(edge.Interior);
            points.Add(edge.EndP);
            if (periodicA || periodicB)
            {
                for (int index = 1; index < points.Count; index++)
                {
                    var point = points[index];
                    var previous = points[index - 1];
                    if (periodicA)
                        point.UA += Math.Round((previous.UA - point.UA) / (2 * Math.PI))
                            * (2 * Math.PI);
                    if (periodicB)
                        point.UB += Math.Round((previous.UB - point.UB) / (2 * Math.PI))
                            * (2 * Math.PI);
                    points[index] = point;
                }
            }

            var refined = new List<EdgeUV> { points[0] };
            var sharedRefined = edge.SharedBoundaryPoints is { } sourceShared && sourceShared.Count == points.Count
                ? new List<double[]> { sourceShared[0] } : null;
            var fractions = splitFractions ??= new();
            for (int index = 0; index + 1 < points.Count; index++)
            {
                var first = points[index];
                var second = points[index + 1];
                fractions.Clear();
                void AddFractions(double start, double end,
                    IReadOnlyList<double> samples, bool periodic)
                {
                    double delta = end - start;
                    if (delta == 0) return;
                    double middle = (start + end) / 2;
                    foreach (double sample in samples)
                    {
                        long shift = periodic
                            ? (long)Math.Round((middle - sample) / (2 * Math.PI)) : 0;
                        int firstOffset = periodic ? -1 : 0;
                        int lastOffset = periodic ? 1 : 0;
                        for (int offset = firstOffset; offset <= lastOffset; offset++)
                        {
                            double value = sample + (shift + offset) * (2 * Math.PI);
                            double fraction = (value - start) / delta;
                            if (fraction > 0 && fraction < 1) fractions.Add(fraction);
                        }
                    }
                }
                if (splitA)
                {
                    AddFractions(first.UA, second.UA, gridA.U, gridA.PeriodicU);
                    if (gridA.SplitV)
                        AddFractions(first.VA, second.VA, gridA.V, periodic: false);
                }
                if (splitB)
                {
                    AddFractions(first.UB, second.UB, gridB.U, gridB.PeriodicU);
                    if (gridB.SplitV)
                        AddFractions(first.VB, second.VB, gridB.V, periodic: false);
                }
                fractions.Sort();
                double previousFraction = 0;
                foreach (double fraction in fractions)
                {
                    if (fraction == previousFraction) continue;
                    previousFraction = fraction;
                    refined.Add(new EdgeUV
                    {
                        UA = first.UA + (second.UA - first.UA) * fraction,
                        VA = first.VA + (second.VA - first.VA) * fraction,
                        UB = first.UB + (second.UB - first.UB) * fraction,
                        VB = first.VB + (second.VB - first.VB) * fraction,
                    });
                    if (sharedRefined != null)
                    {
                        var firstPoint = edge.SharedBoundaryPoints![index];
                        var secondPoint = edge.SharedBoundaryPoints[index + 1];
                        sharedRefined.Add(Enumerable.Range(0, 3)
                            .Select(axis => firstPoint[axis] + (secondPoint[axis] - firstPoint[axis]) * fraction).ToArray());
                    }
                }
                refined.Add(second);
                if (sharedRefined != null) sharedRefined.Add(edge.SharedBoundaryPoints![index + 1]);
            }
            edge.Start = refined[0];
            edge.EndP = refined[^1];
            edge.Interior = refined.GetRange(1, refined.Count - 2);
            edge.SharedBoundaryPoints = sharedRefined;
        }

        if (splitFractions != null)
        {
            byFace = new();
            CollectFaceSegments();
        }

        var gd = new GRepData
        {
            Faces = new List<GRepFace>(),
            FamilyClosurePlaneOrigin = c.FamilyClosurePlaneOrigin,
            FamilyClosurePlaneNormal = c.FamilyClosurePlaneNormal,
        };
        // GInfo distinguishes final body geometry from the cached construction/display branches
        // stored beside it in the same GRep. Geometry nodes carrying model control commands have
        // bit 0x4000; intermediate array states do not.
        var bodyFaceHandles = new HashSet<uint>();
        // faces that only the display-surface bit keeps out of the body (see
        // GRepData.DisplayOnlyNodes)
        var displayFaceHandles = new HashSet<uint>();
        var lightingFaceHandles = measureLighting || bodyMeasure == BodyMeasure.DirectionalRawSurface ? new HashSet<uint>() : null;
        var regionFaces = new HashSet<uint>();
        var walkedFaceHandles = c.FaceOrder.ToHashSet();
        var complexCutGeometryIndices = c.RootClass == 1829
            ? c.Queue.Select((item, index) => (item, index))
                .Where(entry => entry.item.Cls == 2092
                    && entry.index < c.QueueLinks.Count
                    && c.QueueLinks[entry.index].ParentQueueIndex < 0
                    && c.QueueLinks[entry.index].Field == "m_complexCutGeoms")
                .Select(entry => entry.index).ToHashSet()
            : [];
        gd.IsFamilyComplexCut = complexCutGeometryIndices.Count > 0;
        foreach (var (item, faceQueueIndex) in c.Queue.Select((item, index) => (item, index)))
        {
            if (item.Cls != 1589 || item.V <= 0) continue;
            if (complexCutGeometryIndices.Count > 0)
            {
                int cutAncestor = faceQueueIndex;
                bool belongsToComplexCut = false;
                while (cutAncestor >= 0 && cutAncestor < c.QueueLinks.Count)
                {
                    cutAncestor = c.QueueLinks[cutAncestor].ParentQueueIndex;
                    if (complexCutGeometryIndices.Contains(cutAncestor))
                    {
                        belongsToComplexCut = true;
                        break;
                    }
                }
                if (belongsToComplexCut) bodyFaceHandles.Add((uint)item.V);
                continue;
            }
            // GFace.m_faceRegions lists the sub-regions of a Face (split face): the Face itself
            // stays whole and is the one the exporter sees, so a region is never a Face of the
            // shell.
            if (!c.ExactPlanarSpaceBoundary && faceQueueIndex < c.QueueLinks.Count
                && c.QueueLinks[faceQueueIndex].Field == "m_faceRegions")
            {
                regionFaces.Add((uint)item.V);
                continue;
            }
            bool body = IsPhysicalModelBranch(c, faceQueueIndex);
            if (body) bodyFaceHandles.Add((uint)item.V);
            else if (IsPhysicalModelBranch(c, faceQueueIndex, allowDisplaySurface: true))
                displayFaceHandles.Add((uint)item.V);
            if (lightingFaceHandles != null && (body || IsPhysicalModelBranch(c, faceQueueIndex, forLightingQuantity: true)))
                lightingFaceHandles.Add((uint)item.V);
        }
        var inactiveBodyFaceHandles = bodyFaceHandles.Where(deadFaces.Contains).ToArray();
        bodyFaceHandles.ExceptWith(deadFaces);
        displayFaceHandles.ExceptWith(deadFaces);
        var loopsByFace = c.ExactPlanarSpaceBoundary && !c.CurvedSpaceBoundary ? new Dictionary<uint, List<int>>() : completeLoops;
        if (bodyMeasure != BodyMeasure.None)
        {
            var selected = lightingFaceHandles ?? bodyFaceHandles;
            if (lightingFaceHandles == null)
            {
                var nodes = ResolveGeometryNodes(c.Queue, c.QueueLinks, c.ObjectGInfo);
                selected.RemoveWhere(face => nodes.TryGetValue(face, out var node) && node.Node < 0);
            }
            else selected.ExceptWith(deadFaces);
            gd.SourceBodyMeasures = selected.Count == 0 ? (0, 0, 0)
                : MeasureClosedSourceBodies(c, selected, handleToSurf, loopsByFace,
                    volumeOnly: bodyMeasure == BodyMeasure.VisibleVolume, rawCoedges: bodyMeasure != BodyMeasure.VisibleVolume);
            AttachInstances(gd, c, selected.Count != 0);
            return gd;
        }
        if (measureFills) gd.FilledArea = MeasureFilledFaces(c, handleToSurf, loopsByFace);
        // The rings of an exact space boundary come from its segment graph; its serialized loops
        // only name the bounds of the IfcFace Revit writes for each Face (one bound per
        // loop, however many disjoint regions the Face owns).
        var spaceLoopsByFace = c.ExactPlanarSpaceBoundary && !c.CurvedSpaceBoundary ? completeLoops : null;
        if (Environment.GetEnvironmentVariable("RVTIO_TOPO") is { Length: > 0 } topologyPath
            && (Environment.GetEnvironmentVariable("RVTIO_TOPO_EID") is not { Length: > 0 } topologyEids
                || topologyEids.Split(',').Contains(c.ElementId.ToString())))
            DumpTopology(c, handleToSurf, bodyFaceHandles, spaceLoopsByFace ?? loopsByFace, topologyPath);
        var chainFaces = new HashSet<uint>();
        var chainEdges = new HashSet<uint>();
        if (c.Debug)
            DiagnosticSink.WriteLine($"  V2 eid={c.ElementId} body-selection={bodyFaceHandles.Count}/{c.FaceOrder.Count} faces dead={deadFaces.Count} loop-lists={loopsByFace.Count}");
        if (lightingFaceHandles != null)
        {
            lightingFaceHandles.ExceptWith(deadFaces);
            gd.LightingBodyMeasures = lightingFaceHandles.Count == 0 ? (0, 0, 0)
                : MeasureClosedSourceBodies(c, lightingFaceHandles, handleToSurf, loopsByFace);
            // A directional quantity with no nested instances needs only the source integral.
            if (gd.LightingBodyMeasures.HasValue && c.Instances.Count == 0 && !c.ExactPlanarSpaceBoundary)
                return gd;
        }

        // Revit's solid is a Geometry node, whatever number of disjoint shells it holds,
        // and a node it does not take is marked with a negative id (the faces stay in the geometry
        // for every other consumer): see ResolveGeometryNodes.
        var geometryNodeByFace = ResolveGeometryNodes(c.Queue, c.QueueLinks, c.ObjectGInfo, c.IncludeNonVisible);
        foreach (uint handle in inactiveBodyFaceHandles)
        {
            if (!c.DirectSurfaceByFace.TryGetValue(handle, out var surface) || surface.Kind == SurfKind.Unknown
                || !geometryNodeByFace.TryGetValue(handle, out var owner) || owner.Node <= 0) continue;
            var normal = surface.Kind == SurfKind.Plane ? Normalize(Cross(surface.X, surface.Y)) : new double[3];
            if (normal == null) continue;
            (gd.InactiveFaces ??= []).Add(new GRepFace
            {
                SourceFace = handle, GeometryNode = owner.Node, XVector = normal,
                CurvedSurface = surface.Kind != SurfKind.Plane,
            });
        }
        var nodeOrders = ComputeNodeOrders(c.Queue, c.QueueLinks, c.Instances.Select(instance => instance.OwnerQueueIndex).ToHashSet());
        var meshFaceAreas = ComputeFaceAreas(c, handleToSurf, bodyFaceHandles, spaceLoopsByFace ?? loopsByFace, sourceEdgesByHandle);
        // Its polygons stay out of the geometry; the node is only recorded, with the material of
        // its faces, for the material list of the product.
        var nodesWithBodyFace = bodyFaceHandles
            .Select(handle => geometryNodeByFace.GetValueOrDefault(handle).Node).Where(node => node > 0).ToHashSet();
        var displayOnlyNodes = displayFaceHandles
            .Where(handle => geometryNodeByFace.TryGetValue(handle, out var owner)
                && owner.Node > 0 && !nodesWithBodyFace.Contains(owner.Node))
            .GroupBy(handle => geometryNodeByFace[handle].Node).Where(group => group.Count() >= MinSolidFaces)
            .OrderBy(group => group.Key)
            .Select(group => (Node: group.Key, Style: geometryNodeByFace[group.First()].Style,
                RenderStyleId: group.Select(handle => c.FaceRenderStyle.GetValueOrDefault(handle)).Where(id => id > 0)
                    .GroupBy(id => id).OrderByDescending(ids => ids.Count()).ThenBy(ids => ids.Key)
                    .Select(ids => ids.Key).FirstOrDefault())).ToList();
        if (MeshEnabled && (!c.ExactPlanarSpaceBoundary || c.CurvedSpaceBoundary))
        {
            // the curved ones of those nodes are triangle sets like any other node of Revit
            // (the face meshes need no polygon)
            var displayNodeSet = MeshDisplayNodes ? displayOnlyNodes.Select(n => n.Node).ToHashSet() : [];
            var displayFaces = displayNodeSet.Count == 0 ? []
                : displayFaceHandles.Where(handle => geometryNodeByFace.TryGetValue(handle, out var owner) && displayNodeSet.Contains(owner.Node)).ToHashSet();
            var nodeMeasures = new List<MeshNodeMeasure>();
            gd.MeshNodes = BuildNodes(c, handleToSurf, displayFaces.Count > 0 ? bodyFaceHandles.Concat(displayFaces).ToHashSet() : bodyFaceHandles,
                inactiveBodyFaceHandles, loopsByFace, sourceEdgesByHandle, geometryNodeByFace, nodeOrders,
                displayFaces.Count > 0 ? displayOnlyNodes : null, nodeMeasures, measureSourceNodes);
            if (nodeMeasures.Count > 0) gd.NodeMeasures = nodeMeasures;
        }
        if (displayOnlyNodes.Count > 0) gd.DisplayOnlyNodes = displayOnlyNodes;

        // m_tightbBox is the schema-defined extent of the root representation. It is also a
        // structural consistency check: a decoded face outside it cannot belong to this GRep and
        // indicates a desynchronised deferred-object walk.
        // The serialized root extent covers visible geometry only. Hidden quantity solids can lie outside it.
        double[]? authoritativeBounds = c.IncludeNonVisible ? null : ValidBox(c.RootTightBoundingBox)
            ? c.RootTightBoundingBox
            : c.RootBoundingBox;
        var faceIndicesByHandle = new Dictionary<uint, List<int>>();
        var faceHandleByIndex = new List<uint>();
        var rejectedFaceHandles = new HashSet<uint>();
        var kindCounts = new Dictionary<string, int>();
        bool kindDiag = Environment.GetEnvironmentVariable("RVTIO_GREP_SLOW") is { Length: > 0 };
        void AddFace(uint handle, GRepFace face)
        {
            if (kindDiag)
            {
                string kindName = handleToSurf.TryGetValue(handle, out var counted) ? counted.Kind.ToString() : "direct/none";
                kindCounts[kindName] = kindCounts.GetValueOrDefault(kindName) + 1;
            }
            if (!bodyFaceHandles.Contains(handle) && walkedFaceHandles.Contains(handle)) return;
            var boundedFace = ClipToSerializedBounds(face, authoritativeBounds);
            if (c.ExactPlanarSpaceBoundary && !ReferenceEquals(boundedFace, face))
                throw Stop;
            if (boundedFace == null)
            {
                rejectedFaceHandles.Add(handle);
                if (c.Debug)
                {
                    var rejected = (face.Vertices ?? []).Concat((face.InnerVertices ?? []).SelectMany(x => x)).ToArray();
                    if (rejected.Length > 0)
                        DiagnosticSink.WriteLine($"    reject face {handle}: box=[{rejected.Min(p => p[0]):G8},{rejected.Min(p => p[1]):G8},{rejected.Min(p => p[2]):G8},{rejected.Max(p => p[0]):G8},{rejected.Max(p => p[1]):G8},{rejected.Max(p => p[2]):G8}]");
                }
                return;
            }
            if (!ReferenceEquals(boundedFace, face)) rejectedFaceHandles.Add(handle);
            var (faceNode, faceStyle) = geometryNodeByFace.GetValueOrDefault(handle, (0, -1));
            bool hasFaceGInfo = c.ObjectGInfo.TryGetValue((int)handle, out var faceGInfo);
            face = boundedFace with
            {
                SourceFace = handle, GeometryNode = faceNode,
                NodeOrder = faceNode != 0 ? nodeOrders.GetValueOrDefault(Math.Abs(faceNode) - 1) : 0,
                GeometryStyle = faceStyle,
                GInfoFlags = hasFaceGInfo ? faceGInfo.Flags : null,
                GInfoTag = hasFaceGInfo ? faceGInfo.Tag : null,
                MeshEdgeBounds = meshEdgeBounds.GetValueOrDefault(handle),
                // (the circular Edges of the loops the face was built from; the others are
                // leftovers of an earlier state of the join)
                MeshArcs = meshArcsByFace.GetValueOrDefault(handle) is { } faceArcs
                    && faceArcs.Where(arc => !chainFaces.Contains(handle) || chainEdges.Contains(arc.Edge)).ToList() is { Count: > 0 } liveArcs
                    ? liveArcs : null,
                QuadratureArea = meshFaceAreas.TryGetValue(handle, out double meshFaceArea) ? meshFaceArea : null,
                MeshPlaneEnvelopeCorners = c.DirectSurfaceByFace.TryGetValue(handle, out var sourcePlane)
                    && sourcePlane.Kind == SurfKind.Plane && sourcePlane.Env is { Length: 4 } sourceEnv
                    && World(ToPlane(sourcePlane), sourceEnv[0], sourceEnv[1]) is { } minCorner
                    && World(ToPlane(sourcePlane), sourceEnv[2], sourceEnv[3]) is { } maxCorner
                    ? [.. minCorner, .. maxCorner] : null,
            };
            int index = gd.Faces.Count;
            if (c.FaceRenderStyle.TryGetValue(handle, out int renderStyleId) && face.RenderStyleId != renderStyleId)
                face = face with { RenderStyleId = renderStyleId };
            gd.Faces.Add(face);
            faceHandleByIndex.Add(handle);
            if (!faceIndicesByHandle.TryGetValue(handle, out var indices))
                faceIndicesByHandle[handle] = indices = new();
            indices.Add(index);
            foreach (var p in face.Vertices ?? []) Bump(p);
            foreach (var ring in face.InnerVertices ?? []) foreach (var p in ring) Bump(p);
        }
        static GRepFace CylindricalPatchFace(
            SurfaceRec surface, List<double[]> vertices, double[] normal)
        {
            var face = new GRepFace
            {
                Vertices = vertices,
                Origin = vertices[0],
                XVector = normal,
                CurvedSurface = true,
            };
            if (surface.Kind == SurfKind.Cylinder)
            {
                face.AnalyticCylinderOrigin = (double[])surface.O.Clone();
                face.AnalyticCylinderAxis = (double[])surface.Z.Clone();
                face.AnalyticCylinderReferenceDirection = (double[])surface.X.Clone();
                face.AnalyticCylinderRadius = Math.Abs(surface.R);
            }
            return face;
        }
        double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue;
        double maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
        void Bump(double[] p)
        {
            minx = Math.Min(minx, p[0]); maxx = Math.Max(maxx, p[0]);
            miny = Math.Min(miny, p[1]); maxy = Math.Max(maxy, p[1]);
            minz = Math.Min(minz, p[2]); maxz = Math.Max(maxz, p[2]);
        }

        foreach (var kv in byFace)
        {
            if (deadFaces.Contains(kv.Key) || regionFaces.Contains(kv.Key)) continue;
            t_coarseTessellation = gd.Faces.Count >= CoarseTessellationFaceBudget;
            var surf = handleToSurf[kv.Key];
            if (surf.Kind == SurfKind.Hermite)
            {
                foreach (var quad in TessellateHermiteSurface(surf, kv.Value))
                {
                    var (n, _) = NewellNormalArea(quad);
                    AddFace(kv.Key, new GRepFace { Vertices = quad, Origin = quad[0], XVector = n, CurvedSurface = true });
                }
                continue;
            }
            if (surf.Kind == SurfKind.Rev)
            {
                if (c.Curves.TryGetValue(surf.ProfileHandle, out var arc))
                {
                    foreach (var quad in TessellateRevolution(surf, arc, kv.Value,
                                 authoredSamplingEdges.GetValueOrDefault(kv.Key)))
                    {
                        var (n, _) = NewellNormalArea(quad);
                        AddFace(kv.Key, new GRepFace { Vertices = quad, Origin = quad[0], XVector = n, CurvedSurface = true });
                    }
                }
                continue;
            }
            if (surf.Kind == SurfKind.Ruled)
            {
                foreach (var quad in TessellateRuled(surf, c.Curves, kv.Value,
                             authoredSamplingEdges.GetValueOrDefault(kv.Key)))
                {
                    var (n, _) = NewellNormalArea(quad);
                    AddFace(kv.Key, new GRepFace { Vertices = quad, Origin = quad[0], XVector = n, CurvedSurface = true });
                }
                continue;
            }
            if (surf.IsCyl)
            {
                // Cylindrical face: tessellate over the u(angle)/v(axial) span of its edges.
                if (c.Debug)
                {
                    var normalizedEdges = NormalizePeriodicEdges(kv.Value, 2 * Math.PI);
                    var normalizedSamples = NormalizePeriodicEdges(
                        authoredSamplingEdges.GetValueOrDefault(kv.Key) ?? kv.Value,
                        2 * Math.PI);
                    if (TryEdgeParameterBounds(normalizedEdges,
                            out double diagnosticUMin, out double diagnosticVMin,
                            out double diagnosticUMax, out double diagnosticVMax))
                    {
                        var diagnosticUSamples = ParameterSamples(normalizedSamples,
                            useU: true, diagnosticUMin, diagnosticUMax);
                        var diagnosticVSamples = ParameterSamples(normalizedSamples,
                            useU: false, diagnosticVMin, diagnosticVMax);
                        double? initialGridAspect = null;
                        if (surf.Kind == SurfKind.Cylinder)
                        {
                            var angularChords = diagnosticUSamples.Zip(
                                    diagnosticUSamples.Skip(1),
                                    (first, second) => 2 * Math.Abs(surf.R)
                                        * Math.Sin(Math.Min(Math.PI, Math.Abs(second - first)) / 2))
                                .Where(length => length > 1e-12)
                                .ToList();
                            var axialSteps = diagnosticVSamples.Zip(
                                    diagnosticVSamples.Skip(1),
                                    (first, second) => Math.Abs(second - first))
                                .Where(length => length > 1e-12)
                                .ToList();
                            if (angularChords.Count > 0 && axialSteps.Count > 0)
                            {
                                double longest = Math.Max(angularChords.Max(), axialSteps.Max());
                                double shortest = Math.Min(angularChords.Min(), axialSteps.Min());
                                initialGridAspect = longest / shortest;
                            }
                        }
                        string surfaceScale = surf.Kind == SurfKind.Cylinder
                            ? $"radius={Math.Abs(surf.R):G8}"
                            : $"half-angle={Math.Abs(surf.R):G8}";
                        DiagnosticSink.WriteLine($"    trim-grid face={kv.Key} kind={surf.Kind} "
                            + $"edges={normalizedEdges.Count} u={diagnosticUSamples.Count} "
                            + $"v={diagnosticVSamples.Count} "
                            + $"range=[{diagnosticUMin:G8},{diagnosticVMin:G8},"
                            + $"{diagnosticUMax:G8},{diagnosticVMax:G8}] "
                            + $"{surfaceScale} "
                            + $"initial-grid-aspect={initialGridAspect?.ToString("G8") ?? "n/a"}");
                    }
                }
                IEnumerable<List<double[]>> cylinderPatches = TessellateCylinder(surf, kv.Value,
                    authoredSamplingEdges.GetValueOrDefault(kv.Key),
                    incompleteWorldEdges.Contains(kv.Key) ? null : authoredWorldEdges.GetValueOrDefault(kv.Key));
                if (c.Debug)
                {
                    var materializedPatches = cylinderPatches.ToList();
                    DiagnosticSink.WriteLine($"    trim-grid face={kv.Key} patches={materializedPatches.Count}");
                    cylinderPatches = materializedPatches;
                }
                foreach (var quad in cylinderPatches)
                {
                    var (n, _) = NewellNormalArea(quad);
                    AddFace(kv.Key, CylindricalPatchFace(surf, quad, n));
                }
                continue;
            }
            // Planar face: map UVs -> world, order into a ring.
            var segs = new List<(double[] A, double[] B)>();
            List<List<double[]>>? loopRings = null;
            if (loopsByFace.TryGetValue(kv.Key, out var faceLoops))
            {
                loopRings = TryOrderLoopRings(c, kv.Key, faceLoops, sourceEdgesByHandle, ToPlane(surf), segs, chainEdges);
                if (loopRings == null) segs.Clear(); else chainFaces.Add(kv.Key);
            }
            if (loopRings == null)
                foreach (var s in kv.Value)
                {
                    var a = World(ToPlane(surf), s.U1, s.V1);
                    var b = World(ToPlane(surf), s.U2, s.V2);
                    if (a != null && b != null)
                        segs.Add(c.WeldFeet > 0 ? (c.Weld(a), c.Weld(b)) : (a, b));
                }
            if (segs.Count < 3) continue;
            var rings = (loopRings ?? OrderRingsW(segs))
                .Select(r => (Ring: r, Shape: NewellNormalArea(r)))
                .Where(x => x.Shape.Area >= 1e-6)
                .OrderByDescending(x => x.Shape.Area)
                .ToList();
            if (c.Debug)
                DiagnosticSink.WriteLine($"    face {kv.Key}: segments={segs.Count}, rings={rings.Count}, loops=[{string.Join(',', rings.Select(r => $"{r.Ring.Count}:{r.Shape.Area:G6}"))}]");
            if (rings.Count == 0) continue;

            var alternativeOuter = OrderRingsW(segs, includeContainingCycles: true)
                .Select(ring => (Ring: ring, Shape: NewellNormalArea(ring)))
                .Where(candidate => candidate.Shape.Area > rings[0].Shape.Area)
                .OrderByDescending(candidate => candidate.Shape.Area)
                .Select(candidate => candidate.Ring)
                .FirstOrDefault();

            List<List<double[]>>? spaceSourceLoops = null;
            if (spaceLoopsByFace != null && spaceLoopsByFace.TryGetValue(kv.Key, out var spaceFaceLoops))
            {
                spaceSourceLoops = TryOrderLoopRings(c, kv.Key, spaceFaceLoops, sourceEdgesByHandle, ToPlane(surf), [], []);
            }

            // One serialized Face handle can own several disconnected coplanar regions. Only a
            // strictly contained loop is a hole; treating every smaller/equal loop as an inner
            // bound drops valid caps and merges otherwise independent solids.
            foreach (var region in ClassifyCoplanarRings(
                         rings.Select(candidate => candidate.Ring).ToList(),
                         exactPredicates: c.ExactPlanarSpaceBoundary))
                AddFace(kv.Key, new GRepFace
                {
                    Vertices = region.Outer,
                    InnerVertices = region.Holes.Count > 0 ? region.Holes : null,
                    AlternativeOuterVertices = ReferenceEquals(region.Outer, rings[0].Ring)
                        ? alternativeOuter : null,
                    Origin = region.Outer[0],
                    XVector = region.Normal,
                    PlaneX = (double[])surf.X.Clone(),
                    PlaneY = (double[])surf.Y.Clone(),
                    FirstSourceLoop = loopRings?[0],
                    SourceLoops = loopRings ?? spaceSourceLoops,
                    PlaneSurface = surf.Kind == SurfKind.Plane,
                });
        }

        // Faces whose surface was found but whose UV edges were never captured (the queue walk
        // bailed at a format-variant boundary before reaching their Edge records): emit them from
        // the surface ENVELOPE - the env quad in the plane's frame.
        double coreDiag = 0;
        double[]? core = null;
        if (gd.Faces.Count > 0 && IsFinite(minx) && IsFinite(maxx))
        {
            coreDiag = Math.Sqrt((maxx - minx) * (maxx - minx) + (maxy - miny) * (maxy - miny) + (maxz - minz) * (maxz - minz));
            double m = Math.Max(coreDiag * 0.5, 1.0);
            core = new[] { minx - m, miny - m, minz - m, maxx + m, maxy + m, maxz + m };
        }
        bool InCore(List<double[]> poly)
        {
            if (core == null) return true;
            foreach (var p in poly)
                if (p[0] < core[0] || p[1] < core[1] || p[2] < core[2] ||
                    p[0] > core[3] || p[1] > core[4] || p[2] > core[5]) return false;
            return true;
        }
        foreach (var kv in handleToSurf)
        {
            if (byFace.ContainsKey(kv.Key) || deadFaces.Contains(kv.Key)) continue;
            // Grammar-harvested pairs carry stronger provenance (blob-anchored EdgeLoop -> face
            // handle -> signature-validated surface in its triplet span) - and tail regions
            // legitimately extend past the walked core; don't core-gate them.
            bool harvested = c.HarvestSurf.ContainsKey(kv.Key);
            bool Pass(List<double[]> poly) => harvested || InCore(poly);
            var s = kv.Value;
            if (s.Env is not { Length: 4 } env || !IsFinite(env[0]) || !IsFinite(env[3])) continue;
            double du = env[2] - env[0], dv = env[3] - env[1];
            if (du <= 1e-9 || dv <= 1e-9 || du > 1e4 || dv > 1e4) continue;
            if (s.Kind == SurfKind.Plane)
            {
                var quad = new List<double[]>();
                bool ok = true;
                foreach (var (u, v) in new[] { (env[0], env[1]), (env[2], env[1]), (env[2], env[3]), (env[0], env[3]) })
                {
                    var p = World(ToPlane(s), u, v);
                    if (p == null) { ok = false; break; }
                    quad.Add(p);
                }
                if (!ok || !Pass(quad)) continue;
                var (n, area) = NewellNormalArea(quad);
                if (area < 1e-6) continue;
                AddFace(kv.Key, new GRepFace { Vertices = quad, Origin = quad[0], XVector = n, PlaneX = (double[])s.X.Clone(), PlaneY = (double[])s.Y.Clone(), PlaneSurface = true });
            }
            else if (s.IsCyl)
            {
                foreach (var quad in TessellateCylinder(s, new List<(double, double, double, double)>()))
                {
                    if (!Pass(quad)) continue;
                    var (n, _) = NewellNormalArea(quad);
                    AddFace(kv.Key, CylindricalPatchFace(s, quad, n));
                }
            }
            else if (s.Kind == SurfKind.Hermite)
            {
                foreach (var quad in TessellateHermiteSurface(s, new List<(double, double, double, double)>()))
                {
                    if (!Pass(quad)) continue;
                    var (n, _) = NewellNormalArea(quad);
                    AddFace(kv.Key, new GRepFace { Vertices = quad, Origin = quad[0], XVector = n, CurvedSurface = true });
                }
            }
            else if (s.Kind == SurfKind.Rev && c.Curves.TryGetValue(s.ProfileHandle, out var arc))
            {
                foreach (var quad in TessellateRevolution(s, arc, new List<(double, double, double, double)>()))
                {
                    if (!Pass(quad)) continue;
                    var (n, _) = NewellNormalArea(quad);
                    AddFace(kv.Key, new GRepFace { Vertices = quad, Origin = quad[0], XVector = n, CurvedSurface = true });
                }
            }
            else if (s.Kind == SurfKind.Ruled)
            {
                foreach (var quad in TessellateRuled(s, c.Curves, new List<(double, double, double, double)>()))
                {
                    if (!Pass(quad)) continue;
                    var (n, _) = NewellNormalArea(quad);
                    AddFace(kv.Key, new GRepFace { Vertices = quad, Origin = quad[0], XVector = n, CurvedSurface = true });
                }
            }
        }

        if (requireCompleteWalk && gd.LightingBodyMeasures == null && !bodyFaceHandles.SetEquals(faceIndicesByHandle.Keys))
        {
            // The display bounds can clip a complete body, including the ends of circular piles.
            if (!measureSourceNodes || MeasureClosedSourceBodies(c, bodyFaceHandles, handleToSurf, loopsByFace) is not { } measures) return null;
            gd.SourceBodyMeasures = measures;
            gd.SourceCapAreas = MeasureHorizontalCaps(c, bodyFaceHandles, handleToSurf, loopsByFace);
        }

        if (gd.Faces.Count == 0 && gd.LightingBodyMeasures == null && gd.SourceBodyMeasures == null
            && !(requireCompleteWalk && bodyFaceHandles.Count == 0))
            return PolyMeshGeometry(c) ?? (c.Instances.Count > 0 ? InstanceGeometry(c) : null);

        if (c.ExactPlanarSpaceBoundary)
        {
            // A lost Face cannot be compensated by another Face splitting into regions.
            if (!bodyFaceHandles.SetEquals(faceIndicesByHandle.Keys)) return null;
            // Matching Face/polygon counts do not confirm complete regions: one Face may lose a
            // second loop while still contributing its first polygon. Confirm every authored trim,
            // even in the one-polygon-per-Face group.
            foreach (uint handle in bodyFaceHandles)
            {
                if (handleToSurf[handle].Kind != SurfKind.Plane) continue;
                var plane = ToPlane(handleToSurf[handle]);
                var segments = new List<(double[] A, double[] B)>();
                foreach (var segment in authoredSamplingEdges[handle])
                {
                    var a = World(plane, segment.U1, segment.V1);
                    var b = World(plane, segment.U2, segment.V2);
                    if (a == null || b == null) return null;
                    segments.Add(c.WeldFeet > 0 ? (c.Weld(a), c.Weld(b)) : (a, b));
                }
                if (!HasExactPlanarTrimCoverage(segments,
                        faceIndicesByHandle[handle].Select(index => gd.Faces[index]).ToList(),
                        subdividedSource: c.CurvedSpaceBoundary))
                    return null;
            }
        }

        if (c.Debug)
        {
            var missingFaces = handleToSurf.Keys.Where(h => !faceIndicesByHandle.ContainsKey(h)).OrderBy(h => h).ToList();
            DiagnosticSink.WriteLine($"  V2 eid={c.ElementId} emitted={gd.Faces.Count} handles={faceIndicesByHandle.Count} missing-emitted=[{string.Join(',', missingFaces)}]");
            foreach (var group in missingFaces
                .GroupBy(h => handleToSurf[h].Kind)
                .OrderBy(g => g.Key))
            {
                var segmentCounts = group
                    .GroupBy(h => byFace.TryGetValue(h, out var segments) ? segments.Count : 0)
                    .OrderBy(g => g.Key)
                    .Select(g => $"{g.Key}:{g.Count()}");
                DiagnosticSink.WriteLine($"    missing kind={group.Key} count={group.Count()} edgeSegments=[{string.Join(',', segmentCounts)}]");
            }
            foreach (var group in faceIndicesByHandle
                .GroupBy(kv => handleToSurf[kv.Key].Kind)
                .OrderBy(g => g.Key))
                DiagnosticSink.WriteLine($"    emitted kind={group.Key} handles={group.Count()} parts={group.Sum(kv => kv.Value.Count)}");
            foreach (var group in byFace
                .Where(kv => handleToSurf[kv.Key].Kind is not SurfKind.Plane)
                .Select(kv =>
                {
                    var surface = handleToSurf[kv.Key];
                    var diagnosticEdges = surface.Kind is SurfKind.Cylinder or SurfKind.Cone or SurfKind.Rev
                        ? NormalizePeriodicEdges(kv.Value, 2 * Math.PI)
                        : kv.Value;
                    if (surface.Kind == SurfKind.Rev
                        && c.Curves.TryGetValue(surface.ProfileHandle, out var profile))
                        diagnosticEdges = CloseRevolutionPoleEdges(diagnosticEdges, profile);
                    var uvSegments = diagnosticEdges.Select(e =>
                        (new[] { e.U1, e.V1, 0d }, new[] { e.U2, e.V2, 0d })).ToList();
                    var rings = OrderRingsW(uvSegments);
                    return (Kind: handleToSurf[kv.Key].Kind,
                        Segments: kv.Value.Count, Rings: rings.Count);
                })
                .GroupBy(x => (x.Kind, x.Segments, x.Rings))
                .OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Segments).ThenBy(g => g.Key.Rings))
                DiagnosticSink.WriteLine($"    trim kind={group.Key.Kind} segments={group.Key.Segments} rings={group.Key.Rings} handles={group.Count()}");
            foreach (var group in c.FaceOrder
                .Where(handle => c.ObjectGInfo.ContainsKey((int)handle))
                .GroupBy(handle =>
                {
                    var info = c.ObjectGInfo[(int)handle];
                    return (info.Control, info.Category, info.Flags);
                })
                .OrderByDescending(group => group.Count()))
                DiagnosticSink.WriteLine($"    face-ginfo control={group.Key.Control} category={group.Key.Category} flags=0x{group.Key.Flags:X8} handles={group.Count()} emitted={group.Count(handle => faceIndicesByHandle.ContainsKey(handle))}");
            DiagnosticSink.WriteLine($"    rejected-by-root-bounds handles={rejectedFaceHandles.Count}");
            foreach (var pair in faceIndicesByHandle.OrderBy(x => x.Key))
            {
                var pts = pair.Value.SelectMany(i => gd.Faces[i].Vertices ?? []).ToArray();
                if (pts.Length == 0) continue;
                var box = new[]
                {
                    pts.Min(p => p[0]), pts.Min(p => p[1]), pts.Min(p => p[2]),
                    pts.Max(p => p[0]), pts.Max(p => p[1]), pts.Max(p => p[2]),
                };
                DiagnosticSink.WriteLine($"    emitted face {pair.Key}: parts={pair.Value.Count}, box=[{string.Join(',', box.Select(x => x.ToString("G8", System.Globalization.CultureInfo.InvariantCulture)))}]");
            }
        }

        // Edge.m_pFace is the serialized BRep topology and is authoritative for solid membership.
        gd.Solids.AddRange(BuildTopologicalSolids(gd.Faces, faceHandleByIndex,
            c.Edges.Where(edge => chainEdges.Contains(edge.Handle)
                    || !deadFaces.Contains(edge.FaceA) && !deadFaces.Contains(edge.FaceB)
                        && !regionFaces.Contains(edge.FaceA) && !regionFaces.Contains(edge.FaceB)
                        && !chainFaces.Contains(edge.FaceA) && !chainFaces.Contains(edge.FaceB))
                .Select(edge => (edge.FaceA, edge.FaceB))));
        if (measureSourceNodes && gd.Solids.Count > 1)
        {
            // The saved framing cut keeps the trimmed body's faces and the detached pieces in separate shells.
            var keptFaces = bodyFaceHandles.Where(face => (c.ObjectGInfo.GetValueOrDefault((int)face).Flags & 0x00080000u) != 0).ToHashSet();
            if (keptFaces.Count > 0 && keptFaces.Count < bodyFaceHandles.Count
                && gd.Solids.Any(solid => solid.IsClosed && keptFaces.SetEquals(solid.FaceIndices.Select(index => gd.Faces[index].SourceFace))))
                gd.TrimmedBodyMeasures = MeasureAnalyticSolid(c, c.FaceOrder.Where(keptFaces.Contains), handleToSurf, loopsByFace, EdgesByHandle(c), true);
        }

        int polyMeshFaceCount = AppendPolyMeshes(gd, c);
        gd.BoundingBox = authoritativeBounds is { Length: 6 }
            ? (double[])authoritativeBounds.Clone()
            : GeometryBounds(gd.Faces) ?? new[] { minx, miny, minz, maxx, maxy, maxz };
        bool hasCompleteResolvedBody = bodyFaceHandles.Count > 0
            && (gd.SourceBodyMeasures.HasValue || bodyFaceHandles.All(faceIndicesByHandle.ContainsKey))
            && polyMeshFaceCount == 0;
        AttachInstances(gd, c, hasCompleteResolvedBody);
        System.Threading.Interlocked.Increment(ref DbgWorldElems);
        System.Threading.Interlocked.Add(ref DbgWorldFaces, gd.Faces.Count);
        if (kindDiag && gd.Faces.Count > 100000)
            DiagnosticSink.WriteLine($"GRep eid={c.ElementId} faces={gd.Faces.Count} by surface kind: "
                + string.Join(", ", kindCounts.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key}={pair.Value}")));
        if (gd.MeshNodes != null && rejectedFaceHandles.Count > 0)
            gd.MeshNodes = gd.MeshNodes.Select(node => node.Faces.Any(face => rejectedFaceHandles.Contains((uint)face.Marker))
                ? node with { SourceMeasures = null } : node).ToList();
        return gd;
    }

    /// <summary>
    /// Confirms that disjoint source trim cycles were partitioned without losing or inventing edges.
    /// Equality uses the existing OrderRingsW/IFC 0.01 mm lattice; coordinates themselves are
    /// neither rounded nor changed.
    /// </summary>
    internal static bool HasExactPlanarTrimCoverage(
        IReadOnlyList<(double[] A, double[] B)> source,
        IReadOnlyList<GRepFace> regions, bool requireDisjointCycles = true, bool subdividedSource = false)
    {
        if (source.Count < 3 || regions.Count == 0) return false;
        const double latticeFeet = 1e-5 / 0.3048;
        static (long X, long Y, long Z)? Key(double[] point)
        {
            if (point.Length != 3) return null;
            var bins = new long[3];
            for (int axis = 0; axis < 3; axis++)
            {
                double bin = Math.Round(point[axis] / latticeFeet);
                if (!double.IsFinite(bin) || bin <= long.MinValue || bin >= long.MaxValue)
                    return null;
                bins[axis] = (long)bin;
            }
            return (bins[0], bins[1], bins[2]);
        }
        var edges = new HashSet<((long, long, long) A, (long, long, long) B)>();
        var degrees = new Dictionary<(long, long, long), int>();
        foreach (var (start, end) in source)
        {
            if (Key(start) is not { } a || Key(end) is not { } b || a == b)
                return false;
            if (!edges.Add(a.CompareTo(b) < 0 ? (a, b) : (b, a))) return false;
            degrees[a] = degrees.GetValueOrDefault(a) + 1;
            degrees[b] = degrees.GetValueOrDefault(b) + 1;
        }
        if (requireDisjointCycles && degrees.Values.Any(degree => degree != 2)) return false;
        var authoredVertices = subdividedSource ? degrees.Keys.ToHashSet() : null;
        foreach (var region in regions)
        {
            if (region.Vertices is not { Count: >= 3 }) return false;
            foreach (var ring in new[] { region.Vertices }.Concat(region.InnerVertices ?? []))
            {
                if (ring.Count < 3) return false;
                // the vertices a trim grid added to a refined Edge are not ends of an authored
                // segment: the polygon owns the segment they cut
                var corners = subdividedSource ? ring.Where(point => Key(point) is { } key && authoredVertices!.Contains(key)).ToList() : ring;
                if (corners.Count < 3) return false;
                for (int index = 0; index < corners.Count; index++)
                {
                    if (Key(corners[index]) is not { } a
                        || Key(corners[(index + 1) % corners.Count]) is not { } b || a == b
                        || !edges.Remove(a.CompareTo(b) < 0 ? (a, b) : (b, a)))
                        return false;
                }
            }
        }
        return edges.Count == 0;
    }

    internal static List<GRepSolid> BuildTopologicalSolids(
        IReadOnlyList<GRepFace> faces,
        IReadOnlyList<uint> faceHandles,
        IEnumerable<(uint FaceA, uint FaceB)> serializedEdges)
    {
        if (faces.Count != faceHandles.Count)
            throw new ArgumentException("Every emitted face must have a serialized Face handle.");

        var indicesByHandle = Enumerable.Range(0, faceHandles.Count)
            .GroupBy(index => faceHandles[index])
            .ToDictionary(group => group.Key, group => group.ToList());
        var parent = Enumerable.Range(0, faces.Count).ToArray();
        int Find(int index)
        {
            int root = index;
            while (parent[root] != root) root = parent[root];
            while (index != root)
            {
                int next = parent[index];
                parent[index] = root;
                index = next;
            }
            return root;
        }
        void Union(int first, int second)
        {
            int firstRoot = Find(first), secondRoot = Find(second);
            if (firstRoot != secondRoot) parent[secondRoot] = firstRoot;
        }

        foreach (var indices in indicesByHandle.Values)
            foreach (int index in indices.Skip(1))
                Union(indices[0], index);

        static bool ValidHandle(uint handle) => handle != 0 && handle != 0xFFFFFFFF;
        var connected = new HashSet<uint>();
        var dangling = new HashSet<uint>();
        foreach (var edge in serializedEdges)
        {
            indicesByHandle.TryGetValue(edge.FaceA, out var first);
            indicesByHandle.TryGetValue(edge.FaceB, out var second);
            if (first != null && second != null && ValidHandle(edge.FaceA) && ValidHandle(edge.FaceB))
            {
                Union(first[0], second[0]);
                connected.Add(edge.FaceA);
                connected.Add(edge.FaceB);
            }
            else
            {
                if (first != null) dangling.Add(edge.FaceA);
                if (second != null) dangling.Add(edge.FaceB);
            }
        }

        var solids = new List<GRepSolid>();
        foreach (var component in Enumerable.Range(0, faces.Count).GroupBy(Find))
        {
            var faceIndices = component.OrderBy(index => index).ToList();
            if (faceIndices.Count < 4) continue;

            // Every valid edge between emitted faces joined their components above.
            bool topologicallyClosed = faceIndices.All(index =>
                connected.Contains(faceHandles[index]) && !dangling.Contains(faceHandles[index]));
            bool closed = topologicallyClosed || IsExactClosedShell(faces, faceIndices);
            solids.Add(new GRepSolid { FaceIndices = faceIndices, IsClosed = closed });
        }

        return solids;
    }

    /// <summary>
    /// A polygonal shell is closed exactly when every undirected boundary edge is used by two face
    /// loops.
    /// </summary>
    internal static bool IsExactClosedShell(
        IReadOnlyList<GRepFace> faces, IReadOnlyCollection<int> faceIndices)
    {
        static (long X, long Y, long Z) Key(double[] point) =>
            (BitConverter.DoubleToInt64Bits(point[0]), BitConverter.DoubleToInt64Bits(point[1]),
                BitConverter.DoubleToInt64Bits(point[2]));
        static ((long X, long Y, long Z) A, (long X, long Y, long Z) B) EdgeKey(
            (long X, long Y, long Z) a, (long X, long Y, long Z) b)
            => a.CompareTo(b) <= 0 ? (a, b) : (b, a);

        var edgeUses = new Dictionary<
            ((long X, long Y, long Z) A, (long X, long Y, long Z) B),
            (int Count, int DirectionBalance)>();
        foreach (int faceIndex in faceIndices)
        {
            if ((uint)faceIndex >= faces.Count) return false;
            var face = faces[faceIndex];
            IEnumerable<IReadOnlyList<double[]>> rings = face.Vertices is { Count: >= 3 } outer
                ? new[] { (IReadOnlyList<double[]>)outer }
                    .Concat(face.InnerVertices ?? Enumerable.Empty<List<double[]>>())
                : Enumerable.Empty<IReadOnlyList<double[]>>();
            foreach (var ring in rings)
            {
                if (ring.Count < 3 || ring.Any(point => point.Length < 3)) return false;
                for (int index = 0; index < ring.Count; index++)
                {
                    var a = Key(ring[index]);
                    var b = Key(ring[(index + 1) % ring.Count]);
                    if (a == b) return false;
                    var edge = EdgeKey(a, b);
                    var use = edgeUses.GetValueOrDefault(edge);
                    int direction = a.CompareTo(b) <= 0 ? 1 : -1;
                    edgeUses[edge] = (use.Count + 1, use.DirectionBalance + direction);
                }
            }
        }
        return edgeUses.Count > 0
            && edgeUses.Values.All(use => use.Count == 2 && use.DirectionBalance == 0);
    }

    /// <summary>
    /// The Geometry node that owns each Face, with the graphics style (GStyleElem id, -1 for none)
    /// in the node's GInfo.
    /// </summary>
    internal static Dictionary<uint, (int Node, int Style)> ResolveGeometryNodes(
        IReadOnlyList<(ushort Cls, int V)> queue, IReadOnlyList<(int ParentQueueIndex, string Field)> links,
        IReadOnlyDictionary<int, (int Tag, int Control, int Category, uint Flags)> gInfo,
        bool includeNonVisible = false)
    {
        const ushort faceClass = 1589, geometryClass = 2092;
        const uint visibilityBits = 0x1E0;
        var nodeByFace = new Dictionary<uint, int>();
        var nodesWithVisibleFace = new HashSet<int>();
        for (int faceIndex = 0; faceIndex < queue.Count && faceIndex < links.Count; faceIndex++)
        {
            var face = queue[faceIndex];
            if (face.Cls != faceClass || face.V <= 0) continue;
            for (int owner = links[faceIndex].ParentQueueIndex;
                owner >= 0 && owner < queue.Count && owner < links.Count; owner = links[owner].ParentQueueIndex)
                if (queue[owner].Cls == geometryClass)
                {
                    nodeByFace[(uint)face.V] = owner + 1;
                    if (gInfo.TryGetValue(face.V, out var faceInfo) && (faceInfo.Flags & visibilityBits) == 0)
                        nodesWithVisibleFace.Add(owner + 1);
                    break;
                }
        }
        var result = new Dictionary<uint, (int Node, int Style)>();
        foreach (var (handle, node) in nodeByFace)
        {
            bool hasInfo = gInfo.TryGetValue(queue[node - 1].V, out var nodeInfo);
            bool taken = includeNonVisible || (nodesWithVisibleFace.Contains(node) && !(hasInfo && (nodeInfo.Flags & visibilityBits) != 0));
            result[handle] = (taken ? node : -node, hasInfo ? nodeInfo.Category : -1);
        }
        return result;
    }

    /// <summary>
    /// Bits an order key keeps for one nesting level of GInstance symbols: the first level in bits
    /// 42..62, a nested symbol's own keys one level lower (see <see cref="NestOrderKey"/>).
    /// </summary>
    internal const int OrderLevelShift = 21;
    internal const int OrderTopShift = 2 * OrderLevelShift;

    /// <summary>
    /// Revit collects the solids of a GElement by walking its nodes depth first.
    /// </summary>
    internal static Dictionary<int, long> ComputeNodeOrders(IReadOnlyList<(ushort Cls, int V)> queue,
        IReadOnlyList<(int ParentQueueIndex, string Field)> links, IReadOnlyCollection<int> instanceOwners)
    {
        const ushort geometryClass = 2092;
        int count = Math.Min(queue.Count, links.Count);
        var children = new List<int>?[count];
        var roots = new List<int>();
        for (int index = 0; index < count; index++)
        {
            int parent = links[index].ParentQueueIndex;
            if (parent < 0 || parent >= count) roots.Add(index);
            else (children[parent] ??= new List<int>()).Add(index);
        }
        var owners = instanceOwners as ISet<int> ?? instanceOwners.ToHashSet();
        var orders = new Dictionary<int, long>();
        var stack = new Stack<int>();
        for (int index = roots.Count - 1; index >= 0; index--) stack.Push(roots[index]);
        long rank = 0;
        while (stack.Count > 0)
        {
            int node = stack.Pop();
            if (queue[node].Cls == geometryClass || owners.Contains(node))
                orders[node] = ++rank << OrderTopShift;
            if (children[node] is { } list)
                for (int index = list.Count - 1; index >= 0; index--) stack.Push(list[index]);
        }
        return orders;
    }

    /// <summary>
    /// The order key of a node of a symbol placed at a GInstance whose own key is <paramref
    /// name="instanceKey"/>: the node's key moves one level down and is added to the instance's.
    /// </summary>
    internal static long NestOrderKey(long instanceKey, long nodeKey) => instanceKey | (nodeKey >> OrderLevelShift);

    internal const uint DisplaySurfaceBit = 0x00200000u;

    internal const int MinSolidFaces = 4;

    internal static bool IsPhysicalBodySurface(uint flags)
        => (flags & (DisplaySurfaceBit | 0x04000000u | 0x40000000u)) == 0;

    /// <summary>The GInfo flags of a drawable node as <see cref="IsPhysicalBodySurface"/> judges them. Bit 0x40000000 stays for a
    /// Face, except for the faces of an exactly decoded space boundary, and is not judged for a GPolyMesh: the polygon mesh of an RPC
    /// entourage carries it, and the current profile exports all facets of such a mesh as the body of the proxy.</summary>
    internal static uint NodeSurfaceFlags(int nodeClass, bool exactPlanarSpaceBoundary, uint flags)
        => exactPlanarSpaceBoundary && nodeClass == 1589 || nodeClass == 2038 ? flags & ~0x40000000u : flags;

    private readonly record struct ModelFilter(int DetailLevels, bool HasDetail, bool HasDirection, bool AllEvaluated)
    {
        public bool Allows(int detail) => (DetailLevels & (1 << detail)) != 0;
    }

    private static ModelFilter ReadModelFilter(Cur c, int parent)
    {
        int allowed = (1 << CoarseDetailLevel) | (1 << 2) | (1 << FineDetailLevel);
        bool hasDetail = false, hasDirection = false, allEvaluated = true;
        if (c.QueueChildren.TryGetValue(parent, out var children))
            foreach (int child in children)
            {
                if (child < 0 || child >= c.Queue.Count || child >= c.QueueLinks.Count
                    || c.QueueLinks[child].Field != "m_oConditions") continue;
                bool? negated = c.QueueBytes.TryGetValue(child, out var values)
                    && values.TryGetValue("m_negateDirCondition", out var bytes)
                    && bytes.Count > 0 ? bytes[0] != 0 : null;
                c.QueueInts.TryGetValue(child, out var intValues);
                int? IntField(string name) => intValues != null && intValues.TryGetValue(name, out int value) ? value : null;
                var condition = new SerializedModelCondition(c.Queue[child].Cls, negated,
                    IntField("m_comp"), IntField("m_param"), IntField("m_value"));
                hasDetail |= IsDetailCondition(condition);
                hasDirection |= condition.Class == 1996;
                allEvaluated &= IsEvaluatedModelCondition(condition);
                for (int detail = CoarseDetailLevel; detail <= FineDetailLevel; detail++)
                    if (!AllowsUncutModelCondition(condition, detail)) allowed &= ~(1 << detail);
            }
        return new ModelFilter(allowed, hasDetail, hasDirection, allEvaluated);
    }

    private static bool HasRootMappedSource(Cur c)
    {
        foreach (var instance in c.Instances)
        {
            int owner = instance.OwnerQueueIndex;
            if (owner >= 0 && owner < c.QueueLinks.Count && owner < c.Queue.Count
                && c.QueueLinks[owner].ParentQueueIndex < 0
                && (!c.ObjectGInfo.TryGetValue(c.Queue[owner].V, out var info) || IsPhysicalBodySurface(info.Flags)))
                return true;
        }
        return false;
    }

    /// <summary>Evaluate the serialized ownership path of a drawable GNode.</summary>
    private static bool IsPhysicalModelBranch(Cur c, int nodeQueueIndex, bool allowDisplaySurface = false, bool forLightingQuantity = false)
    {
        // (a display surface is taken only below filters whose every condition is one this decoder
        // evaluates)
        if (nodeQueueIndex < 0 || nodeQueueIndex >= c.Queue.Count
            || nodeQueueIndex >= c.QueueLinks.Count)
            return false;

        var node = c.Queue[nodeQueueIndex];
        if (c.ObjectGInfo.TryGetValue(node.V, out var nodeInfo)
            && !IsPhysicalBodySurface(NodeSurfaceFlags(node.Cls, c.ExactPlanarSpaceBoundary, nodeInfo.Flags)
                & (allowDisplaySurface ? ~DisplaySurfaceBit : ~0u) & (forLightingQuantity ? ~0x40000000u : ~0u)))
            return false;

        bool body = true;
        bool checkedGeometry = false;
        bool graphicalGeometryState = false;
        int ancestor = nodeQueueIndex;
        while (body && ancestor >= 0 && ancestor < c.QueueLinks.Count)
        {
            ancestor = c.QueueLinks[ancestor].ParentQueueIndex;
            if (ancestor < 0 || ancestor >= c.Queue.Count) break;
            var ancestorItem = c.Queue[ancestor];
            if (ancestorItem.Cls == 2092 && !checkedGeometry)
            {
                checkedGeometry = true;
                if (c.ObjectGInfo.TryGetValue(ancestorItem.V, out var geometryInfo))
                {
                    graphicalGeometryState = (geometryInfo.Flags & 0x000000E0u) != 0;
                    body = forLightingQuantity || IsPhysicalBodyGeometry(geometryInfo.Flags, c.IncludeNonVisible);
                }
            }
            if (ancestorItem.Cls != 2015) continue;

            var filters = c.ModelFilters ??= new();
            if (!filters.TryGetValue(ancestor, out var filter))
                filters.Add(ancestor, filter = ReadModelFilter(c, ancestor));
            c.SawDetailCondition |= filter.HasDetail;
            bool hasRootMappedSource = c.HasRootMappedSource ??= HasRootMappedSource(c);
            body = (forLightingQuantity || IsPhysicalBodyGeometryState(graphicalGeometryState, hasRootMappedSource, filter.HasDirection, filter.HasDetail))
                && filter.Allows(t_detailLevel == 0 ? FineDetailLevel : t_detailLevel)
                && (!allowDisplaySurface || filter.AllEvaluated);
        }
        return body;
    }

    /// <summary>
    /// A model Geometry node must carry control bit 0x4000 and must not be suppressed.
    /// Graphical-state ownership is evaluated separately against the node's serialized parent
    /// conditions.
    /// </summary>
    internal static bool IsPhysicalBodyGeometry(uint flags, bool includeNonVisible = false)
        => ((flags & 0x00004000u) != 0 || (includeNonVisible && IsHiddenInstanceVariant(flags)))
            && (flags & 0x40000000u) == 0;

    /// <summary>
    /// The conditions whose meaning in a 3D export is known: a detail-level GConditionInt
    /// (parameter 3) and a negated view-direction GConditionDir.
    /// </summary>
    internal static bool IsEvaluatedModelCondition(SerializedModelCondition condition)
        => condition.Class switch
        {
            1999 or 2002 => condition.Parameter == 3,
            1996 => condition.Negated == true,
            _ => false,
        };

    internal static bool IsPhysicalBodyGeometryState(
        bool graphicalState, bool hasRootMappedSource,
        IReadOnlyCollection<SerializedModelCondition> conditions)
        => IsPhysicalBodyGeometryState(graphicalState, hasRootMappedSource,
            conditions.Any(condition => condition.Class == 1996), conditions.Any(IsDetailCondition));

    private static bool IsPhysicalBodyGeometryState(bool graphicalState, bool hasRootMappedSource,
        bool hasDirectionCondition, bool hasDetailCondition)
        => !(hasDirectionCondition && (graphicalState || hasRootMappedSource))
            && !(hasDetailCondition && graphicalState);

    /// <summary>A condition of a serialized model: class, negation, comparison, parameter and value.</summary>
    internal readonly record struct SerializedModelCondition(
        ushort Class, bool? Negated, int? Comparison, int? Parameter, int? Value);

    private static bool IsDetailCondition(SerializedModelCondition condition)
        => condition.Class is 1999 or 2002 && condition.Parameter == 3;

    /// <summary>
    /// IFC is exported as an uncut three-dimensional model at Revit's Fine detail level. Revit
    /// serializes cut and detail alternatives as sibling GFilter branches.
    /// </summary>
    internal static bool AllowsFineUncutModelBranch(
        IEnumerable<SerializedModelCondition> conditions)
        => conditions.All(condition => AllowsUncutModelCondition(condition, FineDetailLevel));

    private static bool AllowsUncutModelCondition(SerializedModelCondition condition, int detail)
        => condition.Class switch
        {
            1995 => condition.Negated == true,
            1999 or 2002 when condition.Parameter == 3
                && condition.Comparison is int comparison
                && condition.Value is int value
                => CompareSerializedInteger(detail, comparison, value),
            _ => true,
        };

    /// <summary>
    /// The detail-level values a GConditionInt on parameter 3 compares against: Coarse 1, Medium 2,
    /// Fine 3.
    /// </summary>
    internal const int CoarseDetailLevel = 1, FineDetailLevel = 3;

    /// <summary>The detail level the decoding thread evaluates (0 is Fine).</summary>
    [ThreadStatic] private static int t_detailLevel;

    /// <summary>
    /// Revit CompareMode values used by GConditionInt. Unknown modes are satisfied, which is the
    /// serialized runtime contract; rejecting them would silently discard geometry.
    /// </summary>
    internal static bool CompareSerializedInteger(int actual, int comparison, int expected)
        => comparison switch
        {
            -1 or 3 => actual == expected,
            0 => actual != expected,
            1 => actual < expected,
            2 => actual <= expected,
            4 => actual >= expected,
            5 => actual > expected,
            _ => true,
        };

    /// <summary>
    /// Checks a UV-reconstructed face against the serialized representation bounds. <see
    /// cref="double.Epsilon"/> is the smallest subnormal value, not the relative precision of an
    /// IEEE-754 double.
    /// </summary>
    internal static bool IsWithinSerializedBounds(GRepFace face, double[]? bounds)
    {
        if (bounds is not { Length: >= 6 }) return true;
        const double MachineEpsilon = 2.2204460492503131e-16;
        double scale = Math.Max(1.0, bounds.Take(6).Select(Math.Abs).Max());
        double roundoff = 64 * MachineEpsilon * scale;
        return (face.Vertices ?? []).Concat((face.InnerVertices ?? []).SelectMany(x => x))
            .All(point => point.Length >= 3
                && point[0] >= bounds[0] - roundoff && point[0] <= bounds[3] + roundoff
                && point[1] >= bounds[1] - roundoff && point[1] <= bounds[4] + roundoff
                && point[2] >= bounds[2] - roundoff && point[2] <= bounds[5] + roundoff);
    }

    /// <summary>
    /// Clips a reconstructed polygon to the exact serialized representation extent. Rejecting the
    /// whole polygon when one tessellation vertex crosses m_tightbBox loses the valid portion of
    /// analytic surfaces.
    /// </summary>
    internal static GRepFace? ClipToSerializedBounds(GRepFace face, double[]? bounds)
    {
        if (bounds is not { Length: >= 6 } || IsWithinSerializedBounds(face, bounds)) return face;
        if (face.Vertices is not { Count: >= 3 } vertices
            || face.InnerVertices is { Count: > 0 }) return null;

        static List<double[]> Clip(
            List<double[]> input, int axis, double limit, bool keepGreater)
        {
            if (input.Count == 0) return input;
            bool Inside(double[] point) => keepGreater
                ? point[axis] >= limit : point[axis] <= limit;
            double[] Intersection(double[] a, double[] b)
            {
                double denominator = b[axis] - a[axis];
                double t = denominator == 0 ? 0 : (limit - a[axis]) / denominator;
                t = Math.Clamp(t, 0, 1);
                var point = new[]
                {
                    a[0] + (b[0] - a[0]) * t,
                    a[1] + (b[1] - a[1]) * t,
                    a[2] + (b[2] - a[2]) * t,
                };
                point[axis] = limit;
                return point;
            }

            var output = new List<double[]>();
            var previous = input[^1];
            bool previousInside = Inside(previous);
            foreach (var current in input)
            {
                bool currentInside = Inside(current);
                if (currentInside)
                {
                    if (!previousInside) output.Add(Intersection(previous, current));
                    output.Add(current);
                }
                else if (previousInside)
                    output.Add(Intersection(previous, current));
                previous = current;
                previousInside = currentInside;
            }
            for (int index = output.Count - 1; index >= 0 && output.Count > 1; index--)
            {
                int next = (index + 1) % output.Count;
                if (output[index].SequenceEqual(output[next])) output.RemoveAt(index);
            }
            return output;
        }

        var clipped = vertices.Select(point => (double[])point.Clone()).ToList();
        for (int axis = 0; axis < 3 && clipped.Count >= 3; axis++)
        {
            clipped = Clip(clipped, axis, bounds[axis], keepGreater: true);
            clipped = Clip(clipped, axis, bounds[axis + 3], keepGreater: false);
        }
        if (clipped.Count < 3 || NewellNormalArea(clipped).Area < 1e-12) return null;
        return face with
        {
            Vertices = clipped,
            Origin = (double[])clipped[0].Clone(),
            AlternativeOuterVertices = null,
        };
    }

    private static GRepData? PolyMeshGeometry(Cur c)
    {
        var geometry = new GRepData { Faces = new List<GRepFace>() };
        if (AppendPolyMeshes(geometry, c) == 0) return null;

        var authoritativeBounds = ValidBox(c.RootTightBoundingBox)
            ? c.RootTightBoundingBox : c.RootBoundingBox;
        geometry.BoundingBox = authoritativeBounds is { Length: 6 }
            ? (double[])authoritativeBounds.Clone()
            : GeometryBounds(geometry.Faces);
        AttachInstances(geometry, c, hasCompleteResolvedBody: false);
        System.Threading.Interlocked.Increment(ref DbgWorldElems);
        System.Threading.Interlocked.Add(ref DbgWorldFaces, geometry.Faces.Count);
        return geometry;
    }

    /// <summary>
    /// Materializes exact FacetedTopology triangles owned by active GPolyMesh nodes. Selection uses
    /// the serialized GInfo model/suppression bits, just as the BRep path does for Geometry/Face
    /// nodes.
    /// </summary>
    private static int AppendPolyMeshes(GRepData target, Cur c)
    {
        const uint modelGeometryFlag = 0x00004000;
        int firstAddedFace = target.Faces.Count;
        int includedMeshes = 0;

        foreach (var mesh in c.PolyMeshes)
        {
            if (mesh.OwnerQueueIndex < 0 || mesh.OwnerQueueIndex >= c.Queue.Count) continue;
            var owner = c.Queue[mesh.OwnerQueueIndex];
            if (owner.Cls != 2038) { c.MeshDropOwner++; continue; }
            if (!IsPhysicalModelBranch(c, mesh.OwnerQueueIndex)) { c.MeshDropBranch++; continue; }
            if (c.ObjectGInfo.TryGetValue(owner.V, out var info)
                && (info.Flags & modelGeometryFlag) == 0)
            {
                c.MeshDropFlags++;
                if (MeshDiagPath != null) c.FtNotes.Add($"flags=0x{info.Flags:X8}");
                continue;
            }

            int meshFaceStart = target.Faces.Count;
            int? meshMaterial = c.QueueInts.GetValueOrDefault(mesh.OwnerQueueIndex)?.GetValueOrDefault("m_materialID") is > 0 and int materialId
                ? materialId : null;
            var edgeUse = new Dictionary<(int A, int B), int>();
            foreach (var (a, b, d) in mesh.Triangles)
            {
                if ((uint)a >= mesh.Points.Count || (uint)b >= mesh.Points.Count
                    || (uint)d >= mesh.Points.Count) continue;
                var vertices = new List<double[]>(3)
                {
                    mesh.Points[a], mesh.Points[b], mesh.Points[d],
                };
                var (normal, _) = NewellNormalArea(vertices);
                target.Faces.Add(new GRepFace
                {
                    Vertices = vertices,
                    Origin = vertices[0],
                    XVector = normal,
                    RenderStyleId = meshMaterial,
                });
                AddEdge(a, b);
                AddEdge(b, d);
                AddEdge(d, a);
            }

            void AddEdge(int a, int b)
            {
                var edge = a <= b ? (a, b) : (b, a);
                edgeUse[edge] = edgeUse.GetValueOrDefault(edge) + 1;
            }

            int meshFaceCount = target.Faces.Count - meshFaceStart;
            if (meshFaceCount == 0) continue;
            includedMeshes++;
            bool closed = edgeUse.Count > 0 && edgeUse.Values.All(count => count == 2);
            if (MeshDiagPath != null)
                c.FtNotes.Add($"drawn:points={mesh.Points.Count},facets={meshFaceCount},closed={closed}");
            if (closed)
                target.Solids.Add(new GRepSolid
                {
                    FaceIndices = Enumerable.Range(meshFaceStart, meshFaceCount).ToList(),
                    IsClosed = true,
                });
            else
                target.IsOpenSurface = true;
        }

        if (c.Debug && includedMeshes > 0)
            DiagnosticSink.WriteLine($"  V2 eid={c.ElementId} polymeshes={includedMeshes}/{c.PolyMeshes.Count} triangles={target.Faces.Count - firstAddedFace}");
        return target.Faces.Count - firstAddedFace;
    }

    private static double[]? GeometryBounds(IEnumerable<GRepFace> faces)
    {
        var points = faces.SelectMany(face => face.Vertices ?? []).ToList();
        if (points.Count == 0) return null;
        return new[]
        {
            points.Min(point => point[0]),
            points.Min(point => point[1]),
            points.Min(point => point[2]),
            points.Max(point => point[0]),
            points.Max(point => point[1]),
            points.Max(point => point[2]),
        };
    }

    private static GRepData? InstanceGeometry(Cur c)
    {
        var bounds = ValidBox(c.RootTightBoundingBox) ? c.RootTightBoundingBox : c.RootBoundingBox;
        var result = new GRepData
        {
            BoundingBox = bounds is { Length: 6 } ? (double[])bounds.Clone() : null,
        };
        AttachInstances(result, c, hasCompleteResolvedBody: false);
        return result.Instances.Count > 0 ? result : null;
    }

    private static void AttachInstances(
        GRepData target, Cur c, bool hasCompleteResolvedBody)
    {
        var physicalInstances = c.Instances
            .Where(x => IsPhysicalModelBranch(c, x.OwnerQueueIndex))
            .ToList();
        int rootMappedInstanceCount = physicalInstances.Count(x =>
            c.QueueLinks[x.OwnerQueueIndex].ParentQueueIndex < 0);
        var nodeOrders = ComputeNodeOrders(c.Queue, c.QueueLinks, c.Instances.Select(x => x.OwnerQueueIndex).ToHashSet());
        target.Instances = physicalInstances
            .Where(x => ShouldAttachMappedInstance(hasCompleteResolvedBody,
                rootMappedInstanceCount,
                c.QueueLinks[x.OwnerQueueIndex].ParentQueueIndex))
            .Select(x => new GRepInstance
            {
                SymbolElemId = x.SymbolElemId,
                Transform = (double[])x.Transform.Clone(),
                NodeOrder = nodeOrders.GetValueOrDefault(x.OwnerQueueIndex),
            }).ToList();
        if (c.Debug && target.Instances.Count != c.Instances.Count)
            DiagnosticSink.WriteLine($"  V2 eid={c.ElementId} instance-selection="
                + $"{target.Instances.Count}/{c.Instances.Count}");
        var primary = target.Instances.FirstOrDefault();
        if (primary != null)
        {
            target.SymbolElemId = primary.SymbolElemId;
            target.InstanceTransform = primary.Transform;
        }
    }

    private static double[]? World(PlaneRec pl, double u, double v)
    {
        double x = pl.O[0] + u * pl.X[0] + v * pl.Y[0];
        double y = pl.O[1] + u * pl.X[1] + v * pl.Y[1];
        double z = pl.O[2] + u * pl.X[2] + v * pl.Y[2];
        if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z)) return null;
        return new[] { x, y, z };
    }

    private static PlaneRec ToPlane(SurfaceRec s)
    {
        var pr = new PlaneRec();
        Array.Copy(s.O, pr.O, 3); Array.Copy(s.X, pr.X, 3); Array.Copy(s.Y, pr.Y, 3);
        return pr;
    }

    /// <summary>
    /// Find the face surfaces guided by the EXPECTED class sequence from the queue: for each
    /// expected class, scan forward for its byte signature. Keeps face[i] and surface[i] alignment
    /// exact and disambiguates cylinder vs cone (both 104b).
    /// </summary>
    private static List<SurfaceRec> ScanSurfacesGuided(byte[] d, int start, int end, List<ushort> expected,
        Dictionary<uint, ArcRec>? curves = null)
    {
        var res = new List<SurfaceRec>();
        int lim = Math.Min(end, d.Length);
        static Pt3 V(byte[] b, int p) => new(BitConverter.ToDouble(b, p), BitConverter.ToDouble(b, p + 8), BitConverter.ToDouble(b, p + 16));
        static bool Unit(Pt3 v) => Math.Abs(v.Norm - 1) < 1e-9;
        static bool Fin3(Pt3 v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);

        // Surface payload = base Surface(523) fields FIRST: [m_Envelope: 4xf64 uv bounds]
        // [m_orientFlag: u8 in {0,1}], then the surface's own frame.
        bool HasSurfPrefix(int p)
        {
            if (p < 33) return false;
            double uMin = BitConverter.ToDouble(d, p - 33), vMin = BitConverter.ToDouble(d, p - 25);
            double uMax = BitConverter.ToDouble(d, p - 17), vMax = BitConverter.ToDouble(d, p - 9);
            return IsFinite(uMin) && IsFinite(vMin) && IsFinite(uMax) && IsFinite(vMax)
                && d[p - 1] <= 1 && uMin < uMax && vMin < vMax;
        }

        double[] ReadEnvelope(int p) => [BitConverter.ToDouble(d, p - 33), BitConverter.ToDouble(d, p - 25),
            BitConverter.ToDouble(d, p - 17), BitConverter.ToDouble(d, p - 9)];

        bool TryPlane(int p, out SurfaceRec sr)
        {
            sr = default;
            if (p + 72 > lim || !HasSurfPrefix(p)) return false;
            var o = V(d, p); var x = V(d, p + 24); var y = V(d, p + 48);
            if (!(Fin3(o) && Fin3(x) && Fin3(y) && Unit(x) && Unit(y) && Math.Abs(x.Dot(y)) < 1e-9)) return false;
            sr = new SurfaceRec { Kind = SurfKind.Plane, O = o.ToArray(), X = x.ToArray(), Y = y.ToArray(), Env = ReadEnvelope(p), Orient = d[p - 1] };
            return true;
        }
        bool TryFrame(int p, SurfKind kind, out SurfaceRec sr)
        {
            sr = default;
            if (p + 104 > lim || !HasSurfPrefix(p)) return false;
            var o = V(d, p); var x = V(d, p + 24); var y = V(d, p + 48); var z = V(d, p + 72);
            double r = BitConverter.ToDouble(d, p + 96);
            if (!(Fin3(o) && Fin3(x) && Fin3(y) && Fin3(z) && IsFinite(r))) return false;
            if (!(Unit(x) && Unit(y) && Unit(z))) return false;
            if (Math.Abs(x.Dot(y)) > 1e-9 || Math.Abs(x.Dot(z)) > 1e-9 || Math.Abs(y.Dot(z)) > 1e-9) return false;
            if (Math.Abs(Math.Abs(z.Dot(Pt3.Cross(x, y))) - 1) > 1e-6) return false;
            if (kind == SurfKind.Cylinder && !(Math.Abs(r) > 1e-4 && Math.Abs(r) < 1e5)) return false;
            if (kind == SurfKind.Cone && !(Math.Abs(r) > 1e-4 && Math.Abs(r) < 1.56)) return false;
            sr = new SurfaceRec { Kind = kind, O = o.ToArray(), X = x.ToArray(), Y = y.ToArray(), Z = z.ToArray(), R = r, Env = ReadEnvelope(p), Orient = d[p - 1] };
            return true;
        }

        // SurfRev: same orthonormal frame as CylSurf but followed by a 6-byte pointer to its
        // profile curve instead of an f64 radius - validate via the pointer's class (GArc 1974 /
        // GLine 1736).
        bool TryRev(int p, out SurfaceRec sr)
        {
            sr = default;
            if (p + 102 > lim || !HasSurfPrefix(p)) return false;
            var o = V(d, p); var x = V(d, p + 24); var y = V(d, p + 48); var z = V(d, p + 72);
            if (!(Fin3(o) && Fin3(x) && Fin3(y) && Fin3(z))) return false;
            if (!(Unit(x) && Unit(y) && Unit(z))) return false;
            if (Math.Abs(x.Dot(y)) > 1e-9 || Math.Abs(x.Dot(z)) > 1e-9 || Math.Abs(y.Dot(z)) > 1e-9) return false;
            uint pv = BitConverter.ToUInt32(d, p + 96);
            ushort pcls = BitConverter.ToUInt16(d, p + 100);
            if (pcls != 1974 && pcls != 1736) return false;
            sr = new SurfaceRec { Kind = SurfKind.Rev, O = o.ToArray(), X = x.ToArray(), Y = y.ToArray(), Z = z.ToArray(), Env = ReadEnvelope(p), ProfileHandle = pv, Orient = d[p - 1] };
            return true;
        }
        bool FindArcAfter(int p, SurfaceRec rev, out ArcRec arc)
        {
            arc = default;
            var axis = Pt3.From(rev.Z);
            int stop = Math.Min(lim - 112, p + 8192);
            for (int q = p; q < stop; q++)
            {
                var x = V(d, q); var y = V(d, q + 24);
                if (!(Fin3(x) && Fin3(y) && Unit(x) && Unit(y) && Math.Abs(x.Dot(y)) < 1e-9)) continue;
                double r = BitConverter.ToDouble(d, q + 48);
                var cc = V(d, q + 56);
                if (!(IsFinite(r) && Math.Abs(r) > 1e-6 && Math.Abs(r) < 1e4 && Fin3(cc))) continue;
                // Meridian plane: the arc plane's normal must be perpendicular the revolution axis.
                if (Math.Abs(Pt3.Cross(x, y).Dot(axis)) > 0.05) continue;
                arc = new ArcRec { R = r, X = x.ToArray(), Y = y.ToArray(), C = cc.ToArray() };
                arc.T0 = BitConverter.ToDouble(d, q - 16); arc.T1 = BitConverter.ToDouble(d, q - 8);
                return true;
            }
            return false;
        }

        int pos = Math.Max(0, start);
        int foundCount = 0;
        foreach (var cls in expected)
        {
            var kind = cls switch { 983 => SurfKind.Cylinder, 763 => SurfKind.Cone, 522 => SurfKind.Plane, 3881 => SurfKind.Rev, _ => SurfKind.Unknown };
            if (kind == SurfKind.Unknown) { res.Add(new SurfaceRec { Kind = SurfKind.Unknown }); continue; }
            bool found = false;
            for (int p = pos; p < lim; p++)
            {
                SurfaceRec sr;
                bool hit = kind == SurfKind.Plane ? TryPlane(p, out sr)
                         : kind == SurfKind.Rev ? TryRev(p, out sr)
                         : TryFrame(p, kind, out sr);
                if (hit)
                {
                    // A Rev surface needs its profile arc: try to locate the GArc payload further
                    // in the stream (BuildGeometry skips the face if none found).
                    if (kind == SurfKind.Rev && curves != null && !curves.ContainsKey(sr.ProfileHandle)
                        && FindArcAfter(p + 102, sr, out var arc))
                        curves[sr.ProfileHandle] = arc;
                    res.Add(sr);
                    pos = p + (kind == SurfKind.Plane ? 72 : kind == SurfKind.Rev ? 102 : 104);
                    found = true;
                    foundCount++;
                    break;
                }
            }
            if (!found) res.Add(new SurfaceRec { Kind = SurfKind.Unknown });
        }

        // Fallback: some blocks (e.g. slabs whose surfaces sit in a packed tail region) store
        // surfaces WITHOUT the standard [Envelope][orient] prefix - rescan without the prefix
        // requirement so their planar geometry isn't lost entirely.
        if (foundCount == 0 && expected.Count > 0)
        {
            res.Clear();
            for (int p = Math.Max(0, start); p + 72 <= lim && res.Count < expected.Count;)
            {
                var o = V(d, p); var x = V(d, p + 24); var y = V(d, p + 48);
                if (Fin3(o) && Fin3(x) && Fin3(y) && Unit(x) && Unit(y) && Math.Abs(x.Dot(y)) < 1e-9)
                {
                    res.Add(new SurfaceRec { Kind = SurfKind.Plane, O = o.ToArray(), X = x.ToArray(), Y = y.ToArray() });
                    p += 72;
                    continue;
                }
                p++;
            }
            while (res.Count < expected.Count) res.Add(new SurfaceRec { Kind = SurfKind.Unknown });
        }
        return res;
    }

    /// <summary>
    /// Tessellate a cylindrical face into quads. The edge UVs are (u=angle rad, v=axial ft): P =
    /// center + R*(cos u*X + sin u*Y) + v*Z. Sample the u-range spanned by the face's edges into
    /// steps, at the two v extremes.
    /// </summary>
    private static IEnumerable<List<double[]>> TessellateHermiteSurface(SurfaceRec s,
        List<(double U1, double V1, double U2, double V2)> edges)
    {
        if (s.HermiteNodes is not { Count: > 0 } nodes
            || s.UParams is not { Length: >= 2 } uKnots
            || s.VParams is not { Length: >= 2 } vKnots
            || nodes.Count != uKnots.Length * vKnots.Length) yield break;

        double uMin, vMin, uMax, vMax;
        if (!TryEdgeParameterBounds(edges, out uMin, out vMin, out uMax, out vMax))
        {
            uMin = s.Env is { Length: 4 } env ? env[0] : uKnots[0];
            vMin = s.Env is { Length: 4 } ? s.Env[1] : vKnots[0];
            uMax = s.Env is { Length: 4 } ? s.Env[2] : uKnots[^1];
            vMax = s.Env is { Length: 4 } ? s.Env[3] : vKnots[^1];
        }
        if (uMax <= uMin || vMax <= vMin) yield break;

        var uSamples = ParameterSamples(edges, true, uMin, uMax);
        var vSamples = ParameterSamples(edges, false, vMin, vMax);
        uSamples.AddRange(uKnots.Where(u => u > uMin && u < uMax));
        vSamples.AddRange(vKnots.Where(v => v > vMin && v < vMax));
        static void SortUnique(List<double> values)
        {
            values.Sort();
            double span = values.Count > 1 ? Math.Abs(values[^1] - values[0]) : 0;
            double eps = Math.Max(1e-12, span * 1e-12);
            for (int i = values.Count - 1; i > 0; i--)
                if (values[i] - values[i - 1] <= eps) values.RemoveAt(i);
        }
        SortUnique(uSamples); SortUnique(vSamples);
        // Every trim vertex becomes a grid line in both directions, so a densely trimmed surface (a
        // bent pipe fitting) would make a mesh of the product of the two counts: millions of quads
        // and tens of gigabytes for one section.
        int MaxHermiteGridLines = t_coarseTessellation ? 24 : 200;
        uSamples = ThinSamples(uSamples, uKnots, MaxHermiteGridLines);
        vSamples = ThinSamples(vSamples, vKnots, MaxHermiteGridLines);
        if (uSamples.Count < 2 || vSamples.Count < 2) yield break;

        static Pt3 Hermite(Pt3 p0, Pt3 p1, Pt3 d0, Pt3 d1,
            double t, double parameterSpan)
        {
            double t2 = t * t, t3 = t2 * t;
            double h00 = 2 * t3 - 3 * t2 + 1;
            double h10 = t3 - 2 * t2 + t;
            double h01 = -2 * t3 + 3 * t2;
            double h11 = t3 - t2;
            return new Pt3(
                h00 * p0.X + h10 * parameterSpan * d0.X + h01 * p1.X + h11 * parameterSpan * d1.X,
                h00 * p0.Y + h10 * parameterSpan * d0.Y + h01 * p1.Y + h11 * parameterSpan * d1.Y,
                h00 * p0.Z + h10 * parameterSpan * d0.Z + h01 * p1.Z + h11 * parameterSpan * d1.Z);
        }
        Pt3 Eval(double u, double v)
        {
            int ui = Array.BinarySearch(uKnots, u);
            if (ui < 0) ui = ~ui - 1;
            ui = Math.Clamp(ui, 0, uKnots.Length - 2);
            int vi = Array.BinarySearch(vKnots, v);
            if (vi < 0) vi = ~vi - 1;
            vi = Math.Clamp(vi, 0, vKnots.Length - 2);
            double du = uKnots[ui + 1] - uKnots[ui];
            double dv = vKnots[vi + 1] - vKnots[vi];
            double tu = Math.Abs(du) < 1e-15 ? 0 : (u - uKnots[ui]) / du;
            double tv = Math.Abs(dv) < 1e-15 ? 0 : (v - vKnots[vi]) / dv;
            var n00 = nodes[vi * uKnots.Length + ui];
            var n10 = nodes[vi * uKnots.Length + ui + 1];
            var n01 = nodes[(vi + 1) * uKnots.Length + ui];
            var n11 = nodes[(vi + 1) * uKnots.Length + ui + 1];
            var p0 = Hermite(Pt3.From(n00.Point), Pt3.From(n10.Point), Pt3.From(n00.Du), Pt3.From(n10.Du), tu, du);
            var p1 = Hermite(Pt3.From(n01.Point), Pt3.From(n11.Point), Pt3.From(n01.Du), Pt3.From(n11.Du), tu, du);
            var dv0 = Hermite(Pt3.From(n00.Dv), Pt3.From(n10.Dv), Pt3.From(n00.Duv), Pt3.From(n10.Duv), tu, du);
            var dv1 = Hermite(Pt3.From(n01.Dv), Pt3.From(n11.Dv), Pt3.From(n01.Duv), Pt3.From(n11.Duv), tu, du);
            return Hermite(p0, p1, dv0, dv1, tv, dv);
        }

        static bool ValidPoint(Pt3 point) => IsFinite(point.X) && IsFinite(point.Y) && IsFinite(point.Z);

        // Reuse sampled coordinates across neighboring cells. Each face still owns its vertices.
        var currentRow = new Pt3[vSamples.Count];
        var nextRow = new Pt3[vSamples.Count];
        for (int j = 0; j < vSamples.Count; j++) currentRow[j] = Eval(uSamples[0], vSamples[j]);
        for (int i = 0; i + 1 < uSamples.Count; i++)
        {
            for (int j = 0; j < vSamples.Count; j++) nextRow[j] = Eval(uSamples[i + 1], vSamples[j]);
            for (int j = 0; j + 1 < vSamples.Count; j++)
            {
                var a = currentRow[j];
                var b = nextRow[j];
                var c = nextRow[j + 1];
                var d = currentRow[j + 1];
                if (ValidPoint(a) && ValidPoint(b) && ValidPoint(c) && ValidPoint(d))
                    yield return [a.ToArray(), b.ToArray(), c.ToArray(), d.ToArray()];
            }
            (currentRow, nextRow) = (nextRow, currentRow);
        }
    }

    /// <summary>
    /// Samples as they are when there are at most <paramref name="maxCount"/> of them; otherwise
    /// the first and last, every knot inside and evenly spaced picks of the others up to the limit.
    /// </summary>
    internal static List<double> ThinSamples(List<double> samples, double[] knots, int maxCount)
    {
        if (samples.Count <= maxCount) return samples;
        double first = samples[0], last = samples[^1];
        var kept = new SortedSet<double> { first, last };
        foreach (double knot in knots)
            if (knot > first && knot < last) kept.Add(knot);
        int others = Math.Max(0, maxCount - kept.Count);
        for (int k = 1; k <= others; k++)
            kept.Add(samples[(int)((long)k * (samples.Count - 1) / (others + 1))]);
        return kept.ToList();
    }

    private static IEnumerable<List<double[]>> TessellateCylinder(SurfaceRec s,
        List<(double U1, double V1, double U2, double V2)> edges,
        List<(double U1, double V1, double U2, double V2)>? samplingEdges = null,
        IReadOnlyList<(double[] First, double[] Second)>? worldBoundary = null)
    {
        bool hasAuthoredTrim = edges.Count >= 3;
        edges = NormalizePeriodicEdges(edges, 2 * Math.PI);
        samplingEdges = samplingEdges == null
            ? edges : NormalizePeriodicEdges(samplingEdges, 2 * Math.PI);
        if (worldBoundary?.Count != samplingEdges.Count) worldBoundary = null;
        double uMin, uMax, vMin, vMax;
        if (!TryEdgeParameterBounds(edges, out uMin, out vMin, out uMax, out vMax))
        {
            if (s.Env is not { Length: 4 } env || env[2] - env[0] >= 7 || env[3] - env[1] >= 1e5)
                yield break;
            uMin = env[0]; vMin = env[1]; uMax = env[2]; vMax = env[3];
        }
        if (uMin > uMax || vMin > vMax) yield break;
        double du = uMax - uMin, dv = vMax - vMin;
        if (Math.Abs(s.R) < 1e-6 || (du < 1e-6 && dv < 1e-6)) yield break;
        double maxRadius = s.Kind == SurfKind.Cone
            ? Math.Max(Math.Abs(vMin * Math.Sin(s.R)), Math.Abs(vMax * Math.Sin(s.R)))
            : Math.Abs(s.R);
        var uSamples = ParameterSamples(samplingEdges, useU: true, uMin, uMax);
        var vSamples = ParameterSamples(samplingEdges, useU: false, vMin, vMax);
        if (!hasAuthoredTrim)
        {
            uSamples = RefineAngularSamples(uSamples, maxRadius);
            AddAngularCoordinateExtrema(uSamples, uMin, uMax, s.X, s.Y);
        }
        double[] P(double u, double v)
        {
            double radial = s.Kind == SurfKind.Cone ? v * Math.Sin(s.R) : s.R;
            double axial = s.Kind == SurfKind.Cone ? v * Math.Cos(s.R) : v;
            return new[]
            {
                s.O[0] + radial * (Math.Cos(u) * s.X[0] + Math.Sin(u) * s.Y[0]) + axial * s.Z[0],
                s.O[1] + radial * (Math.Cos(u) * s.X[1] + Math.Sin(u) * s.Y[1]) + axial * s.Z[1],
                s.O[2] + radial * (Math.Cos(u) * s.X[2] + Math.Sin(u) * s.Y[2]) + axial * s.Z[2],
            };
        }
        var trimPoint = TrimBoundaryEvaluator(samplingEdges, P, worldBoundary);
        var trimmed = TessellateTrimmedGrid(
            edges, uSamples, vSamples, trimPoint, refineV: false);
        if (trimmed != null)
        {
            foreach (var polygon in trimmed) yield return polygon;
            yield break;
        }
        for (int i = 0; i + 1 < uSamples.Count; i++)
        {
            double ua = uSamples[i], ub = uSamples[i + 1];
            for (int j = 0; j + 1 < vSamples.Count; j++)
            {
                double va = vSamples[j], vb = vSamples[j + 1];
                var quad = new List<double[]> { P(ua, va), P(ub, va), P(ub, vb), P(ua, vb) };
                bool ok = true;
                foreach (var q in quad) if (!IsFinite(q[0]) || !IsFinite(q[1]) || !IsFinite(q[2])) { ok = false; break; }
                if (ok) yield return quad;
            }
        }
    }

    /// <summary>
    /// Tessellate a surface of revolution: the profile arc (world-space circle segment) revolved
    /// around the axis (surface O, Z). u = revolution angle, v = profile arc parameter (radians).
    /// Pipe elbows are the common case (torus patch).
    /// </summary>
    private static IEnumerable<List<double[]>> TessellateRevolution(SurfaceRec s, ArcRec arc,
        List<(double U1, double V1, double U2, double V2)> edges,
        List<(double U1, double V1, double U2, double V2)>? samplingEdges = null)
    {
        bool hasAuthoredTrim = edges.Count >= 3;
        edges = NormalizePeriodicEdges(edges, 2 * Math.PI);
        edges = CloseRevolutionPoleEdges(edges, arc);
        samplingEdges = samplingEdges == null
            ? edges : CloseRevolutionPoleEdges(
                NormalizePeriodicEdges(samplingEdges, 2 * Math.PI), arc);
        double uMin, uMax, vMin, vMax;
        if (!TryEdgeParameterBounds(edges, out uMin, out vMin, out uMax, out vMax))
        {
            if (s.Env is not { Length: 4 } env || env[2] - env[0] >= 7 || env[3] - env[1] >= 7)
                yield break;
            uMin = env[0]; vMin = env[1]; uMax = env[2]; vMax = env[3];
        }
        if (uMin > uMax || vMin > vMax) yield break;
        double du = uMax - uMin, dv = vMax - vMin;
        if (du < 1e-9 && dv < 1e-9) yield break;
        // Revit stores the profile in the revolved surface's local xz plane. Its documented
        // parameterization is S(u,v) = O + C1(v)cos(u)X + C1(v)sin(u)Y + C2(v)Z.
        double[] Point(double u, double v)
        {
            double[] profile = arc.Eval(v);
            double radial = profile[0];
            double axial = profile[2];
            return new[]
            {
                s.O[0] + radial * Math.Cos(u) * s.X[0] + radial * Math.Sin(u) * s.Y[0] + axial * s.Z[0],
                s.O[1] + radial * Math.Cos(u) * s.X[1] + radial * Math.Sin(u) * s.Y[1] + axial * s.Z[1],
                s.O[2] + radial * Math.Cos(u) * s.X[2] + radial * Math.Sin(u) * s.Y[2] + axial * s.Z[2],
            };
        }
        List<double> uSamples;
        List<double> vSamples;
        if (arc.NurbControlPoints is { Count: > 0 })
        {
            (uSamples, vSamples) = RevolutionInteriorParameterSamples(
                arc, uMin, uMax, vMin, vMax);
            if (!hasAuthoredTrim)
                AddAngularCoordinateExtrema(uSamples, uMin, uMax, s.X, s.Y);
        }
        else
        {
            uSamples = ParameterSamples(samplingEdges, useU: true, uMin, uMax);
            vSamples = ParameterSamples(samplingEdges, useU: false, vMin, vMax);
            if (!hasAuthoredTrim && arc.HermiteNodes is { Count: > 0 } profileNodes)
            {
                double eps = Math.Max(1e-10, Math.Abs(dv) * 1e-10);
                foreach (var node in profileNodes)
                    if (node.T >= vMin - eps && node.T <= vMax + eps)
                        vSamples.Add(Math.Clamp(node.T, vMin, vMax));
                vSamples.Sort();
                for (int i = vSamples.Count - 1; i > 0; i--)
                    if (vSamples[i] - vSamples[i - 1] <= eps) vSamples.RemoveAt(i);
            }
            else if (!hasAuthoredTrim && Math.Abs(arc.R) > 1e-12)
            {
                double ry = arc.RY == 0 ? arc.R : arc.RY;
                AddAngularCoordinateExtrema(vSamples, vMin, vMax,
                    arc.X.Select(value => value * arc.R).ToArray(),
                    arc.Y.Select(value => value * ry).ToArray());
            }
            double maxRevolutionRadius = 0;
            foreach (double v in vSamples)
                maxRevolutionRadius = Math.Max(
                    maxRevolutionRadius, Math.Abs(arc.Eval(v)[0]));
            if (!hasAuthoredTrim)
            {
                uSamples = RefineAngularSamples(uSamples, maxRevolutionRadius);
                AddAngularCoordinateExtrema(uSamples, uMin, uMax, s.X, s.Y);
                if (vSamples.Count == 2 && Math.Abs(arc.R) > 1e-12)
                    vSamples = RefineAngularSamples(vSamples,
                        Math.Max(Math.Abs(arc.R), Math.Abs(arc.RY)));
            }
        }
        var trimPoint = TrimBoundaryEvaluator(samplingEdges, Point);
        var trimmed = TessellateTrimmedGrid(
            edges, uSamples, vSamples, trimPoint, refineV: true);
        if (trimmed != null)
        {
            foreach (var polygon in trimmed) yield return polygon;
            yield break;
        }
        for (int i = 0; i + 1 < uSamples.Count; i++)
        {
            double ua = uSamples[i], ub = uSamples[i + 1];
            for (int j = 0; j + 1 < vSamples.Count; j++)
            {
                double va = vSamples[j], vb = vSamples[j + 1];
                var quad = new List<double[]> { Point(ua, va), Point(ua, vb), Point(ub, vb), Point(ub, va) };
                bool ok = true;
                foreach (var q in quad) if (!IsFinite(q[0]) || !IsFinite(q[1]) || !IsFinite(q[2])) { ok = false; break; }
                if (ok) yield return quad;
            }
        }
    }

    private static List<double> ParameterSamples(
        List<(double U1, double V1, double U2, double V2)> edges,
        bool useU, double min, double max)
    {
        double eps = Math.Max(1e-10, Math.Abs(max - min) * 1e-10);
        var values = new List<double> { min, max };
        foreach (var edge in edges)
        {
            double a = useU ? edge.U1 : edge.V1;
            double b = useU ? edge.U2 : edge.V2;
            if (IsFinite(a) && a >= min - eps && a <= max + eps) values.Add(Math.Clamp(a, min, max));
            if (IsFinite(b) && b >= min - eps && b <= max + eps) values.Add(Math.Clamp(b, min, max));
        }
        values.Sort();
        var result = new List<double>();
        foreach (double value in values)
            if (result.Count == 0 || value - result[^1] > eps)
                result.Add(value);
        return result;
    }

    /// <summary>
    /// Builds an interior parameter grid for a serialized curve. Boundary samples stay in the trim
    /// polygon itself; promoting every boundary coordinate to a full grid line creates an O(n^2)
    /// mesh for dense NURBS trims.
    /// </summary>
    private static List<double> CurveInteriorParameterSamples(
        ArcRec curve, double min, double max)
    {
        if (!IsFinite(min) || !IsFinite(max) || max <= min) return [min, max];
        var samples = new List<double> { min, max };
        if (curve.NurbKnots is { Length: > 0 } knots)
            samples.AddRange(knots.Where(value => value > min && value < max));
        if (curve.HermiteNodes is { Count: > 0 } nodes)
            samples.AddRange(nodes.Select(node => node.T)
                .Where(value => value > min && value < max));
        samples.Sort();
        double duplicateEpsilon = Math.Max(1e-12, (max - min) * 1e-12);
        for (int index = samples.Count - 1; index > 0; index--)
            if (samples[index] - samples[index - 1] <= duplicateEpsilon)
                samples.RemoveAt(index);

        if (curve.NurbControlPoints is not { Count: > 0 }
            && curve.HermiteNodes is not { Count: > 0 })
        {
            if (Math.Abs(curve.R) > 1e-12)
                return RefineAngularSamples(samples,
                    Math.Max(Math.Abs(curve.R), Math.Abs(curve.RY)));
            return samples;
        }

        double ChordError(double first, double second)
        {
            var start = curve.Eval(first);
            var end = curve.Eval(second);
            double error = 0;
            foreach (double fraction in new[] { 0.25, 0.5, 0.75 })
            {
                var actual = curve.Eval(first + (second - first) * fraction);
                double squared = 0;
                for (int component = 0; component < 3; component++)
                {
                    double chord = start[component]
                        + (end[component] - start[component]) * fraction;
                    double delta = actual[component] - chord;
                    squared += delta * delta;
                }
                error = Math.Max(error, Math.Sqrt(squared));
            }
            return error;
        }

        var source = samples.ToArray();
        var refined = new List<double> { source[0] };
        void Append(double first, double second)
        {
            double middle = (first + second) / 2;
            if (middle == first || middle == second
                || ChordError(first, second) <= ChordTolerance)
            {
                refined.Add(second);
                return;
            }
            Append(first, middle);
            Append(middle, second);
        }
        for (int index = 0; index + 1 < source.Length; index++)
            Append(source[index], source[index + 1]);
        return refined;
    }

    private static (List<double> U, List<double> V) RevolutionInteriorParameterSamples(
        ArcRec profile, double uMin, double uMax, double vMin, double vMax)
    {
        var vSamples = CurveInteriorParameterSamples(profile, vMin, vMax);
        double radius = vSamples.Select(value => Math.Abs(profile.Eval(value)[0]))
            .Where(IsFinite).DefaultIfEmpty(0).Max();
        var uSamples = RefineAngularSamples([uMin, uMax], radius);
        return (uSamples, vSamples);
    }

    /// <summary>
    /// Convert the angular coordinate of a periodic surface to one continuous parameter interval.
    /// </summary>
    internal static List<(double U1, double V1, double U2, double V2)> NormalizePeriodicEdges(
        List<(double U1, double V1, double U2, double V2)> edges, double period)
    {
        if (edges.Count == 0 || !IsFinite(period) || period <= 0) return edges;
        edges = edges.Where(edge => IsFinite(edge.U1) && IsFinite(edge.V1)
            && IsFinite(edge.U2) && IsFinite(edge.V2)).ToList();
        if (edges.Count == 0) return edges;

        const double keyResolution = 1e-9;
        static long Key(double value) => (long)Math.Round(value / keyResolution);
        var nodeByKey = new Dictionary<(long Cos, long Sin, long V), int>();
        var raw = new List<(double U, double V)>();
        int Node(double u, double v)
        {
            double angle = u * (2 * Math.PI / period);
            var key = (Key(Math.Cos(angle)), Key(Math.Sin(angle)), Key(v));
            if (nodeByKey.TryGetValue(key, out int index)) return index;
            index = raw.Count;
            nodeByKey[key] = index;
            raw.Add((u, v));
            return index;
        }

        var graphEdges = edges.Select(edge =>
            (A: Node(edge.U1, edge.V1), B: Node(edge.U2, edge.V2))).ToList();
        var adjacent = Enumerable.Range(0, raw.Count)
            .Select(_ => new List<(int Edge, int Other)>()).ToArray();
        for (int edgeIndex = 0; edgeIndex < graphEdges.Count; edgeIndex++)
        {
            var edge = graphEdges[edgeIndex];
            adjacent[edge.A].Add((edgeIndex, edge.B));
            adjacent[edge.B].Add((edgeIndex, edge.A));
        }

        var unwrapped = Enumerable.Repeat(double.NaN, raw.Count).ToArray();
        for (int seed = 0; seed < raw.Count; seed++)
        {
            if (!double.IsNaN(unwrapped[seed])) continue;
            unwrapped[seed] = raw[seed].U;
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (var (_, other) in adjacent[current])
                {
                    if (!double.IsNaN(unwrapped[other])) continue;
                    double delta = raw[other].U - raw[current].U;
                    delta -= Math.Round(delta / period) * period;
                    unwrapped[other] = unwrapped[current] + delta;
                    queue.Enqueue(other);
                }
            }
        }

        var result = new List<(double U1, double V1, double U2, double V2)>(edges.Count);
        for (int edgeIndex = 0; edgeIndex < graphEdges.Count; edgeIndex++)
        {
            var edge = graphEdges[edgeIndex];
            result.Add((unwrapped[edge.A], raw[edge.A].V,
                unwrapped[edge.B], raw[edge.B].V));
        }
        return result;
    }

    /// <summary>
    /// Close a trim loop at a surface-of-revolution pole. At a profile parameter whose radial
    /// coordinate is zero, every angular parameter maps to the same 3D point and RVT omits the
    /// degenerate boundary edge.
    /// </summary>
    internal static List<(double U1, double V1, double U2, double V2)> CloseRevolutionPoleEdges(
        List<(double U1, double V1, double U2, double V2)> edges, ArcRec profile)
    {
        if (edges.Count < 2) return edges;
        const double endpointResolution = 1e-4;
        static (long U, long V) Key(double u, double v) =>
            ((long)Math.Round(u / endpointResolution), (long)Math.Round(v / endpointResolution));

        var nodes = new Dictionary<(long U, long V), (int Degree, double U, double V)>();
        void Add(double u, double v)
        {
            var key = Key(u, v);
            if (nodes.TryGetValue(key, out var node))
                nodes[key] = (node.Degree + 1, node.U, node.V);
            else
                nodes[key] = (1, u, v);
        }
        foreach (var edge in edges)
        {
            Add(edge.U1, edge.V1);
            Add(edge.U2, edge.V2);
        }

        var additions = new List<(double U1, double V1, double U2, double V2)>();
        foreach (var group in nodes.Values.Where(node => node.Degree % 2 != 0)
            .GroupBy(node => (long)Math.Round(node.V / endpointResolution)))
        {
            var ends = group.OrderBy(node => node.U).ToList();
            if (ends.Count != 2) continue;
            double v = (ends[0].V + ends[1].V) / 2;
            var point = profile.Eval(v);
            if (point.Any(value => !IsFinite(value))) continue;
            double scale = Math.Max(1, point.Max(Math.Abs));
            double roundoff = scale * 64 * Math.ScaleB(1.0, -52);
            if (Math.Abs(point[0]) > roundoff) continue;
            additions.Add((ends[0].U, ends[0].V, ends[1].U, ends[1].V));
        }
        if (additions.Count == 0) return edges;
        var result = new List<(double U1, double V1, double U2, double V2)>(edges.Count + additions.Count);
        result.AddRange(edges);
        result.AddRange(additions);
        return result;
    }

    private static bool TryEdgeParameterBounds(
        List<(double U1, double V1, double U2, double V2)> edges,
        out double uMin, out double vMin, out double uMax, out double vMax)
    {
        uMin = vMin = double.MaxValue;
        uMax = vMax = double.MinValue;
        foreach (var edge in edges)
        {
            foreach (var (u, v) in new[] { (edge.U1, edge.V1), (edge.U2, edge.V2) })
            {
                if (!IsFinite(u) || !IsFinite(v)) continue;
                uMin = Math.Min(uMin, u); uMax = Math.Max(uMax, u);
                vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
            }
        }
        return uMin <= uMax && vMin <= vMax;
    }

    /// <summary>The largest absolute value of six numbers as <c>Select(Math.Abs).Max()</c> gives it: NaN counts as the smallest value, so it is the
    /// result only when all six are NaN (then the last one).</summary>
    internal static double MaxAbsIgnoringNaN(double a, double b, double c, double d, double e, double f)
    {
        Span<double> values = [Math.Abs(a), Math.Abs(b), Math.Abs(c), Math.Abs(d), Math.Abs(e), Math.Abs(f)];
        int index = 0;
        double value = values[0];
        while (double.IsNaN(value))
        {
            if (++index == values.Length) return value;
            value = values[index];
        }
        for (index++; index < values.Length; index++)
            if (values[index] > value) value = values[index];
        return value;
    }

    internal static Func<double, double, double[]> TrimBoundaryEvaluator(
        IReadOnlyList<(double U1, double V1, double U2, double V2)> authoredEdges,
        Func<double, double, double[]> evaluate,
        IReadOnlyList<(double[] First, double[] Second)>? worldBoundary = null)
    {
        double edgeScale = 1.0;
        foreach (var edge in authoredEdges)
            edgeScale = Math.Max(edgeScale, MaxAbsIgnoringNaN(edge.U1, edge.V1, edge.U2, edge.V2, 0, 0));
        return (u, v) => EvaluateTrimBoundaryChord(u, v, authoredEdges, evaluate, worldBoundary, edgeScale);
    }

    private static double[] EvaluateTrimBoundaryChord(double u, double v,
        IReadOnlyList<(double U1, double V1, double U2, double V2)> authoredEdges,
        Func<double, double, double[]> evaluate, IReadOnlyList<(double[] First, double[] Second)>? worldBoundary,
        double edgeScale)
    {
        const double toleranceFactor = 128 * 2.2204460492503131e-16;
        double scale = Math.Max(edgeScale, Math.Max(Math.Abs(u), Math.Abs(v)));
        bool useBounds = IsFinite(scale);
        double maxTolerance = toleranceFactor * scale;
        double maxDistanceSquared = maxTolerance * maxTolerance;
        int bestEdge = -1;
        double bestDistanceSquared = double.PositiveInfinity;
        for (int edgeIndex = 0; edgeIndex < authoredEdges.Count; edgeIndex++)
        {
            var edge = authoredEdges[edgeIndex];
            double du = edge.U2 - edge.U1, dv = edge.V2 - edge.V1;
            if (useBounds && (Outside(u, edge.U1, du, maxDistanceSquared) || Outside(v, edge.V1, dv, maxDistanceSquared)))
                continue;
            double lengthSquared = du * du + dv * dv;
            if (lengthSquared == 0 || !IsFinite(lengthSquared)) continue;
            double fraction = ((u - edge.U1) * du + (v - edge.V1) * dv)
                / lengthSquared;
            double parameterScale = Math.Max(1,
                MaxAbsIgnoringNaN(u, v, edge.U1, edge.V1, edge.U2, edge.V2));
            double parameterTolerance = toleranceFactor * parameterScale;
            if (fraction < -parameterTolerance || fraction > 1 + parameterTolerance)
                continue;
            fraction = Math.Clamp(fraction, 0, 1);
            double projectedU = edge.U1 + du * fraction;
            double projectedV = edge.V1 + dv * fraction;
            double deltaU = u - projectedU, deltaV = v - projectedV;
            double distanceSquared = deltaU * deltaU + deltaV * deltaV;
            if (distanceSquared > parameterTolerance * parameterTolerance
                || distanceSquared >= bestDistanceSquared)
                continue;
            bestEdge = edgeIndex;
            bestDistanceSquared = distanceSquared;
        }
        if (bestEdge < 0) return evaluate(u, v);
        var boundary = authoredEdges[bestEdge];
        double boundaryDu = boundary.U2 - boundary.U1;
        double boundaryDv = boundary.V2 - boundary.V1;
        // Orthogonal projection mixes both rounded coordinates; two adjacent clipping cells can
        // then produce fractions separated by one floating-point bit for the same topological
        // point.
        double bestFraction = Math.Abs(boundaryDu) >= Math.Abs(boundaryDv)
            ? (u - boundary.U1) / boundaryDu
            : (v - boundary.V1) / boundaryDv;
        bestFraction = Math.Clamp(bestFraction, 0, 1);
        var first = worldBoundary == null ? evaluate(boundary.U1, boundary.V1) : worldBoundary[bestEdge].First;
        var second = worldBoundary == null ? evaluate(boundary.U2, boundary.V2) : worldBoundary[bestEdge].Second;
        return
        [
            first[0] + (second[0] - first[0]) * bestFraction,
            first[1] + (second[1] - first[1]) * bestFraction,
            first[2] + (second[2] - first[2]) * bestFraction,
        ];

        static bool Outside(double point, double start, double direction, double limitSquared)
        {
            // Use the rounded projection endpoints to bound the same arithmetic as the exact test.
            double end = start + direction;
            double distance = Math.Max(Math.Min(start, end) - point, point - Math.Max(start, end));
            return distance > 0 && distance * distance > limitSquared;
        }
    }


    /// <summary>Triangulates one exact UV outer loop with zero or more inner loops. Null when the loops do not triangulate, or
    /// when the triangulation would spend more than <paramref name="budget"/>, by default one near-linear in the vertices.</summary>
    internal static List<List<double[]>>? TriangulateTrimRings(
        IReadOnlyList<List<double[]>> sourceRings, TrimWorkBudget? budget = null)
    {
        if (sourceRings.Count == 0) return null;
        try
        {
            return TriangulateTrimRingsWithin(sourceRings,
                budget ?? TrimWorkBudget.ForVertices(sourceRings.Sum(ring => ring.Count)));
        }
        catch (TrimWorkExceeded)
        {
            return null;
        }
    }

    private static List<List<double[]>>? TriangulateTrimRingsWithin(
        IReadOnlyList<List<double[]>> sourceRings, TrimWorkBudget budget)
    {
        static bool Same(double[] first, double[] second) =>
            first[0] == second[0] && first[1] == second[1];
        decimal Orientation(double[] first, double[] second, double[] point)
        {
            budget.Spend();
            return ((decimal)second[0] - (decimal)first[0])
                    * ((decimal)point[1] - (decimal)first[1])
                - ((decimal)second[1] - (decimal)first[1])
                    * ((decimal)point[0] - (decimal)first[0]);
        }
        static decimal SignedArea(IReadOnlyList<double[]> ring)
        {
            decimal twiceArea = 0;
            for (int index = 0; index < ring.Count; index++)
            {
                var first = ring[index];
                var second = ring[(index + 1) % ring.Count];
                twiceArea += (decimal)first[0] * (decimal)second[1]
                    - (decimal)second[0] * (decimal)first[1];
            }
            return twiceArea / 2;
        }
        static List<double[]> Clean(IEnumerable<double[]> source)
        {
            var clean = new List<double[]>();
            foreach (var point in source)
                if (point.Length >= 2 && double.IsFinite(point[0])
                    && double.IsFinite(point[1])
                    && (clean.Count == 0 || !Same(clean[^1], point)))
                    clean.Add([point[0], point[1]]);
            if (clean.Count > 1 && Same(clean[0], clean[^1])) clean.RemoveAt(clean.Count - 1);
            return clean;
        }
        List<double[]> SimplifyCollinear(IReadOnlyList<double[]> source)
        {
            var clean = source.Select(point => (double[])point.Clone()).ToList();
            // Removing a vertex changes the neighbours of only the two beside it, so the scan for the next removable vertex
            // resumes at the one before (at the start after the last, the start's predecessor): the first removable vertex,
            // as a rescan from the start finds it, without the quadratic rescans.
            int index = 0;
            while (index < clean.Count && clean.Count > 3)
            {
                var previous = clean[(index - 1 + clean.Count) % clean.Count];
                var current = clean[index];
                var next = clean[(index + 1) % clean.Count];
                if (Orientation(previous, current, next) != 0
                    || (decimal)current[0] < Math.Min((decimal)previous[0], (decimal)next[0])
                    || (decimal)current[0] > Math.Max((decimal)previous[0], (decimal)next[0])
                    || (decimal)current[1] < Math.Min((decimal)previous[1], (decimal)next[1])
                    || (decimal)current[1] > Math.Max((decimal)previous[1], (decimal)next[1]))
                {
                    index++;
                    continue;
                }
                bool last = index == clean.Count - 1;
                clean.RemoveAt(index);
                index = last ? 0 : Math.Max(0, index - 1);
            }
            return clean;
        }
        bool OnSegment(double[] point, double[] first, double[] second) =>
            Orientation(first, second, point) == 0
            && (decimal)point[0] >= Math.Min((decimal)first[0], (decimal)second[0])
            && (decimal)point[0] <= Math.Max((decimal)first[0], (decimal)second[0])
            && (decimal)point[1] >= Math.Min((decimal)first[1], (decimal)second[1])
            && (decimal)point[1] <= Math.Max((decimal)first[1], (decimal)second[1]);
        bool Intersects(double[] firstA, double[] secondA,
            double[] firstB, double[] secondB)
        {
            decimal ab1 = Orientation(firstA, secondA, firstB);
            decimal ab2 = Orientation(firstA, secondA, secondB);
            decimal ba1 = Orientation(firstB, secondB, firstA);
            decimal ba2 = Orientation(firstB, secondB, secondA);
            if (ab1 == 0 && OnSegment(firstB, firstA, secondA)) return true;
            if (ab2 == 0 && OnSegment(secondB, firstA, secondA)) return true;
            if (ba1 == 0 && OnSegment(firstA, firstB, secondB)) return true;
            if (ba2 == 0 && OnSegment(secondA, firstB, secondB)) return true;
            return (ab1 < 0) != (ab2 < 0) && (ba1 < 0) != (ba2 < 0);
        }

        var ringData = sourceRings.Select(Clean)
            .Select(original => (Original: original,
                Simplified: SimplifyCollinear(original)))
            .Where(ring => ring.Simplified.Count >= 3
                && SignedArea(ring.Simplified) != 0)
            .OrderByDescending(ring => decimal.Abs(SignedArea(ring.Simplified))).ToList();
        if (ringData.Count == 0) return null;
        var outerData = ringData[0];
        var outer = outerData.Simplified;
        if (SignedArea(outer) < 0)
        {
            outer.Reverse();
            outerData.Original.Reverse();
        }
        var outerBands = new TrimEdgeBands([outer], budget, Orientation, OnSegment);
        var holeData = ringData.Skip(1)
            .Where(hole => outerBands.RingsStrictlyContaining(hole.Simplified[0]).Count > 0).ToList();
        foreach (var hole in holeData)
            if (SignedArea(hole.Simplified) > 0)
            {
                hole.Simplified.Reverse();
                hole.Original.Reverse();
            }
        var holes = holeData.Select(hole => hole.Simplified).ToList();

        var allBoundaryRings = new[] { outer }.Concat(holes).ToList();
        // Inside the outer ring (0) and inside no hole.
        var regionBands = new TrimEdgeBands(allBoundaryRings, budget, Orientation, OnSegment);
        bool InsideRegion(double[] point) =>
            regionBands.RingsStrictlyContaining(point) is [0];
        var bridges = new List<(double[] First, double[] Second)>();
        var polygon = outer.Select(point => (double[])point.Clone()).ToList();
        foreach (var hole in holes.OrderByDescending(ring => ring.Max(point => point[0]))
            .ThenBy(ring => ring.Min(point => point[1])))
        {
            int holeIndex = Enumerable.Range(0, hole.Count)
                .OrderByDescending(index => hole[index][0])
                .ThenBy(index => hole[index][1]).First();
            var holePoint = hole[holeIndex];
            // The nearest candidate that passes, on equal distances the first in the polygon: tried nearest first, the first
            // that passes is that one, and most holes need one or two tries instead of a test of every candidate.
            budget.Spend(polygon.Count);
            var candidates = new PriorityQueue<int, (double Distance, int Index)>(
                Enumerable.Range(0, polygon.Count)
                    .Where(candidateIndex => !Same(polygon[candidateIndex], holePoint))
                    .Select(candidateIndex =>
                    {
                        double dx = polygon[candidateIndex][0] - holePoint[0];
                        double dy = polygon[candidateIndex][1] - holePoint[1];
                        return (candidateIndex, (dx * dx + dy * dy, candidateIndex));
                    })
                    .Where(candidate => candidate.Item2.Item1 < double.PositiveInfinity));
            int outerIndex = -1;
            while (candidates.TryDequeue(out int candidateIndex, out _))
            {
                budget.Spend();
                var candidate = polygon[candidateIndex];
                // Only an edge that meets the vertical range of the bridge can cross it.
                bool blocked = regionBands.AnyEdge(Math.Min(holePoint[1], candidate[1]), Math.Max(holePoint[1], candidate[1]),
                    (ringIndex, edgeIndex) =>
                    {
                        var ring = allBoundaryRings[ringIndex];
                        var first = ring[edgeIndex];
                        var second = ring[(edgeIndex + 1) % ring.Count];
                        return !(Same(first, holePoint) || Same(second, holePoint)
                                || Same(first, candidate) || Same(second, candidate))
                            && Intersects(holePoint, candidate, first, second);
                    });
                if (blocked || bridges.Any(bridge => Intersects(
                        holePoint, candidate, bridge.First, bridge.Second)))
                    continue;
                var midpoint = new[] { (holePoint[0] + candidate[0]) / 2,
                    (holePoint[1] + candidate[1]) / 2 };
                if (!InsideRegion(midpoint)) continue;
                outerIndex = candidateIndex;
                break;
            }
            if (outerIndex < 0) return null;
            var outerPoint = polygon[outerIndex];
            var insertion = new List<double[]> { (double[])holePoint.Clone() };
            for (int offset = 1; offset < hole.Count; offset++)
                insertion.Add((double[])hole[(holeIndex + offset) % hole.Count].Clone());
            insertion.Add((double[])holePoint.Clone());
            insertion.Add((double[])outerPoint.Clone());
            polygon.InsertRange(outerIndex + 1, insertion);
            bridges.Add((holePoint, outerPoint));
        }

        // Ear clipping takes on each pass the first ear of the ring from its start, the lowest polygon index that remains.
        // Whether a vertex is an ear changes only when a neighbour goes, or the point that blocked its triangle goes, so only
        // those vertices are tested again, in polygon order, ahead of the vertices not tested yet: the same ears in the same
        // order as a scan of the whole ring on each pass. A grid of the remaining points finds those in a triangle.
        var triangles = new List<List<double[]>>();
        int count = polygon.Count;
        var nextOf = new int[count];
        var previousOf = new int[count];
        for (int index = 0; index < count; index++)
        {
            nextOf[index] = (index + 1) % count;
            previousOf[index] = (index - 1 + count) % count;
        }
        int start = 0;
        var remaining = new TrimPointTree(polygon, budget);
        var gone = new bool[count];
        var tests = new int[count];
        var waiting = new List<(int Vertex, int Test)>?[count];
        var retest = new PriorityQueue<int, int>();
        var queued = new bool[count];
        int untested = 0;
        void Retest(int vertex)
        {
            if (vertex >= untested || queued[vertex]) return;
            queued[vertex] = true;
            retest.Enqueue(vertex, vertex);
        }
        int NextCandidate()
        {
            while (retest.TryPeek(out int vertex, out _) && gone[vertex])
            {
                retest.Dequeue();
                queued[vertex] = false;
            }
            bool hasRetest = retest.TryPeek(out int lowest, out _);
            if (untested < polygon.Count && (!hasRetest || untested < lowest))
            {
                int vertex = untested;
                untested = nextOf[vertex] > vertex ? nextOf[vertex] : polygon.Count;
                return vertex;
            }
            if (!hasRetest) return -1;
            retest.Dequeue();
            queued[lowest] = false;
            return lowest;
        }
        bool IsEar(int index)
        {
            tests[index]++;
            int previousIndex = previousOf[index], nextIndex = nextOf[index];
            var previous = polygon[previousIndex];
            var current = polygon[index];
            var next = polygon[nextIndex];
            if (Orientation(previous, current, next) <= 0) return false;
            int blocker = remaining.FirstInTriangle(previous, current, next,
                pointIndex => pointIndex != index && pointIndex != previousIndex && pointIndex != nextIndex
                    && Orientation(previous, current, polygon[pointIndex]) > 0
                    && Orientation(current, next, polygon[pointIndex]) > 0
                    && Orientation(next, previous, polygon[pointIndex]) > 0);
            if (blocker >= 0)
            {
                (waiting[blocker] ??= new List<(int, int)>()).Add((index, tests[index]));
                return false;
            }
            var centroid = new[] { (previous[0] + current[0] + next[0]) / 3,
                (previous[1] + current[1] + next[1]) / 3 };
            return InsideRegion(centroid);
        }
        // Each iteration removes one vertex or returns; the trim budget limits the search for ears.
        while (count > 3)
        {
            int ear;
            while ((ear = NextCandidate()) >= 0 && !IsEar(ear)) { }
            if (ear < 0) return null;
            int previousIndex = previousOf[ear], nextIndex = nextOf[ear];
            triangles.Add([(double[])polygon[previousIndex].Clone(), (double[])polygon[ear].Clone(),
                (double[])polygon[nextIndex].Clone()]);
            remaining.Remove(ear);
            gone[ear] = true;
            nextOf[previousIndex] = nextIndex;
            previousOf[nextIndex] = previousIndex;
            if (ear == start) start = nextIndex;
            count--;
            Retest(previousIndex);
            Retest(nextIndex);
            if (waiting[ear] is { } blocked)
                foreach (var (vertex, test) in blocked)
                    if (!gone[vertex] && tests[vertex] == test) Retest(vertex);
        }
        if (count != 3 || Orientation(polygon[start], polygon[nextOf[start]], polygon[nextOf[nextOf[start]]]) <= 0)
            return null;
        triangles.Add([(double[])polygon[start].Clone(), (double[])polygon[nextOf[start]].Clone(),
            (double[])polygon[nextOf[nextOf[start]]].Clone()]);

        // Ear clipping operates on the topologically equivalent ring with intermediate collinear UV
        // samples removed. A grid of the original samples finds those on each edge.
        var boundaryChains = new Dictionary<(double, double, double, double), List<double[]>>();
        foreach (var ring in new[] { outerData }.Concat(holeData))
        {
            var samples = new TrimPointTree(ring.Original, budget);
            for (int index = 0; index < ring.Simplified.Count; index++)
            {
                var first = ring.Simplified[index];
                var second = ring.Simplified[(index + 1) % ring.Simplified.Count];
                var onEdge = new List<int>();
                samples.FirstInTriangle(first, second, second,
                    sample =>
                    {
                        if (OnSegment(ring.Original[sample], first, second)) onEdge.Add(sample);
                        return false;
                    });
                onEdge.Sort();
                var chain = onEdge.Select(sample => ring.Original[sample])
                    .OrderBy(point => (point[0] - first[0]) * (point[0] - first[0])
                        + (point[1] - first[1]) * (point[1] - first[1]))
                    .Select(point => (double[])point.Clone()).ToList();
                if (chain.Count < 2) continue;
                boundaryChains[(first[0], first[1], second[0], second[1])] = chain;
                boundaryChains[(second[0], second[1], first[0], first[1])] =
                    chain.AsEnumerable().Reverse()
                        .Select(point => (double[])point.Clone()).ToList();
            }
        }
        var restored = new List<List<double[]>>(triangles.Count);
        foreach (var triangle in triangles)
        {
            var polygonWithSamples = new List<double[]>();
            for (int index = 0; index < triangle.Count; index++)
            {
                var first = triangle[index];
                var second = triangle[(index + 1) % triangle.Count];
                polygonWithSamples.Add((double[])first.Clone());
                if (!boundaryChains.TryGetValue(
                        (first[0], first[1], second[0], second[1]), out var chain))
                    continue;
                for (int sample = 1; sample + 1 < chain.Count; sample++)
                    polygonWithSamples.Add((double[])chain[sample].Clone());
            }
            restored.Add(polygonWithSamples);
        }
        return restored;
    }

    /// <summary>
    /// Partitions a Face's exact UV loops into independent trim regions before triangulation. Null when a region does not
    /// triangulate, or when the triangulation of the regions would spend more than a budget near-linear in the vertices.
    /// </summary>
    internal static List<List<double[]>>? TriangulateTrimRegions(IReadOnlyList<List<double[]>> sourceRings) =>
        TriangulateTrimRegions(sourceRings, out _);

    /// <summary>
    /// Partitions a Face's exact UV loops into independent trim regions before triangulation. Null when a region does not
    /// triangulate, or when the triangulation ran out of <paramref name="budget"/> (by default one near-linear in the vertices),
    /// which <paramref name="outOfBudget"/> tells apart.
    /// </summary>
    internal static List<List<double[]>>? TriangulateTrimRegions(
        IReadOnlyList<List<double[]>> sourceRings, out bool outOfBudget, TrimWorkBudget? budget = null)
    {
        outOfBudget = false;
        static bool Same(double[] first, double[] second) =>
            first[0] == second[0] && first[1] == second[1];
        static decimal SignedArea(IReadOnlyList<double[]> ring)
        {
            decimal twiceArea = 0;
            for (int index = 0; index < ring.Count; index++)
            {
                var first = ring[index];
                var second = ring[(index + 1) % ring.Count];
                twiceArea += (decimal)first[0] * (decimal)second[1]
                    - (decimal)second[0] * (decimal)first[1];
            }
            return twiceArea / 2;
        }

        var rings = sourceRings.Select(source =>
            {
                var clean = new List<double[]>();
                foreach (var point in source)
                    if (point.Length >= 2 && double.IsFinite(point[0])
                        && double.IsFinite(point[1])
                        && (clean.Count == 0 || !Same(clean[^1], point)))
                        clean.Add([point[0], point[1]]);
                if (clean.Count > 1 && Same(clean[0], clean[^1]))
                    clean.RemoveAt(clean.Count - 1);
                return clean;
            })
            .Where(ring => ring.Count >= 3 && SignedArea(ring) != 0)
            .ToList();
        if (rings.Count == 0) return null;

        budget ??= TrimWorkBudget.ForVertices(rings.Sum(ring => ring.Count));
        decimal Orientation(double[] first, double[] second, double[] point)
        {
            budget.Spend();
            return ((decimal)second[0] - (decimal)first[0])
                    * ((decimal)point[1] - (decimal)first[1])
                - ((decimal)second[1] - (decimal)first[1])
                    * ((decimal)point[0] - (decimal)first[0]);
        }
        bool OnSegment(double[] point, double[] first, double[] second) =>
            Orientation(first, second, point) == 0
            && (decimal)point[0] >= Math.Min((decimal)first[0], (decimal)second[0])
            && (decimal)point[0] <= Math.Max((decimal)first[0], (decimal)second[0])
            && (decimal)point[1] >= Math.Min((decimal)first[1], (decimal)second[1])
            && (decimal)point[1] <= Math.Max((decimal)first[1], (decimal)second[1]);

        try
        {
            // The parent of a ring is the smallest ring that strictly contains its first point, on equal areas the first.
            var areas = rings.Select(ring => decimal.Abs(SignedArea(ring))).ToArray();
            var bands = new TrimEdgeBands(rings, budget, Orientation, OnSegment);
            var parent = Enumerable.Repeat(-1, rings.Count).ToArray();
            for (int child = 0; child < rings.Count; child++)
            {
                decimal parentArea = decimal.MaxValue;
                foreach (int candidate in bands.RingsStrictlyContaining(rings[child][0]))
                {
                    if (candidate == child) continue;
                    if (areas[candidate] < parentArea
                        || (areas[candidate] == parentArea && candidate < parent[child]))
                    {
                        parentArea = areas[candidate];
                        parent[child] = candidate;
                    }
                }
            }

            int Depth(int index)
            {
                int depth = 0;
                var seen = new HashSet<int> { index };
                while (parent[index] >= 0)
                {
                    index = parent[index];
                    if (!seen.Add(index)) return int.MaxValue;
                    depth++;
                }
                return depth;
            }

            var result = new List<List<double[]>>();
            for (int outer = 0; outer < rings.Count; outer++)
            {
                int depth = Depth(outer);
                if (depth == int.MaxValue) return null;
                if ((depth & 1) != 0) continue;
                var region = new List<List<double[]>> { rings[outer] };
                region.AddRange(Enumerable.Range(0, rings.Count)
                    .Where(index => parent[index] == outer)
                    .Select(index => rings[index]));
                var triangles = TriangulateTrimRings(region, budget);
                if (triangles == null)
                {
                    outOfBudget = budget.Exhausted;
                    return null;
                }
                result.AddRange(triangles);
            }
            return result.Count > 0 ? result : null;
        }
        catch (TrimWorkExceeded)
        {
            outOfBudget = true;
            return null;
        }
    }

    /// <summary>
    /// Tessellates the exact UV trim region while retaining the Edge-authored boundary.
    /// Surface.Envelope describes the underlying analytic surface and may include regions outside a
    /// Face; the EdgeLoop is authoritative. Null when the loops do not triangulate (the caller then draws the untrimmed grid);
    /// empty, with the face counted as untriangulated, when their triangulation ran out of its work budget.
    /// </summary>
    private static List<List<double[]>>? TessellateTrimmedGrid(
        List<(double U1, double V1, double U2, double V2)> edges,
        List<double> uSamples, List<double> vSamples,
        Func<double, double, double[]> evaluate, bool refineV)
    {
        if (edges.Count < 3 || uSamples.Count < 2 || vSamples.Count < 2) return null;
        // Every trim vertex is a grid line in both directions, so the cell count is the product of
        // the two line counts: a densely trimmed cylinder of an equipment model reaches hundreds of
        // thousands of cells and tens of gigabytes for one section.
        int MaxGridCells = t_coarseTessellation ? 1024 : 20000;
        long cells = (long)(uSamples.Count - 1) * (vSamples.Count - 1);
        if (cells > MaxGridCells)
        {
            double scale = Math.Sqrt((double)MaxGridCells / cells);
            uSamples = ThinSamples(uSamples, [], Math.Max(2, (int)((uSamples.Count - 1) * scale) + 1));
            vSamples = ThinSamples(vSamples, [], Math.Max(2, (int)((vSamples.Count - 1) * scale) + 1));
        }
        var uvSegments = edges.Select(edge =>
            (new[] { edge.U1, edge.V1, 0d }, new[] { edge.U2, edge.V2, 0d })).ToList();
        var rings = OrderRingsW(uvSegments);
        if (rings.Count == 0 || rings.Any(ring => ring.Count < 3)) return null;

        List<List<double[]>>? sourcePolygons;
        if (rings.Count == 1)
        {
            sourcePolygons =
            [
                rings[0].Select(point => new[] { point[0], point[1] }).ToList(),
            ];
        }
        else
        {
            var trimRings = rings.Select(ring => ring.Select(point =>
                new[] { point[0], point[1] }).ToList()).ToList();
            sourcePolygons = TriangulateTrimRegions(trimRings, out bool outOfBudget);
            if (outOfBudget)
            {
                // Valid loops can be adversarial for the triangulation; the face is left out and counted rather than drawn
                // as its untrimmed surface grid, which would fill its holes and pass its outer trim.
                t_untriangulatedFaces++;
                DiagnosticSink.WriteLine($"trim triangulation: a face of {trimRings.Count} loops and "
                    + $"{trimRings.Sum(ring => ring.Count)} vertices ran out of its work budget and is left out");
                return [];
            }
        }
        if (sourcePolygons == null || sourcePolygons.Count == 0) return null;

        return ClipGridCells(sourcePolygons, uSamples, vSamples, rings.Count == 1 || refineV, evaluate);
    }

    /// <summary>Scratch arrays of <see cref="ClipGridCells"/>, kept per thread so that the grid cells of a face allocate nothing
    /// but their result. A point is two doubles (u, v) one after the other.</summary>
    private sealed class GridClipBuffers
    {
        public double[] Source = [], UTemp = [], UClipped = [], VTemp = [], VClipped = [], Clean = [];
        public bool InUse;
    }

    [ThreadStatic] private static GridClipBuffers? t_gridClipBuffers;

    /// <summary>Clips every source polygon against every grid cell of the sample lines and evaluates the corners of what is left.
    /// <paramref name="allVCells"/>: the cells are cut in V as well; otherwise one cell spans all of V.
    /// The arithmetic and its order are those of the earlier per-cell implementation that built a list of arrays for every cell.</summary>
    internal static List<List<double[]>> ClipGridCells(List<List<double[]>> sourcePolygons, List<double> uSamples, List<double> vSamples,
        bool allVCells, Func<double, double, double[]> evaluate)
    {
        var buffers = t_gridClipBuffers ??= new GridClipBuffers();
        if (buffers.InUse) buffers = new GridClipBuffers(); // evaluate called back into this method on the same thread
        buffers.InUse = true;
        try
        {
            var result = new List<List<double[]>>();
            foreach (var source in sourcePolygons)
            {
                int sourceCount = source.Count;
                if (buffers.Source.Length < 2 * sourceCount) buffers.Source = new double[2 * sourceCount];
                for (int index = 0; index < sourceCount; index++)
                {
                    buffers.Source[2 * index] = source[index][0];
                    buffers.Source[2 * index + 1] = source[index][1];
                }
                for (int uIndex = 0; uIndex + 1 < uSamples.Count; uIndex++)
                {
                    int uTempCount = ClipFlat(buffers.Source, sourceCount, 0, uSamples[uIndex], true, ref buffers.UTemp);
                    int uCount = ClipFlat(buffers.UTemp, uTempCount, 0, uSamples[uIndex + 1], false, ref buffers.UClipped);
                    if (uCount == 0) continue;
                    int vCellCount = allVCells ? vSamples.Count - 1 : 1;
                    for (int vIndex = 0; vIndex < vCellCount; vIndex++)
                    {
                        double[] polygon = buffers.UClipped;
                        int count = uCount;
                        if (vCellCount > 1)
                        {
                            int vTempCount = ClipFlat(buffers.UClipped, uCount, 1, vSamples[vIndex], true, ref buffers.VTemp);
                            count = ClipFlat(buffers.VTemp, vTempCount, 1, vSamples[vIndex + 1], false, ref buffers.VClipped);
                            polygon = buffers.VClipped;
                        }
                        count = CleanFlat(polygon, count, ref buffers.Clean);
                        polygon = buffers.Clean;
                        if (count < 3 || Math.Abs(SignedAreaFlat(polygon, count)) < 1e-14) continue;
                        var world = new List<double[]>(count);
                        for (int index = 0; index < count; index++)
                            world.Add(evaluate(polygon[2 * index], polygon[2 * index + 1]));
                        bool finite = true;
                        foreach (var point in world)
                        {
                            foreach (double value in point)
                                if (!IsFinite(value)) { finite = false; break; }
                            if (!finite) break;
                        }
                        if (finite) result.Add(world);
                    }
                }
            }
            return result;
        }
        finally { buffers.InUse = false; }
    }

    /// <summary>One Sutherland-Hodgman step: keeps the part of the polygon (points from <paramref name="input"/>) on one side of the line
    /// axis = value, and writes it to <paramref name="output"/>. Returns the number of points written.</summary>
    private static int ClipFlat(double[] input, int count, int axis, double value, bool keepGreater, ref double[] output)
    {
        if (count == 0) return 0;
        if (output.Length < 4 * count) output = new double[Math.Max(4 * count, 2 * output.Length)];
        double[] target = output;
        int written = 0;
        int previousIndex = count - 1;
        double previousX = input[2 * previousIndex], previousY = input[2 * previousIndex + 1];
        bool previousInside = Inside(axis == 0 ? previousX : previousY);
        for (int index = 0; index < count; index++)
        {
            double currentX = input[2 * index], currentY = input[2 * index + 1];
            bool currentInside = Inside(axis == 0 ? currentX : currentY);
            if (currentInside)
            {
                if (!previousInside) Intersect(previousX, previousY, currentX, currentY);
                target[2 * written] = currentX; target[2 * written + 1] = currentY; written++;
            }
            else if (previousInside)
            {
                Intersect(previousX, previousY, currentX, currentY);
            }
            previousX = currentX; previousY = currentY;
            previousInside = currentInside;
        }
        return written;

        bool Inside(double coordinate) => keepGreater ? coordinate >= value - 1e-12 : coordinate <= value + 1e-12;

        void Intersect(double firstX, double firstY, double secondX, double secondY)
        {
            // Adjacent clipped cells can traverse their shared source segment in opposite
            // directions.
            double firstAxis = axis == 0 ? firstX : firstY, secondAxis = axis == 0 ? secondX : secondY;
            double firstOther = axis == 0 ? firstY : firstX, secondOther = axis == 0 ? secondY : secondX;
            if (firstAxis > secondAxis || (firstAxis == secondAxis && firstOther > secondOther))
            {
                (firstX, secondX) = (secondX, firstX);
                (firstY, secondY) = (secondY, firstY);
                (firstAxis, secondAxis) = (secondAxis, firstAxis);
            }
            double denominator = secondAxis - firstAxis;
            double fraction = Math.Abs(denominator) < 1e-20
                ? 0 : (value - firstAxis) / denominator;
            fraction = Math.Clamp(fraction, 0, 1);
            double x = firstX + (secondX - firstX) * fraction;
            double y = firstY + (secondY - firstY) * fraction;
            if (axis == 0) x = value; else y = value;
            target[2 * written] = x; target[2 * written + 1] = y; written++;
        }
    }

    /// <summary>Drops the points that repeat their predecessor within a span-relative epsilon, and the closing repeat of the first point.</summary>
    private static int CleanFlat(double[] polygon, int count, ref double[] clean)
    {
        double span = 1;
        if (count != 0)
        {
            // LINQ Max/Min over doubles: Max ignores NaN unless every value is NaN, Min returns the first NaN.
            span = Math.Max(
                MaxIgnoringNaN(polygon, count, 0) - MinPropagatingNaN(polygon, count, 0),
                MaxIgnoringNaN(polygon, count, 1) - MinPropagatingNaN(polygon, count, 1));
        }
        double epsilon = Math.Max(1e-12, span * 1e-10);
        if (clean.Length < 2 * count) clean = new double[2 * count];
        int kept = 0;
        for (int index = 0; index < count; index++)
        {
            double x = polygon[2 * index], y = polygon[2 * index + 1];
            if (kept == 0 || Math.Abs(x - clean[2 * kept - 2]) > epsilon
                || Math.Abs(y - clean[2 * kept - 1]) > epsilon)
            {
                clean[2 * kept] = x; clean[2 * kept + 1] = y; kept++;
            }
        }
        if (kept > 1 && Math.Abs(clean[0] - clean[2 * kept - 2]) <= epsilon
            && Math.Abs(clean[1] - clean[2 * kept - 1]) <= epsilon)
            kept--;
        return kept;
    }

    private static double SignedAreaFlat(double[] polygon, int count)
    {
        double area = 0;
        for (int index = 0; index < count; index++)
        {
            int next = (index + 1) % count;
            area += polygon[2 * index] * polygon[2 * next + 1] - polygon[2 * next] * polygon[2 * index + 1];
        }
        return area / 2;
    }

    /// <summary>The value <c>Enumerable.Max</c> gives for doubles: NaN counts as the smallest value, so it is the result only when every value is NaN
    /// (then the last one). <paramref name="count"/> must be above 0.</summary>
    internal static double MaxIgnoringNaN(double[] points, int count, int component)
    {
        int index = 0;
        double value = points[component];
        while (double.IsNaN(value))
        {
            if (++index == count) return value;
            value = points[2 * index + component];
        }
        for (index++; index < count; index++)
        {
            double next = points[2 * index + component];
            if (next > value) value = next;
        }
        return value;
    }

    /// <summary>The value <c>Enumerable.Min</c> gives for doubles: the first NaN if there is one. <paramref name="count"/> must be above 0.</summary>
    internal static double MinPropagatingNaN(double[] points, int count, int component)
    {
        double value = points[component];
        if (double.IsNaN(value)) return value;
        for (int index = 1; index < count; index++)
        {
            double next = points[2 * index + component];
            if (next < value) value = next;
            else if (double.IsNaN(next)) return next;
        }
        return value;
    }

    private static List<double> RefineAngularSamples(List<double> source, double radius)
    {
        if (source.Count < 2 || !IsFinite(radius) || radius <= 1e-12) return source;
        double cosine = Math.Clamp(1 - ChordTolerance / radius, -1, 1);
        double maxStep = 2 * Math.Acos(cosine);
        // Four sides are the minimum non-degenerate representation of a closed circular boundary
        // when its radius is below the requested linear resolution.
        maxStep = Math.Min(maxStep, Math.PI / 2);
        if (!IsFinite(maxStep) || maxStep <= 1e-12) return source;

        var refined = new List<double> { source[0] };
        for (int i = 0; i + 1 < source.Count; i++)
        {
            double a = source[i], b = source[i + 1];
            int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(b - a) / maxStep));
            for (int j = 1; j <= segments; j++)
                refined.Add(a + (b - a) * j / segments);
        }
        return refined;
    }

    /// <summary>
    /// Add exact parameters at which any XYZ coordinate of cos(u)*xVector + sin(u)*yVector reaches
    /// an extremum.
    /// </summary>
    private static void AddAngularCoordinateExtrema(
        List<double> samples, double min, double max,
        IReadOnlyList<double> xVector, IReadOnlyList<double> yVector)
    {
        if (samples.Count < 2 || max < min) return;
        double eps = Math.Max(1e-12, Math.Abs(max - min) * 1e-12);
        int dimensions = Math.Min(xVector.Count, yVector.Count);
        for (int coordinate = 0; coordinate < dimensions; coordinate++)
        {
            double x = xVector[coordinate], y = yVector[coordinate];
            if (!IsFinite(x) || !IsFinite(y) || Math.Abs(x) + Math.Abs(y) < 1e-15) continue;
            double first = Math.Atan2(y, x);
            long n0 = (long)Math.Ceiling((min - first - eps) / Math.PI);
            long n1 = (long)Math.Floor((max - first + eps) / Math.PI);
            if (n1 - n0 > 100_000) continue;
            for (long n = n0; n <= n1; n++)
            {
                double value = first + n * Math.PI;
                if (value >= min - eps && value <= max + eps)
                    samples.Add(Math.Clamp(value, min, max));
            }
        }
        samples.Sort();
        for (int index = samples.Count - 1; index > 0; index--)
            if (samples[index] - samples[index - 1] <= eps)
                samples.RemoveAt(index);
    }

    /// <summary>
    /// Build the deterministic curve-parameter grid shared by ruled-surface tessellation and
    /// topological boundary refinement.
    /// </summary>
    internal static List<double>? RuledParameterSamples(SurfaceRec surface,
        IReadOnlyDictionary<uint, ArcRec> curves,
        List<(double U1, double V1, double U2, double V2)> edges,
        double uMin, double uMax)
    {
        ArcRec? a1 = curves.TryGetValue(surface.ProfileHandle, out var t1) ? t1 : null;
        ArcRec? a2 = curves.TryGetValue(surface.ProfileHandle2, out var t2) ? t2 : null;
        if (a1 == null && a2 == null || uMin > uMax) return null;
        double du = uMax - uMin;
        var uSamples = ParameterSamples(edges, useU: true, uMin, uMax);

        // Ruled-surface u is normalized to each source curve interval. Convert the deterministic
        // curve grid back to that common domain.
        void AddSerializedCurveSamples(ArcRec? curve)
        {
            if (!curve.HasValue) return;
            var value = curve.Value;
            if (value.HermiteNodes is not { Count: > 0 }
                && value.NurbControlPoints is not { Count: > 0 }) return;
            double curveSpan = value.T1 - value.T0;
            if (!IsFinite(curveSpan) || Math.Abs(curveSpan) < 1e-12) return;
            double eps = Math.Max(1e-10, Math.Abs(du) * 1e-10);
            double first = value.T0 + uMin * curveSpan;
            double second = value.T0 + uMax * curveSpan;
            double parameterMin = Math.Min(first, second);
            double parameterMax = Math.Max(first, second);
            foreach (double parameter in CurveInteriorParameterSamples(
                         value, parameterMin, parameterMax))
            {
                double u = (parameter - value.T0) / curveSpan;
                if (IsFinite(u) && u >= uMin - eps && u <= uMax + eps)
                    uSamples.Add(Math.Clamp(u, uMin, uMax));
            }
        }

        AddSerializedCurveSamples(a1);
        AddSerializedCurveSamples(a2);
        uSamples.Sort();
        double sampleEps = Math.Max(1e-10, Math.Abs(du) * 1e-10);
        for (int i = uSamples.Count - 1; i > 0; i--)
            if (uSamples[i] - uSamples[i - 1] <= sampleEps)
                uSamples.RemoveAt(i);

        // Circular and elliptical curves have no serialized interior knots. In that case the
        // angular subdivision is derived from the exact curve parameter span.
        bool arcy = (a1?.R ?? 0) != 0 || (a2?.R ?? 0) != 0;
        if (uSamples.Count == 2 && arcy)
        {
            double radius = Math.Max(
                a1.HasValue ? Math.Max(Math.Abs(a1.Value.R), Math.Abs(a1.Value.RY)) : 0,
                a2.HasValue ? Math.Max(Math.Abs(a2.Value.R), Math.Abs(a2.Value.RY)) : 0);
            uSamples = RefineAngularSamples(uSamples, radius);
        }
        return uSamples;
    }

    /// <summary>
    /// Tessellate a ruled surface: straight lines sweeping from curve1 to curve2 (a missing curve
    /// degenerates to its apex point). u = curve parameter, v = ruling parameter normalized to the
    /// envelope.
    /// </summary>
    private static IEnumerable<List<double[]>> TessellateRuled(SurfaceRec s,
        Dictionary<uint, ArcRec> curves,
        List<(double U1, double V1, double U2, double V2)> edges,
        List<(double U1, double V1, double U2, double V2)>? samplingEdges = null)
    {
        ArcRec? a1 = curves.TryGetValue(s.ProfileHandle, out var t1) ? t1 : null;
        ArcRec? a2 = curves.TryGetValue(s.ProfileHandle2, out var t2) ? t2 : null;
        if (a1 == null && a2 == null) yield break;
        double uMin, uMax, vMin, vMax;
        if (!TryEdgeParameterBounds(edges, out uMin, out vMin, out uMax, out vMax))
        {
            if (s.Env is not { Length: 4 } env || env[2] - env[0] >= 1e4)
                yield break;
            uMin = env[0]; uMax = env[2]; vMin = env[1]; vMax = env[3];
        }
        if (uMin > uMax || vMax - vMin < 1e-12) yield break;
        samplingEdges ??= edges;
        var uSamples = RuledParameterSamples(s, curves, samplingEdges, uMin, uMax);
        if (uSamples == null || uSamples.Count < 2) yield break;
        static double[] EvalNormalized(ArcRec curve, double u)
            => curve.Eval(curve.T0 + u * (curve.T1 - curve.T0));
        double[] C1(double u) => a1 is { } c1 ? EvalNormalized(c1, u) : s.P1 ?? new double[3];
        double[] C2(double u) => a2 is { } c2 ? EvalNormalized(c2, u) : s.P2 ?? new double[3];
        double[] Point(double u, double v)
        {
            var p1 = C1(u); var p2 = C2(u);
            return new[]
            {
                (1 - v) * p1[0] + v * p2[0],
                (1 - v) * p1[1] + v * p2[1],
                (1 - v) * p1[2] + v * p2[2],
            };
        }
        var trimPoint = TrimBoundaryEvaluator(samplingEdges, Point);

        // Clip the frozen interior grid by the refined trim loop.
        var trimmed = TessellateTrimmedGrid(edges, uSamples,
            [vMin, vMax], trimPoint, refineV: false);
        if (trimmed != null)
        {
            foreach (var polygon in trimmed) yield return polygon;
            yield break;
        }

        for (int i = 0; i + 1 < uSamples.Count; i++)
        {
            double ua = uSamples[i], ub = uSamples[i + 1];
            var quad = new List<double[]>
            {
                trimPoint(ua, vMin), trimPoint(ub, vMin),
                trimPoint(ub, vMax), trimPoint(ua, vMax),
            };
            bool ok = true;
            foreach (var q in quad) if (!IsFinite(q[0]) || !IsFinite(q[1]) || !IsFinite(q[2])) { ok = false; break; }
            if (ok) yield return quad;
        }
    }

    internal static List<List<double[]>> OrderRingsW(
        List<(double[] A, double[] B)> edges, bool includeContainingCycles = false,
        bool preferLargestCyclePerComponent = false)
    {
        // The IFC writer uses a 0.01 mm coordinate lattice. Vertices in different lattice bins must
        // remain separate here; merging them can remove a shared boundary sample from a planar cap
        // while the adjacent analytic face retains it.
        const double Tol = 1e-5 / 0.3048;
        static (long X, long Y, long Z) Key(double[] p) =>
            ((long)Math.Round(p[0] / Tol), (long)Math.Round(p[1] / Tol), (long)Math.Round(p[2] / Tol));

        // The key of an edge set: its sorted indices joined by commas.
        static string SortedKey(HashSet<int> edgeSet)
        {
            var sorted = new int[edgeSet.Count];
            edgeSet.CopyTo(sorted);
            Array.Sort(sorted);
            return string.Join(',', (IEnumerable<int>)sorted);
        }

        // Build a geometric, undirected edge graph. The GRep can repeat the same segment through
        // several topology records; collapsing those duplicates prevents a valid loop from being
        // consumed by an arbitrary parallel copy.
        var nodeByKey = new Dictionary<(long, long, long), int>();
        var points = new List<double[]>();
        int Node(double[] p)
        {
            var key = Key(p);
            if (nodeByKey.TryGetValue(key, out int n)) return n;
            n = points.Count;
            nodeByKey[key] = n;
            points.Add(p);
            return n;
        }

        var graphEdges = new List<(int A, int B)>();
        var seenEdges = new HashSet<(int, int)>();
        foreach (var edge in edges)
        {
            int a = Node(edge.A), b = Node(edge.B);
            if (a == b) continue;
            var ek = a < b ? (a, b) : (b, a);
            if (seenEdges.Add(ek)) graphEdges.Add((a, b));
        }
        if (graphEdges.Count < 3) return new();

        // The edges of node n are adjacentEdges[adjacentStart[n] .. adjacentStart[n + 1]), in ascending edge index.
        var adjacentStart = new int[points.Count + 1];
        foreach (var graphEdge in graphEdges)
        {
            adjacentStart[graphEdge.A + 1]++;
            adjacentStart[graphEdge.B + 1]++;
        }
        for (int i = 0; i < points.Count; i++) adjacentStart[i + 1] += adjacentStart[i];
        var adjacentEdges = new int[adjacentStart[points.Count]];
        var adjacentFilled = new int[points.Count];
        for (int i = 0; i < graphEdges.Count; i++)
        {
            adjacentEdges[adjacentStart[graphEdges[i].A] + adjacentFilled[graphEdges[i].A]++] = i;
            adjacentEdges[adjacentStart[graphEdges[i].B] + adjacentFilled[graphEdges[i].B]++] = i;
        }

        // Peel open chains. They are common after a recovered stream gap, but can never participate
        // in a Face boundary. The remaining 2-core contains only cycles.
        var active = Enumerable.Repeat(true, graphEdges.Count).ToArray();
        var degree = new int[points.Count];
        for (int i = 0; i < points.Count; i++) degree[i] = adjacentStart[i + 1] - adjacentStart[i];
        var peel = new Queue<int>(Enumerable.Range(0, points.Count).Where(i => degree[i] < 2));
        while (peel.Count > 0)
        {
            int node = peel.Dequeue();
            for (int slot = adjacentStart[node]; slot < adjacentStart[node + 1]; slot++)
            {
                int edgeIndex = adjacentEdges[slot];
                if (!active[edgeIndex]) continue;
                active[edgeIndex] = false;
                var e = graphEdges[edgeIndex];
                int other = e.A == node ? e.B : e.A;
                degree[node]--;
                if (--degree[other] == 1) peel.Enqueue(other);
            }
        }

        var componentByNode = Enumerable.Repeat(-1, points.Count).ToArray();
        var branchedComponents = new List<bool>();
        for (int seed = 0; seed < points.Count; seed++)
        {
            if (degree[seed] < 2 || componentByNode[seed] >= 0) continue;
            int component = branchedComponents.Count;
            bool branched = false;
            var queue = new Queue<int>();
            componentByNode[seed] = component;
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int node = queue.Dequeue();
                branched |= degree[node] > 2;
                for (int slot = adjacentStart[node]; slot < adjacentStart[node + 1]; slot++)
                {
                    int edgeIndex = adjacentEdges[slot];
                    if (!active[edgeIndex]) continue;
                    var edge = graphEdges[edgeIndex];
                    int other = edge.A == node ? edge.B : edge.A;
                    if (componentByNode[other] >= 0) continue;
                    componentByNode[other] = component;
                    queue.Enqueue(other);
                }
            }
            branchedComponents.Add(branched);
        }
        int componentCount = branchedComponents.Count;

        // For every surviving edge, find the shortest alternate path between its ends. Together
        // they form the smallest cycle containing that edge. In a degree-2 component only the
        // first edge needs a search, and it fixes the original order and direction of the loop.
        var candidates = new List<(List<double[]> Ring, HashSet<int>? Edges, double Area, int Component)>();
        var seenCycles = new HashSet<string>();
        var visitedSimpleLoops = new bool[componentCount];
        bool hasBranches = branchedComponents.Contains(true);
        var pathPrevNode = hasBranches ? new int[points.Count] : [];
        var pathPrevEdge = hasBranches ? new int[points.Count] : [];
        var pathQueue = new Queue<int>();
        for (int excluded = 0; excluded < graphEdges.Count; excluded++)
        {
            if (!active[excluded]) continue;
            var target = graphEdges[excluded];
            int component = componentByNode[target.A];
            if (!branchedComponents[component])
            {
                if (visitedSimpleLoops[component]) continue;
                visitedSimpleLoops[component] = true;
                var simpleRing = new List<double[]> { points[target.A] };
                // Follow the unique alternate path, in the same direction as the general search.
                int previousEdge = excluded, node = target.A;
                while (node != target.B)
                {
                    for (int slot = adjacentStart[node]; slot < adjacentStart[node + 1]; slot++)
                    {
                        int nextEdge = adjacentEdges[slot];
                        if (nextEdge == previousEdge || !active[nextEdge]) continue;
                        var edge = graphEdges[nextEdge];
                        node = edge.A == node ? edge.B : edge.A;
                        simpleRing.Add(points[node]);
                        previousEdge = nextEdge;
                        break;
                    }
                }
                double simpleArea = NewellNormalArea(simpleRing).Area;
                if (simpleArea >= 1e-8) candidates.Add((simpleRing, null, simpleArea, component));
                continue;
            }
            Array.Fill(pathPrevNode, -1);
            pathQueue.Clear();
            pathPrevNode[target.A] = target.A;
            pathQueue.Enqueue(target.A);
            while (pathQueue.Count > 0 && pathPrevNode[target.B] < 0)
            {
                int node = pathQueue.Dequeue();
                for (int slot = adjacentStart[node]; slot < adjacentStart[node + 1]; slot++)
                {
                    int edgeIndex = adjacentEdges[slot];
                    if (edgeIndex == excluded || !active[edgeIndex]) continue;
                    var edge = graphEdges[edgeIndex];
                    int other = edge.A == node ? edge.B : edge.A;
                    if (pathPrevNode[other] >= 0) continue;
                    pathPrevNode[other] = node;
                    pathPrevEdge[other] = edgeIndex;
                    pathQueue.Enqueue(other);
                }
            }
            if (pathPrevNode[target.B] < 0) continue;

            var cycleNodes = new List<int>();
            var cycleEdges = new HashSet<int> { excluded };
            for (int node = target.B; ; node = pathPrevNode[node])
            {
                cycleNodes.Add(node);
                if (node == target.A) break;
                cycleEdges.Add(pathPrevEdge[node]);
            }
            cycleNodes.Reverse();
            if (cycleNodes.Count < 3) continue;
            string cycleKey = SortedKey(cycleEdges);
            if (!seenCycles.Add(cycleKey)) continue;
            var ring = cycleNodes.Select(i => points[i]).ToList();
            double area = NewellNormalArea(ring).Area;
            if (area >= 1e-8) candidates.Add((ring, cycleEdges, area, component));
        }

        // Shortest paths can prefer a collinear zero-area shortcut and never expose the real inner
        // cycle (roof reveals are a common example).
        int activeEdgeCount = active.Count(x => x);
        if (hasBranches && points.Count <= 48 && activeEdgeCount <= 64)
        {
            int cycleBudget = 4096;
            var visited = new bool[points.Count];
            void SearchCycles(int start, int node, List<int> pathNodes, List<int> pathEdges)
            {
                if (cycleBudget <= 0 || pathNodes.Count > 32) return;
                for (int slot = adjacentStart[node]; slot < adjacentStart[node + 1]; slot++)
                {
                    int edgeIndex = adjacentEdges[slot];
                    if (!active[edgeIndex]) continue;
                    var edge = graphEdges[edgeIndex];
                    int other = edge.A == node ? edge.B : edge.A;
                    if (other == start)
                    {
                        if (pathNodes.Count < 3) continue;
                        var cycleEdges = new HashSet<int>(pathEdges) { edgeIndex };
                        string cycleKey = SortedKey(cycleEdges);
                        if (!seenCycles.Add(cycleKey)) continue;
                        cycleBudget--;
                        var ring = pathNodes.Select(i => points[i]).ToList();
                        double area = NewellNormalArea(ring).Area;
                        if (area >= 1e-8)
                            candidates.Add((ring, cycleEdges, area, componentByNode[start]));
                        continue;
                    }
                    // The start is the smallest node in the cycle. This removes rotated duplicates;
                    // the edge-set key above removes the reverse traversal.
                    if (other < start || visited[other]) continue;
                    visited[other] = true;
                    pathNodes.Add(other);
                    pathEdges.Add(edgeIndex);
                    SearchCycles(start, other, pathNodes, pathEdges);
                    pathEdges.RemoveAt(pathEdges.Count - 1);
                    pathNodes.RemoveAt(pathNodes.Count - 1);
                    visited[other] = false;
                }
            }

            for (int start = 0; start < points.Count && cycleBudget > 0; start++)
            {
                int component = componentByNode[start];
                if (component < 0 || !branchedComponents[component]) continue;
                visited[start] = true;
                SearchCycles(start, start, new List<int> { start }, new List<int>());
                visited[start] = false;
            }
        }

        if (!includeContainingCycles)
        {
            // Per component, in ascending order, the first candidate in the order of discovery that no later one beats on
            // (area rounded to 8 places, extent, ring size): what OrderBy(...).ThenBy(...).ThenBy(...).First() gives, ties keep the earlier.
            bool Beats(int challenger, int best)
            {
                int order = Math.Round(candidates[challenger].Area, 8).CompareTo(Math.Round(candidates[best].Area, 8));
                if (order == 0) order = ExtentScore(candidates[challenger].Ring).CompareTo(ExtentScore(candidates[best].Ring));
                if (order == 0) order = candidates[challenger].Ring.Count.CompareTo(candidates[best].Ring.Count);
                return (preferLargestCyclePerComponent ? -order : order) < 0;
            }
            var bestByComponent = new SortedDictionary<int, int>();
            for (int index = 0; index < candidates.Count; index++)
                if (!bestByComponent.TryGetValue(candidates[index].Component, out int best) || Beats(index, best))
                    bestByComponent[candidates[index].Component] = index;
            var primary = new List<List<double[]>>(bestByComponent.Count);
            foreach (int best in bestByComponent.Values) primary.Add(candidates[best].Ring);
            return primary;
        }

        // A recovered graph component may contain an outer EdgeLoop and opening EdgeLoops joined by
        // non-boundary records.
        var result = new List<List<double[]>>();
        static double ExtentScore(List<double[]> ring)
        {
            double dx = ring.Max(p => p[0]) - ring.Min(p => p[0]);
            double dy = ring.Max(p => p[1]) - ring.Min(p => p[1]);
            double dz = ring.Max(p => p[2]) - ring.Min(p => p[2]);
            return dx * dx + dy * dy + dz * dz;
        }
        foreach (var component in candidates.GroupBy(c => c.Component).OrderBy(g => g.Key))
        {
            if (!branchedComponents[component.Key])
            {
                result.Add(component.First().Ring);
                continue;
            }
            var cycles = component.OrderBy(c => c.Area)
                .ThenBy(c => ExtentScore(c.Ring))
                .ThenBy(c => c.Ring.Count).ToList();
            var primary = cycles[0];
            var outerMatch = cycles.Skip(1)
                .Where(cycle => !cycle.Edges!.Overlaps(primary.Edges!))
                .Select(cycle =>
                {
                    bool contains = RingContains(cycle.Ring, primary.Ring, out bool touchesBoundary);
                    return (Cycle: cycle, Contains: contains, TouchesBoundary: touchesBoundary);
                })
                .Where(match => match.Contains)
                .OrderByDescending(match => match.Cycle.Area)
                .ThenByDescending(match => ExtentScore(match.Cycle.Ring))
                .FirstOrDefault();
            if (outerMatch.Cycle.Ring == null)
            {
                result.Add(primary.Ring);
                continue;
            }

            var outer = outerMatch.Cycle;
            result.Add(outer.Ring);
            // A true hole is strictly inside the outer boundary. A cycle touching that boundary
            // describes a notch/opening cut and must not become an IfcFaceBound.
            if (!outerMatch.TouchesBoundary) result.Add(primary.Ring);
            var selectedEdges = new HashSet<int>();
            selectedEdges.UnionWith(outer.Edges!);
            selectedEdges.UnionWith(primary.Edges!);
            foreach (var cycle in cycles)
            {
                if (cycle.Edges!.Overlaps(selectedEdges)
                    || !RingContains(outer.Ring, cycle.Ring, out bool touchesBoundary)
                    || touchesBoundary) continue;
                result.Add(cycle.Ring);
                selectedEdges.UnionWith(cycle.Edges);
            }
        }
        return result.OrderByDescending(ring => NewellNormalArea(ring).Area).ToList();

        static bool RingContains(IReadOnlyList<double[]> outer, IReadOnlyList<double[]> inner,
            out bool touchesBoundary)
        {
            touchesBoundary = false;
            var normal = NewellNormalArea(outer.ToList()).Normal;
            int dropAxis = Math.Abs(normal[0]) >= Math.Abs(normal[1])
                && Math.Abs(normal[0]) >= Math.Abs(normal[2]) ? 0
                : Math.Abs(normal[1]) >= Math.Abs(normal[2]) ? 1 : 2;
            (double X, double Y) Project(double[] point) => dropAxis switch
            {
                0 => (point[1], point[2]),
                1 => (point[0], point[2]),
                _ => (point[0], point[1]),
            };
            var polygon = outer.Select(Project).ToArray();
            foreach (var point3d in inner)
            {
                var point = Project(point3d);
                bool inside = false;
                bool onBoundary = false;
                for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
                {
                    var a = polygon[j];
                    var b = polygon[i];
                    double cross = (b.X - a.X) * (point.Y - a.Y)
                        - (b.Y - a.Y) * (point.X - a.X);
                    if (cross == 0
                        && point.X >= Math.Min(a.X, b.X) && point.X <= Math.Max(a.X, b.X)
                        && point.Y >= Math.Min(a.Y, b.Y) && point.Y <= Math.Max(a.Y, b.Y))
                    {
                        onBoundary = true;
                        break;
                    }
                    if ((a.Y > point.Y) != (b.Y > point.Y)
                        && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                        inside = !inside;
                }
                if (onBoundary)
                {
                    touchesBoundary = true;
                    continue;
                }
                if (!inside) return false;
            }
            return true;
        }
    }

    internal static List<(List<double[]> Outer, List<List<double[]>> Holes, double[] Normal)>
        ClassifyCoplanarRings(IReadOnlyList<List<double[]>> sourceRings, bool exactPredicates = false)
    {
        var rings = sourceRings.Where(ring => ring.Count >= 3)
            .Select(ring => (Ring: ring, Shape: NewellNormalArea(ring)))
            .Where(candidate => candidate.Shape.Area > 0)
            .OrderByDescending(candidate => candidate.Shape.Area).ToList();
        if (rings.Count == 0) return [];

        static bool StrictlyContains(IReadOnlyList<double[]> outer,
            IReadOnlyList<double[]> inner, bool exactPredicates)
        {
            var normal = NewellNormalArea(outer.ToList()).Normal;
            int dropAxis = Math.Abs(normal[0]) >= Math.Abs(normal[1])
                && Math.Abs(normal[0]) >= Math.Abs(normal[2]) ? 0
                : Math.Abs(normal[1]) >= Math.Abs(normal[2]) ? 1 : 2;
            (double X, double Y) Project(double[] point) => dropAxis switch
            {
                0 => (point[1], point[2]),
                1 => (point[0], point[2]),
                _ => (point[0], point[1]),
            };
            var polygon = outer.Select(Project).ToArray();
            // A finite binary64 value is an integer multiple of 2^-1074. Keeping that integer makes
            // orientation exact, including subnormal values. No decimal conversion, tolerance, or
            // coordinate snapping is involved.
            static System.Numerics.BigInteger BinaryInteger(double value)
            {
                if (!double.IsFinite(value)) throw new ArgumentException("Nonfinite ring coordinate.");
                ulong bits = BitConverter.DoubleToUInt64Bits(value);
                int exponent = (int)((bits >> 52) & 0x7FF);
                ulong mantissa = bits & 0x000FFFFFFFFFFFFFul;
                if (exponent != 0) mantissa |= 0x0010000000000000ul;
                var integer = (System.Numerics.BigInteger)mantissa << Math.Max(0, exponent - 1);
                return (bits >> 63) == 0 ? integer : -integer;
            }
            var exactPolygon = exactPredicates
                ? polygon.Select(p => (X: BinaryInteger(p.X), Y: BinaryInteger(p.Y))).ToArray() : null;
            foreach (var point3d in inner)
            {
                var point = Project(point3d);
                var exactPoint = exactPredicates ? (X: BinaryInteger(point.X), Y: BinaryInteger(point.Y)) : default;
                int winding = 0;
                for (int index = 0; index < polygon.Length; index++)
                {
                    var first = polygon[index];
                    var second = polygon[(index + 1) % polygon.Length];
                    int orientation;
                    if (exactPolygon != null)
                    {
                        var a = exactPolygon[index];
                        var b = exactPolygon[(index + 1) % polygon.Length];
                        orientation = ((b.X - a.X) * (exactPoint.Y - a.Y)
                            - (b.Y - a.Y) * (exactPoint.X - a.X)).Sign;
                        if (orientation == 0 && point.X >= Math.Min(first.X, second.X)
                            && point.X <= Math.Max(first.X, second.X)
                            && point.Y >= Math.Min(first.Y, second.Y)
                            && point.Y <= Math.Max(first.Y, second.Y)) return false;
                    }
                    else
                    {
                        decimal determinant = ((decimal)second.X - (decimal)first.X)
                            * ((decimal)point.Y - (decimal)first.Y)
                            - ((decimal)second.Y - (decimal)first.Y)
                            * ((decimal)point.X - (decimal)first.X);
                        orientation = Math.Sign(determinant);
                        if (orientation == 0
                            && (decimal)point.X >= Math.Min((decimal)first.X, (decimal)second.X)
                            && (decimal)point.X <= Math.Max((decimal)first.X, (decimal)second.X)
                            && (decimal)point.Y >= Math.Min((decimal)first.Y, (decimal)second.Y)
                            && (decimal)point.Y <= Math.Max((decimal)first.Y, (decimal)second.Y)) return false;
                    }
                    if (first.Y <= point.Y && point.Y < second.Y && orientation > 0) winding++;
                    else if (second.Y <= point.Y && point.Y < first.Y && orientation < 0)
                        winding--;
                }
                if (winding == 0) return false;
            }
            return true;
        }

        var parent = Enumerable.Repeat(-1, rings.Count).ToArray();
        var depth = new int[rings.Count];
        for (int index = 1; index < rings.Count; index++)
        {
            parent[index] = Enumerable.Range(0, index)
                .Where(candidate => rings[candidate].Shape.Area > rings[index].Shape.Area
                    && StrictlyContains(rings[candidate].Ring, rings[index].Ring, exactPredicates))
                .OrderBy(candidate => rings[candidate].Shape.Area)
                .DefaultIfEmpty(-1).First();
            depth[index] = parent[index] < 0 ? 0 : depth[parent[index]] + 1;
        }

        var result = new List<(
            List<double[]> Outer, List<List<double[]>> Holes, double[] Normal)>();
        for (int index = 0; index < rings.Count; index++)
        {
            if (depth[index] % 2 != 0) continue;
            var outer = rings[index].Ring;
            var normal = rings[index].Shape.Normal;
            var holes = Enumerable.Range(0, rings.Count)
                .Where(candidate => parent[candidate] == index && depth[candidate] % 2 == 1)
                .Select(candidate =>
                {
                    var hole = rings[candidate].Ring;
                    var holeNormal = rings[candidate].Shape.Normal;
                    double dot = holeNormal[0] * normal[0] + holeNormal[1] * normal[1]
                        + holeNormal[2] * normal[2];
                    if (dot > 0) hole.Reverse();
                    return hole;
                }).ToList();
            result.Add((outer, holes, normal));
        }
        return result;
    }

    private static (double[] Normal, double Area) NewellNormalArea(List<double[]> ring)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            nx += (a[1] - b[1]) * (a[2] + b[2]);
            ny += (a[2] - b[2]) * (a[0] + b[0]);
            nz += (a[0] - b[0]) * (a[1] + b[1]);
        }
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        double area = len / 2;
        if (len > 1e-12) { nx /= len; ny /= len; nz /= len; }
        return (new[] { nx, ny, nz }, area);
    }


}
