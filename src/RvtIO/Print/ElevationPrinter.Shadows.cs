namespace RvtIO.Print;

using RvtIO.BuiltIns;
using RvtIO.Geometry.Meshing;
using RvtIO.Model;

public static partial class ElevationPrinter
{
    internal sealed record CastShadow(double Light, List<List<(double X, double Y)>> Rings,
        List<List<(double X, double Y)>> Openings);

    internal sealed record VisibleSiteFacets(IReadOnlySet<List<(double X, double Y)>> Sources,
        List<List<(double X, double Y)>> Rings, List<List<(double X, double Y)>> Shade, double Light);

    internal sealed class ElevationShadows(double[] h, double[] v, double[] d, double shiftX, double shiftY, double light)
    {
        private const double PlaneTolerance = 0.01; // mm
        private readonly List<ShadowCaster> _casters = [];
        private readonly List<Receiver> _receivers = [];
        private readonly List<VisibleFacet> _visibleFacets = [];
        private readonly List<VisibleFacet> _unlitBillboardFacets = [];
        private readonly List<VisibleFacet> _terrainFacets = [];
        private readonly List<VisibleFacet> _planting = [];
        private readonly List<VisibleFacet> _billboards = [];
        private readonly List<VisibleFacet> _modelOccluders = [];
        private TerrainOcclusion? _terrain;
        internal TerrainOcclusion Terrain => _terrain ??= new(h, v, d, _terrainFacets);
        private sealed record ShadowCaster(List<double[]> Outer, List<List<double[]>> Holes,
            (double X0, double Y0, double X1, double Y1) Bounds, double MinDepth, bool SiteOnly, bool ProjectsToSoilCut,
            bool GroundOnly, int? RoofId, bool VerifiedOpaque, bool CastsOnVisiblePlanting);
        private sealed record Receiver(List<(double X, double Y)> Face, GeoFace Geometry,
            double A, double B, double C, double MaxDepth, bool Site,
            (double X0, double Y0, double X1, double Y1) LightBounds, VisibleFacet? Facet = null, bool SoilCut = false,
            bool Terrain = false, bool RoundShaft = false, int? RoofId = null, bool MeshFacet = false,
            bool ReceivesOffscreenPlantShadows = false);

        internal sealed record VisibleFacet(List<(double X, double Y)> Points, double Dx, double Dy, double Offset,
            (double X0, double Y0, double X1, double Y1) Bounds, bool MasksModel = false)
        {
            internal double DepthAt((double X, double Y) p) => Dx * p.X + Dy * p.Y + Offset;
        }

        internal static ElevationShadows? Create((double Azimuth, double Altitude, int GroundLevel, int Intensity)? sun,
            double[] h, double[] v, double[] d)
        {
            // Rays are parameterized by their depth into the view, so the light must come from the front.
            if (sun is not { Intensity: > 0 } settings) return null;
            double azimuth = settings.Azimuth;
            double altitude = settings.Altitude;
            double forward = -Math.Cos(azimuth) * Math.Cos(altitude);
            if (forward <= 1e-9) return null;
            return new(h, v, d, Math.Sin(azimuth) * Math.Cos(altitude) / forward,
                Math.Sin(altitude) / forward, 1 - settings.Intensity / 100.0);
        }

        internal void AddReceiver(List<(double X, double Y)> points, GeoFace face, double depth, bool roundShaft = false)
        {
            if (Dot(face.Normal, d) < 0 && (face.InnerLoops is not { Count: > 0 }
                || face.InnerVertices is { Count: > 0 }))
                _receivers.Add(new(points, face, 0, 0, depth, depth, false,
                    Bounds(points.Select(p => (p.X + shiftX * depth, p.Y + shiftY * depth))), RoundShaft: roundShaft));
        }

        internal void AddPlanarModelReceiver(List<(double X, double Y)> points, GeoFace face, List<double[]> corners,
            bool meshFacet)
        {
            if (face.InnerLoops is not { Count: > 0 } || face.InnerVertices is { Count: > 0 })
                AddPlanarReceiver(points, face, corners, null, site: false, meshFacet: meshFacet);
        }

