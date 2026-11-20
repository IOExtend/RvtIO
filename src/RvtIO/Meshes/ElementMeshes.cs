using RvtIO.BuiltIns;
using RvtIO.Geometry.Meshing;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Meshes;

/// <summary>
/// Triangle meshes of the drawable model elements of an opened document, in feet, in the coordinates of the document (Revit
/// internal coordinates, Z up). The document is not read again: the meshes are made from the decoded faces it holds.
/// </summary>
/// <remarks>
/// <para>An element is drawable when the IFC export of the current profile would consider it a product by these of its rules:
/// it is not a symbol or type definition (a record kept only for its geometry, an element that other elements name as their type
/// and that names no project phase), not a group template, not in a secondary design option,
/// not outside the last project phase (demolished or not yet created), not a reveal, not a rectangular wall opening (a void), not
/// the container of a stacked wall (its member walls are drawn), not a record outside an exact element table, not owned by a
/// family, not of a non-physical category (annotations, rooms, spaces, ...), names its project phase (walls and family instances
/// of a generic category excepted), and not a void helper whose faces are all unfilled. An element whose geometry is exactly
/// other drawable elements placed (a stair of its runs and landings, a model group of its members) is left out, as those
/// elements are listed themselves; a placement of a drawable element is likewise left out of an element's placements. A
/// drawable element is listed when it has a geometry record, or, in a document whose geometry was not decoded, a bounding box.
/// Other product rules of the IFC export are not applied, so the meshes can include elements that the IFC leaves out: the
/// "export to IFC" flag, topography, walls that are not visible, curtain panels that are walls, family instances whose level is
/// not a project level, curtain children without bounds, openings of hidden walls, and the rebar rules.</para>
/// <para>Faces of Geometry nodes that Revit does not take (another detail level) are left out when the element has faces of
/// nodes it takes and no ghost node (a node Revit exports that has no decoded polygon, which also makes the element
/// <see cref="MeshCoverage.Partial"/>). Planar faces with holes are triangulated in their plane; curved surfaces are the dense polygons the reader
/// decoded for them. Every triangle is counter-clockwise around the normal of its face.</para>
/// <para>The material of a face is its own material, else the material of the symbol face it was placed from, else the material
/// of the graphics style of its Geometry node, else none (0). Only elements of the Materials category count as materials.</para>
/// </remarks>
public static class ElementMeshes
{
    private const int MaterialsCategoryId = -2000700;
    private const int RevealsCategoryId = -2000182;
    private const int WallsCategoryId = -2000011;
    private const double RigidTolerance = 1e-6;
    private const double ArcChordErrorFeet = 0.001 / 0.3048;

    /// <summary>The meshes of the drawable elements of <paramref name="document"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static MeshSet Extract(RvtDocument document, ElementMeshOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new ElementMeshOptions();
        var info = document.RawData;
        var drawable = DrawableElements(info, options.IncludeModelLinesAndReinforcement);
        var drawableIds = drawable.Select(item => item.Element.Id).ToHashSet();
        var symbolCache = new Dictionary<int, SymbolMeshes?>();
        var placedSymbols = new Dictionary<int, IReadOnlyList<TriangleMesh>>();

        bool IsMaterial(int id) => document.GetElement(id)?.CategoryId == MaterialsCategoryId;
        int MaterialOf(GeoFace face)
        {
            if (face.RenderStyleId is > 0 and int own && IsMaterial(own)) return own;
            if (face.NestedRenderStyleId is > 0 and int nested && IsMaterial(nested)) return nested;
            if (face.GeometryStyle > 0 && info.GraphicsStyles?.TryGetValue(face.GeometryStyle, out var style) == true
                && style.Material > 0 && IsMaterial(style.Material))
                return style.Material;
            return 0;
        }

        SymbolMeshes? SymbolOf(int symbolId)
        {
            if (symbolCache.TryGetValue(symbolId, out var cached)) return cached;
            SymbolMeshes? symbol = null;
            // The symbol element holds the symbol in the frame of its family; it is the placed symbol only when its own nested
            // symbols were placed whole.
            if (document.GetElement(symbolId)?.Geometry is { PlacedSymbolClipped: false, PlacedSymbolBox: false } geometry)
            {
                bool ghosts = geometry.GhostNodes is { Count: > 0 };
                var (meshes, failed) = Triangulate(geometry.Faces, MaterialOf, ghosts);
                if (meshes.Count > 0)
                    symbol = new SymbolMeshes(meshes, failed + geometry.UntriangulatedFaces, geometry.UntriangulatedFaces,
                        DropsUntakenNodes(geometry.Faces, ghosts), geometry.Faces.Any(face => face.GeometryNode < 0), ghosts);
            }
            symbolCache[symbolId] = symbol;
            return symbol;
        }

