using RvtIO.Geometry.Meshing;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    private sealed record FixtureFacet(List<double[]> World, List<(double X, double Y)> Ring,
        double Left, double Bottom, double Right, double Top, double MaxZ);

    private static List<(double X1, double Y1, double X2, double Y2)>? FixtureProfiles(GeometryData geometry)
    {
        if (geometry is not { BrepSolids.Count: > 1, UnresolvedSymbol: false, PlacedSymbolBox: false,
                PlacedSymbolClipped: false, UntriangulatedFaces: 0 }
            || geometry.BrepSolids.Any(solid => !solid.IsClosed || solid.FaceIndices.Count == 0))
            return null;

        var groups = new List<List<FixtureFacet>>();
        var faces = geometry.Faces;
        var assigned = new HashSet<int>();
        foreach (var solid in geometry.BrepSolids)
        {
            var facets = new List<FixtureFacet>();
            var members = new HashSet<int>();
            bool decoded = true;
            foreach (int index in solid.FaceIndices)
            {
                if ((uint)index >= faces.Count || !members.Add(index)) return null;
                // The reader can deduplicate an identical face referenced by different closed solids.
                assigned.Add(index);
                if (decoded) decoded = AddFacets(faces[index], facets);
            }
            if (!decoded)
            {
                facets.Clear();
                if (!MeshFixtureSolid(geometry, solid, facets)) return null;
            }
            if (facets.Count == 0) return null;
            groups.Add(facets);
        }
        if (assigned.Count != faces.Count) return null;

        var segments = new List<ElevationPrinter.CollectedEdge>();
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var otherFacets = groups.Where((_, index) => index != i).SelectMany(part => part).ToList();
            var rings = group.Select(facet => facet.Ring).ToList();
            var sources = group.SelectMany(facet => Enumerable.Range(0, facet.Ring.Count).Select(vertex =>
            {
                int next = (vertex + 1) % facet.Ring.Count;
                var a = facet.Ring[vertex];
                var b = facet.Ring[next];
                var (start, end) = a.CompareTo(b) <= 0
                    ? (facet.World[vertex], facet.World[next]) : (facet.World[next], facet.World[vertex]);
                return (Key: ElevationPrinter.ProjectedEdgeKey(a.X, a.Y, b.X, b.Y), Start: start, End: end);
            })).ToLookup(source => source.Key);
            foreach (var edge in ElevationPrinter.OutlineJoinedRings(rings, 0, coverOppositeEdges: true, retainSource: true))
            {
                var (ring, vertex) = edge.SourceRingEdge!.Value;
                var a = rings[ring][vertex];
                var b = rings[ring][(vertex + 1) % rings[ring].Count];
                var key = ElevationPrinter.ProjectedEdgeKey(a.X, a.Y, b.X, b.Y);
                // Several edges at different heights can share a projected silhouette.
                // Any visible span of the original edges contributes to the outline.
                foreach (var source in sources[key].DistinctBy(source => (source.Start[2], source.End[2])))
                    AddVisible(edge, source.Start, source.End, otherFacets);
            }

            double height = geometry.BrepSolids[i].FaceIndices.Max(index =>
                faces[index].OuterVertices!.Max(point => point[2]));
            foreach (int index in geometry.BrepSolids[i].FaceIndices)
            {
                // An upward cap with a hole defines the visible rims of the vessel.
                if (faces[index] is not { PlaneSurface: true, Normal: [_, _, > 0.999999],
                        OuterVertices: { } outer, InnerVertices: { Count: > 0 } holes }
                    || outer.Any(p => Math.Abs(p[2] - height) > 1e-8)
                    || holes.Any(hole => hole.Any(p => Math.Abs(p[2] - height) > 1e-8)))
                    continue;
                (outer, holes) = ResampleFixtureArcs(faces[index], outer, holes);
                foreach (var loop in holes.Prepend(outer))
                    for (int j = 0; j < loop.Count; j++)
                    {
                        var a = loop[j];
                        var b = loop[(j + 1) % loop.Count];
                        var start = InPlan(a);
                        var end = InPlan(b);
                        AddVisible(new(start.X, start.Y, end.X, end.Y, 0, 0, -1, -1, 0, 0, 0, 0),
                            a, b, otherFacets);
                    }
            }
        }

        if (segments.Count == 0) return null;
        return ElevationPrinter.MergeOverlaps(segments).Select(edge =>
            (edge.X1, edge.Y1, edge.X2, edge.Y2)).ToList();

        void AddVisible(ElevationPrinter.CollectedEdge edge, double[] a, double[] b,
            List<FixtureFacet> otherFacets)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1e-12) return;

            // Along this edge the plane has the same height as the original straight segment.
            // A projected face can obscure it only where the face lies above that plane.
            double[] origin = [a[0], a[1], a[2] + 1e-8];
            double[] normal = [-dx * dz / lengthSquared, -dy * dz / lengthSquared, 1];
            double left = Math.Min(edge.X1, edge.X2), right = Math.Max(edge.X1, edge.X2);
            double bottom = Math.Min(edge.Y1, edge.Y2), top = Math.Max(edge.Y1, edge.Y2);
            var covers = otherFacets.Where(facet => facet.MaxZ >= Math.Min(a[2], b[2])
                    && facet.Left <= right && facet.Right >= left && facet.Bottom <= top && facet.Top >= bottom)
                .Select(facet => ElevationPrinter.ClipBehind(facet.World, origin, normal).Select(InPlan).ToList())
                .Where(ring => ring.Count >= 3 && Math.Abs(SignedArea(ring)) > 1e-8);
            foreach (var span in ElevationPrinter.VisibleTerrainEdges([edge], covers, [], coverBoundary: false))
                segments.Add((span.X1, span.Y1).CompareTo((span.X2, span.Y2)) <= 0 ? span
                    : span with { X1 = span.X2, Y1 = span.Y2, X2 = span.X1, Y2 = span.Y1 });
        }
    }

    private static (List<double[]> Outer, List<List<double[]>> Holes) ResampleFixtureArcs(
        GeoFace face, List<double[]> outer, List<List<double[]>> holes)
    {
        if (face is not { PlaneSurface: true, Normal: [_, _, var z], MeshArcs: { Count: > 0 } arcs }
            || !(Math.Abs(z) > 0.999999)) return (outer, holes);

        // A split face carries the arc list of the source face. Match only runs on this region.
        var regionArcs = new List<Internal.Arc>();
        foreach (var ring in holes.Prepend(outer))
        {
            var occupied = new HashSet<int>();
            foreach (var run in IfcExporter.FindArcRunsCurrent(ring, arcs))
            {
                for (int i = 0; i < run.Steps; i++)
                    if (!occupied.Add((run.First + i) % ring.Count)) return (outer, holes);
                regionArcs.Add(run.Arc);
            }
        }
        return IfcExporter.TryResampleArcsCurrent(outer, holes, regionArcs,
            out var curvedOuter, out var curvedHoles, out _) ? (curvedOuter, curvedHoles) : (outer, holes);
    }

    private static bool AddFacets(GeoFace face, List<FixtureFacet> facets)
    {
        if (face.OuterVertices is not { Count: >= 3 } outer
            || face.InnerLoops?.Count > (face.InnerVertices?.Count ?? 0)) return false;
        var holes = face.InnerVertices ?? [];
        var contours = holes.Prepend(outer).ToList();
        if (contours.Any(loop => loop.Count < 3 || loop.Any(p => p.Length != 3 || !p.All(double.IsFinite))))
            return false;
        (outer, holes) = ResampleFixtureArcs(face, outer, holes);
        contours = holes.Prepend(outer).ToList();
        var projected = contours.Select(loop => loop.Select(InPlan).ToList()).ToList();
        if (holes.Count == 0 && (face.PlaneSurface || outer.Count == 3))
        {
            AddFixtureFacet(outer, projected[0], facets);
            return true;
        }

        var world = contours.SelectMany(loop => loop).ToArray();
        var plan = projected.SelectMany(loop => loop).ToArray();
        if (face.Normal is not { Length: 3 } normal || !normal.All(double.IsFinite)
            || normal.All(value => value == 0)) return false;
        // A curved patch can fold over itself in plan. Triangulate its native ring in the
        // dominant plane of the patch normal, then project the individual triangles.
        int axis = Array.IndexOf(normal, normal.MaxBy(value => Math.Abs(value)));
        var local = world.Select(p => (X: p[(axis + 1) % 3], Y: p[(axis + 2) % 3])).ToArray();
        int offset = 0;
        var indices = contours.Select(loop =>
        {
            var range = Enumerable.Range(offset, loop.Count).ToArray();
            offset += loop.Count;
            return range;
        }).ToArray();
        var outerFlags = new bool[indices.Length];
        outerFlags[0] = true;
        double outerArea = Math.Abs(SignedArea(indices[0].Select(index => local[index]).ToList()));
        if (outerArea < 1e-12) return true;
        double expected = outerArea - indices.Skip(1).Sum(loop =>
            Math.Abs(SignedArea(loop.Select(index => local[index]).ToList())));
        double area = 0;
        foreach (var (a, b, c) in GeTess.TriangulateProfile(local, indices, outerFlags))
        {
            area += Math.Abs(SignedArea([local[a], local[b], local[c]]));
            AddFixtureFacet([world[a], world[b], world[c]], [plan[a], plan[b], plan[c]], facets);
        }
        return expected > 0 && Math.Abs(area - expected) <= 1e-7 * outerArea;
    }

    private static bool MeshFixtureSolid(GeometryData geometry, GeoBrepSolid solid, List<FixtureFacet> facets)
    {
        var source = solid.FaceIndices.Select(index => geometry.Faces[index]).ToArray();
        int id = source[0].GeometryNode;
        if (id <= 0 || source.Any(face => face.GeometryNode != id)
            || geometry.MeshNodes?.Where(node => node.Node == id && !node.Ghost).Take(2).ToArray()
                is not [var meshNode]
            || meshNode.Placement is not { Length: 12 } placement
            || !ElementBuilder.IsRigidPlacement(placement)) return false;

        var markers = meshNode.Faces.Select(face => (uint)face.Marker).ToHashSet();
        if (source.Any(face => !markers.Contains(face.SourceFace != 0 ? face.SourceFace : face.MeshMarker)))
            return false;

        // Some curved trims fold across a projection of the dense polygons. In that case use
        // the same authored surface and edge sampler used to mesh the node for export.
        const double lod = 0.2;
        bool freeform = meshNode.Faces.Any(FreeformSolid.IsFreeform);
        var sampler = freeform ? FreeformSolid.Create(meshNode.Faces, lod, meshNode.Diagonal) : null;
        if (freeform && sampler == null) return false;
        foreach (var face in meshNode.Faces)
        {
            bool ok = FreeformSolid.IsFreeform(face) ? sampler!.TryMesh(face, out var mesh)
                : FaceMesher.TryMesh(GRepParserV2.WithSamples(face, lod, meshNode.Diagonal, sampler),
                    lod, meshNode.Diagonal, out mesh);
            if (!ok) return false;
            var world = mesh.Vertices.Select(point => ElementBuilder.TransformGeometryPoint(point, placement)).ToArray();
            if (world.Any(point => !point.All(double.IsFinite))) return false;
            var plan = world.Select(InPlan).ToArray();
            foreach (var triangle in mesh.Triangles)
                AddFixtureFacet([world[triangle[0]], world[triangle[1]], world[triangle[2]]],
                    [plan[triangle[0]], plan[triangle[1]], plan[triangle[2]]], facets);
        }
        return facets.Count > 0;
    }

    private static void AddFixtureFacet(List<double[]> vertices, List<(double X, double Y)> ring,
        List<FixtureFacet> facets)
    {
        double signed = SignedArea(ring);
        if (Math.Abs(signed) < 1e-8) return;
        if (signed < 0)
        {
            vertices = [.. vertices.AsEnumerable().Reverse()];
            ring.Reverse();
        }
        facets.Add(new(vertices, ring, ring.Min(p => p.X), ring.Min(p => p.Y),
            ring.Max(p => p.X), ring.Max(p => p.Y), vertices.Max(p => p[2])));
    }

    private static (double X, double Y) InPlan(double[] point) =>
        (point[0] * FeetToMillimetres, point[1] * FeetToMillimetres);
}
