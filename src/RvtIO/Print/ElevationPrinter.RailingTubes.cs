using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Print;

public static partial class ElevationPrinter
{
    private static IEnumerable<(GeoFace Face, bool Tube)> SectionFaces(RvtDocument document, Element element,
        GeometryData geometry, bool renderTubes, double[] horizontal, double[] vertical, double[] view, double[] origin,
        bool renderRoundShafts, double maxElevationTubeDiameter, List<CollectedEdge> elevationTubeEdges)
    {
        var replaced = new HashSet<GeoFace>();
        if (renderRoundShafts)
            foreach (var (shaft, facets) in ProjectRoundShafts(document, geometry, horizontal, vertical, view, origin,
                element.CategoryId == (int)BuiltInCategory.OST_StructuralColumns))
            {
                replaced.UnionWith(facets);
                yield return (shaft, true);
            }
        if ((renderTubes || maxElevationTubeDiameter > 0) && element.SerializedBaseRailingSymbolId is int id
            && document.GetElement(id) is { NativeRecordClass: 492, Geometry: { } source }
            && geometry.Instances?.Where(instance => instance.SymbolId == id).Take(2).ToArray()
                is [{ Transform: { Length: 12 } transform }]
            && ElementBuilder.IsRigidPlacement(transform))
        {
            // Face handles are local to a GRep. Check the placed vertices as well as the source handle.
            var placed = geometry.Faces.Where(face => face.MeshMarker != 0 && face.OuterVertices is { Count: >= 3 })
                .ToLookup(face => (face.MeshMarker, Key(face.OuterVertices![0])));
            var cylinders = source.Faces.Where(face => face.SourceFace != 0 && face.CurvedSurface
                && face.AnalyticCylinderOrigin is { Length: 3 } && face.AnalyticCylinderAxis is { Length: 3 }
                && face.AnalyticCylinderRadius is > 0 and < double.PositiveInfinity && face.OuterVertices is { Count: >= 3 })
                .GroupBy(face => (face.GeometryNode, Centre: Key(face.AnalyticCylinderOrigin!),
                    Axis: Key(face.AnalyticCylinderAxis!), face.AnalyticCylinderRadius));
            foreach (var cylinder in cylinders)
            {
                // shortcut: two paired analytic sides of a straight rail segment.
                if (cylinder.Select(face => face.SourceFace).Distinct().Count() != 2
                    || !renderTubes && 2 * cylinder.Key.AnalyticCylinderRadius > maxElevationTubeDiameter) continue;
                var patches = new List<GeoFace>();
                foreach (var face in cylinder)
                {
                    var expected = face.OuterVertices!.Select(point => PlaceProjectionPoint(point, transform)).ToArray();
                    var match = placed[(face.SourceFace, Key(expected[0]))].FirstOrDefault(candidate =>
                        candidate.OuterVertices!.Count == expected.Length
                        && candidate.OuterVertices.Zip(expected).All(pair => Key(pair.First) == Key(pair.Second)));
                    if (match == null) break;
                    patches.Add(match);
                }
                if (patches.Count != cylinder.Count()) continue;

                if (!renderTubes)
                {
                    var (x, y, z) = cylinder.Key.Axis;
                    double[] axis =
                    [
                        x * transform[0] + y * transform[3] + z * transform[6],
                        x * transform[1] + y * transform[4] + z * transform[7],
                        x * transform[2] + y * transform[5] + z * transform[8],
                    ];
                    if (Math.Abs(Project(axis, horizontal, 1)) <= 1e-9) continue;
                    var centre = cylinder.Key.Centre;
                    var placedCentre = PlaceProjectionPoint([centre.X, centre.Y, centre.Z], transform);
                    var vertices = patches.SelectMany(patch => patch.OuterVertices!);
                    double axisLengthSquared = Project(axis, axis, 1);
                    double centreAlong = Project(placedCentre, axis, 1);
                    double from = (vertices.Min(point => Project(point, axis, 1)) - centreAlong) / axisLengthSquared;
                    double to = (vertices.Max(point => Project(point, axis, 1)) - centreAlong) / axisLengthSquared;
                    double startDepth = Project(placedCentre, view);
                    double forward = Project(axis, view);
                    double near = Project(origin, view, 1);
                    if (forward > 1e-9) from = Math.Max(from, (near - startDepth) / forward);
                    else if (forward < -1e-9) to = Math.Min(to, (near - startDepth) / forward);
                    else if (startDepth < near) continue;
                    if (!(to > from)) continue;
                    // A tube narrower than its print stroke inks once along the placed analytic axis.
                    double At(double t, double[] direction) => Project(placedCentre, direction) + t * Project(axis, direction);
                    double mid = At((from + to) / 2, view);
                    elevationTubeEdges.Add(new(At(from, horizontal), At(from, vertical), At(to, horizontal), At(to, vertical),
                        mid, mid, -1, -1, 0, 0, 0, 0, false, element.CategoryId ?? 0,
                        Math.Min(At(from, view), At(to, view))));
                    continue;
                }

                var points = patches.SelectMany(patch =>
                    ClipBehind(patch.OuterVertices!.Select(point =>
                        point.Select(value => value * FeetToMillimetres).ToArray()).ToList(), origin, view));
                var hull = ProjectedRailHull(points, horizontal, vertical, view);
                if (hull.Count < 3) continue;
                replaced.UnionWith(patches);
                yield return (new GeoFace
                {
                    OuterVertices = hull.Select(point => point.Select(value => value / FeetToMillimetres).ToArray()).ToList(),
                    Normal = view,
                }, true);
            }
        }
        foreach (var face in geometry.Faces)
            if (!replaced.Contains(face)) yield return (face, false);

        static (double X, double Y, double Z) Key(double[] point) => (point[0], point[1], point[2]);
    }