        // The placements of an element whose geometry is exactly its symbols placed, or null.
        (List<SymbolPlacement> Placements, int Failed, int UntriangulatedInSymbols, bool Ghosts)? PlacementsOf(GeometryData geometry)
        {
            if (geometry.ExactMappedInstances is not { Count: > 0 } mapped || geometry.PlacedSymbolClipped || geometry.PlacedSymbolBox)
                return null;
            bool dropsUntaken = DropsUntakenNodes(geometry.Faces, geometry.GhostNodes is { Count: > 0 });
            var placements = new List<SymbolPlacement>(mapped.Count);
            int failed = 0;
            int untriangulatedInSymbols = 0;
            bool ghosts = false;
            foreach (var instance in mapped)
            {
                if (drawableIds.Contains(instance.SymbolId)) continue;   // drawn as an element of its own
                if (!ElementBuilder.IsRigidPlacement(instance.Transform)) return null;
                var transform = MeshTransform.FromRowMajor(instance.Transform);
                if (Math.Abs(Math.Abs(transform.Determinant) - 1) > RigidTolerance) return null;
                if (SymbolOf(instance.SymbolId) is not { } symbol) return null;
                // The hidden nodes of the symbol must be dropped in the element exactly when they are in the symbol alone.
                if (symbol.HasHiddenNode && symbol.DropsUntakenNodes != dropsUntaken) return null;
                placements.Add(new SymbolPlacement(instance.SymbolId, transform));
                failed += symbol.Failed;
                untriangulatedInSymbols += symbol.UntriangulatedFaces;
                ghosts |= symbol.HasGhostNodes;
            }
            return placements.Count > 0 ? (placements, failed, untriangulatedInSymbols, ghosts) : null;
        }