        internal void AddRoofReceiver(List<(double X, double Y)> points, GeoFace face, List<double[]> corners, int roofId) =>
            AddPlanarReceiver(points, face, corners, null, roofId: roofId, site: false);

        internal void AddTerrainReceiver(List<(double X, double Y)> points, GeoFace face, List<double[]> corners,
            bool placed = true)
        {
            // shortcut: preserve the existing projection for complex faces until their holes and concave outlines are tessellated.
            if (!SupportsTerrainFacet(points, face))
            {
                AddPlanarReceiver(points, face, corners, null);
                return;
            }

            var facet = CreateVisibleFacet(h, v, d, points, face, corners);
            if (facet == null) return;
            if (placed) facet = facet with { MasksModel = true };
            _terrainFacets.Add(facet);
            AddPlanarReceiver(points, face, corners, facet, terrain: true);
        }

        private static bool SupportsTerrainFacet(List<(double X, double Y)> points, GeoFace face) =>
            face.InnerLoops is not { Count: > 0 } && face.InnerVertices is not { Count: > 0 }
            && (points.Count <= 3 || Sides(points).All(side => points.All(point =>
                side.X * point.X + side.Y * point.Y + side.C >= -PlaneTolerance)));

        internal List<(double X, double Y)>? AddSoilCutReceiver(
            (double X0, double Y0, double X1, double Y1) crop, double depth, double baseY)
        {
            double bottom = Math.Max(baseY, crop.Y0);
            if (!(crop.Y1 > bottom && crop.X1 > crop.X0)) return null;
            List<(double X, double Y)> ring =
                [(crop.X0, bottom), (crop.X1, bottom), (crop.X1, crop.Y1), (crop.X0, crop.Y1)];
            var corners = ring.Select(point => Enumerable.Range(0, 3)
                .Select(axis => point.X * h[axis] + point.Y * v[axis] + depth * d[axis]).ToArray()).ToList();
            var face = new GeoFace { Normal = [-d[0], -d[1], -d[2]] };
            AddPlanarReceiver(ring, face, corners, null, soilCut: true);
            return ring;
        }

        internal void AddVisibleFacet(List<(double X, double Y)> points, GeoFace face, List<double[]> corners,
            bool planting = false, bool twoSidedBillboard = false, bool receivesShadow = true,
            bool receivesOffscreenPlantShadows = false)
        {
            var facet = CreateVisibleFacet(h, v, d, points, face, corners);
            if (facet == null) return;
            _visibleFacets.Add(facet);
            if (planting) _planting.Add(facet);
            if (twoSidedBillboard) _billboards.Add(facet);
            if (receivesShadow)
                AddPlanarReceiver(points, face, corners, facet, twoSidedBillboard: twoSidedBillboard,
                    receivesOffscreenPlantShadows: receivesOffscreenPlantShadows);
        }

        internal static VisibleFacet? CreateVisibleFacet(double[] h, double[] v, double[] d,
            List<(double X, double Y)> points, GeoFace face, List<double[]> corners)
        {
            double nz = Dot(face.Normal, d);
            if (Math.Abs(nz) < 1e-9) return null;
            return new(points, -Dot(face.Normal, h) / nz, -Dot(face.Normal, v) / nz,
                Dot(face.Normal, corners[0]) / nz, Bounds(points));
        }

        internal static Dictionary<byte, List<List<(double X, double Y)>>>? VisiblePlantTones(
            List<VisibleFacet> facets, Dictionary<VisibleFacet, byte> tones)
        {
            if (tones.Count == 0) return null;
            facets.Sort((left, right) => left.Bounds.X0.CompareTo(right.Bounds.X0));
            var result = new Dictionary<byte, List<List<(double X, double Y)>>>();
            foreach (var (facet, gray) in tones)
            {
                var visible = VisibleParts(facet, facets);
                if (visible.Count == 0) continue;
                if (!result.TryGetValue(gray, out var rings)) result.Add(gray, rings = []);
                rings.AddRange(visible);
            }
            return result;
        }