    private static double[] PlaceProjectionPoint(double[] p, double[] t) =>
    [
        t[9]  + p[0] * t[0] + p[1] * t[3] + p[2] * t[6],
        t[10] + p[0] * t[1] + p[1] * t[4] + p[2] * t[7],
        t[11] + p[0] * t[2] + p[1] * t[5] + p[2] * t[8],
    ];

    private static IEnumerable<(GeoFace Shaft, GeoFace[] Facets)> ProjectRoundShafts(RvtDocument document,
        GeometryData geometry, double[] horizontal, double[] vertical, double[] view, double[] origin, bool column)
    {
        if (Math.Abs(vertical[2] - 1) > 1e-9 || origin.Length != 3
            || geometry.Instances is not [{ Transform: { Length: 12 } transform } instance]
            || !ElementBuilder.IsRigidPlacement(transform) || Math.Abs(Math.Abs(transform[8]) - 1) > 1e-9
            || document.GetElement(instance.SymbolId)?.Geometry is not { BrepSolids: { } solids } symbol
            || geometry.Faces.Count != symbol.Faces.Count) yield break;

        foreach (var indices in ShaftFaceIndices())
        {
            var native = indices.Select(index => symbol.Faces[index]).ToArray();
            var facets = indices.Select(index => geometry.Faces[index]).ToArray();
            var sides = native.Where(face => face.AnalyticCylinderRadius > 0).ToArray();
            if (sides.Length < 2 || column && (native.Length != sides.Length + 2
                || native.Except(sides).Any(face => !face.PlaneSurface || Math.Abs(face.Normal[2]) < 1 - 1e-9))
                || sides[0] is not { AnalyticCylinderOrigin: { Length: 3 } axisOrigin,
                    AnalyticCylinderRadius: > 0 and < double.PositiveInfinity and double radius }
                || sides.Any(face => face.AnalyticCylinderAxis is not { Length: 3 } axis || Math.Abs(Math.Abs(axis[2]) - 1) > 1e-9
                    || face.AnalyticCylinderRadius != radius || face.AnalyticCylinderOrigin?.SequenceEqual(axisOrigin) != true)
                || (facets[0].RenderStyleId ?? facets[0].NestedRenderStyleId) is not (> 0 and int material)
                || facets.Any(face => (face.RenderStyleId ?? face.NestedRenderStyleId) != material)
                || !native.Zip(facets).All(pair => pair.First.OuterVertices is { Count: >= 3 } source
                    && pair.Second.OuterVertices is { } placed && source.Count == placed.Count
                    && source.Zip(placed).All(points => PlaceProjectionPoint(points.First, transform).Zip(points.Second)
                        .All(coordinate => Math.Abs(coordinate.First - coordinate.Second) < 1e-6)))) continue;

            var centre = PlaceProjectionPoint(axisOrigin, transform);
            if ((Project(centre, view, 1) - radius) * FeetToMillimetres <= Project(origin, view, 1)) continue;
            double bottom = facets.Min(face => face.OuterVertices!.Min(point => point[2]));
            double top = facets.Max(face => face.OuterVertices!.Max(point => point[2]));
            if (!(top > bottom)) continue;
            if (!column && (facets.Any(facet =>
                Math.Abs(facet.OuterVertices!.Min(p => p[2]) - bottom) > 1e-6
                || Math.Abs(facet.OuterVertices!.Max(p => p[2]) - top) > 1e-6)
                || !HasTangent(-1, bottom) || !HasTangent(1, bottom)
                || !HasTangent(-1, top) || !HasTangent(1, top))) continue;
            // shortcut: the silhouette uses tangent depth, replace it when cylindrical depth tests are supported.
            double[] Edge(double offset, double height) =>
                [centre[0] + horizontal[0] * offset, centre[1] + horizontal[1] * offset, height];
            yield return (new GeoFace
            {
                OuterVertices = [Edge(-radius, bottom), Edge(radius, bottom), Edge(radius, top), Edge(-radius, top)],
                Normal = [-view[0], -view[1], -view[2]], PlaneSurface = true, RenderStyleId = material,
            }, facets);

            bool HasTangent(int side, double height)
            {
                // A trimmed cylinder can omit a tangent. Check that its actual rims span each tangent
                // at both ends before replacing the curved sides of a compound solid.
                double centreX = Project(centre, horizontal, 1), centreDepth = Project(centre, view, 1);
                foreach (var facet in facets)
                {
                    var vertices = facet.OuterVertices!;
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        var a = vertices[i];
                        var b = vertices[(i + 1) % vertices.Count];
                        if (Math.Abs(a[2] - height) > 1e-6 || Math.Abs(b[2] - height) > 1e-6
                            || side * (Project(a, horizontal, 1) - centreX) <= 0
                            || side * (Project(b, horizontal, 1) - centreX) <= 0) continue;
                        double da = Project(a, view, 1) - centreDepth;
                        double db = Project(b, view, 1) - centreDepth;
                        if (Math.Min(da, db) <= 1e-6 && Math.Max(da, db) >= -1e-6) return true;
                    }
                }
                return false;
            }
        }

