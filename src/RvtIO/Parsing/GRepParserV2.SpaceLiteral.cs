using RvtIO.Geometry;
using RvtIO.Internal;

namespace RvtIO.Parsing;

internal static partial class GRepParserV2
{
    /// <summary>
    /// A sliver face of 0.9 mm or a gap of 0.36 mm between two Edges of a loop, which the welded
    /// reader cannot close, are part of the body then, and so is an Edge shared by four Faces.
    /// </summary>
    internal static GRepData? ParseLiteralPlanarSpaceBoundary(byte[] data, int start, uint size, uint owner) =>
        ParseExactPlanarSpaceBoundary(data, start, size, owner, 0, literal: true);

    private static GRepData? BuildLiteralSpaceBoundary(Cur c)
    {
        var edges = EdgesByHandle(c);
        var loopsByFace = CompleteFaceLoops(c);
        var faces = new List<GRepFace>();
        foreach (uint handle in c.FaceOrder)
        {
            if (!c.DirectSurfaceByFace.TryGetValue(handle, out var surface) || surface.Kind != SurfKind.Plane
                || !loopsByFace.TryGetValue(handle, out var loopIndices)) return null;
            var rings = new List<List<double[]>>();
            foreach (int index in loopIndices)
            {
                if (LiteralRing(c, handle, index, edges) is not { Count: >= 3 } ring) return null;
                rings.Add(ring);
            }
            // the one IfcFaceOuterBound is the last loop that lies inside no earlier one
            //
            int outer = 0;
            for (int index = 1; index < rings.Count; index++)
                if (!Enumerable.Range(0, index).Any(earlier => GeometryValidation.AllVerticesStrictlyInside(rings[index], rings[earlier]))) outer = index;
            var (normal, _) = NewellNormalArea(rings[outer]);
            faces.Add(new GRepFace
            {
                Vertices = rings[outer],
                InnerVertices = rings.Count > 1 ? rings.Where((_, index) => index != outer).ToList() : null,
                Origin = rings[outer][0], XVector = normal,
                PlaneX = (double[])surface.X.Clone(), PlaneY = (double[])surface.Y.Clone(),
                SourceFace = handle, SourceLoops = rings, FirstSourceLoop = rings[0], PlaneSurface = true,
            });
        }
        if (faces.Count == 0) return null;
        var vertices = faces.SelectMany(face => face.SourceLoops!.SelectMany(ring => ring)).ToList();
        return new GRepData
        {
            Faces = faces, PreserveSourceFaceIdentity = true,
            Solids = [new GRepSolid { FaceIndices = Enumerable.Range(0, faces.Count).ToList(), IsClosed = true }],
            BoundingBox = Enumerable.Range(0, 3).Select(axis => vertices.Min(point => point[axis]))
                .Concat(Enumerable.Range(0, 3).Select(axis => vertices.Max(point => point[axis]))).ToArray(),
        };
    }

    private static List<double[]>? LiteralRing(Cur c, uint face, int loopIndex, Dictionary<uint, EdgeRec?> edges)
    {
        var loop = c.SpaceEdgeLoops[loopIndex];
        if (loop.Open != 0) return null;
        uint sentinel = (uint)c.Queue[loopIndex].V, current = loop.Next;
        var ring = new List<double[]>();
        for (int guard = 0; current != sentinel; guard++)
        {
            if (guard > 100000 || !edges.TryGetValue(current, out var edge) || edge == null || !edge.HasEndpoints
                || edge.FaceA == edge.FaceB || edge.FaceA != face && edge.FaceB != face)
                return null;
            bool secondFace = edge.FaceA != face;
            // the points of an Edge are evaluated on its first Face, whichever Face walks it: the
            // two Faces of an Edge may store ends that differ by the 0.36 mm of a gap
            if (!c.DirectSurfaceByFace.TryGetValue(edge.FaceA, out var edgeSurface) || edgeSurface.Kind != SurfKind.Plane) return null;
            var edgePlane = ToPlane(edgeSurface);
            var points = edge.AuthoredPoints().Select(point => (U: point.UA, V: point.VA)).ToList();
            // The edge flip flag, turned over for the second Face of the Edge
            if ((edge.Flags & 1) != (secondFace ? 1 : 0)) points.Reverse();
            for (int i = 0; i + 1 < points.Count; i++)
            {
                if (World(edgePlane, points[i].U, points[i].V) is not { } world) return null;
                ring.Add(world);
            }
            current = secondFace ? edge.NextB : edge.NextA;
        }
        return ring;
    }
}