        internal sealed class TerrainOcclusion(double[] h, double[] v, double[] d, List<VisibleFacet>? facets = null)
        {
            private readonly List<VisibleFacet> _terrain = facets ?? [];
            private readonly List<VisibleFacet> _targets = [];

            internal void AddSurface(List<(double X, double Y)> points, GeoFace face, List<double[]> corners)
            {
                if (SupportsTerrainFacet(points, face) && CreateVisibleFacet(h, v, d, points, face, corners) is { } facet)
                    _terrain.Add(facet with { MasksModel = true });
            }

            internal void AddTarget(List<(double X, double Y)> points, GeoFace face, List<double[]> corners)
            {
                double origin = Dot(face.Normal, corners[0]);
                if (corners.All(corner => Math.Abs(Dot(face.Normal, corner) - origin) < 1e-6)
                    && CreateVisibleFacet(h, v, d, points, face, corners) is { } facet)
                    _targets.Add(facet);
            }

            internal void Sort() => _terrain.Sort((a, b) => a.Bounds.X0.CompareTo(b.Bounds.X0));

            internal Dictionary<List<(double X, double Y)>, List<List<(double X, double Y)>>>? ClippedFaces()
            {
                var terrain = _terrain.Where(facet => facet.MasksModel).ToList();
                if (terrain.Count == 0) return null;
                var result = new Dictionary<List<(double X, double Y)>, List<List<(double X, double Y)>>>();
                foreach (var target in _targets)
                {
                    var visible = VisibleParts(target, terrain);
                    if (visible.Count != 1 || !ReferenceEquals(visible[0], target.Points))
                        result.Add(target.Points, visible);
                }
                return result.Count > 0 ? result : null;
            }

            internal List<CollectedEdge> ClipEdges(List<CollectedEdge> edges, bool cut, bool requireFace = true)
            {
                if (!_terrain.Any(facet => facet.MasksModel)) return edges;
                var result = new List<CollectedEdge>(edges.Count);
                foreach (var edge in edges)
                {
                    if (edge.IsCut || requireFace && edge.FaceA < 0 || edge.ViewLineStyleId != 0 || edge.NearDepth is not double depth
                        || cut && edge.CategoryId is not ((int)BuiltInCategory.OST_BuildingPad
                            or (int)BuiltInCategory.OST_Walls or (int)BuiltInCategory.OST_StructuralFoundation))
                    {
                        result.Add(edge);
                        continue;
                    }
                    var bounds = (Math.Min(edge.X1, edge.X2), Math.Min(edge.Y1, edge.Y2),
                        Math.Max(edge.X1, edge.X2), Math.Max(edge.Y1, edge.Y2));
                    // shortcut: clip only where terrain is ahead of the entire edge until edge endpoints carry depth.
                    var covers = TerrainCover(bounds, depth).ToList();
                    if (covers.Count == 0) result.Add(edge);
                    else result.AddRange(VisibleTerrainEdges([edge], covers, [], coverBoundary: false));
                }
                return result;
            }

            private IEnumerable<List<(double X, double Y)>> TerrainCover(
                (double X0, double Y0, double X1, double Y1) bounds, double depth)
            {
                foreach (var facet in _terrain)
                {
                    var other = facet.Bounds;
                    if (other.X0 > bounds.X1) break;
                    if (!facet.MasksModel || other.X1 < bounds.X0 || other.Y1 < bounds.Y0 || other.Y0 > bounds.Y1)
                        continue;
                    double Difference((double X, double Y) point) => depth - facet.DepthAt(point) - PlaneTolerance;
                    var cover = ClipHalfPlane(facet.Points, p => Difference(p) > 0, Difference);
                    if (HasArea(cover)) yield return cover;
                }
            }
        }

        internal void AddModelOccluder(List<(double X, double Y)> points, double[] normal,
            double[] corner, double depth, List<List<(double X, double Y)>>? holes) =>
            AddOccluder(_modelOccluders, h, v, d, points, normal, corner, depth, holes);