        IEnumerable<IReadOnlyList<int>> ShaftFaceIndices()
        {
            foreach (var solid in solids.Where(solid => solid.IsClosed))
            {
                if (solid.FaceIndices.Any(index => (uint)index >= symbol.Faces.Count)) continue;
                if (column)
                {
                    yield return solid.FaceIndices;
                    continue;
                }
                var cylinders = solid.FaceIndices.Where(index => symbol.Faces[index] is
                    { CurvedSurface: true, SourceFace: not 0, AnalyticCylinderRadius: > 0,
                        AnalyticCylinderOrigin: { Length: 3 }, InnerVertices: not { Count: > 0 }, InnerLoops: not { Count: > 0 } })
                    .GroupBy(index =>
                    {
                        var face = symbol.Faces[index];
                        var axis = face.AnalyticCylinderOrigin!;
                        return (face.GeometryNode, axis[0], axis[1], axis[2], face.AnalyticCylinderRadius);
                    });
                foreach (var cylinder in cylinders)
                    if (cylinder.Select(index => symbol.Faces[index].SourceFace).Distinct().Count() == 2)
                        yield return cylinder.ToArray();
            }
        }
    }

    private static List<double[]> ProjectedRailHull(IEnumerable<double[]> vertices, double[] horizontal,
        double[] vertical, double[] view)
    {
        var points = vertices.Select(point => (X: Dot(point, horizontal), Y: Dot(point, vertical), Point: point))
            .OrderBy(point => point.X).ThenBy(point => point.Y).ThenBy(point => Dot(point.Point, view))
            .DistinctBy(point => (point.X, point.Y)).ToArray();
        if (points.Length < 3) return [];
        var hull = new List<(double X, double Y, double[] Point)>();
        AddHalf(points, 0);
        hull.RemoveAt(hull.Count - 1);
        AddHalf(Enumerable.Reverse(points), hull.Count);
        hull.RemoveAt(hull.Count - 1);
        return hull.Select(point => point.Point).ToList();

        void AddHalf(IEnumerable<(double X, double Y, double[] Point)> sorted, int start)
        {
            foreach (var point in sorted)
            {
                while (hull.Count > start + 1)
                {
                    var a = hull[^2];
                    var b = hull[^1];
                    if ((b.X - a.X) * (point.Y - a.Y) > (b.Y - a.Y) * (point.X - a.X)) break;
                    hull.RemoveAt(hull.Count - 1);
                }
                hull.Add(point);
            }
        }

        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    }
}