        var elements = new List<ElementMesh>();
        foreach (var (element, body) in drawable)
        {
            if (options.IncludeModelLinesAndReinforcement && IsStairContainer(element, body, drawableIds))
            {
                elements.Add(new ElementMesh
                {
                    ElementId = element.Id,
                    ComponentIds = body!.ExactMappedInstances!.Select(instance => instance.SymbolId).Distinct().ToArray(),
                    Coverage = MeshCoverage.Complete,
                });
                continue;
            }
            if (body == null)
            {
                var curve = options.IncludeModelLinesAndReinforcement ? ModelCurve(element) : null;
                var line = ModelLine(curve);
                var arc = curve is { CurveType: "Arc" } && GridAxis.Tessellate([curve], ArcChordErrorFeet) is { } samples
                    ? samples.Select(p => new MeshVector(p[0], p[1], p[2])).ToArray() : null;
                var linePairs = options.IncludeModelLinesAndReinforcement && AreaReinforcementBounds(element) is { } bounds
                    && element.SerializedHostId is int hostId && document.GetElement(hostId)?.Geometry is { } host
                    ? AreaReinforcementOutline(element.AreaReinforcementPlane!, host, bounds) : null;
                var rebar = options.IncludeModelLinesAndReinforcement && element.RebarBars is { } bars ? ReinforcementMeshes.Build(bars) : null;
                elements.Add(new ElementMesh
                {
                    ElementId = element.Id,
                    Line = line,
                    Arc = arc,
                    LinePairs = linePairs,
                    Meshes = rebar == null ? [] : [rebar],
                    Coverage = line != null || arc != null || linePairs != null || rebar != null ? MeshCoverage.Complete
                        : info.WideLayoutNotRewritten ? MeshCoverage.Undecoded : MeshCoverage.NoGeometry,
                });
                continue;
            }
            if (options.UseSymbolPlacements && ReferenceEquals(body, element.Geometry) && PlacementsOf(body) is var (placements, failedInSymbols, untriangulatedInSymbols, symbolGhosts))
            {
                foreach (var placement in placements)
                    placedSymbols.TryAdd(placement.SymbolId, symbolCache[placement.SymbolId]!.Meshes);
                int ownUntriangulated = Math.Max(0, body.UntriangulatedFaces - untriangulatedInSymbols);
                int totalFailed = failedInSymbols + ownUntriangulated;
                elements.Add(new ElementMesh
                {
                    ElementId = element.Id,
                    Placements = placements,
                    Coverage = CoverageOf(true, body, totalFailed, symbolGhosts),
                    UntriangulatedFaces = totalFailed,
                });
                continue;
            }
            var (meshes, failed) = Triangulate(body.Faces, MaterialOf, body.GhostNodes is { Count: > 0 });
            // A face left out of the reader's triangulation of its trim loops (see GeometryData.UntriangulatedFaces).
            failed += body.UntriangulatedFaces;
            elements.Add(new ElementMesh
            {
                ElementId = element.Id,
                Meshes = meshes,
                Coverage = CoverageOf(meshes.Count > 0, body, failed, false),
                UntriangulatedFaces = failed,
            });
        }
        return new MeshSet { Elements = elements, Symbols = placedSymbols, GeometryUndecoded = info.WideLayoutNotRewritten };
    }

    // A ghost node is a Geometry node Revit exports that has no polygon here (display surfaces, or a duplicate of another node).
    private static MeshCoverage CoverageOf(bool drawn, GeometryData geometry, int failed, bool symbolGhosts) =>
        !drawn ? MeshCoverage.NoGeometry
        : geometry.PlacedSymbolBox ? MeshCoverage.BoxSubstitute
        : failed > 0 || geometry.UnresolvedSymbol || symbolGhosts || geometry.GhostNodes is { Count: > 0 } ? MeshCoverage.Partial
        : MeshCoverage.Complete;

    private sealed record SymbolMeshes(IReadOnlyList<TriangleMesh> Meshes, int Failed, int UntriangulatedFaces, bool DropsUntakenNodes, bool HasHiddenNode,
        bool HasGhostNodes);

    /// <summary>Whether the faces of untaken Geometry nodes are left out: when a face of a taken node is there and the geometry has
    /// no ghost node, the rule of the IFC export of the current profile.</summary>
    private static bool DropsUntakenNodes(IReadOnlyList<GeoFace> faces, bool hasGhostNodes) =>
        !hasGhostNodes && faces.Any(face => face.GeometryNode > 0);

    /// <summary>The drawable elements (see <see cref="ElementMeshes"/>) in document order, each with the geometry that is its body:
    /// the geometry of its computed symbol for a void helper that has one, its own geometry otherwise (null in a document whose
    /// geometry was not decoded).</summary>
    internal static List<(Element Element, GeometryData? Body)> DrawableElements(RevitFileInfo info, bool includeModelLinesAndReinforcement = false)
    {
        var designOptions = DesignOptions.Of(info.Elements);
        var phaseFilter = ProductRules.BuildExportPhaseFilter(info, exportPhaseId: null, lastPhaseByDefault: true);
        var typeElementIds = ProductRules.TypeElementIds(info.Elements);
        var elementsById = new Dictionary<int, Element>();
        foreach (var element in info.Elements) elementsById.TryAdd(element.Id, element);
        var drawable = new List<(Element, GeometryData?)>();
        foreach (var element in info.Elements)
        {
            var curve = includeModelLinesAndReinforcement ? ModelCurve(element) : null;
            bool modelLine = curve?.CurveType == "Arc" || ModelLine(curve) != null;
            bool areaReinforcement = includeModelLinesAndReinforcement && AreaReinforcementBounds(element) != null;
            bool rebar = includeModelLinesAndReinforcement && element.RebarBars is { Bars.Count: > 0 } or { Items.Count: > 0 };
            if (element.IsBlockOnly || element.IsGroupTemplate
                || designOptions.InNonPrimaryOption(element) == true || phaseFilter?.Excludes(element) == true
                || element.CategoryId == RevealsCategoryId || ProductRules.IsWallRectangularOpening(element)
                || ProductRules.IsValidatedStackedWallContainer(element, elementsById)
                || info.ElemTableIsExact && element.IsDiscovered
                || element.SerializedFamilyId is > 0
                || (!modelLine && !areaReinforcement && ProductRules.IsNonPhysicalCategory(element.Category)))
                continue;
            bool namesPhase = ProductRules.HasProjectPhaseReference(element) || ProductRules.IsTransformOnlyFamily(element);
            if (!namesPhase && !modelLine && !rebar && !areaReinforcement
                && (typeElementIds.Contains(element.Id) || element.CategoryId != WallsCategoryId)) continue;
            GeometryData? body = element.Geometry;
            if (element.SerializedComputedSymbolGeometry is { } computed) body = computed;
            else if (ProductRules.HasOnlyUnfilledFaces(element)) continue;
            if (body != null || modelLine || rebar || areaReinforcement || info.WideLayoutNotRewritten && element.BoundingBox != null)
                drawable.Add((element, body));
        }
        // A stair places its runs and landings, a model group its members: those are drawn as elements of their own.
        var ids = drawable.Select(item => item.Item1.Id).ToHashSet();
        return drawable.Where(item => item.Item2?.ExactMappedInstances is not { Count: > 0 } mapped
            || !mapped.All(instance => ids.Contains(instance.SymbolId))
            || includeModelLinesAndReinforcement && IsStairContainer(item.Item1, item.Item2, ids)).ToList();
    }

    private static bool IsStairContainer(Element element, GeometryData? body, HashSet<int> drawableIds) =>
        element.CategoryId == (int)BuiltInCategory.OST_Stairs && element.RootClassIs("StairsElement")
        && body?.ExactMappedInstances is { Count: > 0 } mapped
        && mapped.All(instance => instance.SymbolId != element.Id && drawableIds.Contains(instance.SymbolId));

    private static LocationCurve? ModelCurve(Element element)
    {
        if (element.CategoryId is not ((int)BuiltInCategory.OST_Lines or (int)BuiltInCategory.OST_RoomSeparationLines
            or (int)BuiltInCategory.OST_MEPSpaceSeparationLines) || element.OwnerViewId != null
            || !element.RootClassIs("CurveElem")
            || !element.Parameters.Any(p => p.Id == (int)BuiltInParameter.CURVE_IS_DETAIL && p.Value is 0))
            return null;
        return element.NativeModelArc ?? (element.Location is { CurveType: "Line" } line ? line : null);
    }

    private static double[]? AreaReinforcementBounds(Element element) =>
        element.CategoryId == (int)BuiltInCategory.OST_AreaRein && element.RootClassIs("RebarSystem")
        && element.AreaReinforcementPlane != null && element.SerializedHostId is > 0
        && element.BoundingBox is [var x0, var y0, var z0, var x1, var y1, var z1] bounds
        && bounds.All(double.IsFinite) && x0 < x1 && y0 < y1 && z0 < z1
        ? bounds : null;

    private static MeshVector[]? AreaReinforcementOutline(AreaSketchPlane plane, GeometryData host, double[] bounds)
    {
        const double tolerance = 1e-5; // Feet. The source envelope and face coordinates can round independently.
        var direction = Pt3.Cross(Pt3.From(plane.XAxis), Pt3.From(plane.YAxis));
        double length = direction.Norm;
        if (!(length > 0 && double.IsFinite(length))) return null;
        var normal = direction / length;
        var origin = Pt3.From(plane.Origin);
        var faces = host.Faces.Where(face => face.OuterVertices is { Count: >= 3 } loop &&
            loop.All(point => point.Length == 3 && Math.Abs(normal.Dot(Pt3.From(point) - origin)) <= tolerance))
            .Take(2).ToArray();
        if (faces.Length != 1) return null;

        // The host face supplies the outline. The area's own bounds locate its two parallel copies.
        var points = faces[0].OuterVertices!.Select(Pt3.From).ToArray();
        double[] axis = normal.ToArray();
        int dominant = Math.Abs(axis[0]) >= Math.Abs(axis[1]) ? 0 : 1;
        if (Math.Abs(axis[2]) > Math.Abs(axis[dominant])) dominant = 2;
        var profileBounds = Bounds(points);
        double first = (bounds[dominant] - profileBounds[dominant]) / axis[dominant];
        double second = (bounds[dominant + 3] - profileBounds[dominant + 3]) / axis[dominant];
        if (!double.IsFinite(first) || !double.IsFinite(second) || Math.Abs(first - second) <= tolerance) return null;
        var front = points.Select(point => point + normal * first).ToArray();
        var back = points.Select(point => point + normal * second).ToArray();
        if (Bounds(front.Concat(back)).Zip(bounds).Any(pair => Math.Abs(pair.First - pair.Second) > tolerance))
            return null;
        var pairs = new List<MeshVector>(4 * points.Length);
        AddOutline(front);
        AddOutline(back);
        return pairs.ToArray();

        void AddOutline(Pt3[] loop)
        {
            for (int i = 0; i < loop.Length; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Length];
                pairs.Add(new(a.X, a.Y, a.Z));
                pairs.Add(new(b.X, b.Y, b.Z));
            }
        }

        static double[] Bounds(IEnumerable<Pt3> source)
        {
            var bounds = new double[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
                double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (var point in source)
            {
                bounds[0] = Math.Min(bounds[0], point.X);
                bounds[1] = Math.Min(bounds[1], point.Y);
                bounds[2] = Math.Min(bounds[2], point.Z);
                bounds[3] = Math.Max(bounds[3], point.X);
                bounds[4] = Math.Max(bounds[4], point.Y);
                bounds[5] = Math.Max(bounds[5], point.Z);
            }
            return bounds;
        }
    }

    private static (MeshVector Start, MeshVector End)? ModelLine(LocationCurve? curve)
    {
        if (curve is not { CurveType: "Line" } line
            || line.StartPoint is not [var x0, var y0, var z0] || line.EndPoint is not [var x1, var y1, var z1])
            return null;
        var start = new Pt3(x0, y0, z0);
        var end = new Pt3(x1, y1, z1);
        return start.IsFinite && end.IsFinite && (end - start).Norm is > 0 and < double.PositiveInfinity
            ? (new(x0, y0, z0), new(x1, y1, z1)) : null;
    }

    /// <summary>The triangles of <paramref name="faces"/>, one mesh per material in the order the materials first appear, and the
    /// number of faces that could not be triangulated. The faces of untaken Geometry nodes (a negative node id) are left out when
    /// a face of a taken node is there, unless the geometry has ghost nodes (<paramref name="hasGhostNodes"/>).</summary>
    internal static (List<TriangleMesh> Meshes, int Failed) Triangulate(IReadOnlyList<GeoFace> faces, Func<GeoFace, int> materialOf,
        bool hasGhostNodes = false)
    {
        bool dropUntakenNodes = DropsUntakenNodes(faces, hasGhostNodes);
        var builders = new List<MeshBuilder>();
        var builderOfMaterial = new Dictionary<int, MeshBuilder>();
        int failed = 0;
        foreach (var face in faces)
        {
            if (dropUntakenNodes && face.GeometryNode < 0) continue;
            int material = materialOf(face);
            if (!builderOfMaterial.TryGetValue(material, out var builder))
            {
                builder = new MeshBuilder(material);
                builderOfMaterial[material] = builder;
                builders.Add(builder);
            }
            if (!AppendFace(face, builder)) failed++;
        }
        return (builders.Where(builder => builder.Indices.Count > 0).Select(builder => builder.ToMesh()).ToList(), failed);
    }

    /// <summary>Appends the triangles of one polygon face with its holes; false when the face has no polygon, its triangles do not
    /// cover its area, or (for a face with holes) they cover more than its area: a hole that is filled. A face of no area is drawn
    /// as nothing and counts as triangulated.</summary>
    internal static bool AppendFace(GeoFace face, MeshBuilder target)
    {
        if (face.OuterVertices is not { Count: >= 3 } outer) return false;
        var rings = new List<List<double[]>> { outer };
        if (face.InnerVertices != null) rings.AddRange(face.InnerVertices.Where(ring => ring is { Count: >= 3 }));
        foreach (var ring in rings)
            foreach (var point in ring)
                if (point.Length < 3 || !double.IsFinite(point[0]) || !double.IsFinite(point[1]) || !double.IsFinite(point[2]))
                    return false;

        // The plane of the face: the Newell normal of its outer ring (relative to its first corner, so that large coordinates keep
        // their precision), turned to the side of the face normal.
        var origin = outer[0];
        double nx = 0, ny = 0, nz = 0;
        for (int index = 0; index < outer.Count; index++)
        {
            var a = outer[index];
            var b = outer[(index + 1) % outer.Count];
            double ay = a[1] - origin[1], az = a[2] - origin[2], ax = a[0] - origin[0];
            double by = b[1] - origin[1], bz = b[2] - origin[2], bx = b[0] - origin[0];
            nx += (ay - by) * (az + bz);
            ny += (az - bz) * (ax + bx);
            nz += (ax - bx) * (ay + by);
        }
        double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (!(length > 0)) return true;
        nx /= length; ny /= length; nz /= length;
        if (face.Normal is { Length: >= 3 } given && nx * given[0] + ny * given[1] + nz * given[2] < 0)
        {
            nx = -nx; ny = -ny; nz = -nz;
        }

        // In-plane axes u, v with u x v = n.
        double hx = 0, hy = 0, hz = 0;
        if (Math.Abs(nx) <= Math.Abs(ny) && Math.Abs(nx) <= Math.Abs(nz)) hx = 1;
        else if (Math.Abs(ny) <= Math.Abs(nz)) hy = 1;
        else hz = 1;
        double ux = hy * nz - hz * ny, uy = hz * nx - hx * nz, uz = hx * ny - hy * nx;
        double uLength = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        ux /= uLength; uy /= uLength; uz /= uLength;
        double vx = ny * uz - nz * uy, vy = nz * ux - nx * uz, vz = nx * uy - ny * ux;

        var points = new List<(double X, double Y)>();
        var loops = new List<int[]>(rings.Count);
        var outerFlags = new bool[rings.Count];
        double expectedArea = 0;
        for (int ringIndex = 0; ringIndex < rings.Count; ringIndex++)
        {
            var ring = rings[ringIndex];
            var loop = new int[ring.Count];
            var projected = new (double X, double Y)[ring.Count];
            for (int index = 0; index < ring.Count; index++)
            {
                double dx = ring[index][0] - origin[0], dy = ring[index][1] - origin[1], dz = ring[index][2] - origin[2];
                projected[index] = (dx * ux + dy * uy + dz * uz, dx * vx + dy * vy + dz * vz);
                loop[index] = points.Count;
                points.Add(projected[index]);
            }
            loops.Add(loop);
            outerFlags[ringIndex] = ringIndex == 0;
            double ringArea = Math.Abs(GeTess.SignedArea(projected));
            expectedArea += ringIndex == 0 ? ringArea : -ringArea;
        }
        double outerArea = Math.Abs(GeTess.SignedArea(loops[0].Select(index => points[index]).ToList()));

        var triangles = GeTess.TriangulateProfile(points, loops, outerFlags);
        double coveredArea = 0;
        if (triangles.Count > 0)
        {
            int first = target.VertexCount;
            foreach (var ring in rings)
                foreach (var point in ring)
                    target.AddVertex(point, nx, ny, nz);
            foreach (var (a, b, c) in triangles)
            {
                var (pa, pb, pc) = (points[a], points[b], points[c]);
                double cross = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
                coveredArea += Math.Abs(cross) / 2;
                // Counter-clockwise in (u, v) is counter-clockwise around n.
                if (cross < 0) target.AddTriangle(first + a, first + c, first + b);
                else target.AddTriangle(first + a, first + b, first + c);
            }
        }
        // Without holes, more area than the outline is a fold of a polygon that is not quite planar (the dense polygons of a curved
        // surface), which still covers the face.
        double tolerance = 1e-6 * outerArea + 1e-12;
        return coveredArea >= expectedArea - tolerance && (rings.Count == 1 || coveredArea <= expectedArea + tolerance);
    }

    /// <summary>The vertices and triangles of one material.</summary>
    internal sealed class MeshBuilder(int materialId)
    {
        private readonly List<double> _positions = [];
        private readonly List<double> _normals = [];

        public List<int> Indices { get; } = [];

        public int VertexCount => _positions.Count / 3;

        public void AddVertex(double[] point, double nx, double ny, double nz)
        {
            _positions.Add(point[0]); _positions.Add(point[1]); _positions.Add(point[2]);
            _normals.Add(nx); _normals.Add(ny); _normals.Add(nz);
        }

        public void AddTriangle(int a, int b, int c)
        {
            Indices.Add(a); Indices.Add(b); Indices.Add(c);
        }

        public TriangleMesh ToMesh() => new()
        {
            Positions = _positions.ToArray(),
            Indices = Indices.ToArray(),
            Normals = _normals.ToArray(),
            MaterialId = materialId,
        };
    }
}