        private static void AddOccluder(List<VisibleFacet> occluders, double[] h, double[] v, double[] d,
            List<(double X, double Y)> points, double[] normal, double[] corner, double depth,
            List<List<(double X, double Y)>>? holes)
        {
            double nd = Dot(normal, d);
            double dx = 0, dy = 0;
            if (Math.Abs(nd) > 1e-10)
            {
                dx = -Dot(normal, h) / nd;
                dy = -Dot(normal, v) / nd;
                depth = Dot(normal, corner) / nd;
            }

            if (points.Count == 3 && holes is not { Count: > 0 })
            {
                occluders.Add(new(points, dx, dy, depth, Bounds(points)));
                return;
            }

            var vertices = points.ToList();
            var loops = new List<int[]> { Enumerable.Range(0, points.Count).ToArray() };
            foreach (var hole in holes ?? [])
            {
                loops.Add(Enumerable.Range(vertices.Count, hole.Count).ToArray());
                vertices.AddRange(hole);
            }
            var outer = new bool[loops.Count];
            outer[0] = true;
            foreach (var (a, b, c) in GeTess.TriangulateProfile(vertices, loops, outer))
            {
                var triangle = new List<(double X, double Y)> { vertices[a], vertices[b], vertices[c] };
                occluders.Add(new(triangle, dx, dy, depth, Bounds(triangle)));
            }
        }

        internal sealed class PlantingOutlines(double[] h, double[] v, double[] d, double viewPlaneDepth,
            ElevationShadows? shadows)
        {
            private readonly List<Mesh> _trees = [];
            private readonly List<VisibleFacet> _model = [];

            internal sealed class Mesh(bool clipAtViewPlane)
            {
                internal bool ClipAtViewPlane { get; } = clipAtViewPlane;
                internal readonly List<VisibleFacet> Facets = [];
                internal readonly Dictionary<((long, long, long), (long, long, long)),
                    (double[] A, double[] B, VisibleFacet? Source)> Edges = [];
            }

            internal void AddFacet(Mesh tree, List<double[]> world, List<(double X, double Y)> ring, GeoFace face,
                List<double[]> corners)
            {
                var facet = HasArea(ring) ? CreateVisibleFacet(h, v, d, ring, face, corners) : null;
                if (facet != null) tree.Facets.Add(facet);
                for (int i = 0; i < world.Count; i++)
                {
                    var a = world[i];
                    var b = world[(i + 1) % world.Count];
                    var first = Key(a);
                    var second = Key(b);
                    var key = first.CompareTo(second) < 0 ? (first, second) : (second, first);
                    if (!tree.Edges.TryAdd(key, (a, b, facet))) tree.Edges[key] = default;
                }

                static (long, long, long) Key(double[] point) => ((long)Math.Round(point[0] / PlaneTolerance),
                    (long)Math.Round(point[1] / PlaneTolerance), (long)Math.Round(point[2] / PlaneTolerance));
            }

            internal void AddTree(Mesh tree)
            {
                if (tree.Facets.Count > 0) _trees.Add(tree);
            }

            internal void AddModelOccluder(List<(double X, double Y)> points, double[] normal,
                double[] corner, double depth, List<List<(double X, double Y)>>? holes)
            {
                if (shadows == null)
                    AddOccluder(_model, h, v, d, points, normal, corner, depth, holes);
            }

            internal IEnumerable<CollectedEdge> Project(List<List<(double X, double Y)>> ground)
            {
                if (_trees.Count == 0) yield break;
                var covers = (shadows?._modelOccluders ?? _model).Concat(_trees.SelectMany(tree => tree.Facets))
                    .OrderBy(facet => facet.Bounds.X0).ToList();
                foreach (var tree in _trees)
                foreach (var (a, b, source) in tree.Edges.Values)
                {
                    if (source == null) continue;
                    double start = Dot(a, d) - viewPlaneDepth, end = Dot(b, d) - viewPlaneDepth;
                    if (tree.ClipAtViewPlane && start < 0 && end < 0) continue;
                    double x0 = Dot(a, h), y0 = Dot(a, v), x1 = Dot(b, h), y1 = Dot(b, v);
                    if (tree.ClipAtViewPlane && (start < 0 || end < 0))
                    {
                        double t = start / (start - end);
                        double x = x0 + t * (x1 - x0), y = y0 + t * (y1 - y0);
                        if (start < 0) (x0, y0) = (x, y);
                        else (x1, y1) = (x, y);
                    }
                    var edge = new CollectedEdge(x0, y0, x1, y1, 0, 0, -1, -1, 0, 0, 0, 0,
                        false, (int)BuiltInCategory.OST_Planting);
                    foreach (var span in VisibleTerrainEdges([edge], GroundAndCloser(edge, source), [], coverBoundary: false))
                        yield return span;
                }

                IEnumerable<List<(double X, double Y)>> GroundAndCloser(CollectedEdge edge, VisibleFacet source)
                {
                    foreach (var ring in ground) yield return ring;
                    double left = Math.Min(edge.X1, edge.X2), right = Math.Max(edge.X1, edge.X2);
                    double low = Math.Min(edge.Y1, edge.Y2), high = Math.Max(edge.Y1, edge.Y2);
                    foreach (var cover in covers)
                    {
                        if (cover.Bounds.X0 > right) break;
                        if (ReferenceEquals(source, cover) || cover.Bounds.X1 < left
                            || cover.Bounds.Y0 > high || cover.Bounds.Y1 < low) continue;
                        double Difference((double X, double Y) point) =>
                            source.DepthAt(point) - cover.DepthAt(point) - PlaneTolerance;
                        var mask = ClipHalfPlane(cover.Points, p => Difference(p) > 0, Difference);
                        if (HasArea(mask)) yield return mask;
                    }
                }
            }
        }

