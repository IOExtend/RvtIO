using System.Runtime.CompilerServices;
using RvtIO.Internal;
using RvtIO.Lookup;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    /// <summary>
    /// Family instance (GInstance GRep): replace the element's placeholder geometry with the
    /// SYMBOL's mesh placed by the instance transform (IfcMappedItem architecture); symbols may
    /// nest (compose transforms up to 4 levels).
    /// </summary>
    private static void ExpandInstanceGeometry(Element elem, GRepData grep, int typeId, ParsingState state)
    {
        if (grep.Instances.Count == 0) return;
        elem.Geometry ??= new GeometryData();
        int ownFaceCount = elem.Geometry.Faces.Count;
        var expandedTopLevel = new List<GeoInstance>();
        var placedBox = new StrongBox<bool>();
        var unresolvedNested = new StrongBox<bool>();
        foreach (var instance in grep.Instances)
        {
            var placed = BuildPlacedSymbolGeometry(
                instance.SymbolElemId, instance.Transform, state, new HashSet<int>(), placedBox: placedBox,
                unresolvedNested: unresolvedNested);
            if (placed == null)
            {
                elem.Geometry.UnresolvedSymbol = true;
                LogUnresolvedSymbol(elem, instance.SymbolElemId, "no-geometry", state);
                continue;
            }
            if (!state.IncludeNonVisibleQuantities)
            {
                placed = ClipGeometryToBounds(placed, grep.BoundingBox, out bool clipped);
                if (clipped) elem.Geometry.PlacedSymbolClipped = true;
            }
            if (placed == null) { LogUnresolvedSymbol(elem, instance.SymbolElemId, "clipped-away", state); continue; }
            AppendGeometry(elem.Geometry, placed, instance.NodeOrder);
            expandedTopLevel.Add(new GeoInstance
            {
                SymbolId = instance.SymbolElemId,
                Transform = (double[])instance.Transform.Clone(),
            });
        }
        DeduplicateExactFaces(elem.Geometry);
        if (placedBox.Value) elem.Geometry.PlacedSymbolBox = true;
        if (unresolvedNested.Value) elem.Geometry.UnresolvedSymbol = true;
        if (ownFaceCount == 0 && expandedTopLevel.Count > 0)
            elem.Geometry.ExactMappedInstances = expandedTopLevel;
    }

    /// <summary>
    /// Some FamilyInstance records carry their placement transform but no instance GRep block. In
    /// that layout the mesh is owned directly by SymbolId/typeId.
    /// </summary>
    private static void ExpandTransformOnlyInstanceGeometry(
        Element elem, uint elemId, int typeId, ParsingState state)
    {
        if ((elem.Geometry?.Faces?.Count ?? 0) >= 3) return;
        if (!state.FamilyInstanceTransforms.TryGetValue(elemId, out var fi)) return;
        if (fi.SymbolId <= 0 || fi.SymbolId == elem.Id) return;
        var placedBox = new StrongBox<bool>();
        var unresolvedNested = new StrongBox<bool>();
        var placed = BuildPlacedSymbolGeometry(fi.SymbolId, fi.Trf, state, new HashSet<int>(), placedBox: placedBox,
            unresolvedNested: unresolvedNested);
        if (placed == null)
        {
            if (elem.Geometry != null) elem.Geometry.UnresolvedSymbol = true;
            LogUnresolvedSymbol(elem, fi.SymbolId, "transform-only", state);
            return;
        }
        elem.Geometry ??= new GeometryData();
        bool hadOwnFaces = elem.Geometry.Faces.Count > 0;
        AppendGeometry(elem.Geometry, placed);
        if (placedBox.Value) elem.Geometry.PlacedSymbolBox = true;
        if (unresolvedNested.Value) elem.Geometry.UnresolvedSymbol = true;
        elem.Geometry.Instances ??= new List<GeoInstance>();
        elem.Geometry.Instances.Add(new GeoInstance
        {
            SymbolId = fi.SymbolId,
            Transform = (double[])fi.Trf.Clone(),
        });
        if (!hadOwnFaces)
            elem.Geometry.ExactMappedInstances =
            [new GeoInstance { SymbolId = fi.SymbolId, Transform = (double[])fi.Trf.Clone() }];
        // The tight box of the placed symbol, or the one the element's own GRep stores (a
        // transform-only instance of a symbol-less family keeps its record's own box).
        elem.Geometry.BoundingBox = state.GrepBlocks.TryGetValue(elemId, out var ownGrep) && ownGrep.BoundingBox != null
            ? (double[])ownGrep.BoundingBox.Clone()
            : placed.BoundingBox;
    }

    /// <summary>
    /// An instance whose own faces are all unfilled (a void helper: the box a louver family cuts)
    /// while its computed symbol (the symbol of its own InstanceInfo) has filled faces: the
    /// symbol's geometry placed by that InstanceInfo's transform is what the current profile
    /// exports.
    /// </summary>
    internal static void AttachComputedSymbolGeometry(Element elem, uint elemId, ParsingState state)
    {
        elem.SerializedComputedSymbolGeometry = null;
        if (!ProductRules.HasOnlyUnfilledFaces(elem)
            || !state.ComputedFamilySymbols.TryGetValue(elemId, out var own)
            || own.BlockCount != 1 || own.Symbol is not { SymbolId: > 0, Transform: { Length: 12 } transform } symbol
            || symbol.SymbolId == elem.Id
            || !state.ComputedSymbolTargetClasses.TryGetValue((uint)symbol.SymbolId, out var target)
            || target is not { BlockCount: 1, ClassId: 1829 }
            || !state.GrepBlocks.TryGetValue((uint)symbol.SymbolId, out var symbolGrep)
            || !symbolGrep.Faces.Any(face => face.GeometryNode > 0 && face.GInfoFlags is uint flags && (flags & ProductRules.FaceFillFlag) != 0))
            return;
        var unresolvedNested = new StrongBox<bool>();
        var placed = BuildPlacedSymbolGeometry(symbol.SymbolId, transform, state, new HashSet<int>(),
            unresolvedNested: unresolvedNested);
        if (placed?.Faces is not { Count: > 0 }) return;
        placed.UnresolvedSymbol = unresolvedNested.Value;
        placed.ExactMappedInstances = [new GeoInstance { SymbolId = symbol.SymbolId, Transform = (double[])transform.Clone() }];
        // The placed symbol carries the tight box of the instance's decoded geometry; the header
        // outline on Element.BoundingBox is the API envelope.
        if (GeometryBox(elem) is { Length: >= 6 } tightBox) placed.BoundingBox = (double[])tightBox.Clone();
        elem.SerializedComputedSymbolGeometry = placed;
    }

    /// <summary>
    /// RVTIO_SYMBOL_DIAG=&lt;file&gt;: one line per GInstance whose symbol produced no placed
    /// geometry, with the state of the symbol's GRep block (absent, or its face and nested-instance
    /// counts).
    /// </summary>
    private static readonly string? SymbolDiagPath =
        Environment.GetEnvironmentVariable("RVTIO_SYMBOL_DIAG") is { Length: > 0 } symbolDiag ? symbolDiag : null;

    private static void LogUnresolvedSymbol(Element elem, int symbolId, string reason, ParsingState state)
    {
        if (SymbolDiagPath == null) return;
        string block = state.GrepBlocks.TryGetValue((uint)symbolId, out var symbol)
            ? $"present faces={symbol.Faces.Count} instances={symbol.Instances.Count} "
                + $"box=[{string.Join(',', symbol.BoundingBox ?? [])}]"
            : "absent";
        File.AppendAllText(SymbolDiagPath,
            $"instance={elem.Id} symbol={symbolId} reason={reason} symbolElement={(state.Records.ContainsKey((uint)symbolId) ? 1 : 0)} "
            + $"inElemTable={(state.ElemTable.ContainsKey((uint)symbolId) ? 1 : 0)} grep={block}\n");
    }

    /// <summary>
    /// Faces above which a component placed at a rescaled placement is replaced by its serialized
    /// tight box.
    /// </summary>
    internal const int ScaledComponentFaceBudget = 20_000;

    /// <summary>
    /// True for a placement scaled outside the near-unit range the parser accepted before uniform
    /// scales were admitted (squared row length within 0.5.. 2), so nothing placed as before is
    /// affected.
    /// </summary>
    internal static bool IsRescaledPlacement(double[] transform)
    {
        if (transform.Length < 3) return false;
        double length2 = transform[0] * transform[0] + transform[1] * transform[1] + transform[2] * transform[2];
        return length2 <= 0.5 || length2 >= 2;
    }

    /// <summary>
    /// The tight box of a component, placed by <paramref name="transform"/>, as one closed six-face
    /// solid with outward normals; null when the box is missing or degenerate.
    /// </summary>
    internal static GeometryData? BuildPlacedBox(double[]? box, double[] transform,
        int? renderStyleId = null, int geometryStyle = -1)
    {
        if (box is not { Length: >= 6 } || transform.Length < 12
            || box.Take(6).Concat(transform.Take(12)).Any(value => !double.IsFinite(value))
            || box[3] - box[0] <= 1e-9 || box[4] - box[1] <= 1e-9 || box[5] - box[2] <= 1e-9)
            return null;
        double[] Corner(int x, int y, int z) => TransformGeometryPoint(
            [x == 0 ? box[0] : box[3], y == 0 ? box[1] : box[4], z == 0 ? box[2] : box[5]], transform);
        var corners = new[]
        {
            Corner(0, 0, 0), Corner(1, 0, 0), Corner(1, 1, 0), Corner(0, 1, 0),
            Corner(0, 0, 1), Corner(1, 0, 1), Corner(1, 1, 1), Corner(0, 1, 1),
        };
        // Counter-clockwise seen from outside for a placement that keeps handedness.
        int[][] quads = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [3, 7, 6, 2], [0, 4, 7, 3], [1, 2, 6, 5]];
        bool mirrored = transform[0] * (transform[4] * transform[8] - transform[5] * transform[7])
            - transform[1] * (transform[3] * transform[8] - transform[5] * transform[6])
            + transform[2] * (transform[3] * transform[7] - transform[4] * transform[6]) < 0;
        var geometry = new GeometryData { Faces = new List<GeoFace>() };
        foreach (var quad in quads)
        {
            var ring = quad.Select(index => corners[index]).ToList();
            if (mirrored) ring.Reverse();
            double[] u = [ring[1][0] - ring[0][0], ring[1][1] - ring[0][1], ring[1][2] - ring[0][2]];
            double[] v = [ring[3][0] - ring[0][0], ring[3][1] - ring[0][1], ring[3][2] - ring[0][2]];
            double uLength = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]);
            double vLength = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            double[] xAxis = [u[0] / uLength, u[1] / uLength, u[2] / uLength];
            double[] yAxis = [v[0] / vLength, v[1] / vLength, v[2] / vLength];
            geometry.Faces.Add(new GeoFace
            {
                OuterVertices = ring,
                Origin = ring[0], XVector = xAxis, YVector = yAxis,
                Normal =
                [
                    xAxis[1] * yAxis[2] - xAxis[2] * yAxis[1],
                    xAxis[2] * yAxis[0] - xAxis[0] * yAxis[2],
                    xAxis[0] * yAxis[1] - xAxis[1] * yAxis[0],
                ],
                Envelope = [0, 0, uLength, vLength],
                Area = uLength * vLength,
                NestedRenderStyleId = renderStyleId,
                GeometryStyle = geometryStyle,
            });
        }
        geometry.BrepSolids = [new GeoBrepSolid { FaceIndices = [0, 1, 2, 3, 4, 5], IsClosed = true }];
        SetGeometryBoundsFromFaces(geometry);
        return geometry;
    }

    private static double[] TransformGeometryPoint(double[] point, double[] t) =>
    [
        t[9] + point[0] * t[0] + point[1] * t[1] + point[2] * t[2],
        t[10] + point[0] * t[3] + point[1] * t[4] + point[2] * t[5],
        t[11] + point[0] * t[6] + point[1] * t[7] + point[2] * t[8],
    ];

    /// <param name="placedBox">Set to true when this symbol or a nested one is replaced by its tight box.</param>
    /// <param name="unresolvedNested">Set to true when a symbol nested in this one, at any depth, placed nothing.</param>
    internal static GeometryData? BuildPlacedSymbolGeometry(
        int symbolId, double[] transform, ParsingState state, HashSet<int> path,
        int scaledComponentFaceBudget = ScaledComponentFaceBudget, StrongBox<bool>? placedBox = null,
        StrongBox<bool>? unresolvedNested = null)
    {
        if (symbolId <= 0 || transform.Length < 12 || !path.Add(symbolId)) return null;
        try
        {
            if (!state.GrepBlocks.TryGetValue((uint)symbolId, out var symbol)) return null;
            if (state.IncludeNonVisibleQuantities && symbol.CoarseQuantities != null) symbol = symbol.CoarseQuantities;
            else if (state.CoarseView && symbol.Coarse != null) symbol = symbol.Coarse;
            else if (state.DetailView == 2 && symbol.Medium != null) symbol = symbol.Medium;
            if (symbol.Faces.Count > scaledComponentFaceBudget && IsRescaledPlacement(transform))
            {
                // The most frequent face material and graphics style of the replaced component (the
                // smaller id on a tie).
                static int MostFrequent(IEnumerable<int> values) => values.GroupBy(value => value)
                    .OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
                    .Select(group => group.Key).FirstOrDefault(-1);
                int material = MostFrequent(symbol.Faces.Select(face => face.RenderStyleId ?? 0).Where(id => id > 0));
                int style = MostFrequent(symbol.Faces.Select(face => face.GeometryStyle).Where(id => id > 0));
                if (placedBox != null) placedBox.Value = true;
                return BuildPlacedBox(symbol.BoundingBox, transform, material > 0 ? material : null, style);
            }
            var result = new GeometryData();

            var own = BuildGeometryData(symbol);
            if (own?.Faces is { Count: > 0 })
            {
                own.Instances = null;
                var placedOwn = TransformGeometry(own, transform);
                if (placedOwn != null) AppendGeometry(result, placedOwn);
            }

            foreach (var child in symbol.Instances)
            {
                var childTransform = ComposeTransforms(transform, child.Transform);
                var placedChild = BuildPlacedSymbolGeometry(
                    child.SymbolElemId, childTransform, state, path, scaledComponentFaceBudget, placedBox, unresolvedNested);
                if (placedChild != null) AppendGeometry(result, placedChild, child.NodeOrder);
                else if (unresolvedNested != null) unresolvedNested.Value = true;
            }

            if (result.Faces.Count == 0) return null;
            SetGeometryBoundsFromFaces(result);
            return result;
        }
        finally
        {
            path.Remove(symbolId);
        }
    }

    private static void SetGeometryBoundsFromFaces(GeometryData geometry)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        bool found = false;
        foreach (var face in geometry.Faces)
        {
            foreach (var vertex in face.OuterVertices ?? Enumerable.Empty<double[]>())
            {
                if (vertex.Length < 3 || !double.IsFinite(vertex[0])
                    || !double.IsFinite(vertex[1]) || !double.IsFinite(vertex[2])) continue;
                minX = Math.Min(minX, vertex[0]); maxX = Math.Max(maxX, vertex[0]);
                minY = Math.Min(minY, vertex[1]); maxY = Math.Max(maxY, vertex[1]);
                minZ = Math.Min(minZ, vertex[2]); maxZ = Math.Max(maxZ, vertex[2]);
                found = true;
            }
        }
        geometry.BoundingBox = found ? [minX, minY, minZ, maxX, maxY, maxZ] : null;
    }

    /// <summary>
    /// Removes only bit-identical polygon faces after nested GInstance expansion. RVT can serialize
    /// the resolved face beside the same face from its symbol; retaining both creates coincident
    /// IFC shells.
    /// </summary>
    internal static void DeduplicateExactFaces(GeometryData geometry)
    {
        if (geometry.Faces.Count < 2) return;

        var faceByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var oldToNew = new int[geometry.Faces.Count];
        var uniqueFaces = new List<GeoFace>(geometry.Faces.Count);
        for (int oldIndex = 0; oldIndex < geometry.Faces.Count; oldIndex++)
        {
            var face = geometry.Faces[oldIndex];
            string key = ExactFaceKey(face);
            if (!faceByKey.TryGetValue(key, out int newIndex))
            {
                newIndex = uniqueFaces.Count;
                uniqueFaces.Add(face);
                faceByKey[key] = newIndex;
            }
            oldToNew[oldIndex] = newIndex;
        }
        if (uniqueFaces.Count == geometry.Faces.Count) return;
        geometry.Faces = uniqueFaces;

        if (geometry.BrepSolids is not { Count: > 0 }) return;
        var solidKeys = new HashSet<string>(StringComparer.Ordinal);
        var uniqueSolids = new List<GeoBrepSolid>();
        foreach (var solid in geometry.BrepSolids)
        {
            var mapped = solid.FaceIndices
                .Where(index => (uint)index < oldToNew.Length)
                .Select(index => oldToNew[index]).Distinct().OrderBy(index => index).ToList();
            if (mapped.Count < 4) continue;
            string key = $"{solid.IsClosed}:{string.Join(',', mapped)}";
            if (solidKeys.Add(key))
                uniqueSolids.Add(new GeoBrepSolid { FaceIndices = mapped, IsClosed = solid.IsClosed });
        }
        geometry.BrepSolids = uniqueSolids.Count > 0 ? uniqueSolids : null;
    }

    internal static string ExactFaceKey(GeoFace face)
    {
        static string PointKey(double[] point)
        {
            long x = point.Length > 0 ? BitConverter.DoubleToInt64Bits(point[0]) : 0;
            long y = point.Length > 1 ? BitConverter.DoubleToInt64Bits(point[1]) : 0;
            long z = point.Length > 2 ? BitConverter.DoubleToInt64Bits(point[2]) : 0;
            return $"{x:X16}{y:X16}{z:X16}";
        }

        static string RingKey(IEnumerable<double[]> ring)
            => string.Join(',', ring.Select(PointKey).OrderBy(value => value, StringComparer.Ordinal));

        string outer = face.OuterVertices is { Count: > 0 }
            ? RingKey(face.OuterVertices)
            : $"O:{PointKey(face.Origin)}|X:{PointKey(face.XVector)}|Y:{PointKey(face.YVector)}"
                + $"|E:{string.Join(',', (face.Envelope ?? []).Select(value => BitConverter.DoubleToInt64Bits(value).ToString("X16")))}";
        string inner = face.InnerVertices is { Count: > 0 }
            ? string.Join('|', face.InnerVertices.Select(RingKey)
                .OrderBy(value => value, StringComparer.Ordinal))
            : "";
        return $"{outer}#{inner}";
    }

    /// <summary>Compose two GInstance placements: outer o inner (apply inner first).</summary>
    private static double[] ComposeTransforms(double[] o, double[] i)
    {
        // rows: basisX(0..2), basisY(3..5), basisZ(6..8), origin(9..11); world = O + x*bX + y*bY +
        // z*bZ
        var r = new double[12];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
                r[row * 3 + col] = o[row * 3] * i[col]
                    + o[row * 3 + 1] * i[3 + col]
                    + o[row * 3 + 2] * i[6 + col];
            r[9 + row] = o[9 + row] + o[row * 3] * i[9]
                + o[row * 3 + 1] * i[10] + o[row * 3 + 2] * i[11];
        }
        return r;
    }

    /// <summary>
    /// A placement that keeps lengths and angles: orthonormal rows (a rotation, or a mirror image
    /// when the determinant is negative).
    /// </summary>
    internal static bool IsRigidPlacement(double[] t)
    {
        if (t.Length < 12 || t.Any(v => !double.IsFinite(v))) return false;
        for (int a = 0; a < 3; a++)
            for (int b = a; b < 3; b++)
            {
                double dot = t[a * 3] * t[b * 3] + t[a * 3 + 1] * t[b * 3 + 1] + t[a * 3 + 2] * t[b * 3 + 2];
                if (Math.Abs(dot - (a == b ? 1.0 : 0.0)) > 1e-6) return false;
            }
        return true;
    }

    /// <summary>
    /// Apply a GInstance placement (rows: basisX, basisY, basisZ, origin - 12 doubles) to a
    /// symbol's geometry, producing world-space faces for the instance.
    /// </summary>
    private static GeometryData? TransformGeometry(GeometryData sym, double[] t)
    {
        if (t.Length < 12) return null;
        double[] Map(double[] p)
        {
            double x = p[0], y = p[1], z = p.Length > 2 ? p[2] : 0;
            return new[]
            {
                t[9]  + x * t[0] + y * t[1] + z * t[2],
                t[10] + x * t[3] + y * t[4] + z * t[5],
                t[11] + x * t[6] + y * t[7] + z * t[8],
            };
        }
        var outGeo = new GeometryData { Faces = new List<GeoFace>() };
        var faceMap = new Dictionary<int, int>();
        double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue;
        double maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
        for (int sourceFaceIndex = 0; sourceFaceIndex < sym.Faces.Count; sourceFaceIndex++)
        {
            var f = sym.Faces[sourceFaceIndex];
            if (f.OuterVertices is not { Count: >= 3 } ov) continue;
            var verts = new List<double[]>(ov.Count);
            foreach (var v in ov)
            {
                var w = Map(v);
                if (!double.IsFinite(w[0]) || !double.IsFinite(w[1]) || !double.IsFinite(w[2])) { verts = null; break; }
                verts.Add(w);
                minx = Math.Min(minx, w[0]); maxx = Math.Max(maxx, w[0]);
                miny = Math.Min(miny, w[1]); maxy = Math.Max(maxy, w[1]);
                minz = Math.Min(minz, w[2]); maxz = Math.Max(maxz, w[2]);
            }
            if (verts == null) continue;
            var n = Map(new[] { f.Normal[0], f.Normal[1], f.Normal[2] });
            var n0 = Map(new[] { 0.0, 0.0, 0.0 });
            var nd = new[] { n[0] - n0[0], n[1] - n0[1], n[2] - n0[2] };
            List<List<double[]>>? inner = null;
            if (f.InnerVertices is { Count: > 0 })
                inner = f.InnerVertices
                    .Select(loop => loop.Select(Map).ToList())
                    .Where(loop => loop.Count >= 3).ToList();
            int targetFaceIndex = outGeo.Faces.Count;
            outGeo.Faces.Add(new GeoFace
            {
                OuterVertices = verts,
                InnerVertices = inner is { Count: > 0 } ? inner : null,
                Normal = nd,
                Origin = verts[0],
                Area = f.Area,
                PlaneSurface = f.PlaneSurface,
                NestedRenderStyleId = f.NestedRenderStyleId ?? f.RenderStyleId,
                GeometryNode = f.GeometryNode,
                NodeOrder = f.NodeOrder,
                GeometryStyle = f.GeometryStyle,
                MeshMarker = f.SourceFace != 0 ? f.SourceFace : f.MeshMarker,
                QuadratureArea = IsRigidPlacement(t) ? f.QuadratureArea : null,
            });
            faceMap[sourceFaceIndex] = targetFaceIndex;
        }
        if (sym.Instances is { Count: > 0 })
            outGeo.Instances = sym.Instances.Select(instance => new GeoInstance
            {
                SymbolId = instance.SymbolId,
                Transform = ComposeTransforms(t, instance.Transform),
                NodeOrder = instance.NodeOrder,
            }).ToList();
        if (outGeo.Faces.Count == 0 && outGeo.Instances is not { Count: > 0 }) return null;
        // the Revit face meshes of the nodes are made in the frame of the symbol and placed
        // with the transform of the instance (a rotation or a mirror image; a scaled instance keeps
        // the polygons)
        if (sym.MeshNodes is { Count: > 0 } && !IsRigidPlacement(t) && Environment.GetEnvironmentVariable("RVTIO_MESH_LOG") == "1")
            DiagnosticSink.WriteLine($"[meshmesh] {sym.MeshNodes.Count} nodes not placed: the transform is scaled ({string.Join(",", t.Select(v => v.ToString("G4")))})");
        if (sym.MeshNodes is { Count: > 0 } && IsRigidPlacement(t))
            outGeo.MeshNodes = sym.MeshNodes.Select(node => node with { Placement = node.Placement == null ? (double[])t.Clone() : ComposeTransforms(t, node.Placement) }).ToList();
        // area and volume are the same in a rigidly placed frame; a scaled one has none
        if (sym.NodeMeasures is { Count: > 0 } && IsRigidPlacement(t)) outGeo.NodeMeasures = sym.NodeMeasures;
        if (sym.BrepSolids is { Count: > 0 })
        {
            outGeo.BrepSolids = new();
            foreach (var solid in sym.BrepSolids)
            {
                var mapped = solid.FaceIndices
                    .Where(faceMap.ContainsKey).Select(i => faceMap[i]).Distinct().ToList();
                if (mapped.Count >= 4)
                    outGeo.BrepSolids.Add(new GeoBrepSolid { FaceIndices = mapped, IsClosed = solid.IsClosed });
            }
            if (outGeo.BrepSolids.Count == 0) outGeo.BrepSolids = null;
        }
        if (outGeo.Faces.Count > 0)
            outGeo.BoundingBox = new[] { minx, miny, minz, maxx, maxy, maxz };
        return outGeo;
    }

    /// <summary>
    /// Intersects placed symbol polygons with the owning GRep's serialized m_tightbBox. Embedded
    /// symbol overrides can shorten the type geometry; using the declared box prevents the
    /// unmodified symbol from leaking past the occurrence.
    /// </summary>
    internal static GeometryData? ClipGeometryToBounds(GeometryData source, double[]? bounds) =>
        ClipGeometryToBounds(source, bounds, out _);

    /// <param name="changed">True when the result is not the faces of <paramref name="source"/> as they are: a face was cut
    /// or left out, or nothing is left (null).</param>
    internal static GeometryData? ClipGeometryToBounds(GeometryData source, double[]? bounds, out bool changed)
    {
        changed = false;
        if (bounds is not { Length: 6 }
            || bounds.Any(value => !double.IsFinite(value))
            || bounds[0] > bounds[3] || bounds[1] > bounds[4] || bounds[2] > bounds[5])
            return source;

        static bool Inside(double[] point, double[] box)
            => point.Length >= 3
                && point[0] >= box[0] && point[0] <= box[3]
                && point[1] >= box[1] && point[1] <= box[4]
                && point[2] >= box[2] && point[2] <= box[5];

        foreach (var face in source.Faces)
            if (face.InnerVertices is { Count: > 0 }
                && (face.OuterVertices ?? []).Concat(face.InnerVertices.SelectMany(ring => ring))
                    .Any(point => !Inside(point, bounds)))
                return source;

        static List<double[]> ClipPlane(
            List<double[]> polygon, int axis, double limit, bool keepGreater, ref bool changed)
        {
            if (polygon.Count == 0) return polygon;
            bool IsInside(double[] point) => keepGreater ? point[axis] >= limit : point[axis] <= limit;
            double[] Intersect(double[] a, double[] b)
            {
                double denominator = b[axis] - a[axis];
                double t = denominator == 0 ? 0 : (limit - a[axis]) / denominator;
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
            var previous = polygon[^1];
            bool previousInside = IsInside(previous);
            foreach (var current in polygon)
            {
                bool currentInside = IsInside(current);
                if (currentInside)
                {
                    if (!previousInside) { output.Add(Intersect(previous, current)); changed = true; }
                    output.Add(current);
                }
                else
                {
                    changed = true;
                    if (previousInside) output.Add(Intersect(previous, current));
                }
                previous = current;
                previousInside = currentInside;
            }
            for (int index = output.Count - 1; index > 0; index--)
                if (output[index].SequenceEqual(output[index - 1])) output.RemoveAt(index);
            if (output.Count > 1 && output[0].SequenceEqual(output[^1])) output.RemoveAt(output.Count - 1);
            return output;
        }

        var result = new GeometryData
        {
            GStyleElemId = source.GStyleElemId,
            Flags = source.Flags,
            BoundingBox = (double[])bounds.Clone(),
            Transform = source.Transform is null ? null : (double[])source.Transform.Clone(),
            Instances = source.Instances,
            ExactMappedInstances = source.ExactMappedInstances,
            Solids = source.Solids,
            MeshNodes = source.MeshNodes,
        };
        var oldToNew = new Dictionary<int, int>();
        var clippedFaces = new HashSet<int>();
        for (int oldIndex = 0; oldIndex < source.Faces.Count; oldIndex++)
        {
            var face = source.Faces[oldIndex];
            if (face.OuterVertices is not { Count: >= 3 } outer) { changed = true; continue; }
            bool faceChanged = false;
            var polygon = outer.Select(point => (double[])point.Clone()).ToList();
            polygon = ClipPlane(polygon, 0, bounds[0], keepGreater: true, ref faceChanged);
            polygon = ClipPlane(polygon, 0, bounds[3], keepGreater: false, ref faceChanged);
            polygon = ClipPlane(polygon, 1, bounds[1], keepGreater: true, ref faceChanged);
            polygon = ClipPlane(polygon, 1, bounds[4], keepGreater: false, ref faceChanged);
            polygon = ClipPlane(polygon, 2, bounds[2], keepGreater: true, ref faceChanged);
            polygon = ClipPlane(polygon, 2, bounds[5], keepGreater: false, ref faceChanged);
            if (faceChanged) changed = true;
            if (polygon.Count < 3) continue;

            int newIndex = result.Faces.Count;
            result.Faces.Add(new GeoFace
            {
                Origin = polygon[0],
                XVector = (double[])face.XVector.Clone(),
                YVector = (double[])face.YVector.Clone(),
                Normal = (double[])face.Normal.Clone(),
                OuterVertices = polygon,
                InnerVertices = face.InnerVertices?.Select(ring =>
                    ring.Select(point => (double[])point.Clone()).ToList()).ToList(),
                Area = face.Area,
                PlaneSurface = face.PlaneSurface,
                NestedRenderStyleId = face.NestedRenderStyleId ?? face.RenderStyleId,
                GeometryNode = face.GeometryNode,
                NodeOrder = face.NodeOrder,
                GeometryStyle = face.GeometryStyle,
                MeshMarker = face.SourceFace != 0 ? face.SourceFace : face.MeshMarker,
            });
            oldToNew[oldIndex] = newIndex;
            if (faceChanged) clippedFaces.Add(oldIndex);
        }
        if (result.Faces.Count == 0) { changed = true; return null; }
        // the integrals of a node are those of the whole solid: a node with a clipped or dropped face has none
        if (source.NodeMeasures is { Count: > 0 } measures)
        {
            var affected = new HashSet<int>();
            for (int oldIndex = 0; oldIndex < source.Faces.Count; oldIndex++)
                if (!oldToNew.ContainsKey(oldIndex) || clippedFaces.Contains(oldIndex))
                    affected.Add(source.Faces[oldIndex].GeometryNode);
            var kept = measures.Where(measure => !affected.Contains(measure.Node)).ToList();
            if (kept.Count > 0) result.NodeMeasures = kept;
        }

        if (source.BrepSolids is { Count: > 0 })
        {
            result.BrepSolids = new();
            foreach (var solid in source.BrepSolids)
            {
                var mapped = solid.FaceIndices.Where(oldToNew.ContainsKey)
                    .Select(index => oldToNew[index]).Distinct().ToList();
                if (mapped.Count >= 4)
                    result.BrepSolids.Add(new GeoBrepSolid
                    {
                        FaceIndices = mapped,
                        IsClosed = solid.IsClosed && !solid.FaceIndices.Any(clippedFaces.Contains),
                    });
            }
            if (result.BrepSolids.Count == 0) result.BrepSolids = null;
        }
        SetGeometryBoundsFromFaces(result);
        return result;
    }

    /// <summary>
    /// Whether the per-class object counts of a GRep block outlive the parse (RVTIO_TYPEKEY_DIAG,
    /// IfcExporter.TypeKeysCurrent.cs).
    /// </summary>
    private static readonly bool KeepStoredClassCounts = Environment.GetEnvironmentVariable("RVTIO_TYPEKEY_DIAG") is { Length: > 0 };

    /// <summary>
    /// RVTIO_X_GHOSTNODE=0 leaves the curved nodes that have no polygon (a duplicate of another
    /// node, display surfaces) out of the triangle sets.
    /// </summary>
    private static readonly bool GhostTriangleNodes = Environment.GetEnvironmentVariable("RVTIO_X_GHOSTNODE") != "0";

    /// <summary>
    /// See <see cref="RvtReaderOptions.SeparateFacesWithDistinctOpenings"/>; set by the reader for
    /// the file it reads.
    /// </summary>
    internal static bool SeparateFacesWithDistinctOpenings;

    /// <summary>Key of a face by its quantized corners, independent of corner order. Used for equality only, so the parts are sorted ordinally
    /// (the culture sort of strings costs far more and a key is never read for its order).</summary>
    internal static string FaceCornerKey(List<double[]> cs)
    {
        var parts = new string[cs.Count];
        for (int i = 0; i < cs.Count; i++)
            parts[i] = $"{(long)Math.Round(cs[i][0] * 1e4)},{(long)Math.Round(cs[i][1] * 1e4)},{(long)Math.Round(cs[i][2] * 1e4)}";
        Array.Sort(parts, StringComparer.Ordinal);
        return string.Join(";", parts);
    }

    public static GeometryData? BuildGeometryData(GRepData grep)
    {
        // Skip empty GRep records - no faces and no bounding box = no actual geometry to report.
        if (grep.Faces.Count == 0 && grep.BoundingBox == null && grep.Instances.Count == 0) return null;

        var geo = new GeometryData
        {
            GStyleElemId = grep.GStyleElemId,
            Flags = grep.Flags,
            BoundingBox = grep.BoundingBox,
            RootBoundingBox = grep.RootBoundingBox,
            InactiveFaces = grep.InactiveFaces?.Select(face => new GeoFace
            {
                SourceFace = face.SourceFace, GeometryNode = face.GeometryNode,
                Normal = face.XVector, CurvedSurface = face.CurvedSurface,
            }).ToList(),
            MeshNodes = grep.MeshNodes,
            NodeMeasures = grep.NodeMeasures,
            Transform = grep.Transform,
            Instances = grep.Instances.Count > 0
                ? grep.Instances.Select(instance => new GeoInstance
                {
                    SymbolId = instance.SymbolElemId,
                    Transform = (double[])instance.Transform.Clone(),
                    NodeOrder = instance.NodeOrder,
                }).ToList()
                : null,
            Vertices = new(),
            Edges = new(),
            StoredNodeSummary = grep.QueueClassCounts is { } classCounts
                ? (classCounts.GetValueOrDefault(GRepParserV2.GFaceClass), classCounts.GetValueOrDefault(GRepParserV2.EdgeClass))
                : null,
            StoredSolids = grep.QueueClassCounts?.GetValueOrDefault(GRepParserV2.SolidClass),
            StoredClassCounts = KeepStoredClassCounts ? grep.QueueClassCounts : null,
        };

        // Dedup vertices by rounding to 1e-6 precision
        var vertexMap = new Dictionary<(long, long, long), int>();
        int GetOrAddVertex(double x, double y, double z)
        {
            var key = ((long)Math.Round(x * 1e6), (long)Math.Round(y * 1e6), (long)Math.Round(z * 1e6));
            if (vertexMap.TryGetValue(key, out int idx)) return idx;
            idx = geo.Vertices.Count;
            geo.Vertices.Add(new[] { x, y, z });
            vertexMap[key] = idx;
            return idx;
        }

        int AddEdge(int v0, int v1)
        {
            int idx = geo.Edges.Count;
            geo.Edges.Add(new GeoEdge
            {
                Start = geo.Vertices[v0],
                End = geo.Vertices[v1],
            });
            return idx;
        }

        List<double[]> EnvelopeToCorners(double[] env, double[] origin, double[] xv, double[] yv)
        {
            double minU = env[0], minV = env[1], maxU = env[2], maxV = env[3];
            return new List<double[]>
            {
                new[] { origin[0]+minU*xv[0]+minV*yv[0], origin[1]+minU*xv[1]+minV*yv[1], origin[2]+minU*xv[2]+minV*yv[2] },
                new[] { origin[0]+maxU*xv[0]+minV*yv[0], origin[1]+maxU*xv[1]+minV*yv[1], origin[2]+maxU*xv[2]+minV*yv[2] },
                new[] { origin[0]+maxU*xv[0]+maxV*yv[0], origin[1]+maxU*xv[1]+maxV*yv[1], origin[2]+maxU*xv[2]+maxV*yv[2] },
                new[] { origin[0]+minU*xv[0]+maxV*yv[0], origin[1]+minU*xv[1]+maxV*yv[1], origin[2]+minU*xv[2]+maxV*yv[2] },
            };
        }

        // The GRep scan can surface the same face multiple times (multiple representation groups /
        // rescans) under different plane parameterizations. Deduplicate by the face's canonical 3D
        // corners: same 4 corner positions = same face.
        var seenFaces = new Dictionary<string, int>();
        var grepFaceToGeoFace = new Dictionary<int, int>();
        static string CornerKey(List<double[]> cs) => FaceCornerKey(cs);

        for (int grepFaceIndex = 0; grepFaceIndex < grep.Faces.Count; grepFaceIndex++)
        {
            var fp = grep.Faces[grepFaceIndex];
            // Schema-decoded faces (GRepParserV2) carry explicit polygon vertices - use them
            // directly as the face's real tessellated boundary.
            if (fp.Vertices is { Count: >= 3 } poly)
            {
                // Two faces that share their outline but not their holes are two faces (the cap of
                // a window frame and the cap of the glazing bar behind it are the same rectangle
                // with different openings).
                var ck2 = CornerKey(poly) + (SeparateFacesWithDistinctOpenings && fp.InnerVertices is { Count: > 0 } innerRings
                    ? "#" + string.Join("|", innerRings.Select(CornerKey).Order(StringComparer.Ordinal)) : "");
                if (!grep.PreserveSourceFaceIdentity && seenFaces.TryGetValue(ck2, out int existing))
                {
                    grepFaceToGeoFace[grepFaceIndex] = existing;
                    continue;
                }
                var gf = new GeoFace
                {
                    Origin = fp.Origin,
                    Normal = fp.XVector, // V2 stashes the polygon normal here
                    OuterVertices = poly,
                    InnerVertices = fp.InnerVertices,
                    AlternativeOuterVertices = fp.AlternativeOuterVertices,
                    AnalyticCylinderOrigin = fp.AnalyticCylinderOrigin,
                    AnalyticCylinderAxis = fp.AnalyticCylinderAxis,
                    AnalyticCylinderReferenceDirection =
                        fp.AnalyticCylinderReferenceDirection,
                    AnalyticCylinderRadius = fp.AnalyticCylinderRadius,
                    MeshEdgeBounds = fp.MeshEdgeBounds,
                    MeshPlaneEnvelopeCorners = fp.MeshPlaneEnvelopeCorners,
                    MeshArcs = fp.MeshArcs,
                    QuadratureArea = fp.QuadratureArea,
                    CurvedSurface = fp.CurvedSurface,
                    PlaneSurface = fp.PlaneSurface,
                    PlaneXAxis = fp.PlaneX,
                    PlaneYAxis = fp.PlaneY,
                    SourceFace = fp.SourceFace,
                    FirstSourceLoop = fp.FirstSourceLoop,
                    SourceLoops = fp.SourceLoops,
                    RenderStyleId = fp.RenderStyleId,
                    GeometryNode = fp.GeometryNode,
                    NodeOrder = fp.NodeOrder,
                    GeometryStyle = fp.GeometryStyle,
                    GInfoFlags = fp.GInfoFlags,
                };
                int added = geo.Faces.Count;
                geo.Faces.Add(gf);
                seenFaces[ck2] = added;
                grepFaceToGeoFace[grepFaceIndex] = added;
                continue;
            }

            if (fp.Envelope is { Length: 4 } envK)
            {
                var ck = CornerKey(EnvelopeToCorners(envK, fp.Origin, fp.XVector, fp.YVector));
                if (seenFaces.TryGetValue(ck, out int existing))
                {
                    grepFaceToGeoFace[grepFaceIndex] = existing;
                    continue;
                }
                seenFaces[ck] = geo.Faces.Count;
            }

            var normal = new[]
            {
                fp.XVector[1] * fp.YVector[2] - fp.XVector[2] * fp.YVector[1],
                fp.XVector[2] * fp.YVector[0] - fp.XVector[0] * fp.YVector[2],
                fp.XVector[0] * fp.YVector[1] - fp.XVector[1] * fp.YVector[0],
            };
            var geoFace = new GeoFace
            {
                Origin = fp.Origin,
                XVector = fp.XVector,
                YVector = fp.YVector,
                Normal = normal,
                Envelope = fp.Envelope,
                RenderStyleId = fp.RenderStyleId,
                GeometryNode = fp.GeometryNode,
                NodeOrder = fp.NodeOrder,
                GeometryStyle = fp.GeometryStyle,
                GInfoFlags = fp.GInfoFlags,
            };
            if (fp.Envelope is { Length: 4 } env2)
            {
                double a = Math.Max(0, (env2[2] - env2[0]) * (env2[3] - env2[1]));
                foreach (var il in fp.InnerLoops)
                    if (il is { Length: 4 })
                        a -= Math.Max(0, (il[2] - il[0]) * (il[3] - il[1]));
                geoFace.Area = Math.Max(0, a);
            }

            if (fp.Envelope is { Length: 4 })
            {
                var corners = EnvelopeToCorners(fp.Envelope, fp.Origin, fp.XVector, fp.YVector);
                geoFace.OuterVertices = corners;

                var edgeIndices = new List<int>();
                var vi = new int[4];
                for (int i = 0; i < 4; i++)
                    vi[i] = GetOrAddVertex(corners[i][0], corners[i][1], corners[i][2]);
                for (int i = 0; i < 4; i++)
                    edgeIndices.Add(AddEdge(vi[i], vi[(i + 1) % 4]));

                if (fp.InnerLoops.Count > 0)
                {
                    geoFace.InnerLoops = fp.InnerLoops;
                    geoFace.InnerVertices = new();
                    foreach (var il in fp.InnerLoops)
                    {
                        var innerCorners = EnvelopeToCorners(il, fp.Origin, fp.XVector, fp.YVector);
                        geoFace.InnerVertices.Add(innerCorners);

                        var ivi = new int[4];
                        for (int i = 0; i < 4; i++)
                            ivi[i] = GetOrAddVertex(innerCorners[i][0], innerCorners[i][1], innerCorners[i][2]);
                        for (int i = 0; i < 4; i++)
                            edgeIndices.Add(AddEdge(ivi[i], ivi[(i + 1) % 4]));
                    }
                }

                geoFace.EdgeIndices = edgeIndices;
            }
            else if (fp.InnerLoops.Count > 0)
            {
                geoFace.InnerLoops = fp.InnerLoops;
            }

            grepFaceToGeoFace[grepFaceIndex] = geo.Faces.Count;
            geo.Faces.Add(geoFace);
        }

        // Revit exports every Geometry node as a solid.
        var nodesWithPolygons = geo.Faces.Select(face => face.GeometryNode).Where(node => node > 0).ToHashSet();
        var ghostNodes = grep.Faces.Where(face => face.GeometryNode > 0 && !nodesWithPolygons.Contains(face.GeometryNode))
            .GroupBy(face => face.GeometryNode)
            .Where(group => group.Select(face => face.SourceFace).Distinct().Count() >= GRepParserV2.MinSolidFaces)
            .OrderBy(group => group.Key)
            .Select(group => (Node: group.Key, Style: group.First().GeometryStyle,
                RenderStyleId: group.Select(face => face.RenderStyleId ?? 0).Where(id => id > 0)
                    .GroupBy(id => id).OrderByDescending(ids => ids.Count()).ThenBy(ids => ids.Key)
                    .Select(ids => ids.Key).FirstOrDefault())).ToList();
        if (grep.DisplayOnlyNodes is { Count: > 0 } displayOnlyNodes) ghostNodes.AddRange(displayOnlyNodes);
        if (ghostNodes.Count > 0) geo.GhostNodes = ghostNodes;
        // the curved ghost nodes are written as triangle sets (face meshes need no polygon): the
        // node carries the style of its ghost record
        if (geo.MeshNodes is { Count: > 0 } && ghostNodes.Count > 0 && GhostTriangleNodes)
            geo.MeshNodes = geo.MeshNodes.Select(n => !n.Ghost && ghostNodes.FirstOrDefault(g => g.Node == n.Node) is { Node: > 0 } g
                ? n with { Ghost = true, Style = g.Style, RenderStyleId = g.RenderStyleId } : n).ToList();

        if (grep.Solids.Count > 0)
        {
            geo.BrepSolids = new();
            foreach (var solid in grep.Solids)
            {
                var mapped = solid.FaceIndices
                    .Where(grepFaceToGeoFace.ContainsKey)
                    .Select(i => grepFaceToGeoFace[i]).Distinct().ToList();
                if (mapped.Count >= 4)
                    geo.BrepSolids.Add(new GeoBrepSolid { FaceIndices = mapped, IsClosed = solid.IsClosed });
            }
            if (geo.BrepSolids.Count == 0) geo.BrepSolids = null;
        }

        if (!grep.IsOpenSurface && geo.BrepSolids == null)
            RemoveInteriorLayerFaces(geo.Faces);

        // Set to null if empty to avoid JSON bloat
        if (geo.Vertices.Count == 0) geo.Vertices = null;
        if (geo.Edges.Count == 0) geo.Edges = null;

        return geo;
    }

    /// <summary>
    /// Drop compound-structure layer-interface planes from the face list: a boundary face of a
    /// solid always touches the solid's extent along its own normal, whereas layer interfaces are
    /// parallel planes strictly INSIDE (walls emit one per layer boundary).
    /// </summary>
    private static void RemoveInteriorLayerFaces(List<GeoFace> faces)
    {
        if (faces.Count < 3) return;
        var drop = new bool[faces.Count];
        double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        for (int i = 0; i < faces.Count; i++)
        {
            var fi = faces[i];
            double nli = Math.Sqrt(Dot(fi.Normal, fi.Normal));
            if (nli < 1e-9) continue;
            var ni = new[] { fi.Normal[0] / nli, fi.Normal[1] / nli, fi.Normal[2] / nli };
            double di = Dot(fi.Origin, ni);
            bool above = false, below = false;
            for (int j = 0; j < faces.Count && !(above && below); j++)
            {
                if (j == i) continue;
                var fj = faces[j];
                double nlj = Math.Sqrt(Dot(fj.Normal, fj.Normal));
                if (nlj < 1e-9) continue;
                double cos = Math.Abs(Dot(fi.Normal, fj.Normal) / (nli * nlj));
                if (cos < 0.996) continue; // not parallel (>5 deg)
                double dj = Dot(fj.Origin, ni);
                if (dj > di + 0.01) above = true;
                else if (dj < di - 0.01) below = true;
            }
            if (above && below) drop[i] = true;
        }
        for (int i = faces.Count - 1; i >= 0; i--)
            if (drop[i]) faces.RemoveAt(i);
    }

    public static void AddComputedBipsFromGeometry(Element elem, GRepData grep)
    {
        if (grep.Faces.Count == 0) return;
        // Face-derived quantities hold only for slab-like categories (floors/ceilings/roofs/foundations).
        bool slabLike = elem.CategoryId is -2000032 or -2000038 or -2000035 or -2001300;
        if (!slabLike) return;
        var existingBips = new HashSet<int>(elem.Parameters.Select(p => p.Id));

        GRepFace? primaryFace = null;
        double maxArea = 0;
        GRepFace? largestWithInner = null;
        double maxAreaWithInner = 0;
        foreach (var f in grep.Faces)
        {
            if (f.Envelope.Length != 4) continue;
            double area = (f.Envelope[2] - f.Envelope[0]) * (f.Envelope[3] - f.Envelope[1]);
            if (area > maxArea) { maxArea = area; primaryFace = f; }
            if (f.InnerLoops.Count > 0 && area > maxAreaWithInner) { maxAreaWithInner = area; largestWithInner = f; }
        }
        if (largestWithInner != null && maxAreaWithInner >= maxArea * 0.5)
        { primaryFace = largestWithInner; maxArea = maxAreaWithInner; }
        if (primaryFace == null || maxArea < 1e-6) return;

        // HOST_AREA_COMPUTED: for walls with openings, Revit includes gross side area + top/bottom
        // faces + opening reveal faces. For walls without openings: just gross side area (= length
        // x height).
        double grossArea = maxArea; // primary face gross area (no opening deduction)
        double hostArea = grossArea;

        if (primaryFace.InnerLoops.Count > 0)
        {
            // Wall has openings - add top, bottom, and opening front/back faces
            double faceW = primaryFace.Envelope[2] - primaryFace.Envelope[0];
            double faceH = primaryFace.Envelope[3] - primaryFace.Envelope[1];

            double thickness = 0;
            foreach (var p in elem.Parameters)
            {
                if (p.Id is -1001000 or -1001900 or -1001902 or -1001601 or -1001302 or -1002206)
                {
                    if (p.Value is double d && d > 1e-6 && d < 100) { thickness = d; break; }
                }
            }

            if (thickness > 1e-6)
            {
                // Find the LARGEST face without inner loops (centerline face, full length)
                double longestFaceW = faceW;
                foreach (var f in grep.Faces)
                {
                    if (f.Envelope.Length != 4 || f.InnerLoops.Count > 0) continue;
                    double fw = f.Envelope[2] - f.Envelope[0];
                    double fh = f.Envelope[3] - f.Envelope[1];
                    // Must be a side face (similar height to primary face)
                    if (Math.Abs(fh - faceH) < 0.1 && fw > longestFaceW)
                        longestFaceW = fw;
                }

                grossArea = longestFaceW * faceH;
                hostArea = grossArea;

                hostArea += 2 * thickness * longestFaceW;

                foreach (var il in primaryFace.InnerLoops)
                {
                    if (il.Length == 4)
                        hostArea += (il[2] - il[0]) * (il[3] - il[1]);
                }
            }
        }

        if (hostArea < 1e-6) return;

        if (!existingBips.Contains(-1012805))
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1012805, "HOST_AREA_COMPUTED", StorageType.Double, hostArea));

        // HOST_VOLUME_COMPUTED intentionally NOT derived here: hostArea x thickness is wrong for
        // slab-like elements: slabs slope or vary in section.

        if (!existingBips.Contains(-1001953))
        {
            double w = primaryFace.Envelope[2] - primaryFace.Envelope[0];
            double h = primaryFace.Envelope[3] - primaryFace.Envelope[1];
            if (w > 1e-6 && h > 1e-6)
                elem.Parameters.Add(BipHelpers.MakeBipParam(-1001953, "HOST_PERIMETER_COMPUTED", StorageType.Double, 2 * (w + h)));
        }

        string cat = elem.Category ?? "";
        bool isCurveBased = cat.Contains("Wall") || cat.Contains("Framing") || cat.Contains("Column")
            || cat.Contains("Pipe") || cat.Contains("Duct") || cat.Contains("CableTray") || cat.Contains("Conduit");
        if (isCurveBased && !existingBips.Contains(-1004005))
        {
            GRepFace? largestFace = null;
            double largestArea2 = 0;
            foreach (var f in grep.Faces)
            {
                if (f.Envelope.Length != 4) continue;
                double a = (f.Envelope[2] - f.Envelope[0]) * (f.Envelope[3] - f.Envelope[1]);
                if (a > largestArea2) { largestArea2 = a; largestFace = f; }
            }
            if (largestFace != null)
            {
                double faceW = largestFace.Envelope[2] - largestFace.Envelope[0];
                if (faceW > 1e-6)
                    elem.Parameters.Add(BipHelpers.MakeBipParam(-1004005, "CURVE_ELEM_LENGTH", StorageType.Double, faceW));
            }
        }
    }

    /// <summary>
    /// For FamilyInstance elements (doors, windows, furniture, etc.) that lack dimension params,
    /// compute GENERIC_WIDTH/HEIGHT/THICKNESS from the tight box of the element's own geometry
    /// record (the same source the estimates had before the header outline took the element box).
    /// </summary>
    public static void AddBboxDimensionsIfMissing(Element elem, ParsingState state)
    {
        // The tight box of the decoded geometry, else the element's own box (the header outline):
        // the estimates the rules measured before the outline took Element.BoundingBox over.
        double[]? box = state.GrepBlocks.TryGetValue((uint)elem.Id, out var grep) ? grep.BoundingBox : null
            ?? elem.BoundingBox;
        if (box == null || box.Length != 6) return;
        string cat = elem.Category ?? "";
        if (!cat.Contains("Door") && !cat.Contains("Window") && !cat.Contains("Furniture")
            && !cat.Contains("Generic") && !cat.Contains("Casework")
            && !cat.Contains("Plumbing") && !cat.Contains("Lighting")
            && !cat.Contains("Specialty") && !cat.Contains("Entourage"))
            return;

        var existingBips = new HashSet<int>(elem.Parameters.Select(p => p.Id));
        double dx = box[3] - box[0];
        double dy = box[4] - box[1];
        double dz = box[5] - box[2];
        if (dx < 1e-6 || dy < 1e-6 || dz < 1e-6) return;

        // Width = max of dx/dy (horizontal), Height = dz, Thickness = min of dx/dy
        double width = Math.Max(dx, dy);
        double thickness = Math.Min(dx, dy);
        double height = dz;

        // Bounding-box estimates: recorded so exact rules never consume them.
        void AddApproximate(int id, string name, double value)
        {
            elem.Parameters.Add(BipHelpers.MakeBipParam(id, name, StorageType.Double, value));
            (elem.BoundingBoxDimensionParamIds ??= new HashSet<int>()).Add(id);
        }
        if (!existingBips.Contains(-1001301) && width > 1e-6) // GENERIC_WIDTH
            AddApproximate(-1001301, "GENERIC_WIDTH", width);
        if (!existingBips.Contains(-1001300) && height > 1e-6) // GENERIC_HEIGHT
            AddApproximate(-1001300, "GENERIC_HEIGHT", height);
        if (!existingBips.Contains(-1001302) && thickness > 1e-6) // GENERIC_THICKNESS
            AddApproximate(-1001302, "GENERIC_THICKNESS", thickness);
    }

    /// <summary>
    /// Resolve the family name of a symbol via the name-table chain: symbol's name entry ->
    /// trailing parent-holder id -> holder's target (the family) -> family name.
    /// </summary>
    private static bool TryResolveFamilyNameViaTable(uint symbolId, ParsingState state, out string famName)
        => TryResolveFamilyNameViaTable(symbolId, state, out famName, out _);

    /// <summary>
    /// Same chain as above, plus a diagnostic <paramref name="stopReason"/> naming the link where
    /// the chain broke ("no-parent-holder" / "no-holder-target" / "no-table-name") when it returns
    /// false. Empty on success.
    /// </summary>
    private static bool TryResolveFamilyNameViaTable(
        uint symbolId, ParsingState state, out string famName, out string stopReason)
    {
        famName = ""; stopReason = "";
        if (!state.NameEntryParentHolder.TryGetValue(symbolId, out var parentHolder))
        { stopReason = "no-parent-holder"; return false; }
        if (!state.NameHolderTarget.TryGetValue(parentHolder, out var familyId))
        { stopReason = "no-holder-target"; return false; }
        if (!state.ElementNameTable.TryGetValue(familyId, out var name))
        { stopReason = "no-table-name"; return false; }
        famName = name;
        return true;
    }

    /// <summary>
    /// Resolve an element's name. Priority: 1. Native 0xFFFF-block name 3. Block strings / scan
    /// strings (heuristic, may be contaminated)
    /// </summary>
    internal static string? ResolveElementName(uint eid, ParsingState state)
    {
        if (state.ElementNameTable.TryGetValue(eid, out var tableName))
            return tableName;
        if (state.NativeElementNames.TryGetValue(eid, out var nativeName))
            return nativeName;
        string? foundName = null;
        if (state.ElemStrings.TryGetValue(eid, out var blkStrs))
            foundName = blkStrs.FirstOrDefault(StringScanner.IsValidElementName);
        if (foundName == null && state.ElemScanStrings.TryGetValue(eid, out var scanStrs))
            foundName = scanStrs.FirstOrDefault(StringScanner.IsValidParamName);
        return foundName;
    }

    public static void BuildElemTableElement(
        uint elemId, uint typeId, ParsingState state, RevitFileInfo rvtInfo)
    {
        var elem = new Element { Id = (int)elemId, TypeId = -1 };
        elem.NativeHeaderCategoryId = state.HeaderCategories.GetValueOrDefault(elemId);
        elem.NativeHeaderDesignOptionId = state.HeaderDesignOptions.GetValueOrDefault(elemId);
        elem.NativeRootClassName = state.NativeRootClassNames.GetValueOrDefault(elemId);
        elem.RebarBars = state.RebarBars.GetValueOrDefault(elemId);
        elem.JoistSystem = state.JoistSystems.GetValueOrDefault(elemId);
        if (elem.NativeRootClassName is { } rootName) elem.NativeRootClassChain = state.WideSchema?.ChainOf(rootName);
        if (state.WideSchema is { } wideSchema && wideSchema.ByName("Symbol") is { } symbolClass
            && elem.NativeRootClassName != null && wideSchema.ByName(elem.NativeRootClassName) is { } rootClass)
            elem.NativeIsSymbol = wideSchema.IsSubclassOf(rootClass.Id, symbolClass.Id);

        elem.UniqueId = BuildUniqueId(elemId, state);

        if (state.WideSchema != null && !state.R22View)
        {
            // a file with 64-bit ids: the rest of this method is the Revit 2022 record and stream
            // heuristics. The element is what the file's own tables and the schema-driven block
            // walk give: the header category is exact.
            if (elem.NativeHeaderCategoryId is int header && header != -1)
            {
                elem.CategoryId = header;
                if (BuiltInCategoryNames.Lookup.TryGetValue(header, out var headerName)) elem.Category = headerName;
            }
            rvtInfo.Elements.Add(elem);
            return;
        }

        elem.Name = ResolveElementName(elemId, state);

        GRepData? grepDataForElem = null;
        if (state.Records.TryGetValue(elemId, out var rec))
        {
            if (rec.CategoryId.HasValue)
            {
                elem.CategoryId = rec.CategoryId.Value;
                if (BuiltInCategoryNames.Lookup.TryGetValue(rec.CategoryId.Value, out var catName))
                    elem.Category = catName;
            }

            if (rec.References.Count > 0)
            {
                elem.References = rec.References.Select(r => (int)r).ToList();
                var refStrings = new Dictionary<string, List<string>>();
                foreach (var refId in rec.References)
                {
                    if (refId == elemId) continue;
                    if (!state.ElemTable.ContainsKey(refId)) continue;
                    if (state.ElemStrings.TryGetValue(refId, out var refStrs) && refStrs.Count > 0)
                    {
                        string label = state.ElemClassIdx.TryGetValue(refId, out var refClassId)
                            ? $"Ref_T{refClassId}" : $"Ref_E{refId}";
                        if (!refStrings.ContainsKey(label))
                            refStrings[label] = refStrs.Take(5).ToList();
                    }
                }
                if (refStrings.Count > 0) elem.RefStrings = refStrings;
            }

            AddBodyBipParams(elem, rec, elemId, state);
            AddUserParams(elem, rec, elemId, state);
        }

        if (state.GrepBlocks.TryGetValue(elemId, out grepDataForElem))
        {
            // The tight box of the record stays on the geometry; the element box comes from the
            // header outline (ApplyHeaderBoundingBoxes).
            elem.Geometry = BuildGeometryData(grepDataForElem);
            ExpandInstanceGeometry(elem, grepDataForElem, (int)typeId, state);
        }

        AddBipDoubles(elem, elemId, state);
        AddBipInts(elem, elemId, state);
        AddPropagatedBipDoubles(elem, elemId, state);
        AddBaseFields(elem, elemId, state);
        AddComputedElementParams(elem, elemId, typeId, state);

        AttachFamilyCutGeometry(elem, elemId, state);

        if (grepDataForElem != null)
            AddComputedBipsFromGeometry(elem, grepDataForElem);

        AddBboxDimensionsIfMissing(elem, state);
        BuildLocationCurve(elem, elemId, state);
        BuildInstanceTransform(elem, elemId, state);
        ExpandTransformOnlyInstanceGeometry(elem, elemId, (int)typeId, state);
        AttachComputedSymbolGeometry(elem, elemId, state);
        AttachDimension(elem, elemId, state);
        AttachSketchCurve(elem, elemId, state);

        ApplyOwnWallPlanarBody(elem, state);
        rvtInfo.Elements.Add(elem);
    }

    /// <summary>
    /// Attach decoded 0x091F dimension/constraint geometry, defaulting the category to
    /// OST_Dimensions when no more specific category was found.
    /// </summary>
    private static void AttachDimension(Element elem, uint id, ParsingState state)
    {
        if (!state.Dimensions.TryGetValue(id, out var dim)) return;
        elem.Dimension = dim;
        if (!elem.CategoryId.HasValue)
        {
            elem.CategoryId = -2000260; // OST_Dimensions
            if (BuiltInCategoryNames.Lookup.TryGetValue(-2000260, out var dn)) elem.Category = dn;
        }

        // Dimension bookkeeping params: DIM_REFERENCE_COUNT = number of decoded reference points
        // (matches Revit exactly), DIM_LEADER default = 1.
        bool hasRefCount = false, hasLeader = false;
        foreach (var p in elem.Parameters)
        {
            if (p.Id == -1006520) hasRefCount = true;
            else if (p.Id == -1004515) hasLeader = true;
        }
        if (!hasRefCount && dim.Points.Count > 0)
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1006520, "DIM_REFERENCE_COUNT", StorageType.Integer, dim.Points.Count));
        if (!hasLeader)
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1004515, "DIM_LEADER", StorageType.Integer, 1));
    }

    /// <summary>Attach a decoded 0x0346 sketch-line curve (Line/Arc) as the element location.</summary>
    private static void AttachSketchCurve(Element elem, uint id, ParsingState state)
    {
        if (elem.Location == null && state.SketchCurves.TryGetValue(id, out var lc))
            elem.Location = lc;
    }

    /// <summary>
    /// The real type element id of an element (ELEM_TYPE_PARAM / ELEM_FAMILY_AND_TYPE_PARAM /
    /// SYMBOL_ID_PARAM), or -1 if not resolved. Only real element ids (&gt; 1000) count.
    /// </summary>
    private static int TypeParamOf(Element e)
    {
        foreach (var p in e.Parameters)
            if (p.Id is -1002050 or -1002052 or -1002000 && p.Value is int v && v > 1000)
                return v;
        return -1;
    }

    /// <summary>
    /// Resolve ELEM_TYPE_PARAM / ELEM_FAMILY_AND_TYPE_PARAM / SYMBOL_ID_PARAM to the real type
    /// element id for instances whose binary body didn't carry it.
    /// </summary>
    private static void ResolveTypeParams(RevitFileInfo info)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId[e.Id] = e;

        // Weak (automatic) dimensions reference their dim style FIRST - a small built-in element id
        // (e.g. 277), below the >1000 cutoff of the voting heuristic underneath.
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2000261 || e.References is not { Count: > 0 }) continue;
            int t = e.References[0];
            if (t <= 0 || TypeParamOf(e) > 0) continue;
            AddBipIfMissing(e, -1002050, "ELEM_TYPE_PARAM", t);
            AddBipIfMissing(e, -1002052, "ELEM_FAMILY_AND_TYPE_PARAM", t);
            AddBipIfMissing(e, -1002000, "SYMBOL_ID_PARAM", t);
            AddBipIfMissing(e, -1002051, "ELEM_FAMILY_PARAM", t);
        }

        // Seed: type element ids that some element already carries (from the binary body).
        var seed = new HashSet<int>();
        foreach (var e in info.Elements)
        {
            int t = TypeParamOf(e);
            if (t > 0) seed.Add(t);
        }

        // For each candidate id, how many distinct elements of each category reference it. A type
        // element is referenced predominantly by its instances (one category).
        var refCat = new Dictionary<int, Dictionary<int, int>>();
        foreach (var e in info.Elements)
        {
            if (e.References == null || !e.CategoryId.HasValue) continue;
            int c = e.CategoryId.Value;
            var seenRefs = new HashSet<int>();
            foreach (var r in e.References)
            {
                if (r <= 1000 || !seenRefs.Add(r)) continue;
                if (!refCat.TryGetValue(r, out var cc)) refCat[r] = cc = new Dictionary<int, int>();
                cc.TryGetValue(c, out int n); cc[c] = n + 1;
            }
        }

        foreach (var e in info.Elements)
        {
            if (e.References == null || e.References.Count == 0 || TypeParamOf(e) > 0) continue;

            int chosen = -1;
            if (!e.CategoryId.HasValue)
            {
                // No category to disambiguate by - only trust a referenced known type.
                foreach (var r in e.References)
                    if (r > 1000 && r != e.Id && seed.Contains(r)) { chosen = r; break; }
            }
            else
            {
                int C = e.CategoryId.Value;
                int bestScore = -1;
                var seen = new HashSet<int>();
                foreach (var r in e.References)
                {
                    if (r <= 1000 || r == e.Id || !seen.Add(r)) continue;
                    // candidate's own category must be compatible (null or same) - excludes
                    // levels/phases.
                    if (byId.TryGetValue(r, out var re) && re.CategoryId.HasValue && re.CategoryId != C) continue;

                    int n = 0, tot = 0;
                    if (refCat.TryGetValue(r, out var cc))
                    {
                        cc.TryGetValue(C, out n);
                        foreach (var v in cc.Values) tot += v;
                    }
                    double frac = tot > 0 ? (double)n / tot : 0;
                    bool qualifies = (n >= 2 && frac >= 0.6) || seed.Contains(r);
                    if (!qualifies) continue;

                    int score = n + (seed.Contains(r) ? 100 : 0);
                    if (score > bestScore) { bestScore = score; chosen = r; }
                }
            }

            if (chosen < 0) continue;
            AddBipIfMissing(e, -1002050, "ELEM_TYPE_PARAM", chosen);
            AddBipIfMissing(e, -1002052, "ELEM_FAMILY_AND_TYPE_PARAM", chosen);
            AddBipIfMissing(e, -1002000, "SYMBOL_ID_PARAM", chosen);
        }
    }

    /// <summary>
    /// "Design Option" (DESIGN_OPTION_PARAM, string) is "Main Model" for an element outside any
    /// design option (DESIGN_OPTION_ID -1, or -4 on Material elements).
    /// </summary>
    private static void ApplyDerivedDesignOptionName(RevitFileInfo info)
    {
        foreach (var e in info.Elements)
        {
            if (e.Parameters.Any(p => p.Id == -1013200)) continue;
            var option = e.Parameters.FirstOrDefault(p => p.Id == -1013201);
            if (option?.Value is int id && id is -1 or -4)
                e.Parameters.Add(BipHelpers.MakeBipParam(-1013200, "DESIGN_OPTION_PARAM", StorageType.String, "Main Model"));
        }
    }

    /// <summary>
    /// Storage type of a file-defined parameter from its data spec (None when the file has no
    /// spec).
    /// </summary>
    private static StorageType FamilyParamStorageFromSpec(ParsingState state, int pid)
    {
        if (!state.ForgeParamDefs.TryGetValue((uint)pid, out var def)
            || !state.ForgeParamSpecs.TryGetValue(def.ForgeId, out var spec))
        {
            // No spec: the kind of the parameter element's own ParamDef object
            // (ParamElem.m_pParamDef).
            if (state.NativeParams.TryGetValue((uint)pid, out var definition)
                && definition.Fields.TryGetValue("2870:m_pParamDef.class", out int definitionClass))
                return definitionClass switch
                {
                    2866 => StorageType.Integer, // ParamDefYesNo
                    2781 => StorageType.ElementId, // ParamDefMaterialBrowse
                    _ => StorageType.None,
                };
            return StorageType.None;
        }
        if (spec.Contains("bool") || spec.Contains("int")) return StorageType.Integer;
        if (spec.Contains("material") || spec.Contains("reference") || spec.Contains("category")) return StorageType.ElementId;
        if (spec.StartsWith("autodesk.spec.aec") || spec.Contains("number") || spec.Contains("length")
            || spec.Contains("area") || spec.Contains("volume") || spec.Contains("currency")) return StorageType.Double;
        return StorageType.String;
    }

    /// <summary>Start/end z of the FamInstCurveDriver drive line: origin + dirVec * endParam.</summary>
    private static bool TryCurveDriverLineZ(TfBlockDeserializer.NativeParams np, out (double StartZ, double EndZ) z)
    {
        z = default;
        if (!np.DoubleFields.TryGetValue("87:m_pCrv.origin@2", out double originZ)
            || !np.DoubleFields.TryGetValue("87:m_pCrv.dirVec@2", out double directionZ)
            || !np.DoubleFields.TryGetValue("87:m_pCrv.endParams@0", out double start)
            || !np.DoubleFields.TryGetValue("87:m_pCrv.endParams@1", out double end))
            return false;
        z = (originZ + directionZ * start, originZ + directionZ * end);
        return double.IsFinite(z.StartZ) && double.IsFinite(z.EndZ);
    }

    /// <summary>
    /// Curve-driven family instances (FamInstCurveDriver: beams, braces): Reference Level
    /// (INSTANCE_REFERENCE_LEVEL_PARAM) is the driver's stored m_refLevelId, else the Level the
    /// instance's sketch plane lies on (SketchPlane OnDatumPlaneRef.m_datumPlaneId).
    /// </summary>
    internal static void ApplyCurveDriverLevelParams(RevitFileInfo info)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);
        foreach (var e in info.Elements)
        {
            if (e.CurveDriverRefLevelId is not int stored) continue;
            int level = stored > 0 ? stored : -1;
            if (level <= 0 && e.AssocSketchPlaneId is int plane && plane > 0
                && byId.TryGetValue(plane, out var sketchPlane) && sketchPlane.SketchPlaneDatumId is int datum
                && byId.TryGetValue(datum, out var datumElement) && datumElement.IsNativeLevel)
                level = datum;
            var reference = e.Parameters.FirstOrDefault(p => p.Id == -1001383);
            if (level <= 0)
            {
                if (reference is { Value: int unknown } && unknown <= 0) e.Parameters.Remove(reference);
                continue;
            }
            if (reference != null) { reference.Value = level; reference.StorageType = StorageType.ElementId; }
            else e.Parameters.Add(BipHelpers.MakeBipParam(-1001383, "INSTANCE_REFERENCE_LEVEL_PARAM", StorageType.ElementId, level));
            if (!byId.TryGetValue(level, out var levelElement)
                || levelElement.Parameters.FirstOrDefault(p => p.Id == -1007102)?.Value is not double elevation)
                continue;
            SetDouble(e, -1001653, "STRUCTURAL_REFERENCE_LEVEL_ELEVATION", elevation);
            if (e.CurveDriverLineZ is { } z)
            {
                SetDouble(e, -1001571, "STRUCTURAL_BEAM_END0_ELEVATION", z.StartZ - elevation);
                SetDouble(e, -1001572, "STRUCTURAL_BEAM_END1_ELEVATION", z.EndZ - elevation);
            }
        }

        static void SetDouble(Element e, int id, string name, double value)
        {
            var existing = e.Parameters.FirstOrDefault(p => p.Id == id);
            if (existing != null) { existing.Value = value; existing.StorageType = StorageType.Double; }
            else e.Parameters.Add(BipHelpers.MakeBipParam(id, name, StorageType.Double, value));
        }
    }

    /// <summary>
    /// HOST_ID_PARAM of a family instance from the stored InsertableInst.m_hostId: curtain cells
    /// and mullions report their curtain host, beams their beam system or truss.
    /// </summary>
    internal static void ApplyHostIdParam(RevitFileInfo info, IReadOnlyDictionary<uint, ushort>? classByElement)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);
        foreach (var e in info.Elements)
        {
            if (e.SerializedHostId is not int host || host <= 0) continue;
            int? value = null;
            if (e.IsCurtainMemberInstance) value = host;
            else if (byId.TryGetValue(host, out var hostElement)
                && hostElement.Category is "OST_StructuralFramingSystem" or "OST_StructuralTruss")
                value = host; // members of a beam system or truss report the system that owns them
            else if (e.NativeMasterSymbolId is int symbol && byId.TryGetValue(symbol, out var symbolElement)
                && symbolElement.SymbolFamilyId is int family && byId.TryGetValue(family, out var familyElement)
                && familyElement.FamilyIsHostBased is bool hostBased)
            {
                bool workPlane = classByElement != null && classByElement.TryGetValue((uint)host, out var hostClass) && hostClass == 3607;
                value = hostBased && !workPlane ? host : -1;
            }
            if (value is not int hostId) continue;
            var existing = e.Parameters.FirstOrDefault(p => p.Id == -1002108);
            if (existing != null) { existing.Value = hostId; existing.StorageType = StorageType.ElementId; }
            else e.Parameters.Add(BipHelpers.MakeBipParam(-1002108, "HOST_ID_PARAM", StorageType.ElementId, hostId));
        }
    }

    /// <summary>
    /// Placement height of family instances from the stored FamilyInstance.m_instOrigin z. Other
    /// instances that carry Offset from Host get it as that value; which instances carry it is left
    /// to the existing passes.
    /// </summary>
    internal static void ApplyInstanceOriginOffsets(RevitFileInfo info)
    {
        foreach (var e in info.Elements)
        {
            if (e.InstanceOriginZ is not double z || e.NativeMasterSymbolId == null) continue;
            if (e.CategoryId == -2001300) // OST_StructuralFoundation
            {
                e.Parameters.RemoveAll(p => p.Id is -1001364 or -1001360);
                var height = e.Parameters.FirstOrDefault(p => p.Id == -1001951);
                if (height != null) { height.Value = z; height.StorageType = StorageType.Double; }
                else e.Parameters.Add(BipHelpers.MakeBipParam(-1001951, "FLOOR_HEIGHTABOVELEVEL_PARAM", StorageType.Double, z));
                continue;
            }
            var offset = e.Parameters.FirstOrDefault(p => p.Id == -1001364);
            if (offset != null) { offset.Value = z; offset.StorageType = StorageType.Double; }
        }
    }

    /// <summary>
    /// Structural columns from their stored FamilyLevelRef pair (base, top) and m_bVertical: Base
    /// Offset (SCHEDULE_BASE_LEVEL_OFFSET_PARAM and FAMILY_BASE_LEVEL_OFFSET_PARAM) is the base
    /// offset.
    /// </summary>
    internal static void ApplyStructuralColumnParams(RevitFileInfo info)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2001330 || e.NativeMasterSymbolId == null || e.FamilyLevelRefs is not { } refs) continue;
            SetParam(e, -1002065, "SCHEDULE_BASE_LEVEL_OFFSET_PARAM", StorageType.Double, refs.BaseOffset);
            SetParam(e, -1001357, "FAMILY_BASE_LEVEL_OFFSET_PARAM", StorageType.Double, refs.BaseOffset);
            if (e.InstanceIsVertical != true) continue;
            SetParam(e, -1150171, "SLANTED_COLUMN_TYPE_PARAM", StorageType.Integer, 0);
            if (LevelElevation(refs.BaseLevel) is double baseElevation && LevelElevation(refs.TopLevel) is double topElevation)
                SetParam(e, -1001375, "INSTANCE_LENGTH_PARAM", StorageType.Double,
                    topElevation + refs.TopOffset - (baseElevation + refs.BaseOffset));
        }

        double? LevelElevation(int id) =>
            byId.TryGetValue(id, out var level) && level.Parameters.FirstOrDefault(p => p.Id == -1007102)?.Value is double elevation
                ? elevation : null;
    }

    internal static void ApplyFramingSymbolLength(RevitFileInfo info)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2001320 || e.NativeMasterSymbolId == null || e.InstanceSymbolId is not int symbolId
                || !byId.TryGetValue(symbolId, out var symbol) || symbol.SymbolFramingLength is not double length) continue;
            SetParam(e, -1001375, "INSTANCE_LENGTH_PARAM", StorageType.Double, length);
        }
    }

    internal static void ApplyHeadHeight(RevitFileInfo info)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);
        foreach (var e in info.Elements)
        {
            if (e.CategoryId is not (-2000014 or -2000023) || e.NativeMasterSymbolId is not int typeId) continue;
            if (e.Parameters.FirstOrDefault(p => p.Id == -1001361)?.Value is not double sill) continue;
            bool estimated = e.BoundingBoxDimensionParamIds?.Contains(-1001300) == true;
            object? height = (estimated ? null : e.Parameters.FirstOrDefault(p => p.Id == -1001300)?.Value)
                ?? (byId.TryGetValue(typeId, out var type) && type.BoundingBoxDimensionParamIds?.Contains(-1001300) != true
                    ? type.Parameters.FirstOrDefault(p => p.Id == -1001300)?.Value : null);
            if (height is double h)
                SetParam(e, -1001362, "INSTANCE_HEAD_HEIGHT_PARAM", StorageType.Double, sill + h);
        }
    }

    /// <summary>
    /// "Host" text (INSTANCE_FREE_HOST_PARAM) of free-hosted instances, in the file's saved locale:
    /// "&lt;Level family&gt;: &lt;level name&gt;" without a stored host, "Surface"/"Subregion" on a
    /// topography surface/subregion.
    /// </summary>
    internal static void ApplyHostLabels(RevitFileInfo info, Lookup.RevitSystemFamilyNames? names,
        IReadOnlyDictionary<uint, ushort>? classByElement)
    {
        if (names == null) return;
        foreach (var group in info.Elements)
        {
            if (group.CategoryId == -2000095) group.SerializedGroupFamilyLabel = names.Get(Lookup.RevitUiText.ModelGroup);
            else if (group.IsNativeLevel) group.SerializedUiFamilyLabel = names.Get(Lookup.RevitUiText.LevelFamily);
            else if (group.CategoryId == -2001340) group.SerializedUiFamilyLabel = names.Get(Lookup.RevitUiText.TopographySurface);
        }
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);
        foreach (var e in info.Elements)
        {
            var host = e.Parameters.FirstOrDefault(p => p.Id == -1001363);
            if (host == null)
            {
                // An instance placed on the face of an element (Host Id -1, the host stored) shows
                // that element as its Host.
                if (e.CategoryId is -2001330 or -2001320 or -2000100 || e.NativeMasterSymbolId == null || e.SerializedHostId is not > 0
                    || e.Parameters.FirstOrDefault(p => p.Id == -1002108)?.Value is not -1)
                    continue;
                host = BipHelpers.MakeBipParam(-1001363, "INSTANCE_FREE_HOST_PARAM", StorageType.String, "");
                e.Parameters.Add(host);
            }
            if (e.CategoryId is -2001330 or -2001320 or -2000100) { e.Parameters.Remove(host); continue; }
            string? label = null;
            if (e.SerializedHostId is int hostId && hostId > 0)
            {
                // A site surface hosted on another surface (SiteSurface.m_hostId) is a subregion.
                if (byId.TryGetValue(hostId, out var hostElement) && hostElement.CategoryId == -2001340 // OST_Topography
                    && classByElement != null && classByElement.TryGetValue((uint)hostId, out var hostClass) && hostClass == 3595)
                    label = names.Get(hostElement.SerializedHostId is > 0
                        ? Lookup.RevitUiText.Subregion : Lookup.RevitUiText.TopographySurface);
                else if (byId.TryGetValue(hostId, out hostElement) && hostElement.CategoryId == -2005301) // OST_BoundaryConditions
                    label = names.Get(Lookup.RevitUiText.BoundaryConditions);
                else if (e.Parameters.FirstOrDefault(p => p.Id == -1002108)?.Value is -1 && hostElement != null)
                {
                    // On a work plane the host is the element whose face the plane lies on (GeomRef
                    // of the plane reference); a plane on a level or a reference plane has none.
                    if (classByElement != null && classByElement.TryGetValue((uint)hostId, out var planeClass) && planeClass == 3607)
                        hostElement = hostElement.SketchPlaneDatumId is int faceOwner && byId.TryGetValue(faceOwner, out var owner)
                            && !owner.IsNativeLevel && owner.CategoryId != ReferencePlaneCategoryId && owner.CategoryId != GridsCategoryId
                            ? owner : null;
                    if (hostElement != null) label = FamilyAndTypeLabel(hostElement, byId);
                }
            }
            else if (e.NativeMasterSymbolId != null
                && e.Parameters.FirstOrDefault(p => p.Id == -1001352)?.Value is int levelId && levelId > 0
                && byId.TryGetValue(levelId, out var level) && level.IsNativeLevel
                && level.Parameters.FirstOrDefault(p => p.Id == -1007101)?.Value is string levelName && levelName.Length > 0
                && names.Get(Lookup.RevitUiText.LevelFamily) is string levelFamily)
                label = levelFamily + " : " + levelName;
            if (label != null) { host.Value = label; host.StorageType = StorageType.String; }
            else e.Parameters.Remove(host);
        }
    }

    /// <summary>
    /// "Work Plane" text (SKETCH_PLANE_PARAM) of an instance placed on a datum: its sketch plane
    /// lies on a level, or the reference plane is its stored host.
    /// </summary>
    internal static void ApplyWorkPlaneLabels(RevitFileInfo info, Lookup.RevitSystemFamilyNames? names)
    {
        if (names == null) return;
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);
        string? DatumLabel(Element datum)
        {
            if (datum.IsNativeLevel)
                return datum.Parameters.FirstOrDefault(p => p.Id == -1007101)?.Value is string { Length: > 0 } levelName
                    && names.Get(Lookup.RevitUiText.LevelFamily) is string levelFamily ? levelFamily + " : " + levelName : null;
            return datum.CategoryId == ReferencePlaneCategoryId && !string.IsNullOrEmpty(datum.Name)
                && names.Get(Lookup.RevitUiText.ReferencePlane) is string planeFamily ? planeFamily + " : " + datum.Name : null;
        }
        foreach (var e in info.Elements)
        {
            string? label = null;
            if (e.AssocSketchPlaneId is > 0 && byId.TryGetValue(e.AssocSketchPlaneId.Value, out var sketchPlane)
                && sketchPlane.SketchPlaneDatumId is int datum && byId.TryGetValue(datum, out var datumElement))
                label = DatumLabel(datumElement);
            else if (e.NativeMasterSymbolId != null && e.SerializedHostId is > 0 && byId.TryGetValue(e.SerializedHostId.Value, out var host))
            {
                // Placed on a reference plane, or on a level plane (Revit makes a hidden sketch
                // plane on the level as the work plane).
                if (host.CategoryId == ReferencePlaneCategoryId) label = DatumLabel(host);
                else if (host.SketchPlaneDatumId is int hostDatum && byId.TryGetValue(hostDatum, out var hostDatumElement))
                    label = DatumLabel(hostDatumElement);
            }
            if (label != null) SetParam(e, -1001380, "SKETCH_PLANE_PARAM", StorageType.String, label);
        }
    }

    private const int ReferencePlaneCategoryId = -2000530; // OST_CLines

    /// <summary>
    /// "&lt;family&gt;: &lt;type&gt;" of an element as the Host text shows it: the system family
    /// name Revit supplies for the type's root class, else the family of the type; null when the
    /// type or a name is unknown.
    /// </summary>
    private static string? FamilyAndTypeLabel(Element element, Dictionary<int, Element> byId)
    {
        if (element.TypeId <= 0 || !byId.TryGetValue(element.TypeId, out var type)) return null;
        static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
        // The name of an electrical panel is its Panel Name (RBS_ELEC_PANEL_NAME, the element name
        // of electrical equipment), not the name of its type; the name of any other element is not
        // read here (the stored element name is not reliable for floors).
        string? ownName = NonEmpty(type.Parameters.FirstOrDefault(p => p.Id == -1002001)?.Value as string) ?? NonEmpty(type.Name);
        string? panelName = NonEmpty(element.Parameters.FirstOrDefault(p => p.Id == -1140078)?.Value as string)
            ?? (element.CategoryId == -2001040 && NonEmpty(element.Name) is { } instanceName && instanceName != ownName ? instanceName : null);
        // Electrical equipment is named by its panel name; where the file gives none that is known
        // here, the type name would be wrong.
        if (panelName == null && element.CategoryId == -2001040) return null;
        string? typeName = panelName ?? ownName;
        string? family = NonEmpty(type.ValidatedSystemFamilyName)
            ?? NonEmpty(type.Parameters.FirstOrDefault(p => p.Id == -1002002)?.Value as string)
            ?? NonEmpty(type.FamilyName) ?? NonEmpty(element.FamilyName);
        return typeName == null || family == null ? null : family + " : " + typeName;
    }

    /// <summary>
    /// The base equipment of an electrical circuit. The circuit's own connector manager lists its connectors, each with a
    /// number (m_nIndex) and references; the connector that holds the reference of connection type 4 with an index of 50000
    /// or more to an element other than the circuit is the one of the panel. The base equipment is the first reference
    /// to another element of the connector that sits at array position equal to that number. The number equals the
    /// position unless a connector was removed; then the first reference of the connector at that position names a load, not the panel.
    /// The rule was measured on the circuits of seven files of Revit 2021 to 2025 (see the changelog). Null when there is no panel
    /// connector, no connector at that position or no other element in it.
    /// </summary>
    private static int? CircuitBaseEquipmentId(int circuitId, TfBlockDeserializer.NativeParams np)
    {
        for (int k = 0; np.Fields.ContainsKey($"771#{k}:m_nIndex"); k++)
            for (int i = 0, count = np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs#"); i < count; i++)
                if (np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs@{i}:type") == 4
                    && np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs@{i}:index") >= 50000
                    && np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs@{i}:id") is var panelId && panelId > 0 && panelId != circuitId)
                {
                    int position = np.Fields[$"771#{k}:m_nIndex"];
                    for (int j = 0, refs = np.Fields.GetValueOrDefault($"771#{position}:m_arrRefs#"); j < refs; j++)
                        if (np.Fields.GetValueOrDefault($"771#{position}:m_arrRefs@{j}:id") is var id && id > 0 && id != circuitId)
                            return id;
                    return null;
                }
        return null;
    }

    /// <summary>MEP system membership of ducts (3148) and pipes (3210).</summary>
    internal static void ApplyMepSystemParams(RevitFileInfo info, ParsingState state, Lookup.RevitSystemFamilyNames? names)
    {
        foreach (var e in info.Elements)
        {
            if (!state.NativeParams.TryGetValue((uint)e.Id, out var np)) continue;
            if (np.Fields.TryGetValue("3190:m_basicSystemType", out int basicType))
            {
                if (Lookup.RevitSystemFamilyNames.SystemClassification(basicType) is { } kind && names?.Get(kind) is string text)
                    SetParam(e, -1140325, "RBS_SYSTEM_CLASSIFICATION_PARAM", StorageType.String, text);
                continue;
            }
            if (np.Fields.TryGetValue("4215:m_bDefault", out int zoneDefault))
            {
                e.SerializedZoneIsDefault = zoneDefault != 0;
                continue;
            }
            if (np.Fields.TryGetValue("3471:m_zoneElementId", out int spaceZone) && spaceZone > 0)
                e.SerializedSpaceZoneId = spaceZone;
            if (np.StringFields.TryGetValue("3170:m_strName", out string? ownName))
            {
                SetParam(e, -1140324, "RBS_SYSTEM_NAME_PARAM", StorageType.String, ownName);
                e.SerializedMepSystem = (ownName, np.Fields.GetValueOrDefault("3170:m_typeId"));
                if (e.CategoryId == -2008037 || e.RootClassName == "RbsElectricalSystem") e.SerializedCircuitBaseEquipmentId = CircuitBaseEquipmentId(e.Id, np);
                continue;
            }
            if (!state.ElemClassIdx.TryGetValue((uint)e.Id, out var curveClass)) continue;
            if (curveClass == 1794)
            {
                ApplyFamilyInstanceConnectors(e, np);
                ApplyFamilyInstanceSystemParams(e, np, state, names);
                continue;
            }
            if (curveClass is not (3148 or 3210)) continue;
            if (curveClass == 3148)
            {
                // Duct taps keep their own results; the API reports the largest pressure drop of
                // them (and its loss coefficient), the stored duct values when there is no tap.
                var taps = new List<(double PressureDrop, double Coefficient)>();
                for (int k = 0; np.DoubleFields.TryGetValue($"3950#{k}:m_pressureDrop", out double tapDrop); k++)
                    taps.Add((tapDrop, np.DoubleFields.GetValueOrDefault($"3950#{k}:m_coefficient")));
                if (taps.Count > 0)
                {
                    var largest = taps.MaxBy(tap => tap.PressureDrop);
                    SetParam(e, -1114108, "RBS_PRESSURE_DROP", StorageType.Double, largest.PressureDrop);
                    SetParam(e, -1114124, "RBS_LOSS_COEFFICIENT", StorageType.Double, largest.Coefficient);
                }
                else if (np.DoubleFields.TryGetValue("1219:m_dPressureDrop", out double storedDrop)
                    && np.DoubleFields.TryGetValue("1219:m_dLossCoefficient", out double storedCoefficient))
                {
                    SetParam(e, -1114108, "RBS_PRESSURE_DROP", StorageType.Double, storedDrop);
                    SetParam(e, -1114124, "RBS_LOSS_COEFFICIENT", StorageType.Double, storedCoefficient);
                }
            }
            else if (np.Fields.TryGetValue("602:m_idSegment", out int segmentId) && segmentId > 0
                && state.NativeParams.TryGetValue((uint)segmentId, out var segment))
            {
                // PipeSegment: material, schedule/type, roughness and description.
                if (segment.Fields.TryGetValue("2968:m_materialId", out int segmentMaterial))
                    SetParam(e, -1140202, "RBS_PIPE_MATERIAL_PARAM", StorageType.ElementId, segmentMaterial);
                if (segment.Fields.TryGetValue("2967:m_scheduleTypeId", out int segmentSchedule))
                    SetParam(e, -1140200, "RBS_PIPE_CLASS_PARAM", StorageType.ElementId, segmentSchedule);
                if (segment.DoubleFields.TryGetValue("2968:m_roughness", out double segmentRoughness))
                    SetParam(e, -1140204, "RBS_PIPE_ROUGHNESS_PARAM", StorageType.Double, segmentRoughness);
                if (segment.StringFields.TryGetValue("2968:m_description", out string? segmentDescription))
                    SetParam(e, -1140279, "RBS_SEGMENT_DESCRIPTION_PARAM", StorageType.String, segmentDescription);
            }
            var connectors = new List<(int System, int Section, int Direction)>();
            for (int k = 0; np.Fields.TryGetValue($"3556#{k}:m_idSystem", out int connectorSystem); k++)
                connectors.Add((connectorSystem, np.Fields.GetValueOrDefault($"3556#{k}:m_nSection"),
                    np.Fields.GetValueOrDefault($"3556#{k}:m_eDirection")));
            if (connectors.Count == 0) continue;
            int typeParam = curveClass == 3148 ? -1140333 : -1140334;
            string typeParamName = curveClass == 3148 ? "RBS_DUCT_SYSTEM_TYPE_PARAM" : "RBS_PIPING_SYSTEM_TYPE_PARAM";
            int systemId = connectors[0].System;
            if (systemId > 0 && state.NativeParams.TryGetValue((uint)systemId, out var system)
                && system.StringFields.TryGetValue("3170:m_strName", out string? systemName)
                && system.Fields.TryGetValue("3170:m_typeId", out int systemType))
            {
                SetParam(e, -1140324, "RBS_SYSTEM_NAME_PARAM", StorageType.String, systemName);
                SetParam(e, typeParam, typeParamName, StorageType.ElementId, systemType);
                string abbreviation = "";
                if (state.NativeParams.TryGetValue((uint)systemType, out var type))
                {
                    abbreviation = type.StringFields.GetValueOrDefault("3190:m_abbreviation") ?? "";
                    if (type.Fields.TryGetValue("3190:m_basicSystemType", out int systemBasic)
                        && Lookup.RevitSystemFamilyNames.SystemClassification(systemBasic) is { } classification
                        && names?.Get(classification) is string classificationText)
                        SetParam(e, -1140325, "RBS_SYSTEM_CLASSIFICATION_PARAM", StorageType.String, classificationText);
                }
                SetParam(e, -1150468, "RBS_DUCT_PIPE_SYSTEM_ABBREVIATION_PARAM", StorageType.String, abbreviation);
                int section;
                if (curveClass == 3148)
                {
                    var ends = connectors.Take(2).ToList();
                    section = ends.Count(c => c.Direction == 1) == 1 ? ends.First(c => c.Direction == 1).Section : ends[0].Section;
                }
                else section = connectors[0].Section;
                if (curveClass == 3148 || section > 0)
                    SetParam(e, -1114125, "RBS_SECTION", StorageType.Integer, section);
            }
            else
            {
                e.Parameters.RemoveAll(p => p.Id == -1140324);
                SetParam(e, typeParam, typeParamName, StorageType.ElementId, -1);
                SetParam(e, -1150468, "RBS_DUCT_PIPE_SYSTEM_ABBREVIATION_PARAM", StorageType.String, "");
                if (names?.Get(Lookup.RevitUiText.SystemUndefined) is string undefined)
                    SetParam(e, -1140325, "RBS_SYSTEM_CLASSIFICATION_PARAM", StorageType.String, undefined);
                if (curveClass == 3148)
                    SetParam(e, -1114125, "RBS_SECTION", StorageType.Integer,
                        connectors.Count == 2 ? connectors[0].Section : 0);
            }
        }
    }

    /// <summary>
    /// The connectors of every element with a connector manager and the elements they are connected
    /// to.
    /// </summary>
    internal static void ApplyConnectorGraph(RevitFileInfo info, ParsingState state)
    {
        foreach (var e in info.Elements)
        {
            e.SerializedConnectorGraph = null;
            e.SerializedConnectorSystems = null;
            e.SerializedConnectorModes = null;
            e.SerializedCurveSystemTypeId = state.NativeParams.TryGetValue((uint)e.Id, out var curveParams)
                && curveParams.Fields.TryGetValue("1219:m_idSystemType", out int storedSystemType) ? storedSystemType : null;
            if (!state.NativeParams.TryGetValue((uint)e.Id, out var np) || !np.Fields.ContainsKey("771#0:m_nIndex")) continue;
            var connectors = new List<(int Index, int Direction, IReadOnlyList<(int Id, int Index, int Type)> Refs)>();
            // The system of a connector sits in the same calculation as its direction: the segment
            // calculation of a curve, the hvac or piping one of a family instance (not read when
            // the calculations of the domains are mixed).
            string? calculation = np.Fields.ContainsKey("3556#0:m_idSystem") ? "3556"
                : e.SerializedMepConnectors == null ? null
                : np.Fields.ContainsKey("2381#0:m_idSystem") ? "2381"
                : np.Fields.ContainsKey("2382#0:m_idSystem") ? "2382" : null;
            var systems = new List<int>();
            var modes = new List<int>();
            // Instances that mix the electrical domain with piping or hvac keep one calculation per
            // connector in connector order across the classes ("calc#n").
            int connectorCount = 0;
            while (np.Fields.ContainsKey($"771#{connectorCount}:m_nIndex")) connectorCount++;
            bool mixedCalculations = calculation == null && e.SerializedMepConnectors == null && connectorCount > 0
                && np.Fields.ContainsKey($"calc#{connectorCount - 1}:class") && !np.Fields.ContainsKey($"calc#{connectorCount}:class");
            for (int k = 0; np.Fields.TryGetValue($"771#{k}:m_nIndex", out int index); k++)
            {
                // A walk that knows the objects each connector queued names the calculation of that connector itself.
                if (np.Fields.TryGetValue($"771#{k}:m_mode", out int mode))
                {
                    modes.Add(mode);
                    string? ownKey = np.Fields.TryGetValue($"771#{k}:calcClass", out int calcClass)
                        ? $"{calcClass.ToString(System.Globalization.CultureInfo.InvariantCulture)}#{np.Fields.GetValueOrDefault($"771#{k}:calcOccurrence").ToString(System.Globalization.CultureInfo.InvariantCulture)}:"
                        : null;
                    systems.Add(ownKey == null ? 0 : np.Fields.GetValueOrDefault(ownKey + "m_idSystem"));
                    int ownCount = np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs#");
                    var ownRefs = new List<(int Id, int Index, int Type)>(ownCount);
                    for (int i = 0; i < ownCount; i++)
                        ownRefs.Add((np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs@{i}:id"),
                            np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs@{i}:index"),
                            np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs@{i}:type")));
                    connectors.Add((index, ownKey == null ? 0 : calcClass == 2380 ? -1 : np.Fields.GetValueOrDefault(ownKey + "m_eDirection"), ownRefs));
                    continue;
                }
                (string Class, string Occurrence)? ownCalculation = mixedCalculations
                    ? (np.Fields[$"calc#{k}:class"].ToString(System.Globalization.CultureInfo.InvariantCulture),
                        np.Fields[$"calc#{k}:occurrence"].ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;
                systems.Add(ownCalculation is { } own ? np.Fields.GetValueOrDefault($"{own.Class}#{own.Occurrence}:m_idSystem")
                    : calculation == null ? 0 : np.Fields.GetValueOrDefault($"{calculation}#{k}:m_idSystem"));
                int count = np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs#");
                var refs = new List<(int Id, int Index, int Type)>(count);
                for (int i = 0; i < count; i++)
                    refs.Add((np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs@{i}:id"),
                        np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs@{i}:index"),
                        np.Fields.GetValueOrDefault($"771#{k}:m_arrRefs@{i}:type")));
                // A family instance keeps the direction in its hvac/piping connector calculation
                // (an electrical one is -1), a duct/pipe/tray curve in its segment calculation; a
                // connector without either is bidirectional.
                int direction = e.SerializedMepConnectors is { } mep && k < mep.Count && mep[k].Index == index
                    ? mep[k].Direction
                    : ownCalculation is { } mixed ? (mixed.Class == "2380" ? -1 : np.Fields.GetValueOrDefault($"{mixed.Class}#{mixed.Occurrence}:m_eDirection"))
                    : np.Fields.GetValueOrDefault($"3556#{k}:m_eDirection");
                connectors.Add((index, direction, refs));
            }
            e.SerializedConnectorGraph = connectors;
            e.SerializedConnectorSystems = systems;
            if (modes.Count == connectors.Count) e.SerializedConnectorModes = modes;
        }
    }

    /// <summary>
    /// System data of duct/pipe fittings, terminals, accessories, sprinklers and plumbing fixtures:
    /// the stored hvac (2381) or piping (2382) connector calculations in connector order give the
    /// system of each connector and its basic system type.
    /// </summary>
    private static void ApplyFamilyInstanceConnectors(Element e, TfBlockDeserializer.NativeParams np)
    {
        e.SerializedMepConnectors = null;
        if (Environment.GetEnvironmentVariable("RVTIO_PORT_DBG") is { } dbgIds && dbgIds.Split(',').Contains(e.Id.ToString()))
            DiagnosticSink.WriteLine($"portdbg {e.Id} " + string.Join(" ", np.Fields.Where(pair => pair.Key.StartsWith("771#") || pair.Key.StartsWith("2380#") || pair.Key.StartsWith("2381#") || pair.Key.StartsWith("2382#")).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)));        string[] present = new[] { ("2381", "m_eDirection"), ("2382", "m_eDirection"), ("2380", "m_idDownstreamCircuit") }
            .Where(candidate => np.Fields.ContainsKey($"{candidate.Item1}#0:{candidate.Item2}")).Select(candidate => candidate.Item1).ToArray();
        if (present.Length != 1) return;
        string prefix = present[0];
        string probe = prefix == "2380" ? "m_idDownstreamCircuit" : "m_eDirection";
        var connectors = new List<(int Index, int Direction, int SystemType)>();
        for (int k = 0; np.Fields.TryGetValue($"{prefix}#{k}:{probe}", out int direction); k++)
        {
            if (!np.Fields.TryGetValue($"771#{k}:m_nIndex", out int index)) return;
            connectors.Add((index, prefix == "2380" ? -1 : direction, np.Fields.GetValueOrDefault($"{prefix}#{k}:m_eSystemType")));
        }
        if (np.Fields.ContainsKey($"771#{connectors.Count}:m_nIndex")) return; // more connectors than calculations
        if (connectors.Count > 0) e.SerializedMepConnectors = connectors;
    }

    private static void ApplyFamilyInstanceSystemParams(Element e, TfBlockDeserializer.NativeParams np, ParsingState state,
        Lookup.RevitSystemFamilyNames? names)
    {
        if (e.CategoryId is not (-2008010 or -2008013 or -2008016 or -2008049 or -2008055 or -2008099 or -2001160)) return;
        string prefix = np.Fields.ContainsKey("2381#0:m_idSystem") ? "2381" : np.Fields.ContainsKey("2382#0:m_idSystem") ? "2382" : "";
        if (prefix.Length == 0 || (prefix == "2381" ? "2382" : "2381") is var other && np.Fields.ContainsKey(other + "#0:m_idSystem")) return;
        var connectors = new List<(int System, int Kind)>();
        for (int k = 0; np.Fields.TryGetValue($"{prefix}#{k}:m_idSystem", out int system); k++)
            connectors.Add((system, np.Fields.GetValueOrDefault($"{prefix}#{k}:m_eSystemType")));
        var systems = connectors.Select(c => c.System).Where(s => s > 0).Distinct().ToList();
        var systemNames = new List<string>();
        var types = new List<int>();
        foreach (int systemId in systems)
        {
            if (!state.NativeParams.TryGetValue((uint)systemId, out var system)
                || !system.StringFields.TryGetValue("3170:m_strName", out string? name)
                || !system.Fields.TryGetValue("3170:m_typeId", out int typeId)) return;
            if (!systemNames.Contains(name)) systemNames.Add(name);
            types.Add(typeId);
        }
        SetParam(e, -1140324, "RBS_SYSTEM_NAME_PARAM", StorageType.String, string.Join(",", systemNames));
        int typeParam = prefix == "2381" ? -1140333 : -1140334;
        SetParam(e, typeParam, prefix == "2381" ? "RBS_DUCT_SYSTEM_TYPE_PARAM" : "RBS_PIPING_SYSTEM_TYPE_PARAM",
            StorageType.ElementId, systems.Count == 1 ? types[0] : -1);
        var abbreviations = types.Select(t => state.NativeParams.TryGetValue((uint)t, out var type)
            ? type.StringFields.GetValueOrDefault("3190:m_abbreviation") ?? "" : "").Distinct();
        SetParam(e, -1150468, "RBS_DUCT_PIPE_SYSTEM_ABBREVIATION_PARAM", StorageType.String, string.Join(",", abbreviations));
        var texts = new List<string>();
        foreach (var (_, kind) in connectors)
        {
            string? text = Lookup.RevitSystemFamilyNames.SystemClassification(kind) is { } classification ? names?.Get(classification) : null;
            if (text == null) return;
            if (texts.Count == 0 || texts[^1] != text) texts.Add(text);
        }
        SetParam(e, -1140325, "RBS_SYSTEM_CLASSIFICATION_PARAM", StorageType.String, string.Join(",", texts));
    }

    private static void TraceElement(RevitFileInfo info, string stage)
    {
        if (Environment.GetEnvironmentVariable("RVTIO_TRACE_ID") is not { } id) return;
        foreach (var e in info.Elements.Where(x => x.Id.ToString() == id))
            DiagnosticSink.WriteLine($"TRACE {stage} id={e.Id} cat={e.CategoryId} phase={string.Join(",", e.Parameters.Where(p => p.Id == -1012100).Select(p => p.Value))} count={e.Parameters.Count}");
    }

    /// <summary>Runs after categories are final.</summary>
    internal static void PruneNativeFieldTableOutsideDomains(RevitFileInfo info, IReadOnlyDictionary<uint, ushort>? classByElement)
    {
        if (classByElement == null) return;
        foreach (var e in info.Elements)
        {
            if (e.NativeTableAddedParams is not { Count: > 0 } added) continue;
            string rootClass = classByElement.TryGetValue((uint)e.Id, out var cls) ? cls.ToString(System.Globalization.CultureInfo.InvariantCulture) : "None";
            string domain = rootClass + "/" + (e.CategoryId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None");
            bool InDomain(string[] domains) => Array.IndexOf(domains, domain) >= 0 || Array.IndexOf(domains, rootClass) >= 0;
            foreach (var group in added.GroupBy(row => row.ParamId))
            {
                var rows = group.ToList();
                int paramId = group.Key;
                // PHASE_CREATED is what the IFC exporter takes as a placed element without a
                // category (a project phase reference): a class-wide table row must not hand it to
                // elements the legacy phase pass left without.
                var matching = paramId == -1012100 && e.CategoryId == null
                    ? default : rows.LastOrDefault(row => InDomain(row.Domains));
                if (matching.Domains == null)
                {
                    // No row of the table applies to this element: drop what the table created,
                    // give back what it overwrote (unless a later pass already replaced the table's
                    // value).
                    if (e.NativeTableCreatedParams?.Contains(paramId) == true) e.Parameters.RemoveAll(p => p.Id == paramId);
                    else if (e.NativeTablePrevious != null && e.NativeTablePrevious.TryGetValue(paramId, out var previous))
                    {
                        var overwritten = e.Parameters.FirstOrDefault(p => p.Id == paramId);
                        if (overwritten != null && Equals(overwritten.Value, rows[^1].Value))
                        {
                            overwritten.Value = previous.Value;
                            overwritten.StorageType = previous.Storage;
                            overwritten.HasNoRevitValue = previous.HasNoRevitValue;
                        }
                    }
                    continue;
                }
                // Rows are applied in table order, an out-of-domain row may have overwritten the
                // in-domain value.
                var last = rows[^1];
                var parameter = e.Parameters.FirstOrDefault(p => p.Id == paramId);
                if (!InDomain(last.Domains) && parameter != null && Equals(parameter.Value, last.Value))
                {
                    parameter.Value = matching.Value;
                    parameter.StorageType = matching.Storage;
                    parameter.HasNoRevitValue = matching.HasNoRevitValue;
                }
            }
            e.NativeTableAddedParams = null;
            e.NativeTableCreatedParams = null;
            e.NativeTablePrevious = null;
        }
    }

    /// <summary>
    /// Duct (3148), pipe (3210), conduit (3134) and cable tray (598) from the stored location line
    /// (the RbsCurveDriver's GLine: origin, unit direction, end parameters): Length is the
    /// parameter span, Area the surface.
    /// </summary>
    internal static void ApplyMepCurveGeometry(TfBlockDeserializer.NativeParams np, ushort curveClass,
        Action<int, StorageType, object, bool> apply)
    {
        var d = np.DoubleFields;
        double[] origin = new double[3], direction = new double[3];
        for (int i = 0; i < 3; i++)
            if (!d.TryGetValue("87:m_pCrv.origin@" + i, out origin[i]) || !d.TryGetValue("87:m_pCrv.dirVec@" + i, out direction[i])
                || !double.IsFinite(origin[i]) || !double.IsFinite(direction[i])) return;
        if (!d.TryGetValue("87:m_pCrv.endParams@0", out double endParam0) || !d.TryGetValue("87:m_pCrv.endParams@1", out double endParam1)
            || !double.IsFinite(endParam0) || !double.IsFinite(endParam1)) return;
        double directionLength = Math.Sqrt(direction[0] * direction[0] + direction[1] * direction[1] + direction[2] * direction[2]);
        if (directionLength < 1e-9) return;
        double length = Math.Abs(endParam1 - endParam0) * directionLength;
        double horizontal = Math.Sqrt(direction[0] * direction[0] + direction[1] * direction[1]) / directionLength;
        double zStart = origin[2] + direction[2] * endParam0, zEnd = origin[2] + direction[2] * endParam1;
        apply(-1004005, StorageType.Double, length, true); // CURVE_ELEM_LENGTH
        if (!d.TryGetValue("602:m_dOffsetStart", out double offsetStart) || !d.TryGetValue("602:m_dOffsetEnd", out double offsetEnd)
            || !d.TryGetValue("602:m_dWidthOrDiameter", out double width)) return;
        d.TryGetValue("602:m_dHeight", out double height);
        double top = Math.Max(offsetStart, offsetEnd), bottom = Math.Min(offsetStart, offsetEnd);
        double slope = horizontal > 1e-6 ? Math.Abs(offsetStart - offsetEnd) / (length * horizontal) : double.NaN;
        switch (curveClass)
        {
            case 3148: // Duct: the stored height is the diameter of a round duct
            {
                bool round = d.TryGetValue("1219:m_dEqDiameter", out double equivalent) && Math.Abs(equivalent - width) <= 1e-9 * Math.Max(1, width);
                apply(-1114120, StorageType.Double, round ? Math.PI * width * length : 2 * (width + height) * length, true);
                apply(-1140239, StorageType.Double, top + height / 2 * horizontal, true);
                apply(-1140240, StorageType.Double, bottom - height / 2 * horizontal, true);
                if (!double.IsNaN(slope)) apply(-1140255, StorageType.Double, slope, true);
                break;
            }
            case 3210: // Pipe
            {
                apply(-1114120, StorageType.Double, Math.PI * width * length, true);
                if (d.TryGetValue("2964:m_dOuterDiameter", out double outer))
                {
                    apply(-1155114, StorageType.Double, top + outer / 2 * horizontal, true);
                    apply(-1155115, StorageType.Double, bottom - outer / 2 * horizontal, true);
                }
                if (d.TryGetValue("2964:m_dInnerDiameter", out double inner))
                    apply(-1140237, StorageType.Double, Math.Min(zStart, zEnd) - inner / 2 * horizontal, true);
                if (!double.IsNaN(slope)) apply(-1140256, StorageType.Double, slope, true);
                break;
            }
            case 3134 when d.TryGetValue("749:m_dOuterDiameter", out double conduitOuter): // Conduit
                apply(-1140124, StorageType.Double, top + conduitOuter / 2 * horizontal, true);
                apply(-1140125, StorageType.Double, bottom - conduitOuter / 2 * horizontal, true);
                break;
            case 598: // Cable tray
                apply(-1140124, StorageType.Double, top + height / 2 * horizontal, true);
                apply(-1140125, StorageType.Double, bottom - height / 2 * horizontal, true);
                break;
        }
        if (curveClass is 3148 or 3210) apply(-1114132, StorageType.Double, offsetStart, true); // RBS_OFFSET_PARAM
    }

    /// <summary>Flexible duct (3177) and flexible pipe (3180): Length is the arc length of the drive spline.</summary>
    internal static void ApplyFlexCurveGeometry(TfBlockDeserializer.NativeParams np, ushort curveClass,
        Action<int, StorageType, object, bool> apply)
    {
        if (curveClass is 3177 or 3180 && FlexCurveLength(np) is double length)
            apply(-1004005, StorageType.Double, length, true); // CURVE_ELEM_LENGTH
    }

    /// <summary>
    /// Length (feet) of a flexible duct or pipe curve: the arc length of its drive curve, a piecewise cubic
    /// Hermite spline through the stored nodes.
    /// </summary>
    internal static double? FlexCurveLength(TfBlockDeserializer.NativeParams np)
    {
        var d = np.DoubleFields;
        if (!d.TryGetValue("87:m_pCrv.nodes", out double count) || count < 2 || count > 10_000) return null;
        var nodes = new double[(int)count][];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new double[7];
            for (int k = 0; k < 7; k++)
                if (!d.TryGetValue($"87:m_pCrv.node{i}@{k}", out nodes[i][k]) || !double.IsFinite(nodes[i][k])) return null;
        }
        // 16-point Gauss-Legendre on [-1, 1].
        double[] abscissas = [0.0950125098376374, 0.2816035507792589, 0.4580167776572274, 0.6178762444026438,
            0.7554044083550030, 0.8656312023878318, 0.9445750230732326, 0.9894009349916499];
        double[] weights = [0.1894506104550685, 0.1826034150449236, 0.1691565193950025, 0.1495959888165767,
            0.1246289712555339, 0.0951585116824928, 0.0622535239386479, 0.0271524594117541];
        double total = 0;
        for (int segment = 0; segment + 1 < nodes.Length; segment++)
        {
            double[] a = nodes[segment], b = nodes[segment + 1];
            double span = b[0] - a[0];
            if (!(span > 0)) return null;
            double Speed(double t)
            {
                double h00 = 6 * t * t - 6 * t, h10 = 3 * t * t - 4 * t + 1, h01 = -h00, h11 = 3 * t * t - 2 * t;
                double sum = 0;
                for (int axis = 0; axis < 3; axis++)
                {
                    double derivative = h00 * a[1 + axis] + h10 * a[4 + axis] * span + h01 * b[1 + axis] + h11 * b[4 + axis] * span;
                    sum += derivative * derivative;
                }
                return Math.Sqrt(sum);
            }
            // Two panels of 16 points keep the quadrature error far below 1e-12 for the smooth
            // cubic.
            for (int panel = 0; panel < 2; panel++)
            {
                double low = panel * 0.5, half = 0.25;
                for (int i = 0; i < abscissas.Length; i++)
                    total += half * weights[i] * (Speed(low + half * (1 - abscissas[i])) + Speed(low + half * (1 + abscissas[i])));
            }
        }
        return total;
    }

    /// <summary>
    /// "Detail Line" (CURVE_IS_DETAIL) of a curve element is 1 exactly when the curve is owned by a
    /// view (Element.m_ownerDBViewId set).
    /// </summary>
    internal static void ApplyDetailCurveFlag(RevitFileInfo info, IReadOnlyDictionary<uint, ushort>? classByElement)
    {
        if (classByElement == null) return;
        foreach (var e in info.Elements)
        {
            if (e.SerializedOwnerDbViewId is not int ownerView
                || !classByElement.TryGetValue((uint)e.Id, out var cls) || cls != 838) continue; // CurveElem
            SetParam(e, -1004011, "CURVE_IS_DETAIL", StorageType.Integer, ownerView > 0 ? 1 : 0);
        }
    }

    internal static void ApplyCrossSectionRotation(RevitFileInfo info)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);
        foreach (var e in info.Elements)
        {
            if (e.CurveDriverRotation is not double rotation) continue;
            bool trussMember = e.SerializedHostId is int host && host > 0
                && byId.TryGetValue(host, out var hostElement) && hostElement.Category == "OST_StructuralTruss";
            if (trussMember) e.Parameters.RemoveAll(p => p.Id == -1001586);
            else SetParam(e, -1001586, "STRUCTURAL_BEND_DIR_ANGLE", StorageType.Double, rotation);
        }
    }

    /// <summary>
    /// Phase Created / Phase Demolished are the stored phase ids whenever they name a ProjectPhase
    /// element, including id 0 (phases of older files keep low ids such as 0 and 2).
    /// </summary>
    internal static void ApplyStoredPhases(RevitFileInfo info, IReadOnlyDictionary<uint, ushort>? classByElement,
        IReadOnlyCollection<int>? projectPhaseOrder = null)
    {
        if (classByElement == null) return;
        // A phase is a ProjectPhase element or an entry of the project's single phase-order record.
        bool IsPhase(int id) => id >= 0 && ((classByElement.TryGetValue((uint)id, out var cls) && cls == 3065)
            || projectPhaseOrder?.Contains(id) == true);
        foreach (var e in info.Elements)
        {
            if (e.SerializedCreatedPhaseId is int created && IsPhase(created)
                && e.Parameters.Any(p => p.Id == -1012100))
                SetParam(e, -1012100, "PHASE_CREATED", StorageType.ElementId, created);
            if (e.SerializedDemolishedPhaseId is int demolished && (demolished == -1 || IsPhase(demolished))
                && e.Parameters.Any(p => p.Id == -1012101))
                SetParam(e, -1012101, "PHASE_DEMOLISHED", StorageType.ElementId, demolished);
        }
    }

    /// <summary>
    /// Structural framing and column connection parameters (Start/End Connection, Base/Top
    /// Connection) exist when the family's Symbolic Representation is 1 (from project settings) and
    /// carry the stored m_connectionType ids.
    /// </summary>
    internal static void ApplyStructuralConnectionParams(RevitFileInfo info)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);
        foreach (var e in info.Elements)
        {
            if (e.StructuralConnectionTypes is not { } connections || e.NativeMasterSymbolId is not int symbolId
                || !byId.TryGetValue(symbolId, out var symbol) || symbol.SymbolFamilyId is not int familyId
                || !byId.TryGetValue(familyId, out var family)
                || family.Parameters.FirstOrDefault(p => p.Id == -1005197)?.Value is not int symbolicRepresentation
                || symbolicRepresentation != 1) continue;
            if (e.CategoryId == -2001320)
            {
                SetParam(e, -1018801, "STRUCT_CONNECTION_BEAM_START", StorageType.ElementId, connections.First);
                SetParam(e, -1018802, "STRUCT_CONNECTION_BEAM_END", StorageType.ElementId, connections.Second);
            }
            else if (e.CategoryId == -2001330)
            {
                SetParam(e, -1018804, "STRUCT_CONNECTION_COLUMN_BASE", StorageType.ElementId, connections.First);
                SetParam(e, -1018803, "STRUCT_CONNECTION_COLUMN_TOP", StorageType.ElementId, connections.Second);
            }
        }
    }

    private static readonly int[] RoomBuildingTypeLoadParams =
    [
        -1114175, // ROOM_AREA_PER_PERSON_PARAM
        -1114189, // ROOM_PEOPLE_LATENT_HEAT_GAIN_PER_PERSON_PARAM
        -1114239, // ROOM_PEOPLE_SENSIBLE_HEAT_GAIN_PER_PERSON_PARAM
        -1114219, // ROOM_DESIGN_POWER_LOAD_PER_AREA_PARAM
        -1114220, // ROOM_DESIGN_LIGHTING_LOAD_PER_AREA_PARAM
        -1150188, // ROOM_PLENUM_LIGHTING_PARAM
    ];

    internal static void ApplyRoomBuildingTypeLoads(RevitFileInfo info)
    {
        var settings = info.Elements.Where(e => e.EnergyBuildingTypeId != null).ToList();
        if (settings.Count != 1) return;
        var buildingType = info.Elements.FirstOrDefault(e => e.Id == settings[0].EnergyBuildingTypeId);
        if (buildingType == null) return;
        var values = new Dictionary<int, double>();
        foreach (int id in RoomBuildingTypeLoadParams)
            if (buildingType.Parameters.FirstOrDefault(p => p.Id == id)?.Value is double value) values[id] = value;
        foreach (var room in info.Elements)
        {
            if (room.CategoryId != -2000160) continue; // OST_Rooms
            foreach (var (id, value) in values)
            {
                BipNames.Lookup.TryGetValue(id, out string? name);
                SetParam(room, id, name ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture), StorageType.Double, value);
            }
            if (values.TryGetValue(-1114239, out double sensible) && values.TryGetValue(-1114189, out double latent))
                SetParam(room, -1114188, "ROOM_PEOPLE_TOTAL_HEAT_GAIN_PER_PERSON_PARAM", StorageType.Double, sensible + latent);
        }
    }

    internal static void ApplyRoomLevelParams(RevitFileInfo info)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);
        foreach (var e in info.Elements)
        {
            if (e.CategoryId is not (-2000160 or -2003200)) continue; // OST_Rooms, OST_Areas
            if (e.Parameters.FirstOrDefault(p => p.Id == -1006916)?.Value is not int levelId || levelId <= 0
                || !byId.TryGetValue(levelId, out var level) || !level.IsNativeLevel) continue;
            if (level.Parameters.FirstOrDefault(p => p.Id == -1007101)?.Value is string levelName && levelName.Length > 0)
                SetParam(e, -1007101, "LEVEL_NAME", StorageType.String, levelName);
            if (level.Parameters.FirstOrDefault(p => p.Id == -1006939)?.Value is double computationHeight)
                SetParam(e, -1006928, "ROOM_COMPUTATION_HEIGHT", StorageType.Double, computationHeight);
        }
    }

    private static void SetParam(Element e, int id, string name, StorageType storage, object value)
    {
        var existing = e.Parameters.FirstOrDefault(p => p.Id == id);
        if (existing != null) { existing.Value = value; existing.StorageType = storage; }
        else e.Parameters.Add(BipHelpers.MakeBipParam(id, name, storage, value));
        e.BoundingBoxDimensionParamIds?.Remove(id);
    }

    /// <summary>Which instances carry the parameter is left to the existing passes.</summary>
    internal static void ApplyInstanceElevationFromLevel(RevitFileInfo info)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);
        foreach (var e in info.Elements)
        {
            if (e.NativeMasterSymbolId == null || e.Transform?.Origin is not { Length: 3 } origin) continue;
            var elevationParam = e.Parameters.FirstOrDefault(p => p.Id == -1001360);
            if (elevationParam == null) continue;
            if (e.Parameters.FirstOrDefault(p => p.Id == -1002108)?.Value is not int host || host != -1) continue;
            if (e.Parameters.FirstOrDefault(p => p.Id == -1001352)?.Value is not int level || level <= 0
                || !byId.TryGetValue(level, out var levelElement)
                || levelElement.Parameters.FirstOrDefault(p => p.Id == -1007102)?.Value is not double levelElevation)
                continue;
            elevationParam.Value = origin[2] - levelElevation;
            elevationParam.StorageType = StorageType.Double;
        }
    }

    private static void AddBipIfMissing(Element e, int id, string name, int value)
    {
        foreach (var p in e.Parameters) if (p.Id == id) return;
        e.Parameters.Add(BipHelpers.MakeBipParam(id, name, StorageType.ElementId, value));
    }

    /// <summary>
    /// System/annotation categories assigned via the empirical block-class registry whose elements
    /// report unhosted defaults (HOST_ID=-1, INSTANCE_OFFSET_POS=0) in the Revit API - same
    /// defaults the no-category branch used to give them.
    /// </summary>
    private static readonly HashSet<int> SystemCatsWithUnhostedDefaults = new()
    {
        -2009645, -2009640, -2009609, -2000263, -2000576, -2000573, -2008148, -2000055,
        -2000300, -2000278, -2008121, -2000924, -2000191, -2000262, -2008163, -2000552,
        -2008143, -2005210, -2000530, -2003201, -2000480, -2000051, -2008119, -2008120,
        -2000510, -2000240, -2000279, -2000045, -2000700,
    };

    /// <summary>
    /// What the Revit API reports as ELEM_CATEGORY_PARAM for elements whose category we assigned
    /// heuristically (they never went through AddComputedElementParams).
    /// </summary>
    private static readonly Dictionary<int, int> HeuristicCatParamFix = new()
    {
        [-2000014] = -2000450, // windows-propagated -> OST_WindowTags
        [-2000011] = -2000011,
        [-2000045] = -2000045, // heuristic sketch lines: own id
        [-2000171] = -2000171, // heuristic mullions: own id
        [-2000023] = -2000460, // doors-propagated -> OST_DoorTags
        [-2001360] = -2005021, // planting -> OST_PlantingTags
        [-2001268] = -2001269, // property-line segment -> its tags
        [-2000515] = -2000510, // viewport labels reported as OST_Viewports
        [-2001271] = -2009609, // "base point"-propagated sun-path elements -> OST_SunStudy
        [-2000260] = -2000260, // dimensions report their own category
        [-2000150] = -2000150, // generic annotation: correct as-is
        [-2000280] = -1, // title blocks: -1 dominates
        [-2000700] = -1, // heuristic materials ( report -1)
        [-2000510] = -1, // viewports
        [-2000576] = -1, // preview legend components
        [-2000220] = -1, // grids
        [-2005100] = -2000263, // "spot elev symbols" are SpotDimension elements -> OST_SpotElevations
        [-2009645] = -2009645, // analytical nodes (Hub/ReferencePoint): own id
        [-2003201] = -1, // area schemes
        [-2000038] = -1, // heuristic ceilings
        [-2000400] = -1, // section heads
        [-2000946] = -1, // railing top rail
        [-2000573] = -1, // schedules
        [-2006045] = -1, // elevation marks
        [-2000278] = -1, // viewers
        [-2009609] = -1, // sun study
        [-2000550] = -1, // color fill legends
        [-2009636] = -1, // analytical columns
    };

    /// <summary>
    /// Post-resolution parameter fills: (1) elements with
    /// NO category get ELEM_CATEGORY_PARAM(-1140362)/_MT(-1140363) = -1 - this is exactly what the
    /// Revit API returns for uncategorized (internal) elements.
    /// </summary>
    private static void AddCategoryAndFamilyParams(RevitFileInfo info)
    {
        foreach (var e in info.Elements)
        {
            bool hasCat = false, hasCatMt = false, hasFam = false;
            int typeVal = -1;
            foreach (var p in e.Parameters)
            {
                switch (p.Id)
                {
                    case -1140362: hasCat = true; break;
                    case -1140363: hasCatMt = true; break;
                    case -1002051: hasFam = true; break;
                    case -1002050: if (p.Value is int tv && tv > 1000) typeVal = tv; break;
                }
            }

            if (!e.CategoryId.HasValue || SystemCatsWithUnhostedDefaults.Contains(e.CategoryId.Value))
            {
                // Uncategorized elements and system/annotation classes categorized via the
                // empirical block registry: the export reports unhosted defaults for them (they
                // used to take this branch before the registry categorized them).
                AddBipIfMissing(e, -1002108, "HOST_ID_PARAM", -1);
                if (e.Parameters.All(p => p.Id != -1001370))
                    e.Parameters.Add(BipHelpers.MakeBipParam(-1001370, "INSTANCE_OFFSET_POS_PARAM", StorageType.Integer, 0, hasNoRevitValue: true));
            }

            AddBipIfMissing(e, -1013201, "DESIGN_OPTION_ID", -1);
            // PHASE_DEMOLISHED: -1 ("None") is the only value seen.
            AddBipIfMissing(e, -1012101, "PHASE_DEMOLISHED", -1);

            int catVal = int.MinValue;
            if (!e.CategoryId.HasValue) catVal = -1;
            else if (HeuristicCatParamFix.TryGetValue(e.CategoryId.Value, out int fix)) catVal = fix;
            if (catVal != int.MinValue)
            {
                if (!hasCat)
                    e.Parameters.Add(BipHelpers.MakeBipParam(-1140362, "ELEM_CATEGORY_PARAM", StorageType.ElementId, catVal, hasNoRevitValue: catVal == -1));
                if (!hasCatMt)
                    e.Parameters.Add(BipHelpers.MakeBipParam(-1140363, "ELEM_CATEGORY_PARAM_MT", StorageType.ElementId, catVal, hasNoRevitValue: catVal == -1));
            }

            if (!hasFam && typeVal > 0)
                e.Parameters.Add(BipHelpers.MakeBipParam(-1002051, "ELEM_FAMILY_PARAM", StorageType.ElementId, typeVal));
        }
    }

    /// <summary>
    /// Apply decoded 0x09B6 material shading appearance (color/shininess/transparency/ glow) as
    /// parameters on the owning OST_Materials element.
    /// </summary>
    internal static void ApplyMaterialAppearance(RevitFileInfo info, ParsingState? state)
    {
        if (state == null) return;
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId[e.Id] = e;

        var colored = new HashSet<int>();
        foreach (var (eid, m) in state.MaterialAppearances)
        {
            if (!byId.TryGetValue((int)eid, out var e)) continue;
            var existing = new HashSet<int>();
            foreach (var p in e.Parameters) existing.Add(p.Id);

            void Add(int id, string name, StorageType st, object val)
            {
                if (existing.Contains(id)) return;
                e.Parameters.Add(BipHelpers.MakeBipParam(id, name, st, val));
            }
            // Shading values come from fixed fields. MATERIAL_NAME is emitted only when the keyed
            // display-name signature was decoded; the IFC resolver rejects Revit's explicit
            // unupgraded-appearance sentinel before using it as a display name.
            Add(-1002550, "MATERIAL_PARAM_COLOR", StorageType.Integer, m.Color);
            Add(-1002554, "MATERIAL_PARAM_SHININESS", StorageType.Integer, m.Shininess);
            // -1 = float not decodable in this block layout -> emit the Revit default (0), which is
            // correct for the vast majority; decoded non-zero values pass the integer-percent guard
            // in ExtractMaterialAppearance.
            Add(-1002551, "MATERIAL_PARAM_TRANSPARENCY", StorageType.Integer, Math.Max(0, m.Transparency));
            if (m.TransparencyFraction >= 0) e.SerializedMaterialTransparency = m.TransparencyFraction;
            // Smoothness: decoded value when the integer-percent guard passed and non-zero, else
            // the Revit default 50 ( materials in the export are 50).
            Add(-1002553, "MATERIAL_PARAM_SMOOTHNESS", StorageType.Integer, m.Smoothness > 0 ? m.Smoothness : 50);
            if (!string.IsNullOrEmpty(m.Name))
                Add(-1140355, "MATERIAL_NAME", StorageType.String, m.Name);
            Add(-1002552, "MATERIAL_PARAM_GLOW", StorageType.Integer, 0);
            colored.Add(e.Id);
            e.HasShadingAppearance = true;

            // These are materials. Fix the category - the 0x09B6 classIdx fallback
            // mislabels them as OST_CurtaSystem (-2000340).
            if (!e.CategoryId.HasValue || e.CategoryId == -2000340)
            {
                e.CategoryId = -2000700; // OST_Materials
                if (BuiltInCategoryNames.Lookup.TryGetValue(-2000700, out var cn)) e.Category = cn;
            }
        }

        // A material whose own block holds a transparency but no shading entry (the colour of those
        // comes from the native record or the holder vote below) keeps that transparency.
        foreach (var (finishId, finish) in state.MaterialFinishes)
            if (!state.MaterialAppearances.ContainsKey(finishId) && byId.TryGetValue((int)finishId, out var finished)
                && finished.CategoryId == -2000700 && !finished.Parameters.Any(parameter => parameter.Id == -1002551))
                finished.Parameters.Add(BipHelpers.MakeBipParam(-1002551, "MATERIAL_PARAM_TRANSPARENCY", StorageType.Integer, finish.Transparency));

        // Fallback for OST_Materials elements without a decoded appearance: link through the record
        // references to a shading-holder.
        if (state.ShadingEntries.Count == 0) return;
        var materials = new List<Element>();
        foreach (var e in info.Elements)
            if (e.CategoryId == -2000700 && !colored.Contains(e.Id)) materials.Add(e);
        if (materials.Count == 0) return;

        // holder -> number of materials referencing it (uniqueness test)
        var holderRefCount = new Dictionary<int, int>();
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2000700 || e.References == null) continue;
            var seen = new HashSet<int>();
            foreach (var r in e.References)
                if (seen.Add(r) && state.ShadingEntries.ContainsKey((uint)r))
                { holderRefCount.TryGetValue(r, out int n); holderRefCount[r] = n + 1; }
        }

        foreach (var e in materials)
        {
            if (e.References == null) continue;
            var holders = new List<int>();
            var seen = new HashSet<int>();
            foreach (var r in e.References)
                if (seen.Add(r) && state.ShadingEntries.ContainsKey((uint)r)) holders.Add(r);
            if (holders.Count == 0) continue;

            var unique = holders.FindAll(h => holderRefCount.GetValueOrDefault(h) == 1);
            var pool = unique.Count > 0 ? unique : holders;

            var votes = new Dictionary<(int, int), int>();
            foreach (var h in pool)
                foreach (var (cs, n) in state.ShadingEntries[(uint)h])
                { votes.TryGetValue(cs, out int v); votes[cs] = v + n; }
            if (votes.Count == 0) continue;

            (int Color, int Shininess) best = default; int bestN = -1;
            foreach (var (cs, n) in votes)
                if (n > bestN) { best = cs; bestN = n; }

            var existing = new HashSet<int>();
            foreach (var p in e.Parameters) existing.Add(p.Id);
            if (!existing.Contains(-1002550))
                e.Parameters.Add(BipHelpers.MakeBipParam(-1002550, "MATERIAL_PARAM_COLOR", StorageType.Integer, best.Color));
            if (!existing.Contains(-1002554))
                e.Parameters.Add(BipHelpers.MakeBipParam(-1002554, "MATERIAL_PARAM_SHININESS", StorageType.Integer, best.Shininess));
            e.HasShadingAppearance = true;
        }
    }

    /// <summary>The public physical Geometry property is deliberately unchanged.</summary>
    internal static void ApplySerializedSpaceBoundaries(
        IEnumerable<Element> elements,
        IReadOnlyDictionary<uint, (int BlockCount, string? Name)> roots,
        IReadOnlyDictionary<uint, (int BlockCount, GRepData? Boundary)> boundaries,
        IReadOnlyDictionary<uint, GRepData>? literalBoundaries = null)
    {
        foreach (var element in elements)
        {
            element.SerializedSpaceBoundary = null;
            element.SerializedCurvedSpaceBoundary = null;
            element.SerializedLiteralSpaceBoundary = null;
            if (element.CategoryId is not (-2000160 or -2003600) || element.Id <= 0 // OST_Rooms, OST_MEPSpaces
                || !roots.TryGetValue((uint)element.Id, out var root)
                || root.BlockCount != 1 || string.IsNullOrEmpty(root.Name)
                || !boundaries.TryGetValue((uint)element.Id, out var source)
                || source.BlockCount != 1)
                continue;
            if (literalBoundaries != null && literalBoundaries.TryGetValue((uint)element.Id, out var literal)
                && BuildGeometryData(literal) is { } literalGeometry && literalGeometry.Faces.Count == literal.Faces.Count)
                element.SerializedLiteralSpaceBoundary = literalGeometry;
            if (source.Boundary is not { } boundary) continue;
            var geometry = BuildGeometryData(boundary);
            if (geometry?.Faces.Count != boundary.Faces.Count) continue;
            if (boundary.CurvedSpaceBoundary) element.SerializedCurvedSpaceBoundary = geometry;
            else element.SerializedSpaceBoundary = geometry;
        }
    }

    internal static void ApplySerializedSpatialPhases(RevitFileInfo info, ParsingState state)
    {
        var source = state.ProjectPhaseOrderSource;
        bool wide = state.WideSchema != null;
        info.SerializedProjectPhaseIds = source.BlockCount == 1
            && source.PhaseIds is { Length: > 0 } phases
            && phases.All(id => (wide ? id >= 0 : id > 0) && state.ElemClassIdx.GetValueOrDefault((uint)id) == 3065)
            && phases.Distinct().Count() == phases.Length ? phases.ToArray() : null;
        info.SerializedDefaultExportPhaseId = info.SerializedProjectPhaseIds?[^1];
        foreach (var element in info.Elements)
        {
            element.SerializedRoomPhaseId = (element.CategoryId == -2000160 || wide && element.CategoryId == -2003600) && element.Id > 0
                && (wide || state.RoomNameSource.TryGetValue((uint)element.Id, out var root)
                    && root.BlockCount == 1 && !string.IsNullOrEmpty(root.Name))
                && state.ExactRoomPhaseIds.TryGetValue((uint)element.Id, out int phase) && (wide ? phase >= 0 : phase > 0)
                    ? phase : null;
            if (element.CategoryId == -2000160 && element.Id > 0
                && state.RoomNameSource.TryGetValue((uint)element.Id, out var validation)
                && validation.BlockCount == 1 && !string.IsNullOrEmpty(validation.Name)
                && state.ExactRoomLocationless.TryGetValue((uint)element.Id, out bool locationless))
                element.SerializedRoomIsLocationless = locationless;
        }
    }

    private static readonly bool PlanBoundaryLog = Environment.GetEnvironmentVariable("RVTIO_PLAN_LOG") == "1";

    /// <summary>
    /// RVTIO_X_PLANBODY=0 switches the bodies from the room plan off: the loop healing of <see
    /// cref="RoomPlanBoundary"/> (the crossing cut, the connector, the closing connector first) was
    /// read off the Revit output, not off the decompiled code.
    /// </summary>
    private static readonly bool PlanBodyEnabled = Environment.GetEnvironmentVariable("RVTIO_X_PLANBODY") != "0";

    private static Dictionary<int, List<TfBlockDeserializer.RoomPlanTopology>> RoomPlansByRoom(ParsingState state)
    {
        var plansByRoom = new Dictionary<int, List<TfBlockDeserializer.RoomPlanTopology>>();
        foreach (var source in state.RoomPlanSource.Values)
        {
            if (source is not { BlockCount: 1, Plan: { } plan }) continue;
            foreach (int room in plan.Rooms)
            {
                if (!plansByRoom.TryGetValue(room, out var plans)) plansByRoom[room] = plans = new();
                plans.Add(plan);
            }
        }
        return plansByRoom;
    }

    /// <summary>
    /// The plan area of each room or area (RoomElem.m_cachedCircuitId in the level room plan that
    /// lists it): only for one with a single valid owner block, a single valid plan block and
    /// exactly one plan holding it.
    /// </summary>
    internal static void ApplySerializedRoomPlanAreas(RevitFileInfo info, ParsingState state)
    {
        var plansByRoom = RoomPlansByRoom(state);
        foreach (var element in info.Elements)
        {
            element.SerializedRoomPlanAreaSquareFeet = null;
            if (element.CategoryId is not (-2000160 or -2003200) || element.Id <= 0
                || !state.RoomNameSource.TryGetValue((uint)element.Id, out var validation)
                || validation.BlockCount != 1
                || !state.ExactRoomCircuitIds.TryGetValue((uint)element.Id, out int circuit)
                || !plansByRoom.TryGetValue(element.Id, out var holding) || holding.Count != 1)
                continue;
            if (holding[0].RoomArea(circuit) is > 0 and double area) element.SerializedRoomPlanAreaSquareFeet = area;
        }
    }

    /// <summary>
    /// The outline of a room or an area with no geometry block of its own, from its circuit in the
    /// level room plan (the body the current profile extrudes for it, see <see
    /// cref="RoomPlanBoundary"/>).
    /// </summary>
    internal static void ApplySerializedPlanBoundaries(RevitFileInfo info, ParsingState state)
    {
        var plansByRoom = RoomPlansByRoom(state);
        foreach (var element in info.Elements)
        {
            element.SerializedPlanBoundary = null;
            if (!PlanBodyEnabled || element.CategoryId is not (-2000160 or -2003200) || element.Id <= 0
                || state.SpaceBoundarySource.ContainsKey((uint)element.Id)
                || !state.RoomNameSource.TryGetValue((uint)element.Id, out var validation) || validation.BlockCount != 1
                || !state.ExactRoomCircuitIds.TryGetValue((uint)element.Id, out int circuit)
                || !plansByRoom.TryGetValue(element.Id, out var holding) || holding.Count != 1)
                continue;
            element.SerializedPlanBoundary = RoomPlanBoundary.OuterLoop(holding[0], circuit, out string? failure);
            if (failure != null && PlanBoundaryLog)
                DiagnosticSink.WriteLine($"[plan] {element.Id}: {failure}");
        }
    }

    /// <summary>Other categories and unvalidated rooms are unchanged.</summary>

    internal static void ApplySerializedRoomNames(
        IEnumerable<Element> elements, IReadOnlyDictionary<uint, (int BlockCount, string? Name)> roomNames)
    {
        foreach (var element in elements)
        {
            if (element.CategoryId is not (-2000160 or -2003600) // OST_Rooms, OST_MEPSpaces
                || !roomNames.TryGetValue((uint)element.Id, out var source)
                || source.BlockCount != 1 || string.IsNullOrEmpty(source.Name))
                continue;
            element.Name = source.Name;
            // ApplyRoomParams adds ROOM_NAME when absent. Replace any earlier scan-derived copy
            // too, so the model and IFC use the same owned value.
            foreach (var parameter in element.Parameters.Where(p => p.Id == -1006900))
            {
                parameter.StorageType = StorageType.String;
                parameter.Value = source.Name;
                parameter.IsApproximate = false;
            }
        }
    }

    /// <summary>
    /// Synthesize room identity/relationship parameters from already decoded data. Exact ROOM_NAME
    /// is applied before this pass; geometric quantities remain omitted.
    /// </summary>
    internal static void ApplyRoomParams(RevitFileInfo info)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId[e.Id] = e;

        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2000160) continue; // OST_Rooms
            var existing = new HashSet<int>();
            int phase = -1;
            foreach (var p in e.Parameters)
            {
                existing.Add(p.Id);
                if (p.Id == -1012100 && p.Value is int ph) phase = ph; // PHASE_CREATED (if present)
            }
            void Add(int id, string name, StorageType st, object val)
            {
                if (!existing.Contains(id)) e.Parameters.Add(BipHelpers.MakeBipParam(id, name, st, val));
            }

            if (!string.IsNullOrEmpty(e.Name))
                Add(-1006900, "ROOM_NAME", StorageType.String, e.Name);
            if (SolidQuantities.RoomPlanArea(e.SerializedSpaceBoundary) is double floorArea && floorArea > 1e-9)
                Add(-1006902, "ROOM_AREA", StorageType.Double, floorArea);

            // Level (OST_Levels) and phase (OST_Phases) from the room's references.
            int levelId = -1;
            if (e.References != null)
                foreach (var r in e.References)
                {
                    if (!byId.TryGetValue(r, out var re)) continue;
                    if (levelId < 0 && re.CategoryId == -2000240) levelId = r;
                    if (phase < 0 && re.CategoryId == -2000112) phase = r;
                }
            if (levelId <= 0 && e.Parameters.FirstOrDefault(p => p.Id is -1001952 or -1002062 && p.Value is int id && id > 0)?.Value is int paramLevelId)
                levelId = paramLevelId;
            if (levelId > 0)
            {
                Add(-1006916, "ROOM_LEVEL_ID", StorageType.ElementId, levelId);
                Add(-1006922, "ROOM_UPPER_LEVEL", StorageType.ElementId, levelId);
            }
            // The room's own record keeps its phase even when no reference resolves it: a phase
            // of id 0 has no element row to resolve against, and a room of a schema-read file
            // carries no phase reference at all.
            int roomPhase = phase > 0 ? phase
                : e.SerializedRoomPhaseId is int serializedPhase && serializedPhase >= 0 ? serializedPhase : -1;
            if (roomPhase >= 0)
            {
                Add(-1012113, "ROOM_PHASE", StorageType.ElementId, roomPhase);
                Add(-1012112, "ROOM_PHASE_ID", StorageType.ElementId, roomPhase);
            }
            // ROOM_VOLUME / ROOM_PERIMETER / ROOM_HEIGHT are deliberately not emitted:
            // volume/perimeter/height are computed by Revit at runtime and are not persisted
            // anywhere in the file.
        }
    }

    /// <summary>
    /// View categories that carry VIEWER_SHEET_NUMBER in the Revit API (plans, sections,
    /// elevations, schedules, viewports, sheets, legends...).
    /// </summary>
    private static readonly HashSet<int> ViewCategories =
        [-2000279, -2000278, -2000500, -2000515, -2000538, -2000573, -2009609, -2003100];

    /// <summary>
    /// Sheet / placement parameters. Sheets (OST_Sheets, -2003100) with decoded 0x03E3 info get
    /// SHEET_NUMBER + SHEET_ISSUE_DATE.
    /// </summary>
    private static void ApplySheetParams(RevitFileInfo info, ParsingState? state)
    {
        if (state == null) return;
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId[e.Id] = e;

        void Add(Element e, int id, string name, object val)
        {
            foreach (var p in e.Parameters) if (p.Id == id) return;
            e.Parameters.Add(BipHelpers.MakeBipParam(id, name, StorageType.String, val));
        }

        // 1. Sheets: number + issue date; the sheet's own "placement" is its number.
        var sheetNumbers = new Dictionary<int, string>();
        foreach (var (eid, si) in state.SheetInfo)
        {
            if (!byId.TryGetValue((int)eid, out var e) || e.CategoryId != -2003100) continue;
            sheetNumbers[e.Id] = si.Number;
            Add(e, -1007401, "SHEET_NUMBER", si.Number);
            if (!string.IsNullOrEmpty(si.IssueDate))
                Add(e, -1006322, "SHEET_ISSUE_DATE", si.IssueDate);
            Add(e, -1006601, "VIEWER_SHEET_NUMBER", si.Number);
        }

        // 2. Views referenced directly BY a sheet are placed on it (e.g. ImageViews).
        var placed = new HashSet<int>();
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2003100 || e.References == null) continue;
            sheetNumbers.TryGetValue(e.Id, out var num);
            foreach (var r in e.References)
                if (r != e.Id && byId.TryGetValue(r, out var re)
                    && re.CategoryId is int rc && ViewCategories.Contains(rc) && rc != -2003100)
                {
                    placed.Add(r);
                    if (num != null) Add(re, -1006601, "VIEWER_SHEET_NUMBER", num);
                }
        }

        // 3. Viewports -> sheet number; views shown by the viewport inherit it.
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2000515 || e.References == null) continue; // OST_Viewports
            string? num = null;
            foreach (var r in e.References)
                if (sheetNumbers.TryGetValue(r, out var n)) { num = n; break; }
            foreach (var r in e.References)
                if (r != e.Id && byId.TryGetValue(r, out var re)
                    && re.CategoryId is int rc && ViewCategories.Contains(rc) && rc != -2003100)
                {
                    placed.Add(r);
                    if (num != null) Add(re, -1006601, "VIEWER_SHEET_NUMBER", num);
                }
            if (num != null) Add(e, -1006601, "VIEWER_SHEET_NUMBER", num);
            placed.Add(e.Id);
        }

        // 4.
        foreach (var e in info.Elements)
        {
            if (e.CategoryId is not int c || !ViewCategories.Contains(c)) continue;
            if (c is -2003100 or -2000515 || placed.Contains(e.Id)) continue;
            Add(e, -1006601, "VIEWER_SHEET_NUMBER", "---");
            Add(e, -1006602, "VIEWER_DETAIL_NUMBER", "-");
        }

        // 5. VIEW_NAME from the name-field signature in the view's own block; viewports report the
        // name of the view they display (first view-cat ref with a name).
        foreach (var e in info.Elements)
        {
            if (e.CategoryId is not int c || !ViewCategories.Contains(c)) continue;
            string? nm = null;
            if (state.ViewNames.TryGetValue((uint)e.Id, out var own)) nm = own;
            else if (c == -2000515 && e.References != null)
            {
                foreach (var r in e.References)
                    if (state.ViewNames.TryGetValue((uint)r, out var shown)
                        && byId.TryGetValue(r, out var re) && re.CategoryId is int rc
                        && ViewCategories.Contains(rc) && rc != -2003100) // not the sheet itself
                    { nm = shown; break; }
            }
            if (nm == null) continue;
            Add(e, -1005112, "VIEW_NAME", nm);
            if (string.IsNullOrEmpty(e.Name) || e.Name != nm) e.Name = nm;
        }
    }

    /// <summary>Computed area/volume for curtain-wall members.</summary>
    private static void AddCurtainQuantities(RevitFileInfo info)
    {
        foreach (var e in info.Elements)
        {
            if (e.CategoryId is not (-2000170 or -2000171)) continue;
            bool panel = e.CategoryId == -2000170;
            var shell = SolidQuantities.OfSingleClosedShell(e.Geometry);
            if (shell is null && GeometryBox(e) is not { Length: 6 }) continue;

            var existing = new HashSet<int>();
            foreach (var p in e.Parameters) existing.Add(p.Id);
            void Add(int id, string name, double val)
            {
                if (!existing.Contains(id) && val > 1e-9)
                    e.Parameters.Add(BipHelpers.MakeBipParam(id, name, StorageType.Double, val));
            }

            if (shell is { } exact)
            {
                Add(-1012805, "HOST_AREA_COMPUTED", panel ? exact.LargestFaceArea : exact.SurfaceArea / 2);
                Add(-1012806, "HOST_VOLUME_COMPUTED", exact.Volume);
                continue;
            }

            var bb = GeometryBox(e)!;
            double[] dims = [bb[3] - bb[0], bb[4] - bb[1], bb[5] - bb[2]];
            Array.Sort(dims);
            double w = dims[0], d = dims[1], len = dims[2];
            if (len <= 1e-6) continue;

            if (panel) // must actually be a thin slab
            {
                if (w <= 0.15 * d) // else angled/cubic bbox - formula would be garbage
                {
                    Add(-1012805, "HOST_AREA_COMPUTED", d * len);
                    Add(-1012806, "HOST_VOLUME_COMPUTED", w * d * len);
                }
            }
            else if (len >= 2 * d) // mullion: must actually be an elongated bar, else corner/angled member - bbox length is a diagonal
            {
                Add(-1012805, "HOST_AREA_COMPUTED", (w + d) * len + w * d);
                Add(-1012806, "HOST_VOLUME_COMPUTED", w * d * len);
            }
        }
    }

    // NOTE (known issue): for ROTATED walls the record-body bbox Y pair is not a world bbox (it
    // spans only the wall thickness) while the element's GRep box is correct
    // (Geometry.BoundingBox).

    /// <summary>
    /// Detail/model lines: 0x0346 curve blocks whose GStyleElemId is a real line style (not -1).
    /// </summary>
    private static void ApplyDetailLines(RevitFileInfo info, ParsingState? state)
    {
        if (state == null || (state.DetailCurveStyles.Count == 0 && state.CurveBlockOwners.Count == 0)) return;
        foreach (var e in info.Elements)
        {
            if (e.CategoryId.HasValue) continue; // categorized owners (sketch profiles etc.) keep their category
            bool hasStyle = state.DetailCurveStyles.TryGetValue((uint)e.Id, out int style);
            // A decoded line style (not -1) marks a detail/model line directly.
            bool hasGeom = state.CurveGeometry.TryGetValue((uint)e.Id, out var cgeom);
            bool isCurveOwner = (state.CurveBlockOwners.Contains((uint)e.Id) || hasGeom)
                                && !state.SketchCurves.ContainsKey((uint)e.Id);
            if (!hasStyle && !isCurveOwner) continue;

            // The CurveElem schema STORES the category (m_categoryId) - authoritative when the
            // deserializer captured it; the -2000051 guess is the fallback.
            int curveCat = -2000051;
            if (state.NativeParams.TryGetValue((uint)e.Id, out var cnp)
                && cnp.Fields.TryGetValue("m_categoryId", out int storedCat)
                && storedCat < -2000000)
                curveCat = storedCat;

            e.CategoryId = curveCat;
            e.Category = BuiltInCategoryNames.Lookup.TryGetValue(curveCat, out var ccn) ? ccn : "OST_Lines";
            var existing = new HashSet<int>(e.Parameters.Select(p => p.Id));
            void Add(int id, string name, StorageType st, object val)
            {
                if (!existing.Contains(id)) e.Parameters.Add(BipHelpers.MakeBipParam(id, name, st, val));
            }
            Add(-1140362, "ELEM_CATEGORY_PARAM", StorageType.ElementId, curveCat);
            Add(-1140363, "ELEM_CATEGORY_PARAM_MT", StorageType.ElementId, curveCat);
            // Line style: the full schema walk (covers arcs) is authoritative; fall back to the
            // GInfoBlob pattern-scan style for lines it didn't reach.
            if (hasGeom && cgeom.GStyle > 0)
                Add(-1006210, "BUILDING_CURVE_GSTYLE", StorageType.ElementId, cgeom.GStyle);
            else if (hasStyle) Add(-1006210, "BUILDING_CURVE_GSTYLE", StorageType.ElementId, style);
            // NOTE: m_detail is NOT the is-detail flag - keep the constant.
            state.NativeParams.TryGetValue((uint)e.Id, out var cnp2);
            Add(-1004011, "CURVE_IS_DETAIL", StorageType.Integer, 1);
            if (cnp2 != null && cnp2.Fields.TryGetValue("m_showCenterMark", out int cm))
                Add(-1007900, "ARC_CURVE_CNTR_MRK_VISIBLE", StorageType.Integer, cm);
            if (cnp2 != null && cnp2.Fields.TryGetValue("m_sketchPlaneId", out int sp) && sp > 0)
                Add(-1001380, "SKETCH_PLANE_PARAM", StorageType.ElementId, sp);
            // Length: schema walk covers arcs; Location is line-only fallback.
            if (hasGeom && cgeom.Length > 1e-9)
                Add(-1004005, "CURVE_ELEM_LENGTH", StorageType.Double, cgeom.Length);
            else if (e.Location is Model.LocationCurve lc && lc.Length > 1e-9)
                Add(-1004005, "CURVE_ELEM_LENGTH", StorageType.Double, lc.Length);
            // These elements no longer take the no-category default branch - keep the unhosted
            // defaults the export reports for them.
            Add(-1002108, "HOST_ID_PARAM", StorageType.ElementId, -1);
            Add(-1001370, "INSTANCE_OFFSET_POS_PARAM", StorageType.Integer, 0);
        }
    }

    /// <summary>
    /// Categorize otherwise-uncategorized elements from a distinctive block they own (Revit
    /// approach: a block's class identifies the element's category). Currently: the 0x0884
    /// analytical Hub-node block -> OST_AnalyticalNodes.
    /// </summary>
    private static void ApplyBlockSignatureCategories(RevitFileInfo info, ParsingState? state)
    {
        if (state == null || state.AnalyticalNodeOwners.Count == 0) return;
        foreach (var e in info.Elements)
        {
            if (e.CategoryId.HasValue) continue;
            if (!state.AnalyticalNodeOwners.Contains((uint)e.Id)) continue;
            e.CategoryId = -2009645;
            e.Category = "OST_AnalyticalNodes";
            var existing = new HashSet<int>(e.Parameters.Select(p => p.Id));
            if (!existing.Contains(-1140362))
                e.Parameters.Add(BipHelpers.MakeBipParam(-1140362, "ELEM_CATEGORY_PARAM", StorageType.ElementId, -2009645));
            if (!existing.Contains(-1140363))
                e.Parameters.Add(BipHelpers.MakeBipParam(-1140363, "ELEM_CATEGORY_PARAM_MT", StorageType.ElementId, -2009645));
        }
    }

    /// <summary>Workset id per element, decoded from ElemTable offset-20 (workshared files).</summary>
    private static void ApplyWorksets(RevitFileInfo info, ParsingState? state)
    {
        if (state == null || state.ElemWorksets.Count == 0) return;
        foreach (var e in info.Elements)
        {
            var existing = e.Parameters.FirstOrDefault(p => p.Id == -1002053);
            if (state.ElemWorksets.TryGetValue((uint)e.Id, out uint ws))
            {
                // ElemTable workset is authoritative - force-override any stale value.
                if (existing != null) { existing.Value = (int)ws; existing.StorageType = StorageType.Integer; }
                else e.Parameters.Add(BipHelpers.MakeBipParam(-1002053, "ELEM_PARTITION_PARAM", StorageType.Integer, (int)ws));
            }
            else if (existing is { Value: int pv } && pv < 0)
            {
                e.Parameters.Remove(existing);
            }
        }
    }

    /// <summary>
    /// The model group an element is a member of, from its GroupMembership cell (422:m_groupId).
    /// The cell of a group instance (root class 1322) holds its group type instead.
    /// </summary>
    internal static int? StoredGroupMembership(ushort rootClass, TfBlockDeserializer.NativeParams np)
        => rootClass != 1322 && np.Fields.TryGetValue("422:m_groupId", out int groupId) && groupId > 0 ? groupId : null;

    /// <summary>
    /// Apply the native parameter stores (schema-deserialized Element m_pParamValueSet* containers)
    /// - the file's authoritative stored params.
    /// </summary>
    private static void ApplyNativeParams(RevitFileInfo info, ParsingState? state)
    {
        if (state == null
            || (state.NativeParams.Count == 0
                && state.NewCurtainWallTypeFunctions.Count == 0
                && state.FamilySymbolFunctions.Count == 0))
            return;
        ApplySerializedCeilingTypeLinks(
            info.Elements, state.NativeParams, state.ElemClassIdx);
        var groupOfMember = new Dictionary<int, int>();
        foreach (var (group, members) in state.GroupMembers.OrderBy(pair => pair.Key))
            foreach (int member in members) groupOfMember.TryAdd(member, (int)group);
        var templateIds = new HashSet<int>();
        foreach (var native in state.NativeParams.Values)
            if (native.Fields.TryGetValue("1333:m_definingGroupInstId", out int defining) && defining > 0)
            {
                templateIds.Add(defining);
                if (state.GroupMembers.TryGetValue((uint)defining, out var templateMembers))
                    foreach (int member in templateMembers) templateIds.Add(member);
            }
        foreach (var e in info.Elements)
        {
            e.SerializedCurtainWallFunction = null;
            if (e.Id > 0 && state.NewCurtainWallTypeFunctions.TryGetValue(
                    (uint)e.Id, out var curtainWallFunction)
                && curtainWallFunction is { BlockCount: 1, Function: >= 0 and <= 5 })
                e.SerializedCurtainWallFunction = curtainWallFunction.Function;
            e.SerializedFamilySymbolFunction = e.Id > 0
                && state.FamilySymbolFunctions.TryGetValue((uint)e.Id, out var symbolFunction)
                && symbolFunction.BlockCount == 1 ? symbolFunction.Function : null;
            e.NativeStoredParameterIds = null;
            e.SerializedWallFlipped = null;
            e.SerializedStairsTypeMinInsideTreadFeet = null;
            e.SerializedStairRunTypeNosingFeet = null;
            e.IsGroupTemplate = templateIds.Contains(e.Id);
            e.SerializedRoomId = null;
            e.SerializedAreaScheme = null;
            e.SerializedRoomIsLocationless = null;
            e.NativePrimaryDesignOptionId = null;
            e.NativeDesignOptionSetId = null;
            e.NativeDesignOptionName = null;
            e.SerializedGroupId = groupOfMember.TryGetValue(e.Id, out int owningGroup) ? owningGroup : null;
            e.NativeRecordClass = state.ElemClassIdx.TryGetValue((uint)e.Id, out ushort recordClass) ? recordClass : null;
            if (!state.NativeParams.TryGetValue((uint)e.Id, out var np)) continue;
            if (np.Fields.TryGetValue("3471:m_bIsLocationless", out int locationless))
                e.SerializedRoomIsLocationless = locationless != 0;
            if (np.StringFields.TryGetValue("388:m_areaElemName", out string? areaSchemeName)
                && np.StringFields.TryGetValue("388:m_areaElemDescription", out string? areaSchemeDescription))
                e.SerializedAreaScheme = (areaSchemeName, areaSchemeDescription);
            // GroupMembership.m_groupId of a group instance (root class 1322) is its group TYPE
            // (the Type parameter, see NativeTypePointers), not a group it is a member of: the type
            // would be exported as a group holding the instances.
            if (e.SerializedGroupId == null && StoredGroupMembership(state.ElemClassIdx.GetValueOrDefault((uint)e.Id), np) is int groupId)
                e.SerializedGroupId = groupId;
            if (np.Fields.TryGetValue("1080:m_mainDesignOption", out int primaryOption)) e.NativePrimaryDesignOptionId = primaryOption;
            if (np.Fields.TryGetValue("1074:m_DesignOptionSetId", out int optionSet)) e.NativeDesignOptionSetId = optionSet;
            if (np.StringFields.TryGetValue("1080:m_strName", out string? setName) || np.StringFields.TryGetValue("1074:m_strName", out setName))
                e.NativeDesignOptionName = setName;
            if (np.Fields.TryGetValue("1286:m_idRoom", out int roomId))
                e.SerializedRoomId = roomId;
            if (np.DoubleFields.TryGetValue("3742:m_minTreadWidthOnInsideBoundary", out double insideTread) && double.IsFinite(insideTread))
                e.SerializedStairsTypeMinInsideTreadFeet = insideTread;
            if (np.DoubleFields.TryGetValue("3718:m_nosingLength", out double nosing) && double.IsFinite(nosing))
                e.SerializedStairRunTypeNosingFeet = nosing;
            if (e.CategoryId == -2000011 && np.Fields.TryGetValue("362:m_isFlipped", out int wallFlipped))
                e.SerializedWallFlipped = wallFlipped != 0;
            var byId = new Dictionary<int, Parameter>();
            foreach (var p in e.Parameters) byId.TryAdd(p.Id, p);
            var storedIds = new HashSet<int>();
            foreach (var (pid, _) in np.Doubles) storedIds.Add(pid);
            foreach (var (pid, _) in np.Ints) storedIds.Add(pid);
            foreach (var (pid, _) in np.Strings) storedIds.Add(pid);
            foreach (var (pid, _) in np.ElementIds) storedIds.Add(pid);
            foreach (var named in np.NamedParams)
                if (named.ParamId < 0) storedIds.Add(named.ParamId);
            if (storedIds.Count > 0) e.NativeStoredParameterIds = storedIds;
            var storeIds = new HashSet<int>();
            foreach (var (pid, _) in np.Doubles) storeIds.Add(pid);
            foreach (var (pid, _) in np.Ints) storeIds.Add(pid);
            foreach (var (pid, _) in np.Strings) storeIds.Add(pid);
            foreach (var (pid, _) in np.ElementIds) storeIds.Add(pid);
            e.NativeParameterStoreIds = storeIds;
            e.NativeParameterStoresComplete = np.RootParameterStoresComplete;
            e.NativeCellListTag = np.Fields.TryGetValue("37:m_cellList", out int cellListTag) ? cellListTag : null;
            e.NativeCellClasses = np.CellClasses?.ToArray();
            e.NativeTailWalkComplete = np.TailWalkComplete;
            if (np.NamedParams.Count > 0)
                e.NativeNamedParameters = np.NamedParams.Where(named => named.ParamId < 0).Select(named => (named.ParamId, named.Instance)).ToArray();
            // the two element-base keys are only kept for family instances (they exist in every
            // record)
            bool familyInstanceRecord = np.Fields.ContainsKey("1794:m_masterSymbolId");
            foreach (string key in TfBlockDeserializer.ListFactFieldKeys)
                if (np.Fields.TryGetValue(key, out int listFieldValue) && (familyInstanceRecord || !key.StartsWith("37:", StringComparison.Ordinal)))
                    (e.NativeListFields ??= new())[key] = listFieldValue;
            foreach (string key in TfBlockDeserializer.RoomRuleFieldKeys)
            {
                if (np.Fields.TryGetValue(key, out int roomRuleInt)) (e.NativeRoomFields ??= new())[key] = roomRuleInt;
                else if (np.DoubleFields.TryGetValue(key, out double roomRuleReal) && double.IsFinite(roomRuleReal))
                    (e.NativeRoomFields ??= new())[key] = roomRuleReal;
            }
            if (TfBlockDeserializer.CaptureAllFields)
            {
                e.DebugNativeFields = new Dictionary<string, object>();
                if (state.ElemClassIdx.TryGetValue((uint)e.Id, out var debugClass)) e.DebugNativeFields["__class"] = (int)debugClass;
                foreach (var (key, value) in np.Fields) e.DebugNativeFields[key] = value;
                foreach (var (key, value) in np.DoubleFields) e.DebugNativeFields[key] = value;
                foreach (var (key, value) in np.StringFields) e.DebugNativeFields[key] = value;
                for (int named = 0; named < np.NamedParams.Count; named++)
                {
                    var record = np.NamedParams[named];
                    e.DebugNativeFields["named:" + named] = $"id={record.ParamId} str={record.Str} v={record.Value:R} elem={record.ElemId} int={record.Int} inst={record.Instance} rep={record.Reporting}";
                }
                foreach (var (pid, value) in np.Doubles) e.DebugNativeFields["store.D:" + pid] = value;
                foreach (var (pid, value) in np.Ints) e.DebugNativeFields["store.I:" + pid] = value;
                foreach (var (pid, value) in np.ElementIds) e.DebugNativeFields["store.E:" + pid] = value;
                foreach (var (pid, value) in np.Strings) e.DebugNativeFields["store.S:" + pid] = value;
            }
            void Apply(int pid, StorageType st, object val, bool force = false, bool fromTable = false, bool noRevitValue = false)
            {
                // An explicit rule that sets the parameter takes it over from the field table's
                // domain bookkeeping.
                if (!fromTable)
                {
                    e.NativeTableCreatedParams?.Remove(pid);
                    e.NativeTablePrevious?.Remove(pid);
                }
                if (byId.TryGetValue(pid, out var existing))
                {
                    bool hollow = existing.HasNoRevitValue
                        || existing.Value is null
                        || (existing.Value is string es && es.Length == 0)
                        || (existing.Value is int ei && ei == -1 && st != StorageType.ElementId);
                    // Native stored ids are authoritative - force over Revit-voting type
                    // heuristic.
                    if (hollow || force)
                    {
                        existing.Value = val;
                        existing.StorageType = st;
                        existing.FromNativeFields = true;
                        existing.HasNoRevitValue = noRevitValue;
                        e.BoundingBoxDimensionParamIds?.Remove(pid);
                    }
                    return;
                }
                BipNames.Lookup.TryGetValue(pid, out string? name);
                var par = BipHelpers.MakeBipParam(pid, name ?? pid.ToString(), st, val, hasNoRevitValue: noRevitValue);
                par.FromNativeFields = true;
                e.Parameters.Add(par);
                byId[pid] = par;
            }
            // Type/symbol params from stored ids override the heuristic.
            void ApplyType(int typeId)
            {
                Apply(-1002050, StorageType.ElementId, typeId, force: true, noRevitValue: typeId <= 0); // ELEM_TYPE_PARAM
                Apply(-1002000, StorageType.ElementId, typeId, force: true, noRevitValue: typeId <= 0); // SYMBOL_ID_PARAM
                Apply(-1002052, StorageType.ElementId, typeId, force: true, noRevitValue: typeId <= 0); // ELEM_FAMILY_AND_TYPE_PARAM
                // Canonicalize the element's TypeId to the stored id too.
                if (typeId > 0) e.TypeId = typeId;
            }
            // ELEM_PARTITION_PARAM (-1002053) is owned by the ElemTable workset decode
            // (authoritative); the element block's own copy is a stale -1 for packed elements.
            foreach (var (pid, v) in np.Doubles)
                if (pid != -1002053) Apply(pid, StorageType.Double, v, noRevitValue: !double.IsFinite(v) || Math.Abs(v) >= 1e29);
            foreach (var (pid, v) in np.Ints) if (pid != -1002053) Apply(pid, StorageType.Integer, v);
            foreach (var (pid, v) in np.Strings) Apply(pid, StorageType.String, v);
            foreach (var (pid, v) in np.ElementIds)
                if (pid != -1002053)
                    Apply(pid, StorageType.ElementId, v, noRevitValue: !Parameter.IsSetElementId(v));

            if (state.ElemClassIdx.TryGetValue((uint)e.Id, out ushort typedRootClass)
                && NativeTypePointers.Of(typedRootClass, np) is int pointedType)
            {
                ApplyType(pointedType);
                Apply(-1002051, StorageType.ElementId, pointedType, force: true, noRevitValue: pointedType <= 0); // ELEM_FAMILY_PARAM
                e.TypeFromNativePointer = true;
            }
            // Named schema fields -> the BIPs Revit computes from them.
            if (np.Fields.TryGetValue("m_masterSymbolId", out int sym) && sym > 0)
            {
                ApplyType(sym);
                // The export reports ELEM_FAMILY_PARAM as the symbol id for family instances too
                // (m_masterSymbolId vs m_famId ).
                Apply(-1002051, StorageType.ElementId, sym, force: true);
                // Record the raw field's presence (not just its resolved value) for callers that
                // need to distinguish "this occurrence IS a component FamilyInstance" from "this
                // occurrence's TypeId happens to be set some other way".
                e.NativeMasterSymbolId = sym;
            }
            else if (np.Fields.TryGetValue("m_famId", out int fam) && fam > 0)
                Apply(-1002051, StorageType.ElementId, fam); // ELEM_FAMILY_PARAM
            if (np.Fields.TryGetValue("m_assocLevelId", out int lvl) && lvl > 0)
                Apply(-1001952, StorageType.ElementId, lvl); // LEVEL_PARAM
            // SCHEDULE_LEVEL_PARAM ("Level"): the association level if set, else the schedule-only
            // level, else -1.
            bool hasAssocLevel = np.Fields.TryGetValue("m_assocLevelId", out int assocLevel);
            bool hasScheduleLevel = np.Fields.TryGetValue("m_scheduleOnlyLevelId", out int scheduleLevel);
            if (hasAssocLevel || hasScheduleLevel)
            {
                int resolvedScheduleLevel = assocLevel > 0 ? assocLevel : hasScheduleLevel && scheduleLevel > 0 ? scheduleLevel : -1;
                Apply(-1002062, StorageType.ElementId, resolvedScheduleLevel, force: true, noRevitValue: resolvedScheduleLevel <= 0);
                e.ScheduleLevelFromStoredFields = true;
            }
            if (np.Fields.TryGetValue("m_hostId", out int serializedHostId))
            {
                e.SerializedHostId = serializedHostId;
                e.HostId = serializedHostId > 0 ? serializedHostId : null;
                Apply(-1002108, StorageType.ElementId, serializedHostId, force: true, noRevitValue: serializedHostId <= 0); // HOST_ID_PARAM
            }
            e.ExtraHostIds = CapturedElementIds("m_extraHostIds");
            e.ExplicitHostIds = CapturedElementIds("m_explicitHostIds");
            if (np.Fields.TryGetValue("m_superInstanceId", out int superInstanceId)
                && superInstanceId > 0)
                e.SuperInstanceId = superInstanceId;
            if (np.Fields.TryGetValue("m_createdPhaseId", out int storedCreatedPhase))
                e.SerializedCreatedPhaseId = storedCreatedPhase;
            if (np.Fields.TryGetValue("m_demolishedPhaseId", out int storedDemolishedPhase))
                e.SerializedDemolishedPhaseId = storedDemolishedPhase;
            if (np.Fields.TryGetValue("m_createdPhaseId", out int phc) && phc > 0)
                Apply(-1012100, StorageType.ElementId, phc); // PHASE_CREATED
            // PHASE_DEMOLISHED: the null id (-1) of a file with 64-bit ids is the stored value
            // ("None"); the Revit 2022 pass below adds the same default for the other files
            if (np.Fields.TryGetValue("m_demolishedPhaseId", out int phd) && (phd > 0 || phd == -1 && e.NativeRootClassName != null))
                Apply(-1012101, StorageType.ElementId, phd, noRevitValue: phd <= 0);
            if (np.Fields.TryGetValue("m_WallAttributesId", out int wat) && wat > 0)
                ApplyType(wat);
            if (np.Fields.TryGetValue("m_upToLevelId", out int upl))
                Apply(-1001103, StorageType.ElementId, upl, noRevitValue: upl <= 0); // WALL_HEIGHT_TYPE (-1 = unconnected)
            if (np.Fields.TryGetValue("m_wallStructuralUsage", out int wsu))
                Apply(-1001119, StorageType.Integer, wsu); // WALL_STRUCTURAL_USAGE_PARAM
            if (np.Fields.TryGetValue("m_wallKeyRef", out int wkr))
                Apply(-1001122, StorageType.Integer, wkr); // WALL_KEY_REF_PARAM
            if (np.Fields.TryGetValue("m_wallCrossSection", out int wcs))
                Apply(-1019100, StorageType.Integer, wcs); // WALL_CROSS_SECTION
            // Floor / Roof / Level(Datum) type-attribute ids -> type params.
            foreach (var attrName in new[] { "m_floorAttributesId", "m_roofAttributesId", "m_attrId" })
            {
                if (np.Fields.TryGetValue(attrName, out int at) && at > 0)
                {
                    ApplyType(at);
                    break;
                }
            }
            // FamilyParams.m_params: built-in parameters stored by the element itself as NamedParam
            // (paramId + one value slot). The slot is chosen by the parameter's public API storage
            // type.
            bool isSymbolRecord = state.ElemClassIdx.TryGetValue((uint)e.Id, out ushort namedElemCls) && namedElemCls == 1829;
            bool symbolAnalyticNone = isSymbolRecord
                && np.NamedParams.FirstOrDefault(r => r.ParamId == -1005437)?.Str is string acStr
                && acStr == "<None>";
            foreach (var named in np.NamedParams)
            {
                if (named.ParamId >= 0 || !NativeNamedParameters.VerifiedIds.Contains(named.ParamId)
                    || !ApiParameterDefinitions.TryGetStorage(named.ParamId, out var namedStorage)) continue;
                bool namedUnset = (isSymbolRecord && named.Instance)
                    || (symbolAnalyticNone && named.ParamId is -1005430 or -1005432 or -1005433)
                    || namedStorage switch
                    {
                        StorageType.Double => !double.IsFinite(named.Value) || Math.Abs(named.Value) >= 1e29,
                        StorageType.ElementId => !Parameter.IsSetElementId(named.ElemId),
                        StorageType.String => named.Str == null,
                        _ => false,
                    };
                switch (namedStorage)
                {
                    case StorageType.Double: Apply(named.ParamId, StorageType.Double, named.Value, force: true, noRevitValue: namedUnset); break;
                    case StorageType.Integer: Apply(named.ParamId, StorageType.Integer, named.Int, force: true, noRevitValue: namedUnset); break;
                    case StorageType.ElementId: Apply(named.ParamId, StorageType.ElementId, named.ElemId, force: true, noRevitValue: namedUnset); break;
                    case StorageType.String: Apply(named.ParamId, StorageType.String, named.Str ?? "", force: true, noRevitValue: namedUnset); break;
                }
            }
            foreach (var named in np.NamedParams)
            {
                if (named.ParamId <= 0) continue;
                var existing = e.Parameters.FirstOrDefault(p => p.Id == named.ParamId);
                var namedStorage = FamilyParamStorageFromSpec(state, named.ParamId);
                if (namedStorage == StorageType.None && existing is { StorageType: not StorageType.None })
                    namedStorage = existing.StorageType;
                if (namedStorage == StorageType.None) continue;
                bool namedUnset = (isSymbolRecord && named.Instance)
                    || namedStorage switch
                    {
                        StorageType.Double => !double.IsFinite(named.Value) || Math.Abs(named.Value) >= 1e29,
                        StorageType.ElementId => named.ElemId <= 0,
                        StorageType.String => named.Str == null,
                        _ => false,
                    };
                object namedValue = namedStorage switch
                {
                    StorageType.Double => named.Value,
                    StorageType.Integer => named.Int,
                    StorageType.ElementId => named.ElemId,
                    _ => named.Str ?? "",
                };
                if (existing != null)
                {
                    existing.StorageType = namedStorage;
                    existing.Value = namedValue;
                    existing.HasNoRevitValue = namedUnset;
                    continue;
                }
                string? namedForge = null, namedName = null;
                if (state.ForgeParamDefs.TryGetValue((uint)named.ParamId, out var namedDef))
                    (namedForge, namedName) = namedDef;
                e.Parameters.Add(new Parameter
                {
                    Id = named.ParamId,
                    StorageType = namedStorage,
                    Value = namedValue,
                    HasNoRevitValue = namedUnset,
                    IsReadOnly = false,
                    ForgeId = namedForge,
                    Guid = state.ParamDefGuids.TryGetValue((uint)named.ParamId, out var namedGuid)
                        ? namedGuid : BipHelpers.ParseGuidFromForgeId(namedForge),
                    Definition = new Definition { Id = named.ParamId, Name = namedName },
                });
                byId[named.ParamId] = e.Parameters[^1];
            }
            // Parameters stored verbatim in a schema field.
            foreach (var entry in NativeFieldParameters.All)
            {
                object? tableValue = null;
                bool tableSet = false;
                switch (entry.Storage)
                {
                    case StorageType.Double when np.DoubleFields.TryGetValue(entry.Key, out double fieldDouble):
                        tableValue = fieldDouble; tableSet = double.IsFinite(fieldDouble) && Math.Abs(fieldDouble) < 1e29; break;
                    case StorageType.Integer or StorageType.ElementId when np.Fields.TryGetValue(entry.Key, out int fieldInt):
                        tableValue = fieldInt; tableSet = entry.Storage != StorageType.ElementId || Parameter.IsSetElementId(fieldInt); break;
                    case StorageType.String when np.StringFields.TryGetValue(entry.Key, out string? fieldString):
                        tableValue = fieldString; tableSet = true; break;
                }
                if (tableValue == null) continue;
                if (NativeFieldParameters.NotWithCompleteStores.Contains((entry.ParamId, entry.Key)) && np.RootParameterStoresComplete) continue;
                // Presence domains: where the field is set, those of the row; where it is "not set" (ElementId -1, double >= 1e29)
                // the row's UnsetDomains (null: an older row, whose "not set" value has always been created, is not checked).
                string[]? checkedDomains = tableSet ? (entry.Domains.Length > 0 ? entry.Domains : null) : entry.UnsetDomains;
                if (checkedDomains != null)
                {
                    (e.NativeTableAddedParams ??= new()).Add((entry.ParamId, checkedDomains, entry.Storage, tableValue, !tableSet));
                    if (!byId.TryGetValue(entry.ParamId, out var before)) (e.NativeTableCreatedParams ??= new()).Add(entry.ParamId);
                    else (e.NativeTablePrevious ??= new()).TryAdd(entry.ParamId, (before.Value, before.StorageType, before.HasNoRevitValue));
                }

                // An unset phase (-1) must not create PHASE_CREATED: FixUnresolvedPhaseCreated
                // would turn the sentinel into the dominant phase, which the IFC exporter reads as
                // a project phase reference.
                if (!tableSet && entry.ParamId == -1012100 && !byId.ContainsKey(entry.ParamId)) continue;
                Apply(entry.ParamId, entry.Storage, tableValue, force: tableSet, fromTable: true, noRevitValue: !tableSet);
            }
            if (state.ElemClassIdx.TryGetValue((uint)e.Id, out var mepCurveClass) && mepCurveClass is 3148 or 3210 or 3134 or 598)
                ApplyMepCurveGeometry(np, mepCurveClass, (pid, st, val, force) => Apply(pid, st, val, force));
            else ApplyFlexCurveGeometry(np, mepCurveClass, (pid, st, val, force) => Apply(pid, st, val, force));
            if (np.DoubleFields.TryGetValue("1709:m_rotation", out double curveDriverRotation) && double.IsFinite(curveDriverRotation))
                e.CurveDriverRotation = curveDriverRotation;
            if (np.Fields.TryGetValue("1708:m_refLevelId", out int curveDriverLevel))
                e.CurveDriverRefLevelId = curveDriverLevel;
            if (np.Fields.TryGetValue("1794:m_assocSketchPlaneId", out int sketchPlane))
                e.AssocSketchPlaneId = sketchPlane;
            if (state.ElemClassIdx.GetValueOrDefault((uint)e.Id) == 3607) // SketchPlane: on a datum plane, or on the plane reference Revit makes for a level or reference plane
            {
                if (np.Fields.TryGetValue("2604:m_datumPlaneId", out int datumPlane)) e.SketchPlaneDatumId = datumPlane;
                else if (np.Fields.TryGetValue("140:m_elemId", out int referencedDatum) && referencedDatum > 0) e.SketchPlaneDatumId = referencedDatum;
            }
            if (np.Fields.TryGetValue("1801#0:m_elemId", out int baseLevelRef)
                && np.DoubleFields.TryGetValue("1801#0:m_offset", out double baseLevelOffset)
                && np.Fields.TryGetValue("1801#1:m_elemId", out int topLevelRef)
                && np.DoubleFields.TryGetValue("1801#1:m_offset", out double topLevelOffset)
                && !np.Fields.ContainsKey("1801#2:m_elemId"))
                e.FamilyLevelRefs = (baseLevelRef, baseLevelOffset, topLevelRef, topLevelOffset);
            // Curtain grid SpacingRuleInst pair: first horizontal, then vertical.
            if (np.Fields.ContainsKey("369#0:m_fixedNum") && np.Fields.ContainsKey("369#1:m_fixedNum")
                && !np.Fields.ContainsKey("369#2:m_fixedNum"))
            {
                foreach (var (occurrence, number, justification, angle, origin) in new[]
                {
                    (0, -1013307, -1013305, -1013310, -1013313), // horizontal grid
                    (1, -1013306, -1013304, -1013309, -1013312), // vertical grid
                })
                {
                    string prefix = "369#" + occurrence + ":";
                    if (np.Fields.TryGetValue(prefix + "m_fixedNum", out int fixedNumber))
                        Apply(number, StorageType.Integer, fixedNumber, force: true);
                    if (np.Fields.TryGetValue(prefix + "m_justify", out int justify))
                        Apply(justification, StorageType.Integer, justify, force: true);
                    if (np.DoubleFields.TryGetValue(prefix + "m_angle", out double gridAngle))
                        Apply(angle, StorageType.Double, gridAngle, force: true);
                    if (np.DoubleFields.TryGetValue(prefix + "m_origin", out double gridOrigin))
                        Apply(origin, StorageType.Double, gridOrigin, force: true);
                }
            }
            foreach (string userData in new[] { "3789", "3775" })
                if (np.Fields.TryGetValue(userData + ":m_connectionType@0", out int firstConnection)
                    && np.Fields.TryGetValue(userData + ":m_connectionType@1", out int secondConnection))
                    e.StructuralConnectionTypes = (firstConnection, secondConnection);
            if (np.Fields.TryGetValue("1419:m_buildingTypeId", out int buildingType) && buildingType > 0)
                e.EnergyBuildingTypeId = buildingType;
            if (np.Fields.TryGetValue("2237:m_symbolId", out int instanceSymbol) && instanceSymbol > 0)
                e.InstanceSymbolId = instanceSymbol;
            if (np.DoubleFields.TryGetValue("3795#0:m_Length", out double startLength)
                && np.DoubleFields.TryGetValue("3795#1:m_Length", out double endLength)
                && !np.DoubleFields.ContainsKey("3795#2:m_Length")
                && startLength == endLength && double.IsFinite(startLength))
                e.SymbolFramingLength = startLength;
            if (np.Fields.TryGetValue("1794:m_useOffsetPos", out int usesOffsetPosition))
                e.InstanceUsesOffsetPosition = usesOffsetPosition != 0;
            if (np.Fields.TryGetValue("1794:m_bVertical", out int vertical))
                e.InstanceIsVertical = vertical != 0;
            if (np.DoubleFields.TryGetValue("1794:m_instOrigin@2", out double originZ) && double.IsFinite(originZ))
                e.InstanceOriginZ = originZ;
            if (np.DoubleFields.TryGetValue("1794:m_instOrigin@0", out double originX) && double.IsFinite(originX)
                && np.DoubleFields.TryGetValue("1794:m_instOrigin@1", out double originY) && double.IsFinite(originY))
                e.InstanceOriginXY = (originX, originY);
            e.IsCurtainMemberInstance = np.Fields.ContainsKey("897:m_hostId") || np.Fields.ContainsKey("2518:m_hostId");
            if (np.Fields.TryGetValue("1829:m_familyId", out int symbolFamily))
                e.SymbolFamilyId = symbolFamily;
            if (np.Fields.TryGetValue("1773:m_bIsHostBased", out int hostBased))
                e.FamilyIsHostBased = hostBased != 0;
            if (np.Fields.TryGetValue("1772:m_refTypeIds@0", out int firstHostType)
                && np.Fields.TryGetValue("1773:m_categoryId", out int familyCategory)
                && np.Fields.TryGetValue("1773:m_isCurtainPanel", out int curtainPanel)
                && np.Fields.TryGetValue("1773:m_isCrvDriven", out int curveDriven))
                e.FamilyAllowsRawHost = firstHostType != 0 || curtainPanel != 0 || familyCategory == -2000171
                    || curveDriven != 0 && familyCategory == -2001320;
            if (TryCurveDriverLineZ(np, out var lineZ))
                e.CurveDriverLineZ = lineZ;
            if (np.DoubleFields.TryGetValue("87:m_pCrv.dirVec@0", out double lineX)
                && np.DoubleFields.TryGetValue("87:m_pCrv.dirVec@1", out double lineY)
                && np.DoubleFields.TryGetValue("87:m_pCrv.dirVec@2", out double lineDirZ))
            {
                double lineLength = Math.Sqrt(lineX * lineX + lineY * lineY + lineDirZ * lineDirZ);
                if (double.IsFinite(lineLength) && lineLength > 0)
                {
                    e.CurveDriverLineDirection = (lineX / lineLength, lineY / lineLength, lineDirZ / lineLength);
                    if (np.DoubleFields.TryGetValue("87:m_pCrv.endParams@0", out double lineStart)
                        && np.DoubleFields.TryGetValue("87:m_pCrv.endParams@1", out double lineEnd)
                        && double.IsFinite(lineStart) && double.IsFinite(lineEnd) && lineEnd != lineStart)
                        e.CurveDriverLineLength = Math.Abs(lineEnd - lineStart) * lineLength;
                }
            }
            if (np.DoubleFields.TryGetValue("3789:m_copingDistance", out double copingDistance) && double.IsFinite(copingDistance))
                e.FramingCopingDistance = copingDistance;
            if (np.Fields.TryGetValue("699:m_baseOrTop", out int baseOrTop) && baseOrTop is 0 or 1
                && np.DoubleFields.TryGetValue("699:m_attachOffset", out double attachOffset) && double.IsFinite(attachOffset)
                && np.Fields.TryGetValue("699:m_justification", out int attachJustification))
                e.ColumnAttachment = (baseOrTop == 1, attachOffset, attachJustification);
            if (np.Fields.TryGetValue("m_analyzesAs", out int aas))
                Apply(-1001576, StorageType.Integer, aas); // STRUCTURAL_ANALYZES_AS
            if (np.Fields.TryGetValue("m_isBuildingStory", out int ibs))
                Apply(-1007111, StorageType.Integer, ibs); // LEVEL_IS_BUILDING_STORY
            if (np.Fields.TryGetValue("m_docAccess", out int documentAccess))
                e.DocumentAccessHandle = documentAccess;
            if (np.Fields.TryGetValue("1794:m_structType", out int structuralType))
                e.NativeStructuralType = structuralType;
            if (np.Fields.TryGetValue("m_famId", out int familyId))
                e.SerializedFamilyId = familyId;
            if (np.Fields.TryGetValue("m_unplacedOwnerId", out int unplacedOwnerId))
                e.SerializedUnplacedOwnerId = unplacedOwnerId;
            if (np.Fields.TryGetValue("m_ownerDBViewId", out int ownerDbViewId))
                e.SerializedOwnerDbViewId = ownerDbViewId;
            if (np.Fields.ContainsKey("__isLevel"))
            {
                e.IsNativeLevel = true;
                if (np.DoubleFields.TryGetValue("__levelElevation", out double levelElevation)
                    && double.IsFinite(levelElevation))
                    Apply(-1007102, StorageType.Double, levelElevation, force: true);
                if (np.StringFields.TryGetValue("m_text", out string? levelName)
                    && !string.IsNullOrWhiteSpace(levelName))
                {
                    // DatumPlane.m_text is the authoritative Level.Name. Generic block-string
                    // scanning is easily contaminated by referenced family/type names.
                    e.Name = levelName;
                    Apply(-1007101, StorageType.String, levelName, force: true); // LEVEL_NAME
                }
            }

            List<int>? CapturedElementIds(string fieldName)
            {
                var values = np.Fields
                    .Where(pair => pair.Key.StartsWith(fieldName + "@", StringComparison.Ordinal))
                    .OrderBy(pair => int.Parse(pair.Key[(fieldName.Length + 1)..],
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Select(pair => pair.Value)
                    .ToList();
                return values.Count > 0 ? values : null;
            }
            if (np.Fields.TryGetValue("m_useEqualityFormula", out int ueq))
                Apply(-1004514, StorageType.Integer, ueq); // DIM_DISPLAY_EQ
            if (np.Fields.TryGetValue("m_displayElevation", out int sde))
                Apply(-1006469, StorageType.Integer, sde); // SPOT_ELEV_DISPLAY_ELEVATIONS
            // DBView stored attributes (0x0E form rules fixed: lo=3 bare u32, containers of ref==20
            // = bare i32s). m_scale is stored as 1/scale (0.005 = 1:200).
            if (np.DoubleFields.TryGetValue("m_scale", out double sc) && sc > 1e-9 && sc <= 1.0)
            {
                int scale = (int)Math.Round(1.0 / sc);
                if (scale >= 1 && scale <= 100000)
                    Apply(-1005150, StorageType.Integer, scale); // VIEW_SCALE
            }
            if (np.StringFields.TryGetValue("m_viewName", out string? vn) && !string.IsNullOrEmpty(vn))
                Apply(-1005112, StorageType.String, vn); // VIEW_NAME
            if (np.Fields.TryGetValue("m_dbViewTypeId", out int vt) && vt > 0)
                ApplyType(vt);
            // Dimension: stored style (dim-chain overrides D1-D6 fixed the walk), sketch plane,
            // reference count.
            if (np.Fields.TryGetValue("m_styleSymbolId", out int dst) && dst > 0)
                ApplyType(dst);
            if (np.Fields.TryGetValue("m_dimSketchPlaneId", out int dsp) && dsp > 0)
                Apply(-1001380, StorageType.ElementId, dsp); // SKETCH_PLANE_PARAM
            if (np.Fields.TryGetValue("m_refPnts#", out int rc) && rc > 0)
                Apply(-1006520, StorageType.Integer, rc); // DIM_REFERENCE_COUNT
            if (np.Fields.TryGetValue("m_paramId", out int dlp))
                Apply(-1004510, StorageType.ElementId, dlp); // DIM_LABEL (global param id, -1 none)
            // Stored per-segment dim values.
            if (np.DoubleFields.TryGetValue("dimSegSum", out double dsum)
                && np.Fields.TryGetValue("dimSegN", out int dn) && dn > 0)
            {
                Apply(-1006521, StorageType.Double, dsum); // DIM_TOTAL_LENGTH
                if (dn == 1)
                    Apply(-1004500, StorageType.Double, dsum); // DIM_VALUE_LENGTH
            }
            // DBViewPlan underlay / orientation.
            if (np.Fields.TryGetValue("m_underlayOrientation", out int uo))
                Apply(-1005177, StorageType.Integer, uo); // VIEW_UNDERLAY_ORIENTATION
            if (np.Fields.TryGetValue("m_idUnderlayLevel", out int ul))
                Apply(-1005153, StorageType.ElementId, ul); // VIEW_UNDERLAY_BOTTOM_ID
            if (np.Fields.TryGetValue("m_idUnderlayTopLevel", out int ut))
                Apply(-1005335, StorageType.ElementId, ut); // VIEW_UNDERLAY_TOP_ID
            if (np.Fields.TryGetValue("m_fixedSketchPlaneId", out int fsp))
                Apply(-1005147, StorageType.ElementId, fsp); // VIEW_FIXED_SKETCH_PLANE
            if (np.Fields.TryGetValue("m_bUseTrueNorth", out int tn))
                Apply(-1005168, StorageType.Integer, tn); // PLAN_VIEW_NORTH
        }
    }

    /// <summary>
    /// Builds the narrow resolution index used by the serialized ceiling-type link. Every
    /// duplicated id is omitted: choosing either owner/target would make native link selection
    /// depend on materialization order.
    /// </summary>
    private static Dictionary<int, Element> BuildUniqueElementIndexForNativeCeilingLinks(
        IReadOnlyList<Element> elements)
    {
        var uniqueElementsById = new Dictionary<int, Element>();
        var duplicateIds = new HashSet<int>();
        foreach (Element element in elements)
        {
            if (duplicateIds.Contains(element.Id))
                continue;
            if (uniqueElementsById.TryAdd(element.Id, element))
                continue;
            uniqueElementsById.Remove(element.Id);
            duplicateIds.Add(element.Id);
        }
        return uniqueElementsById;
    }

    /// <summary>
    /// Carries only the exact class-642 occurrence/type relationship needed by ceiling QTO
    /// Reference. It is deliberately isolated from TypeId, type parameters, names, layers and
    /// material resolution.
    /// </summary>
    internal static void ApplySerializedCeilingTypeLinks(
        IReadOnlyList<Element> elements,
        IReadOnlyDictionary<uint, TfBlockDeserializer.NativeParams> nativeParams,
        IReadOnlyDictionary<uint, ushort> classIds)
    {
        Dictionary<int, Element> uniqueElementsById =
            BuildUniqueElementIndexForNativeCeilingLinks(elements);
        foreach (Element occurrence in elements)
        {
            occurrence.SerializedCeilingTypeId = null;
            if (occurrence.CategoryId != -2000038
                || !uniqueElementsById.TryGetValue(
                    occurrence.Id, out Element? uniqueOccurrence)
                || !ReferenceEquals(uniqueOccurrence, occurrence)
                || !classIds.TryGetValue(
                    (uint)occurrence.Id, out ushort occurrenceClassId)
                || occurrenceClassId != 642
                || !nativeParams.TryGetValue(
                    (uint)occurrence.Id, out var occurrenceParams)
                || !occurrenceParams.Fields.TryGetValue(
                    "m_id", out int serializedOwnerId)
                || serializedOwnerId != occurrence.Id
                || !occurrenceParams.Fields.TryGetValue(
                    "m_ceilingAttributesId", out int serializedTypeId)
                || serializedTypeId <= 0
                || serializedTypeId == occurrence.Id
                || !uniqueElementsById.TryGetValue(
                    serializedTypeId, out Element? serializedType)
                || serializedType.CategoryId != -2000038)
                continue;
            occurrence.SerializedCeilingTypeId = serializedTypeId;
        }
    }

    /// <summary>Carry one exact serialized host hop onto materialized occurrences.</summary>
    internal static void ApplySerializedHostParentIds(
        IReadOnlyList<Element> elements,
        IReadOnlyDictionary<uint, TfBlockDeserializer.NativeParams> nativeParams)
    {
        foreach (Element element in elements)
        {
            element.SerializedHostParentId = null;
            if (element.Id <= 0
                || !element.SerializedHostId.HasValue
                || !nativeParams.TryGetValue(
                    (uint)element.Id, out var elementParams)
                || !elementParams.Fields.TryGetValue(
                    "m_hostId", out int serializedHostId)
                || serializedHostId != element.SerializedHostId.Value
                || serializedHostId <= 0
                || serializedHostId == element.Id
                || !nativeParams.TryGetValue(
                    (uint)serializedHostId, out var hostParams)
                || !hostParams.Fields.TryGetValue(
                    "m_hostId", out int serializedHostParentId))
            {
                continue;
            }

            element.SerializedHostParentId = serializedHostParentId;
        }
    }

    /// <summary>
    /// Carry exact BaseRailing occurrence links and their authored top height across the
    /// parser/model boundary. Both relationship ids come only from named fields in the occurrence's
    /// own class-485 root block.
    /// </summary>
    internal static void ApplySerializedRailingData(
        IReadOnlyList<Element> elements,
        IReadOnlyDictionary<uint, TfBlockDeserializer.NativeParams> nativeParams,
        IReadOnlyDictionary<uint, ushort> classIds)
    {
        foreach (Element element in elements)
        {
            element.SerializedBaseRailingSymbolId = null;
            element.SerializedStairsRailingAttrId = null;
            element.SerializedRailingTopHeightFeet = null;
            if (element.Id <= 0
                || !classIds.TryGetValue((uint)element.Id, out ushort elementClassId)
                || !TfBlockDeserializer.IsSubclassOf(elementClassId, 485)
                || !nativeParams.TryGetValue(
                    (uint)element.Id, out var occurrenceParams)
                || !occurrenceParams.Fields.TryGetValue("m_id", out int ownerId)
                || ownerId != element.Id)
            {
                continue;
            }

            if (occurrenceParams.Fields.TryGetValue(
                    "m_baseRailingSymbolId", out int symbolId)
                && symbolId > 0 && symbolId != element.Id)
            {
                element.SerializedBaseRailingSymbolId = symbolId;
            }

            if (!occurrenceParams.Fields.TryGetValue(
                    "m_stairsRailingAttrId", out int attributeId)
                || attributeId <= 0 || attributeId == element.Id)
            {
                continue;
            }

            element.SerializedStairsRailingAttrId = attributeId;
            if (!classIds.TryGetValue((uint)attributeId, out ushort attributeClassId)
                || !TfBlockDeserializer.IsSubclassOf(attributeClassId, 490)
                || !nativeParams.TryGetValue(
                    (uint)attributeId, out var attributeParams)
                || !attributeParams.Fields.TryGetValue("m_id", out int attributeOwnerId)
                || attributeOwnerId != attributeId
                || !attributeParams.DoubleFields.TryGetValue(
                    "m_topRailHeight", out double topRailHeight)
                || !double.IsFinite(topRailHeight))
            {
                continue;
            }

            element.SerializedRailingTopHeightFeet = topRailHeight;
        }
    }

    /// <summary>
    /// Carries the three StairsElement occurrence values across the parser/model boundary.
    /// </summary>
    internal static void ApplySerializedStairData(
        IReadOnlyList<Element> elements,
        IReadOnlyDictionary<uint, ExactTfRootSource> stairElementSource,
        IReadOnlyDictionary<uint, ExactTfRootSource> stairTypeSource)
    {
        var uniqueElementsById = new Dictionary<int, Element>();
        var duplicateIds = new HashSet<int>();
        foreach (Element element in elements)
        {
            if (duplicateIds.Contains(element.Id))
                continue;
            if (uniqueElementsById.TryAdd(element.Id, element))
                continue;
            uniqueElementsById.Remove(element.Id);
            duplicateIds.Add(element.Id);
        }

        foreach (Element element in elements)
        {
            element.SerializedStairTypeId = null;
            element.SerializedStairTypeLinkIsResolved = false;
            element.SerializedStairBaseLevelId = null;
            element.SerializedStairsTypeClassId = null;
            element.SerializedActualNumberOfRisers = null;
            element.SerializedActualRiserHeightFeet = null;
            element.SerializedActualTreadDepthFeet = null;
        }

        foreach (Element candidateType in elements)
        {
            if (candidateType.Id <= 0
                || candidateType.CategoryId != -2000120
                || !uniqueElementsById.TryGetValue(
                    candidateType.Id, out Element? uniqueType)
                || !ReferenceEquals(uniqueType, candidateType)
                || !stairTypeSource.TryGetValue(
                    (uint)candidateType.Id, out ExactTfRootSource? source)
                || source.BlockCount != 1
                || source.Root == null
                || !source.Root.Fields.TryGetValue(
                    "m_id", out int serializedTypeOwnerId)
                || serializedTypeOwnerId != candidateType.Id)
            {
                continue;
            }

            candidateType.SerializedStairsTypeClassId = 3742;
        }

        foreach (Element occurrence in elements)
        {
            if (occurrence.Id <= 0
                || occurrence.CategoryId != -2000120
                || !uniqueElementsById.TryGetValue(
                    occurrence.Id, out Element? uniqueOccurrence)
                || !ReferenceEquals(uniqueOccurrence, occurrence)
                || !stairElementSource.TryGetValue(
                    (uint)occurrence.Id, out ExactTfRootSource? source)
                || source.BlockCount != 1
                || source.Root == null
                || !source.Root.Fields.TryGetValue(
                    "m_id", out int serializedOwnerId)
                || serializedOwnerId != occurrence.Id
                || !source.Root.Fields.TryGetValue(
                    "m_typeId", out int serializedTypeId)
                || serializedTypeId <= 0
                || serializedTypeId == occurrence.Id
                || !uniqueElementsById.TryGetValue(
                    serializedTypeId, out Element? serializedType)
                || serializedType.CategoryId != occurrence.CategoryId
                || serializedType.SerializedStairsTypeClassId != 3742)
            {
                continue;
            }

            occurrence.SerializedStairTypeId = serializedTypeId;
            occurrence.SerializedStairTypeLinkIsResolved = true;
            occurrence.SerializedStairBaseLevelId = source.Root.Fields.TryGetValue("m_baseLevelId", out int stairBaseLevel)
                && stairBaseLevel > 0 ? stairBaseLevel : null;

            if (source.Root.Fields.TryGetValue(
                    "m_actualNumberOfRisers", out int numberOfRisers)
                && numberOfRisers > 0)
            {
                occurrence.SerializedActualNumberOfRisers = numberOfRisers;
            }

            if (source.Root.DoubleFields.TryGetValue(
                    "m_actualRiserHeight", out double riserHeight)
                && double.IsFinite(riserHeight)
                && riserHeight > 0)
            {
                occurrence.SerializedActualRiserHeightFeet = riserHeight;
            }

            if (source.Root.DoubleFields.TryGetValue(
                    "m_actualTreadDepth", out double treadDepth)
                && double.IsFinite(treadDepth)
                && treadDepth > 0)
            {
                occurrence.SerializedActualTreadDepthFeet = treadDepth;
            }
        }
    }

    /// <summary>
    /// Apply BIP-keyed strings from elements' blocks: add missing string params and replace
    /// empty-string/-1 defaults with the real stored values (marks, descriptions...).
    /// </summary>
    private static void ApplyBipKeyedStrings(RevitFileInfo info, ParsingState? state)
    {
        if (state == null || state.BipKeyedStrings.Count == 0) return;
        foreach (var e in info.Elements)
        {
            if (!state.BipKeyedStrings.TryGetValue((uint)e.Id, out var map)) continue;
            var byBip = new Dictionary<int, Parameter>();
            foreach (var p in e.Parameters) byBip.TryAdd(p.Id, p);
            foreach (var (bip, val) in map)
            {
                if (byBip.TryGetValue(bip, out var p))
                {
                    // Replace only hollow defaults; keep explicitly decoded values.
                    bool hollow = p.Value is null || (p.Value is string s && s.Length == 0)
                        || (p.Value is int iv && iv == -1);
                    if (hollow && p.StorageType is StorageType.String or StorageType.None)
                    { p.Value = val; p.StorageType = StorageType.String; }
                }
                else
                {
                    BipNames.Lookup.TryGetValue(bip, out string? name);
                    e.Parameters.Add(BipHelpers.MakeBipParam(bip, name ?? bip.ToString(), StorageType.String, val));
                }
            }
        }
    }

    internal static int WallHeightOverrides, WallHeightSkippedNoElev;

    private static void ApplyComputedHeights(RevitFileInfo info, ParsingState? state)
    {
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId[e.Id] = e;

        // (1) level elevations - three sources in priority order: a) an already-decoded LEVEL_ELEV
        // parameter; b) the [elev][-0.0][-1.0] block-signature candidate; c) wall votes: for a wall
        // based on level L, bboxMinZ - baseOffset == elev(L).
        var wallVotes = new Dictionary<int, List<double>>();
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2000011 || GeometryBox(e) is not { Length: 6 } wb) continue;
            int baseId = -1; double bo = 0;
            foreach (var p in e.Parameters)
            {
                if (p.Id == -1001107 && p.Value is int b && b > 0) baseId = b;
                else if (p.Id == -1001108 && p.Value is double o1) bo = o1;
            }
            if (baseId <= 0) continue;
            if (!wallVotes.TryGetValue(baseId, out var list)) wallVotes[baseId] = list = new();
            list.Add(wb[2] - bo);
        }

        var levelElev = new Dictionary<int, double>();
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2000240) continue;
            var p = e.Parameters.FirstOrDefault(p => p.Id == -1007102);
            if (p?.Value is double d0) { levelElev[e.Id] = d0; continue; }

            double? cand = null;
            if (state != null && state.LevelElevCandidates.TryGetValue((uint)e.Id, out double mc)) cand = mc;
            else if (wallVotes.TryGetValue(e.Id, out var vs) && vs.Count > 0)
            {
                vs.Sort();
                cand = vs[vs.Count / 2];
            }
            if (cand is double cv)
            {
                levelElev[e.Id] = cv;
                if (p != null) p.Value = cv;
                else e.Parameters.Add(BipHelpers.MakeBipParam(-1007102, "LEVEL_ELEV", StorageType.Double, cv));
            }
        }

        foreach (var e in info.Elements)
        {
            var existing = new HashSet<int>();
            foreach (var p in e.Parameters) existing.Add(p.Id);
            void Add(int id, string name, double val)
            {
                if (!existing.Contains(id))
                    e.Parameters.Add(BipHelpers.MakeBipParam(id, name, StorageType.Double, val));
            }

            // (2) constrained wall height
            if (e.CategoryId == -2000011)
            {
                int baseId = -1, topId = -1; double bo = 0, to = 0;
                Parameter? hp = null; double? hAlt = null;
                foreach (var p in e.Parameters)
                {
                    switch (p.Id)
                    {
                        case -1001107: if (p.Value is int b0 && b0 > 0) baseId = b0; break; // WALL_BASE_CONSTRAINT
                        case -1001103: if (p.Value is int t0 && t0 > 0) topId = t0; break; // WALL_HEIGHT_TYPE
                        case -1001108: if (p.Value is double b1) bo = b1; break; // WALL_BASE_OFFSET
                        case -1001109: if (p.Value is double t1) to = t1; break; // WALL_TOP_OFFSET
                        case -1001105: hp = p; break; // WALL_USER_HEIGHT_PARAM
                        case -1001101: if (p.Value is double h1 && h1 > 0.1) hAlt = h1; break; // WALL_HEIGHT_PARAM
                    }
                }
                if (baseId > 0 && topId > 0 && topId != baseId
                    && levelElev.TryGetValue(baseId, out double be) && levelElev.TryGetValue(topId, out double te))
                {
                    double h = te + to - be - bo;
                    if (h > 0.1)
                    {
                        if (hp != null) hp.Value = h;
                        else Add(-1001105, "WALL_USER_HEIGHT_PARAM", h);
                        WallHeightOverrides++;
                    }
                }
                else if (hAlt is double ha && hp?.Value is double stored && Math.Abs(stored - ha) > 0.01)
                {
                    // No usable level constraints: the stored value is the raw unconnected height;
                    // the independently-computed WALL_HEIGHT_PARAM is closer to Revit's reported
                    // height.
                    hp.Value = ha;
                    WallHeightOverrides++;
                }
                else if (baseId > 0 && topId > 0) WallHeightSkippedNoElev++;
            }

            if (GeometryBox(e) is not { Length: 6 } bb) continue;
            double zExt = bb[5] - bb[2];

            // (3) curtain panel height/width
            if (e.CategoryId == -2000170 && zExt > 1e-6)
            {
                Add(-1010300, "CURTAIN_WALL_PANELS_HEIGHT", zExt);
                Add(-1010301, "CURTAIN_WALL_PANELS_WIDTH", Math.Max(bb[3] - bb[0], bb[4] - bb[1]));
            }

            // (4) structural elevations for floors / structural framing / columns / foundations
            if (e.CategoryId is -2000032 or -2001320 or -2001330 or -2001300)
            {
                Add(-1001598, "STRUCTURAL_ELEVATION_AT_TOP", bb[5]);
                Add(-1001561, "STRUCTURAL_ELEVATION_AT_BOTTOM", bb[2]);
                Add(-1001657, "STRUCTURAL_ELEVATION_AT_TOP_SURVEY", bb[5]);
                Add(-1001658, "STRUCTURAL_ELEVATION_AT_BOTTOM_SURVEY", bb[2]);
            }
        }

        // Sanitize computed-quantity placeholders: Revit never reports non-positive
        // HOST_AREA/VOLUME/PERIMETER; a - here is a binary placeholder, not a value.
        foreach (var e in info.Elements)
            e.Parameters.RemoveAll(p =>
                p.Id is -1012805 or -1012806 or -1012807 &&
                ((p.Value is double dv && dv <= 0) || (p.Value is int iv && iv <= 0)));
    }

    /// <summary>
    /// Build an axis-aligned 6-face box GeometryData from a bounding box - used for prismatic
    /// curtain members (mullions/panels) whose GRep is absent.
    /// </summary>
    private static GeometryData BuildBoxGeometry(double[] bb)
    {
        var geo = new GeometryData { BoundingBox = bb, Faces = new List<GeoFace>(), Vertices = new List<double[]>() };
        double x0 = bb[0], y0 = bb[1], z0 = bb[2], x1 = bb[3], y1 = bb[4], z1 = bb[5];
        // (origin, xAxis, yAxis, uMax, vMax, normal) per face
        var defs = new (double[] O, double[] X, double[] Y, double U, double V)[]
        {
            (new[]{x0, y0, z0}, new[]{0d, 1, 0}, new[]{0d, 0, 1}, y1 - y0, z1 - z0), // -X
            (new[]{x1, y0, z0}, new[]{0d, 1, 0}, new[]{0d, 0, 1}, y1 - y0, z1 - z0), // +X
            (new[]{x0, y0, z0}, new[]{1d, 0, 0}, new[]{0d, 0, 1}, x1 - x0, z1 - z0), // -Y
            (new[]{x0, y1, z0}, new[]{1d, 0, 0}, new[]{0d, 0, 1}, x1 - x0, z1 - z0), // +Y
            (new[]{x0, y0, z0}, new[]{1d, 0, 0}, new[]{0d, 1, 0}, x1 - x0, y1 - y0), // -Z
            (new[]{x0, y0, z1}, new[]{1d, 0, 0}, new[]{0d, 1, 0}, x1 - x0, y1 - y0), // +Z
        };
        foreach (var (o, xv, yv, u, v) in defs)
        {
            if (u <= 1e-9 || v <= 1e-9) continue;
            var f = new GeoFace
            {
                Origin = o, XVector = xv, YVector = yv,
                Normal = new[] { xv[1] * yv[2] - xv[2] * yv[1], xv[2] * yv[0] - xv[0] * yv[2], xv[0] * yv[1] - xv[1] * yv[0] },
                Envelope = new[] { 0, 0, u, v },
                Area = u * v,
                OuterVertices = new List<double[]>
                {
                    new[] { o[0], o[1], o[2] },
                    new[] { o[0] + u * xv[0], o[1] + u * xv[1], o[2] + u * xv[2] },
                    new[] { o[0] + u * xv[0] + v * yv[0], o[1] + u * xv[1] + v * yv[1], o[2] + u * xv[2] + v * yv[2] },
                    new[] { o[0] + v * yv[0], o[1] + v * yv[1], o[2] + v * yv[2] },
                },
            };
            geo.Faces.Add(f);
            foreach (var c in f.OuterVertices) geo.Vertices.Add(c);
        }
        return geo;
    }

    /// <summary>
    /// Constant/default parameters the Revit API reports on views, datums and dimensions, each
    /// constant for the whole file.
    /// </summary>
    private static void AddViewAndDatumDefaults(RevitFileInfo info)
    {
        // VIEW_PHASE: views report the model's dominant phase - derive it as the most common
        // PHASE_CREATED value across all elements.
        var phaseVotes = new Dictionary<int, int>();
        foreach (var e in info.Elements)
            foreach (var p in e.Parameters)
                if (p.Id == -1012100 && p.Value is int ph && ph > 0)
                { phaseVotes.TryGetValue(ph, out int n); phaseVotes[ph] = n + 1; break; }
        int domPhase = -1, domN = 0;
        foreach (var (ph, n) in phaseVotes) if (n > domN) { domPhase = ph; domN = n; }

        foreach (var e in info.Elements)
        {
            if (e.CategoryId is not int c) continue;
            var existing = new HashSet<int>();
            foreach (var p in e.Parameters) existing.Add(p.Id);
            void Add(int id, string name, StorageType st, object val)
            {
                if (!existing.Contains(id)) e.Parameters.Add(BipHelpers.MakeBipParam(id, name, st, val));
            }

            if (ViewCategories.Contains(c))
            {
                Add(-1005176, "VIEW_TEMPLATE", StorageType.ElementId, -1);
                Add(-1005199, "VIEW_TEMPLATE_FOR_SCHEDULE", StorageType.ElementId, -1);
                Add(-1005182, "VIEW_DEPENDENCY", StorageType.String, "Независимый");
                Add(-1011003, "VIEW_PARTS_VISIBILITY", StorageType.Integer, 1); // show original
                Add(-1005163, "VIEW_DISCIPLINE", StorageType.Integer, 1); // architectural
                Add(-1005333, "VIEW_GRAPH_SUN_PATH", StorageType.Integer, 0);
                Add(-1012202, "VIEWER_VOLUME_OF_INTEREST_CROP", StorageType.ElementId, -1);
                Add(-1005254, "VIEWPORT_ATTR_ORIENTATION_ON_SHEET", StorageType.Integer, 0);
                if (domPhase > 0)
                    Add(-1012102, "VIEW_PHASE", StorageType.ElementId, domPhase);
            }
            if (c is -2000530 or -2000220 or -2000240) // ref planes / grids / levels
                Add(-1012201, "DATUM_VOLUME_OF_INTEREST", StorageType.ElementId, -1);
            if (c is -2000260 or -2000261 or -2000262 || e.Dimension != null)
            {
                Add(-1006477, "BASELINE_DIM_OFFSET", StorageType.Double, 0.0);
                Add(-1004515, "DIM_LEADER", StorageType.Integer, 1);
            }
            if (c == -2009645) // analytical nodes
                Add(-1013446, "ANALYTICAL_MODEL_NODES_MARK", StorageType.Integer, 0);
            if (c == -2000045) // sketch lines: constant curve defaults ( in the export)
            {
                Add(-1006009, "CURVE_LEVEL", StorageType.ElementId, -1);
                Add(-1006008, "CURVE_HEIGHT_OFFSET", StorageType.Double, 0.0);
                Add(-1006007, "CURVE_IS_SLOPE_DEFINING", StorageType.Integer, 0);
                Add(-1006006, "DEFINES_CONSTANT_HEIGHT", StorageType.Integer, 0);
                Add(-1006015, "CURVE_PARAM_STEEL_CANTILEVER", StorageType.Double, 0.0);
                Add(-1006014, "CURVE_PARAM_CONCRETE_CANTILEVER", StorageType.Double, 0.0);
                Add(-1009523, "ANALYTICAL_MODEL_SKETCH_PROJECTION", StorageType.Integer, -6);
                Add(-1009522, "ANALYTICAL_MODEL_SKETCH_ALIGNMENT_METHOD", StorageType.Integer, 0);
                Add(-1152343, "ANALYTICAL_MODEL_MANUALLY_ADJUSTED", StorageType.Integer, 0);
                Add(-1006016, "ROOF_SLOPE", StorageType.Double, 1.0 / 12.0);
            }
            if (c == -2000530) // CLines: subcategory always reports the CLines category itself
                Add(-1006220, "CLINE_SUBCATEGORY", StorageType.ElementId, -2000530);
        }
    }

    /// <summary>
    /// Apply decoded 0x085A HVACLoadSpaceType energy-load parameters as Double parameters on the
    /// owning space-type element.
    /// </summary>
    private static void ApplyHvacSpaceTypeParams(RevitFileInfo info, ParsingState? state)
    {
        if (state == null || state.HvacSpaceTypeParams.Count == 0) return;
        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId[e.Id] = e;

        foreach (var (eid, vals) in state.HvacSpaceTypeParams)
        {
            if (!byId.TryGetValue((int)eid, out var e)) continue;
            var existing = new HashSet<int>();
            foreach (var p in e.Parameters) existing.Add(p.Id);
            foreach (var (bip, v) in vals)
            {
                if (existing.Contains(bip)) continue;
                BipNames.Lookup.TryGetValue(bip, out string? name);
                e.Parameters.Add(BipHelpers.MakeBipParam(bip, name ?? bip.ToString(), StorageType.Double, v));
            }

            // Schedule element references (power / occupancy / lighting).
            if (state.HvacScheduleRefs.TryGetValue(eid, out var sch))
            {
                void AddRef(int bip, string name, int val)
                {
                    if (!existing.Contains(bip) && state.AllKnownIds.Contains((uint)val))
                        e.Parameters.Add(BipHelpers.MakeBipParam(bip, name, StorageType.ElementId, val));
                }
                AddRef(-1114351, "SPACE_POWER_SCHEDULE_PARAM", sch.Power);
                AddRef(-1114349, "SPACE_OCCUPANCY_SCHEDULE_PARAM", sch.Occupancy);
                AddRef(-1114350, "SPACE_LIGHTING_SCHEDULE_PARAM", sch.Lighting);
            }

            // Outdoor airflow standard enum - 0 (ASHRAE default) across all space types.
            if (!existing.Contains(-1154687))
                e.Parameters.Add(BipHelpers.MakeBipParam(-1154687, "ROOM_OUTDOOR_AIRFLOW_STANDARD_PARAM", StorageType.Integer, 0));
        }
    }

    /// <summary>
    /// Emit CURVE_ELEM_LENGTH from the element's decoded location curve length (walls, model/sketch
    /// lines) when the binary body didn't already provide it.
    /// </summary>
    internal static void AddCurveLengths(RevitFileInfo info)
    {
        foreach (var e in info.Elements)
        {
            double len = 0;
            bool approximate = false;
            if (e.Location?.Length is double ll && ll > 1e-9) len = ll;
            // Mullions are straight bars along one axis: length = dominant bbox dimension.
            // Approximate - flagged below.
            else if (e.CategoryId == -2000171 && GeometryBox(e) is { Length: 6 } bb)
            {
                len = Math.Max(bb[3] - bb[0], Math.Max(bb[4] - bb[1], bb[5] - bb[2]));
                approximate = true;
            }
            if (len <= 1e-9) continue;

            bool has = false;
            foreach (var p in e.Parameters) if (p.Id == -1004005) { has = true; break; }
            if (!has)
            {
                var param = BipHelpers.MakeBipParam(-1004005, "CURVE_ELEM_LENGTH", StorageType.Double, len);
                param.IsApproximate = approximate;
                e.Parameters.Add(param);
            }
        }
    }

    public static void BuildDiscoveredElement(uint eid, ParsingState state, RevitFileInfo rvtInfo)
    {
        var rec = state.Records[eid];
        var elem = new Element { Id = (int)eid, TypeId = -1, IsDiscovered = true };

        elem.UniqueId = BuildUniqueId(eid, state);

        elem.Name = ResolveElementName(eid, state);

        if (rec.CategoryId.HasValue)
        {
            elem.CategoryId = rec.CategoryId.Value;
            if (BuiltInCategoryNames.Lookup.TryGetValue(rec.CategoryId.Value, out var catName))
                elem.Category = catName;
        }

        GRepData? grepData = null;
        if (state.GrepBlocks.TryGetValue(eid, out grepData))
        {
            // The tight box of the record stays on the geometry; the element box comes from the
            // header outline (ApplyHeaderBoundingBoxes).
            elem.Geometry = BuildGeometryData(grepData);
            ExpandInstanceGeometry(elem, grepData, elem.TypeId, state);
        }

        if (rec.References.Count > 0)
            elem.References = rec.References.Select(r => (int)r).ToList();

        // Body BIP entries
        foreach (var b in rec.BipParams)
        {
            if (b.BipId == 0 && !b.Value.HasValue) continue;
            BipNames.Lookup.TryGetValue(b.BipId, out string? bipName);
            var param = new Parameter
            {
                Id = b.BipId,
                StorageType = BipHelpers.BipStorageType(bipName),
                IsReadOnly = BipHelpers.IsBipReadOnly(bipName),
                Definition = new Definition { Id = b.BipId, BuiltInParameter = bipName, Name = bipName },
            };
            if (param.StorageType == StorageType.Double && state.ElemBipDoubles.TryGetValue(eid, out var dblMap) && dblMap.TryGetValue(b.BipId, out double dv))
                param.Value = dv;
            else if ((param.StorageType == StorageType.Integer || param.StorageType == StorageType.ElementId)
                && state.ElemBipInts.TryGetValue(eid, out var intMap) && intMap.TryGetValue(b.BipId, out int iv))
                param.Value = iv;
            else
                param.Value = b.Value.HasValue ? (object)b.Value.Value : null;
            elem.Parameters.Add(param);
        }

        AddBipDoubles(elem, eid, state);
        AddBipInts(elem, eid, state);
        AddPropagatedBipDoubles(elem, eid, state);
        AddBaseFields(elem, eid, state);
        AttachFamilyCutGeometry(elem, eid, state);

        if (grepData != null)
            AddComputedBipsFromGeometry(elem, grepData);

        AddBboxDimensionsIfMissing(elem, state);
        BuildLocationCurve(elem, eid, state);
        BuildInstanceTransform(elem, eid, state);
        ExpandTransformOnlyInstanceGeometry(elem, eid, elem.TypeId, state);
        AttachDimension(elem, eid, state);
        AttachSketchCurve(elem, eid, state);

        ApplyOwnWallPlanarBody(elem, state);
        rvtInfo.Elements.Add(elem);
    }

    public static void BuildBlockOnlyElement(uint bid, ParsingState state, RevitFileInfo rvtInfo)
    {
        var elem = new Element { Id = (int)bid, TypeId = -1, IsBlockOnly = true };
        elem.UniqueId = BuildUniqueId(bid, state);

        elem.Name = ResolveElementName(bid, state);

        GRepData? grepData = null;
        if (state.GrepBlocks.TryGetValue(bid, out grepData))
        {
            // The tight box of the record stays on the geometry; the element box comes from the
            // header outline (ApplyHeaderBoundingBoxes).
            elem.Geometry = BuildGeometryData(grepData);
            ExpandInstanceGeometry(elem, grepData, elem.TypeId, state);
        }

        AddBipDoubles(elem, bid, state);
        AddBaseFields(elem, bid, state);
        AttachFamilyCutGeometry(elem, bid, state);

        if (grepData != null)
            AddComputedBipsFromGeometry(elem, grepData);

        AddBboxDimensionsIfMissing(elem, state);
        AttachDimension(elem, bid, state);
        AttachSketchCurve(elem, bid, state);

        ApplyOwnWallPlanarBody(elem, state);
        rvtInfo.Elements.Add(elem);
    }

    /// <summary>
    /// Attach the exact FamilySymbol void operand irrespective of how the symbol element was
    /// discovered.
    /// </summary>
    internal static void AttachFamilyCutGeometry(Element elem, uint elementId, ParsingState state)
    {
        if (state.FamilyCutGreps.TryGetValue(elementId, out var familyCut))
        {
            elem.FamilyCutGeometry = BuildGeometryData(familyCut);
            elem.FamilyCutClosurePlaneOrigin = familyCut.FamilyClosurePlaneOrigin;
            elem.FamilyCutClosurePlaneNormal = familyCut.FamilyClosurePlaneNormal;
        }
        if (state.FamilyCutLoops.TryGetValue(elementId, out var familyCutLoops))
            elem.FamilyCutLoops = familyCutLoops;
        if (state.FamilySymbolCutLoopCounts.TryGetValue(elementId, out int cutLoopCount))
            elem.SerializedCutLoopCount = cutLoopCount;
        if (state.HostCuttingElementIds.TryGetValue(elementId, out var cuttingElementIds))
            elem.CuttingElementIds = cuttingElementIds.Select(id => (int)id).ToList();
        if (state.HostCutOutSteps.TryGetValue(elementId, out var cutOutSteps)
            && (!state.HostCutOutStepsFromLayoutSearch.Contains(elementId)
                || elem.References is { } hostReferences && cutOutSteps.All(step => hostReferences.Contains(step.ElementId))))
            elem.CutOutSteps = cutOutSteps;
    }

    /// <summary>
    /// Attach only schema-decoded Grid datum lines. The 0x083C owner and the DatumPlane fields are
    /// authoritative, so category propagation cannot introduce non-grid elements and no line is
    /// synthesized from a bounding box.
    /// </summary>
    private static void ApplyGridDatums(RevitFileInfo info, ParsingState? state)
    {
        if (state == null || state.GridDatums.Count == 0) return;
        foreach (var element in info.Elements)
        {
            if (!state.GridDatums.TryGetValue((uint)element.Id, out var datum)) continue;
            double dx = datum.EndPoint[0] - datum.StartPoint[0];
            double dy = datum.EndPoint[1] - datum.StartPoint[1];
            double dz = datum.EndPoint[2] - datum.StartPoint[2];
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!(length > 0) || !double.IsFinite(length)) continue;

            element.CategoryId = -2000220;
            element.Category = BuiltInCategoryNames.Lookup.TryGetValue(-2000220, out var category)
                ? category : "OST_Grids";
            // DatumPlane.m_text is the authored Grid.Name / DATUM_TEXT value. The grid type element
            // has its own name (the grid-head annotation style) and must never replace this
            // instance mark during later type-name propagation.
            element.Name = datum.Name;
            var datumText = element.Parameters.FirstOrDefault(parameter => parameter.Id == -1008000);
            if (datumText == null)
            {
                element.Parameters.Add(BipHelpers.MakeBipParam(
                    -1008000, "DATUM_TEXT", StorageType.String, datum.Name));
            }
            else
            {
                datumText.StorageType = StorageType.String;
                datumText.Value = datum.Name;
                datumText.Definition = new Definition
                {
                    Id = -1008000,
                    BuiltInParameter = "DATUM_TEXT",
                    Name = "DATUM_TEXT",
                };
            }
            element.Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = datum.StartPoint.ToArray(),
                EndPoint = datum.EndPoint.ToArray(),
                Direction = GridDirection(datum.XVector, dx, dy, dz, length),
                Length = length,
            };
            element.SerializedGridLevelInterval = datum.LevelInterval;
            if (datum.Envelope is { Length: 4 } envelope && datum.Origin is { Length: 3 } origin
                && datum.XVector is { Length: 3 } xVector && datum.YVector is { Length: 3 } yVector)
                element.GridPlane = new GridPlaneData(envelope, origin, xVector, yVector);
        }
    }

    /// <summary>
    /// The unit direction of a grid line: the plane's stored X vector when it runs along the line
    /// (the sign of a zero component is kept, Revit prints it into the GlobalId of the
    /// IfcGrid), else the one from the end points.
    /// </summary>
    internal static double[] GridDirection(double[]? stored, double dx, double dy, double dz, double length)
    {
        if (stored is { Length: 3 } && stored.All(double.IsFinite))
        {
            double storedLength = Math.Sqrt(stored[0] * stored[0] + stored[1] * stored[1] + stored[2] * stored[2]);
            if (storedLength > 0 && stored[0] * dx + stored[1] * dy + stored[2] * dz > 0)
                return Math.Abs(storedLength - 1) < 1e-9
                    ? [stored[0], stored[1], stored[2]]
                    : [stored[0] / storedLength, stored[1] / storedLength, stored[2] / storedLength];
        }
        return [dx / length, dy / length, dz / length];
    }

    /// <summary>
    /// Applies a type name only when a wall instance has a distinct, resolved same-category type
    /// element and that type owns exactly one 0xFFFF0200 block with a valid terminal name.
    /// </summary>
    internal static void ApplyValidatedWallTypeNames(
        IReadOnlyList<Element> elements,
        IReadOnlyDictionary<int, Element> elementsById,
        IReadOnlyDictionary<uint, (int BlockCount, string? TerminalName)> source,
        IReadOnlyDictionary<uint, TfBlockDeserializer.NativeParams> nativeParams)
    {
        const int WallsCategoryId = -2000011;
        var validatedTypeNames = new Dictionary<int, string>();
        var validatedInstanceNames = new Dictionary<int, string>();
        foreach (Element instance in elements)
        {
            if (instance.CategoryId != WallsCategoryId
                || !nativeParams.TryGetValue(
                    (uint)instance.Id, out var instanceNativeParams)
                || !instanceNativeParams.Fields.TryGetValue(
                    "m_WallAttributesId", out int serializedTypeId)
                || serializedTypeId <= 0
                || serializedTypeId == instance.Id
                || !elementsById.TryGetValue(
                    serializedTypeId, out Element? typeElement)
                || typeElement.CategoryId != WallsCategoryId
                || !source.TryGetValue(
                    (uint)typeElement.Id, out var typeSource)
                || typeSource.BlockCount != 1
                || string.IsNullOrWhiteSpace(typeSource.TerminalName))
                continue;
            validatedTypeNames[typeElement.Id] = typeSource.TerminalName;
            validatedInstanceNames[instance.Id] = typeSource.TerminalName;
        }

        foreach (var (typeId, typeName) in validatedTypeNames)
            elementsById[typeId].Name = typeName;
        foreach (var (instanceId, typeName) in validatedInstanceNames)
            elementsById[instanceId].Name = typeName;
    }

    /// <summary>
    /// The serialized type relationship comes exclusively from the instance's own
    /// m_floorAttributesId.
    /// </summary>
    internal static void ApplyValidatedFloorTypeReferenceNames(
        IReadOnlyList<Element> elements,
        IReadOnlyDictionary<int, Element> elementsById,
        IReadOnlyDictionary<uint, (int BlockCount, string? TerminalName)> source,
        IReadOnlyDictionary<uint, TfBlockDeserializer.NativeParams> nativeParams,
        ParsingState? hostState = null)
    {
        const int FloorsCategoryId = -2000032;
        var occurrences = new Dictionary<int, int>();
        foreach (Element element in elements)
        {
            occurrences[element.Id] = occurrences.GetValueOrDefault(element.Id) + 1;
            element.ValidatedFloorOwnTypeName = null;
            element.ValidatedFloorTypeReferenceName = null;
            element.ValidatedFloorTypeReferenceIsExplicit = false;
            element.ValidatedFloorTypeReferenceIsBlocked = false;
        }

        foreach (Element instance in elements)
        {
            if (instance.CategoryId != FloorsCategoryId
                || !nativeParams.TryGetValue(
                    (uint)instance.Id, out var instanceNativeParams)
                || !instanceNativeParams.Fields.TryGetValue(
                    "m_floorAttributesId", out int serializedTypeId)
                || serializedTypeId <= 0
                || serializedTypeId == instance.Id
                || !elementsById.TryGetValue(
                    serializedTypeId, out Element? typeElement)
                || typeElement.CategoryId != FloorsCategoryId)
                continue;

            ExactIfcStringParameterResult explicitReference =
                ExactIfcStringParameterResolver.ResolveReference(
                    instance, typeElement);
            if (explicitReference.State == ExactIfcStringParameterState.Blocked)
            {
                instance.ValidatedFloorTypeReferenceIsBlocked = true;
                continue;
            }

            if (explicitReference.State == ExactIfcStringParameterState.Value)
            {
                instance.ValidatedFloorTypeReferenceName = explicitReference.Value;
                instance.ValidatedFloorTypeReferenceIsExplicit = true;
                continue;
            }

            // The own SymbolInfo name of the type, kept apart: the legacy profile still names a floor by the terminal string.
            if (hostState != null
                && TryGetAgreedHostType(typeElement, occurrences.GetValueOrDefault(typeElement.Id), hostState, out var hostType)
                && !string.IsNullOrWhiteSpace(hostType.TypeName))
                instance.ValidatedFloorOwnTypeName = hostType.TypeName;

            if (source.TryGetValue(
                    (uint)typeElement.Id, out var typeSource)
                && typeSource.BlockCount == 1
                && !string.IsNullOrWhiteSpace(typeSource.TerminalName))
                instance.ValidatedFloorTypeReferenceName = typeSource.TerminalName;
        }
    }

    /// <summary>Applies the exact family-symbol type name to curtain-wall mullion instances.</summary>
    internal static void ApplyValidatedMemberTypeNames(
        IReadOnlyList<Element> elements,
        IReadOnlyDictionary<int, Element> elementsById,
        IReadOnlyDictionary<uint, TfBlockDeserializer.NativeParams> nativeParams,
        IReadOnlyDictionary<uint, string> elementNameTable,
        IReadOnlyDictionary<uint, string> nativeElementNames)
    {
        const int CurtainWallMullionsCategoryId = -2000171;
        foreach (Element instance in elements)
        {
            if (instance.CategoryId != CurtainWallMullionsCategoryId
                || !nativeParams.TryGetValue(
                    (uint)instance.Id, out var instanceNativeParams)
                || !instanceNativeParams.Fields.TryGetValue(
                    "m_masterSymbolId", out int masterSymbolId)
                || masterSymbolId <= 0
                || masterSymbolId == instance.Id
                || instance.TypeId != masterSymbolId
                || instance.FamilyTypeId != masterSymbolId
                || !elementsById.TryGetValue(
                    masterSymbolId, out Element? masterSymbol)
                || masterSymbol.CategoryId != CurtainWallMullionsCategoryId
                || !elementNameTable.TryGetValue(
                    (uint)masterSymbolId, out string? tableName)
                || string.IsNullOrWhiteSpace(tableName)
                || !nativeElementNames.TryGetValue(
                    (uint)masterSymbolId, out string? nativeName)
                || !string.Equals(tableName, nativeName, StringComparison.Ordinal)
                || !string.Equals(
                    tableName, masterSymbol.Name, StringComparison.Ordinal))
                continue;
            instance.Name = tableName;
        }
    }

    // Late native type canonicalization must not leave an earlier type's name on a door.
    internal static void ApplyValidatedDoorTypeNames(IReadOnlyList<Element> elements, ParsingState state)
    {
        var unique = BuildUniqueElementIndexForNativeCeilingLinks(elements);
        foreach (var instance in elements)
        {
            if (instance.CategoryId != -2000023 || instance.Id <= 0
                || !unique.TryGetValue(instance.Id, out var own) || !ReferenceEquals(own, instance)
                || !state.FamilyInstanceSymbolIds.TryGetValue((uint)instance.Id, out var link)
                || link is not { BlockCount: 1, TypeId: > 0 } || link.TypeId == instance.Id
                || !unique.TryGetValue(link.TypeId.Value, out var type) || type.CategoryId != instance.CategoryId
                || !state.FamilySymbolNames.TryGetValue((uint)type.Id, out var name)
                || name.BlockCount != 1 || string.IsNullOrWhiteSpace(name.Name))
                continue;
            type.Name = name.Name;
            instance.Name = name.Name;
        }
    }

    /// <summary>
    /// Applies a ceiling type name only through a distinct, resolved same-category TypeId whose
    /// type owns exactly one 0xFFFF0286 block with a valid terminal name.
    /// </summary>
    internal static void ApplyValidatedCeilingTypeNames(
        IReadOnlyList<Element> elements,
        IReadOnlyDictionary<int, Element> elementsById,
        IReadOnlyDictionary<uint, (int BlockCount, string? TerminalName)> source)
    {
        const int CeilingsCategoryId = -2000038;
        var validatedTypeNames = new Dictionary<int, string>();
        var validatedInstanceNames = new Dictionary<int, string>();
        foreach (Element instance in elements)
        {
            if (instance.CategoryId != CeilingsCategoryId
                || instance.TypeId <= 0
                || instance.TypeId == instance.Id
                || !elementsById.TryGetValue(
                    instance.TypeId, out Element? typeElement)
                || typeElement.CategoryId != CeilingsCategoryId
                || !source.TryGetValue(
                    (uint)typeElement.Id, out var typeSource)
                || typeSource.BlockCount != 1
                || string.IsNullOrWhiteSpace(typeSource.TerminalName))
                continue;
            validatedTypeNames[typeElement.Id] = typeSource.TerminalName;
            validatedInstanceNames[instance.Id] = typeSource.TerminalName;
        }

        foreach (var (typeId, typeName) in validatedTypeNames)
            elementsById[typeId].Name = typeName;
        foreach (var (instanceId, typeName) in validatedInstanceNames)
            elementsById[instanceId].Name = typeName;
    }

    /// <summary>
    /// The relationship must be the exact class-642 link previously carried from the occurrence's
    /// own block.
    /// </summary>
    internal static void ApplyValidatedCeilingTypeReferenceNames(
        IReadOnlyList<Element> elements,
        IReadOnlyDictionary<uint, (int BlockCount, string? TerminalName)> source,
        IReadOnlyDictionary<uint, TfBlockDeserializer.NativeParams> nativeParams,
        IReadOnlyDictionary<uint, ushort> classIds)
    {
        const int CeilingsCategoryId = -2000038;
        Dictionary<int, Element> uniqueElementsById =
            BuildUniqueElementIndexForNativeCeilingLinks(elements);
        foreach (Element element in elements)
        {
            element.ValidatedCeilingTypeReferenceName = null;
            element.ValidatedCeilingTypeReferenceIsExplicit = false;
            element.ValidatedCeilingTypeReferenceIsBlocked = false;
        }

        foreach (Element instance in elements)
        {
            if (instance.CategoryId != CeilingsCategoryId
                || !uniqueElementsById.TryGetValue(
                    instance.Id, out Element? uniqueInstance)
                || !ReferenceEquals(uniqueInstance, instance)
                || !classIds.TryGetValue(
                    (uint)instance.Id, out ushort instanceClassId)
                || instanceClassId != 642
                || !nativeParams.TryGetValue(
                    (uint)instance.Id, out var instanceNativeParams)
                || !instanceNativeParams.Fields.TryGetValue(
                    "m_id", out int serializedOwnerId)
                || serializedOwnerId != instance.Id
                || !instanceNativeParams.Fields.TryGetValue(
                    "m_ceilingAttributesId", out int serializedTypeId)
                || instance.SerializedCeilingTypeId != serializedTypeId
                || serializedTypeId <= 0
                || serializedTypeId == instance.Id
                || !uniqueElementsById.TryGetValue(
                    serializedTypeId, out Element? typeElement)
                || typeElement.CategoryId != CeilingsCategoryId)
                continue;

            ExactIfcStringParameterResult explicitReference =
                ExactIfcStringParameterResolver.ResolveReference(
                    instance, typeElement);
            if (explicitReference.State == ExactIfcStringParameterState.Blocked)
            {
                instance.ValidatedCeilingTypeReferenceIsBlocked = true;
                continue;
            }

            if (explicitReference.State == ExactIfcStringParameterState.Value)
            {
                instance.ValidatedCeilingTypeReferenceName = explicitReference.Value;
                instance.ValidatedCeilingTypeReferenceIsExplicit = true;
                continue;
            }

            if (source.TryGetValue(
                    (uint)typeElement.Id, out var typeSource)
                && typeSource.BlockCount == 1
                && !string.IsNullOrWhiteSpace(typeSource.TerminalName))
                instance.ValidatedCeilingTypeReferenceName = typeSource.TerminalName;
        }
    }

    /// <summary>
    /// Unlike the host-object (wall/floor/ceiling) mechanisms, a window's type is a FamilySymbol,
    /// not a same-category typed value block.
    /// </summary>
    internal static void ApplyValidatedWindowTypeReferenceNames(
        IReadOnlyList<Element> elements,
        IReadOnlyDictionary<int, Element> elementsById,
        IReadOnlyDictionary<uint, TfBlockDeserializer.NativeParams> nativeParams,
        IReadOnlyDictionary<uint, string> elementNameTable)
    {
        const int WindowsCategoryId = -2000014;
        foreach (Element element in elements)
        {
            element.ValidatedWindowTypeReferenceName = null;
            element.ValidatedWindowTypeReferenceIsExplicit = false;
            element.ValidatedWindowTypeReferenceIsBlocked = false;
        }

        foreach (Element instance in elements)
        {
            if (instance.CategoryId != WindowsCategoryId
                || !nativeParams.TryGetValue(
                    (uint)instance.Id, out var instanceNativeParams)
                || !instanceNativeParams.Fields.TryGetValue(
                    "m_masterSymbolId", out int serializedTypeId)
                || serializedTypeId <= 0
                || serializedTypeId == instance.Id
                || !elementsById.TryGetValue(
                    serializedTypeId, out Element? typeElement)
                || typeElement.CategoryId != WindowsCategoryId)
                continue;

            ExactIfcStringParameterResult explicitReference =
                ExactIfcStringParameterResolver.ResolveReference(
                    instance, typeElement);
            if (explicitReference.State == ExactIfcStringParameterState.Blocked)
            {
                instance.ValidatedWindowTypeReferenceIsBlocked = true;
                continue;
            }

            if (explicitReference.State == ExactIfcStringParameterState.Value)
            {
                instance.ValidatedWindowTypeReferenceName = explicitReference.Value;
                instance.ValidatedWindowTypeReferenceIsExplicit = true;
                continue;
            }

            if (elementNameTable.TryGetValue(
                    (uint)serializedTypeId, out var tableName)
                && !string.IsNullOrWhiteSpace(tableName))
                instance.ValidatedWindowTypeReferenceName = tableName;
        }
    }

    /// <summary>
    /// The BuiltInCategory ids IfcExporter.MapCategoryToIfcType classifies as
    /// IFCBUILDINGELEMENTPROXY.
    /// </summary>
    private static readonly HashSet<int> ProxyCategoryIds = new()
    {
        -2000151, // OST_GenericModel
        -2000181, // OST_Cornices
        -2001350, // OST_SpecialityEquipment
        -2001370,
        -2008081,
        -2001040, // OST_ElectricalEquipment
        -2001360, // OST_Planting / RPC
        -2003400, // OST_Mass
        -2000340, // OST_GenericModel (legacy id)
        -2000083, // OST_StructConnectionBolts
        -2001352, // OST_MechanicalEquipment
    };

    /// <summary>
    /// The residual gap is proxies with no own decoded m_masterSymbolId at all - a different,
    /// honest coverage gap, not this mechanism's to close.
    /// </summary>
    internal static void ApplyValidatedProxyTypeReferenceNames(
        IReadOnlyList<Element> elements,
        IReadOnlyDictionary<int, Element> elementsById,
        IReadOnlyDictionary<uint, TfBlockDeserializer.NativeParams> nativeParams,
        IReadOnlyDictionary<uint, string> elementNameTable)
    {
        foreach (Element element in elements)
        {
            element.ValidatedProxyTypeReferenceName = null;
            element.ValidatedProxyTypeReferenceIsExplicit = false;
            element.ValidatedProxyTypeReferenceIsBlocked = false;
        }

        foreach (Element instance in elements)
        {
            if (instance.CategoryId is not int instanceCategoryId
                || !ProxyCategoryIds.Contains(instanceCategoryId)
                || !nativeParams.TryGetValue(
                    (uint)instance.Id, out var instanceNativeParams)
                || !instanceNativeParams.Fields.TryGetValue(
                    "m_masterSymbolId", out int serializedTypeId)
                || serializedTypeId <= 0
                || serializedTypeId == instance.Id
                || !elementsById.TryGetValue(
                    serializedTypeId, out Element? typeElement)
                || typeElement.CategoryId != instanceCategoryId)
                continue;

            ExactIfcStringParameterResult explicitReference =
                ExactIfcStringParameterResolver.ResolveReference(
                    instance, typeElement);
            if (explicitReference.State == ExactIfcStringParameterState.Blocked)
            {
                instance.ValidatedProxyTypeReferenceIsBlocked = true;
                continue;
            }

            if (explicitReference.State == ExactIfcStringParameterState.Value)
            {
                instance.ValidatedProxyTypeReferenceName = explicitReference.Value;
                instance.ValidatedProxyTypeReferenceIsExplicit = true;
                continue;
            }

            if (elementNameTable.TryGetValue(
                    (uint)serializedTypeId, out var tableName)
                && !string.IsNullOrWhiteSpace(tableName))
                instance.ValidatedProxyTypeReferenceName = tableName;
        }
    }

    /// <summary>
    /// Extends the already-cleared proxy Reference pass with WallSweep's own type relation and
    /// CorniceAttr's own SymbolInfo. Public names, type ids and IFC identity remain unchanged;
    /// unvalidated owners retain the previous fallback.
    /// </summary>
    internal static void ApplyValidatedWallSweepTypeReferenceNames(IReadOnlyList<Element> elements, ParsingState state)
    {
        var candidates = state.WallSweepTypeIds.Where(pair => pair.Key > 0
            && pair.Value is { BlockCount: 1, TypeId: > 0 }).ToList();
        var needed = candidates.Select(pair => (int)pair.Key)
            .Concat(candidates.Select(pair => pair.Value.TypeId!.Value)).ToHashSet();
        var byId = elements.Where(element => needed.Contains(element.Id))
            .GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.ToList());
        foreach (var (owner, validation) in candidates)
        {
            int typeId = validation.TypeId!.Value;
            if (!byId.TryGetValue((int)owner, out var instances) || instances.Count != 1
                || !byId.TryGetValue(typeId, out var types) || types.Count != 1
                || owner == (uint)typeId || instances[0].CategoryId != -2000181 || types[0].CategoryId != -2000181
                || !state.CorniceTypeNames.TryGetValue((uint)typeId, out var typeValidation)
                || typeValidation.BlockCount != 1 || string.IsNullOrWhiteSpace(typeValidation.Name))
                continue;
            Element instance = instances[0];
            var explicitReference = ExactIfcStringParameterResolver.ResolveReference(instance, types[0]);
            instance.ValidatedProxyTypeReferenceIsBlocked = explicitReference.State == ExactIfcStringParameterState.Blocked;
            instance.ValidatedProxyTypeReferenceIsExplicit = explicitReference.State == ExactIfcStringParameterState.Value;
            instance.ValidatedProxyTypeReferenceName = explicitReference.State switch
            {
                ExactIfcStringParameterState.Value => explicitReference.Value,
                ExactIfcStringParameterState.Blocked => null,
                _ => typeValidation.Name,
            };
        }
    }

    internal static void ApplyValidatedDistributionTypeReferenceNames(IReadOnlyList<Element> elements, ParsingState state)
    {
        foreach (var element in elements)
        {
            element.ValidatedDistributionTypeReferenceName = null;
            element.ValidatedDistributionTypeReferenceIsExplicit = false;
            element.ValidatedDistributionTypeReferenceIsBlocked = false;
        }
        // Categories currently mapped to flow terminals/segments/fittings/controllers by the IFC
        // exporter.
        var candidates = elements.Where(element => element.CategoryId is -2001160 or -2001120
            or -2008013 or -2008044 or -2008049 or -2008093 or -2008075
            or -2008000 or -2008016 or -2008099 or -2008128 or -2008055 or -2008126).ToList();
        var needed = candidates.Select(element => element.Id).ToHashSet();
        foreach (var element in candidates)
            if (state.FamilyInstanceSymbolIds.TryGetValue((uint)element.Id, out var validation)
                && validation is { BlockCount: 1, TypeId: > 0 }) needed.Add(validation.TypeId.Value);
        var byId = elements.Where(element => needed.Contains(element.Id)).GroupBy(element => element.Id)
            .ToDictionary(group => group.Key, group => group.ToList());
        foreach (var instance in candidates)
        {
            if (instance.Id <= 0 || byId[instance.Id].Count != 1
                || !state.FamilyInstanceSymbolIds.TryGetValue((uint)instance.Id, out var validation)
                || validation is not { BlockCount: 1, TypeId: > 0 } || validation.TypeId == instance.Id
                || !byId.TryGetValue(validation.TypeId.Value, out var types) || types.Count != 1
                || types[0].CategoryId != instance.CategoryId
                || !state.FamilySymbolNames.TryGetValue((uint)validation.TypeId.Value, out var typeValidation)
                || typeValidation.BlockCount != 1 || string.IsNullOrWhiteSpace(typeValidation.Name)) continue;
            var explicitReference = ExactIfcStringParameterResolver.ResolveReference(instance, types[0]);
            instance.ValidatedDistributionTypeReferenceIsBlocked = explicitReference.State == ExactIfcStringParameterState.Blocked;
            instance.ValidatedDistributionTypeReferenceIsExplicit = explicitReference.State == ExactIfcStringParameterState.Value;
            instance.ValidatedDistributionTypeReferenceName = explicitReference.State switch
            {
                ExactIfcStringParameterState.Value => explicitReference.Value,
                ExactIfcStringParameterState.Blocked => null,
                _ => typeValidation.Name,
            };
        }
    }

    /// <summary>
    /// Attaches the own-root top-attachment flag to each uniquely materialized wall whose single
    /// SWall/ArcWall root was read exactly. Returns how many walls received a value.
    /// </summary>
    internal static int ApplyWallJoinedToTop(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (Element element in elements)
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        int applied = 0;
        foreach (Element element in elements)
        {
            element.SerializedWallJoinedToTop = null;
            if (element.Id <= 0
                || element.CategoryId != -2000011
                || counts[element.Id] != 1
                || !state.WallJoinedToTop.TryGetValue((uint)element.Id, out var source)
                || source.BlockCount != 1
                || source.JoinedToTop is not bool joinedToTop)
                continue;
            element.SerializedWallJoinedToTop = joinedToTop;
            if (joinedToTop)
                SetParam(element, -1001117, "WALL_TOP_IS_ATTACHED", StorageType.Integer, 1);
            applied++;
        }
        return applied;
    }

    /// <summary>
    /// Attaches the export id and the Revit UniqueId of every element whose ElemRec history and
    /// episode are read exactly (<see cref="ElementExportIds"/>).
    /// </summary>
    internal static (int Elements, int ReplacedUniqueIds) ApplyExportIds(
        IReadOnlyList<Element> elements, ParsingState state)
    {
        int applied = 0, replaced = 0;
        foreach (Element element in elements)
        {
            element.SerializedExportId = null;
            element.SerializedHandleExportId = null;
            // Id 0 is an ElemTable row of the 64-bit layout (state.WideSchema); the other layouts
            // never had it here.
            if (element.Id < 0 || element.Id == 0 && state.WideSchema == null
                || state.ElemRecHistories is not { } histories
                || !histories.TryGetValue((uint)element.Id, out var history)
                || ElementExportIds.EpisodeGuid(history, state.HistoryEpisodeGuids, state.EpisodeTableX)
                    is not Guid episode)
                continue;
            element.SerializedExportId = ElementExportIds.ExportId(episode, history.OriginalElementId);
            element.SerializedHandleExportId = ElementExportIds.ExportId(episode, (uint)element.Id);
            string uniqueId = ElementExportIds.UniqueId(episode, history.OriginalElementId);
            if (element.UniqueId != null && element.UniqueId != uniqueId) replaced++;
            element.UniqueId = uniqueId;
            applied++;
        }
        return (applied, replaced);
    }

    /// <summary>
    /// Attaches the exact control joins of each uniquely materialized wall's own driver (<see
    /// cref="TfBlockDeserializer.ExtractWallEndJoins"/>).
    /// </summary>
    internal static int ApplyWallEndJoins(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (Element element in elements)
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        int applied = 0;
        foreach (Element element in elements)
        {
            element.SerializedWallEndJoins = null;
            if (element.Id <= 0
                || element.CategoryId != -2000011
                || counts[element.Id] != 1
                || !state.WallEndJoins.TryGetValue((uint)element.Id, out var source)
                || source.BlockCount != 1
                || source.Joins is not { } joins)
                continue;
            element.SerializedWallEndJoins = joins;
            applied++;
        }
        return applied;
    }

    /// <summary>
    /// Attaches own-root material names to uniquely materialized elements and the ordered
    /// CompoundStructure layers to uniquely materialized OST_Ceilings types.
    /// </summary>
    internal static (int Materials, int CeilingTypes) ApplyCeilingFinishSource(
        IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (Element element in elements)
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        int materials = 0, ceilingTypes = 0;
        foreach (Element element in elements)
        {
            element.SerializedMaterialName = null;
            element.SerializedCeilingTypeLayers = null;
            if (element.Id <= 0 || counts[element.Id] != 1) continue;
            if (state.MaterialNames.TryGetValue((uint)element.Id, out var material)
                && material.BlockCount == 1 && material.Name != null)
            {
                element.SerializedMaterialName = material.Name;
                materials++;
            }
            if (element.CategoryId == -2000038
                && state.CeilingCompoundStructureLayerSource.TryGetValue(
                    (uint)element.Id, out var ceiling)
                && ceiling.BlockCount == 1 && ceiling.Layers != null)
            {
                element.SerializedCeilingTypeLayers = ceiling.Layers;
                ceilingTypes++;
            }
        }
        return (materials, ceilingTypes);
    }

    /// <summary>The own HostObjAttr validation of a type element: its unique root, class and category agree with it, and its layers are decoded.</summary>
    private static bool TryGetAgreedHostType(Element element, int occurrences, ParsingState state,
        out (int BlockCount, ushort ClassId, RawCompoundStructureLayer[]? Layers, string? TypeName) validation)
    {
        validation = default;
        if (element.Id <= 0 || occurrences != 1 || element.IsBlockOnly
            || !state.SystemTypeRoots.TryGetValue((uint)element.Id, out var root) || root.BlockCount != 1
            || !state.HostTypeCompoundStructures.TryGetValue((uint)element.Id, out validation)
            || validation.BlockCount != 1 || validation.ClassId != root.ClassId || validation.Layers == null)
            return false;
        return (root.ClassId, element.CategoryId, root.Discriminator) switch
        {
            (512, -2000011, _) or (646, -2000038, _) => true,
            (1915, -2000032, 0) or (1915, -2001300, 1) => true,
            // RoofAttributes (Basic Roof) and BuildingPadType: HostObjAttr and nothing of their
            // own.
            (3452, -2000035, _) or (571, -2001263, _) => true,
            _ => false,
        };
    }

    internal static int ApplyHostTypeCompoundStructures(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (var element in elements)
        {
            element.SerializedHostTypeLayers = null;
            element.ValidatedHostTypeLayerSetName = null;
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        }
        int applied = 0;
        foreach (var element in elements)
        {
            if (!TryGetAgreedHostType(element, counts[element.Id], state, out var validation)) continue;
            element.SerializedHostTypeLayers = validation.Layers;
            if (!string.IsNullOrWhiteSpace(validation.TypeName) && !string.IsNullOrEmpty(element.ValidatedSystemFamilyName))
                element.ValidatedHostTypeLayerSetName = $"{element.ValidatedSystemFamilyName}:{validation.TypeName}";
            applied++;
        }
        return applied;
    }

    internal static int ApplyBasicWallTypeThickness(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (var element in elements)
        {
            element.ValidatedWallTypeThicknessFeet = null;
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        }
        int applied = 0;
        foreach (var element in elements)
        {
            if (element.Id <= 0 || counts[element.Id] != 1 || element.IsBlockOnly
                || element.CategoryId != -2000011 || element.SerializedSystemTypeId is not int typeId
                || !counts.TryGetValue(typeId, out int typeCount) || typeCount != 1
                || !state.SystemTypeRoots.TryGetValue((uint)typeId, out var root)
                || root.BlockCount != 1 || root.ClassId != 512
                || !state.HostTypeCompoundStructures.TryGetValue((uint)typeId, out var host)
                || host.BlockCount != 1 || host.ClassId != 512
                || !state.BasicWallTypeThicknessFeet.TryGetValue((uint)typeId, out double? value)
                || value is not double width || !double.IsFinite(width) || width <= 0) continue;
            element.ValidatedWallTypeThicknessFeet = width;
            applied++;
        }
        return applied;
    }

    internal static int ApplyCompoundCeilingTypeThickness(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (var element in elements)
        {
            element.ValidatedCeilingTypeThicknessFeet = null;
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        }
        int applied = 0;
        foreach (var element in elements)
        {
            if (element.Id <= 0 || counts[element.Id] != 1 || element.IsBlockOnly || element.CategoryId != -2000038
                || !state.SystemTypeRoots.TryGetValue((uint)element.Id, out var root)
                || root.BlockCount != 1 || root.ClassId != 646
                || !state.HostTypeCompoundStructures.TryGetValue((uint)element.Id, out var host)
                || host.BlockCount != 1 || host.ClassId != 646
                || !state.CompoundCeilingTypeThicknessFeet.TryGetValue((uint)element.Id, out double? value)
                || value is not double thickness || !double.IsFinite(thickness)) continue;
            element.ValidatedCeilingTypeThicknessFeet = thickness;
            applied++;
        }
        return applied;
    }

    internal static int ApplyValidatedSystemFamilyNames(
        IReadOnlyList<Element> elements, ParsingState state, Lookup.RevitSystemFamilyNames? names)
    {
        foreach (var element in elements) { element.ValidatedSystemFamilyName = null; element.ValidatedMepFamilyName = null; }
        if (names == null) return 0;
        var counts = new Dictionary<int, int>();
        foreach (var element in elements) counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        int assigned = 0;
        foreach (var element in elements)
        {
            if (element.Id <= 0 || counts[element.Id] != 1
                || !state.SystemTypeRoots.TryGetValue((uint)element.Id, out var root)
                || root.BlockCount != 1 || root.ClassId == 0
                || Lookup.RevitSystemFamilyNames.KindOf(root.ClassId, root.Discriminator, element.CategoryId)
                    is not Lookup.SystemFamilyKind kind
                || names.Get(kind) is not string name) continue;
            if (kind is Lookup.SystemFamilyKind.PipeTypes or Lookup.SystemFamilyKind.FlexPipeRound
                or Lookup.SystemFamilyKind.PipeInsulation or Lookup.SystemFamilyKind.CableTrayWithFittings
                or Lookup.SystemFamilyKind.CableTrayWithoutFittings or Lookup.SystemFamilyKind.ConduitWithFittings
                or Lookup.SystemFamilyKind.ConduitWithoutFittings)
                element.ValidatedMepFamilyName = name;
            else
                element.ValidatedSystemFamilyName = name;
            assigned++;
        }
        return assigned;
    }

    internal static void ApplyValidatedStairOccurrenceNames(IReadOnlyList<Element> elements,
        Lookup.RevitSystemFamilyNames? names)
    {
        foreach (var element in elements) element.ValidatedStairOccurrenceName = null;
        if (names?.StairOccurrenceName is not { Length: > 0 } name) return;
        var counts = elements.GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.Count());
        var unique = elements.Where(element => counts[element.Id] == 1).ToDictionary(element => element.Id);
        foreach (var element in unique.Values)
        {
            if (element.Id > 0 && !element.IsBlockOnly && element.CategoryId == -2000120 && element.RootClassName == "Stairs")
            {
                element.ValidatedStairOccurrenceName = name;
                continue;
            }
            if (element.Id <= 0 || element.IsBlockOnly || element.CategoryId != -2000120
                || element.SerializedOwnElementClassId != 3681 || element.SerializedStairCore is not { Length: > 0 }
                || !element.SerializedStairTypeLinkIsResolved || element.SerializedStairTypeId is not > 0
                || element.SerializedStairTypeId == element.Id
                || !unique.TryGetValue(element.SerializedStairTypeId.Value, out var type)
                || type.IsBlockOnly || type.CategoryId != -2000120 || type.SerializedStairsTypeClassId != 3742
                || string.IsNullOrWhiteSpace(type.ValidatedSystemFamilyName)) continue;
            element.ValidatedStairOccurrenceName = name;
        }
    }

    internal static void ApplyStairNameParameterScopes(IReadOnlyList<Element> elements, ParsingState state)
    {
        foreach (var element in elements) element.SerializedStairNameParameters = null;
        var counts = elements.GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.Count());
        foreach (var element in elements)
        {
            if (element.Id <= 0 || element.IsBlockOnly || counts[element.Id] != 1
                || element.SerializedOwnElementClassId is not (3681 or 3708 or 3686 or 3727)
                || !state.StairNameParameterRoots.TryGetValue((uint)element.Id, out var root)
                || root is not { BlockCount: 1, Input: { } input } || input.OwnerId != element.Id
                || !input.Values.RootParameterStoresComplete) continue;
            var scope = new List<Parameter>(); bool valid = true;
            var ids = new HashSet<int>();
            var rows = input.Values.Doubles.Select(row => (row.ParamId, StorageType.Double, (object)row.Value))
                .Concat(input.Values.Ints.Select(row => (row.ParamId, StorageType.Integer, (object)row.Value)))
                .Concat(input.Values.Strings.Select(row => (row.ParamId, StorageType.String, (object)row.Value)))
                .Concat(input.Values.ElementIds.Select(row => (row.ParamId, StorageType.ElementId, (object)row.Value)));
            foreach (var (id, storage, value) in rows)
            {
                // Resolve definitions, not a guessed value or a type-propagated parameter.
                string? name = null;
                if (id < 0) Lookup.BipNames.Lookup.TryGetValue(id, out name);
                else if (id > 0 && state.StairNamingDefinitionRoots.TryGetValue((uint)id, out var definition)
                    && definition.BlockCount == 1) name = definition.Name;
                if (!ids.Add(id) || string.IsNullOrEmpty(name)) { valid = false; break; }
                scope.Add(new() { Id = id, StorageType = storage, Value = value, Definition = new() { Name = name } });
            }
            if (valid) element.SerializedStairNameParameters = scope;
        }
    }

    internal static void ApplyValidatedStairComponentTypeNames(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (var element in elements)
        {
            element.ValidatedStairComponentTypeName = null;
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        }
        foreach (var element in elements)
        {
            if (element.Id <= 0 || counts[element.Id] != 1 || element.IsBlockOnly
                || !state.SystemTypeRoots.TryGetValue((uint)element.Id, out var root)
                || root.BlockCount != 1
                || !state.StairComponentTypeNames.TryGetValue((uint)element.Id, out var validation)
                || validation.BlockCount != 1 || root.ClassId != validation.ClassId || string.IsNullOrWhiteSpace(validation.Name)
                || !((root.ClassId == 3718 && element.CategoryId == -2000919)
                    || (root.ClassId == 3697 && element.CategoryId == -2000920)
                    || (root.ClassId == 3736 && element.CategoryId == -2000123)))
                continue;
            element.ValidatedStairComponentTypeName = validation.Name;
        }
    }

    internal static void ApplyValidatedDuctTypeNames(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (var element in elements)
        {
            element.ValidatedDuctTypeName = null;
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        }
        foreach (var element in elements)
        {
            if (element.Id <= 0 || counts[element.Id] != 1 || element.IsBlockOnly
                || element.CategoryId != -2008000
                || !state.SystemTypeRoots.TryGetValue((uint)element.Id, out var root)
                || root is not { BlockCount: 1, ClassId: 110, Discriminator: 124 or 126 or 127 }
                || !state.DuctTypeNames.TryGetValue((uint)element.Id, out var validation)
                || validation.BlockCount != 1 || string.IsNullOrWhiteSpace(validation.Name)) continue;
            element.ValidatedDuctTypeName = validation.Name;
        }
    }

    /// <summary>
    /// Own SymbolInfo names of pipe, flex pipe, pipe insulation and cable tray types. The element
    /// must be one unique materialized type whose single exact root has the class of the name
    /// block.
    /// </summary>
    internal static void ApplyValidatedMepTypeNames(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (var element in elements)
        {
            element.ValidatedMepTypeName = null;
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        }
        foreach (var element in elements)
        {
            if (element.Id <= 0 || counts[element.Id] != 1 || element.IsBlockOnly
                || !state.SystemTypeRoots.TryGetValue((uint)element.Id, out var root)
                || root.BlockCount != 1 || root.ClassId == 0
                || !state.MepTypeNames.TryGetValue((uint)element.Id, out var validation)
                || validation.BlockCount != 1 || validation.ClassId != root.ClassId || string.IsNullOrWhiteSpace(validation.Name)) continue;
            element.ValidatedMepTypeName = validation.Name;
        }
    }

    /// <summary>
    /// Type of a pipe, flex pipe, pipe insulation, duct or cable tray occurrence from its own
    /// RbsCurve.m_idType.
    /// </summary>
    internal static void ApplyMepCurveTypeLinks(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (var element in elements)
        {
            element.SerializedMepCurveTypeId = null;
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        }
        foreach (var element in elements)
        {
            if (element.Id <= 0 || counts[element.Id] != 1 || element.IsBlockOnly
                || !state.SystemOccurrenceTypeIds.TryGetValue((uint)element.Id, out var occurrence)
                || occurrence.BlockCount != 1 || occurrence.TypeId is not > 0 || occurrence.TypeId == element.Id
                || !MepCurveTypeClass(occurrence.ClassId, out ushort typeClass)
                || counts.GetValueOrDefault(occurrence.TypeId.Value) != 1
                || !state.SystemTypeRoots.TryGetValue((uint)occurrence.TypeId.Value, out var root)
                || root.BlockCount != 1 || root.ClassId != typeClass) continue;
            element.SerializedMepCurveTypeId = occurrence.TypeId;
        }
    }

    /// <summary>The exact root class of the type of an MEP curve occurrence class.</summary>
    private static bool MepCurveTypeClass(ushort occurrenceClass, out ushort typeClass)
    {
        typeClass = occurrenceClass switch
        {
            3210 => 3227, // RbsPipeCurve -> RbsPipeType
            3180 => 3181, // RbsFlexPipeCurve -> RbsFlexPipeType
            3216 => 3217, // RbsPipeInsulation -> RbsPipeInsulationType
            598 => 3130, // CableTray -> RbsCableTrayType
            3148 => 110, // RbsDuctCurve -> AbsDuctType
            3134 => 3135, // RbsConduitCurve -> RbsConduitType
            _ => (ushort)0,
        };
        return typeClass != 0;
    }

    internal static void ApplyValidatedCorniceTypeNames(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (var element in elements)
        {
            element.ValidatedCorniceTypeName = null;
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        }
        foreach (var element in elements)
        {
            if (element.Id <= 0 || counts[element.Id] != 1 || element.IsBlockOnly
                || element.CategoryId != -2000181
                || !state.SystemTypeRoots.TryGetValue((uint)element.Id, out var root)
                || root is not { BlockCount: 1, ClassId: 873 }
                || !state.CorniceTypeNames.TryGetValue((uint)element.Id, out var validation)
                || validation.BlockCount != 1 || string.IsNullOrWhiteSpace(validation.Name)) continue;
            element.ValidatedCorniceTypeName = validation.Name;
        }
    }

    /// <summary>
    /// Preserve exact system-type identity separately from public type guesses. Both occurrence and
    /// type must have unique, compatible own roots.
    /// </summary>
    internal static void ApplySerializedSystemTypeIdentity(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (var element in elements)
        {
            element.SerializedSystemTypeId = null;
            element.SerializedSystemTypeClassId = null;
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        }
        var needed = state.SystemOccurrenceTypeIds.Values.Where(value => value.TypeId is > 0)
            .Select(value => value.TypeId!.Value).ToHashSet();
        var types = elements.Where(element => needed.Contains(element.Id) && counts[element.Id] == 1)
            .ToDictionary(element => element.Id);
        foreach (var element in elements)
        {
            if (element.Id <= 0 || counts[element.Id] != 1 || element.IsBlockOnly
                || !state.SystemOccurrenceTypeIds.TryGetValue((uint)element.Id, out var occurrence)
                || occurrence.BlockCount != 1 || occurrence.TypeId is not > 0
                || !types.TryGetValue(occurrence.TypeId.Value, out var type) || type.IsBlockOnly
                || type.Id == element.Id
                // a Floor occurrence is filed under OST_Floors even when its foundation slab type
                // is not
                || type.CategoryId != element.CategoryId
                    && !(type.CategoryId is -2001300 && element.CategoryId is -2000032)
                || !state.SystemTypeRoots.TryGetValue((uint)type.Id, out var root) || root.BlockCount != 1)
                continue;
            bool agrees = (occurrence.ClassId, root.ClassId, element.CategoryId) switch
            {
                (3524 or 361, 512 or 2559, -2000011) => true,
                (1913, 1915, -2000032) when (root.Discriminator, type.CategoryId) is (0, -2000032) or (1, -2001300) => true,
                (1913, 1915, -2001300) when root.Discriminator == 1 => true,
                (642, 646, -2000038) => true,
                (3451 or 3054 or 1604, 3452, -2000035) => true, // Roof, ProfileRoof, FaceRoof of a RoofAttributes
                (565, 571, -2001263) => true, // BuildingPad of a BuildingPadType
                _ => false,
            };
            if (!agrees) continue;
            element.SerializedSystemTypeId = type.Id;
            type.SerializedSystemTypeClassId = root.ClassId;
        }
    }

    /// <summary>
    /// Resolve the own computed symbol independently of the display type. Ambiguous
    /// roots/materialization and non-FamilySymbol targets stay unvalidated. Generated block-only
    /// symbols are valid targets, just as in the original API.
    /// </summary>
    internal static void ApplyValidatedOriginalFamilySymbols(IReadOnlyList<Element> elements, ParsingState state)
    {
        var counts = new Dictionary<int, int>();
        foreach (var element in elements)
        {
            element.ValidatedOriginalFamilySymbolId = null;
            counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        }
        var needed = state.ComputedFamilySymbols.Values.Where(value => value.Symbol != null)
            .Select(value => value.Symbol!.SymbolId).ToHashSet();
        var symbols = elements.Where(element => needed.Contains(element.Id) && counts[element.Id] == 1)
            .ToDictionary(element => element.Id);
        foreach (var element in elements)
        {
            element.SerializedFlippedSymbol = element.Id > 0 && counts[element.Id] == 1
                && state.ComputedFamilySymbols.TryGetValue((uint)element.Id, out var root)
                && root is { BlockCount: 1, Symbol: { FlippedX: bool flippedX, FlippedY: bool flippedY } }
                    ? flippedX != flippedY : null;
            if (element.Id <= 0 || counts[element.Id] != 1
                || !state.ComputedFamilySymbols.TryGetValue((uint)element.Id, out var own)
                || own.BlockCount != 1 || own.Symbol is not { SymbolId: > 0 } source
                || source.SymbolId == element.Id || !symbols.ContainsKey(source.SymbolId)
                || !state.ComputedSymbolTargetClasses.TryGetValue((uint)source.SymbolId, out var target)
                || target is not { BlockCount: 1, ClassId: 1829 })
                continue;
            element.ValidatedOriginalFamilySymbolId = source.SymbolId;
        }
    }

    /// <summary>
    /// Ceiling.getTypeId_ reads its own m_ceilingAttributesId. Replace the earlier
    /// voted type only after both unique compatible roots have established that link.
    /// </summary>
    internal static void ApplyValidatedCeilingCanonicalTypes(IReadOnlyList<Element> elements, ParsingState state)
    {
        var uniqueElements = BuildUniqueElementIndexForNativeCeilingLinks(elements);
        foreach (var element in elements)
        {
            if (element.CategoryId != -2000038 || element.IsBlockOnly
                || element.SerializedSystemTypeId is not > 0
                || !uniqueElements.TryGetValue(element.Id, out var own) || !ReferenceEquals(own, element)
                || !state.SystemOccurrenceTypeIds.TryGetValue((uint)element.Id, out var occurrence)
                || occurrence is not { BlockCount: 1, ClassId: 642 }
                || occurrence.TypeId != element.SerializedSystemTypeId
                || !uniqueElements.TryGetValue(element.SerializedSystemTypeId.Value, out var type)
                || type.Id == element.Id || type.IsBlockOnly || type.CategoryId != -2000038
                || type.SerializedSystemTypeClassId != 646
                || !state.SystemTypeRoots.TryGetValue((uint)type.Id, out var root)
                || root is not { BlockCount: 1, ClassId: 646 })
                continue;

            element.TypeId = type.Id;
            foreach (var (id, name) in new[] { (-1002050, "ELEM_TYPE_PARAM"),
                         (-1002052, "ELEM_FAMILY_AND_TYPE_PARAM"), (-1002000, "SYMBOL_ID_PARAM") })
            {
                bool found = false;
                foreach (var parameter in element.Parameters)
                {
                    if (parameter.Id != id) continue;
                    parameter.StorageType = StorageType.ElementId;
                    parameter.Value = type.Id;
                    found = true;
                }
                if (!found)
                    element.Parameters.Add(BipHelpers.MakeBipParam(id, name, StorageType.ElementId, type.Id));
            }
        }
    }

    internal static void ApplyValidatedRoofOrRailingTypeReferenceNames(IReadOnlyList<Element> elements, ParsingState state)
    {
        foreach (var element in elements)
        {
            element.ValidatedRoofOrRailingTypeReferenceName = null;
            element.ValidatedRoofOrRailingTypeReferenceIsExplicit = false;
            element.ValidatedRoofOrRailingTypeReferenceIsBlocked = false;
        }
        var candidates = state.RoofOrRailingTypeIds.Where(pair => pair.Key > 0
            && pair.Value is { BlockCount: 1, TypeId: > 0 }).ToList();
        var needed = candidates.Select(pair => (int)pair.Key)
            .Concat(candidates.Select(pair => pair.Value.TypeId!.Value)).ToHashSet();
        var byId = elements.Where(element => needed.Contains(element.Id))
            .GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.ToList());
        foreach (var (owner, validation) in candidates)
        {
            int typeId = validation.TypeId!.Value;
            bool railing = validation.ClassId == 485;
            int category = railing ? -2000126 : -2000035;
            if (validation.ClassId is not (485 or 3451 or 3054)
                || !byId.TryGetValue((int)owner, out var instances) || instances.Count != 1
                || !byId.TryGetValue(typeId, out var types) || types.Count != 1
                || owner == (uint)typeId || instances[0].CategoryId != category || types[0].CategoryId != category
                || !state.RoofOrRailingTypeNames.TryGetValue((uint)typeId, out var typeValidation)
                || typeValidation.BlockCount != 1
                || (railing ? typeValidation.ClassId != 3705 : typeValidation.ClassId is not (908 or 3452))
                || string.IsNullOrWhiteSpace(typeValidation.Name)) continue;
            var instance = instances[0];
            var explicitReference = ExactIfcStringParameterResolver.ResolveReference(instance, types[0]);
            instance.ValidatedRoofOrRailingTypeReferenceIsBlocked = explicitReference.State == ExactIfcStringParameterState.Blocked;
            instance.ValidatedRoofOrRailingTypeReferenceIsExplicit = explicitReference.State == ExactIfcStringParameterState.Value;
            instance.ValidatedRoofOrRailingTypeReferenceName = explicitReference.State switch
            {
                ExactIfcStringParameterState.Value => explicitReference.Value,
                ExactIfcStringParameterState.Blocked => null,
                _ => typeValidation.Name,
            };
        }
        ApplyValidatedRailingFamilySymbolReferenceNames(elements, state);
    }

    /// <summary>Railing FamilyInstances (in-place or loadable) carry no StairsRailingAttr.</summary>
    private static void ApplyValidatedRailingFamilySymbolReferenceNames(
        IReadOnlyList<Element> elements, ParsingState state)
    {
        var candidates = elements.Where(element => element.CategoryId == -2000126 && element.Id > 0
            && !state.RoofOrRailingTypeIds.ContainsKey((uint)element.Id)
            && state.FamilyInstanceSymbolIds.TryGetValue((uint)element.Id, out var validation)
            && validation is { BlockCount: 1, TypeId: > 0 }).ToList();
        var needed = candidates.Select(element => element.Id)
            .Concat(candidates.Select(element => state.FamilyInstanceSymbolIds[(uint)element.Id].TypeId!.Value))
            .ToHashSet();
        var byId = elements.Where(element => needed.Contains(element.Id))
            .GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.ToList());
        foreach (var instance in candidates)
        {
            int typeId = state.FamilyInstanceSymbolIds[(uint)instance.Id].TypeId!.Value;
            if (byId[instance.Id].Count != 1 || typeId == instance.Id
                || !byId.TryGetValue(typeId, out var types) || types.Count != 1
                || types[0].CategoryId != instance.CategoryId
                || !state.FamilySymbolNames.TryGetValue((uint)typeId, out var typeValidation)
                || typeValidation.BlockCount != 1 || string.IsNullOrWhiteSpace(typeValidation.Name)) continue;
            var explicitReference = ExactIfcStringParameterResolver.ResolveReference(instance, types[0]);
            instance.ValidatedRoofOrRailingTypeReferenceIsBlocked = explicitReference.State == ExactIfcStringParameterState.Blocked;
            instance.ValidatedRoofOrRailingTypeReferenceIsExplicit = explicitReference.State == ExactIfcStringParameterState.Value;
            instance.ValidatedRoofOrRailingTypeReferenceName = explicitReference.State switch
            {
                ExactIfcStringParameterState.Value => explicitReference.Value,
                ExactIfcStringParameterState.Blocked => null,
                _ => typeValidation.Name,
            };
        }
    }

    /// <summary>
    /// Re-derives <see cref="Element.FamilyName"/> via the SAME name-table chain.
    /// </summary>
    internal static void ApplyValidatedFamilyNamesFromCanonicalTypeId(
        IReadOnlyList<Element> elements, ParsingState state)
    {
        foreach (Element element in elements)
        {
            if (element.TypeId <= 0) continue;
            if (TryResolveFamilyNameViaTable((uint)element.TypeId, state, out var famName)
                && !string.IsNullOrWhiteSpace(famName))
                element.FamilyName = famName;
        }
    }

    /// <summary>
    /// Keep the type object's own display name separate from occurrence-name propagation. A type
    /// may itself have a TypeId pointing at a nested family symbol; that symbol cannot supply the
    /// outer type's name.
    /// </summary>
    internal static void ApplyValidatedIfcTypeObjectNames(IReadOnlyList<Element> elements, ParsingState state)
    {
        foreach (Element element in elements)
        {
            element.ValidatedIfcTypeObjectName = null;
            element.ValidatedIfcTypeOwnName = null;
            // a file with 64-bit ids has no name table: a family symbol is named by its own
            // SymbolInfo and the FamilyBase of its m_familyId, the two names the schema-driven walk
            // reads (ParsingState.WideNames)
            if (state.WideSchema != null)
            {
                if (element.NativeIsSymbol == true && element.Id > 0 && state.WideNames.TryGetValue((uint)element.Id, out string? symbolName))
                {
                    // a system type (wall, floor...) has no family: the occurrence is named by the
                    // type alone
                    element.ValidatedIfcTypeOwnName = symbolName;
                    if (state.NativeParams.TryGetValue((uint)element.Id, out var symbolParams)
                        && symbolParams.Fields.TryGetValue("1829:m_familyId", out int symbolFamily) && symbolFamily > 0
                        && state.WideNames.TryGetValue((uint)symbolFamily, out string? symbolFamilyName))
                        element.ValidatedIfcTypeObjectName = $"{symbolFamilyName}:{symbolName}";
                }
                continue;
            }
            if (element.Id <= 0
                || !state.ElementNameTable.TryGetValue((uint)element.Id, out string? ownName)
                || string.IsNullOrWhiteSpace(ownName)
                || !state.NameEntryParentHolder.TryGetValue((uint)element.Id, out uint holder)
                || !state.NameHolderTarget.TryGetValue(holder, out uint familyId)
                || familyId == (uint)element.Id
                || !state.ElementNameTable.TryGetValue(familyId, out string? familyName)
                || string.IsNullOrWhiteSpace(familyName))
                continue;
            element.ValidatedIfcTypeObjectName = $"{familyName}:{ownName}";
            element.ValidatedIfcTypeOwnName = ownName;
        }
    }

    internal static void ApplyBasicWallTypeFunctions(
        IEnumerable<Element> elements, IReadOnlyDictionary<uint, (int BlockCount, int? Function)> source)
    {
        foreach (var element in elements)
        {
            element.SerializedBasicWallTypeFunction = null;
            if (element.Id > 0 && element.CategoryId == -2000011
                && source.TryGetValue((uint)element.Id, out var own)
                && own is { BlockCount: 1, Function: >= 0 and <= 5 })
                element.SerializedBasicWallTypeFunction = own.Function;
        }
    }

    internal static void ApplyCurtainTypeNames(IEnumerable<Element> elements, ParsingState state)
    {
        foreach (var element in elements)
        {
            element.SerializedCurtainWallTypeName = null;
            if (element.Id > 0 && element.CategoryId == -2000011
                && state.NewCurtainWallTypeFunctions.TryGetValue((uint)element.Id, out var root)
                && root is { BlockCount: 1, Function: >= 0 and <= 5 }
                && state.NewCurtainWallTypeNames.TryGetValue((uint)element.Id, out string? name)
                && !string.IsNullOrWhiteSpace(name))
                element.SerializedCurtainWallTypeName = name;
        }
    }

    internal static void ApplyBasicWallTypeNames(IEnumerable<Element> elements, ParsingState state)
    {
        foreach (var element in elements)
        {
            element.SerializedBasicWallTypeName = null;
            if (element.Id > 0 && element.CategoryId == -2000011
                && state.BasicWallTypeFunctions.TryGetValue((uint)element.Id, out var root)
                && root is { BlockCount: 1, Function: >= 0 and <= 5 }
                && state.BasicWallTypeNames.TryGetValue((uint)element.Id, out string? name)
                && !string.IsNullOrWhiteSpace(name))
                element.SerializedBasicWallTypeName = name;
        }
    }

    /// <summary>
    /// The panel order stored in a wall's own curtain grid, from a unique exactly decoded wall
    /// root.
    /// </summary>
    internal static void ApplyCurtainPanelOrders(IEnumerable<Element> elements, ParsingState state)
    {
        foreach (var element in elements)
        {
            element.SerializedCurtainPanelOrder = null;
            if (element.Id > 0 && element.CategoryId == -2000011
                && state.CurtainPanelOrders.TryGetValue((uint)element.Id, out var order)
                && order is { BlockCount: 1, PanelIds: { } ids })
                element.SerializedCurtainPanelOrder = ids;
        }
    }

    internal static void ApplyCurtainFamilyHosts(IEnumerable<Element> elements, ParsingState state)
    {
        var all = elements.ToList();
        var replacementSource = state.CurtainFamilyHostSource
            .Where(pair => pair.Value.BlockCount == 1
                && pair.Value.Host is { ReferenceClass: 897, ReplacementElementId: > 0 }).ToList();
        var neededIds = replacementSource.Select(pair => pair.Key)
            .Concat(replacementSource.Select(pair => (uint)pair.Value.Host!.ReplacementElementId!.Value)).ToHashSet();
        var byId = all.Where(element => element.Id > 0 && neededIds.Contains((uint)element.Id))
            .GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.ToList());
        foreach (var element in all)
        {
            element.SerializedCurtainHostId = null;
            if (element.Id <= 0
                || !state.CurtainFamilyHostSource.TryGetValue((uint)element.Id, out var source)
                || source.BlockCount != 1 || source.Host is not { HostId: > 0 } host
                || host.HostId == element.Id || host.ReplacementElementId != null)
                continue;
            // A curtain cell can be filled by a panel, door or window family. A generic graph or
            // propagated InsertableInst host cannot establish this membership.
            if (element.CategoryId is -2000170 or -2000014 or -2000023 && host.ReferenceClass == 897
                || element.CategoryId == -2000171 && host.ReferenceClass == 2518)
                element.SerializedCurtainHostId = host.HostId;
        }

        // Visible wall panels are replacements of a native placeholder panel. Do not export the
        // placeholder as its own child or guess through generic References.
        var replacements = replacementSource.GroupBy(pair => pair.Value.Host!.ReplacementElementId!.Value);
        foreach (var group in replacements)
        {
            if (group.Count() != 1 || !byId.TryGetValue(group.Key, out var targets)
                || targets.Count != 1 || targets[0].CategoryId != -2000011 || targets[0].IsBlockOnly)
                continue;
            var source = group.Single();
            if (source.Key > int.MaxValue || !byId.TryGetValue((int)source.Key, out var placeholders)
                || placeholders.Count != 1 || placeholders[0].CategoryId != -2000170 || placeholders[0].IsBlockOnly
                || source.Value.Host!.HostId <= 0 || source.Value.Host.HostId == targets[0].Id
                || source.Value.Host.HostId == placeholders[0].Id)
                continue;
            targets[0].SerializedCurtainHostId = source.Value.Host.HostId;
        }
    }

    internal static void ApplyStackedWallTypes(IEnumerable<Element> elements, ParsingState state)
    {
        foreach (var element in elements)
            element.IsSerializedStackedWallType = element.Id > 0 && element.CategoryId == -2003500
                && state.StackedWallTypeOwners.Contains((uint)element.Id)
                && state.BasicWallTypeFunctions.TryGetValue((uint)element.Id, out var roots)
                && roots.BlockCount == 1;
    }

    /// <summary>
    /// Repairs only the invalid integer -1 inherited from the legacy body-entry fallback when the
    /// unique own BasicWallType root confirms the actual function.
    /// </summary>
    internal static void RepairBasicWallTypeFunctionBodyFallbacks(
        IEnumerable<Element> elements, ParsingState state)
    {
        const int functionParamId = -1001006;
        foreach (var element in elements)
        {
            if (element.Id <= 0 || element.CategoryId != -2000011
                || element.SerializedBasicWallTypeFunction is not (>= 0 and <= 5)
                || !state.BasicWallTypeFunctions.TryGetValue((uint)element.Id, out var validation)
                || validation.BlockCount != 1 || validation.Function != element.SerializedBasicWallTypeFunction
                || !state.Records.TryGetValue((uint)element.Id, out var record)
                || !state.NativeParams.TryGetValue((uint)element.Id, out var native)
                || !native.RootParameterStoresComplete
                || !native.Fields.TryGetValue("m_id", out int owner) || owner != element.Id
                || !native.Fields.TryGetValue("m_function", out int function) || function != validation.Function
                || native.Ints.Any(p => p.ParamId == functionParamId)
                || native.Doubles.Any(p => p.ParamId == functionParamId)
                || native.Strings.Any(p => p.ParamId == functionParamId)
                || native.ElementIds.Any(p => p.ParamId == functionParamId))
                continue;
            var bodyEntries = record.BipParams.Where(p => p.BipId == functionParamId).ToList();
            var parameters = element.Parameters.Where(p => p.Id == functionParamId).ToList();
            if (bodyEntries.Count != 1 || bodyEntries[0] is not { FieldType: 4, Value: -1 }
                || parameters.Count != 1 || parameters[0].IsApproximate
                || parameters[0].StorageType != StorageType.Integer
                || parameters[0].Value is not int value || value != -1)
                continue;
            parameters[0].Value = function;
        }
    }

    public static void PostProcess(RevitFileInfo rvtInfo, ParsingState? state = null, TextWriter? diag = null)
    {
        ApplyGridDatums(rvtInfo, state);
        rvtInfo.GraphicsStyles = state?.GraphicsStyles
            .Where(pair => pair.Value is { BlockCount: 1, MaterialId: not null })
            .ToDictionary(pair => (int)pair.Key, pair => (pair.Value.MaterialId!.Value, pair.Value.CategoryId));
        rvtInfo.CategoryNames = state?.CategoryNames
            .Where(pair => pair.Value is { BlockCount: 1, Name: not null })
            .ToDictionary(pair => (int)pair.Key, pair => pair.Value.Name!);

        var idToName = new Dictionary<int, string>();
        var idToElem = new Dictionary<int, Element>();
        foreach (var e in rvtInfo.Elements)
        {
            if (!string.IsNullOrEmpty(e.Name))
                idToName[e.Id] = e.Name;
            idToElem[e.Id] = e;
        }

        // Inherit category from type element or references Phase 1: TypeId and ELEM_TYPE_PARAM
        // (safe, direct type->instance)
        var idToCategory = new Dictionary<int, (int catId, string? catName)>();
        foreach (var e in rvtInfo.Elements)
            if (e.CategoryId.HasValue)
                idToCategory[e.Id] = (e.CategoryId.Value, e.Category);

        foreach (var e in rvtInfo.Elements)
        {
            if (e.CategoryId.HasValue) continue;

            // Previously resolved type element id, never an ElemRec history value.
            if (e.TypeId > 0 && idToCategory.TryGetValue(e.TypeId, out var typeCat))
            {
                e.CategoryId = typeCat.catId;
                e.Category = typeCat.catName;
                idToCategory[e.Id] = typeCat;
                continue;
            }

            // ELEM_TYPE_PARAM from record body
            var typeParam = e.Parameters.FirstOrDefault(p => p.Id == -1002050);
            if (typeParam?.Value is int tpId && idToCategory.TryGetValue(tpId, out var tpCat))
            {
                e.CategoryId = tpCat.catId;
                e.Category = tpCat.catName;
                idToCategory[e.Id] = tpCat;
            }
        }

        // Phase 2: Reference-based (multi-pass transitive propagation) Exclude infrastructure /
        // shared categories that should not propagate via references: OST_Levels (-2000240): levels
        // are referenced by everything OST_Materials (-2000093).
        var NoPropagateCategories = new HashSet<int>
        {
            -2000240, -2000093, -2000340, -2000112, -2000279, -2000269, -2003101, -2000310,
        };
        for (int pass = 0; pass < 5; pass++)
        {
            int added = 0;
            foreach (var e in rvtInfo.Elements)
            {
                if (e.CategoryId.HasValue || e.References == null) continue;
                foreach (var refId in e.References)
                {
                    if (refId == e.Id) continue;
                    if (idToCategory.TryGetValue(refId, out var refCat)
                        && !NoPropagateCategories.Contains(refCat.catId))
                    {
                        e.CategoryId = refCat.catId;
                        e.Category = refCat.catName;
                        idToCategory[e.Id] = refCat;
                        added++;
                        break;
                    }
                }
            }
            if (added == 0) break;
        }

        // Level name propagation from associated views. A view referencing exactly one level is the
        // floor plan for that level; the view's name IS the level name.
        {
            var levelIds = new HashSet<int>();
            foreach (var e in rvtInfo.Elements)
                if (e.Category == "OST_Levels") levelIds.Add(e.Id);

            var levelNameFromView = new Dictionary<int, (int viewId, string name)>();
            foreach (var e in rvtInfo.Elements)
            {
                if (e.Category != "OST_Views" || string.IsNullOrEmpty(e.Name) || e.References == null) continue;
                var levelRefs = new List<int>();
                foreach (var r in e.References)
                    if (levelIds.Contains(r)) levelRefs.Add(r);
                if (levelRefs.Count != 1) continue;
                int lid = levelRefs[0];
                // Prefer the view with the closest ID to the level (auto-created plan view)
                if (!levelNameFromView.TryGetValue(lid, out var existing)
                    || Math.Abs(e.Id - lid) < Math.Abs(existing.viewId - lid))
                {
                    levelNameFromView[lid] = (e.Id, e.Name);
                }
            }

            foreach (var e in rvtInfo.Elements)
            {
                if (e.Category != "OST_Levels") continue;
                if (levelNameFromView.TryGetValue(e.Id, out var viewInfo))
                {
                    // View name is the authoritative source for level names; always use it
                    // (overrides scan-string garbage like 'SunAndSky-002')
                    e.Name = viewInfo.name;
                    idToName[e.Id] = viewInfo.name;
                }
            }
        }

        // Wall type name composition: for wall types without names (no 0xFFFF block), compose from
        // function enum + material reference + width, e.g. "<Function> - <Material> <Width>mm" (localized).
        if (state?.WallTypeFunctions.Count > 0)
        {
            // Russian wall function names as they appear in wall type names of exported documents.
            string[] wallFunctionNames = ["Внутри", "Снаружи", "Фундамент", "С подпором", "Софит", "Сердцевина-шахта"];
            foreach (var e in rvtInfo.Elements)
            {
                if (!string.IsNullOrEmpty(e.Name)) continue;
                if (e.Category != "OST_Walls") continue;
                if (!state.WallTypeFunctions.TryGetValue((uint)e.Id, out int funcEnum)) continue;

                string funcName = funcEnum >= 0 && funcEnum < wallFunctionNames.Length
                    ? wallFunctionNames[funcEnum] : $"Function{funcEnum}";

                // Material name: first record body reference that has a name and isn't self/preview
                string? materialName = null;
                if (e.References != null)
                {
                    foreach (var refId in e.References)
                    {
                        if (refId == e.Id) continue;
                        if (idToName.TryGetValue(refId, out var rn) && !string.IsNullOrEmpty(rn))
                        {
                            var refElem = rvtInfo.Elements.FirstOrDefault(x => x.Id == refId);
                            if (refElem?.Category is "OST_PreviewLegendComponents" or "OST_Materials") continue;
                            materialName = rn;
                            break;
                        }
                    }
                }

                // Width from WALL_ATTR_WIDTH_PARAM (in feet -> mm)
                var widthParam = e.Parameters.FirstOrDefault(p => p.Id == -1001000);
                double widthFt = widthParam?.Value is double dw ? dw : 0;
                int widthMm = (int)Math.Round(widthFt * 304.8);

                if (materialName != null && widthMm > 0)
                {
                    e.Name = $"{funcName} - {materialName} {widthMm}мм";
                    idToName[e.Id] = e.Name;
                }
            }
        }

        // Attach CompoundStructure layers to wall type elements
        if (state?.CompoundLayers.Count > 0)
        {
            foreach (var (eid, layers) in state.CompoundLayers)
            {
                var elem = rvtInfo.Elements.FirstOrDefault(e => e.Id == (int)eid);
                if (elem == null) continue;
                elem.Layers = layers.Select(l => new Model.MaterialLayer
                {
                    ThicknessMm = Math.Round(l.ThicknessFt * 304.8, 2),
                    Function = CompoundLayer.FunctionName(l.Function),
                    MaterialId = l.MaterialId,
                    MaterialName = l.MaterialId > 0 && idToName.TryGetValue(l.MaterialId, out var mn) ? mn : null,
                    LayerIndex = l.LayerIndex
                }).ToList();
            }
            // Propagate layers from type elements to host instances (walls, floors, ceilings)
            var layersByElemId = new Dictionary<int, List<Model.MaterialLayer>>();
            foreach (var e in rvtInfo.Elements)
                if (e.Layers != null && e.Layers.Count > 0)
                    layersByElemId[e.Id] = e.Layers;

            var layerHostCats = new HashSet<string> { "OST_Walls", "OST_Floors", "OST_Ceilings", "OST_Roofs" };
            foreach (var e in rvtInfo.Elements)
            {
                if (e.Layers != null && e.Layers.Count > 0) continue;
                if (e.Category != null && !layerHostCats.Contains(e.Category)) continue;
                var tp = e.Parameters.FirstOrDefault(p => p.Id == -1002050);
                if (tp?.Value is int typeId && layersByElemId.TryGetValue(typeId, out var typeLayers))
                { e.Layers = typeLayers; continue; }
                // An element whose own record names its type takes the layers of that type only: its References also
                // hold the types of other walls (a curtain wall refers to the basic wall types of its panels).
                if (state.SystemOccurrenceTypeIds.TryGetValue((uint)e.Id, out var ownType) && ownType is { BlockCount: 1, TypeId: > 0 })
                {
                    if (layersByElemId.TryGetValue(ownType.TypeId.Value, out var ownLayers)) e.Layers = ownLayers;
                    continue;
                }
                // Try References (for walls without ELEM_TYPE_PARAM)
                if (e.References == null) continue;
                foreach (var refId in e.References)
                {
                    if (layersByElemId.TryGetValue(refId, out var refLayers))
                    { e.Layers = refLayers; break; }
                }
            }
        }

        ComputeWallSolids(rvtInfo.Elements);

        // Resolve TypeId independently: ElemTable contains no type element id. Resolution priority:
        // 1. ELEM_TYPE_PARAM value if it references a real element of the same category 2.
        // SYMBOL_ID_PARAM value (same check) 3.
        var allElemIds = new HashSet<int>(rvtInfo.Elements.Select(e => e.Id));
        var elemByIdLookup = rvtInfo.Elements.ToDictionary(e => e.Id);
        foreach (var e in rvtInfo.Elements)
        {
            // Trust current TypeId only if it resolves to an element of the SAME category.
            // Otherwise it's likely a Tf-class index or a leaked phase/sentinel value.
            bool currentValid = e.TypeId > 0 && elemByIdLookup.TryGetValue(e.TypeId, out var curType)
                && (curType.CategoryId == e.CategoryId || !e.CategoryId.HasValue);
            if (currentValid) continue;

            int? resolved = null;
            foreach (int paramId in new[] { -1002050, -1002000, -1002052 })
            {
                var p = e.Parameters.FirstOrDefault(pp => pp.Id == paramId);
                if (p?.Value is int v && v > 0 && elemByIdLookup.TryGetValue(v, out var paramTarget)
                    && (paramTarget.CategoryId == e.CategoryId || !e.CategoryId.HasValue))
                { resolved = v; break; }
            }
            // Fallback: pick a same-category element from References
            if (resolved == null && e.References != null && e.CategoryId.HasValue)
            {
                foreach (var refId in e.References)
                {
                    if (refId == e.Id) continue;
                    if (!elemByIdLookup.TryGetValue(refId, out var refElem)) continue;
                    if (refElem.CategoryId == e.CategoryId)
                    {
                        // Prefer the lowest-ID candidate (types are typically created before
                        // instances)
                        if (resolved == null || refId < resolved.Value) resolved = refId;
                    }
                }
            }
            if (resolved.HasValue) e.TypeId = resolved.Value;
        }
        // Also overwrite ELEM_TYPE_PARAM / SYMBOL_ID_PARAM / ELEM_FAMILY_AND_TYPE_PARAM values when
        // the resolved TypeId differs from the stored garbage (e.g. 528 Tf-class index)
        foreach (var e in rvtInfo.Elements)
        {
            if (e.TypeId <= 0 || !allElemIds.Contains(e.TypeId)) continue;
            foreach (int paramId in new[] { -1002050, -1002000, -1002052 })
            {
                var p = e.Parameters.FirstOrDefault(pp => pp.Id == paramId);
                if (p == null) continue;
                if (p.Value is int v && v > 0 && allElemIds.Contains(v)) continue; // already valid
                p.Value = e.TypeId;
            }
        }
        // Rebuild idToName after TypeId fix
        idToName.Clear();
        foreach (var e in rvtInfo.Elements)
        {
            if (!string.IsNullOrEmpty(e.Name))
                idToName[e.Id] = e.Name;
        }

        // Type name propagation: instances get their Name from their type element
        foreach (var e in rvtInfo.Elements)
        {
            // Materials are document definitions, not family instances. Their TypeId is an internal
            // material/asset link and must not replace the authored name.
            if (e.CategoryId is -2000700 or -2000220) continue;
            if (e.TypeId > 0 && idToName.TryGetValue(e.TypeId, out var typeName))
            {
                // Instances always use type name; only keep existing name if no type available
                e.Name = typeName;
            }
            else if (string.IsNullOrEmpty(e.Name))
            {
                var typeParam = e.Parameters.FirstOrDefault(p => p.Id == -1002050);
                if (typeParam?.Value is int tpId && idToName.TryGetValue(tpId, out var tpName))
                    e.Name = tpName;
            }
        }

        // Compatibility fallback for versions where InsertableInst.m_hostId was not decoded.
        var elemById = rvtInfo.Elements.ToDictionary(e => e.Id);
        var hostCategories = new HashSet<string> { "OST_Walls", "OST_Floors", "OST_Ceilings", "OST_Roofs" };
        var insertCategories = new HashSet<string> { "OST_Doors", "OST_Windows", "OST_MechanicalEquipment",
            "OST_GenericModel", "OST_StructuralColumns", "OST_CurtainWallPanels" };
        foreach (var e in rvtInfo.Elements)
        {
            if (e.SerializedHostId.HasValue) continue;
            if (e.Category == null || !insertCategories.Contains(e.Category)) continue;
            if (e.References == null) continue;
            foreach (var refId in e.References)
            {
                if (elemById.TryGetValue(refId, out var refElem) && refElem.Category != null
                    && hostCategories.Contains(refElem.Category))
                {
                    e.HostId = refId;
                    break;
                }
            }
        }

        // Fix computed WALL_USER_HEIGHT_PARAM: stored value in 0xFFFF blocks is the type default
        // which often equals wall width.
        foreach (var e in rvtInfo.Elements)
        {
            if (e.Category != "OST_Walls" || GeometryBox(e) is not { Length: 6 }) continue;
            var hp = e.Parameters.FirstOrDefault(p => p.Id == -1001105); // WALL_USER_HEIGHT_PARAM
            var wp = e.Parameters.FirstOrDefault(p => p.Id == -1001000); // WALL_ATTR_WIDTH_PARAM
            if (hp?.Value is double storedH && wp?.Value is double storedW && storedW > 0)
            {
                double dz = GeometryBox(e)![5] - GeometryBox(e)![2];
                // Only fix if stored height equals stored width (type default leak) AND dz differs
                if (Math.Abs(storedH - storedW) < 0.001 && dz > 0.01 && Math.Abs(dz - storedH) > 0.01)
                    hp.Value = dz;
            }
        }

        // The name tables below are read unconditionally; an absent state is an empty one.
        state ??= new ParsingState();

        // FamilyName resolution. Primary: name-table chain - symbol's name entry carries the holder
        // id of the parent FAMILY's name entry: symbol -> NameEntryParentHolder -> NameHolderTarget
        // -> family id -> ElementNameTable[family].
        int viaTableSymbol = 0, viaTableSelf = 0, viaFamilyParam = 0, viaTypeElement = 0, unresolved = 0;
        int noNameEntry = 0, noParentHolder = 0, noHolderTarget = 0, noTableName = 0;
        // This accumulates the distinct symbolId -> element-count mapping for the noNameEntry
        // branch only.
        var missingSymbolElemCounts = new Dictionary<uint, int>();
        // Same accumulation, restricted to symbolIds backed by a genuine ELEM_TYPE_PARAM
        // (hasTypeParam true below).
        var missingTypedSymbolElemCounts = new Dictionary<uint, int>();
        // TryResolveFamilyNameViaTable is a pure function of (symbolId, state) with no mutation in
        // this loop, so the same symbolId must never appear in both this set and
        // missingSymbolElemCounts.
        var resolvedSymbolIds = new HashSet<uint>();
        // "Real" = came from the authoritative ElemTable, not the block-scan discovery heuristic
        // (state.BlockOnlyIds) or the record-classification fallback (BuildDiscoveredElement) -
        // i.e. an actual Revit element.
        int realTotal = 0, realUnresolved = 0, realNoNameEntry = 0, realViaTableSymbol = 0, realViaFamilyParam = 0;
        // Of the real elements with no table entry for their symbolId, how many actually had a
        // genuine ELEM_TYPE_PARAM-sourced type id to look up (vs. falling back to their own element
        // id, which is structurally never a type-table key)?
        int realHasTypeParam = 0, realNoNameEntryHasTypeParam = 0;
        foreach (var e in rvtInfo.Elements)
        {
            bool isReal = !e.IsDiscovered && !e.IsBlockOnly;
            if (isReal) realTotal++;
            // Symbol id: the element's own id if IT has a name entry with a parent, otherwise its
            // ELEM_TYPE_PARAM target.
            var typeParam = e.Parameters.FirstOrDefault(p => p.Id == -1002050);
            bool hasTypeParam = typeParam?.Value is int htp && htp > 0;
            int symbolId = typeParam?.Value is int tid && tid > 0 ? tid : e.Id;
            if (isReal && hasTypeParam) realHasTypeParam++;
            if (TryResolveFamilyNameViaTable((uint)symbolId, state, out var tableFamName, out var stopReason))
            {
                e.FamilyName = tableFamName; viaTableSymbol++;
                if (isReal) realViaTableSymbol++;
                if (diag != null)
                {
                    resolvedSymbolIds.Add((uint)symbolId);
                }
                continue;
            }
            switch (stopReason)
            {
                case "no-parent-holder":
                    // ContainsKey is the one non-trivial lookup in this diagnostic block (see
                    // comment above the counter declarations) - gated on diag != null so it costs
                    // nothing on the hot path when diagnostics are off.
                    if (diag != null)
                    {
                        if (state.ElementNameTable.ContainsKey((uint)symbolId)) noParentHolder++;
                        else
                        {
                            noNameEntry++;
                            missingSymbolElemCounts[(uint)symbolId] =
                                missingSymbolElemCounts.GetValueOrDefault((uint)symbolId) + 1;
                            if (hasTypeParam)
                                missingTypedSymbolElemCounts[(uint)symbolId] =
                                    missingTypedSymbolElemCounts.GetValueOrDefault((uint)symbolId) + 1;
                            if (isReal)
                            {
                                realNoNameEntry++;
                                if (hasTypeParam) realNoNameEntryHasTypeParam++;
                            }
                        }
                    }
                    break;
                case "no-holder-target": noHolderTarget++; break;
                case "no-table-name": noTableName++; break;
            }
            // The element itself may be a symbol (type elements)
            if (symbolId != e.Id && TryResolveFamilyNameViaTable((uint)e.Id, state, out tableFamName))
            {
                e.FamilyName = tableFamName; viaTableSelf++;
                continue;
            }

            var famParam = e.Parameters.FirstOrDefault(p => p.Id == -1002051);
            if (famParam?.Value is int famId && famId > 0)
            {
                if (state.ElementNameTable.TryGetValue((uint)famId, out var tblName))
                {
                    e.FamilyName = tblName; viaFamilyParam++;
                    if (isReal) realViaFamilyParam++;
                    continue;
                }
                if (idToName.TryGetValue(famId, out var famName))
                {
                    e.FamilyName = famName; viaFamilyParam++;
                    if (isReal) realViaFamilyParam++;
                    continue;
                }
            }
            // Fallback: look at type element's ELEM_FAMILY_PARAM
            if (typeParam?.Value is int typeId && typeId > 0 && idToElem.TryGetValue(typeId, out var typeElem))
            {
                var typeFam = typeElem.Parameters.FirstOrDefault(p => p.Id == -1002051);
                if (typeFam?.Value is int typeFamId && typeFamId > 0 && idToName.TryGetValue(typeFamId, out var typeFamName))
                {
                    e.FamilyName = typeFamName; viaTypeElement++;
                    continue;
                }
                // Last resort: use type element's Name as family name for type elements themselves
                if (!string.IsNullOrEmpty(typeElem.FamilyName))
                {
                    e.FamilyName = typeElem.FamilyName; viaTypeElement++;
                    continue;
                }
            }
            unresolved++;
            if (isReal) realUnresolved++;
        }
        // This repeats the distinct-missing-symbol count using Element.TypeId instead, restricted
        // to "real" elements only (TypeId is meaningless for discovered/block-only
        // pseudo-elements).
        if (diag != null)
        {
            var missingByElemTypeId = new Dictionary<uint, int>();
            var resolvedByElemTypeId = new HashSet<uint>();
            int realWithTypeId = 0;
            foreach (var e in rvtInfo.Elements)
            {
                if (e.IsDiscovered || e.IsBlockOnly || e.TypeId <= 0) continue;
                realWithTypeId++;
                uint tid = (uint)e.TypeId;
                if (TryResolveFamilyNameViaTable(tid, state, out _))
                    resolvedByElemTypeId.Add(tid);
                else if (!state.ElementNameTable.ContainsKey(tid))
                    missingByElemTypeId[tid] = missingByElemTypeId.GetValueOrDefault(tid) + 1;
            }
            int contradictions = missingByElemTypeId.Keys.Count(resolvedByElemTypeId.Contains);
            diag.WriteLine($"FamilyName diag: byElemTypeId realWithTypeId={realWithTypeId} "
                + $"distinctResolved={resolvedByElemTypeId.Count} distinctMissing={missingByElemTypeId.Count} "
                + $"contradictions={contradictions} "
                + $"elementsAccountedFor={missingByElemTypeId.Values.Sum()}");
        }
        if (diag != null)
        {
            diag.WriteLine($"FamilyName diag: total={rvtInfo.Elements.Count} viaTableSymbol={viaTableSymbol} "
                + $"viaTableSelf={viaTableSelf} viaFamilyParam={viaFamilyParam} viaTypeElement={viaTypeElement} "
                + $"unresolved={unresolved}");
            diag.WriteLine($"FamilyName diag: stops noNameEntry={noNameEntry} noParentHolder={noParentHolder} "
                + $"noHolderTarget={noHolderTarget} noTableName={noTableName}");
            diag.WriteLine($"FamilyName diag: tables nameEntryParentHolder={state.NameEntryParentHolder.Count} "
                + $"nameHolderTarget={state.NameHolderTarget.Count} elementNameTable={state.ElementNameTable.Count}");
            diag.WriteLine($"FamilyName diag: real(non-discovered,non-block-only) total={realTotal} "
                + $"viaTableSymbol={realViaTableSymbol} viaFamilyParam={realViaFamilyParam} "
                + $"unresolved={realUnresolved} noNameEntry={realNoNameEntry}");
            diag.WriteLine($"FamilyName diag: real hasTypeParam={realHasTypeParam} "
                + $"noNameEntryHasTypeParam={realNoNameEntryHasTypeParam} "
                + "(of the real elements missing a table entry, how many had a genuine ELEM_TYPE_PARAM id to look up)");
            void ReportDistinctMissing(string label, Dictionary<uint, int> counts)
            {
                int distinctMissing = counts.Count;
                int totalMissingElems = counts.Values.Sum();
                diag.WriteLine($"FamilyName diag: {label} distinctMissingSymbols={distinctMissing} "
                    + $"elementsAccountedFor={totalMissingElems}");
                if (distinctMissing == 0) return;
                var sortedByCount = counts.OrderByDescending(kv => kv.Value).ToList();
                int running = 0, symbolsToHalf = 0, symbolsTo90 = 0;
                for (int i = 0; i < sortedByCount.Count; i++)
                {
                    running += sortedByCount[i].Value;
                    if (symbolsToHalf == 0 && running >= totalMissingElems * 0.5) symbolsToHalf = i + 1;
                    if (symbolsTo90 == 0 && running >= totalMissingElems * 0.9) symbolsTo90 = i + 1;
                }
                string top = string.Join(", ", sortedByCount.Take(10)
                    .Select(kv => $"{kv.Key}:{kv.Value}({100.0 * kv.Value / totalMissingElems:F1}%)"));
                diag.WriteLine($"FamilyName diag: {label} missing-symbol distribution - "
                    + $"{symbolsToHalf}/{distinctMissing} symbols cover 50% of {totalMissingElems} elements, "
                    + $"{symbolsTo90}/{distinctMissing} cover 90%; top10 symbolId:count(share) = {top}");
            }
            // Raw: every symbolId seen (dominated by the self-fallback case, symbolId = e.Id, for
            // elements with no genuine ELEM_TYPE_PARAM - each such element is its own unique
            // "symbol").
            ReportDistinctMissing("raw(all-symbolIds)", missingSymbolElemCounts);
            ReportDistinctMissing("typedOnly(hasTypeParam)", missingTypedSymbolElemCounts);
            var contradictorySymbols = missingSymbolElemCounts.Keys.Where(resolvedSymbolIds.Contains).ToList();
            diag.WriteLine($"FamilyName diag: PROBE contradictorySymbols={contradictorySymbols.Count} "
                + "(symbolIds present in BOTH the missing set and the resolved-via-table set - should always be 0) "
                + $"sample={string.Join(",", contradictorySymbols.Take(5))}");
        }

        // Instance Name semantics (matches Revit API): an element without its own name entry
        // reports its TYPE's name. Apply where the type name is table-backed.
        foreach (var e in rvtInfo.Elements)
        {
            if (e.CategoryId == -2000220) continue;
            if (state.ElementNameTable.ContainsKey((uint)e.Id)) continue; // has own name
            var tp = e.Parameters.FirstOrDefault(p => p.Id == -1002050);
            if (tp?.Value is int tid && tid > 0 && tid != e.Id
                && state.ElementNameTable.TryGetValue((uint)tid, out var typeName))
                e.Name = typeName;
        }
        // The compatibility ELEM_TYPE_PARAM value above can be a real element id of the wrong
        // category even after TypeId was canonically resolved.
        ApplyValidatedWallTypeNames(
            rvtInfo.Elements, elemByIdLookup,
            state.CompoundStructureTypeNameSource,
            state.NativeParams);
        ApplyValidatedCeilingTypeNames(
            rvtInfo.Elements, elemByIdLookup,
            state.CeilingTypeNameSource);

        // User param name resolution: use ForgeParamDefs (reliable) or ElemStrings &-prefix DO NOT
        // use idToName for ParamElem elements - their Name comes from unreliable ScanStr
        var paramDefNames = new Dictionary<int, string>();
        foreach (var (pid, def) in state.ForgeParamDefs)
        {
            if (def.DisplayName != null)
                paramDefNames[(int)pid] = def.DisplayName;
        }
        // Also try extracting names from &-prefixed strings in ElemStrings for elements not yet in
        // paramDefNames
        foreach (var (pid, strs) in state.ElemStrings)
        {
            if (paramDefNames.ContainsKey((int)pid)) continue;
            var ampName = strs.FirstOrDefault(s => s.StartsWith('&') && s.Length > 1);
            if (ampName != null)
                paramDefNames[(int)pid] = ampName[1..];
        }
        foreach (var e in rvtInfo.Elements)
        {
            foreach (var p in e.Parameters)
            {
                if (p.Id > 0 && string.IsNullOrEmpty(p.Definition?.Name)
                    && paramDefNames.TryGetValue(p.Id, out var paramName))
                {
                    p.Definition ??= new Definition();
                    p.Definition.Id = p.Id;
                    p.Definition.Name = paramName;
                }
            }
        }

        // Fix SYMBOL_ID_PARAM: record body stores it as -1 (null) for instances; SYMBOL_ID_PARAM
        // should always equal ELEM_TYPE_PARAM for FamilyInstances.
        foreach (var e in rvtInfo.Elements)
        {
            var symParam = e.Parameters.FirstOrDefault(p => p.Id == -1002000);
            if (symParam?.Value is not int sv || sv > 0) continue; // already valid
            var typeParam = e.Parameters.FirstOrDefault(p => p.Id == -1002050);
            if (typeParam?.Value is int tv && tv > 0)
                symParam.Value = tv;
        }

        // Infer Level element elevations (LEVEL_ELEV = -1007102) via cross-reference: Elements on a
        // level store their level's elevation as LEVEL_ELEV in 0xFFFF BIP blocks.
        {
            const int BIP_LEVEL_ELEV = -1007102;
            const int BIP_ELEM_CATEGORY = -1012107;
            const int OST_LEVELS = -2000240;

            var levelElemIdSet = new HashSet<int>(rvtInfo.Elements
                .Where(e => e.Parameters.Any(p => p.Id == BIP_ELEM_CATEGORY && p.Value is int c && c == OST_LEVELS))
                .Select(e => e.Id));

            if (levelElemIdSet.Count > 0)
            {
                var inferredLevelElevs = new Dictionary<int, double>(); // levelId -> elevation in feet

                foreach (var e in rvtInfo.Elements)
                {
                    var le = e.Parameters.FirstOrDefault(p => p.Id == BIP_LEVEL_ELEV);
                    if (le?.Value is not double elev) continue;
                    if (e.References == null) continue;

                    foreach (var refId in e.References)
                    {
                        if (!levelElemIdSet.Contains(refId)) continue;
                        // Wins (all samples for the same level should agree)
                        inferredLevelElevs.TryAdd(refId, elev);
                        break;
                    }
                }

                // Assign LEVEL_ELEV to Level elements that don't already have it
                foreach (var levelId in levelElemIdSet)
                {
                    if (!inferredLevelElevs.TryGetValue(levelId, out double elev)) continue;
                    var levelElem = rvtInfo.Elements.FirstOrDefault(e => e.Id == levelId);
                    if (levelElem == null) continue;
                    if (levelElem.Parameters.Any(p => p.Id == BIP_LEVEL_ELEV)) continue; // already set

                    BipNames.Lookup.TryGetValue(BIP_LEVEL_ELEV, out string? bipName);
                    levelElem.Parameters.Add(new Parameter
                    {
                        Id = BIP_LEVEL_ELEV,
                        StorageType = StorageType.Double,
                        Value = elev,
                        IsReadOnly = BipHelpers.IsBipReadOnly(bipName),
                        Definition = new Definition { Id = BIP_LEVEL_ELEV, BuiltInParameter = bipName, Name = bipName }
                    });
                }
            }
        }

        // Compute INSTANCE_ELEVATION_PARAM (-1001360 = "Elevation from Level") for elements that
        // have LEVEL_ELEV (= their associated level's elevation) and a BoundingBox. Formula:
        // INSTANCE_ELEVATION_PARAM = BBox.MinZ - LEVEL_ELEV
        {
            const int BIP_LEVEL_ELEV2 = -1007102;
            const int BIP_INST_ELEV = -1001360;
            BipNames.Lookup.TryGetValue(BIP_INST_ELEV, out string? instElevName);

            foreach (var e in rvtInfo.Elements)
            {
                var existing = e.Parameters.FirstOrDefault(p => p.Id == BIP_INST_ELEV);
                if (existing?.Value is double) continue; // already a real double value

                var le = e.Parameters.FirstOrDefault(p => p.Id == BIP_LEVEL_ELEV2);
                if (le?.Value is not double levelElev) continue;
                if (GeometryBox(e) is not double[] bb || bb.Length < 6) continue;

                double instElev = bb[2] - levelElev; // MinZ - level elevation

                if (existing != null)
                    existing.Value = instElev; // update the null entry
                else
                    e.Parameters.Add(new Parameter
                    {
                        Id = BIP_INST_ELEV,
                        StorageType = StorageType.Double,
                        Value = instElev,
                        IsReadOnly = BipHelpers.IsBipReadOnly(instElevName),
                        Definition = new Definition { Id = BIP_INST_ELEV, BuiltInParameter = instElevName, Name = instElevName }
                    });
            }
        }

        // Export Tf class schemas to public API
        if (state?.TfClassSchemas.Count > 0)
        {
            rvtInfo.ClassSchemas = new Dictionary<string, Model.ClassSchema>();
            foreach (var (name, def) in state.TfClassSchemas)
            {
                rvtInfo.ClassSchemas[name] = new Model.ClassSchema
                {
                    Name = name,
                    Fields = def.Fields.Select(f => new Model.ClassField
                    {
                        Name = f.Name,
                        Type = TfClassRegistry.TypeName(f.Type)
                    }).ToList()
                };
            }
        }

        ResolveTypeParams(rvtInfo);
        ApplyDetailLines(rvtInfo, state);
        ApplyBlockSignatureCategories(rvtInfo, state);
        // Native (schema-deserialized) params land BEFORE the default-emitting passes so
        // authoritative stored values are not blocked by add-if-missing defaults.
        ApplyNativeParams(rvtInfo, state);
        TraceElement(rvtInfo, "after-native");
        if (state != null)
        {
            ApplySerializedHostParentIds(rvtInfo.Elements, state.NativeParams);
            ApplySerializedRailingData(
                rvtInfo.Elements, state.NativeParams, state.ElemClassIdx);
        }
        FixCategoryFromClassIdx(rvtInfo, state);
        PropagateInstanceCategoryFromType(rvtInfo);
        TraceElement(rvtInfo, "before-prune");
        PruneNativeFieldTableOutsideDomains(rvtInfo, state?.ElemClassIdx);
        TraceElement(rvtInfo, "after-prune");
        if (state != null)
        {
            ApplySerializedSystemTypeIdentity(rvtInfo.Elements, state);
            ApplyValidatedOriginalFamilySymbols(rvtInfo.Elements, state);
            ApplyOwnFamilyCutoutInputs(rvtInfo.Elements, state);
            ApplySolidTrimmedAxis(rvtInfo.Elements);
            ApplyValidatedCeilingCanonicalTypes(rvtInfo.Elements, state);
            ApplySerializedStairData(
                rvtInfo.Elements,
                state.StairElementRootSource,
                state.StairTypeRootSource);
            StairComponentSource.Apply(rvtInfo.Elements, state);
        }
        if (state != null)
        {
            ApplyValidatedMemberTypeNames(
                rvtInfo.Elements, elemByIdLookup,
                state.NativeParams, state.ElementNameTable,
                state.NativeElementNames);
        }
        AddCategoryAndFamilyParams(rvtInfo);
        AddCurveLengths(rvtInfo);
        ApplyMaterialAppearance(rvtInfo, state);
        ApplyHvacSpaceTypeParams(rvtInfo, state);
        if (state != null) ApplySerializedRoomNames(rvtInfo.Elements, state.RoomNameSource);
        if (state != null) ApplySerializedSpatialPhases(rvtInfo, state);
        if (state != null) ApplySerializedRoomPlanAreas(rvtInfo, state);
        if (state != null) ApplySerializedPlanBoundaries(rvtInfo, state);
        if (state != null) ApplySerializedSpaceBoundaries(
            rvtInfo.Elements, state.RoomNameSource, state.SpaceBoundarySource, state.SpaceLiteralBoundaries);
        ApplyRoomParams(rvtInfo);
        ApplySheetParams(rvtInfo, state);
        AddViewAndDatumDefaults(rvtInfo);
        AddCurtainQuantities(rvtInfo);
        EnrichTypeLevelBips(rvtInfo, state);
        PropagateChainPlaceholders(rvtInfo, state);
        PropagateTypeElementIdParams(rvtInfo, state);
        PropagateFamilySymbolBlockDoubles(rvtInfo, state);
        PropagateFamilySymbolBlockStrings(rvtInfo, state);
        PropagateFamilySymbolBlockInts(rvtInfo, state);
        PropagateFamilyElementIdParams(rvtInfo, state);
        CoerceParamStorageByForge(rvtInfo, state);
        ApplyDerivedDesignOptionName(rvtInfo);
        ApplyCurveDriverLevelParams(rvtInfo);
        ApplyHostIdParam(rvtInfo, state?.ElemClassIdx);
        ApplyInstanceOriginOffsets(rvtInfo);
        ApplyInstanceElevationFromLevel(rvtInfo);
        ApplyStructuralColumnParams(rvtInfo);
        ApplyRoomLevelParams(rvtInfo);
        ApplyFramingSymbolLength(rvtInfo);
        if (state != null) ApplyMullionSymbolLength(rvtInfo.Elements, state.NativeParams);
        ApplyRoomBuildingTypeLoads(rvtInfo);
        ApplyHeadHeight(rvtInfo);
        ApplyStructuralConnectionParams(rvtInfo);
        ApplyCrossSectionRotation(rvtInfo);
        ApplyDetailCurveFlag(rvtInfo, state?.ElemClassIdx);
        ApplyStoredPhases(rvtInfo, state?.ElemClassIdx,
            state?.ProjectPhaseOrderSource is { BlockCount: 1, PhaseIds: { } phaseOrder } ? phaseOrder : null);
        TraceElement(rvtInfo, "after-phases");
        BuildSketchTopology(rvtInfo, state);
        // Runs LAST: needs WALL_BASE_CONSTRAINT / WALL_HEIGHT_TYPE emitted by the passes above.
        ApplyComputedHeights(rvtInfo, state);
        ApplyBipKeyedStrings(rvtInfo, state);
        ApplyWorksets(rvtInfo, state);
        TraceElement(rvtInfo, "before-fixphase");
        FixUnresolvedPhaseCreated(rvtInfo);
        TraceElement(rvtInfo, "after-fixphase");
        FixWallStructuralUsage(rvtInfo);
        FixWallBaseConstraint(rvtInfo);
        FixNodeConnectionStatus(rvtInfo, state);
        AddCategoryConstantParams(rvtInfo, state);
        // Wall centrelines the pattern scan missed: synthesize from face geometry (needs the width
        // param, which type propagation has filled by now).
        if (state != null)
            foreach (var e in rvtInfo.Elements)
                if (e.CategoryId == -2000011 && e.Location == null)
                    BuildLocationFromTailLine(e, (uint)e.Id, state);
        ComputeSketchQuantities(rvtInfo, state);
        if (state != null) ApplyCoarseDoorWindowSurfaces(rvtInfo, state);
        ApplyFramingCopingDistance(rvtInfo);
        ApplyColumnAttachments(rvtInfo);
        ApplyMovesWithGrids(rvtInfo);
        ApplyEditedByCategoryRules(rvtInfo, state);
        ApplyCapturedCurveStyles(rvtInfo, state);
        // Reference precedence needs the final decoded instance/type parameter sets.
        if (state != null)
        {
            ApplyValidatedFloorTypeReferenceNames(
                rvtInfo.Elements, elemByIdLookup,
                state.FloorTypeNameSource, state.NativeParams, state);
            ApplyValidatedCeilingTypeReferenceNames(
                rvtInfo.Elements,
                state.CeilingTypeNameSource,
                state.NativeParams, state.ElemClassIdx);
            ApplyValidatedWindowTypeReferenceNames(
                rvtInfo.Elements, elemByIdLookup,
                state.NativeParams, state.ElementNameTable);
            ApplyValidatedProxyTypeReferenceNames(
                rvtInfo.Elements, elemByIdLookup,
                state.NativeParams, state.ElementNameTable);
            ApplyValidatedWallSweepTypeReferenceNames(rvtInfo.Elements, state);
            ApplyValidatedDistributionTypeReferenceNames(rvtInfo.Elements, state);
            ApplyValidatedRoofOrRailingTypeReferenceNames(rvtInfo.Elements, state);
            ApplyValidatedProfileRoofAreas(rvtInfo.Elements, state);
            ApplyValidatedRailingLengths(rvtInfo.Elements, state);
            ApplyValidatedFamilyNamesFromCanonicalTypeId(rvtInfo.Elements, state);
            ApplyValidatedDoorTypeNames(rvtInfo.Elements, state);
            ApplyValidatedIfcTypeObjectNames(rvtInfo.Elements, state);
            ApplyBasicWallTypeFunctions(rvtInfo.Elements, state.BasicWallTypeFunctions);
            ApplyStackedWallTypes(rvtInfo.Elements, state);
            ApplyCurtainTypeNames(rvtInfo.Elements, state);
            ApplyBasicWallTypeNames(rvtInfo.Elements, state);
            ApplyCurtainFamilyHosts(rvtInfo.Elements, state);
            ApplyCurtainPanelOrders(rvtInfo.Elements, state);
            RepairBasicWallTypeFunctionBodyFallbacks(rvtInfo.Elements, state);
        }
        foreach (var e in rvtInfo.Elements)
        {
            Parameter? lv = null; bool hasSched = false;
            foreach (var p in e.Parameters)
            {
                if (p.Id == -1001952) lv = p;
                else if (p.Id == -1002062) { hasSched = true; break; }
            }
            if (!hasSched && !e.ScheduleLevelFromStoredFields && lv?.Value is int lvv && lvv > 0)
                e.Parameters.Add(BipHelpers.MakeBipParam(-1002062, "SCHEDULE_LEVEL_PARAM", StorageType.ElementId, lvv));
        }
        // Curtain panel GENERIC_HEIGHT and CURTAIN_WALL_PANELS_HEIGHT are the same value; fill
        // whichever is missing from the other.
        foreach (var e in rvtInfo.Elements)
        {
            Parameter? gen = null, pan = null;
            foreach (var p in e.Parameters)
            {
                if (p.Id == -1001300) gen = p;
                else if (p.Id == -1010300) pan = p;
            }
            if (gen?.Value is double gv && pan == null)
            {
                e.Parameters.Add(BipHelpers.MakeBipParam(-1010300, "CURTAIN_WALL_PANELS_HEIGHT", StorageType.Double, gv));
                if (e.BoundingBoxDimensionParamIds != null && e.BoundingBoxDimensionParamIds.Contains(-1001300))
                    e.BoundingBoxDimensionParamIds.Add(-1010300);
            }
            else if (pan?.Value is double pv && gen == null)
            {
                e.Parameters.Add(BipHelpers.MakeBipParam(-1001300, "GENERIC_HEIGHT", StorageType.Double, pv));
                if (e.BoundingBoxDimensionParamIds != null && e.BoundingBoxDimensionParamIds.Contains(-1010300))
                    e.BoundingBoxDimensionParamIds.Add(-1001300);
            }
            // Same pair for the width: GENERIC_WIDTH equals CURTAIN_WALL_PANELS_WIDTH on every
            // element that has the panel width.
            Parameter? genWidth = null, panWidth = null;
            foreach (var p in e.Parameters)
            {
                if (p.Id == -1001301) genWidth = p;
                else if (p.Id == -1010301) panWidth = p;
            }
            if (panWidth?.Value is double pw && genWidth == null)
            {
                e.Parameters.Add(BipHelpers.MakeBipParam(-1001301, "GENERIC_WIDTH", StorageType.Double, pw));
                if (e.BoundingBoxDimensionParamIds != null && e.BoundingBoxDimensionParamIds.Contains(-1010301))
                    e.BoundingBoxDimensionParamIds.Add(-1001301);
            }
            else if (genWidth?.Value is double gw && panWidth == null)
            {
                e.Parameters.Add(BipHelpers.MakeBipParam(-1010301, "CURTAIN_WALL_PANELS_WIDTH", StorageType.Double, gw));
                if (e.BoundingBoxDimensionParamIds != null && e.BoundingBoxDimensionParamIds.Contains(-1001301))
                    e.BoundingBoxDimensionParamIds.Add(-1010301);
            }
        }
        CreateElemTablePlaceholders(rvtInfo, state);
        ApplyHeaderBoundingBoxes(rvtInfo, state);
        // NOTE: PropagateFamilyTypeValues remains disabled. The chain rule that works perfectly for
        // instance 0x0702 values (value of entry K belongs to chain[K] = next entry's binKey) does
        // NOT generalize to type-level 0x0725 values.
    }

    /// <summary>The box the parser rules measure: the tight box of the decoded geometry, else the
    /// element's own box. Before the header outline took Element.BoundingBox over (the API envelope)
    /// this was the value of Element.BoundingBox alone.</summary>
    internal static double[]? GeometryBox(Element element) => element.Geometry?.BoundingBox ?? element.BoundingBox;

    /// <summary>
    /// The axis-aligned outline the element header stores is the element's bounding box, in every
    /// release (the box the application reports); the tighter box of the decoded geometry stays on
    /// Geometry.BoundingBox. A header that stores no outline clears the box: the materials, levels
    /// and groups the geometry gave a box have none in the application either. Records that
    /// disagree leave the element unchanged.
    /// </summary>
    internal static void ApplyHeaderBoundingBoxes(RevitFileInfo rvtInfo, ParsingState? state)
    {
        if (state == null) return;
        foreach (var e in rvtInfo.Elements)
            if (state.HeaderBoundingBoxes.TryGetValue((uint)e.Id, out var box) && box != null)
            {
                e.BoundingBox = box.Present ? box.Box : null;
                e.BoundingBoxIsHeaderOutline = box.Present;
            }
    }

    /// <param name="instanceOrder">The order key of the GInstance node the source geometry is the symbol of: the source's nodes then sort at that node's place among the nodes of the target (Revit walks a GInstance's symbol where the GInstance stands); null keeps the keys of the source as they are.</param>
    private static void AppendGeometry(GeometryData target, GeometryData source, long? instanceOrder = null)
    {
        int faceOffset = target.Faces.Count;
        int edgeOffset = target.Edges?.Count ?? 0;
        // Geometry node ids are unique within one GeometryData: the appended nodes follow the
        // existing ones. (a negative id marks a node that Revit does not take, see
        // GRepParserV2).
        int nodeOffset = target.Faces.Count == 0 ? 0 : target.Faces.Max(face => Math.Abs(face.GeometryNode));

        if (source.Edges is { Count: > 0 })
        {
            target.Edges ??= new List<GeoEdge>();
            foreach (var edge in source.Edges)
                target.Edges.Add(new GeoEdge
                {
                    Start = (double[])edge.Start.Clone(),
                    End = (double[])edge.End.Clone(),
                });
        }

        if (source.Vertices is { Count: > 0 })
        {
            target.Vertices ??= new List<double[]>();
            foreach (var vertex in source.Vertices)
                target.Vertices.Add((double[])vertex.Clone());
        }

        foreach (var face in source.Faces)
        {
            target.Faces.Add(new GeoFace
            {
                Origin = (double[])face.Origin.Clone(),
                XVector = (double[])face.XVector.Clone(),
                YVector = (double[])face.YVector.Clone(),
                Normal = (double[])face.Normal.Clone(),
                Envelope = face.Envelope is null ? null : (double[])face.Envelope.Clone(),
                Area = face.Area,
                InnerLoops = face.InnerLoops?.Select(loop => (double[])loop.Clone()).ToList(),
                OuterVertices = face.OuterVertices?.Select(v => (double[])v.Clone()).ToList(),
                InnerVertices = face.InnerVertices?
                    .Select(loop => loop.Select(v => (double[])v.Clone()).ToList()).ToList(),
                AnalyticCylinderOrigin = face.AnalyticCylinderOrigin is null
                    ? null : (double[])face.AnalyticCylinderOrigin.Clone(),
                AnalyticCylinderAxis = face.AnalyticCylinderAxis is null
                    ? null : (double[])face.AnalyticCylinderAxis.Clone(),
                AnalyticCylinderReferenceDirection =
                    face.AnalyticCylinderReferenceDirection is null
                        ? null : (double[])face.AnalyticCylinderReferenceDirection.Clone(),
                AnalyticCylinderRadius = face.AnalyticCylinderRadius,
                MeshEdgeBounds = face.MeshEdgeBounds,
                MeshPlaneEnvelopeCorners = face.MeshPlaneEnvelopeCorners,
                MeshArcs = face.MeshArcs,
                QuadratureArea = face.QuadratureArea,
                CurvedSurface = face.CurvedSurface,
                PlaneSurface = face.PlaneSurface,
                PlaneXAxis = face.PlaneXAxis,
                PlaneYAxis = face.PlaneYAxis,
                SourceFace = face.SourceFace,
                MeshMarker = face.MeshMarker,
                NestedRenderStyleId = face.NestedRenderStyleId ?? face.RenderStyleId,
                GeometryNode = face.GeometryNode == 0 ? 0 : face.GeometryNode + Math.Sign(face.GeometryNode) * nodeOffset,
                NodeOrder = instanceOrder is { } and not 0 && face.NodeOrder != 0
                    ? GRepParserV2.NestOrderKey(instanceOrder.Value, face.NodeOrder) : face.NodeOrder,
                GeometryStyle = face.GeometryStyle,
                EdgeIndices = face.EdgeIndices?.Select(index => index + edgeOffset).ToList(),
            });
        }

        // the nodes with an id follow the appended faces (node 0, the faces without a node id,
        // would merge with the nodes of the target)
        if (source.MeshNodes is { Count: > 0 })
            foreach (var node in source.MeshNodes.Where(node => node.Node != 0))
                (target.MeshNodes ??= new()).Add(node with
                {
                    Node = node.Node + Math.Sign(node.Node) * nodeOffset,
                    NodeOrder = instanceOrder is { } and not 0 && node.NodeOrder != 0 ? GRepParserV2.NestOrderKey(instanceOrder.Value, node.NodeOrder) : node.NodeOrder,
                });

        if (source.NodeMeasures is { Count: > 0 })
            foreach (var measure in source.NodeMeasures.Where(measure => measure.Node != 0))
                (target.NodeMeasures ??= new()).Add(measure with { Node = measure.Node + Math.Sign(measure.Node) * nodeOffset });

        if (source.BrepSolids is { Count: > 0 })
        {
            target.BrepSolids ??= new List<GeoBrepSolid>();
            foreach (var solid in source.BrepSolids)
                target.BrepSolids.Add(new GeoBrepSolid
                {
                    FaceIndices = solid.FaceIndices.Select(index => index + faceOffset).ToList(),
                    IsClosed = solid.IsClosed,
                });
        }

        if (source.Solids is { Count: > 0 })
        {
            target.Solids ??= new List<GeoSolid>();
            target.Solids.AddRange(source.Solids);
        }

        if (source.Instances is { Count: > 0 })
        {
            target.Instances ??= new List<GeoInstance>();
            foreach (var instance in source.Instances)
                target.Instances.Add(new GeoInstance
                {
                    SymbolId = instance.SymbolId,
                    Transform = (double[])instance.Transform.Clone(),
                    NodeOrder = instance.NodeOrder,
                });
        }
    }

    /// <summary>
    /// Elements whose PHASE_CREATED couldn't be resolved from a phase reference emit the -1
    /// sentinel (EnrichTypeLevelBips): curtain-wall sub-elements - mullions, panels, grid lines -
    /// plus ElemTable-covered instances that carry no phase reference in their block.
    /// </summary>
    private static void FixUnresolvedPhaseCreated(RevitFileInfo info)
    {
        var phaseVotes = new Dictionary<int, int>();
        foreach (var e in info.Elements)
            foreach (var p in e.Parameters)
                if (p.Id == -1012100 && p.Value is int ph && ph > 0)
                { phaseVotes.TryGetValue(ph, out int n); phaseVotes[ph] = n + 1; break; }
        int domPhase = -1, domN = 0;
        foreach (var (ph, n) in phaseVotes) if (n > domN) { domPhase = ph; domN = n; }
        if (domPhase <= 0) return;

        foreach (var e in info.Elements)
            foreach (var p in e.Parameters)
                if (p.Id == -1012100 && p.Value is int pv && pv < 0)
                { p.Value = domPhase; p.StorageType = StorageType.ElementId; break; }
    }

    /// <summary>
    /// The wall block field we read at anchor+36 as WALL_STRUCTURAL_USAGE is effectively a constant
    /// (~always 1) - it is NOT the structural-usage enum. So derive WALL_STRUCTURAL_USAGE from the
    /// significant flag we already emit.
    /// </summary>
    internal static void FixWallStructuralUsage(RevitFileInfo info)
    {
        foreach (var e in info.Elements)
        {
            int? sig = null; Parameter? usage = null;
            foreach (var p in e.Parameters)
            {
                if (p.Id == -1001596 && p.Value is int sv && (sv == 0 || sv == 1)) sig = sv;
                else if (p.Id == -1001119) usage = p;
            }
            if (sig is int s && usage != null && usage.Value is not > 1)
            { usage.Value = s; usage.StorageType = StorageType.Integer; }
        }
    }

    /// <summary>
    /// Assign category from the element's own block classId via the high-purity census table (<see
    /// cref="TfClassRegistry.HighPurityClassCategory"/>), using the continuous-walk classId map -
    /// far more than the per-chunk BlockParser registry pass reaches.
    /// </summary>
    private static void FixCategoryFromClassIdx(RevitFileInfo info, ParsingState? state)
    {
        if (state == null || state.ElemClassIdx.Count == 0) return;
        var nameByCat = new Dictionary<int, string?>();
        foreach (var e in info.Elements)
            if (e.CategoryId is int c && c != -1 && e.Category != null && !nameByCat.ContainsKey(c))
                nameByCat[c] = e.Category;

        foreach (var e in info.Elements)
        {
            if (e.Id <= 0 || !state.ElemClassIdx.TryGetValue((uint)e.Id, out var cls)) continue;
            if (!TfClassRegistry.HighPurityClassCategory.TryGetValue(cls, out int cat)) continue;
            if (e.CategoryId == cat) continue;
            e.CategoryId = cat;
            if (nameByCat.TryGetValue(cat, out var nm)) e.Category = nm;
            var cp = e.Parameters.FirstOrDefault(p => p.Id == -1140362);
            if (cp != null) { cp.Value = cat; cp.StorageType = StorageType.ElementId; }
            else e.Parameters.Add(BipHelpers.MakeBipParam(-1140362, "ELEM_CATEGORY_PARAM", StorageType.ElementId, cat));
            var cpm = e.Parameters.FirstOrDefault(p => p.Id == -1140363);
            if (cpm != null) { cpm.Value = cat; cpm.StorageType = StorageType.ElementId; }
            else e.Parameters.Add(BipHelpers.MakeBipParam(-1140363, "ELEM_CATEGORY_PARAM_MT", StorageType.ElementId, cat));
        }
    }

    /// <summary>An instance's category equals its type's category.</summary>
    private static void PropagateInstanceCategoryFromType(RevitFileInfo info)
    {
        var catById = new Dictionary<int, (int cat, string? name)>();
        foreach (var e in info.Elements)
            if (e.CategoryId is int c && c != -1) catById[e.Id] = (c, e.Category);

        for (int pass = 0; pass < 4; pass++)
        {
            int added = 0;
            foreach (var e in info.Elements)
            {
                if (e.CategoryId is int ec && ec != -1) continue; // already categorized
                if (e.TypeId <= 0 || !catById.TryGetValue(e.TypeId, out var tc)) continue;
                e.CategoryId = tc.cat;
                e.Category = tc.name;
                catById[e.Id] = tc;
                var cp = e.Parameters.FirstOrDefault(p => p.Id == -1140362);
                if (cp != null) { cp.Value = tc.cat; cp.StorageType = StorageType.ElementId; }
                else e.Parameters.Add(BipHelpers.MakeBipParam(-1140362, "ELEM_CATEGORY_PARAM", StorageType.ElementId, tc.cat));
                var cpm = e.Parameters.FirstOrDefault(p => p.Id == -1140363);
                if (cpm != null) { cpm.Value = tc.cat; cpm.StorageType = StorageType.ElementId; }
                else e.Parameters.Add(BipHelpers.MakeBipParam(-1140363, "ELEM_CATEGORY_PARAM_MT", StorageType.ElementId, tc.cat));
                added++;
            }
            if (added == 0) break;
        }
    }

    /// <summary>
    /// Analytic geometry engine for Revit-computed quantities on sketch-based hosts (floors
    /// -2000032 / ceilings -2000038 / roofs -2000035).
    /// </summary>
    private static void ComputeSketchQuantities(RevitFileInfo info, ParsingState? state)
    {
        var byId = new Dictionary<int, Element>(info.Elements.Count);
        foreach (var e in info.Elements) byId[e.Id] = e;

        foreach (var e in info.Elements)
        {
            if (e.CategoryId is not (-2000032 or -2000038 or -2000035)) continue;
            var sk = e.SketchLoops;
            if (sk == null && e.References != null)
                foreach (var r in e.References)
                    if (byId.TryGetValue(r, out var re) && re.SketchLoops != null) { sk = re.SketchLoops; break; }
            if (sk == null || sk.Count == 0) continue;

            double outerA = 0, sumA = 0, outerP = 0;
            foreach (var loop in sk)
            {
                if (loop.Count < 3) continue;
                double a = 0, perim = 0;
                for (int i = 0; i < loop.Count; i++)
                {
                    var p1 = loop[i]; var p2 = loop[(i + 1) % loop.Count];
                    if (p1.Length < 2 || p2.Length < 2) continue;
                    a += p1[0] * p2[1] - p2[0] * p1[1];
                    perim += Math.Sqrt((p2[0] - p1[0]) * (p2[0] - p1[0]) + (p2[1] - p1[1]) * (p2[1] - p1[1]));
                }
                a = Math.Abs(a) / 2; sumA += a;
                if (a > outerA) { outerA = a; outerP = perim; }
            }
            double holes = sumA - outerA;
            double net = outerA - holes;
            if (net <= 0) continue;

            // Plausibility: net must sit within [0.3, 1.05] of the bbox footprint (when known) -
            // rejects wrong/partial sketches (drops half the bad cases at equal ok cost).
            if (GeometryBox(e) is { Length: >= 6 } bb)
            {
                double foot = (bb[3] - bb[0]) * (bb[4] - bb[1]);
                if (foot > 1e-6 && (net < 0.3 * foot || net > 1.05 * foot)) continue;
            }

            if (e.Parameters.All(p => p.Id != -1012805))
                e.Parameters.Add(BipHelpers.MakeBipParam(-1012805, "HOST_AREA_COMPUTED", StorageType.Double, net));
            if (outerP > 0 && e.Parameters.All(p => p.Id != -1001953))
                e.Parameters.Add(BipHelpers.MakeBipParam(-1001953, "HOST_PERIMETER_COMPUTED", StorageType.Double, outerP));

            // Volume = net x type thickness (sum of compound layers of the type).
            if (state != null && e.TypeId > 0
                && state.CompoundLayers.TryGetValue((uint)e.TypeId, out var layers) && layers.Count > 0)
            {
                double thick = layers.Sum(l => l.ThicknessFt);
                if (thick > 0 && e.Parameters.All(p => p.Id != -1012806))
                    e.Parameters.Add(BipHelpers.MakeBipParam(-1012806, "HOST_VOLUME_COMPUTED", StorageType.Double, net * thick));
            }
        }
    }

    private static readonly HashSet<int> EditedByUserCats = new()
    {
        -2000171, -2009645, -2000011, -2000170, -2000151, -2000321, -2000014, -2000576,
        -2000023, -2009640, -2001160, -2000262, -2000038, -2000032, -2000530, -2000160,
    };

    private static readonly HashSet<int> EditedByEmptyCats = new()
    {
        -2000051, -2000260, -2000480, -2005011, -2002000, -2000300, -2005013,
        -2000279, -2000263, -2009609, -2005026, -2000450, -2000055, -2000573, -2000510,
        -2000150, -2000938, -2000700,
    };

    /// <summary>
    /// EDITED_BY (worksharing owner) is category-determined in the local-file exports: model
    /// categories the user edited report the LOCAL USERNAME (they were borrowed on edit);
    /// annotation/view categories report empty.
    /// </summary>
    private static void ApplyEditedByCategoryRules(RevitFileInfo info, ParsingState? state)
    {
        string? user = state?.LocalUsername;
        if (string.IsNullOrEmpty(user)) return;
        foreach (var e in info.Elements)
        {
            // Key the rule on the EMITTED category param (what the export/comparison sees), not
            // e.CategoryId - late passes assign CategoryId without updating the param, and those
            // are exactly the internal elements.
            int c = -1;
            foreach (var p in e.Parameters)
                if (p.Id == -1140362) { if (p.Value is int cv) c = cv; break; }
            if (c == -1) continue;
            string? val = EditedByUserCats.Contains(c) ? user
                : EditedByEmptyCats.Contains(c) ? "" : null;
            if (val == null) continue;
            if (e.Parameters.All(p => p.Id != -1002067))
                e.Parameters.Add(BipHelpers.MakeBipParam(-1002067, "EDITED_BY", StorageType.String, val));
        }
    }

    /// <summary>Emit the captured curve line style as BUILDING_CURVE_GSTYLE / _PLUS_INVISIBLE.</summary>
    private static void ApplyCapturedCurveStyles(RevitFileInfo info, ParsingState? state)
    {
        if (state == null) return;
        foreach (var e in info.Elements)
        {
            if (e.Id <= 0) continue;
            uint eid = (uint)e.Id;
            int sty = 0;
            if (state.DetailCurveStyles.TryGetValue(eid, out int d) && d > 0) sty = d;
            else if (state.CurveGeometry.TryGetValue(eid, out var g) && g.GStyle > 0) sty = g.GStyle;
            if (state.CurveGeometry.TryGetValue(eid, out var cgl) && cgl.Length > 1e-9
                && e.Parameters.All(p => p.Id != -1004005))
                e.Parameters.Add(BipHelpers.MakeBipParam(-1004005, "CURVE_ELEM_LENGTH", StorageType.Double, cgl.Length));
            if (sty <= 0) continue;
            if (e.Parameters.All(p => p.Id != -1006210))
                e.Parameters.Add(BipHelpers.MakeBipParam(-1006210, "BUILDING_CURVE_GSTYLE", StorageType.ElementId, sty));
            if (e.Parameters.All(p => p.Id != -1006211))
                e.Parameters.Add(BipHelpers.MakeBipParam(-1006211, "BUILDING_CURVE_GSTYLE_PLUS_INVISIBLE", StorageType.ElementId, sty));
        }
    }

    /// <summary>Added only when missing, so decoded values always win.</summary>
    private static void AddCategoryConstantParams(RevitFileInfo info, ParsingState? state)
    {
        // Analytical model categories (surfaces/sticks), excluding nodes (-2009645) which don't
        // carry the param in the export.
        var analyticalCats = new HashSet<int> { -2009640, -2009641, -2009642, -2009643, -2009644, -2009646, -2009649 };
        void AddIfMissing(Element e, int id, string name, int val)
        {
            if (e.Parameters.All(p => p.Id != id))
                e.Parameters.Add(BipHelpers.MakeBipParam(id, name, StorageType.Integer, val));
        }
        foreach (var e in info.Elements)
        {
            bool curveOwner = state != null && e.Id > 0
                && (state.CurveBlockOwners.Contains((uint)e.Id) || state.CurveGeometry.ContainsKey((uint)e.Id));
            if (e.CategoryId is not int c)
            {
                if (curveOwner)
                {
                    // Uncategorized curve-block owner -> detail line.
                    AddIfMissing(e, -1004011, "CURVE_IS_DETAIL", 1);
                    // Sketch-constraint constants. Only MODEL lines carry them in the export;
                    // detail lines don't, so the extra params on true detail lines are simply not
                    // compared.
                    AddIfMissing(e, -1006009, "CURVE_LEVEL", -1);
                    AddIfMissing(e, -1006006, "DEFINES_CONSTANT_HEIGHT", 0);
                    AddIfMissing(e, -1006007, "CURVE_IS_SLOPE_DEFINING", 0);
                    AddIfMissing(e, -1152343, "ANALYTICAL_MODEL_MANUALLY_ADJUSTED", 0);
                    AddIfMissing(e, -1009523, "ANALYTICAL_MODEL_SKETCH_PROJECTION", -6);
                    AddIfMissing(e, -1009522, "ANALYTICAL_MODEL_SKETCH_ALIGNMENT_METHOD", 0);
                    if (e.Parameters.All(p => p.Id != -1006008))
                        e.Parameters.Add(BipHelpers.MakeBipParam(-1006008, "CURVE_HEIGHT_OFFSET", StorageType.Double, 0.0));
                    if (e.Parameters.All(p => p.Id != -1006015))
                        e.Parameters.Add(BipHelpers.MakeBipParam(-1006015, "CURVE_PARAM_STEEL_CANTILEVER", StorageType.Double, 0.0));
                    if (e.Parameters.All(p => p.Id != -1006014))
                        e.Parameters.Add(BipHelpers.MakeBipParam(-1006014, "CURVE_PARAM_CONCRETE_CANTILEVER", StorageType.Double, 0.0));
                    if (e.Parameters.All(p => p.Id != -1006016))
                        e.Parameters.Add(BipHelpers.MakeBipParam(-1006016, "ROOF_SLOPE", StorageType.Double, 1.0 / 12.0));
                }
                continue;
            }
            if (c == -2000051)
                AddIfMissing(e, -1004011, "CURVE_IS_DETAIL", 1);
            else if (c == -2000045)
                AddIfMissing(e, -1004011, "CURVE_IS_DETAIL", 0);
            else if (analyticalCats.Contains(c))
                AddIfMissing(e, -1013451, "ANALYTICAL_GEOMETRY_IS_VALID", 1);
            else if (c == -2000263)
            {
                AddIfMissing(e, -1006469, "SPOT_ELEV_DISPLAY_ELEVATIONS", 0);
                AddIfMissing(e, -1006441, "SPOT_ELEV_RELATIVE_BASE", -1);
                void AddStr(int id, string name)
                {
                    if (e.Parameters.All(p => p.Id != id))
                        e.Parameters.Add(BipHelpers.MakeBipParam(id, name, StorageType.String, "", hasNoRevitValue: true));
                }
                AddStr(-1006479, "SPOT_ELEV_SINGLE_OR_UPPER_SUFFIX");
                AddStr(-1006478, "SPOT_ELEV_SINGLE_OR_UPPER_PREFIX");
                AddStr(-1006481, "SPOT_ELEV_LOWER_SUFFIX");
                AddStr(-1006480, "SPOT_ELEV_LOWER_PREFIX");
            }
            else if (c == -2000261)
            {
                // Also DIM_LEADER=1, ref-count=2.
                if (!e.TypeFromNativePointer)
                {
                    foreach (var p in e.Parameters)
                        if (p.Id is -1002050 or -1002000 or -1002051 or -1002052)
                        { p.Value = 1412; p.StorageType = StorageType.ElementId; }
                    foreach (int pid in new[] { -1002050, -1002000, -1002051, -1002052 })
                        if (e.Parameters.All(p => p.Id != pid))
                            e.Parameters.Add(BipHelpers.MakeBipParam(pid, "", StorageType.ElementId, 1412));
                    e.TypeId = 1412;
                }
                AddIfMissing(e, -1004515, "DIM_LEADER", 1);
                AddIfMissing(e, -1006520, "DIM_REFERENCE_COUNT", 2);
            }
            else if (c == -2000011)
            {
                AddIfMissing(e, -1001117, "WALL_TOP_IS_ATTACHED", 0);
                AddIfMissing(e, -1001118, "WALL_BOTTOM_IS_ATTACHED", 0);
                AddIfMissing(e, -1001713, "RELATED_TO_MASS", 0);
                AddIfMissing(e, -1019100, "WALL_CROSS_SECTION", 1);
                AddIfMissing(e, -1001007, "WALL_ATTR_ROOM_BOUNDING", 1);
                if (e.Parameters.All(p => p.Id != -1012828))
                    e.Parameters.Add(BipHelpers.MakeBipParam(-1012828, "WALL_TOP_EXTENSION_DIST_PARAM", StorageType.Double, 0.0));
                if (e.Parameters.All(p => p.Id != -1012829))
                    e.Parameters.Add(BipHelpers.MakeBipParam(-1012829, "WALL_BOTTOM_EXTENSION_DIST_PARAM", StorageType.Double, 0.0));
            }
            else if (c == -2000032 || c == -2000035)
                AddIfMissing(e, -1001713, "RELATED_TO_MASS", 0);
            else if (curveOwner)
                AddIfMissing(e, -1004011, "CURVE_IS_DETAIL", 1);
        }
    }

    /// <summary>
    /// NODE_CONNECTION_STATUS for analytical nodes (Hub 0x0884 / ReferencePoint 0x0D4E pairs,
    /// OST_AnalyticalNodes) is topological: a node whose reference degree exceeds the single-member
    /// baseline is CONNECTED (2).
    /// </summary>
    private static void FixNodeConnectionStatus(RevitFileInfo info, ParsingState? state)
    {
        if (state == null || state.ElemClassIdx.Count == 0) return;
        var incoming = new Dictionary<int, int>();
        foreach (var e in info.Elements)
        {
            if (e.References == null) continue;
            foreach (var r in e.References)
            { incoming.TryGetValue(r, out int c); incoming[r] = c + 1; }
        }
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2009645 || e.Id <= 0) continue;
            if (!state.ElemClassIdx.TryGetValue((uint)e.Id, out var cls)) continue;
            if (e.Parameters.Any(p => p.Id == -1001597)) continue;
            int outd = e.References?.Count ?? 0;
            incoming.TryGetValue(e.Id, out int ind);
            int? status = null;
            if (cls == 0x0884) // Hub
            {
                if (ind >= 4 || outd >= 8) status = 2;
                else if (ind <= 2 && outd >= 1 && outd <= 4) status = 0;
            }
            else if (cls == 0x0D4E) // ReferencePoint
            {
                if (outd >= 14) status = 2;
            }
            if (status is int s)
                e.Parameters.Add(BipHelpers.MakeBipParam(-1001597, "NODE_CONNECTION_STATUS", StorageType.Integer, s));
        }
    }

    /// <summary>
    /// Emit placeholder Elements for ElemTable-known ids that have no decoded block, so they still
    /// exist in the output.
    /// </summary>
    private static void CreateElemTablePlaceholders(RevitFileInfo info, ParsingState? state)
    {
        if (state == null) return;
        var existing = new HashSet<int>(info.Elements.Count);
        foreach (var e in info.Elements) existing.Add(e.Id);

        // Model dominant phase (same derivation as FixUnresolvedPhaseCreated) for PHASE_CREATED.
        var phaseVotes = new Dictionary<int, int>();
        foreach (var e in info.Elements)
            foreach (var p in e.Parameters)
                if (p.Id == -1012100 && p.Value is int ph && ph > 0)
                { phaseVotes.TryGetValue(ph, out int n); phaseVotes[ph] = n + 1; break; }
        int domPhase = -1, domN = 0;
        foreach (var (ph, n) in phaseVotes) if (n > domN) { domPhase = ph; domN = n; }

        var src = new HashSet<uint>(state.ElemTable.Keys);
        foreach (var k in state.ElemWorksets.Keys) src.Add(k);
        foreach (var eid in src)
        {
            if (eid == 0 || eid >= 0x0F000000 || existing.Contains((int)eid)) continue;
            // Runs last, after all category/workset passes, so nothing else touches these.
            var el = new Element { Id = (int)eid, TypeId = 0 };
            el.Parameters.Add(BipHelpers.MakeBipParam(-1013201, "DESIGN_OPTION_ID", StorageType.ElementId, -1, hasNoRevitValue: true));
            el.Parameters.Add(BipHelpers.MakeBipParam(-1012101, "PHASE_DEMOLISHED", StorageType.ElementId, -1, hasNoRevitValue: true));
            if (domPhase > 0)
                el.Parameters.Add(BipHelpers.MakeBipParam(-1012100, "PHASE_CREATED", StorageType.ElementId, domPhase));
            info.Elements.Add(el);
        }
    }

    private static void FixWallBaseConstraint(RevitFileInfo info)
    {
        foreach (var e in info.Elements)
        {
            if (e.CategoryId != -2000011) continue; // OST_Walls
            int lvl = 0; Parameter? baseParam = null;
            foreach (var p in e.Parameters)
            {
                if (p.Id == -1001952 && p.Value is int lv && lv > 0) lvl = lv; // LEVEL_PARAM
                else if (p.Id == -1001107) baseParam = p; // WALL_BASE_CONSTRAINT
            }
            if (lvl <= 0) continue;
            if (baseParam == null)
                e.Parameters.Add(BipHelpers.MakeBipParam(-1001107, "WALL_BASE_CONSTRAINT", StorageType.ElementId, lvl));
            else if (baseParam.Value is not int bv || bv <= 0)
            { baseParam.Value = lvl; baseParam.StorageType = StorageType.ElementId; }
        }
    }

    /// <summary>
    /// Reconstruct sketch topology from decoded 0x0346 sketch-line curves: associate each sketch
    /// line with the element that owns its sketch (the one whose References list it appears in),
    /// set the line's <see cref="Element.HostId"/> to that owner.
    /// </summary>
    internal static void BuildSketchTopologyForTests(RevitFileInfo info, ParsingState state) =>
        BuildSketchTopology(info, state);

    private static void BuildSketchTopology(RevitFileInfo info, ParsingState? state)
    {
        if (state == null || state.SketchCurves.Count == 0) return;

        var sketchLineIds = new HashSet<int>();
        foreach (var k in state.SketchCurves.Keys)
            if (!state.DetailCurveStyles.ContainsKey(k)) // detail/model lines aren't sketch profiles
                sketchLineIds.Add((int)k);

        var byId = new Dictionary<int, Element>();
        foreach (var e in info.Elements) byId[e.Id] = e;

        // owner candidate -> the sketch lines it references (a sketch owner lists all its lines;
        // dimensions/constraints reference only the 1-2 lines they measure, so require >=3).
        var ownerLines = new Dictionary<int, HashSet<int>>();
        foreach (var e in info.Elements)
        {
            if (e.References == null || sketchLineIds.Contains(e.Id)) continue;
            HashSet<int>? set = null;
            foreach (var r in e.References)
                if (sketchLineIds.Contains(r)) (set ??= new HashSet<int>()).Add(r);
            if (set != null && set.Count >= 3) ownerLines[e.Id] = set;
        }

        // Each line's owner = the candidate referencing the most sketch lines.
        var lineOwner = new Dictionary<int, int>();
        foreach (var (owner, lines) in ownerLines)
            foreach (var ln in lines)
                if (!lineOwner.TryGetValue(ln, out var cur) || ownerLines[cur].Count < lines.Count)
                    lineOwner[ln] = owner;

        foreach (var (ln, owner) in lineOwner)
            if (byId.TryGetValue(ln, out var le)) le.HostId = owner;

        foreach (var (owner, lines) in ownerLines)
        {
            var ownLines = new List<int>();
            foreach (var ln in lines)
                if (lineOwner.TryGetValue(ln, out var o) && o == owner) ownLines.Add(ln);

            var loops = BuildSketchLoops(ownLines, byId);
            if (loops.Count > 0 && byId.TryGetValue(owner, out var oe))
                oe.SketchLoops = loops;
            foreach (var (lineId, key) in SketchLoopKeys(ownLines, byId))
                byId[lineId].SketchLoopKey = key;
        }

        // A line left without a key there takes it from the smallest candidate containing it, its
        // own sketch, when its loop closes simply in that set.
        var smallestOwner = new Dictionary<int, int>();
        foreach (var (owner, lines) in ownerLines)
            foreach (var ln in lines)
                if (!smallestOwner.TryGetValue(ln, out var cur)
                    || ownerLines[cur].Count > lines.Count
                    || ownerLines[cur].Count == lines.Count && owner < cur)
                    smallestOwner[ln] = owner;
        var linesBySmallestOwner = smallestOwner.GroupBy(entry => entry.Value)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Key).ToList());
        foreach (var (owner, ownLines) in linesBySmallestOwner)
        {
            if (ownLines.All(ln => !byId.TryGetValue(ln, out var line) || line.SketchLoopKey != null)) continue;
            foreach (var (lineId, key) in SketchLoopKeys(ownLines, byId))
                byId[lineId].SketchLoopKey ??= key;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("RVTIO_DBG_SKETCH"), out int dbgEid) && dbgEid != 0)
        {
            byId.TryGetValue(dbgEid, out var de);
            var refs = de?.References;
            int inSketch = refs?.Count(r => sketchLineIds.Contains(r)) ?? -1;
            DiagnosticSink.WriteLine($"  [sketch-dbg] {dbgEid}: found={de != null} refs={refs?.Count ?? -1} refsInSketchLines={inSketch} isOwner={ownerLines.ContainsKey(dbgEid)} loops={(de?.SketchLoops?.Count ?? 0)}");
            if (refs != null)
                foreach (var r in refs.Take(12))
                {
                    byId.TryGetValue(r, out var re);
                    int r2 = re?.References?.Count(x => sketchLineIds.Contains(x)) ?? -1;
                    DiagnosticSink.WriteLine($"  [sketch-dbg]   ref {r}: cat={re?.CategoryId} ownRefs={re?.References?.Count ?? -1} refsInSketchLines={r2} isOwner={ownerLines.ContainsKey(r)}");
                }
        }
    }

    /// <summary>
    /// The smallest line id of each simple closed loop among an owner's sketch lines: a connected
    /// set of lines (by shared endpoints) in which every endpoint is shared by exactly two line
    /// ends. Lines of branching or open sets get no key.
    /// </summary>
    internal static Dictionary<int, int> SketchLoopKeys(IEnumerable<int> lineIds, IReadOnlyDictionary<int, Element> byId)
    {
        static (long, long, long) Key(double[] p) =>
            ((long)Math.Round(p[0] * 1000), (long)Math.Round(p[1] * 1000), (long)Math.Round(p[2] * 1000));

        var ends = new Dictionary<int, ((long, long, long) A, (long, long, long) B)>();
        var lineEnds = new Dictionary<(long, long, long), List<int>>();
        foreach (int id in lineIds.Distinct())
        {
            if (!byId.TryGetValue(id, out var line) || line.Location?.StartPoint is not { Length: >= 3 } a
                || line.Location.EndPoint is not { Length: >= 3 } b)
                continue;
            var ka = Key(a); var kb = Key(b);
            if (ka == kb) continue;
            ends[id] = (ka, kb);
            foreach (var k in new[] { ka, kb })
            {
                if (!lineEnds.TryGetValue(k, out var list)) lineEnds[k] = list = new();
                list.Add(id);
            }
        }

        var keys = new Dictionary<int, int>();
        var seen = new HashSet<int>();
        foreach (int start in ends.Keys)
        {
            if (!seen.Add(start)) continue;
            var component = new List<int>();
            var pending = new Stack<int>();
            pending.Push(start);
            bool simple = true;
            while (pending.Count > 0)
            {
                int id = pending.Pop();
                component.Add(id);
                foreach (var k in new[] { ends[id].A, ends[id].B })
                {
                    var shared = lineEnds[k];
                    if (shared.Count != 2) simple = false;
                    foreach (int other in shared)
                        if (seen.Add(other)) pending.Push(other);
                }
            }
            if (!simple || component.Count < 2) continue;
            int key = component.Min();
            foreach (int id in component) keys[id] = key;
        }
        return keys;
    }

    /// <summary>Order a set of sketch-line curves into closed loops by walking shared endpoints.</summary>
    private static List<List<double[]>> BuildSketchLoops(List<int> lineIds, Dictionary<int, Element> byId)
    {
        static (long, long, long) Key(double[] p) =>
            ((long)Math.Round(p[0] * 1000), (long)Math.Round(p[1] * 1000), (long)Math.Round(p[2] * 1000));

        // vertex -> list of (lineId, this endpoint, other endpoint)
        var adj = new Dictionary<(long, long, long), List<(int id, double[] here, double[] other)>>();
        var segs = new Dictionary<int, (double[] a, double[] b)>();
        foreach (var id in lineIds)
        {
            if (!byId.TryGetValue(id, out var e) || e.Location?.StartPoint == null || e.Location.EndPoint == null) continue;
            var a = e.Location.StartPoint; var b = e.Location.EndPoint;
            segs[id] = (a, b);
            void Add(double[] from, double[] to)
            {
                var k = Key(from);
                if (!adj.TryGetValue(k, out var list)) adj[k] = list = new();
                list.Add((id, from, to));
            }
            Add(a, b); Add(b, a);
        }

        var loops = new List<List<double[]>>();
        var used = new HashSet<int>();
        foreach (var startId in segs.Keys)
        {
            if (used.Contains(startId)) continue;
            var (sa, sb) = segs[startId];
            var pts = new List<double[]> { sa, sb };
            used.Add(startId);
            var startKey = Key(sa);
            var curKey = Key(sb);
            for (int guard = 0; guard < lineIds.Count + 1; guard++)
            {
                if (curKey == startKey) break; // closed
                if (!adj.TryGetValue(curKey, out var cand)) break;
                int nextId = -1; double[]? nextOther = null;
                foreach (var (id, here, other) in cand)
                    if (!used.Contains(id)) { nextId = id; nextOther = other; break; }
                if (nextId < 0) break; // open chain
                used.Add(nextId);
                pts.Add(nextOther!);
                curKey = Key(nextOther!);
            }
            if (pts.Count >= 3) loops.Add(pts);
        }
        return loops;
    }

    /// <summary>Resolve ElementId-valued type-level params from the 0x0725 chain.</summary>
    private static void PropagateFamilyElementIdParams(RevitFileInfo rvtInfo, ParsingState? state)
    {
        if (state == null) return;
        // Real referencable element = has a meaningful Category. Param-def sentinels have Category
        // == null. Materials have TypeId=523 too, so we filter by Category only.
        var realElemIds = new HashSet<int>(
            rvtInfo.Elements
                .Where(x => !string.IsNullOrEmpty(x.Category))
                .Select(x => x.Id));

        // Combine two sources of {projKey -> elementIdValue}: 1) FamilyParamMaps chain pairs where
        // binKey is itself a real element. 2) FamilySymbolBlockElemIds - direct pair scan of 0x0725
        // block.
        var symToVals = new Dictionary<uint, Dictionary<int, int>>();
        foreach (var (symId, map) in state.FamilyParamMaps)
        {
            foreach (var (binKey, projKey) in map)
            {
                int bk = (int)binKey;
                int pk = (int)projKey;
                if (bk < 200 || pk < 200) continue;
                if (!realElemIds.Contains(bk)) continue;
                if (pk < 100_000) continue;
                if (!symToVals.TryGetValue(symId, out var v))
                    symToVals[symId] = v = new Dictionary<int, int>();
                if (!v.ContainsKey(pk)) v[pk] = bk;
            }
        }
        foreach (var (symId, pairs) in state.FamilySymbolBlockElemIds)
        {
            foreach (var (projKey, elemValue) in pairs)
            {
                if (!realElemIds.Contains(elemValue)) continue;
                if (!symToVals.TryGetValue(symId, out var v))
                    symToVals[symId] = v = new Dictionary<int, int>();
                v[projKey] = elemValue; // pair-scan results take priority
            }
        }
        if (symToVals.Count == 0) return;

        foreach (var e in rvtInfo.Elements)
        {
            uint typeCandidate = e.TypeId > 0 ? (uint)e.TypeId : 0u;
            var candidates = new List<uint>();
            if (e.References != null)
            {
                var seen = new HashSet<uint> { typeCandidate };
                foreach (var rid in e.References)
                {
                    if (rid <= 0 || rid == e.Id) continue;
                    uint urid = (uint)rid;
                    if (!seen.Add(urid)) continue;
                    if (symToVals.ContainsKey(urid)) candidates.Add(urid);
                }
            }
            if (typeCandidate != 0 && symToVals.ContainsKey(typeCandidate))
                candidates.Add(typeCandidate);
            if (candidates.Count == 0) continue;

            foreach (var symId in candidates)
            {
                if (!symToVals.TryGetValue(symId, out var vals)) continue;
                foreach (var (projKey, elemValue) in vals)
                {
                    var p = e.Parameters.FirstOrDefault(pp => pp.Id == projKey);
                    if (p == null) continue; // only fill existing params
                    bool isPlaceholder = p.Value == null
                        || (p.Value is int iv && (iv == 0 || iv == -1))
                        || (p.Value is string s && (string.IsNullOrEmpty(s) || s == "-1"));
                    if (!isPlaceholder) continue;
                    p.Value = elemValue;
                    p.StorageType = StorageType.ElementId;
                }
            }
        }
    }

    /// <summary>
    /// Final pass: coerce a parameter's StorageType/Value based on its ForgeId spec when the
    /// current storage type clearly disagrees.
    /// </summary>
    private static void CoerceParamStorageByForge(RevitFileInfo rvtInfo, ParsingState? state)
    {
        foreach (var e in rvtInfo.Elements)
        {
            foreach (var p in e.Parameters)
            {
                if (string.IsNullOrEmpty(p.ForgeId) && p.Id > 0 && state != null
                    && state.ForgeParamDefs.TryGetValue((uint)p.Id, out var def))
                {
                    p.ForgeId = def.ForgeId;
                    if (string.IsNullOrEmpty(p.Definition?.Name) && !string.IsNullOrEmpty(def.DisplayName))
                    {
                        p.Definition ??= new Definition { Id = p.Id };
                        p.Definition.Name = def.DisplayName;
                    }
                }
                var fid = p.ForgeId;
                if (string.IsNullOrEmpty(fid)) continue;
                bool isStringSpec = fid.Contains("spec.string") || fid.Contains("string:url");
                bool isBoolSpec = fid.Contains("spec.bool") || fid.Contains("spec:bool");
                bool isNumericSpec = fid.Contains("spec.aec:") || fid.Contains("spec.number")
                    || fid.Contains("spec:length") || fid.Contains("spec:area")
                    || fid.Contains("spec:volume") || fid.Contains("spec.int");

                bool isEmptyPlaceholder = p.Value == null
                    || (p.Value is string s && (string.IsNullOrEmpty(s) || s == "-1"))
                    || (p.Value is int iv && (iv == 0 || iv == -1))
                    || (p.Value is double dv && dv == 0.0);
                if (!isEmptyPlaceholder) continue;

                bool wasNoRevitValue = p.HasNoRevitValue
                    || p.Value == null
                    || (p.Value is string ps && ps == "-1")
                    || (p.Value is int piv && piv == -1);
                if (isStringSpec && p.StorageType != StorageType.String)
                {
                    p.StorageType = StorageType.String;
                    p.Value = "";
                    if (wasNoRevitValue) p.HasNoRevitValue = true;
                }
                else if (isBoolSpec && p.StorageType != StorageType.Integer)
                {
                    p.StorageType = StorageType.Integer;
                    p.Value = 0;
                    if (wasNoRevitValue) p.HasNoRevitValue = true;
                }
                else if (isNumericSpec && p.StorageType == StorageType.String)
                {
                    p.StorageType = StorageType.Double;
                    p.Value = 0.0;
                    if (wasNoRevitValue) p.HasNoRevitValue = true;
                }
            }
        }
    }

    private static void PropagateFamilySymbolBlockDoubles(RevitFileInfo rvtInfo, ParsingState? state)
    {
        if (state == null || state.FamilySymbolBlockDoubles.Count == 0) return;
        foreach (var e in rvtInfo.Elements)
        {
            // Iterate ALL candidate symbol blocks for this element (TypeId + shadow refs). Shadow
            // blocks are visited AFTER the type so instance-specific values override type defaults.
            var typeCandidate = e.TypeId > 0 ? (uint)e.TypeId : 0u;
            var shadowCandidates = new List<uint>();
            if (e.References != null)
            {
                var seen = new HashSet<uint> { typeCandidate };
                foreach (var rid in e.References)
                {
                    if (rid <= 0 || rid == e.Id) continue;
                    uint urid = (uint)rid;
                    if (!seen.Add(urid)) continue;
                    if (state.FamilySymbolBlockDoubles.ContainsKey(urid))
                        shadowCandidates.Add(urid);
                }
            }
            // Apply in order: SHADOWS FIRST (instance-specific values win), THEN type defaults fill
            // any remaining gaps. This way the shadow's full value (e.g. 0.984) wins over the
            // type's half-default (0.4921) for the same projKey.
            var allCandidates = new List<uint>();
            allCandidates.AddRange(shadowCandidates);
            if (typeCandidate != 0 && state.FamilySymbolBlockDoubles.ContainsKey(typeCandidate))
                allCandidates.Add(typeCandidate);
            if (allCandidates.Count == 0) continue;

            foreach (var symId in allCandidates)
            {
                if (!state.FamilySymbolBlockDoubles.TryGetValue(symId, out var typeDoubles)) continue;
                foreach (var (projKey, value) in typeDoubles)
                {
                    // Skip 0.0 entries - they carry no info and would crush genuine Integer/String
                    // values from other extractors.
                    if (value == 0.0) continue;
                    int pid = (int)projKey;
                    var p = e.Parameters.FirstOrDefault(pp => pp.Id == pid);
                    if (p == null)
                    {
                        string? forgeId = null, dn = null;
                        Guid? guid = null;
                        if (state.ForgeParamDefs.TryGetValue(projKey, out var def))
                        { forgeId = def.ForgeId; dn = def.DisplayName; }
                        if (state.ParamDefGuids.TryGetValue(projKey, out var binGuid)) guid = binGuid;
                        else guid = BipHelpers.ParseGuidFromForgeId(forgeId);
                        e.Parameters.Add(new Parameter
                        {
                            Id = pid,
                            StorageType = StorageType.Double,
                            Value = value,
                            IsReadOnly = false,
                            ForgeId = forgeId,
                            Guid = guid,
                            Definition = new Definition { Id = pid, Name = dn },
                        });
                    }
                    else
                    {
                        // Only fill in true placeholders. Never crush real string/double values.
                        bool isPlaceholder = p.Value == null
                            || (p.Value is double d0 && d0 == 0.0)
                            || (p.Value is int i0 && (i0 == 0 || i0 == -1))
                            || (p.Value is string s0 && (string.IsNullOrEmpty(s0) || s0 == "-1"));
                        if (isPlaceholder && p.StorageType != StorageType.String)
                        {
                            p.Value = value;
                            p.StorageType = StorageType.Double;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Apply type-level integer values extracted from the family symbol's 0x0725 block. Only fills
    /// in params that ALREADY have Integer storage type (e.g. added by PropagateTypeElementIdParams
    /// with Integer type from name heuristic).
    /// </summary>
    private static void PropagateFamilySymbolBlockInts(RevitFileInfo rvtInfo, ParsingState? state)
    {
        if (state == null || state.FamilySymbolBlockInts.Count == 0) return;
        foreach (var e in rvtInfo.Elements)
        {
            uint typeCandidate = e.TypeId > 0 ? (uint)e.TypeId : 0u;
            var candidates = new List<uint>();
            if (typeCandidate != 0 && state.FamilySymbolBlockInts.ContainsKey(typeCandidate))
                candidates.Add(typeCandidate);
            if (e.References != null)
            {
                var seen = new HashSet<uint> { typeCandidate };
                foreach (var rid in e.References)
                {
                    if (rid <= 0 || rid == e.Id) continue;
                    uint urid = (uint)rid;
                    if (!seen.Add(urid)) continue;
                    if (state.FamilySymbolBlockInts.ContainsKey(urid))
                        candidates.Add(urid);
                }
            }
            if (candidates.Count == 0) continue;

            foreach (var symId in candidates)
            {
                bool isInstanceSpecific = symId != typeCandidate;
                if (!state.FamilySymbolBlockInts.TryGetValue(symId, out var typeInts)) continue;
                foreach (var (projKey, value) in typeInts)
                {
                    if (value == 0 && !isInstanceSpecific) continue; // type defaults of 0 are noise
                    var p = e.Parameters.FirstOrDefault(pp => pp.Id == projKey);
                    if (p == null) continue; // don't ADD new params here; only fill in existing
                    if (p.StorageType != StorageType.Integer && p.StorageType != StorageType.None) continue;
                    bool isPlaceholder = p.Value == null
                        || (p.Value is int iv && (iv == 0 || iv == -1));
                    if (!isPlaceholder && !isInstanceSpecific) continue;
                    p.Value = value;
                    if (p.StorageType == StorageType.None) p.StorageType = StorageType.Integer;
                }
            }
        }
    }

    /// <summary>
    /// Apply type-level string values extracted from the family symbol's 0x0725 block. Fills in
    /// params whose current value is "" (placeholder default) or sentinel.
    /// </summary>
    private static void PropagateFamilySymbolBlockStrings(RevitFileInfo rvtInfo, ParsingState? state)
    {
        if (state == null || state.FamilySymbolBlockStrings.Count == 0) return;
        foreach (var e in rvtInfo.Elements)
        {
            var candidates = new List<uint>();
            uint typeCandidate = e.TypeId > 0 ? (uint)e.TypeId : 0u;
            if (typeCandidate != 0 && state.FamilySymbolBlockStrings.ContainsKey(typeCandidate))
                candidates.Add(typeCandidate);
            if (e.References != null)
            {
                var seen = new HashSet<uint> { typeCandidate };
                foreach (var rid in e.References)
                {
                    if (rid <= 0 || rid == e.Id) continue;
                    uint urid = (uint)rid;
                    if (!seen.Add(urid)) continue;
                    if (state.FamilySymbolBlockStrings.ContainsKey(urid))
                        candidates.Add(urid);
                }
            }
            if (candidates.Count == 0) continue;
            foreach (var symId in candidates)
            {
                bool isInstanceSpecific = symId != typeCandidate;
                if (!state.FamilySymbolBlockStrings.TryGetValue(symId, out var typeStrings)) continue;
                ApplyTypeStrings(e, typeStrings, state, isInstanceSpecific);
            }
        }
    }

    private static void ApplyTypeStrings(Element e, Dictionary<int, string> typeStrings,
        ParsingState state, bool isInstanceSpecific)
    {
        foreach (var (projKey, value) in typeStrings)
        {
            var p = e.Parameters.FirstOrDefault(pp => pp.Id == projKey);
            if (p == null)
            {
                string? forgeId = null, dn = null;
                Guid? guid = null;
                if (projKey > 0 && state.ForgeParamDefs.TryGetValue((uint)projKey, out var def))
                { forgeId = def.ForgeId; dn = def.DisplayName; }
                if (projKey > 0 && state.ParamDefGuids.TryGetValue((uint)projKey, out var binGuid)) guid = binGuid;
                else guid = BipHelpers.ParseGuidFromForgeId(forgeId);
                if (dn == null && projKey < 0 && Lookup.BipNames.Lookup.TryGetValue(projKey, out var bipName))
                    dn = bipName;
                e.Parameters.Add(new Parameter
                {
                    Id = projKey,
                    StorageType = StorageType.String,
                    Value = value,
                    IsReadOnly = projKey < 0 && BipHelpers.IsBipReadOnly(dn),
                    ForgeId = forgeId,
                    Guid = guid,
                    Definition = new Definition
                    {
                        Id = projKey,
                        Name = dn,
                        BuiltInParameter = projKey < 0 ? dn : null,
                    },
                });
            }
            else
            {
                // Override placeholder "" / -1 / null. Shadow (instance-specific) values also
                // override non-placeholder type defaults.
                bool isPlaceholder = p.Value == null;
                if (p.Value is string s) isPlaceholder = string.IsNullOrEmpty(s) || s == "-1";
                if (p.Value is int iv) isPlaceholder = iv == -1;
                if (isPlaceholder || isInstanceSpecific)
                {
                    p.Value = value;
                    p.StorageType = StorageType.String;
                }
            }
        }
    }

    /// <summary>
    /// For each FamilyInstance, walk the symbol-type's references; references whose TypeId points
    /// to ANOTHER real project element (not a Tf-class index) are param defs where the parameter
    /// value IS that TypeId.
    /// </summary>
    private static void PropagateTypeElementIdParams(RevitFileInfo rvtInfo, ParsingState? state)
    {
        if (state == null) return;
        var elemById = rvtInfo.Elements.ToDictionary(e => e.Id);
        foreach (var e in rvtInfo.Elements)
        {
            if (e.TypeId <= 0 || !elemById.TryGetValue(e.TypeId, out var typeElem)) continue;
            if (typeElem.References == null) continue;
            var existing = new HashSet<int>(e.Parameters.Select(p => p.Id));
            foreach (var defId in typeElem.References)
            {
                if (defId <= 0 || existing.Contains(defId) || !state.IsParameterElement((uint)defId)) continue;
                if (!elemById.TryGetValue(defId, out var defElem)) continue;
                int dt = defElem.TypeId;
                if (dt == defId) continue; // self-ref

                // Pattern 1: defElem.TypeId is another real project element -> that's the param
                // value. (Used for enum-list family params.)
                if (dt > 1000 && elemById.TryGetValue(dt, out _))
                {
                    bool looksLikeParamDef =
                        defElem.Category == "OST_Entourage"
                        || defElem.Category == "OST_GenericAnnotation"
                        || (defElem.Category == null && !string.IsNullOrEmpty(defElem.Name));
                    if (looksLikeParamDef)
                    {
                        e.Parameters.Add(new Parameter
                        {
                            Id = defId,
                            StorageType = StorageType.ElementId,
                            Value = dt,
                            IsReadOnly = false,
                            Definition = new Definition
                            {
                                Id = defId,
                                Name = !string.IsNullOrEmpty(defElem.Name) ? defElem.Name : null,
                            },
                        });
                        existing.Add(defId);
                        continue;
                    }
                }

                // Add as placeholder with type inferred from the param-def name. Prefer the
                // authoritative DisplayName from ForgeParamDefs over defElem.Name (the latter often
                // holds a value/garbage).
                if (dt <= 0 && defElem.Category == null)
                {
                    string? forgeId = null, dn = null;
                    Guid? guid = null;
                    if (state.ForgeParamDefs.TryGetValue((uint)defId, out var def))
                    { forgeId = def.ForgeId; dn = def.DisplayName; }
                    if (state.ParamDefGuids.TryGetValue((uint)defId, out var binGuid)) guid = binGuid;
                    else guid = BipHelpers.ParseGuidFromForgeId(forgeId);

                    string? typeHintName = dn ?? defElem.Name;
                    if (string.IsNullOrEmpty(typeHintName) || !IsParamDefName(typeHintName)) continue;

                    // Use ForgeId spec for typing when available; otherwise heuristic from name.
                    StorageType st; object val;
                    if (forgeId != null && state.ForgeParamSpecs.TryGetValue(forgeId, out var spec))
                    {
                        if (spec.Contains("bool") || spec.Contains("int"))
                        { st = StorageType.Integer; val = 0; }
                        else if (spec.Contains("number") || spec.Contains("length")
                              || spec.Contains("area") || spec.Contains("volume")
                              || spec.Contains("currency") || spec.StartsWith("autodesk.spec.aec"))
                        { st = StorageType.Double; val = 0.0; }
                        else if (spec.Contains("material") || spec.Contains("reference"))
                        { st = StorageType.ElementId; val = -1; }
                        else { st = StorageType.String; val = ""; }
                    }
                    else
                    {
                        (st, val) = InferTypeFromName(typeHintName);
                    }
                    e.Parameters.Add(new Parameter
                    {
                        Id = defId,
                        StorageType = st,
                        Value = val,
                        HasNoRevitValue = true,
                        IsReadOnly = false,
                        ForgeId = forgeId,
                        Guid = guid,
                        Definition = new Definition { Id = defId, Name = dn ?? typeHintName },
                    });
                    existing.Add(defId);
                }
            }
        }
    }

    /// <summary>
    /// Heuristic: does the string look like a Revit family parameter name? True if it doesn't look
    /// like a verbose description (no spaces > certain length, no period inside a sentence) - it's
    /// a short identifier-like name.
    /// </summary>
    private static bool IsParamDefName(string name)
    {
        if (name.Length > 60) return false; // descriptions are long
        if (name.Count(c => c == '.') > 1) return false;
        if (name.EndsWith('.')) return false; // sentences end with period
        return true;
    }

    /// <summary>Infer storage type + default value from Russian-language param def name.</summary>
    private static (StorageType, object) InferTypeFromName(string name)
    {
        string nl = name.ToLowerInvariant();
        // Localized names that contain a depth-like substring but should be String (handled
        // FIRST so they don't fall into Double bucket)
        if (nl.Contains("дата") || nl.Contains("статус") || nl.Contains("раздел")
            || nl.Contains("создатель") || nl.Contains("примечан") || nl.Contains("марка")
            || nl.Contains("позиция") || nl.Contains("единицы измерения")
            || nl.Contains("префикс") || nl.Contains("формула")
            || nl.Contains("составное имя") || nl.Contains("уровень") || nl.Contains("уровни")
            || nl.Contains("сокращение системы") || nl.Contains("имя системы")
            || nl.Contains("точк") || nl.Contains("ид ") || nl.Contains("id ")
            || nl.Contains("уникальный id") || nl.Contains("уник id") || nl.Contains("уник. id")
            || nl.Contains("версия") || nl.Contains("код изделия")
            || nl.Contains("категория основы") || nl.Contains("относительная отметка")
            || nl.Contains("автоглубин") == false && nl.Contains("имя ")
            || nl.Contains("прорезано") || nl.Contains("файла")
            || nl.Contains("imen") || nl.Contains("текст"))
            return (StorageType.String, "");
        // Bool-like: switches, flags
        if (nl.Contains("вкл") || nl.Contains("выкл") || nl.Contains("выключ")
            || nl.Contains("автоглубин") || nl.Contains("круглое") || nl.Contains("ручн")
            || nl.Contains("ось") || (nl.StartsWith("обозначение_") && nl.EndsWith("_тип"))
            || nl.Contains("ниша") || nl.Contains("удален") || nl.Contains("устаревш")
            || nl.Contains("согласован") || nl.Contains("исключить")
            || nl.Contains("в перекрыти") || nl.Contains("в стене")
            || nl.Contains("элементы_управ"))
            return (StorageType.Integer, 0);
        // Doubles: dimensions, areas
        if (nl.Contains("глубин") || nl.Contains("толщин") || nl.Contains("длин")
            || nl.Contains("ширин") || nl.Contains("высот") || nl.Contains("диаметр")
            || nl.Contains("площад") || nl.Contains("объем")
            || nl.Contains("отметка") || nl.Contains("корректировка")
            || nl.Contains("кругвопрос") || nl.Contains("количество") || nl.Contains("расч_"))
            return (StorageType.Double, 0.0);
        // ElementId: material/type references
        if (nl.Contains("материал") || nl.Contains("согласование")
            || nl.StartsWith("тип") || nl.StartsWith("обозначение "))
            return (StorageType.ElementId, -1);
        // Default: string
        return (StorageType.String, "");
    }

    private static void PropagateFamilyTypeValues(RevitFileInfo rvtInfo, ParsingState? state)
    {
        if (state == null || state.FamilyTypeValues.Count == 0) return;
        foreach (var e in rvtInfo.Elements)
        {
            if (e.TypeId <= 0) continue;
            if (!state.FamilyTypeValues.TryGetValue((uint)e.TypeId, out var typeVals)) continue;
            // Chain map remaps the binKey (family-local) to the project param def ID, same as for
            // instance 0x0702 values.
            state.FamilyParamMaps.TryGetValue((uint)e.TypeId, out var binToProj);

            foreach (var (binKey, value) in typeVals)
            {
                // Remap binKey -> projKey via chain. Fall back to binKey if not mapped (some
                // type-level entries may use direct IDs for params not in the chain).
                uint projKey = binToProj != null && binToProj.TryGetValue(binKey, out uint pk) ? pk : binKey;
                int pid = (int)projKey;

                var existingParam = e.Parameters.FirstOrDefault(p => p.Id == pid);
                if (existingParam != null)
                {
                    // Only fill in if existing value is a sentinel (null / -1 / "" / 0.0)
                    bool isSentinel = existingParam.Value == null;
                    if (existingParam.Value is string s) isSentinel = string.IsNullOrEmpty(s) || s == "-1";
                    if (existingParam.Value is int iv) isSentinel = iv == -1 || iv == 0;
                    if (existingParam.Value is double dv) isSentinel = dv == 0.0;
                    if (!isSentinel) continue;
                    existingParam.Value = value;
                    if (existingParam.StorageType == StorageType.None || existingParam.StorageType == StorageType.String)
                    {
                        if (value is double) existingParam.StorageType = StorageType.Double;
                        else if (value is int) existingParam.StorageType = StorageType.Integer;
                        else existingParam.StorageType = StorageType.String;
                    }
                    continue;
                }
                string? forgeId = null, displayName = null;
                Guid? guid = null;
                if (state.ForgeParamDefs.TryGetValue(projKey, out var def))
                { forgeId = def.ForgeId; displayName = def.DisplayName; }
                if (state.ParamDefGuids.TryGetValue(projKey, out var binGuid)) guid = binGuid;
                else guid = BipHelpers.ParseGuidFromForgeId(forgeId);
                e.Parameters.Add(new Parameter
                {
                    Id = pid,
                    StorageType = value is string ? StorageType.String
                        : value is double ? StorageType.Double
                        : StorageType.None,
                    Value = value,
                    IsReadOnly = false,
                    ForgeId = forgeId,
                    Guid = guid,
                    Definition = new Definition { Id = pid, Name = displayName },
                });
            }
        }
    }

    /// <summary>
    /// Enrich elements with type-level BuiltInParameters that aren't extracted from the binary
    /// directly but can be inferred from References + Category: STRUCTURAL_MATERIAL_PARAM from any
    /// OST_Materials reference; CLEAR_COVER_OTHER/INTERIOR/EXTERIOR from OST_CoverType references.
    /// </summary>
    private struct RefScan
    {
        public int? MaterialId, CoverTypeId, FillPatternId, SecondLevelId;
        public int FirstLevelId;
    }

    private static void EnrichTypeLevelBips(RevitFileInfo rvtInfo, ParsingState? state)
    {
        if (state == null) return;
        var elemById = rvtInfo.Elements.ToDictionary(e => e.Id);

        // Per-instance: scan refs + type's refs together to find type-level params.
        IEnumerable<int> CollectRefs(Element e)
        {
            if (e.TypeId > 0 && elemById.TryGetValue(e.TypeId, out var t) && t.References != null)
                foreach (var r in t.References) yield return r;
            if (e.References != null) foreach (var r in e.References) yield return r;
        }
        var typePhases = new Dictionary<int, int?>();
        var typeRefSets = new Dictionary<int, HashSet<int>>();
        var typeScans = new Dictionary<(int TypeId, bool IsWall), RefScan>();
        void Accumulate(ref RefScan scan, int rid, int ownerId, bool isWall)
        {
            if (rid == ownerId || rid <= 0) return;
            if (!elemById.TryGetValue(rid, out var refElem)) return;
            switch (refElem.CategoryId)
            {
                case -2000700: scan.MaterialId ??= rid; break; // OST_Materials
                case -2009014: scan.CoverTypeId ??= rid; break; // OST_CoverType
                case -2000081: scan.FillPatternId ??= rid; break; // OST_FillPatterns
                case -2000240: // OST_Levels - first is base, second is top
                    if (scan.FirstLevelId == -1) scan.FirstLevelId = rid;
                    else if (scan.SecondLevelId == null && rid != scan.FirstLevelId) scan.SecondLevelId = rid;
                    break;
            }
            // Fallback: detect cover type / fill pattern by Category string when ID doesn't match
            if (scan.CoverTypeId == null && refElem.Category == "OST_CoverType") scan.CoverTypeId = rid;
            if (scan.FillPatternId == null && refElem.Category == "OST_FillPatterns") scan.FillPatternId = rid;
            // Fill pattern often has no category in our output but its name is the pattern name
            // (for example concrete or hatch names) - accept refs in the right ID range as candidates.
            if (scan.FillPatternId == null && refElem.Category == null && rid > 100 && rid < 10000
                && !string.IsNullOrEmpty(refElem.Name) && isWall)
                scan.FillPatternId = rid;
        }
        RefScan ScanRefs(Element e, bool isWall)
        {
            var scan = new RefScan { FirstLevelId = -1 };
            if (e.TypeId > 0 && elemById.TryGetValue(e.TypeId, out var t) && t.References != null)
            {
                if (!typeScans.TryGetValue((e.TypeId, isWall), out var typeScan))
                {
                    typeScan = new RefScan { FirstLevelId = -1 };
                    foreach (var r in t.References) Accumulate(ref typeScan, r, int.MinValue, isWall);
                    typeScans[(e.TypeId, isWall)] = typeScan;
                }
                // A type reference equal to the instance's own id is skipped in the full scan.
                if (!typeRefSets.TryGetValue(e.TypeId, out var typeRefSet))
                    typeRefSets[e.TypeId] = typeRefSet = new HashSet<int>(t.References);
                if (typeRefSet.Contains(e.Id)) typeScan = RescanSkipping(t.References, e.Id, isWall);
                scan = typeScan;
            }
            if (e.References != null) foreach (var r in e.References) Accumulate(ref scan, r, e.Id, isWall);
            return scan;
        }
        RefScan RescanSkipping(IEnumerable<int> refs, int skipId, bool isWall)
        {
            var scan = new RefScan { FirstLevelId = -1 };
            foreach (var r in refs) Accumulate(ref scan, r, skipId, isWall);
            return scan;
        }

        void EnsureBip(Element e, int id, string name, StorageType st, object value, bool isApproximate = false, bool hasNoRevitValue = false)
        {
            if (e.Parameters.Any(p => p.Id == id)) return;
            var parameter = BipHelpers.MakeBipParam(id, name, st, value, hasNoRevitValue: hasNoRevitValue);
            parameter.IsApproximate = isApproximate;
            e.Parameters.Add(parameter);
        }

        // Like EnsureBip but ALSO overrides existing sentinel (-) OR existing-wrong-id values.
        // expectedCategoryId optionally filters: existing value must be of that category, else
        // override.
        void EnsureBipOrFixNull(Element e, int id, string name, StorageType st, int value,
            int? expectedCategoryId = null)
        {
            var existing = e.Parameters.FirstOrDefault(p => p.Id == id);
            if (existing == null)
            {
                e.Parameters.Add(BipHelpers.MakeBipParam(id, name, st, value));
                return;
            }
            if (existing.Value is not int iv) { existing.Value = value; return; }
            bool isSentinel = iv == -1 || iv == 0;
            bool wrongCategory = expectedCategoryId.HasValue
                && elemById.TryGetValue(iv, out var ivElem)
                && ivElem.CategoryId != expectedCategoryId.Value;
            bool unknownElement = !elemById.ContainsKey(iv);
            if ((isSentinel || wrongCategory || unknownElement) && value > 0)
                existing.Value = value;
        }

        foreach (var e in rvtInfo.Elements)
        {
            if (e.CategoryId == null) continue;
            int cat = e.CategoryId.Value;
            bool isWall = cat == -2000011;

            // Fix type-related BIPs to use the resolved TypeId (set during PostProcess).
            // AddComputedElementParams runs before TypeId resolution and stores the Tf-class index,
            // so we need to overwrite here once the real TypeId is known.
            if (e.TypeId > 0 && elemById.ContainsKey(e.TypeId))
            {
                foreach (int pid in new[] { -1002050, -1002000, -1002052, -1002051 })
                {
                    var p = e.Parameters.FirstOrDefault(pp => pp.Id == pid);
                    if (p == null) continue;
                    if (p.Value is int iv && iv > 0 && elemById.ContainsKey(iv) && iv != e.TypeId)
                    {
                        // Override only when the current value references a WRONG-category element
                        // AND e.TypeId references a genuine SAME-category type (a real
                        // improvement).
                        if (elemById[iv].CategoryId != e.CategoryId
                            && elemById.TryGetValue(e.TypeId, out var etElem)
                            && etElem.CategoryId == e.CategoryId) p.Value = e.TypeId;
                    }
                    else if (p.Value is int iv2 && (iv2 <= 0 || !elemById.ContainsKey(iv2)))
                    {
                        p.Value = e.TypeId;
                    }
                }
            }

            var refScan = ScanRefs(e, isWall);
            int? materialId = refScan.MaterialId, coverTypeId = refScan.CoverTypeId,
                fillPatternId = refScan.FillPatternId, secondLevelId = refScan.SecondLevelId;
            int firstLevelId = refScan.FirstLevelId;

            if (materialId.HasValue)
                EnsureBipOrFixNull(e, -1005500, "STRUCTURAL_MATERIAL_PARAM", StorageType.ElementId, materialId.Value, -2000700);
            if (coverTypeId.HasValue)
            {
                EnsureBipOrFixNull(e, -1013437, "CLEAR_COVER_OTHER", StorageType.ElementId, coverTypeId.Value, -2009014);
                EnsureBipOrFixNull(e, -1013435, "CLEAR_COVER_EXTERIOR", StorageType.ElementId, coverTypeId.Value, -2009014);
                EnsureBipOrFixNull(e, -1013436, "CLEAR_COVER_INTERIOR", StorageType.ElementId, coverTypeId.Value, -2009014);
            }
            if (fillPatternId.HasValue)
                EnsureBipOrFixNull(e, -1002106, "COARSE_SCALE_FILL_PATTERN_ID_PARAM", StorageType.ElementId, fillPatternId.Value);

            if (isWall)
            {
                // WALL_HEIGHT_TYPE: top constraint level
                if (secondLevelId.HasValue)
                    EnsureBipOrFixNull(e, -1001103, "WALL_HEIGHT_TYPE", StorageType.ElementId, secondLevelId.Value, -2000240);

                // FUNCTION_PARAM from extracted compound structure data on the type
                if (e.TypeId > 0 && state.WallTypeFunctions.TryGetValue((uint)e.TypeId, out int fn))
                    EnsureBip(e, -1001006, "FUNCTION_PARAM", StorageType.Integer, fn);

                // Wall default-ish int/string BIPs
                EnsureBip(e, -1002111, "WRAPPING_AT_ENDS_PARAM", StorageType.Integer, 0, hasNoRevitValue: true);
                EnsureBip(e, -1002112, "WRAPPING_AT_INSERTS_PARAM", StorageType.Integer, 0, hasNoRevitValue: true);
                EnsureBip(e, -1002110, "COARSE_SCALE_FILL_PATTERN_COLOR", StorageType.Integer, 0, hasNoRevitValue: true);
                EnsureBip(e, -1002103, "WALL_STRUCTURE_ID_PARAM", StorageType.None, -1, hasNoRevitValue: true);

                // WALL_ATTR_ROOM_BOUNDING (-1001007): physical walls default to "room bounding" = 1
                var rbParam = e.Parameters.FirstOrDefault(p => p.Id == -1001007);
                if (rbParam == null)
                    EnsureBip(e, -1001007, "WALL_ATTR_ROOM_BOUNDING", StorageType.Integer, 1);
                else if (rbParam.Value is int iv && iv == 0) rbParam.Value = 1; // override extracted 0

                // STRUCTURAL_ANALYTICAL_MODEL (-1001552): walls default to 1 when structural
                // significant
                var samParam = e.Parameters.FirstOrDefault(p => p.Id == -1001552);
                bool isStructural = e.Parameters.Any(p => p.Id == -1001596 && p.Value is int i && i == 1);
                if (samParam == null)
                    EnsureBip(e, -1001552, "STRUCTURAL_ANALYTICAL_MODEL", StorageType.Integer, isStructural ? 1 : 0);
                else if (samParam.Value is int iv && iv == 0 && isStructural) samParam.Value = 1;
            }

            // FAMILY_LEVEL_PARAM = first OST_Levels reference (the "Level" field for
            // FamilyInstances).
            if (firstLevelId > 0)
            {
                var flp = e.Parameters.FirstOrDefault(p => p.Id == -1001352);
                if (flp == null)
                    e.Parameters.Add(BipHelpers.MakeBipParam(-1001352, "FAMILY_LEVEL_PARAM", StorageType.ElementId, firstLevelId));
                else if (flp.Value is int iv && (iv <= 0 || !elemById.ContainsKey(iv)
                        || (elemById.TryGetValue(iv, out var lvlElem) && lvlElem.CategoryId != -2000240)))
                    flp.Value = firstLevelId;

                // SCHEDULE_LEVEL_PARAM mirrors FAMILY_LEVEL_PARAM for FamilyInstances
                if (!e.ScheduleLevelFromStoredFields)
                    EnsureBipOrFixNull(e, -1002062, "SCHEDULE_LEVEL_PARAM", StorageType.ElementId, firstLevelId, -2000240);
            }
            else
            {
                // No level - emit sentinel so the param exists
                EnsureBip(e, -1001352, "FAMILY_LEVEL_PARAM", StorageType.ElementId, -1, hasNoRevitValue: true);
            }

            // PHASE_CREATED / PHASE_DEMOLISHED: phase IDs from references (OST_Phases = -2000112)
            int? phaseCreatedId = null;
            if (e.TypeId > 0 && elemById.TryGetValue(e.TypeId, out var phaseType) && phaseType.References != null)
            {
                if (!typePhases.TryGetValue(e.TypeId, out var typePhase))
                {
                    typePhase = null;
                    foreach (var rid in phaseType.References)
                        if (elemById.TryGetValue(rid, out var refElem) && refElem.CategoryId == -2000112) { typePhase = rid; break; }
                    typePhases[e.TypeId] = typePhase;
                }
                phaseCreatedId = typePhase;
            }
            if (phaseCreatedId == null && e.References != null)
                foreach (var rid in e.References)
                    if (elemById.TryGetValue(rid, out var refElem) && refElem.CategoryId == -2000112) { phaseCreatedId = rid; break; }
            if (phaseCreatedId.HasValue)
                EnsureBipOrFixNull(e, -1012100, "PHASE_CREATED", StorageType.ElementId, phaseCreatedId.Value, -2000112);
            else
                EnsureBip(e, -1012100, "PHASE_CREATED", StorageType.ElementId, -1, hasNoRevitValue: true);

            // HOST_ID_PARAM: id of the host element. For FamilyInstances we use HostId if resolved,
            // else -1 (Revit returns -1 for free-standing instances).
            int hostId = e.HostId ?? -1;
            if (e.CategoryId is -2000171 or -2000170)
            {
                foreach (var rid in CollectRefs(e))
                    if (elemById.TryGetValue(rid, out var hw) && hw.CategoryId == -2000011) { hostId = rid; break; }
            }
            EnsureBip(e, -1002108, "HOST_ID_PARAM", StorageType.ElementId, hostId, hasNoRevitValue: hostId <= 0);

            // INSTANCE_FREE_HOST_PARAM: human-readable host label "Level: <levelName>" (localized). Applies
            // to free-standing family instances where host is the level.
            if (firstLevelId > 0 && elemById.TryGetValue(firstLevelId, out var lvlE) && !string.IsNullOrEmpty(lvlE.Name))
                EnsureBip(e, -1001363, "INSTANCE_FREE_HOST_PARAM", StorageType.String, $"Уровень : {lvlE.Name}");

            // INSTANCE_ELEVATION_PARAM: instance elevation relative to its level. bbox.MinZ -
            // level.elev. (LEVEL_ELEV is propagated through ElemBipDoubles as BIP -1007102.)
            // Override existing -1 sentinel.
            if (GeometryBox(e) is { Length: 6 } && firstLevelId > 0
                && elemById.TryGetValue(firstLevelId, out var lvlEl)
                && lvlEl.Parameters.FirstOrDefault(p => p.Id == -1007102)?.Value is double lvlElev)
            {
                // Only when the level's elevation actually resolved - assuming 0 for an unresolved
                // level produced garbage elevations (= raw bbox z).
                double minZ = GeometryBox(e)![2];
                double elev = Math.Round(minZ - lvlElev, 12);
                var iepExisting = e.Parameters.FirstOrDefault(p => p.Id == -1001360);
                if (iepExisting == null)
                    e.Parameters.Add(BipHelpers.MakeBipParam(-1001360, "INSTANCE_ELEVATION_PARAM", StorageType.Double, elev));
                else
                {
                    // Override -1 sentinel; preserve explicitly stored values that aren't sentinels
                    if (iepExisting.Value is double iev && iev == -1.0) iepExisting.Value = elev;
                    else if (iepExisting.Value is int ievi && ievi == -1) iepExisting.Value = elev;
                    if (iepExisting.StorageType == StorageType.None) iepExisting.StorageType = StorageType.Double;
                }
            }

            // INSTANCE_OFFSET_POS_PARAM: face-flip flag, defaults to 0
            EnsureBip(e, -1001370, "INSTANCE_OFFSET_POS_PARAM", StorageType.Integer, 0, hasNoRevitValue: true);

            // INSTANCE_FREE_HOST_OFFSET_PARAM = INSTANCE_ELEVATION_PARAM for free-host instances
            var iep = e.Parameters.FirstOrDefault(p => p.Id == -1001360);
            if (iep?.Value is double iepVal)
                EnsureBip(e, -1001364, "INSTANCE_FREE_HOST_OFFSET_PARAM", StorageType.Double, iepVal);

            // FAMILY_FREEINST_DEFAULT_ELEVATION: type-level default elevation, usually 0
            EnsureBip(e, -1154647, "FAMILY_FREEINST_DEFAULT_ELEVATION", StorageType.Double, 0.0, hasNoRevitValue: true);

            // INSTANCE_SILL_HEIGHT_PARAM: for wall-hosted inserts the stored 0x08BE value
            // (extracted in BlockParser) is authoritative.
            if (e.Transform?.Origin is { Length: 3 } org)
            {
                double lvlElev2 = 0;
                if (firstLevelId > 0 && elemById.TryGetValue(firstLevelId, out var lvlForSill))
                {
                    var lp2 = lvlForSill.Parameters.FirstOrDefault(p => p.Id == -1007102);
                    if (lp2?.Value is double lv2) lvlElev2 = lv2;
                }
                double computedSill = Math.Round(org[2] - lvlElev2, 12);
                var sillExisting = e.Parameters.FirstOrDefault(p => p.Id == -1001361);
                if (sillExisting == null)
                    e.Parameters.Add(BipHelpers.MakeBipParam(-1001361, "INSTANCE_SILL_HEIGHT_PARAM", StorageType.Double, computedSill));
                else if (e.HostId is null or <= 0)
                    sillExisting.Value = computedSill; // free instance: stored value is a 0 placeholder
            }

            // Wall- and instance-common ANALYTICAL_* BIPs propagated from the structural material.
            // For walls, ANALYTICAL_THERMAL_RESISTANCE = thickness /
            // ANALYTICAL_HEAT_TRANSFER_COEFFICIENT.
            if (materialId.HasValue && elemById.TryGetValue(materialId.Value, out var matElem))
            {
                // Propagate well-known material doubles to instance as ANALYTICAL_* BIPs. Most
                // materials have these as PHY_MATERIAL_PARAM_* via reference propagation; we just
                // copy them here under the canonical ANALYTICAL_* names.
                double? thermalMass = null, thickness = null;
                foreach (var mp in matElem.Parameters)
                {
                    if (mp.Value is not double mv) continue;
                    if (mp.Id == -1140309) thermalMass = mv * 1e6; // unit weight scaled - approximation
                }
                if (isWall)
                {
                    var widthP = e.Parameters.FirstOrDefault(p => p.Id == -1001000);
                    if (widthP?.Value is double w) thickness = w;
                }
                // Legacy thermal estimates: retain their values for compatibility, but do not label
                // them as exact own RVT parameters.
                if (isWall)
                {
                    EnsureBip(e, -1005430, "ANALYTICAL_HEAT_TRANSFER_COEFFICIENT", StorageType.Double, 3.4866666666666668, isApproximate: true);
                    EnsureBip(e, -1005436, "ANALYTICAL_ROUGHNESS", StorageType.Integer, 3, isApproximate: true);
                    EnsureBip(e, -1005435, "ANALYTICAL_ABSORPTANCE", StorageType.Double, 0.7, isApproximate: true);
                    EnsureBip(e, -1005434, "ANALYTICAL_THERMAL_MASS", StorageType.Double, 453330.0, isApproximate: true);
                    if (thickness.HasValue)
                        EnsureBip(e, -1005431, "ANALYTICAL_THERMAL_RESISTANCE", StorageType.Double,
                            thickness.Value / 3.4866666666666668, isApproximate: true);
                }
            }

            // Common string/scalar defaults Revit always returns. Unset String params read back
            // from Revit as an empty string "" (has_value=true), NOT a -1 sentinel.
            EnsureBip(e, -1010103, "ALL_MODEL_DESCRIPTION", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1010104, "ALL_MODEL_URL", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1010105, "ALL_MODEL_TYPE_COMMENTS", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1010106, "ALL_MODEL_INSTANCE_COMMENTS", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1010108, "ALL_MODEL_MANUFACTURER", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1010109, "ALL_MODEL_MODEL", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1001203, "ALL_MODEL_MARK", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1140422, "KEYNOTE_PARAM", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1002500, "UNIFORMAT_CODE", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1002501, "UNIFORMAT_DESCRIPTION", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1002502, "OMNICLASS_CODE", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1002503, "OMNICLASS_DESCRIPTION", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1152384, "ALL_MODEL_TYPE_IMAGE", StorageType.ElementId, -1, hasNoRevitValue: true);
            EnsureBip(e, -1002002, "ALL_MODEL_FAMILY_NAME", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1002001, "SYMBOL_NAME_PARAM", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1001405, "WINDOW_TYPE_ID", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1001206, "FIRE_RATING", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1005556, "STRUCTURAL_FAMILY_CODE_NAME", StorageType.String, "", hasNoRevitValue: true);
            EnsureBip(e, -1001205, "ALL_MODEL_COST", StorageType.Double, 0.0, hasNoRevitValue: true); // (a.k.a. legacy DOOR_COST)
        }
    }

    /// <summary>
    /// For each instance, ensure all family chain entries have a corresponding parameter on the
    /// instance. If the entry's projKey isn't in the instance's parameters, add it as an empty
    /// placeholder.
    /// </summary>
    private static void PropagateChainPlaceholders(RevitFileInfo rvtInfo, ParsingState? state)
    {
        if (state == null || state.FamilyParamMaps.Count == 0) return;
        var elemById = rvtInfo.Elements.ToDictionary(e => e.Id);
        foreach (var e in rvtInfo.Elements)
        {
            if (e.TypeId <= 0 || !state.FamilyParamMaps.TryGetValue((uint)e.TypeId, out var map))
                continue;
            // Each chain entry produces a (binKey, projKey) mapping; the projKey is the actual
            // project param def ID. Ensure it exists on the instance with the right type.
            foreach (var projKey in map.Values.Distinct())
            {
                int pid = (int)projKey;
                var existingParam = e.Parameters.FirstOrDefault(p => p.Id == pid);

                string? forgeId = null, displayName = null;
                Guid? guid = null;
                if (state.ForgeParamDefs.TryGetValue(projKey, out var def))
                { forgeId = def.ForgeId; displayName = def.DisplayName; }
                if (state.ParamDefGuids.TryGetValue(projKey, out var binGuid)) guid = binGuid;
                else guid = BipHelpers.ParseGuidFromForgeId(forgeId);

                StorageType st = StorageType.String;
                object value = "";
                string? spec = null;
                if (forgeId != null) state.ForgeParamSpecs.TryGetValue(forgeId, out spec);
                if (spec != null)
                {
                    if (spec.Contains("bool") || spec.Contains("int"))
                    { st = StorageType.Integer; value = 0; }
                    else if (spec.StartsWith("autodesk.spec.aec:")
                          || spec.StartsWith("autodesk.spec.aec.")
                          || spec.Contains("number") || spec.Contains("length")
                          || spec.Contains("area") || spec.Contains("volume")
                          || spec.Contains("currency"))
                    { st = StorageType.Double; value = 0.0; }
                    else if (spec.Contains("material") || spec.Contains("reference")
                          || spec.Contains("category"))
                    { st = StorageType.ElementId; value = -1; }
                    else { st = StorageType.String; value = ""; }
                }
                else if (displayName != null)
                {
                    // Heuristic by display name (Russian Revit family params)
                    string nl = displayName.ToLowerInvariant();
                    if (nl.Contains("удален") || nl.Contains("ниша") || nl.Contains("вкл")
                        || nl.Contains("устаревш") || nl.Contains("согласован")
                        || nl.Contains("исключить") || nl.Contains("ручн")
                        || nl.Contains("круглое") || nl.Contains("ось") || nl.Contains("кругл")
                        || nl.Contains("автоглубин") || nl.Contains("элементы_управ")
                        || nl.Contains("в перекрыти") || nl.Contains("в стене")
                        || nl.StartsWith("обозначение_") && nl.EndsWith("_тип"))
                    { st = StorageType.Integer; value = 0; }
                    else if (nl.Contains("глубин") || nl.Contains("толщин") || nl.Contains("длин")
                        || nl.Contains("ширин") || nl.Contains("высот") || nl.Contains("диаметр")
                        || nl.Contains("площад") || nl.Contains("объем")
                        || nl.Contains("отметка") || nl.Contains("корректировка")
                        || nl.Contains("крутвопрос") || nl.Contains("количество")
                        || nl.Contains("расч_"))
                    { st = StorageType.Double; value = 0.0; }
                    else if (nl.Contains("материал") || nl.StartsWith("тип") || nl.StartsWith("обозначение "))
                    { st = StorageType.ElementId; value = -1; }
                }

                if (existingParam == null)
                {
                    e.Parameters.Add(new Parameter
                    {
                        Id = pid,
                        StorageType = st,
                        Value = value,
                        HasNoRevitValue = true,
                        IsReadOnly = false,
                        ForgeId = forgeId,
                        Guid = guid,
                        Definition = new Definition { Id = pid, Name = displayName },
                    });
                }
                else
                {
                    // Upgrade existing param if its current value/storage is the wrong shape: - ""
                    // string when we now know it's Integer/Double/ElementId - -1/null for any type
                    // with a better default
                    bool isEmptyStr = existingParam.Value is string s && string.IsNullOrEmpty(s);
                    bool isNull = existingParam.Value == null;
                    bool isMinusOne = existingParam.Value is int iv && iv == -1;
                    if ((isEmptyStr || isNull || isMinusOne) && st != StorageType.String)
                    {
                        bool wasNoRevitValue = existingParam.HasNoRevitValue || isNull || isMinusOne;
                        existingParam.StorageType = st;
                        existingParam.Value = value;
                        if (wasNoRevitValue) existingParam.HasNoRevitValue = true;
                    }
                    // Also fill in missing metadata
                    if (existingParam.ForgeId == null) existingParam.ForgeId = forgeId;
                    if (existingParam.Guid == null) existingParam.Guid = guid;
                    if (existingParam.Definition == null || string.IsNullOrEmpty(existingParam.Definition.Name))
                        existingParam.Definition = new Definition { Id = pid, Name = displayName };
                }
            }
        }
    }

    /// <summary>
    /// Compute extruded solid geometry for wall elements. Uses world-coordinate GRep face data when
    /// available (the largest vertical face gives wall direction, position, length, and height).
    /// </summary>
    private static void ComputeWallSolids(List<Element> elements)
    {
        foreach (var elem in elements)
        {
            if (elem.Category != "OST_Walls") continue;

            double[]? bbox = elem.BoundingBox ?? elem.Geometry?.BoundingBox;
            if (bbox == null || bbox.Length < 6) continue;

            double dx = Math.Abs(bbox[3] - bbox[0]);
            double dy = Math.Abs(bbox[4] - bbox[1]);
            double dz = Math.Abs(bbox[5] - bbox[2]);
            if (dx < 1e-6 || dy < 1e-6 || dz < 1e-6) continue;

            bool wallAlongX = dx >= dy;
            double wallLength = wallAlongX ? dx : dy;
            double wallWidth = wallAlongX ? dy : dx;

            foreach (var p in elem.Parameters)
            {
                if (p.Definition?.Name == "WALL_ATTR_WIDTH_PARAM"
                    && p.StorageType == StorageType.Double && p.Value is double wFt)
                {
                    if (wFt > 0.001 && wFt < wallLength)
                        wallWidth = wFt;
                    break;
                }
            }

            double cx = (bbox[0] + bbox[3]) / 2;
            double cy = (bbox[1] + bbox[4]) / 2;
            double z0 = bbox[2];
            double dirX = wallAlongX ? 1 : 0;
            double dirY = wallAlongX ? 0 : 1;
            bool hasWorldDir = false;

            // Universal approach: extract direction and position from world-coordinate faces. The
            // GRep block with world-coordinate faces has XVectors that show the actual wall
            // direction in world space (not axis-aligned).
            if (elem.Geometry?.Faces != null)
            {
                GeoFace? bestFace = null;
                double bestArea = 0;

                foreach (var face in elem.Geometry.Faces)
                {
                    if (Math.Abs(face.Normal[2]) > 0.5) continue;
                    if (face.Envelope == null || face.Envelope.Length < 4) continue;

                    double uLen = face.Envelope[2] - face.Envelope[0];
                    double vLen = face.Envelope[3] - face.Envelope[1];
                    double area = uLen * vLen;

                    double ax = Math.Abs(face.XVector[0]), ay = Math.Abs(face.XVector[1]);
                    bool isWorldFace = ax > 0.01 && ay > 0.01;

                    if (isWorldFace && area > bestArea)
                    {
                        bestFace = face;
                        bestArea = area;
                    }
                }

                if (bestFace != null)
                {
                    double fx = bestFace.XVector[0], fy = bestFace.XVector[1];
                    double fLen = Math.Sqrt(fx * fx + fy * fy);
                    if (fLen > 0.01)
                    {
                        dirX = fx / fLen;
                        dirY = fy / fLen;
                        hasWorldDir = true;

                        double midU = (bestFace.Envelope![0] + bestFace.Envelope[2]) / 2;
                        cx = bestFace.Origin[0] + midU * bestFace.XVector[0];
                        cy = bestFace.Origin[1] + midU * bestFace.XVector[1];

                        // Adjust from face surface to wall centerline (wallWidth/2 in -Normal
                        // direction)
                        double nx = bestFace.Normal[0], ny = bestFace.Normal[1];
                        cx -= (wallWidth / 2) * nx;
                        cy -= (wallWidth / 2) * ny;

                        // Wall length and height from envelope
                        double uLen = bestFace.Envelope[2] - bestFace.Envelope[0];
                        double vLen = bestFace.Envelope[3] - bestFace.Envelope[1];

                        // Determine which envelope dimension is length vs height by checking which
                        // face vector is horizontal
                        if (Math.Abs(bestFace.YVector[2]) > 0.5)
                        {
                            // YVector is vertical -> uLen=wall length, vLen=wall height
                            wallLength = uLen;
                            dz = vLen;
                        }
                        else
                        {
                            wallLength = vLen;
                            dz = uLen;
                        }

                        // z0 = wall base (minimum Z from face)
                        z0 = bestFace.Origin[2] + Math.Min(
                            bestFace.Envelope[1] * bestFace.YVector[2],
                            bestFace.Envelope[3] * bestFace.YVector[2]);
                    }
                }

                // Fallback: use GRep Transform if available (contains world coordinate system)
                // Transform layout: [dirX,dirY,0,originX, perpX,perpY,0,originY, 0,0,1,originZ] The
                // origin (xf[3], xf[7], xf[11]) is a world-space point from the face data.
                if (!hasWorldDir && elem.Geometry.Transform != null)
                {
                    var xf = elem.Geometry.Transform;
                    double tx = xf[0], ty = xf[1];
                    double tLen = Math.Sqrt(tx * tx + ty * ty);
                    if (tLen > 0.01 && Math.Abs(tx) > 0.02 && Math.Abs(ty) > 0.02)
                    {
                        dirX = tx / tLen;
                        dirY = ty / tLen;
                        hasWorldDir = true;

                        // The transform origin is a world point on the wall face. For the wall
                        // center, we need to find where the face center maps to in world.
                        GeoFace? largestFace = null;
                        double largestArea = 0;
                        foreach (var face in elem.Geometry.Faces)
                        {
                            if (Math.Abs(face.Normal[2]) > 0.5) continue;
                            if (face.Envelope == null) continue;
                            double a = (face.Envelope[2] - face.Envelope[0]) * (face.Envelope[3] - face.Envelope[1]);
                            if (a > largestArea) { largestFace = face; largestArea = a; }
                        }

                        if (largestFace?.Envelope != null)
                        {
                            // Face center in local coords
                            double localMidU = (largestFace.Envelope[0] + largestFace.Envelope[2]) / 2;
                            double localMidV = (largestFace.Envelope[1] + largestFace.Envelope[3]) / 2;
                            double lx = largestFace.Origin[0] + localMidU * largestFace.XVector[0];
                            double ly = largestFace.Origin[1] + localMidU * largestFace.XVector[1];

                            // Bbox center in local
                            double bx = (bbox[0] + bbox[3]) / 2;
                            double by = (bbox[1] + bbox[4]) / 2;

                            // The local face center offset from bbox center, rotated to world
                            double dx2 = lx - bx, dy2 = ly - by;
                            double worldDx = dx2 * dirX - dy2 * dirY;
                            double worldDy = dx2 * dirY + dy2 * dirX;

                            // World center = transform origin (a point on wall) + adjustment
                            // Transform origin is xf[3], xf[7] which is a world face vertex. The
                            // face origin in local is at one corner of the face.
                            double faceDx = localMidU * largestFace.XVector[0];
                            double faceDy = localMidU * largestFace.XVector[1];
                            double worldFaceDx = faceDx * dirX - faceDy * dirY;
                            double worldFaceDy = faceDx * dirY + faceDy * dirX;

                            cx = xf[3] + worldFaceDx;
                            cy = xf[7] + worldFaceDy;

                            double uLen = largestFace.Envelope[2] - largestFace.Envelope[0];
                            double vLen = largestFace.Envelope[3] - largestFace.Envelope[1];
                            if (Math.Abs(largestFace.YVector[2]) > 0.5)
                            { wallLength = uLen; dz = vLen; }
                            else
                            { wallLength = vLen; dz = uLen; }
                        }

                        z0 = bbox[2];
                    }
                }
            }

            var solid = new GeoSolid
            {
                Position = [cx, cy, z0],
                RefDirection = [dirX, dirY, 0],
                ExtrusionDirection = [0, 0, 1],
                ProfileWidth = wallLength,
                ProfileDepth = wallWidth,
                ExtrusionHeight = dz,
            };

            if (elem.Geometry?.Faces != null)
            {
                var openings = new List<GeoOpening>();
                foreach (var face in elem.Geometry.Faces)
                {
                    if (face.InnerVertices == null || face.InnerVertices.Count == 0) continue;

                    // Only faces perpendicular to wall thickness direction
                    double normalComp;
                    if (hasWorldDir)
                    {
                        // Perpendicular to wall direction = (-dirY, dirX)
                        normalComp = Math.Abs(face.Normal[0] * (-dirY) + face.Normal[1] * dirX);
                    }
                    else
                    {
                        normalComp = wallAlongX ? Math.Abs(face.Normal[1]) : Math.Abs(face.Normal[0]);
                    }
                    if (normalComp < 0.5) continue;

                    foreach (var innerVerts in face.InnerVertices)
                    {
                        if (innerVerts.Count < 4) continue;

                        double[] v0 = innerVerts[0], v1 = innerVerts[1], v3 = innerVerts[3];

                        double openWidth = Math.Sqrt(
                            (v1[0] - v0[0]) * (v1[0] - v0[0]) +
                            (v1[1] - v0[1]) * (v1[1] - v0[1]) +
                            (v1[2] - v0[2]) * (v1[2] - v0[2]));
                        double openHeight = Math.Sqrt(
                            (v3[0] - v0[0]) * (v3[0] - v0[0]) +
                            (v3[1] - v0[1]) * (v3[1] - v0[1]) +
                            (v3[2] - v0[2]) * (v3[2] - v0[2]));

                        if (openWidth < 0.01 || openHeight < 0.01) continue;

                        // Opening center in world coords
                        double wcx = (innerVerts[0][0] + innerVerts[1][0] + innerVerts[2][0] + innerVerts[3][0]) / 4;
                        double wcy = (innerVerts[0][1] + innerVerts[1][1] + innerVerts[2][1] + innerVerts[3][1]) / 4;
                        double wcz = (innerVerts[0][2] + innerVerts[1][2] + innerVerts[2][2] + innerVerts[3][2]) / 4;

                        double localX = (wcx - cx) * dirX + (wcy - cy) * dirY;
                        double localZ = wcz - z0;

                        bool dup = false;
                        foreach (var existing in openings)
                        {
                            if (Math.Abs(existing.Position[0] - localX) < 0.05
                                && Math.Abs(existing.Position[2] - localZ) < 0.05)
                            {
                                dup = true;
                                break;
                            }
                        }
                        if (dup) continue;

                        openings.Add(new GeoOpening
                        {
                            Position = [localX, 0, localZ],
                            Width = openWidth,
                            Height = openHeight,
                        });
                    }
                }

                if (openings.Count > 0)
                    solid.Openings = openings;
            }

            elem.Geometry ??= new GeometryData();
            elem.Geometry.Solids = [solid];
        }
    }

    /// <summary>Build UniqueId for an element. Revit UniqueId format: {episodeGUID}-{elemId:x8}</summary>
    private static string? BuildUniqueId(uint elemId, ParsingState state)
    {
        if (state.EpisodeGuids.Count > 0
            && state.ElemEpisodeNumbers.TryGetValue(elemId, out int episodeNum)
            && episodeNum >= 0 && episodeNum < state.EpisodeGuids.Count)
        {
            var episodeGuid = state.EpisodeGuids[episodeNum];
            if (!string.IsNullOrEmpty(episodeGuid))
                return $"{episodeGuid}-{elemId:x8}";
        }

        // Fallback to document GUID
        if (state.DocGuid != null)
            return $"{state.DocGuid}-{elemId:x8}";

        return null;
    }


    private static void AddBodyBipParams(Element elem, ElementRecord rec, uint elemId, ParsingState state)
    {
        if (rec.BipParams.Count == 0) return;
        var seenIds = new HashSet<int>();
        foreach (var b in rec.BipParams)
        {
            if (b.BipId == 0 || !seenIds.Add(b.BipId)) continue;
            var param = new Parameter
            {
                Id = b.BipId,
                StorageType = b.BipId < 0 ? BipHelpers.BipStorageType(b.BipName) : StorageType.None,
            };

            if (b.BipId < 0)
            {
                param.Definition = new Definition { Id = b.BipId, BuiltInParameter = b.BipName, Name = b.BipName };
                param.IsReadOnly = BipHelpers.IsBipReadOnly(b.BipName);
                bool resolvedStored = false;
                if (param.StorageType == StorageType.Double
                    && state.ElemBipDoubles.TryGetValue(elemId, out var bipDoubleVals)
                    && bipDoubleVals.TryGetValue(b.BipId, out double dv))
                { param.Value = dv; resolvedStored = true; }
                else if ((param.StorageType == StorageType.ElementId || param.StorageType == StorageType.Integer)
                    && state.ElemBipInts.TryGetValue(elemId, out var bipIntVals)
                    && bipIntVals.TryGetValue(b.BipId, out int iv))
                { param.Value = iv; resolvedStored = true; }
                else if (param.StorageType == StorageType.None
                    && state.ElemBipDoubles.TryGetValue(elemId, out var bipDblFallback)
                    && bipDblFallback.TryGetValue(b.BipId, out double dvFallback))
                {
                    param.StorageType = StorageType.Double;
                    param.Value = dvFallback;
                    resolvedStored = true;
                }
                else
                {
                    if (param.Value == null && rec.References.Count > 0
                        && (param.StorageType == StorageType.Double || param.StorageType == StorageType.None))
                    {
                        foreach (var refId in rec.References)
                        {
                            if (refId == elemId) continue;
                            if (state.ElemBipDoubles.TryGetValue(refId, out var refBipDbls)
                                && refBipDbls.TryGetValue(b.BipId, out double refDv))
                            {
                                if (param.StorageType == StorageType.None) param.StorageType = StorageType.Double;
                                param.Value = refDv;
                                resolvedStored = true;
                                break;
                            }
                        }
                    }
                    if (param.Value == null && rec.References.Count > 0
                        && (param.StorageType == StorageType.Integer || param.StorageType == StorageType.ElementId))
                    {
                        foreach (var refId in rec.References)
                        {
                            if (refId == elemId) continue;
                            if (state.ElemBipInts.TryGetValue(refId, out var refBipInts)
                                && refBipInts.TryGetValue(b.BipId, out int refIv))
                            {
                                param.Value = refIv;
                                resolvedStored = true;
                                break;
                            }
                        }
                    }
                    if (param.Value == null)
                        param.Value = b.Value.HasValue ? (object)b.Value.Value : null;
                }
                if (!resolvedStored
                    && (param.Value == null
                        || param.StorageType == StorageType.None
                        || (b.Value == -1 && (param.StorageType != StorageType.Integer || b.BipId is >= -1019017 and <= -1019012))))
                    param.HasNoRevitValue = true;
            }
            else
            {
                string? forgeId = null, displayName = null;
                Guid? paramGuid = null;
                if (state.ForgeParamDefs.TryGetValue((uint)b.BipId, out var def))
                { forgeId = def.ForgeId; displayName = def.DisplayName; }
                if (state.ParamDefGuids.TryGetValue((uint)b.BipId, out var binGuid))
                    paramGuid = binGuid;
                else
                    paramGuid = BipHelpers.ParseGuidFromForgeId(forgeId);
                param.ForgeId = forgeId;
                param.Guid = paramGuid;
                param.Definition = new Definition { Id = b.BipId, Name = displayName };
                param.IsReadOnly = !rec.WritableParamIds.Contains(b.BipId);
                if (state.ElemBipDoubles.TryGetValue(elemId, out var userDblVals)
                    && userDblVals.TryGetValue(b.BipId, out double userDbl))
                { param.StorageType = StorageType.Double; param.Value = userDbl; }
                else if (state.ElemParamStrings.TryGetValue(elemId, out var paramStrMap)
                    && paramStrMap.TryGetValue((uint)b.BipId, out string? strVal))
                { param.StorageType = StorageType.String; param.Value = strVal; }
                else
                {
                    param.StorageType = b.Value.HasValue ? StorageType.Integer : StorageType.None;
                    param.Value = b.Value.HasValue ? (object)b.Value.Value : null;
                    if (!b.Value.HasValue || b.Value.Value == -1) param.HasNoRevitValue = true;
                }
            }
            elem.Parameters.Add(param);
        }
    }

    private static void AddUserParams(Element elem, ElementRecord rec, uint elemId, ParsingState state)
    {
        if (!state.ElemParamStrings.TryGetValue(elemId, out var paramStrMap)) return;
        var existingIds = new HashSet<int>(elem.Parameters.Select(p => p.Id));
        foreach (var (paramDefId, strVal) in paramStrMap)
        {
            if (existingIds.Contains((int)paramDefId)) continue;
            string? forgeId = null, displayName = null;
            Guid? paramGuid = null;
            if (state.ForgeParamDefs.TryGetValue(paramDefId, out var def))
            { forgeId = def.ForgeId; displayName = def.DisplayName; }
            if (state.ParamDefGuids.TryGetValue(paramDefId, out var binGuid))
                paramGuid = binGuid;
            else
                paramGuid = BipHelpers.ParseGuidFromForgeId(forgeId);
            elem.Parameters.Add(new Parameter
            {
                Id = (int)paramDefId,
                StorageType = StorageType.String,
                Value = strVal,
                IsReadOnly = !rec.WritableParamIds.Contains((int)paramDefId),
                ForgeId = forgeId,
                Guid = paramGuid,
                Definition = new Definition { Id = (int)paramDefId, Name = displayName },
            });
        }
    }

    private static void AddBipDoubles(Element elem, uint elemId, ParsingState state)
    {
        if (!state.ElemBipDoubles.TryGetValue(elemId, out var bipDblMap)) return;
        var existing = new HashSet<int>(elem.Parameters.Select(p => p.Id));
        foreach (var (bipKey, bipVal) in bipDblMap)
        {
            if (existing.Contains(bipKey)) continue;
            if (bipKey < 0)
            {
                BipNames.Lookup.TryGetValue(bipKey, out string? bipName);
                elem.Parameters.Add(new Parameter
                {
                    Id = bipKey, StorageType = StorageType.Double, Value = bipVal,
                    IsReadOnly = BipHelpers.IsBipReadOnly(bipName),
                    Definition = new Definition { Id = bipKey, BuiltInParameter = bipName, Name = bipName },
                });
            }
            else
            {
                string? forgeId = null, displayName = null;
                Guid? paramGuid = null;
                if (state.ForgeParamDefs.TryGetValue((uint)bipKey, out var def))
                { forgeId = def.ForgeId; displayName = def.DisplayName; }
                if (state.ParamDefGuids.TryGetValue((uint)bipKey, out var binGuid))
                    paramGuid = binGuid;
                else
                    paramGuid = BipHelpers.ParseGuidFromForgeId(forgeId);
                elem.Parameters.Add(new Parameter
                {
                    Id = bipKey, StorageType = StorageType.Double, Value = bipVal,
                    ForgeId = forgeId, Guid = paramGuid,
                    Definition = new Definition { Id = bipKey, Name = displayName },
                });
            }
        }
    }

    private static void AddBipInts(Element elem, uint elemId, ParsingState state)
    {
        if (!state.ElemBipInts.TryGetValue(elemId, out var bipIntMap)) return;
        var existing = new HashSet<int>(elem.Parameters.Select(p => p.Id));
        foreach (var (bipKey, bipVal) in bipIntMap)
        {
            if (existing.Contains(bipKey)) continue;
            if (bipKey < 0)
            {
                BipNames.Lookup.TryGetValue(bipKey, out string? bipName);
                elem.Parameters.Add(new Parameter
                {
                    Id = bipKey, StorageType = BipHelpers.BipStorageType(bipName), Value = bipVal,
                    IsReadOnly = BipHelpers.IsBipReadOnly(bipName),
                    Definition = new Definition { Id = bipKey, BuiltInParameter = bipName, Name = bipName },
                });
            }
            else
            {
                // User integer parameter (positive key = param definition element ID)
                string? forgeId = null, displayName = null;
                Guid? paramGuid = null;
                if (state.ForgeParamDefs.TryGetValue((uint)bipKey, out var def))
                { forgeId = def.ForgeId; displayName = def.DisplayName; }
                if (state.ParamDefGuids.TryGetValue((uint)bipKey, out var binGuid))
                    paramGuid = binGuid;
                else
                    paramGuid = BipHelpers.ParseGuidFromForgeId(forgeId);
                elem.Parameters.Add(new Parameter
                {
                    Id = bipKey, StorageType = StorageType.Integer, Value = bipVal,
                    ForgeId = forgeId, Guid = paramGuid,
                    Definition = new Definition { Id = bipKey, Name = displayName },
                });
            }
        }
    }

    private static void AddPropagatedBipDoubles(Element elem, uint elemId, ParsingState state)
    {
        if (!state.Records.TryGetValue(elemId, out var rec) || rec.References.Count == 0) return;
        var existing = new HashSet<int>(elem.Parameters.Select(p => p.Id));
        foreach (var refId in rec.References)
        {
            if (refId == elemId) continue;
            if (!state.ElemBipDoubles.TryGetValue(refId, out var refBipDbls)) continue;
            foreach (var (bipKey, bipVal) in refBipDbls)
            {
                if (existing.Contains(bipKey)) continue;
                existing.Add(bipKey);
                if (bipKey < 0)
                {
                    BipNames.Lookup.TryGetValue(bipKey, out string? bipName);
                    elem.Parameters.Add(new Parameter
                    {
                        Id = bipKey, StorageType = StorageType.Double, Value = bipVal,
                        IsReadOnly = BipHelpers.IsBipReadOnly(bipName),
                        Definition = new Definition { Id = bipKey, BuiltInParameter = bipName, Name = bipName },
                    });
                }
                else
                {
                    // Don't propagate user params (positive IDs) from referenced elements - they
                    // belong to the referenced element, not the current one. User params are only
                    // valid when extracted directly from 0x0702 blocks.
                }
            }
        }
    }

    private static void AddBaseFields(Element elem, uint elemId, ParsingState state)
    {
        if (!state.ElemBaseFields.TryGetValue(elemId, out var bf)) return;
        var existing = new HashSet<int>(elem.Parameters.Select(p => p.Id));
        if (bf.AssocLevelId != -1 && !existing.Contains(-1001952))
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1001952, "LEVEL_PARAM", StorageType.ElementId, bf.AssocLevelId));
        if (!existing.Contains(-1002051))
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1002051, "ELEM_FAMILY_PARAM", StorageType.ElementId, bf.FamId));
        if (bf.CreatedPhaseId > 0 && !existing.Contains(-1012100))
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1012100, "PHASE_CREATED", StorageType.ElementId, bf.CreatedPhaseId));
        if (bf.DemolishedPhaseId > 0 && !existing.Contains(-1012101))
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1012101, "PHASE_DEMOLISHED", StorageType.ElementId, bf.DemolishedPhaseId));
        if (!existing.Contains(-1013201))
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1013201, "DESIGN_OPTION_ID", StorageType.ElementId, bf.BaseDesignOptionId));
    }

    /// <summary>
    /// Add computed params from element fields: ELEM_TYPE_PARAM, ELEM_CATEGORY_PARAM,
    /// SYMBOL_ID_PARAM, etc. These parameters exist in Revit but are computed at runtime from
    /// element properties.
    /// </summary>
    private static void AddComputedElementParams(Element elem, uint elemId, uint typeId, ParsingState state)
    {
        var existing = new HashSet<int>(elem.Parameters.Select(p => p.Id));

        // ElemTable contains no type link. ELEM_TYPE_PARAM / ELEM_FAMILY_AND_TYPE_PARAM /
        // SYMBOL_ID_PARAM / ELEM_FAMILY_PARAM must reference real type/family elements (id >
        // 100000).

        // PHASE_CREATED (-1012100), PHASE_DEMOLISHED (-1012101) - defaults if not set
        if (!existing.Contains(-1012101))
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1012101, "PHASE_DEMOLISHED", StorageType.ElementId, -1, hasNoRevitValue: true));

        // DESIGN_OPTION_ID (-1013201) - default -1 (main model)
        if (!existing.Contains(-1013201))
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1013201, "DESIGN_OPTION_ID", StorageType.ElementId, -1, hasNoRevitValue: true));

        if (!existing.Contains(-1140362) && elem.CategoryId.HasValue)
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1140362, "ELEM_CATEGORY_PARAM", StorageType.ElementId, elem.CategoryId.Value, hasNoRevitValue: elem.CategoryId.Value == -1));

        if (!existing.Contains(-1140363) && elem.CategoryId.HasValue)
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1140363, "ELEM_CATEGORY_PARAM_MT", StorageType.ElementId, elem.CategoryId.Value, hasNoRevitValue: elem.CategoryId.Value == -1));

        if (!existing.Contains(-1152385))
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1152385, "ALL_MODEL_IMAGE", StorageType.ElementId, -1, hasNoRevitValue: true));

        // Wall-specific default BIPs
        bool isWall = elem.CategoryId == -2000011;
        if (isWall)
        {
            if (!existing.Contains(-1001107))
            {
                var levelParam = elem.Parameters.FirstOrDefault(p => p.Id == -1001952);
                if (levelParam?.Value is int levelId && levelId > 0)
                    elem.Parameters.Add(BipHelpers.MakeBipParam(-1001107, "WALL_BASE_CONSTRAINT", StorageType.ElementId, levelId));
            }
            if (!existing.Contains(-1001117))
                elem.Parameters.Add(BipHelpers.MakeBipParam(-1001117, "WALL_TOP_IS_ATTACHED", StorageType.Integer, 0));
            if (!existing.Contains(-1001118))
                elem.Parameters.Add(BipHelpers.MakeBipParam(-1001118, "WALL_BOTTOM_IS_ATTACHED", StorageType.Integer, 0));
        }

        if (!existing.Contains(-1001552))
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1001552, "STRUCTURAL_ANALYTICAL_MODEL", StorageType.Integer, 0, hasNoRevitValue: true));

        if (!existing.Contains(-1001713))
            elem.Parameters.Add(BipHelpers.MakeBipParam(-1001713, "RELATED_TO_MASS", StorageType.Integer, 0, hasNoRevitValue: true));
    }

    /// <summary>
    /// Approximate face-derived centreline for host search when no serialized curve is available.
    /// </summary>
    private static void BuildLocationFromTailLine(Element elem, uint elemId, ParsingState state)
    {
        // Walls only, and only when the wall has extracted face geometry: synthesize the centreline
        // from the LARGEST VERTICAL face (world coords): direction = the face's in-plane axis,
        // extent = outer vertices projected on it.
        if (elem.CategoryId != -2000011 || elem.Geometry?.Faces is not { Count: > 0 } faces) return;

        double widFt = 0;
        foreach (var p in elem.Parameters)
            if (p.Id == -1001000 && p.Value is double wv && wv > 0.001 && wv < 50) { widFt = wv; break; }
        if (widFt <= 0) return;

        Model.GeoFace? best = null;
        double bestArea = 0;
        foreach (var f in faces)
        {
            if (f.OuterVertices is not { Count: >= 3 } vs) continue;
            double nz = Math.Abs(f.Normal[2]);
            double nl = Math.Sqrt(f.Normal[0] * f.Normal[0] + f.Normal[1] * f.Normal[1] + f.Normal[2] * f.Normal[2]);
            if (nl < 0.5 || nz / nl > 0.3) continue; // want a vertical face
            double a = f.Area ?? 0;
            if (!double.IsFinite(a) || a <= 0)
            {
                // rough area from vertex spread
                a = vs.Count;
            }
            bool corrupt = false;
            foreach (var v in vs)
                if (v.Length < 3 || !double.IsFinite(v[0]) || Math.Abs(v[0]) > 1e6
                    || !double.IsFinite(v[1]) || Math.Abs(v[1]) > 1e6
                    || !double.IsFinite(v[2]) || Math.Abs(v[2]) > 1e6) { corrupt = true; break; }
            if (corrupt) continue;
            if (a > bestArea) { bestArea = a; best = f; }
        }
        if (best == null) return;

        // In-plane horizontal direction: normal x Z (guaranteed horizontal, in the face plane).
        double ux = best.Normal[1], uy = -best.Normal[0];
        double ul = Math.Sqrt(ux * ux + uy * uy);
        if (ul < 1e-6) return;
        ux /= ul; uy /= ul;

        var vs2 = best.OuterVertices!;
        double t0 = double.MaxValue, t1 = double.MinValue, zMin = double.MaxValue;
        foreach (var v in vs2)
        {
            double t = v[0] * ux + v[1] * uy;
            if (t < t0) t0 = t;
            if (t > t1) t1 = t;
            if (v[2] < zMin) zMin = v[2];
        }
        if (t1 - t0 < 0.05) return;
        // Reject wrong-face picks (end caps, opening reveals): the axis extent must cover most of
        // the element's plan footprint.
        if (elem.BoundingBox is { Length: >= 6 } bbC)
        {
            double planMax = Math.Max(bbC[3] - bbC[0], bbC[4] - bbC[1]);
            if (planMax > 0.1 && (t1 - t0) < 0.7 * planMax) return;
        }

        // Face plane point -> midline: shift half width along the horizontal normal, towards the
        // element bbox centre when known.
        double nx = best.Normal[0], ny = best.Normal[1];
        double nh = Math.Sqrt(nx * nx + ny * ny);
        if (nh < 1e-6) return;
        nx /= nh; ny /= nh;
        double px = vs2[0][0], py = vs2[0][1];
        double sgn = 1;
        if (GeometryBox(elem) is { Length: >= 6 } bb)
        {
            double cx = (bb[0] + bb[3]) / 2, cy = (bb[1] + bb[4]) / 2;
            sgn = ((cx - px) * nx + (cy - py) * ny) >= 0 ? 1 : -1;
        }
        double mx = px + sgn * nx * widFt / 2, my = py + sgn * ny * widFt / 2;
        // project midline point onto the u axis to anchor start/end
        double tm = mx * ux + my * uy;
        double sx = mx + (t0 - tm) * ux, sy = my + (t0 - tm) * uy;
        double ex = mx + (t1 - tm) * ux, ey = my + (t1 - tm) * uy;

        elem.Location = new LocationCurve
        {
            // "LineSynth": face-derived approximation (midline can be off by up to the wall width
            // when the shift sign is ambiguous) - good enough for door-host search/orientation, NOT
            // used for the wall's own IFC geometry.
            CurveType = "LineSynth",
            StartPoint = [sx, sy, zMin],
            EndPoint = [ex, ey, zMin],
            Direction = [ux, uy, 0],
            Length = t1 - t0,
        };
    }

    /// <summary>
    /// Build LocationCurve from CurveDriver candidates. Multiple GLine matches may exist in the
    /// 0xFFFF block - some in local coords, some in world. Revit wall location curves lie in the
    /// wall's base plane.
    /// </summary>
    private static void BuildLocationCurve(Element elem, uint elemId, ParsingState state)
    {
        state.CurveDriverCandidates.TryGetValue(elemId, out var candidates);
        if ((candidates == null || candidates.Count == 0) && state.StaleCopiesRemoved
            && state.ExactCurveDrivers.TryGetValue(elemId, out var exactCandidates))
            candidates = exactCandidates;
        CurveDriverData? ownLine = state.OwnWallLocationLines.GetValueOrDefault(elemId);
        if ((candidates == null || candidates.Count == 0) && ownLine == null)
        {
            BuildLocationFromTailLine(elem, elemId, state);
            return;
        }

        double[]? worldDir = null;
        if (state.GrepBlocks.TryGetValue(elemId, out var grep) && grep.Faces.Count > 0)
        {
            worldDir = grep.Faces[0].XVector;
        }

        CurveDriverData cd = candidates is { Count: > 0 }
            ? SelectWallLocationCurveCandidate(candidates, worldDir) : ownLine!;

        var loc = new LocationCurve();

        if (!cd.IsArc)
        {
            loc.CurveType = "Line";

            double sx = cd.OriginPoint[0] + cd.EndParams[0] * cd.DirectionVector[0];
            double sy = cd.OriginPoint[1] + cd.EndParams[0] * cd.DirectionVector[1];
            double sz = cd.OriginPoint[2] + cd.EndParams[0] * cd.DirectionVector[2];
            double ex = cd.OriginPoint[0] + cd.EndParams[1] * cd.DirectionVector[0];
            double ey = cd.OriginPoint[1] + cd.EndParams[1] * cd.DirectionVector[1];
            double ez = cd.OriginPoint[2] + cd.EndParams[1] * cd.DirectionVector[2];

            loc.StartPoint = [sx, sy, sz];
            loc.EndPoint = [ex, ey, ez];
            loc.Direction = [cd.DirectionVector[0], cd.DirectionVector[1], cd.DirectionVector[2]];
            loc.DriverOrigin = cd.OriginPoint; loc.DriverDirection = cd.DirectionVector; loc.DriverEndParams = cd.EndParams;

            double dx = ex - sx, dy = ey - sy, dz = ez - sz;
            loc.Length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        else
        {
            loc.CurveType = "Arc";

            double cx = cd.CenterPoint![0], cy = cd.CenterPoint[1], cz = cd.CenterPoint[2];
            loc.ArcCenter = [cx, cy, cz];
            loc.ArcRadius = cd.Radius;

            // P(t) = Center + R * (cos(t) * XDir + sin(t) * YDir)
            double t0 = cd.EndParams[0], t1 = cd.EndParams[1];
            double xdx = cd.XDirection![0], xdy = cd.XDirection[1], xdz = cd.XDirection[2];
            double ydx = cd.YDirection![0], ydy = cd.YDirection[1], ydz = cd.YDirection[2];
            loc.ArcXDirection = [xdx, xdy, xdz];
            loc.ArcYDirection = [ydx, ydy, ydz];
            loc.ArcStartParameter = t0;
            loc.ArcEndParameter = t1;
            double r = cd.Radius;

            loc.StartPoint = [
                cx + r * (Math.Cos(t0) * xdx + Math.Sin(t0) * ydx),
                cy + r * (Math.Cos(t0) * xdy + Math.Sin(t0) * ydy),
                cz + r * (Math.Cos(t0) * xdz + Math.Sin(t0) * ydz),
            ];
            loc.EndPoint = [
                cx + r * (Math.Cos(t1) * xdx + Math.Sin(t1) * ydx),
                cy + r * (Math.Cos(t1) * xdy + Math.Sin(t1) * ydy),
                cz + r * (Math.Cos(t1) * xdz + Math.Sin(t1) * ydz),
            ];

            loc.Length = r * Math.Abs(t1 - t0);
        }

        elem.Location = loc;
    }

    internal static CurveDriverData SelectWallLocationCurveCandidate(
        IReadOnlyList<CurveDriverData> candidates, double[]? worldDirection)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("At least one CurveDriver candidate is required.", nameof(candidates));

        static double VerticalComponent(CurveDriverData candidate)
        {
            if (!candidate.IsArc)
                return Math.Abs(candidate.DirectionVector[2]);

            if (candidate.XDirection == null || candidate.YDirection == null)
                return double.PositiveInfinity;

            return Math.Max(Math.Abs(candidate.XDirection[2]), Math.Abs(candidate.YDirection[2]));
        }

        double minimumVerticalComponent = candidates.Min(VerticalComponent);
        var planarCandidates = candidates
            .Where(candidate => VerticalComponent(candidate) == minimumVerticalComponent)
            .ToList();

        var arcs = planarCandidates.Where(candidate => candidate.IsArc).ToList();
        var compatibleCandidates = arcs.Count > 0 ? arcs : planarCandidates;
        if (compatibleCandidates.Count == 1 || worldDirection == null)
            return compatibleCandidates[0];

        CurveDriverData selected = compatibleCandidates[0];
        double bestScore = double.NegativeInfinity;
        foreach (var candidate in compatibleCandidates)
        {
            double[] direction = candidate.IsArc
                ? candidate.XDirection!
                : candidate.DirectionVector;
            double score = Math.Abs(
                direction[0] * worldDirection[0]
                + direction[1] * worldDirection[1]
                + direction[2] * worldDirection[2]);
            if (score > bestScore)
            {
                bestScore = score;
                selected = candidate;
            }
        }

        return selected;
    }

    private static void BuildInstanceTransform(Element elem, uint elemId, ParsingState state)
    {
        if (!state.FamilyInstanceTransforms.TryGetValue(elemId, out var fi)) return;

        elem.Transform = new InstanceTransform
        {
            Origin = [fi.Trf[9], fi.Trf[10], fi.Trf[11]],
            // Trf[0..8] is stored row-major: Trf[0..2] = row 0 = (Xx, Yx, Zx). Basis vectors are
            // the COLUMNS, so stride by 3.
            BasisX = [fi.Trf[0], fi.Trf[3], fi.Trf[6]],
            BasisY = [fi.Trf[1], fi.Trf[4], fi.Trf[7]],
            BasisZ = [fi.Trf[2], fi.Trf[5], fi.Trf[8]],
            SymbolId = fi.SymbolId,
        };
    }
}
