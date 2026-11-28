using RvtIO.BuiltIns;
using RvtIO.Geometry.Meshing;
using RvtIO.Model;

namespace RvtIO.Print;

public static partial class SitePrinter
{
    private static (List<List<(double X, double Y)>> Rings, double Gray) CollectShadows(
        RvtDocument document, int viewId, ViewCropFrame? basis, double cut, IReadOnlySet<int>? hidden,
        IReadOnlySet<int> equipmentCasters, IReadOnlyDictionary<int, List<GeoFace>> addedCasters,
        IReadOnlyList<GeoFace> opaquePanes, IReadOnlySet<GeoFace> surfaceFaces, IReadOnlySet<GeoFace> clippedFaces,
        out Dictionary<GeoFace, List<List<(double X, double Y)>>> surfaceShadows,
        out Dictionary<GeoFace, List<List<(double X, double Y)>>> visibility)
    {
        surfaceShadows = [];
        visibility = [];
        double ground = double.NegativeInfinity, rightShift = 0, upShift = 0, gray = 1;
        if (document.RawData.ViewGroundShadows?.GetValueOrDefault(viewId) is { GroundLevel: > 0, Intensity: > 0 } sun
            && document.GetElement(sun.GroundLevel)?.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value
                is double level && double.IsFinite(level))
        {
            double dx = Math.Sin(sun.Azimuth) / Math.Tan(sun.Altitude);
            double dy = -Math.Cos(sun.Azimuth) / Math.Tan(sun.Altitude);
            if (double.IsFinite(dx) && double.IsFinite(dy))
                (ground, rightShift, upShift, gray) = (level, dx, dy, 1 - sun.Intensity / 100d);
        }
        bool castShadows = double.IsFinite(ground);
        if (!castShadows && clippedFaces.Count == 0) return ([], 1);
        double[] right = basis?.Right ?? [1, 0, 0], up = basis?.Up ?? [0, 1, 0];
        double[] down = [0, 0, -1];
        var shadows = new ElevationPrinter.ElevationShadows(right, up, down, rightShift, upShift, gray);
        var sourceFaces = new Dictionary<List<(double X, double Y)>, GeoFace>();
        var paneCovers = new List<ElevationPrinter.ElevationShadows.VisibleFacet>();
        var hiddenCategories = document.RawData.ViewHiddenCategories?.GetValueOrDefault(viewId);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        foreach (var element in document.Elements)
        {
            bool equipment = equipmentCasters.Contains(element.Id);
            var added = addedCasters.GetValueOrDefault(element.Id);
            if (!equipment && added == null
                && element.CategoryId is not ((int)BuiltInCategory.OST_Roofs or (int)BuiltInCategory.OST_Walls
                    or (int)BuiltInCategory.OST_Floors or (int)BuiltInCategory.OST_Stairs
                    or (int)BuiltInCategory.OST_Topography or (int)BuiltInCategory.OST_BuildingPad)
                || element.IsDiscovered || element.SerializedUnplacedOwnerId is > 0
                || element.NativeIsSymbol == true || element.RootClassIs("FamilySymbol")
                || element.CategoryId is int category
                    && category is ((int)BuiltInCategory.OST_Stairs or (int)BuiltInCategory.OST_Floors)
                    && hiddenCategories?.Contains(category) == true
                || hidden?.Contains(element.Id) == true || element.Geometry is not { Faces.Count: > 0 } geometry)
                continue;
            foreach (var face in added ?? geometry.Faces)
            {
                // shortcut: use planar faces wholly below the cut, pending cut-solid reconstruction for shadows.
                if (face.CurvedSurface || face.OuterVertices is not { Count: >= 3 } outer
                    || outer.Any(point => !Valid(point) || point[2] > cut)
                    || face.InnerLoops is { Count: > 0 } && face.InnerVertices is not { Count: > 0 }
                    || face.InnerVertices?.Any(ring => ring.Count < 3 || ring.Any(point => !Valid(point))) == true)
                    continue;

                bool aboveGround = outer.Any(point => point[2] > ground);
                if (!aboveGround && clippedFaces.Count == 0) continue;
                if (castShadows && aboveGround)
                {
                    shadows.AddCaster(face, outer.Select(Millimetres).ToList());
                    foreach (var vertex in outer)
                    {
                        var (x, y) = InView(vertex);
                        double height = vertex[2] - ground;
                        x -= height * rightShift;
                        y -= height * upShift;
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
                }
                if (equipment || added != null && element.CategoryId != (int)BuiltInCategory.OST_RailingTopRail
                    || face.Normal is not { Length: 3 } normal || !(normal[2] > 1e-9)) continue;
                var source = surfaceFaces.Contains(face) ? face : null;
                bool receivesShadow = castShadows && (aboveGround
                    || source != null && outer.All(point => point[2] >= ground));
                AddReceiverFacets(outer, face, source, receivesShadow);
            }
        }

        // The site view paints the transparent pane white. Include that cover when clipping the
        // projected model below it, while retaining the existing shadow geometry behind the pane.
        foreach (var pane in opaquePanes)
            AddReceiverFacets(pane.OuterVertices!, pane, null, receivesShadow: false, paneCover: true);

        var targets = sourceFaces.Where(entry => clippedFaces.Contains(entry.Value)).Select(entry => entry.Key).ToHashSet();
        var clipped = shadows.ClippedReceivers(targets, paneCovers);
        foreach (var source in clipped.Keys.Select(facet => sourceFaces[facet]).Distinct())
            visibility.Add(source, []);
        foreach (var target in targets)
            if (visibility.TryGetValue(sourceFaces[target], out var visible))
                visible.AddRange(clipped.TryGetValue(target, out var parts) ? parts : [target]);

        if (minX < maxX && minY < maxY)
        {
            var plane = new GeoFace { Normal = [0, 0, 1] };
            AddReceiver([World(minX, minY), World(maxX, minY), World(maxX, maxY), World(minX, maxY)], plane);
        }

        var projected = castShadows ? shadows.Project(double.NegativeInfinity) : null;
        if (projected != null)
            foreach (var (facet, source) in sourceFaces)
            {
                if (!projected.TryGetValue(facet, out var shadow)) continue;
                if (!surfaceShadows.TryGetValue(source, out var rings)) surfaceShadows.Add(source, rings = []);
                rings.AddRange(shadow.Rings);
            }
        return (projected?.Values.SelectMany(shadow => shadow.Rings).ToList() ?? [], gray);

        void AddReceiverFacets(List<double[]> outer, GeoFace face, GeoFace? source, bool receivesShadow, bool paneCover = false)
        {
            if (outer.Count == 3 && face.InnerVertices is not { Count: > 0 })
            {
                AddReceiver(outer, face, source, receivesShadow, paneCover);
                return;
            }
            var vertices = outer.ToList();
            var loops = new List<int[]> { Enumerable.Range(0, outer.Count).ToArray() };
            foreach (var inner in face.InnerVertices ?? [])
            {
                loops.Add(Enumerable.Range(vertices.Count, inner.Count).ToArray());
                vertices.AddRange(inner);
            }
            var plane = new GeoFace { Normal = face.Normal };
            var outerFlags = new bool[loops.Count];
            outerFlags[0] = true;
            foreach (var (a, b, c) in GeTess.TriangulateProfile(vertices.Select(InView).ToArray(), loops, outerFlags))
                AddReceiver([vertices[a], vertices[b], vertices[c]], plane, source, receivesShadow, paneCover);
        }

        void AddReceiver(List<double[]> vertices, GeoFace face, GeoFace? source = null, bool receivesShadow = true,
            bool paneCover = false)
        {
            var points = vertices.Select(InView).Select(point => (point.X * FeetToMillimetres,
                point.Y * FeetToMillimetres)).ToList();
            if (Math.Abs(PlanPrinter.SignedArea(points)) <= 1e-6) return;
            var corners = vertices.Select(Millimetres).ToList();
            if (paneCover)
            {
                var cover = ElevationPrinter.ElevationShadows.CreateVisibleFacet(right, up, down, points, face, corners);
                if (cover != null) paneCovers.Add(cover);
                return;
            }
            shadows.AddVisibleFacet(points, face, corners, receivesShadow: receivesShadow);
            if (source != null) sourceFaces.Add(points, source);
        }

        (double X, double Y) InView(double[] point) =>
            (point[0] * right[0] + point[1] * right[1], point[0] * up[0] + point[1] * up[1]);

        double[] World(double x, double y) => [x * right[0] + y * up[0], x * right[1] + y * up[1], ground];

        static double[] Millimetres(double[] vertex) =>
            [vertex[0] * FeetToMillimetres, vertex[1] * FeetToMillimetres, vertex[2] * FeetToMillimetres];

        static bool Valid(double[] vertex) => vertex.Length == 3 && vertex.All(double.IsFinite);
    }
}