        internal VisibleSiteFacets? ClipSiteFacetsToModel(Dictionary<List<(double X, double Y)>, CastShadow>? shadows)
        {
            if ((_planting.Count == 0 && _billboards.Count == 0) || _modelOccluders.Count == 0) return null;
            _modelOccluders.Sort((a, b) => a.Bounds.X0.CompareTo(b.Bounds.X0));
            var sources = new HashSet<List<(double X, double Y)>>();
            var rings = new List<List<(double X, double Y)>>();
            var shade = new List<List<(double X, double Y)>>();
            foreach (var facet in _planting) AddVisible(facet, _modelOccluders);
            if (_billboards.Count > 0)
            {
                var covers = _modelOccluders.Concat(_planting).OrderBy(facet => facet.Bounds.X0).ToList();
                foreach (var facet in _billboards) AddVisible(facet, covers);
            }
            return new(sources, rings, shade, light);

            void AddVisible(VisibleFacet facet, List<VisibleFacet> covers)
            {
                sources.Add(facet.Points);
                var visible = VisibleParts(facet, covers);
                rings.AddRange(visible);
                if (shadows?.GetValueOrDefault(facet.Points) is { } shadow)
                    shade.AddRange(visible.Count == 1 && ReferenceEquals(visible[0], facet.Points)
                        ? shadow.Rings : ClipRings(shadow.Rings, visible));
            }
        }

        private void AddPlanarReceiver(List<(double X, double Y)> points, GeoFace face, List<double[]> corners,
            VisibleFacet? facet, bool soilCut = false, bool terrain = false, bool twoSidedBillboard = false, int? roofId = null,
            bool site = true, bool meshFacet = false, bool receivesOffscreenPlantShadows = false)
        {
            double nx = Dot(face.Normal, h), ny = Dot(face.Normal, v);
            double viewNormal = Dot(face.Normal, d);
            double nz = viewNormal - nx * shiftX - ny * shiftY;
            // A two-sided display face receives direct light only from the side visible in the view.
            if (twoSidedBillboard && facet != null && (viewNormal < 0 ? nz >= -1e-9 : nz <= 1e-9))
            {
                _unlitBillboardFacets.Add(facet);
                return;
            }
            // Foundation mesh facets can receive a verified model shadow from either side.
            if (!meshFacet && (facet == null || terrain) ? !(nz < -1e-9) : Math.Abs(nz) < 1e-9) return;
            // In light coordinates (u, v, depth), the receiving plane has depth A*u + B*v + C.
            var lightRing = corners.Select(point => LightCoordinates(point, 1)).ToList();
            _receivers.Add(new(points, face, -nx / nz, -ny / nz, Dot(face.Normal, corners[0]) / nz,
                lightRing.Max(point => point[2]), site, Bounds(lightRing.Select(point => (point[0], point[1]))),
                facet, soilCut, terrain, RoofId: roofId, MeshFacet: meshFacet,
                ReceivesOffscreenPlantShadows: receivesOffscreenPlantShadows));
        }

        internal void AddCaster(GeoFace face, List<double[]> corners, bool siteOnly = false, bool planting = false,
            bool groundOnly = false, int? roofId = null, bool verifiedOpaque = false,
            bool castsOnVisiblePlanting = false, bool castsOnSoilCut = true)
        {
            if (face.InnerLoops is { Count: > 0 } && face.InnerVertices is not { Count: > 0 }) return;
            var outer = corners.Select(point => LightCoordinates(point, 1)).ToList();
            if (outer.Any(point => !point.All(double.IsFinite))) return;
            var holes = (face.InnerVertices ?? []).Select(ring =>
                ring.Select(point => LightCoordinates(point, FeetToMillimetres)).ToList()).ToList();
            if (holes.Any(ring => ring.Count < 3 || ring.Any(point => !point.All(double.IsFinite)))) return;
            // The foreground faces of the cut's planting projection are the ones facing
            // into the view. Edge-on faces can still project along the light direction.
            bool projectsToSoilCut = planting && castsOnSoilCut && Dot(face.Normal, d) >= -1e-6;
            _casters.Add(new(outer, holes, Bounds(outer.Select(point => (point[0], point[1]))),
                outer.Min(point => point[2]), siteOnly, projectsToSoilCut, groundOnly, roofId, verifiedOpaque,
                castsOnVisiblePlanting));
        }

        internal Dictionary<List<(double X, double Y)>, CastShadow>? Project(double viewDepth)
        {
            var result = new Dictionary<List<(double X, double Y)>, CastShadow>();
            var facets = _visibleFacets.OrderBy(facet => facet.Bounds.X0).ToList();
            _terrainFacets.Sort((a, b) => a.Bounds.X0.CompareTo(b.Bounds.X0));
            var visibleCasters = new List<ShadowCaster>();
            if (facets.Count > 0 || _receivers.Any(receiver => !receiver.Site))
            {
                double[] near = [0, 0, viewDepth], away = [0, 0, 1];
                foreach (var caster in _casters)
                {
                    if (caster.GroundOnly) continue;
                    if (caster.MinDepth >= viewDepth) { visibleCasters.Add(caster); continue; }
                    var outer = ClipBehind(caster.Outer, near, away);
                    if (outer.Count < 3) continue;
                    visibleCasters.Add(caster with
                    {
                        Outer = outer,
                        Holes = caster.Holes.Select(hole => ClipBehind(hole, near, away)).ToList(),
                        Bounds = Bounds(outer.Select(point => (point[0], point[1]))),
                        MinDepth = viewDepth,
                    });
                }
            }
            List<ShadowCaster>? plantingCasters = null;
            foreach (var receiver in _receivers)
            {
                var (face, geometry, a, b, c, maxDepth, site, lightBounds, facet, soilCut, terrain, roundShaft, roofId,
                    meshFacet, receivesOffscreenPlantShadows) = receiver;
                var bounds = Bounds(face);
                double[] limit = [0, 0, c - PlaneTolerance], normal = [a, b, -1];
                var rings = new List<List<(double X, double Y)>>();
                if (roundShaft && shiftX != 0)
                {
                    // Project the point where the horizontal light ray is tangent to the cylinder.
                    double radius = (bounds.X1 - bounds.X0) / 2;
                    double terminator = bounds.X0 + radius - Math.CopySign(radius / Math.Sqrt(1 + shiftX * shiftX), shiftX);
                    var shade = ClipToWindow(face, shiftX > 0 ? bounds.X0 : terminator,
                        shiftX > 0 ? terminator : bounds.X1, bounds.Y0, bounds.Y1);
                    if (PlanPrinter.SignedArea(shade) < 0) shade.Reverse();
                    if (HasArea(shade)) rings.Add(shade);
                }
                var casters = site && (facet == null || terrain) ? _casters
                    : receivesOffscreenPlantShadows
                        ? plantingCasters ??= visibleCasters.Concat(_casters.Where(caster => caster.CastsOnVisiblePlanting)).ToList()
                        : visibleCasters;
                foreach (var caster in casters)
                {
                    if (caster.RoofId != roofId || meshFacet && !caster.VerifiedOpaque
                        || soilCut && !caster.ProjectsToSoilCut || caster.SiteOnly && !site
                        || caster.MinDepth >= maxDepth - PlaneTolerance
                        || caster.Bounds.X0 >= lightBounds.X1 || caster.Bounds.X1 <= lightBounds.X0
                        || caster.Bounds.Y0 >= lightBounds.Y1 || caster.Bounds.Y1 <= lightBounds.Y0) continue;
                    if (!AddRing(caster.Outer, false)) continue;
                    foreach (var hole in caster.Holes) AddRing(hole, true);
                }
                if (rings.Count == 0) continue;
                if (facet != null)
                {
                    rings = ClipToVisibleFacet(rings, facet, terrain ? _terrainFacets : facets);
                    if (rings.Count == 0) continue;
                }
                var openings = (geometry.InnerVertices ?? []).Select(ring => ring.Select(point =>
                    (Dot(point, h) * FeetToMillimetres, Dot(point, v) * FeetToMillimetres)).ToList()).ToList();
                result.Add(face, new CastShadow(light, rings, openings));

                bool AddRing(List<double[]> polygon, bool hole)
                {
                    var ring = ClipBehind(polygon, limit, normal).Select(point =>
                    {
                        double depth = a * point[0] + b * point[1] + c;
                        return (point[0] - depth * shiftX, point[1] - depth * shiftY);
                    }).ToList();
                    ring = ClipToWindow(ring, bounds.X0, bounds.X1, bounds.Y0, bounds.Y1);
                    double area = PlanPrinter.SignedArea(ring);
                    if (Math.Abs(area) <= PlaneTolerance * PlaneTolerance) return false;
                    if ((area > 0) == hole) ring.Reverse();
                    rings.Add(ring);
                    return true;
                }
            }
            foreach (var facet in _unlitBillboardFacets)
            {
                var shade = VisibleParts(facet, facets);
                if (shade.Count > 0) result.Add(facet.Points, new CastShadow(light, shade, []));
            }
            return result.Count > 0 ? result : null;
        }

        private static List<List<(double X, double Y)>> ClipToVisibleFacet(List<List<(double X, double Y)>> rings,
            VisibleFacet facet, List<VisibleFacet> facets)
        {
            return ClipRings(rings, VisibleParts(facet, facets));
        }

        internal Dictionary<List<(double X, double Y)>, List<List<(double X, double Y)>>> ClippedReceivers(
            IReadOnlySet<List<(double X, double Y)>> targets, IReadOnlyList<VisibleFacet> additionalCovers)
        {
            var visible = new Dictionary<List<(double X, double Y)>, List<List<(double X, double Y)>>>();
            if (targets.Count == 0) return visible;
            var facets = _visibleFacets.Concat(additionalCovers).OrderBy(facet => facet.Bounds.X0).ToList();
            foreach (var facet in facets)
            {
                if (!targets.Contains(facet.Points)) continue;
                var parts = VisibleParts(facet, facets);
                if (parts.Count != 1 || !ReferenceEquals(parts[0], facet.Points))
                    visible.Add(facet.Points, parts);
            }
            return visible;
        }

        private static List<List<(double X, double Y)>> VisibleParts(VisibleFacet facet,
            List<VisibleFacet> facets)
        {
            List<List<(double X, double Y)>> visible = [facet.Points];
            // Intersect each covering facet with the half-plane where it is closer to the view.
            foreach (var nearer in facets)
            {
                if (nearer.Bounds.X0 >= facet.Bounds.X1 || visible.Count == 0) break;
                if (ReferenceEquals(facet, nearer) || nearer.Bounds.X1 <= facet.Bounds.X0
                    || nearer.Bounds.Y0 >= facet.Bounds.Y1 || nearer.Bounds.Y1 <= facet.Bounds.Y0) continue;
                double Difference((double X, double Y) point) =>
                    facet.DepthAt(point) - nearer.DepthAt(point) - PlaneTolerance;
                var cover = ClipHalfPlane(nearer.Points, point => Difference(point) > 0, Difference);
                if (!HasArea(cover)) continue;
                var sides = Sides(cover);
                var uncovered = new List<List<(double X, double Y)>>();
                foreach (var polygon in visible)
                {
                    if (!HasArea(ClipConvex(polygon, sides)))
                    {
                        uncovered.Add(polygon);
                        continue;
                    }
                    var inside = polygon;
                    foreach (var (x, y, c) in sides)
                    {
                        double Distance((double X, double Y) p) => x * p.X + y * p.Y + c;
                        var outside = ClipHalfPlane(inside, p => Distance(p) < 0, Distance);
                        if (HasArea(outside)) uncovered.Add(outside);
                        inside = ClipHalfPlane(inside, p => Distance(p) >= 0, Distance);
                    }
                }
                visible = uncovered;
            }
            return visible;
        }

        private static List<List<(double X, double Y)>> ClipRings(List<List<(double X, double Y)>> rings,
            List<List<(double X, double Y)>> visible)
        {
            var result = new List<List<(double X, double Y)>>();
            foreach (var polygon in visible)
            {
                var sides = Sides(polygon);
                foreach (var ring in rings)
                {
                    var clipped = ClipConvex(ring, sides);
                    if (HasArea(clipped)) result.Add(clipped);
                }
            }
            return result;

        }

        private static bool HasArea(List<(double X, double Y)> polygon) =>
            Math.Abs(PlanPrinter.SignedArea(polygon)) > PlaneTolerance * PlaneTolerance;

        private static List<(double X, double Y, double C)> Sides(List<(double X, double Y)> polygon)
        {
            int winding = Math.Sign(PlanPrinter.SignedArea(polygon));
            var sides = new List<(double X, double Y, double C)>(polygon.Count);
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                double x = winding * (a.Y - b.Y), y = winding * (b.X - a.X);
                sides.Add((x, y, -x * a.X - y * a.Y));
            }
            return sides;
        }

        private static List<(double X, double Y)> ClipConvex(List<(double X, double Y)> polygon,
            List<(double X, double Y, double C)> sides)
        {
            foreach (var (x, y, c) in sides)
            {
                double Distance((double X, double Y) p) => x * p.X + y * p.Y + c;
                polygon = ClipHalfPlane(polygon, p => Distance(p) >= 0, Distance);
                if (polygon.Count < 3) break;
            }
            return polygon;
        }

        private double[] LightCoordinates(double[] point, double units)
        {
            double depth = Dot(point, d) * units;
            return [Dot(point, h) * units + depth * shiftX, Dot(point, v) * units + depth * shiftY, depth];
        }

        private static double Dot(double[] point, double[] axis) =>
            point[0] * axis[0] + point[1] * axis[1] + point[2] * axis[2];

        private static (double X0, double Y0, double X1, double Y1) Bounds(IEnumerable<(double X, double Y)> points)
        {
            double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
            double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
            foreach (var (x, y) in points)
            {
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
            return (x0, y0, x1, y1);
        }
    }
}
