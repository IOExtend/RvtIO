namespace RvtIO.Print;

using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

/// <summary>An elevation view that can be printed: its element id, name and the direction it looks at, decoded from the
/// name (North, South, East, West).</summary>
/// <param name="ViewId">The element id of the view.</param>
/// <param name="Name">The view name.</param>
/// <param name="Direction">The direction the view looks at: north, south, east or west.</param>
/// <param name="Scale">The view scale denominator.</param>
public sealed record ElevationView(int ViewId, string Name, string Direction, int Scale);

/// <summary>A section trace on a plan: the polyline points in model millimetres and whether they are
/// the file's decoded extent (bounded: drawn as-is) or a legacy straight-line mark (a ray through the
/// two points, extended to the drawing bounds the way an undecodable trace was always drawn).</summary>
internal sealed record SectionMark(List<(double X, double Y)> Points, bool Bounded, string Name, int ViewId = 0,
    bool UsesPlanExtent = false);

/// <summary>
/// Prints an elevation from the 3D geometry the reader decodes: every planar face of the model (walls, floors, roofs,
/// columns, windows and doors) is projected on the vertical plane of the view, painted white from the farthest to the
/// nearest and outlined when it faces the viewer. The view plane itself is not decoded yet, so the direction is taken
/// from the view name (North, South, East, West); a section is not printed (see docs/printing.md).
/// </summary>
public static partial class ElevationPrinter
{
    private const double FeetToMillimetres = 304.8;

    /// <summary>The elevation views of the document: named North, South, East or West, by name.</summary>
    /// <param name="document">The opened document.</param>
    public static IReadOnlyList<ElevationView> Views(RvtDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var views = new List<ElevationView>();
        // The views the project places on a sheet: a section-class view without a compass word in its name is only
        // printed when a sheet places it (the library of the file holds hundreds of family sections otherwise).
        var placedViews = new HashSet<int>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Viewports) continue;
            if (element.ViewportViewId is int placedId) placedViews.Add(placedId);
        }
        foreach (var element in document.Elements)
        {
            if (element.RootClassName is not ("DBViewSection" or "DBViewElevation") && element.NativeRecordClass != 1016)
                continue;
            if (element.LookupParameter(BuiltInParameter.VIEW_NAME)?.Value is not string name || name.Length == 0)
                continue;
            // A view of the section class is a section even when its name carries no compass word (a stair detail).
            bool sectionClass = element.RootClassName is "DBViewSection" or "DBViewElevation"
                || element.NativeRecordClass == 1016;
            bool placed = placedViews.Contains(element.Id);
            string? direction = DirectionOf(name) ?? (sectionClass && placed ? "section" : null);
            if (direction == null) continue;
            int? storedScale = element.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value as int?;
            int scale = storedScale is int value && value > 0 ? value : direction == "section" ? 0 : 100;
            // A detail section (1:10 to 1:50) is drawn from the same whole-model projection, which is not the detail:
            // only a building section (1:100 or wider, with its scale decoded) is printed, and a stair section, which
            // projects its stairs alone.
            bool section = direction == "section";
            bool planeDecoded = element.ViewDirection is { Length: 3 };
            // A section is printed when it is a building section (1:100 or wider), a stair section, or a detail section
            // whose cutting plane the reader decoded: the detail is then cut at its plane inside a window around it.
            if (section && (scale == 0 || scale < 100) && !name.Contains("stair", StringComparison.OrdinalIgnoreCase)
                && !(planeDecoded && placed))
                continue;
            views.Add(new ElevationView(element.Id, name, direction, scale));
        }
        views.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return views;
    }

    /// <summary>The direction a view name looks at, or null when the name is not a compass elevation.</summary>
    internal static string? DirectionOf(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("north")) return "north";
        if (lower.Contains("south")) return "south";
        if (lower.Contains("east")) return "east";
        if (lower.Contains("west")) return "west";
        if (lower.Contains("section")) return "section";
        return null;
    }

    /// <summary>The projected faces of an elevation: the polygons in view millimetres for the fills, the frame
    /// extents, the faces that get an outline, and the visible edges to ink (shared coplanar diagonals removed,
    /// duplicates drawn once).</summary>
    internal sealed record ElevationContent(
        List<(List<(double X, double Y)> Points, double Depth, bool Outline)> Faces,
        List<(double Y, string Label, double X0, double X1)> Levels,
        List<(double X, string Name)> Grids,
        List<(List<(double X, double Y)> Points, double Depth, bool Terrain)> Context,
        List<CollectedEdge> Edges,
        double MinX, double MinY, double MaxX, double MaxY,
        TerrainHatch? GroundHatch = null,
        IReadOnlyDictionary<double, NativeLevelLine>? NativeLevels = null,
        IReadOnlyDictionary<double, NativeGridLine>? NativeGrids = null,
        List<CollectedEdge>? Insulation = null,
        IReadOnlyList<DetailAnnotations.BreakLine>? DetailBreaks = null,
        IReadOnlyList<DetailAnnotations.LayerHatch>? LayerHatches = null,
        IReadOnlyList<CircularAnnotations.Placement>? AnnotationSymbols = null,
        SurfaceHatches? Hatch = null,
        IReadOnlyDictionary<List<(double X, double Y)>, ModelHatch>? ModelHatches = null,
        IReadOnlyDictionary<List<(double X, double Y)>, CastShadow>? CastShadows = null,
        (int Sky, int Horizon, int? Ground)? SkyGradient = null,
        List<List<(double X, double Y)>>? SiteSilhouettes = null,
        bool OpaqueSection = false,
        IReadOnlyList<CollectedEdge>? SiteOutlines = null,
        IReadOnlyDictionary<List<(double X, double Y)>, List<List<(double X, double Y)>>>? FaceOpenings = null,
        VisibleSiteFacets? ForegroundSite = null,
        IReadOnlyList<SectionBand>? SectionBands = null,
        IReadOnlyList<List<(double X, double Y)>>? DetailCutFaces = null,
        AmbientGlazing? Glazing = null,
        IReadOnlyDictionary<List<(double X, double Y)>, AmbientPatch>? AmbientTones = null,
        IReadOnlyDictionary<List<(double X, double Y)>, double?>? FaceFills = null,
        IReadOnlyDictionary<List<(double X, double Y)>, (double Gray, double Opacity)>? DetailGlass = null,
        IReadOnlyDictionary<List<(double X, double Y)>, List<List<(double X, double Y)>>>? TerrainClippedFaces = null,
        bool KeepDoorDepths = false,
        IReadOnlyList<List<(double X, double Y)>>? SectionBillboards = null,
        IReadOnlyDictionary<byte, List<List<(double X, double Y)>>>? PlantTones = null,
        IReadOnlyDictionary<List<(double X, double Y)>, (double BaseY, double Light)>? ExteriorWallTones = null,
        IReadOnlyDictionary<List<(double X, double Y)>, AmbientPatch>? PaneTones = null);

    internal sealed record SectionBand(double Left, double Right, ElevationContent Content);

    internal sealed record SurfaceHatches(double AnchorX, double AnchorY,
        IReadOnlyDictionary<List<(double X, double Y)>, (Parsing.TfBlockDeserializer.PatternGrid Grid, double Width)> Faces,
        IReadOnlyDictionary<List<(double X, double Y)>, IReadOnlyList<Parsing.TfBlockDeserializer.PatternGrid>> DashedFaces,
        bool UseClippedLines,
        IReadOnlyDictionary<List<(double X, double Y)>, List<SurfaceHatchRegion>>? Regions = null);

    internal sealed record SurfaceHatchRegion(Parsing.TfBlockDeserializer.PatternGrid Grid,
        List<(double X, double Y)> Outer, List<List<(double X, double Y)>> Holes);

    internal sealed record ModelHatch(IReadOnlyList<FillHatch.ModelGrid> Grids,
        IReadOnlyList<List<(double X, double Y)>> Holes);

    internal sealed record TerrainHatch(double AnchorX, double AnchorY, double BaseY,
        IReadOnlyList<CollectedEdge> Profile, IReadOnlyList<Parsing.TfBlockDeserializer.PatternGrid> Grids,
        IReadOnlyList<List<(double X, double Y)>>? SectionMasks = null, CastShadow? Shadow = null);

    internal sealed record NativeLevelLine(double Left, double HeadX, double HeadY, double LineEnd, double Radius,
        (double X, double Y)? Elbow, int SymbolId = 0);
    internal sealed record NativeGridLine(double Bottom, double Top, double Radius);

    /// <summary>Projects the model geometry on the vertical plane of a direction. Null when the model has no face.</summary>
    /// <summary>The categories a stair section draws: the stairs, their railings and supports. Treads and
    /// risers are their own categories (the tread/riser boards the plane passes through); without that mass a
    /// stair section leaves projection edges of the stair body inked that the reference hides behind it.</summary>
    private static readonly HashSet<int> StairCategories =
    [
        (int)BuiltInCategory.OST_Stairs, (int)BuiltInCategory.OST_StairsRailing,
        (int)BuiltInCategory.OST_StairsSupports, (int)BuiltInCategory.OST_Lines,
        (int)BuiltInCategory.OST_StairsTrisers,
    ];

    internal static ElevationContent? Collect(RvtDocument document, string direction, bool stairsOnly = false,
        Element? planeView = null, (double Width, double Height)? window = null, bool isDetail = false,
        (double X0, double Y0, double X1, double Y1)? cropWindow = null, int? viewportScale = null)
    {
        var content = CollectPlane(document, direction, stairsOnly, planeView, window, isDetail, cropWindow,
            viewportScale, planeView?.ViewOrigin);
        if (direction != "section" || planeView is not
            {
                SectionViewerId: int viewerId, ViewOrigin: { Length: 3 } origin,
                ViewDirection: { Length: 3 } look, ViewHorizontalDirection: { Length: 3 } horizontal,
                ViewVerticalDirection: { Length: 3 } vertical,
            } || document.RawData.SectionViews?.GetValueOrDefault(viewerId).FrontCut is not { Count: >= 4 } frontCut
            || frontCut.Count % 2 != 0) return content;

        const double tolerance = 0.1; // Same model precision as the projected edges.
        var points = frontCut.Select(p => (X: Project(p, horizontal), Y: Project(p, vertical),
            Depth: Project(p, look))).ToArray();
        if (points[0].X > points[^1].X) Array.Reverse(points);
        if (points.Any(p => Math.Abs(p.Y - points[0].Y) > tolerance)) return content;
        for (int i = 0; i < points.Length; i += 2)
        {
            var start = points[i];
            var end = points[i + 1];
            if (end.X - start.X <= tolerance || Math.Abs(end.Depth - start.Depth) > tolerance
                || i > 0 && Math.Abs(start.X - points[i - 1].X) > tolerance) return content;
        }

        var bands = new List<SectionBand>(points.Length / 2);
        double originalDepth = Project(origin, look);
        for (int i = 0; i < points.Length; i += 2)
        {
            // A section's end marks bound its plan symbol. The end planes extend to the model crop.
            double left = i == 0 ? double.NegativeInfinity : points[i].X;
            double right = i + 2 == points.Length ? double.PositiveInfinity : points[i + 2].X;
            if (cropWindow is { } crop && (right <= crop.X0 || left >= crop.X1)) continue;
            double depthShift = points[i].Depth - originalDepth;
            if (Math.Abs(depthShift) <= tolerance)
            {
                if (cropWindow is { } detail && left <= detail.X0 && right >= detail.X1) return content;
                if (content != null) bands.Add(new(left, right, content));
                continue;
            }
            double delta = depthShift / FeetToMillimetres;
            double[] shiftedOrigin = [origin[0] + look[0] * delta, origin[1] + look[1] * delta,
                origin[2] + look[2] * delta];
            var bandContent = CollectPlane(document, direction, stairsOnly, planeView, window, isDetail, cropWindow,
                viewportScale, shiftedOrigin);
            if (bandContent != null)
                bands.Add(new(left, right, bandContent with { Edges = TrimProjectedEdgesAtCuts(bandContent.Edges).ToList() }));
        }
        if (bands.Count == 0) return null;
        var frame = content ?? bands[0].Content;
        return frame with
        {
            MinX = Math.Min(frame.MinX, bands.Min(band => band.Content.MinX)),
            MinY = Math.Min(frame.MinY, bands.Min(band => band.Content.MinY)),
            MaxX = Math.Max(frame.MaxX, bands.Max(band => band.Content.MaxX)),
            MaxY = Math.Max(frame.MaxY, bands.Max(band => band.Content.MaxY)),
            SectionBands = bands,
        };
    }

    private static ElevationContent? CollectPlane(RvtDocument document, string direction, bool stairsOnly,
        Element? planeView, (double Width, double Height)? window, bool isDetail,
        (double X0, double Y0, double X1, double Y1)? cropWindow, int? viewportScale, double[]? projectionOrigin)
    {
        double[] planeOrigin = [];
        bool cut = false;
        double[] H, V, D;
        if (projectionOrigin is { Length: 3 } origin && planeView is { ViewDirection: { Length: 3 } dir,
            ViewVerticalDirection: { Length: 3 } vert, ViewHorizontalDirection: { Length: 3 } horiz })
        {
            (H, V, D) = (horiz, vert, dir);
            // The file stores the view origin in feet, like every model coordinate; the corners below
            // are millimetres, so the cut plane and the detail window need it converted.
            planeOrigin = [origin[0] * FeetToMillimetres, origin[1] * FeetToMillimetres,
                origin[2] * FeetToMillimetres];
            // A section is cut at its plane: only what stands behind it is drawn.
            cut = direction == "section";
        }
        else
        {
        if (direction == "section")
        {
            // A section is not decoded to its cutting plane yet: it is projected along the short axis of the building,
            // the way a longitudinal section looks.
            double wallMinX = double.MaxValue, wallMinY = double.MaxValue;
            double wallMaxX = double.MinValue, wallMaxY = double.MinValue;
            foreach (var element in document.Elements)
            {
                if (element.CategoryId != (int)BuiltInCategory.OST_Walls) continue;
                if (element.Location is not { CurveType: "Line" } line) continue;
                foreach (var point in new[] { line.StartPoint, line.EndPoint })
                {
                    double x = point[0] * FeetToMillimetres, y = point[1] * FeetToMillimetres;
                    wallMinX = Math.Min(wallMinX, x); wallMaxX = Math.Max(wallMaxX, x);
                    wallMinY = Math.Min(wallMinY, y); wallMaxY = Math.Max(wallMaxY, y);
                }
            }
            direction = wallMaxX - wallMinX >= wallMaxY - wallMinY ? "south" : "east";
        }
        (H, V, D) = direction switch
        {
            "south" => (new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }),
            "north" => (new[] { -1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, -1, 0 }),
            "east" => (new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, new[] { -1.0, 0, 0 }),
            // The view of a 3D view: an isometric projection from above the south west corner.
            "iso" => (new[] { 0.7071068, -0.7071068, 0 }, new[] { -0.4082483, -0.4082483, 0.8164966 },
                new[] { 0.5773503, 0.5773503, -0.5773503 }),
            _ => (new[] { 0.0, -1, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }),
        };
        }
        var faces = new List<(List<(double X, double Y)> Points, double Depth, bool Outline)>();
        // The plants, the topography and the site surfaces print as the faded background of an elevation, behind the
        // building, the way the reference export fades them.
        var context = new List<(List<(double X, double Y)> Points, double Depth, bool Terrain)>();
        // The outlined faces for the shared-edge pass below, with their 3D rings for midpoint depths
        // and the category of the element they came from (a cut edge inks with its own category's pen).
        var edgeFaces = new List<ProjectionFace>();
        // Every face for the occlusion pass below: its smallest corner depth, so an edge hidden
        // behind nearer mass is not inked even though the painter's fills cover it only visually. A face counts
        // as nearer when its nearest corner stands nearer, even a face turned away from the view.
        var faceOccluders = new List<(double MinDepth, int ElementId, int? HostId, int CategoryId,
            PlanarOcclusion? Plane)>();
        var roofInsertFaces = new HashSet<int>();
        var stairMasks = new Dictionary<int, List<(double X, double Y)>>();
        var railingMaskDepths = new Dictionary<int, double>();
        var topRailEdges = new List<CollectedEdge>();
        var elevationTubeEdges = new List<CollectedEdge>();
        var viewLines = new List<CollectedEdge>();
        // The chords where the cut plane slices through a face, in page coordinates: the cut profile of
        // solids the plane passes through. Transverse faces never outline, so the cut flag never marks their
        // boundary, yet the plane carves a profile out of them (a wall cross-section in a detail). Outline
        // faces keep their flagged boundary instead, so the same line is never inked twice. Each chord keeps
        // its source face normal and element type: a pair of parallel chords a type thickness apart gets the
        // type's layer boundaries drawn between them.
        var rawChords = new List<(double X1, double Y1, double X2, double Y2,
            double Nx, double Ny, double Nz, int TypeId, int CategoryId, double Depth)>();
        var wallChordSources = new Dictionary<int, (int WallId, int MaterialId)>();
        var wallCutRegions = new List<(int WallId, int MaterialId, List<(double X, double Y)> Ring)>();
        bool detailPath = window != null || isDetail || cropWindow != null;
        int viewScale = planeView?.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value is int scaleValue && scaleValue > 0
            ? scaleValue : 100;
        var surfacePatterns = new Dictionary<List<(double X, double Y)>,
            (Parsing.TfBlockDeserializer.PatternGrid Grid, double Width)>();
        var modelHatches = new Dictionary<List<(double X, double Y)>, ModelHatch>();
        Dictionary<List<(double X, double Y)>, List<SurfaceHatchRegion>>? regionHatches = null;
        var faceFills = new Dictionary<List<(double X, double Y)>, double?>();
        var exteriorWallTones = new Dictionary<List<(double X, double Y)>, (double BaseY, double Light)>();
        var roundShaftFaces = new HashSet<int>();
        var paintOpenings = new Dictionary<List<(double X, double Y)>, List<List<(double X, double Y)>>>();
        var dashedSurfacePatterns = new Dictionary<List<(double X, double Y)>, IReadOnlyList<Parsing.TfBlockDeserializer.PatternGrid>>();
        var shadows = !cut && !stairsOnly && planeOrigin.Length == 3 && Math.Abs(V[2] - 1) < 1e-9
            ? ElevationShadows.Create(document.RawData.ViewGroundShadows?.GetValueOrDefault(planeView!.Id), H, V, D)
            : null;
        var shadowTerrainCrop = shadows != null && planeView?.ViewCrop is { Active: true } terrainCrop
            && SheetComposer.ViewBasisMatches(planeView, terrainCrop) ? SheetComposer.CropContentRect(terrainCrop)
            : ((double X0, double Y0, double X1, double Y1)?)null;
        // shortcut: keep the unlit ground backdrop white until its terrain coverage is decoded.
        (int Sky, int Horizon, int? Ground)? skyGradient = !cut && !stairsOnly && planeView is { ViewCrop.Active: true }
            && Math.Abs(V[2] - 1) < 1e-9
            && document.RawData.ViewSkyGradients?.TryGetValue(planeView.Id, out var sky) == true
            ? (sky.Sky, sky.Horizon, shadowTerrainCrop != null ? sky.Ground : null)
            : null;
        bool fineSection = cut && viewScale <= 50;
        bool keepDoorDepths = fineSection && !detailPath;
        int? viewDetail = planeOrigin.Length == 3
            ? planeView?.LookupParameter(BuiltInParameter.VIEW_DETAIL_LEVEL)?.Value as int? : null;
        bool showSurfacePatterns = planeView != null
            && document.RawData.OpaqueViewsWithSurfacePatterns?.Contains(planeView.Id) == true;
        // shortcut: surface hatching in fine sections, until the view's coarse surface overrides are decoded.
        bool showDraftingPatterns = fineSection && showSurfacePatterns;
        var datumCrop = isDetail ? cropWindow : null;
        if (datumCrop == null && window == null && cut
            && planeView is { ViewAnnotationCropActive: true, ViewCrop: { Active: true } viewCrop }
            && SheetComposer.ViewBasisMatches(planeView, viewCrop))
            datumCrop = SheetComposer.CropContentRect(viewCrop);
        (double X0, double Y0, double X1, double Y1)? sectionAnnotationCrop = null;
        if (datumCrop is { } modelCrop && planeView?.ViewAnnotationCrop is { } offsets)
        {
            double paperToModel = FeetToMillimetres * (viewportScale ?? viewScale);
            sectionAnnotationCrop = (modelCrop.X0 - offsets.Left * paperToModel, modelCrop.Y0 - offsets.Bottom * paperToModel,
                modelCrop.X1 + offsets.Right * paperToModel, modelCrop.Y1 + offsets.Top * paperToModel);
        }
        var sectionCrop = cropWindow;
        if (fineSection && window == null && sectionCrop == null && planeView?.ViewCrop is { Active: true } sectionViewCrop
            && SheetComposer.ViewBasisMatches(planeView, sectionViewCrop))
            sectionCrop = SheetComposer.CropContentRect(sectionViewCrop);
        bool drawTerrainCut = planeOrigin.Length == 3 && !stairsOnly && !detailPath && viewScale >= 100;
        bool overviewSection = cut && drawTerrainCut;
        bool drawGround = drawTerrainCut || cut && !stairsOnly && sectionCrop != null;
        bool detailCallout = planeView?.SectionViewerId is int sectionViewer
            && document.RawData.SectionViews?.GetValueOrDefault(sectionViewer).ParentViewId is > 0;
        // The backing surfaces are available in a full section, outside the detail cut-cap pipeline.
        var glazing = fineSection && !detailPath && showSurfacePatterns && planeView != null
            && document.RawData.ViewAmbientLight?.TryGetValue(planeView.Id, out double ambientLight) == true
            ? new AmbientGlazing(ambientLight, detailCallout) : null;
        bool paintDetailCuts = cut && isDetail && cropWindow != null && planeView != null
            && document.RawData.OpaquePlanViews?.Contains(planeView.Id) == true;
        bool opaqueSection = cut && planeView != null && document.RawData.OpaquePlanViews?.Contains(planeView.Id) == true;
        bool opaqueFineSection = opaqueSection && fineSection && sectionCrop != null;
        bool regularFineSection = opaqueFineSection && !detailPath && !detailCallout;
        bool regularOpaqueSection = regularFineSection || opaqueSection && drawTerrainCut && showSurfacePatterns;
        var sectionTerrainCrop = opaqueSection && drawTerrainCut && showSurfacePatterns
            && planeView?.ViewCrop is { Active: true } sectionTerrainViewCrop
            && SheetComposer.ViewBasisMatches(planeView, sectionTerrainViewCrop)
            ? SheetComposer.CropContentRect(sectionTerrainViewCrop)
            : ((double X0, double Y0, double X1, double Y1)?)null;
        var sectionTerrain = sectionTerrainCrop.HasValue ? new ElevationShadows.TerrainOcclusion(H, V, D) : null;
        var terrainOcclusion = shadowTerrainCrop != null ? shadows!.Terrain : sectionTerrain;
        var terrainFillCrop = shadowTerrainCrop ?? sectionTerrainCrop;
        double ambientFraction = 0;
        var ambient = opaqueFineSection && (!detailPath || paintDetailCuts) && sectionCrop is { } lightCrop
            && document.RawData.ViewAmbientFractions?.TryGetValue(planeView!.Id, out ambientFraction) == true
            ? new AmbientOcclusion(ambientFraction, lightCrop, binaryVisibility: regularFineSection) : null;
        bool screenShadows = ambient != null && glazing != null && detailCallout;
        var screenPaneOwners = screenShadows ? new HashSet<int>() : null;
        var screenWallReceivers = screenShadows ? new Dictionary<List<(double X, double Y)>, double>() : null;
        var plantSun = sectionTerrainCrop.HasValue ? SolarStudyOverlay.StillSunDirection(document.RawData, planeView!.Id) : null;
        double plantAmbientFraction = plantSun.HasValue
            ? document.RawData.ViewAmbientFractions?.GetValueOrDefault(planeView!.Id, 1) ?? 1 : 1;
        var plantFacets = new List<ElevationShadows.VisibleFacet>();
        var plantTones = new Dictionary<ElevationShadows.VisibleFacet, byte>();
        var detailGlass = paintDetailCuts && ambient != null
            ? new Dictionary<List<(double X, double Y)>, (double Gray, double Opacity)>()
            : null;
        var roofSectionCrop = opaqueFineSection || paintDetailCuts ? sectionCrop : null;
        if (roofSectionCrop == null && opaqueSection && viewDetail == 3 && window == null
            && planeView?.ViewCrop is { Active: true } roofViewCrop && SheetComposer.ViewBasisMatches(planeView, roofViewCrop))
            roofSectionCrop = SheetComposer.CropContentRect(roofViewCrop);
        List<List<(double X, double Y)>>? modelSectionMasks = roofSectionCrop != null && !paintDetailCuts ? [] : null;
        List<DetailAnnotations.LayerBoundary>? layerBoundaries = roofSectionCrop != null ? [] : null;
        var sectionedLayerChords = new HashSet<int>();
        bool hasSiteProjection = !stairsOnly && planeOrigin.Length == 3;
        bool projectSiteMeshes = hasSiteProjection && (!cut || drawTerrainCut);
        List<List<(double X, double Y)>>? siteSilhouettes =
            projectSiteMeshes || hasSiteProjection && paintDetailCuts ? [] : null;
        bool clipSiteContext = cut && !projectSiteMeshes;
        var siteObjects = new List<(List<CollectedEdge> Edges, double Near, bool WireMesh)>();
        var sectionBillboards = new List<List<(double X, double Y)>>();
        var plantMasks = new List<(List<(double X, double Y)> Points, double Far)>();
        var siteModelMasks = new List<(List<(double X, double Y)> Points, double Near, double Far)>();
        double[]? towardViewer = drawTerrainCut && !cut ? [-D[0], -D[1], -D[2]] : null;
        double viewPlaneDepth = planeOrigin.Length == 3
            ? planeOrigin[0] * D[0] + planeOrigin[1] * D[1] + planeOrigin[2] * D[2] : 0;
        var plantingOutlines = drawTerrainCut && showSurfacePatterns
            ? new ElevationShadows.PlantingOutlines(H, V, D, viewPlaneDepth, shadows) : null;
        var terrainEdges = new List<CollectedEdge>();
        var terrainPads = new List<(double From, double To)>();
        var padProfile = new List<CollectedEdge>();
        var terrainForeground = new List<List<(double X, double Y)>>();
        var terrainSectionMasks = new List<List<(double X, double Y)>>();
        List<List<(double X, double Y)>>? detailRoofCuts = paintDetailCuts ? [] : null;
        List<List<(double X, double Y)>>? detailWallCuts = paintDetailCuts ? [] : null;
        var sectionWallFaces = new Dictionary<int, (int TypeId, int MaterialId, double Depth)>();
        var detailWallOccluders = paintDetailCuts ? new List<(List<(double X, double Y)> Points, double Depth)>() : null;
        bool roundElevationShafts = !cut && drawTerrainCut && showSurfacePatterns;
        var spanOccluders = fineSection || roundElevationShafts
            ? new List<(List<(double X, double Y)> Points, double FarDepth, bool ViewAlignedSlabSide)>() : null;
        var spanOpenings = roundElevationShafts
            ? new Dictionary<List<(double X, double Y)>, List<List<(double X, double Y)>>>() : null;
        var elevationCutEdges = new List<CollectedEdge>();
        var meshSilhouettes = new List<CollectedEdge>();
        bool calloutFurnitureMeshes = opaqueFineSection && detailCallout && !detailPath;
        var meshCovers = opaqueSection && (drawTerrainCut || regularFineSection || calloutFurnitureMeshes)
            ? new List<(List<(double X, double Y)> Points, double FarDepth, int ElementId)>()
            : null;
        var furnitureCovers = overviewSection && opaqueSection ? new Dictionary<int, double>() : null;
        TerrainHatch? groundHatch = null;
        List<DetailAnnotations.LayerHatch>? layerHatches = sectionCrop != null && (isDetail || fineSection && window == null)
            || roofSectionCrop != null && window == null || towardViewer != null && showSurfacePatterns
            ? [] : null;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double maxElevationTubeDiameter = 0;
        if (!cut && drawTerrainCut && showSurfacePatterns)
        {
            int scale = viewportScale ?? viewScale;
            double width = PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Walls, false, scale, out double pen)
                ? pen : 0.2;
            maxElevationTubeDiameter = width * scale / FeetToMillimetres;
        }
        var hiddenElements = planeView == null ? null : document.RawData.ViewHiddenElements?.GetValueOrDefault(planeView.Id);
        var hiddenCategories = regularFineSection || paintDetailCuts
            ? document.RawData.ViewHiddenCategories?.GetValueOrDefault(planeView!.Id) : null;
        foreach (var element in document.Elements)
        {
            if (opaqueFineSection && element.IsGroupTemplate || hiddenElements?.Contains(element.Id) == true
                || element.CategoryId is int category && hiddenCategories?.Contains(category) == true) continue;
            if (opaqueFineSection && document.RawData.ElemTableIsExact
                && element.SerializedOwningElementId is > 0 and int owner
                && document.GetElement(owner)?.RootClassIs("Family") == true) continue;
            if (regularFineSection && (element.NativeIsSymbol == true || element.SerializedUnplacedOwnerId is > 0
                || element.RootClassIs("FamilySymbol") || element.RootClassName == "FillPatternElem"
                || document.RawData.ElemTableIsExact && element.IsDiscovered)) continue;
            if (regularOpaqueSection && document.RawData.ElemTableIsExact
                && element.CategoryId is ((int)BuiltInCategory.OST_Casework or (int)BuiltInCategory.OST_StructuralColumns
                    or (int)BuiltInCategory.OST_PlumbingFixtures or (int)BuiltInCategory.OST_Furniture
                    or (int)BuiltInCategory.OST_StructuralFoundation or (int)BuiltInCategory.OST_SpecialityEquipment)
                && !PlacedSiteElement(element)) continue;
            if (element.CategoryId == (int)BuiltInCategory.OST_Viewports) continue;
            if (element.IsDiscovered && element.CategoryId is (int)BuiltInCategory.OST_Materials
                or (int)BuiltInCategory.OST_Constraints or (int)BuiltInCategory.OST_CLines
                or (int)BuiltInCategory.OST_LightingFixtureSource) continue;
            bool siteCategory = element.CategoryId is (int)BuiltInCategory.OST_Planting
                or (int)BuiltInCategory.OST_Topography or (int)BuiltInCategory.OST_Site or (int)BuiltInCategory.OST_Entourage;
            bool outlineSiteObject = siteSilhouettes != null
                && element.CategoryId == (int)BuiltInCategory.OST_Site
                && element.RootClassIs("FamilyInstance");
            if (siteSilhouettes != null && (outlineSiteObject || projectSiteMeshes
                && element.CategoryId is ((int)BuiltInCategory.OST_Planting or (int)BuiltInCategory.OST_Entourage)))
            {
                if (PlacedSiteElement(element) && element.Geometry is { Faces.Count: > 0 } placedGeometry)
                {
                    int silhouetteStart = siteSilhouettes.Count;
                    var siteLight = outlineSiteObject ? null : shadows;
                    bool planting = element.CategoryId == (int)BuiltInCategory.OST_Planting;
                    // The RPC insertion point determines whether its proxy casts onto visible geometry.
                    bool rpcPlant = planting && Math.Abs(D[2]) < 1e-9
                        && element.InstanceOriginXY != null
                        && document.GetElement(element.TypeId)?.LookupParameter(BuiltInParameter.RENDER_RPC_FILENAME)
                            ?.Value is string { Length: > 0 };
                    bool groundOnly = rpcPlant && element.InstanceOriginXY is { } insertion
                        && (insertion.X * D[0] + insertion.Y * D[1]) * FeetToMillimetres < viewPlaneDepth;
                    bool outlineBeforePlane = groundOnly && HeaderOutlineIsBeforePlane(element, D, viewPlaneDepth);
                    bool proxyBeforePlane = groundOnly && placedGeometry.Faces.All(face =>
                        face.OuterVertices is { Count: >= 3 } vertices
                        && vertices.All(point => Project(point, D) < viewPlaneDepth));
                    // The saved RPC extent can intersect the view even when its display panels lie in front of it.
                    bool retainRpcProxy = proxyBeforePlane && !outlineBeforePlane && element.BoundingBoxIsHeaderOutline
                        && element.BoundingBox is { Length: 6 } outlineBounds && outlineBounds.All(double.IsFinite);
                    var tree = planting && plantingOutlines != null && placedGeometry.Faces.All(face =>
                        face.PolygonMesh && face.OuterVertices is { Count: 3 })
                        ? new ElevationShadows.PlantingOutlines.Mesh(clipAtViewPlane: !retainRpcProxy) : null;
                    var wireFaces = element.CategoryId == (int)BuiltInCategory.OST_Entourage
                        && element.RootClassIs("FamilyInstance")
                        ? new List<(List<double[]> Points, double Facing)>() : null;
                    var billboardPlanes = wireFaces != null ? CrossedBillboardPlanes(placedGeometry.Faces) : null;
                    bool crossedBillboard = billboardPlanes != null;
                    bool shadePlant = planting && plantAmbientFraction < 1 && CrossedBillboardPlanes(placedGeometry.Faces) != null;
                    if (billboardPlanes != null && opaqueSection && drawTerrainCut
                        && ProjectSectionBillboard(placedGeometry.Faces, billboardPlanes, H, V, D, viewPlaneDepth)
                            is { } billboard)
                    {
                        sectionBillboards.AddRange(billboard.Rings);
                        siteObjects.Add((billboard.Edges, viewPlaneDepth, true));
                        continue;
                    }
                    double objectNear = double.PositiveInfinity;
                    foreach (var face in placedGeometry.Faces)
                    {
                        if (face.OuterVertices is not { Count: >= 3 } vertices) continue;
                        var world = vertices.Select(point => new[] { point[0] * FeetToMillimetres,
                            point[1] * FeetToMillimetres, point[2] * FeetToMillimetres }).ToList();
                        if (wireFaces != null && face.PolygonMesh
                            && face.RenderStyleId == null && face.NestedRenderStyleId == null)
                            wireFaces.Add((world, Project(face.Normal, D, 1)));
                        if (face.PlaneSurface || vertices.Count == 3)
                            siteLight?.AddCaster(face, world, siteOnly: !planting, planting: planting,
                                groundOnly: groundOnly, castsOnVisiblePlanting: proxyBeforePlane, castsOnSoilCut: !outlineBeforePlane);
                        var visible = retainRpcProxy ? world : ClipBehind(world, planeOrigin, D);
                        var ring = visible.Select(point => (Project(point, H, 1), Project(point, V, 1))).ToList();
                        double area = PlanPrinter.SignedArea(ring);
                        if (tree != null) plantingOutlines!.AddFacet(tree, world, ring, face, visible);
                        if (Math.Abs(area) < 1e-6) continue;
                        if (area < 0) ring.Reverse();
                        siteSilhouettes.Add(ring);
                        if (plantSun is { } sun && plantAmbientFraction < 1 && vertices.Count == 3 && !face.CurvedSurface
                            && ElevationShadows.CreateVisibleFacet(H, V, D, ring, face, visible) is { } facet)
                        {
                            plantFacets.Add(facet);
                            if (shadePlant)
                            {
                                // The untextured proxy uses both sides of its saved display panels.
                                double incidence = Math.Abs(face.Normal[0] * sun.X + face.Normal[1] * sun.Y + face.Normal[2] * sun.Z);
                                double linear = Math.Min(1, plantAmbientFraction + (1 - plantAmbientFraction) * incidence);
                                double gray = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
                                plantTones.Add(facet, (byte)Math.Round(255 * gray));
                            }
                        }
                        if (planting) plantMasks.Add((ring, visible.Max(point => Project(point, D, 1))));
                        else if (shadows != null || plantingOutlines != null)
                        {
                            double siteDepth = visible.Average(point => Project(point, D, 1));
                            shadows?.AddModelOccluder(ring, face.Normal, visible[0], siteDepth, null);
                            plantingOutlines?.AddModelOccluder(ring, face.Normal, visible[0], siteDepth, null);
                        }
                        if (outlineSiteObject) objectNear = Math.Min(objectNear, visible.Min(point => Project(point, D, 1)));
                        if (vertices.Count == 3 && !face.CurvedSurface)
                            siteLight?.AddVisibleFacet(ring, face, visible, planting, crossedBillboard,
                                receivesOffscreenPlantShadows: rpcPlant && !groundOnly);
                    }
                    if (tree != null) plantingOutlines!.AddTree(tree);
                    if (outlineSiteObject && siteSilhouettes.Count > silhouetteStart)
                    {
                        var outlines = OutlineJoinedRings(siteSilhouettes.Skip(silhouetteStart).ToList(), element.CategoryId!.Value);
                        siteObjects.Add((outlines.ToList(), objectNear, false));
                    }
                    if (wireFaces is { Count: > 0 })
                    {
                        double near = wireFaces.Min(face => face.Points.Min(point => Project(point, D, 1)));
                        bool cutMesh = cut && near < viewPlaneDepth;
                        bool wholeMesh = !cutMesh && wireFaces.Count == placedGeometry.Faces.Count;
                        bool outlineBillboard = wholeMesh && crossedBillboard;
                        const int entourageCategory = (int)BuiltInCategory.OST_Entourage;
                        var entourageEdges = outlineBillboard
                            ? OutlineJoinedRings(siteSilhouettes.Skip(silhouetteStart).ToList(),
                                entourageCategory, coverOppositeEdges: true)
                            : SiteWireEdges(wireFaces, H, V, D, viewPlaneDepth, cutMesh);
                        // A one-sided mesh can project a silhouette when its facets face away or are edge-on.
                        if (!outlineBillboard && wholeMesh && wireFaces.All(face => face.Facing >= -1e-6))
                            entourageEdges = entourageEdges.Concat(OutlineJoinedRings(
                                siteSilhouettes.Skip(silhouetteStart).ToList(), entourageCategory));
                        siteObjects.Add((entourageEdges.ToList(), Math.Max(near, viewPlaneDepth), true));
                    }
                }
                continue;
            }
            if (siteCategory)
            {
                if (clipSiteContext && !PlacedSiteElement(element)) continue;
                if (element.Geometry is { Faces.Count: > 0 } siteGeometry)
                {
                    foreach (var siteFace in siteGeometry.Faces)
                    {
                        if (siteFace.OuterVertices is not { Count: >= 3 } siteVertices) continue;
                        var world = siteVertices.Select(point => new[] { point[0] * FeetToMillimetres,
                            point[1] * FeetToMillimetres, point[2] * FeetToMillimetres }).ToList();
                        bool upwardTerrain = element.CategoryId == (int)BuiltInCategory.OST_Topography
                            && siteFace.Normal[2] > 0;
                        if (drawGround && upwardTerrain)
                        {
                            foreach (var (a, b) in CutChords(world, planeOrigin, D))
                                terrainEdges.Add(new CollectedEdge(Project(a, H, 1), Project(a, V, 1),
                                    Project(b, H, 1), Project(b, V, 1),
                                    0, 0, -1, -1, 0, 0, 0, 0, true, (int)BuiltInCategory.OST_Topography));
                        }
                        bool planarTerrain = upwardTerrain && (siteFace.PlaneSurface || siteVertices.Count == 3);
                        bool placedSectionTerrain = planarTerrain && sectionTerrainCrop != null && PlacedSiteElement(element);
                        bool clipTerrain = placedSectionTerrain || upwardTerrain && drawTerrainCut && skyGradient?.Ground != null;
                        var visible = clipSiteContext || clipTerrain
                            ? ClipBehind(world, planeOrigin, D) : world;
                        if (visible.Count < 3) continue;
                        if (regularFineSection && planarTerrain && ambient != null
                            && (siteFace.InnerLoops is not { Count: > 0 } || siteFace.InnerVertices is { Count: > 0 }))
                            ambient.AddOccluder(visible.Select(point => AmbientVector(point, 1)).ToList(),
                                AmbientVector(siteFace.Normal, 1), siteFace.InnerVertices?.Select(ring =>
                                    ring.Select(point => AmbientVector(point, FeetToMillimetres)).ToList()).ToList());
                        var sitePoints = visible.Select(point => (X: Project(point, H, 1), Y: Project(point, V, 1))).ToList();
                        double siteArea = 0;
                        for (int i = 0; i < sitePoints.Count; i++)
                        {
                            var a = sitePoints[i];
                            var b = sitePoints[(i + 1) % sitePoints.Count];
                            siteArea += a.X * b.Y - b.X * a.Y;
                        }
                        bool terrainFacet = placedSectionTerrain || planarTerrain && shadowTerrainCrop != null;
                        if (terrainFacet && terrainFillCrop is { } terrainBounds)
                        {
                            if (Math.Abs(siteArea) < 1e-6 || sitePoints.All(p => p.X < terrainBounds.X0)
                                || sitePoints.All(p => p.X > terrainBounds.X1) || sitePoints.All(p => p.Y < terrainBounds.Y0)
                                || sitePoints.All(p => p.Y > terrainBounds.Y1)) continue;
                        }
                        // Keep the size cutoff for other site context.
                        else if (Math.Abs(siteArea) / 2 < 500_000 || context.Count >= 3000) continue;
                        if (placedSectionTerrain)
                            sectionTerrain!.AddSurface(sitePoints, siteFace, visible);
                        context.Add((sitePoints, visible.Average(point => Project(point, D, 1)),
                            element.CategoryId == (int)BuiltInCategory.OST_Topography));
                        if (planarTerrain)
                            shadows?.AddTerrainReceiver(sitePoints, siteFace, visible, PlacedSiteElement(element));
                    }
                }
                continue;
            }
            if (element.Geometry is not { Faces.Count: > 0 } geometry) continue;
            List<List<(double X, double Y)>>? curtainOpenings = null;
            if (element.CategoryId == (int)BuiltInCategory.OST_Walls
                && element.SerializedCurtainPanelOrder is { Length: > 0 } panelOrder
                && document.GetElement(element.TypeId)?.RootClassIs("NewCurtainWallType") == true)
            {
                var panels = panelOrder.Select(document.GetElement).OfType<Element>().Where(panel => panel is
                    { IsDiscovered: false, Geometry: { } panelGeometry }
                    && panel.SerializedHostId == element.Id && panel.NativeIsSymbol != true
                    && panel.SerializedUnplacedOwnerId is not > 0
                    && panelGeometry.Faces.Any(face => !face.CurvedSurface && face.OuterVertices is { Count: >= 3 })).ToList();
                // A fully decoded curtain assembly prints its own panels and mullions.
                if (panels.Count == panelOrder.Length) continue;
                if (fineSection && showSurfacePatterns && window == null)
                {
                    curtainOpenings = [];
                    foreach (var panel in panels.Distinct().Where(panel => panel.CategoryId == (int)BuiltInCategory.OST_CurtainWallPanels))
                    {
                        var fronts = panel.Geometry!.Faces.Where(face => face.PlaneSurface && !face.CurvedSurface
                            && Project(face.Normal, D, 1) < -1 + 1e-9
                            && face.InnerLoops is not { Count: > 0 } && face.InnerVertices is not { Count: > 0 }
                            && face.OuterVertices is { Count: 4 } ring
                            && ring.All(point => point.Length == 3 && point.All(double.IsFinite)
                                && Project(point, D) >= viewPlaneDepth - 1e-6)
                            && HasGlazingPattern(face)).Take(2).ToArray();
                        if (fronts is [{ OuterVertices: { } vertices }])
                        {
                            var opening = ProjectRing(vertices);
                            if (IsProjectedRectangle(opening) && !curtainOpenings.Any(other => RectanglesOverlap(opening, other)))
                                curtainOpenings.Add(opening);
                        }
                    }
                }
            }
            // A stair section draws the stair, not the building around it.
            if (stairsOnly && !StairCategories.Contains(element.CategoryId ?? -1)) continue;
            int categoryId = element.CategoryId ?? 0;
            bool fineEquipmentMesh = regularFineSection && categoryId == (int)BuiltInCategory.OST_SpecialityEquipment
                && element.RootClassIs("FamilyInstance");
            bool fineFurnitureMesh = calloutFurnitureMeshes && categoryId == (int)BuiltInCategory.OST_Furniture
                && element.RootClassIs("FamilyInstance");
            var meshSilhouette = PlacedSiteElement(element) && (fineEquipmentMesh || fineFurnitureMesh
                || opaqueSection && drawTerrainCut && categoryId == (int)BuiltInCategory.OST_GenericModel)
                ? ProjectSectionMeshSilhouette(geometry, H, V, D, viewPlaneDepth, element.Id, categoryId,
                    multipleRevolvedNodes: fineEquipmentMesh, cylindricalFurniture: fineFurnitureMesh)
                : null;
            if (meshSilhouette != null) meshSilhouettes.AddRange(meshSilhouette);
            bool castColumnShadow = shadows != null && categoryId == (int)BuiltInCategory.OST_StructuralColumns
                && PlacedSiteElement(element);
            bool shadeDoorFinish = keepDoorDepths && ambient != null && categoryId == (int)BuiltInCategory.OST_Doors
                && !element.IsDiscovered && element.NativeIsSymbol != true && element.SerializedUnplacedOwnerId is not > 0
                && !geometry.Faces.Any(face => document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1)
                    ?.SerializedMaterialTransparency is > 0 and <= 1);
            bool physicalOccluder = ambient != null && !element.IsDiscovered && element.NativeIsSymbol != true
                && element.SerializedUnplacedOwnerId is not > 0
                && categoryId != (int)BuiltInCategory.OST_LightingFixtureSource
                && !(categoryId == (int)BuiltInCategory.OST_Walls
                    && document.GetElement(element.TypeId)?.RootClassIs("NewCurtainWallType") == true);
            bool screenPane = screenPaneOwners != null && PlacedSiteElement(element)
                && categoryId == (int)BuiltInCategory.OST_CurtainWallPanels
                && geometry.Faces.All(face => document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1)
                    ?.SerializedMaterialTransparency is > 0 and <= 1);
            bool shadeStructuralSlab = regularFineSection && physicalOccluder && categoryId == (int)BuiltInCategory.OST_Floors
                && element.LookupParameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Value is 1
                && element.LookupParameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.Value is > 0 and int slabMaterial
                && document.GetElement(slabMaterial) is { HasShadingAppearance: true,
                    SerializedMaterialUsesRenderAppearance: false, SerializedMaterialTransparency: 0 };
            var wallType = opaqueFineSection && !detailPath && physicalOccluder
                && categoryId == (int)BuiltInCategory.OST_Walls
                ? document.GetElement(element.TypeId) : null;
            var wallFaceLayers = detailCallout ? wallType?.SerializedHostTypeLayers : null;
            int? interiorFinish = regularFineSection && wallType?.SerializedBasicWallTypeFunction == 1
                && element.LookupParameter(BuiltInParameter.WALL_TOP_IS_ATTACHED)?.Value is 0
                && wallType.SerializedHostTypeLayers is { Length: > 1 } layers
                && layers[^1] is { RawFunction: 4 or 5, RawMaterialId: > 0 and int finishId }
                ? finishId : null;
            int? structuralWallMaterial = regularFineSection
                && wallType is { SerializedBasicWallTypeFunction: 1,
                    SerializedHostTypeLayers: [{ RawFunction: 1, RawMaterialId: > 0 and int structuralLayer }] }
                && element.LookupParameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.Value is 1
                && element.LookupParameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.Value is int wallStructuralId
                && wallStructuralId == structuralLayer
                && document.GetElement(wallStructuralId) is { HasShadingAppearance: true, SerializedMaterialTransparency: 0 }
                ? wallStructuralId : null;
            double? structuralWallBaseY = structuralWallMaterial.HasValue && Math.Abs(V[2] - 1) < 1e-9
                && element.LookupParameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.Value is > 0 and int baseLevelId
                && document.GetElement(baseLevelId)?.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value is double baseElevation
                && double.IsFinite(baseElevation)
                ? baseElevation * FeetToMillimetres : null;
            int? exposedWallFinish = regularFineSection && wallType?.SerializedBasicWallTypeFunction == 1
                && element.LookupParameter(BuiltInParameter.WALL_TOP_IS_ATTACHED)?.Value is 1
                && wallType.SerializedHostTypeLayers is { Length: > 1 } exteriorLayers
                && exteriorLayers[0] is { RawFunction: 4 or 5, RawMaterialId: > 0 and int exteriorMaterial }
                && !exteriorLayers.Skip(1).Any(layer => layer.RawMaterialId == exteriorMaterial)
                ? exteriorMaterial : null;
            int? wallMaterial = paintDetailCuts && categoryId == (int)BuiltInCategory.OST_Walls
                && geometry.Faces[0].RenderStyleId is > 0 and int material
                && geometry.Faces.All(face => face.RenderStyleId == material) ? material : null;
            if (drawGround && categoryId == (int)BuiltInCategory.OST_BuildingPad)
            {
                foreach (var padFace in geometry.Faces)
                {
                    // shortcut: leave pads with inner loops alone until their islands are cut as well.
                    if (padFace.Normal[2] <= 0 || padFace.OuterVertices is not { Count: >= 3 } padVertices
                        || padFace.InnerVertices is { Count: > 0 }) continue;
                    var ring = padVertices.Select(v => new[]
                    {
                        v[0] * FeetToMillimetres, v[1] * FeetToMillimetres, v[2] * FeetToMillimetres,
                    }).ToList();
                    foreach (var (a, b) in CutChords(ring, planeOrigin, D))
                    {
                        double x0 = a[0] * H[0] + a[1] * H[1] + a[2] * H[2];
                        double x1 = b[0] * H[0] + b[1] * H[1] + b[2] * H[2];
                        terrainPads.Add((Math.Min(x0, x1), Math.Max(x0, x1)));
                        padProfile.Add(new CollectedEdge(x0, a[0] * V[0] + a[1] * V[1] + a[2] * V[2],
                            x1, b[0] * V[0] + b[1] * V[1] + b[2] * V[2],
                            0, 0, -1, -1, 0, 0, 0, 0, true, categoryId));
                    }
                }
            }
            var roofStack = roofSectionCrop != null && categoryId == (int)BuiltInCategory.OST_Roofs
                ? document.GetElement(element.TypeId)?.SerializedHostTypeLayers : null;
            bool layeredRoofCut = roofStack is { Length: > 1 };
            List<List<(double X, double Y)>>? wallCutRings = null;
            List<List<(double X, double Y)>>? roofCutRings = null;
            if ((layeredRoofCut || drawGround && categoryId is ((int)BuiltInCategory.OST_Walls or (int)BuiltInCategory.OST_Floors
                or (int)BuiltInCategory.OST_BuildingPad or (int)BuiltInCategory.OST_StructuralFoundation))
                && element.BoundingBox is { Length: 6 } bounds
                && Enumerable.Range(0, 3).Sum(i => bounds[D[i] < 0 ? i + 3 : i] * D[i]) * FeetToMillimetres < viewPlaneDepth
                && Enumerable.Range(0, 3).Sum(i => bounds[D[i] < 0 ? i : i + 3] * D[i]) * FeetToMillimetres > viewPlaneDepth)
            {
                var rings = layeredRoofCut ? [] : ClosedSectionRings(geometry.Faces, planeOrigin, D, H, V).ToList();
                if (rings.Count == 0 && (layeredRoofCut || paintDetailCuts && categoryId == (int)BuiltInCategory.OST_Walls))
                    rings = ClosedSectionWithOpenings(geometry.Faces, planeOrigin, D, H, V) ?? rings;
                if (wallMaterial is int materialId && rings is [var wallRing])
                    wallCutRegions.Add((element.Id, materialId, wallRing));
                if (rings.Count > 0)
                {
                    if (layeredRoofCut) roofCutRings = rings;
                    if (paintDetailCuts && categoryId == (int)BuiltInCategory.OST_Walls && !HasNestedSectionRings(rings))
                        wallCutRings = rings;
                    if (sectionTerrainCrop.HasValue && viewDetail == 3 && categoryId == (int)BuiltInCategory.OST_Floors
                        && layerHatches != null && planeView?.ViewOrigin is { Length: 3 } floorViewOrigin
                        && !HasNestedSectionRings(rings))
                        foreach (var ring in rings)
                            DetailAnnotations.AddClosedHostCutHatches(layerHatches, document, element, ring, H, V,
                                floorViewOrigin, layerBoundaries, penWidth: 0.1);
                    bool needsClosedCap = regularFineSection && categoryId == (int)BuiltInCategory.OST_Walls
                        || opaqueFineSection && detailCallout && categoryId == (int)BuiltInCategory.OST_Floors;
                    if (needsClosedCap
                        && geometry.BrepSolids is [{ IsClosed: true }] && rings.Count == 1
                        && ClosedSectionRings(geometry.Faces, planeOrigin, D, H, V, requireClosed: true)
                            .ToList() is [var cap])
                        modelSectionMasks?.Add(cap);
                }
                if (paintDetailCuts && rings.Count > 0 && !HasNestedSectionRings(rings))
                {
                    if (categoryId == (int)BuiltInCategory.OST_Roofs) detailRoofCuts!.AddRange(rings);
                    if (categoryId == (int)BuiltInCategory.OST_Walls) detailWallCuts!.AddRange(rings);
                }
                foreach (var ring in rings)
                {
                    if (!layeredRoofCut || paintDetailCuts) terrainSectionMasks.Add(ring);
                    if (regularFineSection && categoryId is (int)BuiltInCategory.OST_BuildingPad or (int)BuiltInCategory.OST_Floors)
                        modelSectionMasks?.Add(ring);
                    if (cut) continue;
                    if (layerHatches != null && planeView?.ViewOrigin is { Length: 3 } cutViewOrigin)
                        DetailAnnotations.AddClosedHostCutHatches(layerHatches, document, element, ring, H, V, cutViewOrigin);
                    for (int i = 0; i < ring.Count; i++)
                    {
                        var (ax, ay) = ring[i];
                        var (bx, by) = ring[(i + 1) % ring.Count];
                        elevationCutEdges.Add(new(ax, ay, bx, by, viewPlaneDepth, viewPlaneDepth,
                            -1, -1, 0, 0, 0, 0, true, categoryId));
                    }
                }
            }
            bool isRoof = element.CategoryId == (int)BuiltInCategory.OST_Roofs;
            bool fineFixture = fineSection && categoryId == (int)BuiltInCategory.OST_LightingFixtures
                && !element.IsDiscovered;
            bool groupedEquipment = !cut && planeOrigin.Length == 3
                && categoryId == (int)BuiltInCategory.OST_ElectricalEquipment
                && element.SerializedGroupId is > 0 && !element.IsGroupTemplate && !element.IsDiscovered
                && element.RootClassIs("FamilyInstance");
            bool reachesViewPlane = (towardViewer != null || groupedEquipment) && geometry.Faces.Any(face =>
                face.OuterVertices?.Any(vertex => Project(vertex, D) >= viewPlaneDepth) == true);
            bool visibleEquipment = groupedEquipment && reachesViewPlane;
            bool preciseGroupDepth = visibleEquipment || !cut && planeOrigin.Length == 3
                && categoryId == (int)BuiltInCategory.OST_IOSModelGroups;
            List<(int Index, GeoFace Face)>? roofLayers = showSurfacePatterns && isRoof
                && geometry.BrepSolids is [{ IsClosed: true } shell] && shell.FaceIndices.Count == geometry.Faces.Count
                && geometry.Faces.Any(face => face.ModelPattern != null) ? [] : null;
            // shortcut: a single prismatic top rail, until segmented native profiles are supported.
            bool straightTopRail = fineSection && window == null && element.RootClassName == "TopRail" && geometry.Faces.Count == 6
                && geometry.Faces.All(face => face.PlaneSurface && face.OuterVertices is { Count: 4 }
                    && face.InnerVertices is not { Count: > 0 } && face.InnerLoops is not { Count: > 0 });
            int firstChord = rawChords.Count;
            int firstFace = faces.Count;
            bool isInsert = categoryId is (int)BuiltInCategory.OST_Windows or (int)BuiltInCategory.OST_Doors;
            int? hostId = isInsert
                && element.SerializedHostId is > 0 ? element.SerializedHostId : null;
            int? roofHostId = hostId is int host
                && document.GetElement(host)?.CategoryId == (int)BuiltInCategory.OST_Roofs ? host : null;
            bool roofInsert = roofHostId.HasValue && (fineSection || !cut && HasEdgeOnRoofGlazing(geometry.Faces));
            bool projectRoundShafts = showSurfacePatterns && PlacedSiteElement(element)
                && (regularOpaqueSection && categoryId == (int)BuiltInCategory.OST_StructuralColumns
                    || roundElevationShafts && categoryId == (int)BuiltInCategory.OST_GenericModel);
            bool shadeStair = ambient != null && IsStairBodyCategory(categoryId);
            var outwardStairFaces = shadeStair ? OutwardConvexStairFaces(geometry) : null;
            bool stairIntersectsCut = shadeStair && SpansSectionPlane(geometry.Faces, D, viewPlaneDepth);
            foreach (var (face, projectedSilhouette) in SectionFaces(document, element, geometry,
                fineSection || cut && drawTerrainCut, H, V, D, planeOrigin, projectRoundShafts,
                maxElevationTubeDiameter, elevationTubeEdges))
            {
                bool roundShaft = projectRoundShafts && projectedSilhouette;
                if (face.OuterVertices is not { Count: >= 3 } vertices) continue;
                if (face.CurvedSurface) continue;
                var corners = new List<double[]>(vertices.Count);
                foreach (var vertex in vertices)
                    corners.Add([vertex[0] * FeetToMillimetres, vertex[1] * FeetToMillimetres,
                        vertex[2] * FeetToMillimetres]);
                double facing = Math.Abs(face.Normal[0] * D[0] + face.Normal[1] * D[1] + face.Normal[2] * D[2]);
                var paneMaterial = detailGlass != null && face.PlaneSurface && facing > 1 - 1e-9
                    && categoryId is ((int)BuiltInCategory.OST_CurtainWallPanels or (int)BuiltInCategory.OST_Windows
                        or (int)BuiltInCategory.OST_Doors or (int)BuiltInCategory.OST_Mass
                        or (int)BuiltInCategory.OST_MassForm)
                    ? document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1)
                    : null;
                (double Gray, double Transmission)? paneStyle = paneMaterial is { HasShadingAppearance: true,
                    SerializedMaterialTransparency: > 0 and <= 1 and double transmission }
                    && paneMaterial.LookupParameter(BuiltInParameter.MATERIAL_PARAM_COLOR)?.Value
                        is >= 0 and <= 0xffffff and int color
                    ? (((color & 255) * 0.2126 + ((color >> 8) & 255) * 0.7152 + (color >> 16) * 0.0722) / 255, transmission)
                    : null;
                bool seeThrough = (preciseGroupDepth || groupedEquipment || roofInsert)
                    && (face.RenderStyleId ?? face.NestedRenderStyleId) is int faceMaterialId
                    && document.GetElement(faceMaterialId)?.SerializedMaterialTransparency is > 0;
                if (shadows != null && face.PlaneSurface && (categoryId is ((int)BuiltInCategory.OST_Walls
                    or (int)BuiltInCategory.OST_Roofs or (int)BuiltInCategory.OST_Floors or (int)BuiltInCategory.OST_Stairs)
                    || IsRailingCategory(categoryId) && PlacedSiteElement(element)
                    || castColumnShadow && document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1)
                        ?.SerializedMaterialTransparency is not > 0))
                    shadows.AddCaster(face, corners, verifiedOpaque:
                        document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1) is
                            { HasShadingAppearance: true, SerializedMaterialUsesRenderAppearance: false,
                                SerializedMaterialTransparency: 0 });
                if (!cut && shadows != null && roofHostId is int roof && face.PlaneSurface
                    && document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1)
                        ?.SerializedMaterialTransparency is not > 0)
                    shadows.AddCaster(face, corners, roofId: roof);
                if (groupedEquipment && face.PlaneSurface && !seeThrough) shadows?.AddCaster(face, corners, siteOnly: true);
                // The chords come from the unclipped ring: clipping moves or drops the plane crossings, and a
                // window-clipped polygon loses the vertex correspondence entirely. They are projected and kept
                // below, next to the face they were carved from.
                List<(double[] A, double[] B)>? chords = null;
                if (cut && planeOrigin.Length == 3 && facing <= 0.5)
                    chords = CutChords(corners, planeOrigin, D);
                if (chords != null)
                {
                    double planeDepth = planeOrigin[0] * D[0] + planeOrigin[1] * D[1] + planeOrigin[2] * D[2];
                    foreach (var (ca, cb) in chords)
                    {
                        double ax = ca[0] * H[0] + ca[1] * H[1] + ca[2] * H[2];
                        double ay = ca[0] * V[0] + ca[1] * V[1] + ca[2] * V[2];
                        double bx = cb[0] * H[0] + cb[1] * H[1] + cb[2] * H[2];
                        double by = cb[0] * V[0] + cb[1] * V[1] + cb[2] * V[2];
                        if (Math.Abs(bx - ax) + Math.Abs(by - ay) < 0.5) continue;
                        if (window is { } win && cut && planeOrigin.Length == 3)
                        {
                            double cx = planeOrigin[0] * H[0] + planeOrigin[1] * H[1] + planeOrigin[2] * H[2];
                            double cy = planeOrigin[0] * V[0] + planeOrigin[1] * V[1] + planeOrigin[2] * V[2];
                            if (!ClipSegment(ax, ay, bx, by,
                                cx - win.Width / 2, cy - win.Height / 2,
                                cx + win.Width / 2, cy + win.Height / 2,
                                out ax, out ay, out bx, out by)) continue;
                        }
                        if (cropWindow is { } chordCrop && cut && planeOrigin.Length == 3)
                        {
                            if (!ClipSegment(ax, ay, bx, by,
                                chordCrop.X0, chordCrop.Y0, chordCrop.X1, chordCrop.Y1,
                                out ax, out ay, out bx, out by)) continue;
                        }
                        double nl = Math.Sqrt(face.Normal[0] * face.Normal[0] + face.Normal[1] * face.Normal[1] + face.Normal[2] * face.Normal[2]);
                        double nnx = 0, nny = 0, nnz = 0;
                        if (nl > 1e-12) { nnx = face.Normal[0] / nl; nny = face.Normal[1] / nl; nnz = face.Normal[2] / nl; }
                        if (wallMaterial is int chordMaterial)
                            wallChordSources.Add(rawChords.Count, (element.Id, chordMaterial));
                        rawChords.Add((ax, ay, bx, by, nnx, nny, nnz,
                            element.TypeId, element.CategoryId ?? 0, planeDepth));
                        minX = Math.Min(minX, Math.Min(ax, bx)); maxX = Math.Max(maxX, Math.Max(ax, bx));
                        minY = Math.Min(minY, Math.Min(ay, by)); maxY = Math.Max(maxY, Math.Max(ay, by));
                    }
                }
                if (towardViewer != null && reachesViewPlane && corners.Any(point => Project(point, D, 1) < viewPlaneDepth))
                {
                    var foreground = ClipBehind(corners, planeOrigin, towardViewer);
                    var mask = foreground.Select(point => (X: Project(point, H, 1), Y: Project(point, V, 1))).ToList();
                    if (mask.Count >= 3 && Math.Abs(PlanPrinter.SignedArea(mask)) >= 2000) terrainForeground.Add(mask);
                }
                if ((cut || visibleEquipment || drawTerrainCut) && planeOrigin.Length == 3)
                {
                    corners = ClipBehind(corners, planeOrigin, D);
                    if (corners.Count < 3) continue;
                }
                bool ambientBlocker = physicalOccluder && !roundShaft
                    && (face.PlaneSurface || regularFineSection && corners.All(corner =>
                        Math.Abs(Project(corner, face.Normal, 1) - Project(corners[0], face.Normal, 1)) < 1e-6))
                    && (face.InnerLoops is not { Count: > 0 } || face.InnerVertices is { Count: > 0 })
                    && ((face.RenderStyleId ?? face.NestedRenderStyleId) is not (> 0 and int ambientMaterial)
                        || document.GetElement(ambientMaterial)?.SerializedMaterialTransparency is not > 0);
                System.Numerics.Vector3 ambientNormal = default, ambientOrigin = default;
                if (ambientBlocker)
                {
                    ambientNormal = AmbientVector(face.Normal, 1);
                    ambientOrigin = AmbientVector(corners[0], 1);
                    ambient!.AddOccluder(corners.Select(point => AmbientVector(point, 1)).ToList(), ambientNormal,
                        face.InnerVertices?.Select(ring => ring.Select(point => AmbientVector(point, FeetToMillimetres)).ToList()).ToList(),
                        frontFaceOnly: outwardStairFaces?.Contains(face) == true, roofId: isRoof ? element.Id : 0);
                }
                var points = new List<(double X, double Y)>(corners.Count);
                double depth = 0, minDepth = double.PositiveInfinity;
                foreach (var corner in corners)
                    points.Add((corner[0] * H[0] + corner[1] * H[1] + corner[2] * H[2],
                        corner[0] * V[0] + corner[1] * V[1] + corner[2] * V[2]));
                var railBoundary = straightTopRail && Math.Abs(facing - 1) < 1e-9 ? points : null;
                bool[]? incomingEdges = null;
                if (window is { } detail && cut && planeOrigin.Length == 3)
                {
                    // A detail section shows the part of the cut around the point of its plane, at the height its kind
                    // stands at (a roof detail high, a foundation detail low).
                    double cx = planeOrigin[0] * H[0] + planeOrigin[1] * H[1] + planeOrigin[2] * H[2];
                    double cy = planeOrigin[0] * V[0] + planeOrigin[1] * V[1] + planeOrigin[2] * V[2];
                    points = ClipToWindow(points, cx - detail.Width / 2, cx + detail.Width / 2,
                        cy - detail.Height / 2, cy + detail.Height / 2);
                    if (points.Count < 3) continue;
                    foreach (var point in points)
                    {
                        minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                        minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
                    }
                }
                if (cropWindow is { } cropRect && cut && planeOrigin.Length == 3)
                {
                    // A crop-drawn detail filters by its file crop rectangle instead of the invented window:
                    // the occlusion pass below needs the tight in-crop faces, not the whole model (whose coarse
                    // grid hides the fine construction lines), and the frame maps onto the crop either way.
                    // The clip runs on the 3D ring and projects afterwards: clipping the projected points alone
                    // broke the one-to-one correspondence with the ring, and `isCut` (which reads the ring) was
                    // never computed, so every detail view lost its cut profile.
                    (corners, incomingEdges) = ClipToWindow3(corners, H, V, cropRect.X0, cropRect.X1, cropRect.Y0, cropRect.Y1);
                    if (corners.Count < 3) continue;
                    points = corners.Select(corner => (
                        corner[0] * H[0] + corner[1] * H[1] + corner[2] * H[2],
                        corner[0] * V[0] + corner[1] * V[1] + corner[2] * V[2])).ToList();
                }
                foreach (var corner in corners)
                {
                    depth += corner[0] * D[0] + corner[1] * D[1] + corner[2] * D[2];
                    minDepth = Math.Min(minDepth, corner[0] * D[0] + corner[1] * D[1] + corner[2] * D[2]);
                }
                // A sliver of a face adds a line to the page and nothing to the drawing.
                double projected = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    var a = points[i];
                    var b = points[(i + 1) % points.Count];
                    projected += a.X * b.Y - b.X * a.Y;
                }
                bool fineFace = visibleEquipment && face.PlaneSurface
                    || fineFixture && facing > 0.5 && (face.PlaneSurface
                        || corners.All(corner => Math.Abs(Project(corner, face.Normal, 1)
                            - Project(corners[0], face.Normal, 1)) < 1e-6));
                if (Math.Abs(projected) < (projectedSilhouette || fineFace ? 1e-6 : 4000)) continue;
                depth /= corners.Count;
                foreach (var (x, y) in points)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
                if ((roundShaft || drawTerrainCut && showSurfacePatterns && categoryId == (int)BuiltInCategory.OST_Walls)
                    && face.PlaneSurface && Project(face.Normal, D, 1) < 0
                    && face.InnerVertices is not { Count: > 0 } && face.InnerLoops is not { Count: > 0 }
                    && face.RenderStyleId is int surfaceMaterialId
                    && document.RawData.MaterialPatterns?.TryGetValue(surfaceMaterialId, out var surfaceMaterial) == true
                    && surfaceMaterial.SurfacePatternColor == 0
                    && document.RawData.FillPatterns?.TryGetValue(surfaceMaterial.SurfacePatternId, out var surfacePattern) == true
                    && surfacePattern.Grids.Count > 0
                    && surfacePattern.Grids.All(grid => grid is { Target: 0, Orientation: 0, Segments.Length: > 0 }))
                    dashedSurfacePatterns.Add(points, surfacePattern.Grids);
                bool outline = facing > 0.5 || fineFace || dashedSurfacePatterns.ContainsKey(points);
                if (roundShaft) roundShaftFaces.Add(faces.Count);
                faces.Add((points, depth, outline));
                bool projectedSlabSide = shadeStructuralSlab && face.PlaneSurface && facing > 0.5
                    && Math.Abs(face.Normal[2]) <= 1e-6;
                double? sideGray = null;
                if (structuralWallBaseY is double baseY && ambientBlocker
                    && face.RenderStyleId == structuralWallMaterial && Project(face.Normal, D, 1) < -1 + 1e-9
                    && minDepth > viewPlaneDepth && points.Any(point => point.Y > baseY)
                    && document.RawData.ViewAmbientLight?.TryGetValue(planeView!.Id, out double wallLight) == true)
                    exteriorWallTones.Add(points, (baseY, wallLight));
                if (wallFaceLayers != null && face.PlaneSurface && outline && Math.Abs(facing - 1) < 1e-9
                    && face.RenderStyleId is > 0 and int wallFaceFinish
                    && wallFaceLayers.Any(layer => layer.RawFunction is 4 or 5 && layer.RawMaterialId == wallFaceFinish)
                    && document.GetElement(wallFaceFinish) is { HasShadingAppearance: true,
                        SerializedMaterialUsesRenderAppearance: false, SerializedMaterialTransparency: 0 } wallFinish
                    && wallFinish.LookupParameter(BuiltInParameter.MATERIAL_PARAM_COLOR)?.Value is int wallColour
                        and >= 0 and <= 0xffffff)
                    sideGray = (0.2126 * (wallColour & 255) + 0.7152 * ((wallColour >> 8) & 255)
                        + 0.0722 * ((wallColour >> 16) & 255)) / 255;
                if (regularFineSection && fineFixture && ambientBlocker && face.PlaneSurface && outline
                    && document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1) is
                        { HasShadingAppearance: true, SerializedMaterialUsesRenderAppearance: false,
                            SerializedMaterialTransparency: 0 } fixtureFinish
                    && fixtureFinish.LookupParameter(BuiltInParameter.MATERIAL_PARAM_COLOR)?.Value is int fixtureColour
                        and >= 0 and <= 0xffffff)
                    sideGray = (0.2126 * (fixtureColour & 255) + 0.7152 * ((fixtureColour >> 8) & 255)
                        + 0.0722 * ((fixtureColour >> 16) & 255)) / 255;
                if (sideGray.HasValue) faceFills.Add(points, sideGray);
                if (paneStyle is { } pane)
                {
                    double opacity = Project(face.Normal, D, 1) < 0 ? 1 - pane.Transmission : 0;
                    detailGlass!.Add(points, (pane.Gray, opacity));
                }
                if ((visibleEquipment || roundShaft && regularFineSection || shadeDoorFinish) && face.PlaneSurface
                    && document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1) is { } faceMaterialElement)
                {
                    // shortcut: transparent equipment covers keep their edges until their appearance is composited.
                    if (seeThrough) faceFills.Add(points, null);
                    else if (faceMaterialElement.HasShadingAppearance
                        && (roundShaft || faceMaterialElement.SerializedMaterialUsesRenderAppearance == false)
                        && (!shadeDoorFinish || faceMaterialElement.SerializedMaterialTransparency == 0)
                        && faceMaterialElement.LookupParameter(BuiltInParameter.MATERIAL_PARAM_COLOR)?.Value
                            is int colour and >= 0 and <= 0xffffff)
                        faceFills.Add(points, (0.2126 * (colour & 255) + 0.7152 * ((colour >> 8) & 255)
                            + 0.0722 * ((colour >> 16) & 255)) / 255);
                }
                if (fineSection && IsStairBodyCategory(categoryId) && face.PlaneSurface
                    && Math.Abs(facing - 1) < 1e-9 && Math.Abs(minDepth - depth) < 1e-6
                    && face.InnerVertices is not { Count: > 0 } && face.InnerLoops is not { Count: > 0 })
                    stairMasks.Add(faces.Count - 1, points);
                if (fineSection && (IsRailingCategory(categoryId) || categoryId == (int)BuiltInCategory.OST_StairsRailingBaluster)
                    && face.InnerVertices is not { Count: > 0 } && face.InnerLoops is not { Count: > 0 })
                    railingMaskDepths.Add(faces.Count - 1, corners.Max(point => Project(point, D, 1)));
                if (siteSilhouettes != null)
                    siteModelMasks.Add((points, minDepth, corners.Max(point => Project(point, D, 1))));
                if (shadows != null && face.PlaneSurface && Math.Abs(facing - 1) < 1e-9)
                    shadows.AddReceiver(points, face, depth, roundShaft: roundShaft);
                else if (!cut && shadows != null && Math.Abs(face.Normal[2]) <= 1e-6
                    && Project(face.Normal, D, 1) < -1e-9
                    && (categoryId == (int)BuiltInCategory.OST_Walls && face.PlaneSurface
                        || categoryId == (int)BuiltInCategory.OST_StructuralFoundation && PlacedSiteElement(element)
                        && face.MeshMarker != 0 && !face.CurvedSurface && corners.All(corner =>
                            Math.Abs(Project(corner, face.Normal, 1) - Project(corners[0], face.Normal, 1)) < 1e-6)))
                    shadows.AddPlanarModelReceiver(points, face, corners,
                        meshFacet: categoryId == (int)BuiltInCategory.OST_StructuralFoundation);
                else if (!cut && shadows != null && isRoof && face.PlaneSurface
                    && face.Normal[2] > 1e-9 && Project(face.Normal, D, 1) < -1e-9
                    && face.InnerVertices is { Count: > 0 })
                    shadows.AddRoofReceiver(points, face, corners, element.Id);
                if (face.PlaneSurface) roofLayers?.Add((faces.Count - 1, face));
                bool exteriorDraftingPattern = !cut && showSurfacePatterns && planeOrigin.Length == 3
                    && categoryId == (int)BuiltInCategory.OST_Walls && face.PlaneSurface
                    && minDepth >= viewPlaneDepth - 1e-6 && Project(face.Normal, D, 1) < 0
                    && (face.InnerLoops is not { Count: > 0 } || face.InnerVertices is { Count: > 0 });
                if (outline && (exteriorDraftingPattern || showDraftingPatterns
                    && face.InnerVertices is not { Count: > 0 } && face.InnerLoops is not { Count: > 0 })
                    && face.RenderStyleId is int materialId
                    && document.RawData.MaterialPatterns?.TryGetValue(materialId, out var faceMaterial) == true
                    && faceMaterial.SurfacePatternColor == 0
                    && document.RawData.FillPatterns?.TryGetValue(faceMaterial.SurfacePatternId, out var pattern) == true
                    && pattern.Grids is [{ Target: 0, Orientation: 0, Segments.Length: 0, Offset: > 0 } grid]
                    && double.IsFinite(grid.Angle) && double.IsFinite(grid.OriginX) && double.IsFinite(grid.OriginY)
                    && double.IsFinite(grid.Offset))
                {
                    int patternCategory = categoryId switch
                    {
                        (int)BuiltInCategory.OST_Walls => (int)BuiltInCategory.OST_WallsSurfacePattern,
                        (int)BuiltInCategory.OST_Floors => (int)BuiltInCategory.OST_FloorsSurfacePattern,
                        (int)BuiltInCategory.OST_Roofs => (int)BuiltInCategory.OST_RoofsSurfacePattern,
                        (int)BuiltInCategory.OST_Ceilings => (int)BuiltInCategory.OST_CeilingsSurfacePattern,
                        _ => 0,
                    };
                    double width = fineSection && drawGround && !detailPath && patternCategory != 0
                        && PenWidths.TryGetWidth(document, patternCategory, false, viewportScale ?? viewScale, out double savedWidth)
                        ? savedWidth : 0.1;
                    surfacePatterns.Add(points, (grid, width));
                }
                if (outline && showSurfacePatterns && face.PlaneSurface
                    && categoryId == (int)BuiltInCategory.OST_Walls && Project(face.Normal, D, 1) < 0
                    && face.SurfaceRegions is { Count: > 0 }
                    && document.RawData.MaterialPatterns?.GetValueOrDefault(face.RenderStyleId ?? -1).SurfacePatternId is not > 0)
                {
                    foreach (var region in face.SurfaceRegions)
                    {
                        if (document.RawData.MaterialPatterns?.GetValueOrDefault(region.MaterialId) is not { SurfacePatternColor: 0 } paint
                            || document.RawData.FillPatterns?.GetValueOrDefault(paint.SurfacePatternId).Grids
                                is not [{ Target: 0, Orientation: 0, Segments.Length: 0, Offset: > 0 } paintGrid])
                            continue;
                        var outer = ProjectRing(region.Outer);
                        if (outer.Count < 3 || Math.Abs(PlanPrinter.SignedArea(outer)) <= 1e-6) continue;
                        var regionHoles = region.Holes.Select(ProjectRing).Where(ring => ring.Count >= 3).ToList();
                        regionHatches ??= [];
                        if (!regionHatches.TryGetValue(points, out var hatches)) regionHatches.Add(points, hatches = []);
                        hatches.Add(new(paintGrid, outer, regionHoles));
                    }
                }
                bool glazedFace = outline && Project(face.Normal, D, 1) < 0
                    && categoryId is ((int)BuiltInCategory.OST_CurtainWallPanels or
                        (int)BuiltInCategory.OST_Windows or (int)BuiltInCategory.OST_Doors)
                    && HasGlazingPattern(face);
                glazing?.Add(document, points, minDepth, corners.Max(point => Project(point, D, 1)),
                    face.RenderStyleId ?? face.NestedRenderStyleId, glazedFace,
                    coversBacking: regularFineSection && categoryId == (int)BuiltInCategory.OST_CurtainWallPanels);
                if (screenPane && face.PlaneSurface && Math.Abs(facing - 1) < 1e-9 && glazing?.IsPane(points) == true)
                    screenPaneOwners!.Add(element.Id);
                if (showSurfacePatterns && (isRoof || glazedFace) && window == null && planeOrigin.Length == 3
                    && (face.InnerLoops is not { Count: > 0 } || face.InnerVertices is { Count: > 0 })
                    && face.InnerVertices?.All(hole => hole.Count >= 3
                        && hole.All(point => point.Length == 3 && point.All(double.IsFinite))) != false)
                {
                    (double X, double Y) InView(double[] p) => (Project(p, H), Project(p, V));
                    IReadOnlyList<FillHatch.ModelGrid>? hatchGrids = isRoof
                        ? FillHatch.ProjectModelGrid(document, face, InView) is { } modelGrid ? [modelGrid] : null
                        : FillHatch.ProjectDashedModelGrids(document, face, InView);
                    if (hatchGrids != null)
                        modelHatches.Add(points, new ModelHatch(hatchGrids, ProjectOpenings(face).ToList()));
                }
                bool hasPaintOpenings = face.PlaneSurface && face.InnerVertices is { Count: > 0 }
                    && (categoryId == (int)BuiltInCategory.OST_Walls || !cut && isInsert);
                if (hasPaintOpenings)
                {
                    var openings = ProjectOpenings(face).ToList();
                    if (openings.Count > 0) paintOpenings.Add(points, openings);
                }
                if (curtainOpenings is { Count: > 0 } && face.PlaneSurface && Math.Abs(facing - 1) < 1e-9
                    && (face.RenderStyleId ?? face.NestedRenderStyleId) is not > 0
                    && face.InnerLoops is not { Count: > 0 } && face.InnerVertices is not { Count: > 0 }
                    && IsProjectedRectangle(points))
                {
                    // Clear the fallback only over the saved panes, leaving unresolved cells covered.
                    var openings = curtainOpenings.Where(ring => ring.All(point =>
                        PointInPolygon(point.X, point.Y, points) || DistToPolygon(point.X, point.Y, points) < 1e-6)).ToList();
                    if (openings.Count > 0) paintOpenings.Add(points, openings);
                }
                var faceOpenings = paintOpenings.GetValueOrDefault(points);
                if (detailGlass?.GetValueOrDefault(points).Opacity is > 0)
                    ambient!.AddGlazing(points, faceOpenings, corners.Max(point => Project(point, D, 1)));
                if (ambientBlocker && face.PlaneSurface && !glazedFace
                    && !(shadeDoorFinish && faceFills.ContainsKey(points)))
                {
                    if (screenWallReceivers != null && categoryId == (int)BuiltInCategory.OST_Walls
                        && sideGray == null && Project(face.Normal, D, 1) < -1 + 1e-9)
                        screenWallReceivers.Add(points, depth);
                    bool exposedFinish = exposedWallFinish is int finishMaterial
                        && face.RenderStyleId == finishMaterial && Math.Abs(facing - 1) < 1e-9;
                    ambient!.AddReceiver(points, faceOpenings, ambientOrigin, ambientNormal, sideGray ?? 1,
                        orientDirectLight: paintDetailCuts && categoryId == (int)BuiltInCategory.OST_Walls,
                        ignoreStairExitHits: IsStairBodyCategory(categoryId),
                        // An edge-on blocker has no surface in the projected view.
                        ignoreEdgeOnHits: (stairIntersectsCut || interiorFinish is int inside
                            && face.RenderStyleId == inside) && Math.Abs(facing - 1) < 1e-9
                            && Math.Abs(face.Normal[2]) <= 1e-6,
                        distanceFalloff: exposedFinish,
                        attachedRoofIds: exposedFinish ? element.SerializedWallTopAttachmentTargets : null,
                        excludeSamplesBehindCut: paintDetailCuts && categoryId == (int)BuiltInCategory.OST_Floors
                            && Math.Abs(face.Normal[2]) <= 1e-6,
                        exteriorWall: exteriorWallTones.TryGetValue(points, out var exteriorTone)
                            ? (exteriorTone.BaseY, (float)viewPlaneDepth) : null,
                        projectedSlabSide: projectedSlabSide);
                }
                else if (regularFineSection && face.PlaneSurface && ambient != null
                    && glazing?.AmbientBackgroundGray(points) is double glazedBackground)
                    ambient.AddReceiver(points, faceOpenings, AmbientVector(corners[0], 1), AmbientVector(face.Normal, 1),
                        glazedBackground);
                shadows?.AddModelOccluder(points, face.Normal, corners[0], depth, faceOpenings);
                plantingOutlines?.AddModelOccluder(points, face.Normal, corners[0], depth, faceOpenings);
                if (!cut || categoryId is ((int)BuiltInCategory.OST_BuildingPad or (int)BuiltInCategory.OST_StructuralFoundation))
                    terrainOcclusion?.AddTarget(points, face, corners);
                faceOpenings ??= (isRoof || preciseGroupDepth) && face.InnerVertices is { Count: > 0 } holes
                    ? holes.Where(hole => hole.Count >= 3).Select(hole => hole.Select(p => (
                        (p[0] * H[0] + p[1] * H[1] + p[2] * H[2]) * FeetToMillimetres,
                        (p[0] * V[0] + p[1] * V[1] + p[2] * V[2]) * FeetToMillimetres)).ToList()).ToList()
                    : null;
                PlanarOcclusion? occludingPlane = null;
                bool verifiedDetailPlane = paintDetailCuts
                    && (IsStairBodyCategory(categoryId)
                        || categoryId is ((int)BuiltInCategory.OST_Walls or (int)BuiltInCategory.OST_Floors))
                    && corners.All(corner => Math.Abs(Project(corner, face.Normal, 1)
                        - Project(corners[0], face.Normal, 1)) < 1e-6);
                bool exactStairProjectionDepth = overviewSection && face.PlaneSurface
                    && (IsStairBodyCategory(categoryId) || categoryId == (int)BuiltInCategory.OST_Walls);
                if (isRoof || verifiedDetailPlane || exactStairProjectionDepth
                    || face.PlaneSurface && (preciseGroupDepth || hasPaintOpenings || faceOpenings != null || roofInsert))
                {
                    var normal = face.Normal;
                    double nd = normal[0] * D[0] + normal[1] * D[1] + normal[2] * D[2];
                    if (Math.Abs(nd) > 1e-10)
                    {
                        var corner = corners[0];
                        occludingPlane = new PlanarOcclusion(
                            -(normal[0] * H[0] + normal[1] * H[1] + normal[2] * H[2]) / nd,
                            -(normal[0] * V[0] + normal[1] * V[1] + normal[2] * V[2]) / nd,
                            (normal[0] * corner[0] + normal[1] * corner[1] + normal[2] * corner[2]) / nd,
                            faceOpenings, seeThrough, exactStairProjectionDepth);
                    }
                }
                faceOccluders.Add((minDepth, element.Id, hostId, categoryId, occludingPlane));
                if (meshCovers != null && face.PlaneSurface && !seeThrough && !glazedFace
                    && faceOpenings is not { Count: > 0 } && face.InnerLoops is not { Count: > 0 }
                    && face.InnerVertices is not { Count: > 0 })
                {
                    double farDepth = corners.Max(point => Project(point, D, 1));
                    meshCovers.Add((points, farDepth, element.Id));
                    if (furnitureCovers != null && categoryId == (int)BuiltInCategory.OST_Furniture
                        && !element.IsDiscovered && document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1)
                            ?.SerializedMaterialTransparency is not > 0)
                        furnitureCovers.Add(faces.Count - 1, farDepth);
                }
                if (spanOccluders != null && face.PlaneSurface && !element.IsDiscovered
                    && (face.InnerLoops is not { Count: > 0 } loops || roundElevationShafts
                        && Math.Max(face.InnerVertices?.Count ?? 0, faceOpenings?.Count ?? 0) >= loops.Count)
                    && (roundElevationShafts || face.InnerVertices is not { Count: > 0 } && faceOpenings is not { Count: > 0 })
                    && ((face.RenderStyleId ?? face.NestedRenderStyleId) is not (> 0 and int spanMaskMaterial)
                        || document.GetElement(spanMaskMaterial)?.SerializedMaterialTransparency is not > 0))
                {
                    bool viewAlignedSlabSide = categoryId == (int)BuiltInCategory.OST_Floors
                        && Math.Abs(face.Normal[2]) < 1e-6 && Project(face.Normal, D, 1) < -1 + 1e-9;
                    spanOccluders.Add((points, corners.Max(point => Project(point, D, 1)), viewAlignedSlabSide));
                    if (spanOpenings != null)
                    {
                        var openings = faceOpenings;
                        if (face.InnerVertices is { Count: > 0 } inner)
                            openings = [.. openings ?? [], .. inner.Where(ring => ring.Count >= 3)
                                .Select(ring => ring.Select(p => (Project(p, H), Project(p, V))).ToList())];
                        if (openings is { Count: > 0 }) spanOpenings.Add(points, openings);
                    }
                }
                if (detailWallOccluders != null && categoryId == (int)BuiltInCategory.OST_Walls && !seeThrough
                    && Math.Abs(depth - minDepth) < 1e-6 && faceOpenings is not { Count: > 0 }
                    && face.InnerLoops is not { Count: > 0 } && face.InnerVertices is not { Count: > 0 })
                    detailWallOccluders.Add((points, minDepth));
                if (outline)
                {
                    if (roofInsert) roofInsertFaces.Add(faces.Count - 1);
                    if (sectionTerrainCrop.HasValue && categoryId == (int)BuiltInCategory.OST_Walls
                        && element.TypeId > 0 && PlacedSiteElement(element) && face.PlaneSurface
                        && Math.Abs(Project(face.Normal, D, 1) + 1) < 1e-9
                        && faceOpenings is not { Count: > 0 } && face.InnerLoops is not { Count: > 0 }
                        && face.InnerVertices is not { Count: > 0 }
                        && (face.RenderStyleId ?? face.NestedRenderStyleId) is > 0 and int wallFaceMaterial
                        && document.GetElement(wallFaceMaterial)?.SerializedMaterialTransparency is not > 0
                        && corners.All(point => Math.Abs(Project(point, D, 1) - minDepth) < 1e-6))
                        sectionWallFaces.Add(faces.Count - 1, (element.TypeId, wallFaceMaterial, minDepth));
                    // The plane of the face for edge sharing: a triangulation diagonal is the edge of two
                    // coplanar faces, a crease or silhouette the edge of two different planes.
                    double nx = face.Normal[0], ny = face.Normal[1], nz = face.Normal[2];
                    double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (length > 1e-12) { nx /= length; ny /= length; nz /= length; }
                    var corner = corners[0];
                    edgeFaces.Add(new ProjectionFace(points, corners, depth,
                        (long)Math.Round(nx * 1000), (long)Math.Round(ny * 1000), (long)Math.Round(nz * 1000),
                        (long)Math.Round((nx * corner[0] + ny * corner[1] + nz * corner[2]) * 2.0),
                        faces.Count - 1, element.CategoryId ?? 0, incomingEdges));
                    if (railBoundary != null)
                    {
                        for (int i = 0; i < railBoundary.Count; i++)
                        {
                            var (x1, y1) = railBoundary[i];
                            var (x2, y2) = railBoundary[(i + 1) % railBoundary.Count];
                            if (sectionCrop is { } railCrop && !ClipSegment(x1, y1, x2, y2,
                                railCrop.X0, railCrop.Y0, railCrop.X1, railCrop.Y1, out x1, out y1, out x2, out y2)) continue;
                            var key = ProjectedEdgeKey(x1, y1, x2, y2);
                            if (key.Ax == key.Bx && key.Ay == key.By) continue;
                            topRailEdges.Add(new CollectedEdge(x1, y1, x2, y2, depth, depth,
                                faces.Count - 1, -1, 0, 0, 0, 0, false, element.CategoryId ?? 0));
                        }
                    }
                }
            }
            if (opaqueFineSection && !isDetail && layerHatches != null && rawChords.Count > firstChord
                && categoryId == (int)BuiltInCategory.OST_Stairs && planeView?.ViewOrigin is { Length: 3 } stairViewOrigin
                && (geometry.Faces[0].RenderStyleId ?? geometry.Faces[0].NestedRenderStyleId) is > 0 and int stairMaterial
                && geometry.Faces.All(face => (face.RenderStyleId ?? face.NestedRenderStyleId) == stairMaterial
                    && !face.CurvedSurface && face.OuterVertices is { Count: >= 3 }))
            {
                var stairCut = ClosedSectionRings(geometry.Faces, planeOrigin, D, H, V, requireClosed: true).ToList();
                if (!HasNestedSectionRings(stairCut))
                {
                    if (geometry.BrepSolids is { Count: > 0 } stairSolids && stairSolids.All(solid => solid.IsClosed))
                        modelSectionMasks?.AddRange(stairCut);
                    foreach (var ring in stairCut)
                        DetailAnnotations.AddMaterialCutHatch(layerHatches, document, stairMaterial, ring, H, V, stairViewOrigin);
                }
            }
            if (roofCutRings != null && layerBoundaries != null && roofSectionCrop is { } roofCrop && roofStack != null)
            {
                int prior = layerBoundaries.Count;
                AddRoofLayers(layerBoundaries, rawChords.Skip(firstChord), roofCutRings, roofStack, H, V, D, roofCrop,
                    document, !isDetail ? layerHatches : null, planeView?.ViewOrigin);
                if (layerBoundaries.Count > prior) modelSectionMasks?.AddRange(roofCutRings);
            }
            if (layerHatches != null && (!cut || sectionCrop != null) && rawChords.Count > firstChord
                && planeView?.ViewOrigin is { Length: 3 } viewOrigin)
            {
                if (!isDetail && opaqueFineSection && categoryId == (int)BuiltInCategory.OST_Walls)
                    wallCutRings = ClosedSectionWithOpenings(geometry.Faces, planeOrigin, D, H, V);

                int priorBoundaries = layerBoundaries?.Count ?? 0;
                DetailAnnotations.AddLayerHatches(layerHatches, document, element,
                    rawChords.Skip(firstChord).Select(c => (c.X1, c.Y1, c.X2, c.Y2)), H, V, viewOrigin,
                    paintDetailCuts ? layerBoundaries : null, wallCutRings);
                if (layerBoundaries?.Count > priorBoundaries)
                    for (int i = firstChord; i < rawChords.Count; i++) sectionedLayerChords.Add(i);
            }
            if (planeOrigin.Length == 3 && firstFace < faces.Count
                && isInsert
                && !element.IsDiscovered && element.NativeIsSymbol != true && !element.RootClassIs("FamilySymbol")
                && element.SerializedUnplacedOwnerId is not > 0 && geometry.ViewFacingLines is { Count: > 0 } lines)
            {
                foreach (var line in lines)
                {
                    if (Project(line.Axis, D, 1) < 1 - 1e-9 || line.DetailCondition is { } detail
                        && (viewDetail is not (>= 1 and <= 3)
                            || !Parsing.GRepParserV2.CompareSerializedInteger(viewDetail.Value, detail.Comparison, detail.Level))) continue;
                    double startDepth = Project(line.Start, D), endDepth = Project(line.End, D);
                    if (Math.Min(startDepth, endDepth) < viewPlaneDepth - 1e-6) continue;
                    double depth = (startDepth + endDepth) / 2;
                    viewLines.Add(new CollectedEdge(Project(line.Start, H), Project(line.Start, V),
                        Project(line.End, H), Project(line.End, V), depth, depth, firstFace, -1, 0, 0, 0, 0,
                        CategoryId: categoryId, NearDepth: Math.Min(startDepth, endDepth)) { ViewLineStyleId = line.StyleId });
                }
            }
            if (roofLayers is { Count: > 1 })
                OrderRoofLayerPaint(roofLayers);
        }
        if (sectionBillboards.Count > 0)
            foreach (var (x, y) in sectionBillboards.SelectMany(ring => ring))
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        if (faces.Count == 0 && rawChords.Count == 0 && sectionBillboards.Count == 0 && elevationTubeEdges.Count == 0)
        {
            if (towardViewer == null || siteSilhouettes is not { Count: > 0 }) return null;
            foreach (var (x, y) in siteSilhouettes.SelectMany(ring => ring))
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        if (elevationTubeEdges.Count > 0)
            foreach (var edge in elevationTubeEdges)
            {
                minX = Math.Min(minX, Math.Min(edge.X1, edge.X2)); maxX = Math.Max(maxX, Math.Max(edge.X1, edge.X2));
                minY = Math.Min(minY, Math.Min(edge.Y1, edge.Y2)); maxY = Math.Max(maxY, Math.Max(edge.Y1, edge.Y2));
            }
        // The chords become inked edges on the cutting plane's own key, so the same line coming from two
        // solids sharing the cut surface merges into one below.
        var cutChords = new List<CollectedEdge>();
        long pnx = 0, pny = 0, pnz = 0, poff = 0;
        if (cut && planeOrigin.Length == 3)
        {
            pnx = (long)Math.Round(D[0] * 1000);
            pny = (long)Math.Round(D[1] * 1000);
            pnz = (long)Math.Round(D[2] * 1000);
            poff = (long)Math.Round((planeOrigin[0] * D[0] + planeOrigin[1] * D[1] + planeOrigin[2] * D[2]) * 2.0);
        }
        for (int i = 0; i < rawChords.Count; i++)
        {
            var chord = rawChords[i];
            var edge = new CollectedEdge(chord.X1, chord.Y1, chord.X2, chord.Y2,
                chord.Depth, chord.Depth, -1, -1, pnx, pny, pnz, poff, true, chord.CategoryId);
            if (wallChordSources.TryGetValue(i, out var source) && wallCutRegions.Count > 0)
            {
                // Only interior spans are hidden. Coincident contours and material changes still ink.
                var covers = wallCutRegions.Where(region => region.WallId != source.WallId
                    && region.MaterialId == source.MaterialId).Select(region => region.Ring);
                cutChords.AddRange(VisibleTerrainEdges([edge], covers, [], coverBoundary: false));
            }
            else
            {
                cutChords.Add(edge);
            }
        }
        // Layer boundaries share the exact material bands and keep any spans left by an authored cut edge.
        List<CollectedEdge>? roofLayerEdges = null;
        if (layerBoundaries is { Count: > 0 })
        {
            var sections = new List<CollectedEdge>(cutChords);
            foreach (var layer in layerBoundaries.Distinct())
                sections.Add(new(layer.Start.X, layer.Start.Y, layer.End.X, layer.End.Y,
                    viewPlaneDepth, viewPlaneDepth, -1, -1, pnx, pny, pnz, poff, false, layer.CategoryId));
            var boundaries = TrimProjectedEdgesAtCuts(sections).Where(edge => !edge.IsCut);
            if (paintDetailCuts) cutChords.AddRange(boundaries);
            else roofLayerEdges = boundaries.ToList();
        }
        if (detailPath && viewScale <= 50 && rawChords.Count > 0)
            AddLayerLines(rawChords, document, cutChords, D, H, V, pnx, pny, pnz, poff, sectionedLayerChords);
        HashSet<(long Ax, long Ay, long Bx, long By)>? railSeams = fineSection ? [] : null;
        // Coincident nosing edges can lie at different depths. Keep the front candidate through occlusion.
        bool keepStairDepths = paintDetailCuts || overviewSection;
        var deduped = DedupeEdges(edgeFaces, D, cut ? planeOrigin : null, keepComponentDepths: drawTerrainCut,
            fineSection: fineSection, keepStairDepths: keepStairDepths, railSeams: railSeams,
            keepDoorDepths: keepDoorDepths, keepDetailProfileDepths: paintDetailCuts);
        foreach (var shaftIndex in roundShaftFaces)
        {
            var shaft = faces[shaftIndex];
            // Only the edges behind the full cylinder can be clipped at its whole silhouette.
            double far = shaft.Depth + (shaft.Points.Max(p => p.X) - shaft.Points.Min(p => p.X)) / 2;
            bool BehindShaft(CollectedEdge edge) => !edge.IsCut && edge.NearDepth is double near
                && near > far + 1 && Math.Abs(near - edge.MidDepth) < 1e-6;
            if (faceOccluders[shaftIndex].CategoryId == (int)BuiltInCategory.OST_GenericModel)
            {
                deduped = deduped.SelectMany(edge => BehindShaft(edge)
                    ? VisibleTerrainEdges([edge], [shaft.Points], [], coverBoundary: false)
                    : [edge]).ToList();
                continue;
            }
            var behind = deduped.Where(BehindShaft).ToList();
            deduped.RemoveAll(BehindShaft);
            deduped.AddRange(VisibleTerrainEdges(behind, [shaft.Points], [], coverBoundary: false));
        }
        // The carved chords join the shared-edge pass late on purpose: they are already de-duplicated by
        // construction (one span per inside interval), and the merge below still unions the same line coming
        // from two solids sharing the cut surface.
        deduped.AddRange(cutChords);
        if (paintDetailCuts)
        {
            // Coincident constant-depth copies have the same projected endpoints.
            // Keep the nearest copy before splitting it around solid covers.
            var nearestProfiles = deduped.Where(IsConstantDepthDetailProfile)
                .GroupBy(edge => (ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2),
                    IsMullion: edge.CategoryId == (int)BuiltInCategory.OST_CurtainWallMullions))
                .Select(group => group.MinBy(edge => edge.MidDepth)!).ToList();
            deduped.RemoveAll(IsConstantDepthDetailProfile);
            deduped.AddRange(nearestProfiles);
        }
        if (detailWallOccluders is { Count: > 0 })
        {
            // Split a horizontal wall edge at nearer opaque faces before testing the remaining midpoints.
            deduped = deduped.SelectMany(edge => ClipDetailWallEdge(edge, detailWallOccluders)).ToList();
        }
        var calloutWallSpans = new List<CollectedEdge>();
        var clippedCalloutWalls = new HashSet<((long Ax, long Ay, long Bx, long By) Edge, double Depth)>();
        if (spanOccluders is { Count: > 0 })
        {
            // A nearer face can cover the midpoint of an edge with visible spans on either side.
            var projectedSpans = deduped.Where(NeedsSpanClipping).GroupBy(edge => edge.MidDepth).ToList();
            deduped.RemoveAll(NeedsSpanClipping);
            foreach (var group in projectedSpans)
                deduped.AddRange(VisibleTerrainEdges(group,
                    spanOccluders.Where(mask => mask.FarDepth < group.Key - 1).Select(mask => mask.Points), [],
                    openings: spanOpenings));
            if (opaqueFineSection && !detailPath)
            {
                var slabs = spanOccluders.Where(mask => mask.ViewAlignedSlabSide).ToList();
                if (slabs.Count > 0)
                {
                    // A projected slab can hide the middle of a wall or railing upright while leaving both ends visible.
                    var uprights = deduped.Where(IsSlabCoveredProfile).GroupBy(edge => edge.NearDepth!.Value).ToList();
                    deduped.RemoveAll(IsSlabCoveredProfile);
                    foreach (var group in uprights)
                        deduped.AddRange(VisibleTerrainEdges(group,
                            slabs.Where(mask => mask.FarDepth < group.Key - 1).Select(mask => mask.Points), [],
                            coverBoundary: false));
                }
            }
            if (opaqueFineSection && !regularFineSection)
                foreach (var group in deduped.Where(IsSlopingWall).GroupBy(edge => edge.MidDepth))
                {
                    var masks = spanOccluders.Where(mask => mask.FarDepth < group.Key - 1)
                        .Select(mask => mask.Points).ToList();
                    foreach (var edge in group)
                    {
                        var spans = VisibleTerrainEdges([edge], masks, []).ToList();
                        var key = ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2);
                        if (spans is [var span] && key == ProjectedEdgeKey(span.X1, span.Y1, span.X2, span.Y2))
                            continue;
                        clippedCalloutWalls.Add((key, edge.MidDepth));
                        calloutWallSpans.AddRange(spans);
                    }
                }
        }
        bool NeedsSpanClipping(CollectedEdge edge) =>
            !edge.IsCut && edge.NearDepth is double near && Math.Abs(near - edge.MidDepth) < 1e-6
            && (fineSection && edge.CategoryId == (int)BuiltInCategory.OST_LightingFixtures
                || roundShaftFaces.Contains(edge.FaceA) || roundShaftFaces.Contains(edge.FaceB)
                || regularFineSection && IsSlopingWall(edge)
                || paintDetailCuts && IsVerticalDetailProfile(edge.CategoryId, edge.X1, edge.X2));

        static bool IsSlabCoveredProfile(CollectedEdge edge) =>
            !edge.IsCut && edge.NearDepth is double near && Math.Abs(near - edge.MidDepth) < 1e-6
            && (IsVerticalDetailProfile(edge.CategoryId, edge.X1, edge.X2)
                || edge.CategoryId == (int)BuiltInCategory.OST_Walls && Math.Abs(edge.X1 - edge.X2) < 1e-6);

        static bool IsConstantDepthDetailProfile(CollectedEdge edge) =>
            !edge.IsCut && edge.NearDepth is double near && Math.Abs(near - edge.MidDepth) < 1e-6
            && IsVerticalDetailProfile(edge.CategoryId, edge.X1, edge.X2);

        static bool IsSlopingWall(CollectedEdge edge) =>
            !edge.IsCut && edge.CategoryId == (int)BuiltInCategory.OST_Walls
            && edge.NearDepth is double near && Math.Abs(near - edge.MidDepth) < 1e-6
            && Math.Abs(edge.X1 - edge.X2) > 1e-6 && Math.Abs(edge.Y1 - edge.Y2) > 1e-6;

        IEnumerable<CollectedEdge> ClipDetailWallEdge(CollectedEdge edge,
            List<(List<(double X, double Y)> Points, double Depth)> masks)
        {
            if (edge.IsCut || edge.CategoryId != (int)BuiltInCategory.OST_Walls
                || Math.Abs(edge.Y1 - edge.Y2) >= 1e-6 || edge.NearDepth is not double depth)
                return [edge];
            var plane = edge.FaceA >= 0 ? faceOccluders[edge.FaceA].Plane : null;
            if (plane == null && Math.Abs(depth - edge.MidDepth) >= 1e-6)
                return [edge];
            return VisibleTerrainEdges([edge], masks.Where(face => face.Depth < depth - 1).Select(face => face.Points), [])
                .Select(span => plane == null ? span : span with
                {
                    MidDepth = plane.DepthAt((span.X1 + span.X2) / 2, (span.Y1 + span.Y2) / 2),
                    NearDepth = Math.Min(plane.DepthAt(span.X1, span.Y1), plane.DepthAt(span.X2, span.Y2)),
                });
        }
        var behindRailings = new List<CollectedEdge>();
        var occluded = OccludeEdges(faces, faceOccluders, deduped, !detailPath && viewScale >= 100,
            stairMaskFaces: stairMasks.Count > 0 ? stairMasks : null,
            railingMaskDepths: railingMaskDepths.Count > 0 ? railingMaskDepths : null,
            behindRailings: behindRailings, useDetailPlaneDepth: detailWallOccluders != null,
            stairAssembly: overviewSection ? StairAssembly : null, furnitureCovers: furnitureCovers);
        if (keepStairDepths)
        {
            var stairEdges = occluded.Where(edge => IsStairBodyCategory(edge.CategoryId) && !edge.IsCut)
                .GroupBy(edge => ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2))
                .Select(group => group.MinBy(edge => edge.MidDepth)!).ToList();
            occluded.RemoveAll(edge => IsStairBodyCategory(edge.CategoryId) && !edge.IsCut);
            occluded.AddRange(stairEdges);
        }
        if (viewLines.Count > 0)
            viewLines = OccludeEdges(faces, faceOccluders, viewLines, keepWindowJambs: false,
                furnitureCovers: furnitureCovers);
        if (drawTerrainCut)
        {
            var visibleComponentEdges = occluded.Where(edge => HasCoincidentComponents(edge.CategoryId, !cut))
                .GroupBy(edge => ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2))
                .Select(group => group.MinBy(edge => edge.MidDepth)!).ToList();
            occluded.RemoveAll(edge => HasCoincidentComponents(edge.CategoryId, !cut));
            foreach (var group in visibleComponentEdges.GroupBy(edge => edge.NearDepth))
                occluded.AddRange(group.Key is double depth
                    ? VisibleTerrainEdges(group, plantMasks.Where(mask => mask.Far < depth).Select(mask => mask.Points), [])
                    : group);
        }
        if (stairMasks.Count > 0)
        {
            // Trim visible edges that continue behind the stair.
            occluded = occluded.SelectMany(ClipAtStairs).ToList();
        }
        IEnumerable<CollectedEdge> ClipAtStairs(CollectedEdge edge) =>
            stairMasks.Count > 0 && !edge.IsCut && !IsStairBodyCategory(edge.CategoryId) && edge.NearDepth is double near
                ? VisibleTerrainEdges([edge], stairMasks.Where(mask => faceOccluders[mask.Key].MinDepth < near - 1)
                    .Select(mask => mask.Value), [])
                : new[] { edge };
        bool IsRoundElevationEdge(CollectedEdge edge) =>
            edge.CategoryId == (int)BuiltInCategory.OST_GenericModel
            && (roundShaftFaces.Contains(edge.FaceA) || roundShaftFaces.Contains(edge.FaceB));
        var roundElevationEdges = !cut && roundShaftFaces.Count > 0
            ? occluded.Where(IsRoundElevationEdge).ToList() : null;
        if (roundElevationEdges is { Count: > 0 }) occluded.RemoveAll(IsRoundElevationEdge);
        int sectionPenScale = viewportScale ?? viewScale;
        double wallCutWidth = PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Walls, true,
            sectionPenScale, out double wallPen) ? wallPen : 0.2;
        double CutWidth(CollectedEdge edge) =>
            PenWidths.TryGetWidth(document, edge.CategoryId, true, sectionPenScale, out double width)
                ? width : wallCutWidth;
        if (detailRoofCuts is { Count: > 0 } && detailWallCuts is { Count: > 0 })
            occluded = ResolveRoofWallCutJoins(occluded, detailRoofCuts, detailWallCuts,
                viewPlaneDepth, (pnx, pny, pnz, poff));
        List<CollectedEdge> edges = MergeOverlaps(occluded, paintDetailCuts ? CutWidth : null);
        // Every visible edge inks, horizontals included: the reference prints the wall top, the slab and roof
        // outlines and the ground line. (An earlier rule dropped model horizontals because the reference's vector
        // layer lacks them вЂ” but on A101/A103/A104/A105 the reference carries the drawing as a raster image and only
        // the annotation as vector, so that comparison measured an export artifact.)
        // A detail's own annotation curves: Revit stores detail and insulation lines of a view as curve
        // elements that name the view as their owner (OST_Lines, OST_InsulationLines), with their points in
        // model feet вЂ” they lie on the view plane and belong to no solid, so no face ever reaches them.
        List<CollectedEdge>? insulationLines = null;
        if (planeView != null)
        {
            foreach (var element in document.Elements)
            {
                if (element.OwnerViewId != planeView.Id || hiddenElements?.Contains(element.Id) == true
                    || element.Location is not { CurveType: "Line" } curve) continue;
                if (element.CategoryId is not ((int)BuiltInCategory.OST_Lines
                    or (int)BuiltInCategory.OST_InsulationLines or (int)BuiltInCategory.OST_SketchLines))
                    continue;
                if (curve.StartPoint.Length < 3 || curve.EndPoint.Length < 3) continue;
                var a = curve.StartPoint;
                var b = curve.EndPoint;
                double ax = (a[0] * H[0] + a[1] * H[1] + a[2] * H[2]) * FeetToMillimetres;
                double ay = (a[0] * V[0] + a[1] * V[1] + a[2] * V[2]) * FeetToMillimetres;
                double bx = (b[0] * H[0] + b[1] * H[1] + b[2] * H[2]) * FeetToMillimetres;
                double by = (b[0] * V[0] + b[1] * V[1] + b[2] * V[2]) * FeetToMillimetres;
                if (Math.Abs(bx - ax) + Math.Abs(by - ay) < 1e-6) continue;
                if (isDetail && cropWindow != null && element.DetailInsulation is { } insulation
                    // shortcut: unit-scale batt profile; retain the curve until non-unit scaling is verified.
                    && Math.Abs(insulation.Scale - 1) < 1e-9)
                {
                    var strokes = DetailAnnotations.BattInsulation(ax, ay, bx, by,
                        insulation.Width * FeetToMillimetres, insulation.Mirrored).ToList();
                    if (strokes.Count > 0)
                    {
                        (insulationLines ??= []).AddRange(strokes);
                        continue;
                    }
                }
                // The file keeps annotation the viewport does not show (a line drawn past the crop edge):
                // only the part inside the crop is printed, and it does not stretch the drawing's frame.
                if (cropWindow is { } curveCrop)
                {
                    if (!ClipSegment(ax, ay, bx, by, curveCrop.X0, curveCrop.Y0, curveCrop.X1, curveCrop.Y1,
                        out ax, out ay, out bx, out by)) continue;
                    if (Math.Abs(bx - ax) + Math.Abs(by - ay) < 1e-6) continue;
                }
                edges.Add(new CollectedEdge(ax, ay, bx, by, 0, 0, -1, -1, 0, 0, 0, 0,
                    false, element.CategoryId ?? 0));
                minX = Math.Min(minX, Math.Min(ax, bx)); maxX = Math.Max(maxX, Math.Max(ax, bx));
                minY = Math.Min(minY, Math.Min(ay, by)); maxY = Math.Max(maxY, Math.Max(ay, by));
            }
        }
        // A detail keeps its fine construction lines; anywhere else coincident modeling
        // (a railing family drawing the same board four times) inks once.
        if (window == null && !isDetail)
        {
            double elevationRailPenWidth = 0;
            if (!cut && planeOrigin.Length == 3)
            {
                int scale = viewportScale ?? viewScale;
                double outlinePen = PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Walls,
                    false, scale, out double width) ? width : DefaultOutlinePen;
                elevationRailPenWidth = scale * outlinePen;
            }
            edges = ConsolidateCoincident(edges, keepFineSectionProfiles: fineSection,
                elevationRailPenWidth: elevationRailPenWidth);
        }
        if (roundElevationEdges is { Count: > 0 })
        {
            // Both sides of a round shaft are distinct even when they are closer than the merge tolerance.
            var inked = edges.Select(edge => ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2)).ToHashSet();
            foreach (var edge in roundElevationEdges)
                if (inked.Add(ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2))) edges.Add(edge);
        }
        if (roofInsertFaces.Count > 0)
        {
            // Resolve the two sides of roof inserts before reducing them to projected lines.
            var roofEdges = DedupeEdges(edgeFaces.Where(face => roofInsertFaces.Contains(face.FaceIndex)).ToList(),
                D, cut ? planeOrigin : null, roofInsertFaces: roofInsertFaces);
            var visibleRoofEdges = OccludeEdges(faces, faceOccluders, roofEdges, false, roofInsertFaces)
                .GroupBy(edge => ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2))
                .Select(group => group.MinBy(edge => edge.MidDepth)!).ToList();
            edges.RemoveAll(edge => roofInsertFaces.Contains(edge.FaceA));
            edges.AddRange(window == null && !isDetail ? ConsolidateCoincident(visibleRoofEdges) : visibleRoofEdges);
        }
        if (topRailEdges.Count > 0)
        {
            // Projected duplicates are resolved after visibility; a back face can share the visible face's boundary.
            var inked = edges.Select(edge => ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2)).ToHashSet();
            var visible = OccludeEdges(faces, faceOccluders, topRailEdges, keepWindowJambs: false);
            foreach (var edge in visible)
                if (inked.Add(ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2))) edges.Add(edge);
        }
        if (railingMaskDepths.Count > 0)
        {
            // Consolidate the original strokes first so subdividing jambs cannot change which rail edges survive.
            edges = edges.SelectMany(ClipAtRailings).ToList();
            var inked = edges.Select(edge => ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2)).ToHashSet();
            var restored = MergeOverlaps(behindRailings.SelectMany(ClipAtStairs).SelectMany(ClipAtRailings).ToList());
            // Fine railing profiles can have two visible edges just millimetres apart.
            var jambs = ConsolidateCoincident(restored.FindAll(edge => !IsRailingCategory(edge.CategoryId)));
            foreach (var edge in jambs.Concat(restored.Where(edge => IsRailingCategory(edge.CategoryId))))
                if (inked.Add(ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2))) edges.Add(edge);
        }
        IEnumerable<CollectedEdge> ClipAtRailings(CollectedEdge edge) =>
            RailingClipDepth(edge) is double near
                ? VisibleTerrainEdges([edge], railingMaskDepths.Where(mask => mask.Value < near
                        && ClipsAtBaluster(edge, faceOccluders[mask.Key].CategoryId))
                    .Select(mask => faces[mask.Key].Points), [], coverBoundary: false)
                : new[] { edge };
        if (meshCovers != null && meshSilhouettes.Count > 0)
        {
            // Replace the owning mesh's strokes after resolving the adjacent model's edge candidates.
            var meshOwners = meshSilhouettes.Select(edge => edge.SilhouetteOwnerId).ToHashSet();
            edges.RemoveAll(edge => !edge.IsCut && edge.FaceA >= 0
                && meshOwners.Contains(faceOccluders[edge.FaceA].ElementId));
            var spans = new List<CollectedEdge>();
            foreach (var group in meshSilhouettes.GroupBy(edge => (edge.NearDepth, edge.SilhouetteOwnerId)))
            {
                var covers = meshCovers.Where(face => face.ElementId != group.Key.SilhouetteOwnerId
                    && face.FarDepth < group.Key.NearDepth - 1).Select(face => face.Points);
                if (modelSectionMasks != null) covers = covers.Concat(modelSectionMasks);
                spans.AddRange(VisibleTerrainEdges(group, covers, [], coverBoundary: false));
            }
            edges.AddRange(MergeOverlaps(OccludeEdges(faces, faceOccluders, spans, keepWindowJambs: false)));
        }
        if (terrainEdges.Count > 0)
        {
            var terrainMasks = !cut ? terrainForeground
                : terrainSectionMasks.Count > 0 ? terrainSectionMasks : faces.Select(face => face.Points);
            var terrainProfile = MergeOverlaps(terrainEdges);
            var visibleGrade = VisibleTerrainEdges(terrainProfile, terrainMasks, terrainPads).ToList();
            if (cropWindow is { } gradeCrop)
            {
                foreach (var edge in visibleGrade)
                    if (ClipSegment(edge.X1, edge.Y1, edge.X2, edge.Y2, gradeCrop.X0, gradeCrop.Y0,
                        gradeCrop.X1, gradeCrop.Y1, out double x1, out double y1, out double x2, out double y2))
                        edges.Add(edge with { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
            }
            else
            {
                edges.AddRange(visibleGrade);
            }
            if (document.RawData.SiteCutGraphics is { } site
                && document.RawData.MaterialPatterns?.TryGetValue(site.MaterialId, out var material) == true
                && document.RawData.FillPatterns?.TryGetValue(material.CutPatternId, out var pattern) == true
                && pattern.Grids.Count > 0)
            {
                var hatchProfile = cut ? VisibleTerrainEdges(terrainProfile, [], terrainPads) : visibleGrade;
                // Drafting grids share the stored view origin across the profile and pad spans.
                groundHatch = new TerrainHatch(
                    planeOrigin[0] * H[0] + planeOrigin[1] * H[1] + planeOrigin[2] * H[2],
                    planeOrigin[0] * V[0] + planeOrigin[1] * V[1] + planeOrigin[2] * V[2],
                    site.BaseFeet * FeetToMillimetres, hatchProfile.Concat(padProfile).ToList(), pattern.Grids,
                    terrainSectionMasks.Count > 0 ? terrainSectionMasks : null);
                if ((!cut || groundHatch.SectionMasks != null) && FillHatch.DraftingTiles(pattern.Grids, 0, 0, 0.1) is { Count: > 0 })
                {
                    bool BehindGround(CollectedEdge edge) =>
                        !edge.IsCut && edge.FaceA >= 0 && edge.MidDepth > viewPlaneDepth + 1;
                    var covers = GroundSpans(groundHatch, groundHatch.BaseY);
                    if (!cut) covers = covers.Concat(terrainSectionMasks);
                    var projected = edges.Where(BehindGround).ToList();
                    var visible = VisibleTerrainEdges(projected, covers, [], coverBoundary: false).ToList();
                    if (!cut && spanOccluders is { Count: > 0 })
                    {
                        // The original midpoint can be outside a nearer face even when the grade only leaves a span behind it.
                        var originalSpans = projected.ToHashSet();
                        bool NeedsModelClip(CollectedEdge edge) => edge.NearDepth.HasValue
                            && edge.CategoryId is ((int)BuiltInCategory.OST_Walls or (int)BuiltInCategory.OST_StructuralFoundation)
                            && (!originalSpans.Contains(edge) || WithinPadCut(edge));
                        bool WithinPadCut(CollectedEdge edge)
                        {
                            if (Math.Abs(edge.X1 - edge.X2) >= 1e-6) return false;
                            double x = edge.X1;
                            return terrainPads.Any(pad => x > pad.From && x < pad.To)
                                && terrainProfile.Any(grade => x >= Math.Min(grade.X1, grade.X2)
                                    && x <= Math.Max(grade.X1, grade.X2) && grade.X1 != grade.X2
                                    && Math.Min(edge.Y1, edge.Y2) < grade.Y1
                                        + (x - grade.X1) * (grade.Y2 - grade.Y1) / (grade.X2 - grade.X1));
                        }
                        var trimmed = visible.Where(NeedsModelClip).GroupBy(edge => edge.NearDepth!.Value).ToList();
                        visible.RemoveAll(NeedsModelClip);
                        foreach (var group in trimmed)
                            visible.AddRange(VisibleTerrainEdges(group,
                                spanOccluders.Where(mask => mask.FarDepth < group.Key - 1).Select(mask => mask.Points), [],
                                coverBoundary: false, openings: spanOpenings));
                    }
                    edges.RemoveAll(edge => BehindGround(edge));
                    edges.AddRange(visible);
                }
            }
        }
        edges.AddRange(elevationCutEdges);
        // Clip after merging so the selection of other coincident edges stays independent of these spans.
        if (sectionWallFaces.Count > 1)
        {
            var wallFacesByFinish = sectionWallFaces.ToLookup(face => (face.Value.TypeId, face.Value.MaterialId));
            var trimmedSpans = new List<CollectedEdge>();
            edges = edges.SelectMany(ClipWallOverlap).ToList();
            if (trimmedSpans.Count > 0)
            {
                var visible = OccludeEdges(faces, faceOccluders, trimmedSpans, keepWindowJambs: false);
                var hidden = trimmedSpans.Except(visible).ToHashSet();
                edges.RemoveAll(hidden.Contains);
            }

            IEnumerable<CollectedEdge> ClipWallOverlap(CollectedEdge edge)
            {
                if (edge.IsCut || edge.ViewLineStyleId != 0 || edge.NearDepth is not double near
                    || Math.Abs(edge.MidDepth - near) >= 1e-6
                    || !sectionWallFaces.TryGetValue(edge.FaceA, out var wall)) return [edge];

                int elementId = faceOccluders[edge.FaceA].ElementId;
                var covers = wallFacesByFinish[(wall.TypeId, wall.MaterialId)]
                    .Where(face => faceOccluders[face.Key].ElementId != elementId
                        && Math.Abs(face.Value.Depth - wall.Depth) < 1e-6)
                    .Select(face => faces[face.Key].Points).ToList();
                if (covers.Count == 0) return [edge];
                var spans = VisibleTerrainEdges([edge], covers, [], coverBoundary: false).ToList();
                if (spans is [var span] && Math.Abs(span.X2 - edge.X2) < 1e-6 && Math.Abs(span.Y2 - edge.Y2) < 1e-6
                    && Math.Abs(span.X1 - edge.X1) < 1e-6 && Math.Abs(span.Y1 - edge.Y1) < 1e-6) return [edge];
                trimmedSpans.AddRange(spans);
                return spans;
            }
        }
        if (sectionWallFaces.Count > 0)
            edges = edges.SelectMany(ClipSlopingRailBehindWall).ToList();

        IEnumerable<CollectedEdge> ClipSlopingRailBehindWall(CollectedEdge edge)
        {
            if (edge.IsCut || edge.ViewLineStyleId != 0 || !IsRailingCategory(edge.CategoryId)
                || edge.NearDepth is not double near || Math.Abs(near - edge.MidDepth) >= 1e-6
                || Math.Abs(edge.X1 - edge.X2) < 1e-6 || Math.Abs(edge.Y1 - edge.Y2) < 1e-6)
                return [edge];

            var covers = sectionWallFaces.Where(face => face.Value.Depth < near - 1)
                .Select(face => faces[face.Key].Points);
            return VisibleTerrainEdges([edge], covers, [], coverBoundary: false);
        }
        // The site around the building is drawn, but it does not decide the frame: the building does, with a little
        // air around it for the trees and the ground. Context beyond that air (a tree far down the site) is dropped,
        // so the drawing stays the size of the building.
        if (context.Count > 0)
        {
            double breadth = maxX - minX, heightExtent = maxY - minY;
            double keepLeft = minX - breadth * 0.25, keepRight = maxX + breadth * 0.25;
            double keepBottom = minY - heightExtent * 0.08, keepTop = maxY + heightExtent * 0.08;
            context.RemoveAll(shape => (!shape.Terrain || terrainFillCrop == null) &&
                shape.Points.Any(point => point.X < keepLeft || point.X > keepRight
                    || point.Y < keepBottom || point.Y > keepTop));
            minX -= breadth * 0.02;
            maxX += breadth * 0.02;
            minY -= heightExtent * 0.02;
            maxY += heightExtent * 0.02;
        }
        // The levels of the model and the grids that stand across the view are drawn dashed, as an elevation prints
        // them, with the level name and the grid bubble. Only the level records of the project count: the families
        // each carry their own reference levels (dozens sharing the category), which no project view shows.
        var levels = new List<(double Y, string Label, Element Plane)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Levels || hiddenElements?.Contains(element.Id) == true) continue;
            if (element.RootClassName != "Level") continue;
            if (element.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value is not double elevation) continue;
            levels.Add((elevation * FeetToMillimetres, element.Name ?? "", element));
        }
        levels.Sort((left, right) => left.Y.CompareTo(right.Y));
        // The project holds many level elements at the same height (one per family); an elevation notes each height
        // once, with the first name it finds. The line of a level runs to its datum envelope projected on the
        // view's right axis (rvt-prt datums.py); without a decoded plane it spans the content like before.
        var distinctLevels = new List<(double Y, string Label, double X0, double X1)>();
        var nativeLevels = new Dictionary<double, NativeLevelLine>();
        var defaultLevelHeads = new List<(double Y, int Type, double Left, double Right, double Radius, int SymbolId)>();
        var savedModelExtentHeads = new List<(int Type, double Left, double Right, double Head)>();
        var datumExtents = planeView != null ? document.RawData.DatumExtents?.GetValueOrDefault(planeView.Id) : null;
        if (datumExtents != null && Math.Abs(datumExtents.Normal[0] * D[0] + datumExtents.Normal[1] * D[1]
            + datumExtents.Normal[2] * D[2] - 1) > 1e-6) datumExtents = null;
        foreach (var level in levels)
        {
            if (distinctLevels.Count > 0 && Math.Abs(distinctLevels[^1].Y - level.Y) < 1.0)
            {
                if (distinctLevels[^1].Label.Length == 0)
                    distinctLevels[^1] = (distinctLevels[^1].Y, level.Label, distinctLevels[^1].X0, distinctLevels[^1].X1);
                continue;
            }
            double x0 = minX, x1 = maxX;
            if (level.Plane.LevelPlane is { Envelope.Length: 4, Origin.Length: 3, XVector.Length: 3, YVector.Length: 3 } plane)
            {
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                for (int corner = 0; corner < 4; corner++)
                {
                    double u = plane.Envelope[corner == 0 || corner == 3 ? 0 : 2];
                    double v = plane.Envelope[corner < 2 ? 1 : 3];
                    double s = (plane.Origin[0] + u * plane.XVector[0] + v * plane.YVector[0]) * H[0]
                        + (plane.Origin[1] + u * plane.XVector[1] + v * plane.YVector[1]) * H[1]
                        + (plane.Origin[2] + u * plane.XVector[2] + v * plane.YVector[2]) * H[2];
                    lo = Math.Min(lo, s);
                    hi = Math.Max(hi, s);
                }
                if (hi > lo && double.IsFinite(lo) && double.IsFinite(hi))
                {
                    x0 = lo * FeetToMillimetres;
                    x1 = hi * FeetToMillimetres;
                }
            }
            distinctLevels.Add((level.Y, level.Label, x0, x1));
            if (level.Plane.LevelPlane is not { Origin: var levelOrigin }
                || document.RawData.DatumHeads?.GetValueOrDefault(level.Plane.TypeId) is not { } head
                || CircleHeadRadius(document, head.SymbolId, "M_Level Head - Circle") is not double radius) continue;
            var extent = datumExtents?.Datums.GetValueOrDefault(level.Plane.Id);
            if (extent == null && sectionAnnotationCrop is { } annotation)
            {
                double left = Math.Max(x0, annotation.X0), right = Math.Min(x1, annotation.X1);
                if (right > left)
                    nativeLevels.Add(level.Y, new NativeLevelLine(left, right, level.Y, right, radius, null, head.SymbolId));
            }
            else if (extent == null && datumExtents != null)
            {
                defaultLevelHeads.Add((level.Y, level.Plane.TypeId, x0, x1, radius, head.SymbolId));
            }
            // shortcut: support the authored right head until left and dual heads are decoded.
            else if (extent is { Overrides: 0 or 0x20 or 0x30, FreeEndLeader: null }
                && (extent.Overrides == 0x30 ? extent.FreeEnd : extent.FreeEnd3d) is double freeEnd
                && double.IsFinite(freeEnd))
            {
                double datumOrigin = Project(levelOrigin, H);
                double left = datumOrigin + freeEnd * FeetToMillimetres;
                // A leader remains authored when both endpoints use the model extent.
                if (extent is { Overrides: 0, BubbleLeader: null }) continue;
                double headX = extent.Overrides == 0
                    ? Project(extent.BubbleLeader!.Anchor, H)
                    : datumOrigin + extent.BubbleEnd * FeetToMillimetres;
                if (!(headX > left)) continue;
                var line = new NativeLevelLine(left, headX, level.Y, headX, radius, null, head.SymbolId);
                if (extent.BubbleLeader is { } leader)
                {
                    double endX = Project(leader.End, H);
                    double endY = Project(leader.End, V);
                    var elbow = (X: Project(leader.Elbow, H), Y: Project(leader.Elbow, V));
                    double headY = Project(leader.Anchor, V);
                    if (Math.Abs(endY - level.Y) > 1 || Math.Abs(Project(leader.Anchor, H) - headX) > 1
                        || endX < left || elbow.X < endX || headX < elbow.X) continue;
                    if (Math.Abs(headY - level.Y) > 1 || Math.Abs(elbow.Y - level.Y) > 1)
                        line = line with { HeadY = headY, LineEnd = endX, Elbow = elbow };
                }
                nativeLevels.Add(level.Y, line);
                if (extent.Overrides == 0 && extent.BubbleEnd3d is double modelRight
                    && headX <= x1 && Math.Abs(left - x0) <= 1
                    && Math.Abs(datumOrigin + modelRight * FeetToMillimetres - x1) <= 1)
                    savedModelExtentHeads.Add((level.Plane.TypeId, x0, x1, headX));
            }
        }
        foreach (var level in defaultLevelHeads)
        {
            // Model-extent leaders can expose the shared head column even when straight levels have no view record.
            var heads = savedModelExtentHeads.Where(head => head.Type == level.Type
                && Math.Abs(head.Left - level.Left) <= 1 && Math.Abs(head.Right - level.Right) <= 1)
                .Select(head => head.Head).ToList();
            if (heads.Count >= 2 && heads.Max() - heads.Min() <= 1)
                nativeLevels.Add(level.Y, new NativeLevelLine(level.Left, heads[0], level.Y, heads[0],
                    level.Radius, null, level.SymbolId));
        }
        var grids = new List<(double X, string Name)>();
        var nativeGrids = new Dictionary<double, NativeGridLine>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Grids || hiddenElements?.Contains(element.Id) == true) continue;
            if (element.Location is not { CurveType: "Line" } line) continue;
            double dx = line.EndPoint[0] - line.StartPoint[0], dy = line.EndPoint[1] - line.StartPoint[1];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) continue;
            if (Math.Abs(dx / length * H[0] + dy / length * H[1]) > 0.2) continue;
            double x = (line.StartPoint[0] * H[0] + line.StartPoint[1] * H[1]) * FeetToMillimetres;
            grids.Add((x, element.Name ?? ""));
            if (planeView is { ViewOrigin.Length: 3 } && V[2] > 0.999
                && element.GridPlane is { Envelope.Length: 4 } gridPlane
                && document.RawData.DatumHeads?.GetValueOrDefault(element.TypeId) is { GridBubblePosition: 2 } head
                && CircleHeadRadius(document, head.SymbolId, "M_Grid Head - Circle") is double radius)
            {
                var extent = datumExtents?.Datums.GetValueOrDefault(element.Id);
                bool authored = extent is { Overrides: 0x20 or 0x30, BubbleLeader: null, FreeEndLeader: null,
                    FreeEnd3d: not null, BubbleEnd3d: not null };
                bool unboundedSection = fineSection && !detailPath && !stairsOnly && sectionCrop != null
                    && planeView.ViewAnnotationCropActive is false && extent == null;
                if (!authored && !unboundedSection && (detailPath || stairsOnly || viewScale < 100)
                    && (sectionAnnotationCrop == null || extent != null)) continue;
                var e = gridPlane.Envelope;
                double datumOrigin = Project(gridPlane.Origin, V);
                double u = Project(gridPlane.XVector, V);
                double v = Project(gridPlane.YVector, V);
                double bottom = datumOrigin + Math.Min(u * e[0], u * e[2]) + Math.Min(v * e[1], v * e[3]);
                double top = datumOrigin + Math.Max(u * e[0], u * e[2]) + Math.Max(v * e[1], v * e[3]);
                if (authored)
                {
                    // The stored 2D and 3D values share an origin, even when the detail view itself moved.
                    top += (extent!.BubbleEnd - extent.BubbleEnd3d!.Value) * FeetToMillimetres;
                    if (extent.Overrides == 0x30)
                        bottom += (extent.FreeEnd - extent.FreeEnd3d!.Value) * FeetToMillimetres;
                }
                else if (sectionAnnotationCrop is { } annotation)
                {
                    bottom = Math.Max(bottom, annotation.Y0);
                    top = Math.Min(top, annotation.Y1);
                }
                else if (unboundedSection)
                {
                    // The default grid envelope includes the head above the end of the line.
                    top -= 2 * radius * (viewportScale ?? viewScale);
                }
                if (double.IsFinite(bottom) && double.IsFinite(top) && top > bottom)
                    nativeGrids.TryAdd(x, new NativeGridLine(bottom, top, radius));
            }
        }
        grids.Sort((left, right) => left.X.CompareTo(right.X));
        context.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        // Detail datums need authored ends or a decoded annotation crop. A detail without a model crop
        // also omits the level rows because their visible height is unknown.
        if (window != null || isDetail)
        {
            if (cropWindow == null) distinctLevels.Clear();
            grids.RemoveAll(grid => !nativeGrids.ContainsKey(grid.X));
        }
        // The grid bubbles print above the frame: without grids (a windowed detail) the frame ends at the model.
        if (grids.Count > 0) maxY += 4500;
        var detailBreaks = isDetail && cropWindow != null && planeView != null
            ? DetailAnnotations.BreakLines(document, planeView.Id, H, V) : null;
        var surfaceHatches = surfacePatterns.Count == 0 && dashedSurfacePatterns.Count == 0 && regionHatches == null
            ? null : new SurfaceHatches(
            planeOrigin[0] * H[0] + planeOrigin[1] * H[1] + planeOrigin[2] * H[2],
            planeOrigin[0] * V[0] + planeOrigin[1] * V[1] + planeOrigin[2] * V[2], surfacePatterns, dashedSurfacePatterns,
            UseClippedLines: opaqueFineSection && !detailPath, Regions: regionHatches);
        // shortcut: use wholly nearer masks for site meshes until their edges carry per-point depths.
        foreach (var group in viewLines.GroupBy(edge => edge.NearDepth))
            edges.AddRange(VisibleTerrainEdges(group, plantMasks.Where(mask => mask.Far < group.Key).Select(mask => mask.Points), []));
        // Keep regular section layer edges out of the selection of coincident model edges.
        if (roofLayerEdges != null) edges.AddRange(roofLayerEdges);
        if (clippedCalloutWalls.Count > 0)
        {
            // Adding the split spans earlier can change the selection of unrelated coincident edges.
            edges.RemoveAll(edge => IsSlopingWall(edge)
                && clippedCalloutWalls.Contains((ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2), edge.MidDepth)));
            var visibleSlopes = OccludeEdges(faces, faceOccluders, calloutWallSpans, keepWindowJambs: false,
                useDetailPlaneDepth: detailWallOccluders != null);
            edges.AddRange(MergeOverlaps(visibleSlopes.SelectMany(ClipAtStairs).SelectMany(ClipAtRailings).ToList()));
        }
        var cutFaces = paintDetailCuts ? terrainSectionMasks : modelSectionMasks;
        if (cutFaces is { Count: > 0 })
        {
            bool BehindCap(CollectedEdge edge) =>
                edge.FaceA >= 0 && !edge.IsCut && edge.NearDepth > viewPlaneDepth + 1;
            var visible = VisibleTerrainEdges(edges.Where(BehindCap), cutFaces, [], coverBoundary: false).ToList();
            edges.RemoveAll(edge => BehindCap(edge));
            edges.AddRange(visible);
        }
        List<(double X, double Y)>? soilReceiver = null;
        if (shadows != null && groundHatch != null && shadowTerrainCrop is { } soilBounds)
            soilReceiver = shadows.AddSoilCutReceiver(soilBounds, viewPlaneDepth, groundHatch.BaseY);
        var castShadows = shadows?.Project(viewPlaneDepth);
        if (groundHatch != null && soilReceiver != null && castShadows?.Remove(soilReceiver, out var soilShadow) == true)
            groundHatch = groundHatch with { Shadow = soilShadow };
        if (terrainOcclusion != null)
        {
            sectionTerrain?.Sort();
            edges = terrainOcclusion.ClipEdges(edges, cut);
        }
        if (!cut && drawTerrainCut && showSurfacePatterns)
        {
            var inked = edges.Select(edge => ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2)).ToHashSet();
            // A projected stair edge may have lost its nearer copy during ordinary mesh edge selection.
            var missing = deduped.Where(edge => IsStairBodyCategory(edge.CategoryId)
                    && !inked.Contains(ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2)))
                .GroupBy(edge => ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2))
                .ToDictionary(group => group.Key, group => group.Min(edge => edge.MidDepth));
            if (missing.Count > 0)
            {
                var nearer = DedupeEdges(edgeFaces.Where(face => IsStairBodyCategory(face.CategoryId)).ToList(),
                        D, keepStairDepths: true)
                    .Where(edge => missing.TryGetValue(ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2),
                        out double farDepth) && edge.MidDepth < farDepth - 1).ToList();
                var visible = OccludeEdges(faces, faceOccluders, nearer, keepWindowJambs: false);
                if (terrainOcclusion != null) visible = terrainOcclusion.ClipEdges(visible, cut: false);
                foreach (var edge in ConsolidateCoincident(visible))
                    if (inked.Add(ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2))) edges.Add(edge);
            }
        }
        if (elevationTubeEdges.Count > 0)
        {
            var visible = OccludeEdges(faces, faceOccluders, elevationTubeEdges, keepWindowJambs: false);
            if (terrainOcclusion != null) visible = terrainOcclusion.ClipEdges(visible, cut: false, requireFace: false);
            var inked = edges.Select(edge => ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2)).ToHashSet();
            foreach (var edge in ConsolidateCoincident(visible))
                if (inked.Add(ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2))) edges.Add(edge);
        }
        if (sectionBillboards.Count > 0)
        {
            var points = sectionBillboards.SelectMany(ring => ring).ToArray();
            double left = points.Min(point => point.X), right = points.Max(point => point.X);
            double bottom = points.Min(point => point.Y), top = points.Max(point => point.Y);
            bool BehindBillboard(CollectedEdge edge) => edge.FaceA >= 0 && !edge.IsCut
                && edge.ViewLineStyleId == 0 && edge.MidDepth >= viewPlaneDepth
                && Math.Min(edge.X1, edge.X2) < right && Math.Max(edge.X1, edge.X2) > left
                && Math.Min(edge.Y1, edge.Y2) < top && Math.Max(edge.Y1, edge.Y2) > bottom;
            var visible = VisibleTerrainEdges(edges.Where(BehindBillboard), sectionBillboards, [],
                coverBoundary: false).ToList();
            edges.RemoveAll(edge => BehindBillboard(edge));
            edges.AddRange(visible);
        }
        // Removing seams earlier can change which other coincident model edge is selected.
        if (railSeams is { Count: > 0 })
            edges.RemoveAll(edge => !edge.IsCut && edge.ViewLineStyleId == 0 && IsRailingCategory(edge.CategoryId)
                && railSeams.Contains(ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2)));
        var siteOutlines = siteObjects.Count == 0 ? null : siteObjects.SelectMany(site =>
            VisibleTerrainEdges(site.Edges, plantMasks.Where(mask => mask.Far < site.Near).Select(mask => mask.Points)
                .Concat(site.WireMesh
                    ? siteModelMasks.Where(mask => mask.Near >= viewPlaneDepth && mask.Far < site.Near)
                        .Select(mask => mask.Points)
                    : faces.Select(face => face.Points)), [])).ToList();
        if (plantingOutlines != null)
        {
            var ground = groundHatch != null ? GroundSpans(groundHatch, groundHatch.BaseY).ToList() : [];
            var outlines = plantingOutlines.Project(ground).ToList();
            if (outlines.Count > 0) (siteOutlines ??= []).AddRange(outlines);
        }
        if (cropWindow is { } finalCrop)
        {
            // Layer boundaries and roof-wall seams can be derived from full host rings after the faces were clipped.
            edges = edges.Select(edge => ClipCollectedEdge(edge, finalCrop)).OfType<CollectedEdge>().ToList();
        }
        var ambientTones = ambient?.Calculate(paintDetailCuts ? cutFaces : null, viewPlaneDepth);
        var paneTones = screenPaneOwners is { Count: > 0 } && screenWallReceivers is { Count: > 0 }
            && sectionCrop is { } screenCrop
            ? new PaneScreenShadows(screenCrop, viewportScale ?? viewScale, ambientFraction)
                .Calculate(faces, faceOccluders.Select(owner => owner.ElementId).ToArray(), paintOpenings,
                    screenPaneOwners, screenWallReceivers, ambientTones)
            : null;
        // Visibility uses face indices in collection order. Reorder the fills only for painting.
        faces.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        return new ElevationContent(faces, distinctLevels, grids, context, edges, minX, minY, maxX, maxY,
            groundHatch, nativeLevels, nativeGrids, insulationLines, detailBreaks, layerHatches,
            CircularAnnotations.Project(document, planeView?.Id, H, V), surfaceHatches,
            modelHatches.Count > 0 ? modelHatches : null, castShadows, skyGradient,
            siteSilhouettes is { Count: > 0 } ? siteSilhouettes : null,
            opaqueSection && (drawGround && !detailPath || paintDetailCuts && ambient != null),
            siteOutlines,
            paintOpenings.Count > 0 ? paintOpenings : null, shadows?.ClipSiteFacetsToModel(castShadows),
            DetailCutFaces: cutFaces is { Count: > 0 } ? cutFaces : null,
            Glazing: glazing is { HasPanes: true } ? glazing : null,
            AmbientTones: ambientTones,
            FaceFills: faceFills.Count > 0 ? faceFills : null,
            DetailGlass: detailGlass is { Count: > 0 } ? detailGlass : null,
            TerrainClippedFaces: terrainOcclusion?.ClippedFaces(),
            KeepDoorDepths: keepDoorDepths,
            SectionBillboards: sectionBillboards.Count > 0 ? sectionBillboards : null,
            PlantTones: ElevationShadows.VisiblePlantTones(plantFacets, plantTones),
            ExteriorWallTones: exteriorWallTones.Count > 0 ? exteriorWallTones : null,
            PaneTones: paneTones);

        System.Numerics.Vector3 AmbientVector(double[] point, double units) =>
            new((float)Project(point, H, units), (float)Project(point, V, units), (float)Project(point, D, units));

        int StairAssembly(int id) =>
            document.GetElement(id)?.SerializedOwningElementId is > 0 and int owner
            && document.GetElement(owner)?.CategoryId == (int)BuiltInCategory.OST_Stairs ? owner : id;

        static bool PlacedSiteElement(Element element) => !element.IsDiscovered
            && element.SerializedUnplacedOwnerId is not > 0 && element.NativeIsSymbol != true
            && !element.RootClassIs("FamilySymbol");

        bool HasEdgeOnRoofGlazing(List<GeoFace> geometryFaces)
        {
            bool hasPane = false;
            foreach (var face in geometryFaces)
            {
                // The thickness of the pane has vertical faces even when the pane itself is seen edge-on.
                if (!face.PlaneSurface || Math.Abs(face.Normal[2]) <= 1e-9
                    || document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1)
                        ?.SerializedMaterialTransparency is not > 0)
                    continue;
                if (Math.Abs(Project(face.Normal, D, 1)) > 1e-9) return false;
                hasPane = true;
            }
            return hasPane;
        }

        bool HasGlazingPattern(GeoFace face) =>
            (face.RenderStyleId ?? face.NestedRenderStyleId) is > 0 and int material
            && document.GetElement(material)?.SerializedMaterialTransparency is > 0
            && document.RawData.MaterialPatterns?.TryGetValue(material, out var pattern) == true
            && pattern.SurfacePatternColor == 0 && pattern.SurfacePatternId == face.ModelPattern?.PatternId;

        static bool IsProjectedRectangle(List<(double X, double Y)> points) =>
            points.Count == 4 && Math.Abs(PlanPrinter.SignedArea(points)) > 1e-6
            && points.Zip(points.Skip(1).Append(points[0])).All(edge =>
                Math.Abs(edge.First.X - edge.Second.X) < 1e-6 || Math.Abs(edge.First.Y - edge.Second.Y) < 1e-6);

        static bool RectanglesOverlap(List<(double X, double Y)> a, List<(double X, double Y)> b) =>
            Math.Min(a.Max(point => point.X), b.Max(point => point.X))
                - Math.Max(a.Min(point => point.X), b.Min(point => point.X)) > 1e-6
            && Math.Min(a.Max(point => point.Y), b.Max(point => point.Y))
                - Math.Max(a.Min(point => point.Y), b.Min(point => point.Y)) > 1e-6;

        List<(double X, double Y)> ProjectRing(List<double[]> vertices)
        {
            var ring = vertices.Select(p => p.Select(value => value * FeetToMillimetres).ToArray()).ToList();
            if (cut || drawTerrainCut) ring = ClipBehind(ring, planeOrigin, D);
            var points = ring.Select(p => (X: Project(p, H, 1), Y: Project(p, V, 1))).ToList();
            return cropWindow is { } crop ? ClipToWindow(points, crop.X0, crop.X1, crop.Y0, crop.Y1) : points;
        }

        IEnumerable<List<(double X, double Y)>> ProjectOpenings(GeoFace face)
        {
            foreach (var hole in face.InnerVertices ?? [])
            {
                if (hole.Count < 3 || hole.Any(point => point.Length != 3 || !point.All(double.IsFinite))) continue;
                var points = ProjectRing(hole);
                if (points.Count >= 3 && Math.Abs(PlanPrinter.SignedArea(points)) > 1e-6) yield return points;
            }
        }

        void OrderRoofLayerPaint(List<(int Index, GeoFace Face)> roofLayers)
        {
            foreach (var (index, exterior) in roofLayers)
            {
                var patterned = faces[index];
                if (!modelHatches.ContainsKey(patterned.Points) || exterior.GeometryNode == 0) continue;
                double nd = Project(D, exterior.Normal, 1);
                if (Math.Abs(nd) < 1e-6) continue;
                double exteriorPlane = Project(exterior.OuterVertices![0], exterior.Normal);
                foreach (var (otherIndex, other) in roofLayers)
                {
                    if (other.GeometryNode != exterior.GeometryNode
                        || Math.Abs(other.Normal[0] + exterior.Normal[0])
                            + Math.Abs(other.Normal[1] + exterior.Normal[1])
                            + Math.Abs(other.Normal[2] + exterior.Normal[2]) > 1e-6) continue;
                    double gap = (Project(other.OuterVertices![0], exterior.Normal) - exteriorPlane) / nd;
                    var underside = faces[otherIndex];
                    if (gap <= 1 || underside.Depth > patterned.Depth) continue;
                    // Different extents can reverse the mean depths of parallel skins of the same solid.
                    faces[otherIndex] = (underside.Points, patterned.Depth + gap, underside.Outline);
                }
            }
        }
    }

    internal static double? CircleHeadRadius(RvtDocument document, int symbolId, string familyName)
    {
        var family = document.GetElement(symbolId);
        if (family is not { FamilyName: var name, BoundingBox: { Length: 6 } box } || name != familyName) return null;
        double width = (box[3] - box[0]) * FeetToMillimetres;
        double height = (box[4] - box[1]) * FeetToMillimetres;
        return height > 0 && Math.Abs(width - height) < 0.01 ? height / 2 : null;
    }

    /// <summary>A projected edge for inking: its ends in view millimetres, the painter's depth of its face,
    /// the depth of its midpoint for occlusion, the incident faces (FaceB is -1 when single), its plane key
    /// for coplanar merging, whether the section cut created it (inked with its own category's cut pen;
    /// other edges use the projection pen), and the category of the face it came from.</summary>
    internal sealed record CollectedEdge(double X1, double Y1, double X2, double Y2, double Depth, double MidDepth,
        int FaceA, int FaceB, long Nx, long Ny, long Nz, long Off, bool IsCut = false, int CategoryId = 0,
        double? NearDepth = null)
    {
        internal int ViewLineStyleId { get; init; }
        internal CollectedEdge? UnclippedViewLine { get; init; }
        internal (int Ring, int Segment)? SourceRingEdge { get; init; }
        internal int? SilhouetteOwnerId { get; init; }
    }

    internal static (long Ax, long Ay, long Bx, long By) ProjectedEdgeKey(double x1, double y1, double x2, double y2)
    {
        long ax = (long)Math.Round(x1 * 10), ay = (long)Math.Round(y1 * 10);
        long bx = (long)Math.Round(x2 * 10), by = (long)Math.Round(y2 * 10);
        return (ax < bx || (ax == bx && ay <= by)) ? (ax, ay, bx, by) : (bx, by, ax, ay);
    }

    /// <summary>Visible edges of the projected faces: an edge shared by exactly two coplanar faces is a
    /// triangulation diagonal inside a flat surface and is not inked; every other edge is drawn once, so a
    /// crease shared by two faces is not inked twice. Endpoints quantize to 0.1 mm, plane normals to 0.001
    /// and plane offsets to 0.5 mm. Each edge carries its 3D midpoint depth and its incident faces. An edge
    /// both of whose 3D ends lie on the cut plane (within a millimetre) is cut-created.</summary>
    private sealed record ProjectionFace(List<(double X, double Y)> Points, List<double[]> Ring,
        double Depth, long Nx, long Ny, long Nz, long Off, int FaceIndex, int CategoryId, bool[]? IncomingEdges);

    private static List<CollectedEdge> DedupeEdges(
        List<ProjectionFace> edgeFaces,
        double[] viewDir, double[]? cutOrigin = null, bool keepComponentDepths = false,
        IReadOnlySet<int>? roofInsertFaces = null, bool fineSection = false, bool keepStairDepths = false,
        HashSet<(long Ax, long Ay, long Bx, long By)>? railSeams = null, bool keepDoorDepths = false,
        bool keepDetailProfileDepths = false)
    {
        var byEdge = new Dictionary<((long Ax, long Ay, long Bx, long By) Edge, long Start, long End),
            List<(int Face, int Vertex)>>();
        for (int f = 0; f < edgeFaces.Count; f++)
        {
            var face = edgeFaces[f];
            for (int i = 0; i < face.Points.Count; i++)
            {
                if (face.IncomingEdges?[(i + 1) % face.Points.Count] == false) continue;
                var key = EdgeKey(face.Points, face.Ring, PreserveDepth(face, i), i);
                if (key.Edge.Ax == key.Edge.Bx && key.Edge.Ay == key.Edge.By) continue;
                if (!byEdge.TryGetValue(key, out var members)) byEdge[key] = members = [];
                members.Add((f, i));
            }
        }
        var edges = new List<CollectedEdge>();
        var seen = new HashSet<((long Ax, long Ay, long Bx, long By) Edge, long Start, long End)>();
        for (int f = 0; f < edgeFaces.Count; f++)
        {
            var (points, ring, depth, nx, ny, nz, off, faceIndex, categoryId, incomingEdges) = edgeFaces[f];
            // Window-clipped polygons lose the one-to-one vertex correspondence with the 3D ring;
            // those small details fall back to the face-average depth.
            bool hasRing = ring.Count == points.Count;
            for (int i = 0; i < points.Count; i++)
            {
                int next = (i + 1) % points.Count;
                if (incomingEdges?[next] == false) continue;
                var a = points[i];
                var b = points[next];
                var key = EdgeKey(points, ring, PreserveDepth(edgeFaces[f], i), i);
                if (key.Edge.Ax == key.Edge.Bx && key.Edge.Ay == key.Edge.By) continue;
                if (!seen.Add(key)) continue;
                var members = byEdge[key];
                if (railSeams != null && IsRailingCategory(categoryId) && members.Count > 2 && IsProjectedRailSeam(members, a, b))
                    railSeams.Add(key.Edge);
                int other = members.Count == 2 ? (members[0].Face == f ? members[1].Face : members[0].Face) : -1;
                int otherFace = -1;
                if (other >= 0)
                {
                    var o = edgeFaces[other];
                    // Opposite sides form a seam. Same-side copies can belong to the same native face.
                    if (o.Nx == nx && o.Ny == ny && o.Nz == nz && o.Off == off
                        && (!UsesDetailProfileDepth(edgeFaces[f], i)
                            || ProjectedSide(a, b, points) * ProjectedSide(a, b, o.Points) == -1))
                        continue;
                    otherFace = o.FaceIndex;
                }
                double midDepth = depth;
                double? nearDepth = null;
                bool isCut = false;
                if (hasRing)
                {
                    var ca = ring[i];
                    var cb = ring[(i + 1) % ring.Count];
                    midDepth = ((ca[0] + cb[0]) / 2) * viewDir[0] + ((ca[1] + cb[1]) / 2) * viewDir[1]
                        + ((ca[2] + cb[2]) / 2) * viewDir[2];
                    nearDepth = Math.Min(Project(ca, viewDir, 1), Project(cb, viewDir, 1));
                    isCut = IsCutEdge(ca, cb);
                }
                // A horizontal face projects to a horizontal line, so it is seen edge-on and the edge along it is not
                // inked (the reference draws the wall top as the ends of its boards, without a line across them).
                // A section keeps the edges its own cut created - those are the cut profile of a horizontal slab.
                edges.Add(new CollectedEdge(a.X, a.Y, b.X, b.Y, depth, midDepth, faceIndex, otherFace, nx, ny, nz, off,
                    isCut, categoryId, nearDepth));
            }
        }
        edges.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        return edges;

        bool PreserveDepth(ProjectionFace face, int index) => roofInsertFaces?.Contains(face.FaceIndex) == true
            || fineSection && face.CategoryId == (int)BuiltInCategory.OST_LightingFixtures
            || keepDoorDepths && face.CategoryId == (int)BuiltInCategory.OST_Doors
            || keepStairDepths && IsStairBodyCategory(face.CategoryId)
            || keepComponentDepths && HasCoincidentComponents(face.CategoryId, cutOrigin == null)
            || UsesDetailProfileDepth(face, index);

        bool UsesDetailProfileDepth(ProjectionFace face, int index)
        {
            int next = (index + 1) % face.Points.Count;
            // Cut edges still share the cut-plane key and use the existing cut pens.
            return keepDetailProfileDepths && face.Ring.Count == face.Points.Count
                && IsVerticalDetailProfile(face.CategoryId, face.Points[index].X, face.Points[next].X)
                && !IsCutEdge(face.Ring[index], face.Ring[next]);
        }

        bool IsCutEdge(double[] a, double[] b)
        {
            if (cutOrigin == null) return false;
            double da = (a[0] - cutOrigin[0]) * viewDir[0] + (a[1] - cutOrigin[1]) * viewDir[1]
                + (a[2] - cutOrigin[2]) * viewDir[2];
            double db = (b[0] - cutOrigin[0]) * viewDir[0] + (b[1] - cutOrigin[1]) * viewDir[1]
                + (b[2] - cutOrigin[2]) * viewDir[2];
            return Math.Abs(da) <= 1.0 && Math.Abs(db) <= 1.0;
        }

        bool IsProjectedRailSeam(List<(int Face, int Vertex)> members, (double X, double Y) start,
            (double X, double Y) end) =>
            members.All(member => IsRailingCategory(edgeFaces[member.Face].CategoryId))
            && members.GroupBy(member =>
            {
                var face = edgeFaces[member.Face];
                return EdgeKey(face.Points, face.Ring, true, member.Vertex);
            }).All(group =>
            {
                var faces = group.Select(member => edgeFaces[member.Face]).ToArray();
                var first = faces[0];
                if (faces.Any(face => (face.Nx, face.Ny, face.Nz, face.Off) != (first.Nx, first.Ny, first.Nz, first.Off)))
                    return false;
                // A railing and its embedded components can repeat both faces of one joint.
                var sides = faces.Select(face => ProjectedSide(start, end, face.Points)).ToArray();
                return sides.Contains(-1) && sides.Contains(1);
            });

        ((long Ax, long Ay, long Bx, long By) Edge, long Start, long End) EdgeKey(
            List<(double X, double Y)> points, List<double[]> ring, bool preserveDepth, int index)
        {
            int next = (index + 1) % points.Count;
            var a = points[index];
            var b = points[next];
            var edge = ProjectedEdgeKey(a.X, a.Y, b.X, b.Y);
            if (!preserveDepth || ring.Count != points.Count) return (edge, 0, 0);

            // A far component can share a projection with a nearer one that covers it.
            long da = (long)Math.Round(Project(ring[index], viewDir, 1) * 10);
            long db = (long)Math.Round(Project(ring[next], viewDir, 1) * 10);
            return edge.Ax == (long)Math.Round(a.X * 10) && edge.Ay == (long)Math.Round(a.Y * 10)
                ? (edge, da, db) : (edge, db, da);
        }
    }

    private static bool IsRailingCategory(int categoryId) =>
        categoryId is (int)BuiltInCategory.OST_StairsRailing or (int)BuiltInCategory.OST_RailingTopRail;

    private static bool IsVerticalDetailProfile(int categoryId, double x1, double x2) =>
        Math.Abs(x1 - x2) < 1e-6
        && (IsRailingCategory(categoryId)
            || categoryId is (int)BuiltInCategory.OST_StairsRailingBaluster or (int)BuiltInCategory.OST_CurtainWallMullions);

    private static bool ClipsAtBaluster(CollectedEdge edge, int maskCategory) =>
        maskCategory != (int)BuiltInCategory.OST_StairsRailingBaluster || IsRailingCategory(edge.CategoryId);

    private static bool HasCoincidentComponents(int categoryId, bool elevation) =>
        IsRailingCategory(categoryId)
        || categoryId is (int)BuiltInCategory.OST_CurtainWallPanels or (int)BuiltInCategory.OST_CurtainWallMullions
        || elevation && categoryId is (int)BuiltInCategory.OST_Walls
            or (int)BuiltInCategory.OST_Windows or (int)BuiltInCategory.OST_Doors;

    private static double? RailingClipDepth(CollectedEdge edge) =>
        !edge.IsCut && edge.ViewLineStyleId == 0
        && (IsRailingCategory(edge.CategoryId)
            || edge.CategoryId is ((int)BuiltInCategory.OST_Windows or (int)BuiltInCategory.OST_Doors))
            ? edge.NearDepth - 1 : null;

    private sealed record PlanarOcclusion(double Dx, double Dy, double OriginDepth,
        List<List<(double X, double Y)>>? Openings, bool Transparent, bool ExactStairProjectionDepth)
    {
        internal double DepthAt(double x, double y) => OriginDepth + Dx * x + Dy * y;
    }

    /// <summary>Edges with their midpoint hidden behind nearer mass removed: an edge whose midpoint lies inside
    /// another face strictly nearer the viewer (1 mm or more) is occluded. A uniform grid over the frame keeps
    /// each edge to the faces overlapping its cell; the edge's own incident faces never occlude it. A midpoint
    /// on the boundary of the nearer face counts as inside (half a millimetre): stacked wall layers and window
    /// frames project their edges onto the silhouette of the mass in front, and the reference draws that
    /// silhouette once.</summary>
    private static List<CollectedEdge> OccludeEdges(
        List<(List<(double X, double Y)> Points, double Depth, bool Outline)> faces,
        List<(double MinDepth, int ElementId, int? HostId, int CategoryId,
            PlanarOcclusion? Plane)> faceOccluders,
        List<CollectedEdge> edges, bool keepWindowJambs, IReadOnlySet<int>? roofInsertFaces = null,
        IReadOnlyDictionary<int, List<(double X, double Y)>>? stairMaskFaces = null,
        IReadOnlyDictionary<int, double>? railingMaskDepths = null,
        List<CollectedEdge>? behindRailings = null, bool useDetailPlaneDepth = false, Func<int, int>? stairAssembly = null,
        IReadOnlyDictionary<int, double>? furnitureCovers = null)
    {
        const int Cells = 128;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var face in faces)
            foreach (var (x, y) in face.Points)
            {
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        if (!(maxX > minX) || !(maxY > minY) || faces.Count == 0 || edges.Count == 0) return edges;
        double cellW = (maxX - minX) / Cells + 1e-9, cellH = (maxY - minY) / Cells + 1e-9;
        var grid = new List<int>[Cells * Cells];
        for (int f = 0; f < faces.Count; f++)
        {
            double fx0 = double.MaxValue, fy0 = double.MaxValue, fx1 = double.MinValue, fy1 = double.MinValue;
            foreach (var (x, y) in faces[f].Points)
            {
                fx0 = Math.Min(fx0, x); fy0 = Math.Min(fy0, y);
                fx1 = Math.Max(fx1, x); fy1 = Math.Max(fy1, y);
            }
            int gx0 = Math.Clamp((int)((fx0 - minX) / cellW), 0, Cells - 1);
            int gx1 = Math.Clamp((int)((fx1 - minX) / cellW), 0, Cells - 1);
            int gy0 = Math.Clamp((int)((fy0 - minY) / cellH), 0, Cells - 1);
            int gy1 = Math.Clamp((int)((fy1 - minY) / cellH), 0, Cells - 1);
            for (int gx = gx0; gx <= gx1; gx++)
                for (int gy = gy0; gy <= gy1; gy++)
                    (grid[gy * Cells + gx] ??= new List<int>()).Add(f);
        }
        var kept = new List<CollectedEdge>(edges.Count);
        foreach (var edge in edges)
        {
            double mx = (edge.X1 + edge.X2) / 2, my = (edge.Y1 + edge.Y2) / 2;
            int gx = Math.Clamp((int)((mx - minX) / cellW), 0, Cells - 1);
            int gy = Math.Clamp((int)((my - minY) / cellH), 0, Cells - 1);
            bool hidden = false;
            bool hiddenByRail = false;
            var source = edge.FaceA >= 0 ? faceOccluders[edge.FaceA] : default;
            bool isInsert = edge.CategoryId is (int)BuiltInCategory.OST_Windows or (int)BuiltInCategory.OST_Doors;
            bool roofInsert = roofInsertFaces?.Contains(edge.FaceA) == true;
            bool stairBody = IsStairBodyCategory(edge.CategoryId);
            bool stairProjection = !edge.IsCut && (stairBody || IsRailingCategory(edge.CategoryId)
                || edge.CategoryId == (int)BuiltInCategory.OST_StairsRailingBaluster);
            bool horizontalTread = stairBody && !edge.IsCut && Math.Abs(edge.Y1 - edge.Y2) < 1e-6;
            int assembly = horizontalTread ? stairAssembly?.Invoke(source.ElementId) ?? source.ElementId : 0;
            double? stairDepth = !edge.IsCut && edge.ViewLineStyleId == 0 && !stairBody
                ? edge.NearDepth : null;
            double? railingDepth = railingMaskDepths != null ? RailingClipDepth(edge) : null;
            double? furnitureDepth = furnitureCovers is { Count: > 0 } ? FurnitureClipDepth(edge) : null;
            var candidates = grid[gy * Cells + gx];
            if (candidates != null)
            {
                foreach (int f in candidates)
                {
                    if (f == edge.FaceA || f == edge.FaceB) continue;
                    if (furnitureDepth is double lineDepth && furnitureCovers!.TryGetValue(f, out double farDepth)
                        && farDepth < lineDepth - 1) continue;
                    var (coverDepth, elementId, hostId, categoryId, plane) = faceOccluders[f];
                    // The projected union already resolves overlaps within this mesh.
                    if (edge.SilhouetteOwnerId == elementId) continue;
                    if (stairDepth is double near && coverDepth < near - 1 && stairMaskFaces?.ContainsKey(f) == true)
                        continue; // This face clips the span after midpoint occlusion.
                    if (plane != null && (roofInsert && elementId == source.ElementId
                        || source.Plane != null && source.CategoryId == (int)BuiltInCategory.OST_ElectricalEquipment
                            && categoryId is ((int)BuiltInCategory.OST_ElectricalEquipment or (int)BuiltInCategory.OST_IOSModelGroups)))
                    {
                        if (plane.Transparent || plane.Openings?.Any(hole => PointInPolygon(mx, my, hole)) == true) continue;
                        coverDepth = plane.DepthAt(mx, my);
                    }
                    if (categoryId is (int)BuiltInCategory.OST_Walls or (int)BuiltInCategory.OST_Windows
                        or (int)BuiltInCategory.OST_Doors && plane?.Openings is { Count: > 0 } openings)
                    {
                        if (openings.Any(hole => PointInPolygon(mx, my, hole))) continue;
                        coverDepth = plane.DepthAt(mx, my);
                    }
                    if (plane != null && (stairProjection && plane.ExactStairProjectionDepth
                        && (categoryId == (int)BuiltInCategory.OST_Walls
                            || assembly > 0 && Math.Abs(plane.Dx) > 1e-6 && Math.Abs(plane.Dy) < 1e-6
                                && (elementId == source.ElementId || stairAssembly?.Invoke(elementId) == assembly))
                        || useDetailPlaneDepth
                        && (IsStairBodyCategory(categoryId)
                            || categoryId is ((int)BuiltInCategory.OST_Walls or (int)BuiltInCategory.OST_Floors))))
                        coverDepth = plane.DepthAt(mx, my);
                    if (elementId == source.HostId)
                    {
                        // shortcut: exempt the host wall until its openings are projected.
                        if (categoryId == (int)BuiltInCategory.OST_Walls || plane?.Openings?.Any(hole => PointInPolygon(mx, my, hole)) == true) continue;
                        if (plane != null) coverDepth = plane.DepthAt(mx, my);
                    }
                    // shortcut: keep the insert's inset detail until the occlusion pass handles glazing materials.
                    double clearance = !roofInsert && isInsert && elementId == source.ElementId ? 500.0 : 1.0;
                    if (coverDepth >= edge.MidDepth - clearance) continue;
                    var polygon = faces[f].Points;
                    if (!PointInPolygon(mx, my, polygon) && DistToPolygon(mx, my, polygon) > 0.5) continue;
                    // Ganged windows touch at a projected jamb, so the adjacent pane cannot hide that edge.
                    if (keepWindowJambs && edge.CategoryId == (int)BuiltInCategory.OST_Windows
                        && Math.Abs(edge.X2 - edge.X1) <= 0.1
                        && categoryId == edge.CategoryId && elementId != source.ElementId
                        && hostId.HasValue && hostId == source.HostId
                        && ProjectedSide((edge.X1, edge.Y1), (edge.X2, edge.Y2), polygon)
                            * ProjectedSide((edge.X1, edge.Y1), (edge.X2, edge.Y2), faces[edge.FaceA].Points) == -1)
                        continue;
                    if (railingDepth is double limit && railingMaskDepths!.TryGetValue(f, out var far)
                        && far < limit && ClipsAtBaluster(edge, categoryId))
                    {
                        hiddenByRail = true;
                        continue;
                    }
                    hidden = true;
                    break;
                }
            }
            if (hidden) continue;
            if (hiddenByRail) behindRailings!.Add(edge);
            else kept.Add(edge);
        }
        kept.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        if (furnitureCovers is not { Count: > 0 }) return kept;

        // Keep the original order of coincident edges for the later merge. Only replace a jamb
        // or opening mark when it encounters a nearer furniture face.
        var furnitureMasks = furnitureCovers.Select(mask =>
        {
            var polygon = faces[mask.Key].Points;
            return (mask.Value, Polygon: polygon, Left: polygon.Min(p => p.X), Right: polygon.Max(p => p.X),
                Bottom: polygon.Min(p => p.Y), Top: polygon.Max(p => p.Y));
        }).ToArray();
        return kept.SelectMany(ClipAtFurniture).ToList();

        IEnumerable<CollectedEdge> ClipAtFurniture(CollectedEdge edge)
        {
            if (FurnitureClipDepth(edge) is not double depth) return [edge];
            double left = Math.Min(edge.X1, edge.X2), right = Math.Max(edge.X1, edge.X2);
            double bottom = Math.Min(edge.Y1, edge.Y2), top = Math.Max(edge.Y1, edge.Y2);
            var covers = furnitureMasks.Where(mask => mask.Value < depth - 1
                && mask.Left <= right && mask.Right >= left && mask.Bottom <= top && mask.Top >= bottom)
                .Select(mask => mask.Polygon).ToList();
            return covers.Count == 0 ? [edge] : VisibleTerrainEdges([edge], covers, [],
                coverBoundary: false, preserveDashOrigin: true);
        }
    }

    private static double? FurnitureClipDepth(CollectedEdge edge)
    {
        if (edge.IsCut || edge.NearDepth is not double depth) return null;
        if (edge.ViewLineStyleId > 0 || edge.CategoryId == (int)BuiltInCategory.OST_Windows
            && Math.Abs(edge.X1 - edge.X2) < 1e-6 && Math.Abs(depth - edge.MidDepth) < 1e-6)
            return depth;
        return null;
    }

    private static int ProjectedSide((double X, double Y) start, (double X, double Y) end,
        List<(double X, double Y)> polygon)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double tolerance = 0.1 * Math.Sqrt(dx * dx + dy * dy);
        int side = 0;
        foreach (var (x, y) in polygon)
        {
            double cross = dx * (y - start.Y) - dy * (x - start.X);
            int vertexSide = cross > tolerance ? 1 : cross < -tolerance ? -1 : 0;
            if (vertexSide == 0) continue;
            if (side != 0 && side != vertexSide) return 0;
            side = vertexSide;
        }
        return side;
    }

    /// <summary>Merges overlapping collinear edges on one plane into their union: stacked wall layers and
    /// coincident faces ink the same stretch several times, while the reference draws it once. Only true
    /// overlaps merge (touching ends stay separate); the plane key keeps different depths apart.</summary>
    internal static List<CollectedEdge> MergeOverlaps(List<CollectedEdge> edges,
        Func<CollectedEdge, double>? cutWidth = null)
    {
        var groups = new Dictionary<(long Ang, long Off, long Nx, long Ny, long Nz, long Pln), List<CollectedEdge>>();
        foreach (var edge in edges)
        {
            double dx = edge.X2 - edge.X1, dy = edge.Y2 - edge.Y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (!(len > 1e-9)) continue;
            // The line orientation (0..180 degrees) and its offset from the origin, coarsened to the edge quantum.
            double ang = Math.Atan2(dy, dx);
            if (ang < 0) ang += Math.PI;
            double off = edge.X1 * -dy / len + edge.Y1 * dx / len;
            var key = ((long)Math.Round(ang * 360.0), (long)Math.Round(off * 10),
                edge.Nx, edge.Ny, edge.Nz, edge.Off);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<CollectedEdge>();
            list.Add(edge);
        }
        var merged = new List<CollectedEdge>(edges.Count);
        foreach (var list in groups.Values)
        {
            if (list.Count == 1) { merged.Add(list[0]); continue; }
            bool horizontal = Math.Abs(list[0].Y2 - list[0].Y1) <= Math.Abs(list[0].X2 - list[0].X1);
            var spans = new List<(double Lo, double Hi, CollectedEdge Edge)>(list.Count);
            foreach (var edge in list)
            {
                double lo = horizontal ? Math.Min(edge.X1, edge.X2) : Math.Min(edge.Y1, edge.Y2);
                double hi = horizontal ? Math.Max(edge.X1, edge.X2) : Math.Max(edge.Y1, edge.Y2);
                spans.Add((lo, hi, edge));
            }
            if (cutWidth != null && list.All(edge => edge.IsCut && edge.ViewLineStyleId == 0))
            {
                var cuts = spans.Select(span => (span.Lo, span.Hi, span.Edge, Width: cutWidth(span.Edge)))
                    .OrderByDescending(span => span.Width).ToArray();
                if (cuts[0].Width > cuts[^1].Width)
                {
                    // A wider pen covers only the part supplied by that cut, not the whole joined edge.
                    var ends = cuts.SelectMany(span => new[] { span.Lo, span.Hi }).Distinct().Order().ToArray();
                    int owner = -1, start = 0;
                    for (int i = 0; i < ends.Length; i++)
                    {
                        int next = i + 1 == ends.Length ? -1
                            : Array.FindIndex(cuts, span => span.Lo <= ends[i] && span.Hi >= ends[i + 1]);
                        if (next == owner) continue;
                        if (owner >= 0)
                            merged.Add(WithSpan(cuts[owner].Edge, ends[start], ends[i], horizontal));
                        owner = next;
                        start = i;
                    }
                    continue;
                }
            }
            spans.Sort((a, b) => a.Lo.CompareTo(b.Lo));
            double curLo = spans[0].Lo, curHi = spans[0].Hi, curDepth = spans[0].Edge.Depth;
            var cur = spans[0].Edge;
            for (int i = 1; i < spans.Count; i++)
            {
                if (spans[i].Lo < curHi - 0.1)
                {
                    curHi = Math.Max(curHi, spans[i].Hi);
                    curDepth = Math.Max(curDepth, spans[i].Edge.Depth);
                }
                else
                {
                    merged.Add(WithSpan(cur, curLo, curHi, horizontal) with { Depth = curDepth });
                    cur = spans[i].Edge;
                    curLo = spans[i].Lo; curHi = spans[i].Hi; curDepth = spans[i].Edge.Depth;
                }
            }
            merged.Add(WithSpan(cur, curLo, curHi, horizontal) with { Depth = curDepth });
        }
        merged.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        return merged;
    }

    /// <summary>Collapses coincident modeling drawn several times into one inked line: edges parallel within
    /// 50 mm, overlapping over most of their length, at the same depth and of the same length (a railing family
    /// drawing one board four times, a window jamb doubled by its frame). Only full duplicates collapse: a short
    /// edge on a long one stays (a head on a wall top), lines at different depths stay (a rail in front of its
    /// wall), and staggered layers stay. The nearest edge survives, the way the reference inks the visible one.</summary>
    internal static List<CollectedEdge> ConsolidateCoincident(List<CollectedEdge> edges,
        bool keepFineSectionProfiles = false, double elevationRailPenWidth = 0)
    {
        const double ToleranceMillimetres = 50.0;
        var survivors = new List<CollectedEdge>(edges.Count);
        List<CollectedEdge>? fineProfiles = null;
        foreach (var edge in edges.OrderBy(e => e.MidDepth))
        {
            double dx = edge.X2 - edge.X1, dy = edge.Y2 - edge.Y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (!(len > 1e-9)) continue;
            bool horizontal = Math.Abs(dy) <= Math.Abs(dx);
            double lo = horizontal ? Math.Min(edge.X1, edge.X2) : Math.Min(edge.Y1, edge.Y2);
            double hi = horizontal ? Math.Max(edge.X1, edge.X2) : Math.Max(edge.Y1, edge.Y2);
            bool absorbed = false;
            foreach (var kept in survivors)
            {
                double kdx = kept.X2 - kept.X1, kdy = kept.Y2 - kept.Y1;
                double klen = Math.Sqrt(kdx * kdx + kdy * kdy);
                if (!(klen > 1e-9)) continue;
                bool kHorizontal = Math.Abs(kdy) <= Math.Abs(kdx);
                if (kHorizontal != horizontal) continue;
                // Parallel within the tolerance: the perpendicular distance of both ends.
                double sep = horizontal
                    ? Math.Max(Math.Abs(edge.Y1 - kept.Y1), Math.Abs(edge.Y2 - kept.Y2))
                    : Math.Max(Math.Abs(edge.X1 - kept.X1), Math.Abs(edge.X2 - kept.X2));
                if (sep > ToleranceMillimetres) continue;
                // The two sides of a cut tread or riser can be only 50 mm apart.
                if (sep > 0.1 && keepFineSectionProfiles
                    && (edge.IsCut && IsStairBodyCategory(edge.CategoryId) || kept.IsCut && IsStairBodyCategory(kept.CategoryId))) continue;
                if (Math.Abs(edge.MidDepth - kept.MidDepth) > ToleranceMillimetres) continue;
                double klo = horizontal ? Math.Min(kept.X1, kept.X2) : Math.Min(kept.Y1, kept.Y2);
                double khi = horizontal ? Math.Max(kept.X1, kept.X2) : Math.Max(kept.Y1, kept.Y2);
                double overlap = Math.Min(hi, khi) - Math.Max(lo, klo);
                double shorter = Math.Min(len, klen);
                if (overlap < 0.8 * shorter) continue;
                if (Math.Min(len, klen) < 0.8 * Math.Max(len, klen)) continue;
                // At elevation scale, sub-pen-width boundaries of a rail read as one stroke.
                bool separateElevationRail = elevationRailPenWidth > 0 && sep > elevationRailPenWidth
                    && (Math.Abs(dx) < 1e-6 && Math.Abs(kdx) < 1e-6
                        || Math.Abs(dy) < 1e-6 && Math.Abs(kdy) < 1e-6);
                if (sep > 0.1 && !edge.IsCut && !kept.IsCut
                    && (keepFineSectionProfiles && edge.CategoryId == (int)BuiltInCategory.OST_Doors
                        || IsRailingCategory(edge.CategoryId) && (keepFineSectionProfiles || separateElevationRail))
                    && kept.CategoryId == edge.CategoryId
                    && edge.FaceA >= 0 && kept.FaceA >= 0
                    && (edge.FaceA == kept.FaceA || edge.FaceA == kept.FaceB
                        || edge.FaceB >= 0 && (edge.FaceB == kept.FaceA || edge.FaceB == kept.FaceB)))
                    (fineProfiles ??= []).Add(edge);
                absorbed = true;
                break;
            }
            if (!absorbed) survivors.Add(edge);
        }
        survivors.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        if (fineProfiles != null)
        {
            // Recover both edges of the same profile face without changing the model edge selection.
            var inked = survivors.Select(edge => ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2)).ToHashSet();
            foreach (var edge in fineProfiles)
                if (inked.Add(ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2)))
                    survivors.Add(edge);
        }
        return survivors;
    }

    internal static bool HeaderOutlineIsBeforePlane(Element element, double[] direction, double planeDepth)
        // An RPC outline can reach the view plane even when all its proxy triangles lie in front of it.
        => element.BoundingBoxIsHeaderOutline && element.BoundingBox is { Length: 6 } bounds
            && bounds.All(double.IsFinite)
            && Enumerable.Range(0, 3).Sum(i => bounds[direction[i] < 0 ? i : i + 3] * direction[i])
                * FeetToMillimetres < planeDepth;

    internal static bool SpansSectionPlane(List<GeoFace> faces, double[] direction, double planeDepth)
    {
        foreach (var face in faces)
        {
            if (face.OuterVertices is not { Count: >= 3 } vertices) continue;
            bool before = false, beyond = false;
            foreach (var vertex in vertices)
            {
                double depth = Project(vertex, direction);
                before |= depth < planeDepth - 1e-6;
                beyond |= depth > planeDepth + 1e-6;
                if (before && beyond) return true;
            }
        }
        return false;
    }

    private static bool IsStairBodyCategory(int category) => category is
        (int)BuiltInCategory.OST_Stairs or (int)BuiltInCategory.OST_StairsRuns or (int)BuiltInCategory.OST_StairsTrisers;

    private static void AddRoofLayers(List<DetailAnnotations.LayerBoundary> output,
        IEnumerable<(double X1, double Y1, double X2, double Y2, double Nx, double Ny, double Nz,
            int TypeId, int CategoryId, double Depth)> chords,
        List<List<(double X, double Y)>> rings, Internal.RawCompoundStructureLayer[] layers,
        double[] right, double[] up, double[] direction, (double X0, double Y0, double X1, double Y1) crop,
        RvtDocument document, List<DetailAnnotations.LayerHatch>? hatches, double[]? viewOrigin)
    {
        var sides = chords.ToArray();
        var tops = sides.Where(side => side.Nz > 0.01).Take(3).ToArray();
        if (tops.Length is 0 or > 2) return;
        // A common high end and eaves on opposite sides identify a single ridge.
        if (tops is [var first, var second])
        {
            if (rings.Count != 1) return;
            double ridgeY = Math.Max(first.Y1, first.Y2);
            double ridgeX = first.Y1 > first.Y2 ? first.X1 : first.X2;
            double secondRidgeY = Math.Max(second.Y1, second.Y2);
            double secondRidgeX = second.Y1 > second.Y2 ? second.X1 : second.X2;
            if (Math.Abs(ridgeX - secondRidgeX) > 0.1 || Math.Abs(ridgeY - secondRidgeY) > 0.1
                || Math.Min(first.Y1, first.Y2) >= ridgeY - 0.1
                || Math.Min(second.Y1, second.Y2) >= ridgeY - 0.1
                || (first.X1 + first.X2 - 2 * ridgeX) * (second.X1 + second.X2 - 2 * ridgeX) >= 0)
                return;
        }

        var widths = layers.Select(layer => BitConverter.Int64BitsToDouble(layer.LayerWidthBits)
            * FeetToMillimetres).ToArray();
        if (widths.Any(width => !(width is >= 0 and < 10000))) return;
        double thickness = widths.Sum();
        var planes = new List<(double[] Normal, double Distance)>();
        foreach (var top in tops)
        {
            if (Math.Abs(top.Nx * direction[0] + top.Ny * direction[1] + top.Nz * direction[2]) >= 1e-6
                || sides.Where(bottom => top.Nx * bottom.Nx + top.Ny * bottom.Ny + top.Nz * bottom.Nz < -1 + 1e-6)
                    .Take(2).ToArray() is not [var bottom])
                return;
            var normal = new[] { top.Nx * right[0] + top.Ny * right[1] + top.Nz * right[2],
                top.Nx * up[0] + top.Ny * up[1] + top.Nz * up[2], 0d };
            double measured = (top.X1 - bottom.X1) * normal[0] + (top.Y1 - bottom.Y1) * normal[1];
            if (Math.Abs(thickness - measured) >= 0.1) return;
            planes.Add((normal, top.X1 * normal[0] + top.Y1 * normal[1]));
        }

        foreach (var ring in rings)
        {
            var profile = ring.Select(point => new[] { point.X, point.Y, 0d }).ToList();
            double offset = 0;
            for (int layer = 0; layer < widths.Length; layer++)
            {
                double start = offset, width = widths[layer];
                offset += width;
                if (width <= 0) continue;

                bool drawBoundary = layer < widths.Length - 1 && offset >= 0.01 && thickness - offset >= 0.01;
                var grids = hatches != null && viewOrigin is { Length: 3 }
                    ? DetailAnnotations.CutGrids(document, layers[layer].RawMaterialId) : null;
                if (!drawBoundary && grids == null) continue;

                for (int i = 0; i < planes.Count; i++)
                {
                    var (normal, distance) = planes[i];
                    if (grids != null)
                    {
                        var band = RoofMaterialBand(ring, planes[i], planes.Count == 2 ? planes[1 - i] : null,
                            start, offset);
                        if (band.Count >= 3 && Math.Abs(PlanPrinter.SignedArea(band)) > 1e-6)
                            hatches!.Add(new(band, grids, Project(viewOrigin!, right), Project(viewOrigin!, up)));
                    }
                    if (!drawBoundary) continue;
                    var origin = new[] { normal[0] * (distance - offset), normal[1] * (distance - offset), 0d };
                    foreach (var (a, b) in CutChords(profile, origin, normal))
                    {
                        double x1 = a[0], y1 = a[1], x2 = b[0], y2 = b[1];
                        if (planes.Count == 2)
                        {
                            var adjacent = planes[1 - i];
                            double da = a[0] * adjacent.Normal[0] + a[1] * adjacent.Normal[1] - adjacent.Distance + offset;
                            double db = b[0] * adjacent.Normal[0] + b[1] * adjacent.Normal[1] - adjacent.Distance + offset;
                            if (da > 0 && db > 0) continue;
                            if (da > 0 || db > 0)
                            {
                                double t = da / (da - db);
                                double x = a[0] + (b[0] - a[0]) * t, y = a[1] + (b[1] - a[1]) * t;
                                if (da > 0) (x1, y1) = (x, y);
                                else (x2, y2) = (x, y);
                            }
                        }
                        if (ClipSegment(x1, y1, x2, y2, crop.X0, crop.Y0, crop.X1, crop.Y1,
                            out double ax, out double ay, out double bx, out double by))
                            output.Add(new((ax, ay), (bx, by), (int)BuiltInCategory.OST_Roofs));
                    }
                }
            }
        }
    }

    private static List<(double X, double Y)> RoofMaterialBand(List<(double X, double Y)> ring,
        (double[] Normal, double Distance) plane, (double[] Normal, double Distance)? adjacent, double outer, double inner)
    {
        double Depth((double X, double Y) point) => plane.Distance - point.X * plane.Normal[0] - point.Y * plane.Normal[1];
        var band = ClipHalfPlane(ring, point => Depth(point) >= outer, point => Depth(point) - outer);
        band = ClipHalfPlane(band, point => Depth(point) <= inner, point => inner - Depth(point));
        if (adjacent is { } other)
        {
            // The closest exterior plane owns each side of the ridge.
            double RidgeDistance((double X, double Y) point) =>
                other.Distance - point.X * other.Normal[0] - point.Y * other.Normal[1] - Depth(point);
            band = ClipHalfPlane(band, point => RidgeDistance(point) >= 0, RidgeDistance);
        }
        return band;
    }

    private static CollectedEdge? ClipCollectedEdge(CollectedEdge edge,
        (double X0, double Y0, double X1, double Y1) crop)
    {
        if (!ClipSegment(edge.X1, edge.Y1, edge.X2, edge.Y2, crop.X0, crop.Y0, crop.X1, crop.Y1,
            out double x1, out double y1, out double x2, out double y2)
            || (x1, y1) == (x2, y2)) return null;
        return edge with { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
    }

    internal static List<CollectedEdge> ResolveRoofWallCutJoins(List<CollectedEdge> edges,
        IReadOnlyList<List<(double X, double Y)>> roofs, IReadOnlyList<List<(double X, double Y)>> walls,
        double depth, (long Nx, long Ny, long Nz, long Off) plane)
    {
        const double tolerance = 1e-5;
        const int roofCategory = (int)BuiltInCategory.OST_Roofs;
        const int wallCategory = (int)BuiltInCategory.OST_Walls;
        var wallBoundaries = Boundaries(walls).ToList();
        var seams = new List<CollectedEdge>();
        foreach (var roof in Boundaries(roofs))
        {
            double dx = roof.X2 - roof.X1, dy = roof.Y2 - roof.Y1;
            bool horizontal = Math.Abs(dx) >= Math.Abs(dy);
            foreach (var wall in wallBoundaries)
            {
                // Counterclockwise rings put their interiors to the left.
                if (dx * (wall.X2 - wall.X1) + dy * (wall.Y2 - wall.Y1) >= 0) continue;
                if (CollinearOverlap(roof, wall, horizontal, tolerance) is { } overlap)
                    seams.Add(WithSpan(roof, overlap.Lo, overlap.Hi, horizontal));
            }
        }
        if (seams.Count == 0) return edges;

        var uniqueSeams = MergeOverlaps(seams)
            .DistinctBy(edge => ProjectedEdgeKey(edge.X1, edge.Y1, edge.X2, edge.Y2)).ToList();
        var result = new List<CollectedEdge>(edges.Count + uniqueSeams.Count);
        foreach (var edge in edges)
        {
            if (edge.IsCut && edge.ViewLineStyleId == 0 && edge.CategoryId is roofCategory or wallCategory)
                result.AddRange(UncoveredCollinearSpans(edge, uniqueSeams, tolerance));
            else
                result.Add(edge);
        }
        result.AddRange(uniqueSeams);
        return result;

        IEnumerable<CollectedEdge> Boundaries(IReadOnlyList<List<(double X, double Y)>> rings)
        {
            foreach (var ring in rings)
            {
                bool counterclockwise = PlanPrinter.SignedArea(ring) > 0;
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if (!counterclockwise) (a, b) = (b, a);
                    yield return new(a.X, a.Y, b.X, b.Y, depth, depth, -1, -1,
                        plane.Nx, plane.Ny, plane.Nz, plane.Off, CategoryId: roofCategory);
                }
            }
        }
    }

    internal static IEnumerable<CollectedEdge> TrimProjectedEdgesAtCuts(IReadOnlyList<CollectedEdge> edges,
        Func<CollectedEdge, bool>? coversProjection = null)
    {
        const double tolerance = 0.1;
        var cuts = edges.Where(edge => edge.IsCut && (coversProjection?.Invoke(edge) ?? true)).ToList();
        foreach (var edge in edges)
        {
            if (edge.IsCut || edge.ViewLineStyleId > 0 || cuts.Count == 0)
                yield return edge;
            else
                foreach (var span in UncoveredCollinearSpans(edge, cuts, tolerance))
                    yield return span;
        }
    }

    private static (double Lo, double Hi)? CollinearOverlap(
        CollectedEdge edge, CollectedEdge cover, bool horizontal, double tolerance)
    {
        double dx = edge.X2 - edge.X1, dy = edge.Y2 - edge.Y1;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= tolerance) return null;

        double Distance(double x, double y) => Math.Abs((x - edge.X1) * dy - (y - edge.Y1) * dx) / length;
        if (Distance(cover.X1, cover.Y1) > tolerance || Distance(cover.X2, cover.Y2) > tolerance) return null;

        double a = horizontal ? edge.X1 : edge.Y1, b = horizontal ? edge.X2 : edge.Y2;
        double c = horizontal ? cover.X1 : cover.Y1, d = horizontal ? cover.X2 : cover.Y2;
        double lo = Math.Max(Math.Min(a, b), Math.Min(c, d));
        double hi = Math.Min(Math.Max(a, b), Math.Max(c, d));
        return hi > lo ? (lo, hi) : null;
    }

    private static IEnumerable<CollectedEdge> UncoveredCollinearSpans(
        CollectedEdge edge, IReadOnlyList<CollectedEdge> covers, double tolerance)
    {
        bool horizontal = Math.Abs(edge.X2 - edge.X1) >= Math.Abs(edge.Y2 - edge.Y1);
        double a = horizontal ? edge.X1 : edge.Y1, b = horizontal ? edge.X2 : edge.Y2;
        double start = Math.Min(a, b), end = Math.Max(a, b);
        var covered = covers.Where(cover => cover.MidDepth <= edge.MidDepth + tolerance)
            .Select(cover => CollinearOverlap(edge, cover, horizontal, tolerance))
            .OfType<(double Lo, double Hi)>().OrderBy(span => span.Lo);
        double cursor = start;
        foreach (var span in covered)
        {
            if (span.Lo > cursor + tolerance) yield return WithSpan(edge, cursor, span.Lo, horizontal);
            cursor = Math.Max(cursor, span.Hi);
            if (cursor >= end) break;
        }
        if (cursor == start) yield return edge;
        else if (end > cursor + tolerance) yield return WithSpan(edge, cursor, end, horizontal);
    }

    private static CollectedEdge WithSpan(CollectedEdge edge, double lo, double hi, bool horizontal)
    {
        double dx = edge.X2 - edge.X1, dy = edge.Y2 - edge.Y1;
        double denom = horizontal ? dx : dy;
        if (Math.Abs(denom) < 1e-12) return edge;
        double baseLo = horizontal ? edge.X1 : edge.Y1;
        double t0 = (lo - baseLo) / denom, t1 = (hi - baseLo) / denom;
        return edge with
        {
            X1 = edge.X1 + t0 * dx,
            Y1 = edge.Y1 + t0 * dy,
            X2 = edge.X1 + t1 * dx,
            Y2 = edge.Y1 + t1 * dy,
        };
    }

    internal static IEnumerable<CollectedEdge> VisibleTerrainEdges(IEnumerable<CollectedEdge> terrainEdges,
        IEnumerable<List<(double X, double Y)>> polygons, List<(double From, double To)> pads,
        double minimumLengthSquared = 0.01, bool coverBoundary = true, bool coverOppositeEdges = false,
        IReadOnlyDictionary<List<(double X, double Y)>, List<List<(double X, double Y)>>>? openings = null,
        bool preserveDashOrigin = false)
    {
        // Ground is background. Keep only its parts outside the opaque model polygons so covered lines
        // do not remain as invisible strokes in the PDF.
        var masks = polygons.Select(points => (Points: points, X0: points.Min(p => p.X), Y0: points.Min(p => p.Y),
            X1: points.Max(p => p.X), Y1: points.Max(p => p.Y))).ToList();
        foreach (var edge in terrainEdges)
        {
            double dx = edge.X2 - edge.X1, dy = edge.Y2 - edge.Y1;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < minimumLengthSquared) continue;
            double crossTolerance = coverOppositeEdges ? 1e-5 * Math.Sqrt(lengthSquared) : 0;
            double left = Math.Min(edge.X1, edge.X2), right = Math.Max(edge.X1, edge.X2);
            double bottom = Math.Min(edge.Y1, edge.Y2), top = Math.Max(edge.Y1, edge.Y2);
            var covered = new List<(double From, double To)>();
            // A building pad replaces the terrain within its footprint, at every height in the section.
            foreach (var (from, to) in pads)
            {
                if (right <= from || left >= to) continue;
                if (Math.Abs(dx) < 1e-9)
                {
                    covered.Add((0, 1));
                    break;
                }
                double t0 = (from - edge.X1) / dx, t1 = (to - edge.X1) / dx;
                covered.Add((Math.Max(0, Math.Min(t0, t1)), Math.Min(1, Math.Max(t0, t1))));
            }
            foreach (var (polygon, x0, y0, x1, y1) in masks)
            {
                if (x1 < left || x0 > right || y1 < bottom || y0 > top) continue;
                var crossings = new List<double> { 0, 1 };
                var holes = openings?.GetValueOrDefault(polygon);
                AddCrossings(polygon);
                if (holes != null)
                    foreach (var hole in holes) AddCrossings(hole);
                crossings.Sort();
                for (int i = 1; i < crossings.Count; i++)
                {
                    double from = crossings[i - 1], to = crossings[i];
                    if (to - from < 1e-9) continue;
                    double mid = (from + to) / 2;
                    double x = edge.X1 + dx * mid, y = edge.Y1 + dy * mid;
                    if (PointInPolygon(x, y, polygon) && (coverBoundary || DistToPolygon(x, y, polygon) > 1e-5)
                        && holes?.Any(hole => PointInPolygon(x, y, hole)) != true)
                        covered.Add((from, to));
                }

                void AddCrossings(List<(double X, double Y)> ring)
                {
                    for (int i = 0; i < ring.Count; i++)
                    {
                        var a = ring[i];
                        var b = ring[(i + 1) % ring.Count];
                        double ex = b.X - a.X, ey = b.Y - a.Y;
                        double ax = a.X - edge.X1, ay = a.Y - edge.Y1;
                        if (coverOppositeEdges && dx * ex + dy * ey < 0)
                        {
                            double bx = b.X - edge.X1, by = b.Y - edge.Y1;
                            if (Math.Abs(ax * dy - ay * dx) <= crossTolerance && Math.Abs(bx * dy - by * dx) <= crossTolerance)
                            {
                                // Opposite boundary directions put material on both sides of their common span.
                                double from = Math.Max(0, (bx * dx + by * dy) / lengthSquared);
                                double to = Math.Min(1, (ax * dx + ay * dy) / lengthSquared);
                                if (to > from) covered.Add((from, to));
                                continue;
                            }
                        }
                        double denominator = dx * ey - dy * ex;
                        if (Math.Abs(denominator) < 1e-9) continue;
                        double t = (ax * ey - ay * ex) / denominator;
                        double u = (ax * dy - ay * dx) / denominator;
                        if (t > 0 && t < 1 && u >= 0 && u <= 1) crossings.Add(t);
                    }
                }
            }
            if (covered.Count == 0)
            {
                yield return edge;
                continue;
            }
            double cursor = 0;
            foreach (var (from, to) in covered.OrderBy(interval => interval.From))
            {
                if (from > cursor + 1e-9) yield return Slice(cursor, from);
                cursor = Math.Max(cursor, to);
            }
            if (cursor < 1 - 1e-9) yield return Slice(cursor, 1);

            CollectedEdge Slice(double from, double to) => edge with
            {
                X1 = edge.X1 + dx * from, Y1 = edge.Y1 + dy * from,
                X2 = edge.X1 + dx * to, Y2 = edge.Y1 + dy * to,
                UnclippedViewLine = preserveDashOrigin && edge.ViewLineStyleId > 0 && (from > 1e-9 || to < 1 - 1e-9)
                    ? edge.UnclippedViewLine ?? edge : edge.UnclippedViewLine,
            };
        }
    }

    private static IEnumerable<List<(double X, double Y)>> GroundSpans(TerrainHatch terrain, double baseY)
    {
        foreach (var edge in terrain.Profile)
        {
            if (Math.Abs(edge.X1 - edge.X2) < 1e-9 || baseY >= Math.Max(edge.Y1, edge.Y2)) continue;
            double x1 = edge.X1, y1 = edge.Y1, x2 = edge.X2, y2 = edge.Y2;
            if (y1 < baseY)
            {
                x1 += (x2 - x1) * (baseY - y1) / (y2 - y1);
                y1 = baseY;
            }
            else if (y2 < baseY)
            {
                x2 += (x1 - x2) * (baseY - y2) / (y1 - y2);
                y2 = baseY;
            }
            yield return [(x1, y1), (x2, y2), (x2, baseY), (x1, baseY)];
        }
    }

    private static List<List<(double X, double Y)>>? ClosedSectionWithOpenings(IReadOnlyList<GeoFace> faces,
        double[] origin, double[] direction, double[] horizontal, double[] vertical)
    {
        if (faces.Any(face => face.CurvedSurface || face.OuterVertices is not { Count: >= 3 }
            || (face.InnerLoops?.Count ?? 0) > (face.InnerVertices?.Count ?? 0))) return null;

        var rings = ClosedSectionRings(faces, origin, direction, horizontal, vertical,
            requireClosed: true, includeFaceOpenings: true).ToList();
        // shortcut: nested profiles need a cut mask with holes.
        return rings.Count > 0 && !HasNestedSectionRings(rings) ? rings : null;
    }

    private static bool HasNestedSectionRings(List<List<(double X, double Y)>> rings) =>
        rings.Any(ring => rings.Any(other => other != ring && PointInPolygon(ring[0].X, ring[0].Y, other)));

    private static HashSet<GeoFace>? OutwardConvexStairFaces(GeometryData geometry)
    {
        if (geometry.BrepSolids == null) return null;
        var outward = new HashSet<GeoFace>();
        foreach (var solid in geometry.BrepSolids)
        {
            if (!solid.IsClosed || solid.FaceIndices.Count < 4
                || solid.FaceIndices.Any(index => (uint)index >= (uint)geometry.Faces.Count))
                continue;
            var faces = solid.FaceIndices.Select(index => geometry.Faces[index]).ToArray();
            if (faces.Any(face => !face.PlaneSurface || face.OuterVertices is not { Count: >= 3 }))
                continue;

            var vertices = faces.SelectMany(face => face.OuterVertices!).ToArray();
            // The original face normals point outward only when the shell lies behind every supporting plane.
            if (faces.All(face =>
            {
                double depth = Project(face.OuterVertices![0], face.Normal);
                return vertices.All(point => Project(point, face.Normal) <= depth + 1e-6);
            }))
                outward.UnionWith(faces);
        }
        return outward.Count > 0 ? outward : null;
    }

    internal static IEnumerable<List<(double X, double Y)>> ClosedSectionRings(IReadOnlyList<GeoFace> faces,
        double[] origin, double[] direction, double[] horizontal, double[] vertical, bool requireClosed = false,
        bool includeFaceOpenings = false)
    {
        if (!includeFaceOpenings && faces.Any(face => face.InnerVertices is { Count: > 0 } || face.InnerLoops is { Count: > 0 }))
            yield break;
        var points = new Dictionary<(long, long), (double X, double Y)>();
        var neighbors = new Dictionary<(long, long), HashSet<(long, long)>>();
        foreach (var face in faces)
        {
            if (face.CurvedSurface || face.OuterVertices is not { Count: >= 3 } corners) continue;
            var ring = corners.Select(p => new[] { p[0] * FeetToMillimetres, p[1] * FeetToMillimetres,
                p[2] * FeetToMillimetres }).ToList();
            var chords = CutChords(ring, origin, direction);
            if (includeFaceOpenings && face.InnerVertices is { Count: > 0 } holes)
            {
                var openings = holes.SelectMany(hole => CutChords(hole.Select(p => new[]
                    { p[0] * FeetToMillimetres, p[1] * FeetToMillimetres, p[2] * FeetToMillimetres }).ToList(),
                    origin, direction)).ToList();
                if (openings.Count > 0)
                {
                    var trimmed = SubtractFaceOpeningChords(chords, openings);
                    if (trimmed == null) yield break;
                    chords = trimmed;
                }
            }
            foreach (var (from, to) in chords)
            {
                var a = (X: Project(from, horizontal, 1), Y: Project(from, vertical, 1));
                var b = (X: Project(to, horizontal, 1), Y: Project(to, vertical, 1));
                var ka = ((long)Math.Round(a.X * 10), (long)Math.Round(a.Y * 10));
                var kb = ((long)Math.Round(b.X * 10), (long)Math.Round(b.Y * 10));
                if (ka == kb) continue;
                if (points.TryAdd(ka, a)) neighbors.Add(ka, []);
                if (points.TryAdd(kb, b)) neighbors.Add(kb, []);
                neighbors[ka].Add(kb);
                neighbors[kb].Add(ka);
            }
        }
        if (requireClosed && neighbors.Values.Any(end => end.Count != 2)) yield break;
        var visited = new HashSet<(long, long)>();
        foreach (var start in points.Keys)
        {
            if (!visited.Add(start) || neighbors[start].Count != 2) continue;
            var ring = new List<(double X, double Y)> { points[start] };
            var current = neighbors[start].First();
            var previous = start;
            while (neighbors[current].Count == 2 && visited.Add(current))
            {
                ring.Add(points[current]);
                var next = neighbors[current].First(neighbor => neighbor != previous);
                previous = current;
                current = next;
            }
            if (current == start && ring.Count >= 3 && Math.Abs(PlanPrinter.SignedArea(ring)) > 1e-6)
                yield return ring;
        }
    }

    private static List<(double[] A, double[] B)>? SubtractFaceOpeningChords(
        List<(double[] A, double[] B)> outer, List<(double[] A, double[] B)> openings)
    {
        if (outer.Count == 0) return null;
        // Every crossing of a planar face lies on one line. Sort its endpoints along the dominant axis.
        var (start, end) = outer[0];
        int axis = Enumerable.Range(0, 3).MaxBy(i => Math.Abs(end[i] - start[i]));
        var holes = openings.Select(chord => Ascending(chord.A, chord.B)).OrderBy(hole => hole.Low[axis]).ToList();
        var result = new List<(double[] A, double[] B)>();
        int matched = 0;
        foreach (var (a, b) in outer)
        {
            var (low, high) = Ascending(a, b);
            var cursor = low;
            foreach (var (first, last) in holes)
            {
                if (first[axis] < low[axis] || last[axis] > high[axis]) continue;
                if (first[axis] < cursor[axis]) return null;
                if (first[axis] > cursor[axis]) result.Add((cursor, first));
                cursor = last;
                matched++;
            }
            if (cursor[axis] < high[axis]) result.Add((cursor, high));
        }
        return matched == holes.Count ? result : null;

        (double[] Low, double[] High) Ascending(double[] a, double[] b) => a[axis] <= b[axis] ? (a, b) : (b, a);
    }

    internal static IEnumerable<CollectedEdge> OutlineJoinedRings(List<List<(double X, double Y)>> rings, int category,
        bool coverOppositeEdges = false, bool retainSource = false, bool snapEndpoints = true)
    {
        var edges = new Dictionary<((double X, double Y) A, (double X, double Y) B), (CollectedEdge Edge, int Sides)>();
        for (int ringIndex = 0; ringIndex < rings.Count; ringIndex++)
        {
            var ring = rings[ringIndex];
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                var start = snapEndpoints ? (Math.Round(a.X * 10), Math.Round(a.Y * 10)) : a;
                var end = snapEndpoints ? (Math.Round(b.X * 10), Math.Round(b.Y * 10)) : b;
                if (start == end) continue;
                // Counterclockwise rings put their interiors on the left of each directed edge.
                bool forward = start.CompareTo(end) < 0;
                var key = forward ? (start, end) : (end, start);
                int side = forward ? 1 : 2;
                if (edges.TryGetValue(key, out var existing))
                    edges[key] = (existing.Edge, existing.Sides | side);
                else
                    edges.Add(key, (new(a.X, a.Y, b.X, b.Y, 0, 0, -1, -1, 0, 0, 0, 0, false, category)
                        { SourceRingEdge = retainSource ? (ringIndex, i) : null }, side));
            }
        }
        return VisibleTerrainEdges(edges.Values.Where(e => e.Sides != 3).Select(e => e.Edge), rings, [],
            coverBoundary: false, coverOppositeEdges: coverOppositeEdges);
    }

    private static double Project(double[] point, double[] basis, double units = FeetToMillimetres) =>
        (point[0] * basis[0] + point[1] * basis[1] + point[2] * basis[2]) * units;

    private static IEnumerable<CollectedEdge> SiteWireEdges(
        List<(List<double[]> Points, double Facing)> faces, double[] horizontal, double[] vertical,
        double[] direction, double planeDepth, bool cutMesh)
    {
        var edges = new HashSet<(long Ax, long Ay, long Bx, long By)>();
        foreach (var (points, facing) in faces)
        {
            if (!cutMesh && facing >= 0) continue;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                double da = Project(a, direction, 1) - planeDepth, db = Project(b, direction, 1) - planeDepth;
                if (da < 0 && db < 0) continue;
                double ax = Project(a, horizontal, 1), ay = Project(a, vertical, 1);
                double bx = Project(b, horizontal, 1), by = Project(b, vertical, 1);
                if (da < 0 || db < 0)
                {
                    double t = da / (da - db);
                    double x = ax + t * (bx - ax), y = ay + t * (by - ay);
                    if (da < 0) { ax = x; ay = y; }
                    else { bx = x; by = y; }
                }
                var key = ProjectedEdgeKey(ax, ay, bx, by);
                if (key.Ax == key.Bx && key.Ay == key.By || !edges.Add(key)) continue;
                yield return new CollectedEdge(ax, ay, bx, by, 0, 0, -1, -1, 0, 0, 0, 0, false,
                    (int)BuiltInCategory.OST_Entourage);
            }
        }
    }

    private static bool PointInPolygon(double x, double y, List<(double X, double Y)> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>The distance from a point to the boundary of a polygon, in the polygon's units.</summary>
    private static double DistToPolygon(double x, double y, List<(double X, double Y)> polygon) =>
        Math.Sqrt(ClosestPolygonPoint(x, y, polygon).DistanceSquared);

    private static (double X, double Y, double DistanceSquared) ClosestPolygonPoint(
        double x, double y, List<(double X, double Y)> polygon)
    {
        var best = (X: x, Y: y, DistanceSquared: double.PositiveInfinity);
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (ax, ay) = polygon[j];
            var (bx, by) = polygon[i];
            double dx = bx - ax, dy = by - ay;
            double lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared > 1e-18 ? ((x - ax) * dx + (y - ay) * dy) / lengthSquared : 0;
            t = Math.Clamp(t, 0, 1);
            double px = ax + t * dx, py = ay + t * dy;
            double distSquared = (x - px) * (x - px) + (y - py) * (y - py);
            if (distSquared < best.DistanceSquared) best = (px, py, distSquared);
        }
        return best;
    }

    /// <summary>Prints one elevation view to a one-page PDF document at the view's scale.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="viewId">The element id of the view; it must be one of <see cref="Views"/>.</param>
    /// <param name="options">The paper and margin; null uses the defaults.</param>
    public static PdfDocument Render(RvtDocument document, int viewId, PlanPrintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PlanPrintOptions();
        var views = new Dictionary<int, ElevationView>();
        foreach (var view in Views(document)) views[view.ViewId] = view;
        if (!views.TryGetValue(viewId, out var elevation))
            throw new InvalidOperationException($"View {viewId} is not a compass elevation (see ElevationPrinter.Views).");
        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _), Compress = true };
        AddElevationPage(pdf, document, elevation, options);
        return pdf;
    }

    /// <summary>Draws one elevation as a page of the document.</summary>
    internal static void AddElevationPage(PdfDocument pdf, RvtDocument document, ElevationView view,
        PlanPrintOptions options, Element? planeView = null)
    {
        ElevationContent content = Collect(document, view.Direction, false, planeView)
            ?? throw new InvalidOperationException($"The model has no face to project for the {view.Name} elevation.");
        double width = (content.MaxX - content.MinX) / view.Scale + LabelMarginMillimetres;
        double height = (content.MaxY - content.MinY) / view.Scale;
        PdfPageSize paper = options.PaperSize ?? (width + 2 * options.MarginMillimetres <= PdfPageSize.A4.Landscape.WidthMillimetres
            && height + 2 * options.MarginMillimetres <= PdfPageSize.A4.Landscape.HeightMillimetres
            ? PdfPageSize.A4.Landscape
            : width + 2 * options.MarginMillimetres <= PdfPageSize.A3.Landscape.WidthMillimetres
                && height + 2 * options.MarginMillimetres <= PdfPageSize.A3.Landscape.HeightMillimetres
                ? PdfPageSize.A3.Landscape
                : width + 2 * options.MarginMillimetres <= PdfPageSize.A2.Landscape.WidthMillimetres
                    && height + 2 * options.MarginMillimetres <= PdfPageSize.A2.Landscape.HeightMillimetres
                    ? PdfPageSize.A2.Landscape
                    : PdfPageSize.A1.Landscape);
        double usableWidth = paper.WidthMillimetres - 2 * options.MarginMillimetres;
        double usableHeight = paper.HeightMillimetres - 2 * options.MarginMillimetres;
        double scale = view.Scale;
        if (width > usableWidth || height > usableHeight)
            scale = view.Scale * Math.Max(width / usableWidth, height / usableHeight);
        double offsetX = (paper.WidthMillimetres - (content.MaxX - content.MinX) / scale - LabelMarginMillimetres) / 2;
        double offsetY = (paper.HeightMillimetres - (content.MaxY - content.MinY) / scale) / 2;
        PdfPage page = pdf.AddPage(paper);
        if (!string.IsNullOrEmpty(view.Name)) page.OutlineTitle = view.Name;
        if (options.DrawFrame)
        {
            double m = options.MarginMillimetres;
            page.DrawRectangle(m, m, paper.WidthMillimetres - 2 * m, paper.HeightMillimetres - 2 * m, 0.25);
        }
        DrawContent(page, content, scale, offsetX, offsetY, view.Direction == "section" ? 0.82 : 1, document,
            unshadedHiddenLine: document.RawData.UnshadedHiddenLineViews?.Contains(view.ViewId) == true);
        if (pdf.Font != null)
        {
            page.DrawText(view.Name, options.MarginMillimetres + 2, paper.HeightMillimetres - options.MarginMillimetres - 6, 4);
            page.DrawText($"1:{view.Scale}", options.MarginMillimetres + 2,
                paper.HeightMillimetres - options.MarginMillimetres - 11, 3, PdfTextAlignment.Left, 0.35);
        }
    }

    /// <summary>The width reserved to the right of an elevation for the level markers and their labels, in
    /// millimetres of the page.</summary>
    internal const double LabelMarginMillimetres = 42;

    /// <summary>The part of a projected polygon inside a rectangle: the detail window of a section,
    /// or the crop rectangle a clipped plan draws.</summary>
    internal static List<(double X, double Y)> ClipToWindow(List<(double X, double Y)> polygon, double left,
        double right, double bottom, double top)
    {
        var result = ClipHalfPlane(polygon, point => point.X >= left, point => point.X - left);
        result = ClipHalfPlane(result, point => point.X <= right, point => right - point.X);
        result = ClipHalfPlane(result, point => point.Y >= bottom, point => point.Y - bottom);
        result = ClipHalfPlane(result, point => point.Y <= top, point => top - point.Y);
        return result;
    }

    internal static List<(double X, double Y)> ClipHalfPlane(List<(double X, double Y)> polygon,
        Func<(double X, double Y), bool> inside, Func<(double X, double Y), double> distance)
    {
        var result = new List<(double X, double Y)>(polygon.Count + 2);
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            double da = distance(a), db = distance(b);
            if (inside(a)) result.Add(a);
            if (inside(a) != inside(b))
            {
                double t = da / (da - db);
                result.Add((a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }
        return result;
    }
    /// <summary>Draws the compound-layer boundaries inside wall and floor cross-sections: two parallel cut chords
    /// a wall type thickness apart are the two faces of one wall, and the type's layer stack subdivides the span
    /// between them. Only pairs with no third parallel edge between them qualify, so two separate walls at one
    /// type thickness apart keep their gap instead of gaining phantom layers. The stack runs interior-first (the
    /// mirrored choice measured higher on the sample), so the source face normal decides the side: a face
    /// looking away from its pair starts the stack as authored, a face looking at it runs the stack mirrored.
    /// Layer lines ink thin, like projection edges.</summary>
    internal static void AddLayerLines(
        List<(double X1, double Y1, double X2, double Y2, double Nx, double Ny, double Nz, int TypeId, int CategoryId, double Depth)> raw,
        RvtDocument document,
        List<CollectedEdge> output,
        double[] viewDir, double[] horiz, double[] vert,
        long pnx, long pny, long pnz, long poff, IReadOnlySet<int>? sectionedLayerChords = null)
    {
        var stacks = new Dictionary<int, (double Total, double[] Offsets)>();
        foreach (var chord in raw)
        {
            if (chord.CategoryId != (int)BuiltInCategory.OST_Walls
                && chord.CategoryId != (int)BuiltInCategory.OST_Floors) continue;
            if (chord.TypeId <= 0 || stacks.ContainsKey(chord.TypeId)) continue;
            var layers = document.GetElement(chord.TypeId)?.SerializedHostTypeLayers;
            if (layers is not { Length: > 1 }) continue;
            var cumulative = new List<double>();
            double total = 0;
            foreach (var layer in layers)
            {
                total += BitConverter.Int64BitsToDouble(layer.LayerWidthBits) * 304.8;
                cumulative.Add(total);
            }
            if (total < 10) continue;
            stacks[chord.TypeId] = (total, cumulative.Take(cumulative.Count - 1).ToArray());
        }
        if (stacks.Count == 0) return;
        var cand = new List<int>();
        for (int k = 0; k < raw.Count; k++)
        {
            var chord = raw[k];
            if ((chord.CategoryId == (int)BuiltInCategory.OST_Walls
                    || chord.CategoryId == (int)BuiltInCategory.OST_Floors)
                && stacks.ContainsKey(chord.TypeId)) cand.Add(k);
        }
        for (int a = 0; a < cand.Count; a++)
        {
            for (int b = a + 1; b < cand.Count; b++)
            {
                if (sectionedLayerChords?.Contains(cand[a]) == true
                    || sectionedLayerChords?.Contains(cand[b]) == true) continue;
                var first = raw[cand[a]];
                var second = raw[cand[b]];
                if (first.TypeId != second.TypeId) continue;
                double ux = first.X2 - first.X1, uy = first.Y2 - first.Y1;
                double ulen = Math.Sqrt(ux * ux + uy * uy);
                if (ulen < 100) continue;
                ux /= ulen; uy /= ulen;
                double vx = second.X2 - second.X1, vy = second.Y2 - second.Y1;
                double vlen = Math.Sqrt(vx * vx + vy * vy);
                if (vlen < 100) continue;
                vx /= vlen; vy /= vlen;
                if (Math.Abs(ux * vy - uy * vx) > 0.003) continue;
                double nx = -uy, ny = ux;
                double mxi = (first.X1 + first.X2) / 2, myi = (first.Y1 + first.Y2) / 2;
                double mxj = (second.X1 + second.X2) / 2, myj = (second.Y1 + second.Y2) / 2;
                if ((mxj - mxi) * nx + (myj - myi) * ny < 0) { nx = -nx; ny = -ny; }
                double gap = Math.Abs((mxj - mxi) * nx + (myj - myi) * ny);
                var (total, offsets) = stacks[first.TypeId];
                if (Math.Abs(gap - total) > Math.Max(2.0, 0.02 * total)) continue;
                double ai0 = (first.X1 - mxi) * ux + (first.Y1 - myi) * uy;
                double ai1 = (first.X2 - mxi) * ux + (first.Y2 - myi) * uy;
                double aj0 = (second.X1 - mxi) * ux + (second.Y1 - myi) * uy;
                double aj1 = (second.X2 - mxi) * ux + (second.Y2 - myi) * uy;
                double lo = Math.Max(Math.Min(ai0, ai1), Math.Min(aj0, aj1));
                double hi = Math.Min(Math.Max(ai0, ai1), Math.Max(aj0, aj1));
                if (hi - lo < 120) continue;
                bool blocked = false;
                for (int kk = 0; kk < cand.Count && !blocked; kk++)
                {
                    if (kk == a || kk == b) continue;
                    var other = raw[cand[kk]];
                    double wx = other.X2 - other.X1, wy = other.Y2 - other.Y1;
                    double wlen = Math.Sqrt(wx * wx + wy * wy);
                    if (wlen < 20) continue;
                    wx /= wlen; wy /= wlen;
                    if (Math.Abs(ux * wy - uy * wx) > 0.01) continue;
                    double mox = (other.X1 + other.X2) / 2, moy = (other.Y1 + other.Y2) / 2;
                    double dk = Math.Abs((mox - mxi) * nx + (moy - myi) * ny);
                    if (!(dk > 0.5 && dk < gap - 0.5)) continue;
                    double q0 = (other.X1 - mxi) * ux + (other.Y1 - myi) * uy;
                    double q1 = (other.X2 - mxi) * ux + (other.Y2 - myi) * uy;
                    if (Math.Min(hi, Math.Max(q0, q1)) - Math.Max(lo, Math.Min(q0, q1)) > 10) blocked = true;
                }
                if (blocked) continue;
                double m3x = nx * horiz[0] + ny * vert[0];
                double m3y = nx * horiz[1] + ny * vert[1];
                double m3z = nx * horiz[2] + ny * vert[2];
                double side = first.Nx * m3x + first.Ny * m3y + first.Nz * m3z;
                double[] use = side > 0 ? offsets : offsets.Select(o => total - o).ToArray();
                foreach (double offset in use)
                {
                    double t0 = (first.X1 - mxi) * ux + (first.Y1 - myi) * uy;
                    double t1 = (first.X2 - mxi) * ux + (first.Y2 - myi) * uy;
                    double nt0 = Math.Max(Math.Min(t0, t1), lo);
                    double nt1 = Math.Min(Math.Max(t0, t1), hi);
                    if (nt1 - nt0 < 5) continue;
                    double pax = mxi + ux * nt0 + nx * offset, pay = myi + uy * nt0 + ny * offset;
                    double pbx = mxi + ux * nt1 + nx * offset, pby = myi + uy * nt1 + ny * offset;
                    bool covered = false;
                    foreach (var have in raw)
                    {
                        double hx = have.X2 - have.X1, hy = have.Y2 - have.Y1;
                        double hlen = Math.Sqrt(hx * hx + hy * hy);
                        if (hlen < 5) continue;
                        hx /= hlen; hy /= hlen;
                        if (Math.Abs(ux * hy - uy * hx) > 0.01) continue;
                        double hd = Math.Abs((pax - have.X1) * -hy + (pay - have.Y1) * hx);
                        if (hd > 0.7) continue;
                        double h0 = (have.X1 - mxi) * ux + (have.Y1 - myi) * uy;
                        double h1 = (have.X2 - mxi) * ux + (have.Y2 - myi) * uy;
                        if (Math.Min(nt1, Math.Max(h0, h1)) - Math.Max(nt0, Math.Min(h0, h1)) > 5)
                        { covered = true; break; }
                    }
                    if (covered) continue;
                    output.Add(new CollectedEdge(pax, pay, pbx, pby, first.Depth, first.Depth,
                        -1, -1, pnx, pny, pnz, poff, false, first.CategoryId));
                }
            }
        }
    }

    /// <summary>Clips a face for painting and occlusion, retaining which edges came from its perimeter.</summary>
    private static (List<double[]> Ring, bool[] IncomingEdges) ClipToWindow3(List<double[]> ring,
        double[] horizontal, double[] vertical, double left, double right, double bottom, double top)
    {
        var vertices = ring.Select(point => (Point: point, IncomingEdge: true)).ToList();
        vertices = ClipHalfPlane3(vertices, point => Project(point, horizontal, 1) - left);
        vertices = ClipHalfPlane3(vertices, point => right - Project(point, horizontal, 1));
        vertices = ClipHalfPlane3(vertices, point => Project(point, vertical, 1) - bottom);
        vertices = ClipHalfPlane3(vertices, point => top - Project(point, vertical, 1));
        return (vertices.Select(vertex => vertex.Point).ToList(), vertices.Select(vertex => vertex.IncomingEdge).ToArray());
    }

    internal static List<(double[] Point, bool IncomingEdge)> ClipHalfPlane3(
        List<(double[] Point, bool IncomingEdge)> ring, Func<double[], double> distance)
    {
        var result = new List<(double[] Point, bool IncomingEdge)>(ring.Count + 2);
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            double da = distance(a.Point), db = distance(b.Point);
            bool insideA = da >= 0, insideB = db >= 0;
            if (insideA) result.Add(a);
            if (insideA == insideB) continue;

            double t = da / (da - db);
            var from = a.Point;
            var to = b.Point;
            // Along the crop boundary the entrance follows the previous exit across an artificial edge.
            result.Add(([from[0] + (to[0] - from[0]) * t, from[1] + (to[1] - from[1]) * t,
                from[2] + (to[2] - from[2]) * t], insideA && b.IncomingEdge));
        }
        return result;
    }

    internal static bool ClipSegment(double x0, double y0, double x1, double y1,
        double rx0, double ry0, double rx1, double ry1,
        out double cx0, out double cy0, out double cx1, out double cy1)
    {
        cx0 = x0; cy0 = y0; cx1 = x1; cy1 = y1;
        double dx = x1 - x0, dy = y1 - y0;
        double t0 = 0, t1 = 1;
        double[] p = [-dx, dx, -dy, dy];
        double[] q = [x0 - rx0, rx1 - x0, y0 - ry0, ry1 - y0];
        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-12) { if (q[i] < 0) return false; }
            else
            {
                double t = q[i] / p[i];
                if (p[i] < 0) t0 = Math.Max(t0, t);
                else t1 = Math.Min(t1, t);
            }
        }
        if (t0 > t1) return false;
        cx0 = x0 + t0 * dx; cy0 = y0 + t0 * dy;
        cx1 = x0 + t1 * dx; cy1 = y0 + t1 * dy;
        return (cx1 - cx0) * (cx1 - cx0) + (cy1 - cy0) * (cy1 - cy0) > 1e-12;
    }
    /// <summary>Chords where the cut plane slices through a face: the intersection segments of the ring with the
    /// plane, in 3D. Only a face the plane passes through (vertices strictly on both sides, 1 mm tolerance, the
    /// same tolerance that flags a cut edge) yields chords; a face lying wholly in the plane is skipped because
    /// its boundary is already cut-flagged when the face outlines. Crossing points interpolate along the ring
    /// edges; consecutive points pair along the chord's dominant axis, one span per inside interval.</summary>
    internal static List<(double[] A, double[] B)> CutChords(List<double[]> ring, double[] origin, double[] dir)
    {
        int n = ring.Count;
        var dist = new double[n];
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            dist[i] = (ring[i][0] - origin[0]) * dir[0] + (ring[i][1] - origin[1]) * dir[1]
                + (ring[i][2] - origin[2]) * dir[2];
            if (dist[i] < min) min = dist[i];
            if (dist[i] > max) max = dist[i];
        }
        var result = new List<(double[], double[])>();
        if (!(min < -1.0 && max > 1.0)) return result;
        var points = new List<double[]>();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (Math.Abs(dist[i]) <= 1.0) points.Add(ring[i]);
            if ((dist[i] < -1.0 && dist[j] > 1.0) || (dist[i] > 1.0 && dist[j] < -1.0))
            {
                double t = dist[i] / (dist[i] - dist[j]);
                points.Add([ring[i][0] + (ring[j][0] - ring[i][0]) * t,
                    ring[i][1] + (ring[j][1] - ring[i][1]) * t,
                    ring[i][2] + (ring[j][2] - ring[i][2]) * t]);
            }
        }
        var unique = new List<double[]>();
        foreach (var point in points)
        {
            bool seen = false;
            foreach (var have in unique)
            {
                if (Math.Abs(point[0] - have[0]) + Math.Abs(point[1] - have[1]) + Math.Abs(point[2] - have[2]) < 0.3)
                { seen = true; break; }
            }
            if (!seen) unique.Add(point);
        }
        if (unique.Count < 2) return result;
        double x0 = unique.Min(p => p[0]), x1 = unique.Max(p => p[0]);
        double y0 = unique.Min(p => p[1]), y1 = unique.Max(p => p[1]);
        double z0 = unique.Min(p => p[2]), z1 = unique.Max(p => p[2]);
        int axis = 0;
        double spread = x1 - x0;
        if (y1 - y0 > spread) { axis = 1; spread = y1 - y0; }
        if (z1 - z0 > spread) axis = 2;
        unique.Sort((a, b) => a[axis].CompareTo(b[axis]));
        for (int i = 0; i + 1 < unique.Count; i += 2)
            result.Add((unique[i], unique[i + 1]));
        return result;
    }

    /// <summary>The part of a polygon that stands behind the cutting plane (the side the view direction points
    /// to), the plane crossing a polygon adding the points where it crosses the plane.</summary>
    internal static List<double[]> ClipBehind(List<double[]> polygon, double[] origin, double[] direction)
    {
        var result = new List<double[]>(polygon.Count + 2);
        for (int i = 0; i < polygon.Count; i++)
        {
            double[] a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            double da = (a[0] - origin[0]) * direction[0] + (a[1] - origin[1]) * direction[1]
                + (a[2] - origin[2]) * direction[2];
            double db = (b[0] - origin[0]) * direction[0] + (b[1] - origin[1]) * direction[1]
                + (b[2] - origin[2]) * direction[2];
            if (da >= 0) result.Add(a);
            if (da >= 0 != db >= 0)
            {
                double t = da / (da - db);
                result.Add([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t]);
            }
        }
        return result;
    }

    /// <summary>Draws the projected elevation with (MinX, MinY) at (originX, originY), one model unit per 1/scale.</summary>
    /// <summary>The section marks of a plan: the trace of every section plane on the plan, so a plan shows where it is
    /// cut, the way the reference plans show their section marks. The point is the origin of the plane, the direction
    /// the trace runs, and the name of the view the mark belongs to, all in millimetres.
    /// </summary>
    /// <summary>The section marks of the plan viewports: the trace of each section plane, filtered to the
    /// viewport showing it. A mark is skipped when its view is not a section (an undecodable block), when its
    /// section annotation names a section or elevation parent (a callout belongs to that view, not to a plan),
    /// when the viewport scale is coarser than the section allows, and when its section names another plan as
    /// parent. Without a viewport context no viewport filtering applies.</summary>
    internal static List<SectionMark> SectionMarks(RvtDocument document,
        int? viewportViewId = null, double? viewportScaleDenominator = null)
    {
        var marks = new List<SectionMark>();
        var hidden = viewportViewId is int planId ? document.RawData.ViewHiddenElements?.GetValueOrDefault(planId) : null;
        var planExtents = viewportViewId is int ownerId
            && document.GetElement(ownerId) is { ViewAnnotationCropActive: false, ViewCrop: { Right: [1, 0, 0], Up: [0, 1, 0] } }
            && document.RawData.DatumExtents?.GetValueOrDefault(ownerId) is { Normal: [0, 0, -1] } savedExtents
            ? savedExtents.Sections : null;
        foreach (var view in document.Elements)
        {
            if (view.CategoryId != (int)BuiltInCategory.OST_Views) continue;
            if (view.RootClassName is not ("DBViewSection" or "DBViewElevation") || hidden?.Contains(view.Id) == true) continue;
            string name = view.Name ?? "";
            if (!name.Contains("section", StringComparison.OrdinalIgnoreCase)) continue;
            if (view.ViewOrigin is not { Length: 3 } origin || view.ViewDirection is not { Length: 3 } direction
                || view.ViewVerticalDirection is not { Length: 3 } vertical) continue;
            if (Math.Abs(vertical[2]) < 0.9) continue;
            // A plan trace needs the file's section extent: traces are drawn from Section records, so a
            // section view with no decoded extent (no viewer record) leaves no trace, the way the
            // reference draws none for the Architectural Section (viewer -1, no extent record) while
            // rvt-prt draws traces only from Section m_frontCut extents.
            if (view.SectionViewerId is not int viewerId || viewerId <= 0 || hidden?.Contains(viewerId) == true
                || document.RawData.SectionViews?.TryGetValue(viewerId, out var section) != true)
                continue;
            // A callout of another view marks that view, not the plan; a finer-scale-only section
            // (a 1:20 detail) stays off a coarser plan, and a plan-parented mark stays in its plan.
            if (section.ParentViewId > 0
                && document.GetElement(section.ParentViewId) is { } parent
                && parent.RootClassName is ("DBViewSection" or "DBViewElevation"))
                continue;
            if (viewportScaleDenominator is double scale && scale > 0 && section.CoarserScale > 0
                && 1.0 / scale < section.CoarserScale - 1e-9)
                continue;
            if (viewportViewId is int viewportId && section.ParentViewId > 0
                && section.ParentViewId != viewportId
                && document.GetElement(section.ParentViewId) is { } owner
                && owner.RootClassName is not ("DBViewSection" or "DBViewElevation"))
                continue;
            // The trace of the plane runs along the horizontal in-plane axis: the cross product of the view direction
            // and the vertical of the view.
            double dx = direction[1] * vertical[2] - direction[2] * vertical[1];
            double dy = direction[2] * vertical[0] - direction[0] * vertical[2];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.2) continue;
            double x = origin[0] * FeetToMillimetres, y = origin[1] * FeetToMillimetres;
            double ux = dx / length, uy = dy / length;
            // The file's front-cut polyline is the true trace (a jogged section draws its jogs); without a
            // decoded extent the mark stays a straight ray through the plane origin, as before.
            List<(double X, double Y)> points;
            bool bounded, usesPlanExtent = false;
            if (section.FrontCut is { Count: >= 2 } cut)
            {
                points = cut.Select(p => (p[0] * FeetToMillimetres, p[1] * FeetToMillimetres)).ToList();
                if (planExtents?.GetValueOrDefault(viewerId) is { } extent && extent.SegmentCount == points.Count - 1
                    && document.RawData.SectionTraceOrigins?.TryGetValue(viewerId, out var traceOrigin) == true)
                    usesPlanExtent = ApplyPlanSectionExtent(points, ux, uy, traceOrigin, extent);
                bounded = true;
            }
            else if (section.FrontCut != null)
            {
                continue;
            }
            else
            {
                points = [(x, y), (x + ux, y + uy)];
                bounded = false;
            }
            // A detail section that shares the plane of the building section marks the same cut: one mark is enough.
            var (sx, sy) = points[0];
            var (ex, ey) = points[^1];
            double sdx = ex - sx, sdy = ey - sy;
            double slen = Math.Sqrt(sdx * sdx + sdy * sdy);
            if (slen < 1e-9) continue;
            if (marks.Any(mark =>
            {
                var (mx, my) = mark.Points[0];
                var (mex, mey) = mark.Points[^1];
                double mdx = mex - mx, mdy = mey - my;
                double mlen = Math.Sqrt(mdx * mdx + mdy * mdy);
                if (mlen < 1e-9) return false;
                return Math.Abs(mark.Points[0].X - sx) < 50 && Math.Abs(mark.Points[0].Y - sy) < 50
                    && Math.Abs((mdx * sdx + mdy * sdy) / (mlen * slen)) > 0.99;
            })) continue;
            marks.Add(new SectionMark(points, bounded, name, view.Id, usesPlanExtent));
        }
        return marks;
    }

    private static bool ApplyPlanSectionExtent(List<(double X, double Y)> points, double ux, double uy,
        (double X, double Y) origin, Parsing.TfBlockDeserializer.SectionTraceExtent extent)
    {
        // View extents are distances along the section axis from its stored trace origin.
        // Move the outer endpoints along their own legs so the jogs keep their model positions.
        double zero = (origin.X * ux + origin.Y * uy) * FeetToMillimetres;
        double first = zero + extent.Start * FeetToMillimetres;
        double last = zero + extent.End * FeetToMillimetres;
        double startLeg = Along(points[1]) - Along(points[0]);
        double endLeg = Along(points[^1]) - Along(points[^2]);
        if (!(startLeg > 0 && endLeg > 0 && first < Along(points[1]) && last > Along(points[^2])
            && Math.Abs(Cross(points[0]) - Cross(points[1])) < 1e-6
            && Math.Abs(Cross(points[^2]) - Cross(points[^1])) < 1e-6)) return false;

        points[0] = Extend(points[0], first);
        points[^1] = Extend(points[^1], last);
        return true;

        double Along((double X, double Y) p) => p.X * ux + p.Y * uy;
        double Cross((double X, double Y) p) => p.X * uy - p.Y * ux;
        (double X, double Y) Extend((double X, double Y) p, double distance)
        {
            double delta = distance - Along(p);
            return (p.X + delta * ux, p.Y + delta * uy);
        }
    }

    internal static void DrawContent(PdfPage page, ElevationContent content, double scale, double originX,
        double originY, double faceFill = 1, RvtDocument? document = null,
        (double X0, double Y0, double X1, double Y1)? clipRect = null, bool drawNativeDatums = true,
        bool unshadedHiddenLine = false)
    {
        if (content.SectionBands is { } bands)
        {
            var crop = clipRect ?? (content.MinX, content.MinY, content.MaxX, content.MaxY);
            foreach (var band in bands)
            {
                double left = Math.Max(crop.Item1, band.Left), right = Math.Min(crop.Item3, band.Right);
                if (right <= left) continue;
                DrawModel(page, band.Content, scale, originX + (band.Content.MinX - content.MinX) / scale,
                    originY + (band.Content.MinY - content.MinY) / scale, faceFill, document,
                    (left, crop.Item2, right, crop.Item4), unshadedHiddenLine);
            }
        }
        else
            DrawModel(page, content, scale, originX, originY, faceFill, document, clipRect, unshadedHiddenLine);

        DrawFallbackDatums(page, content, scale, originX, originY, document, clipRect);
        if (drawNativeDatums)
        {
            DrawNativeDatums(page, content, scale, originX, originY, document, clipRect);
            CircularAnnotations.Draw(page, document, content.AnnotationSymbols ?? [], scale,
                x => (x - content.MinX) / scale + originX, y => (y - content.MinY) / scale + originY);
        }
    }

    private const double DefaultOutlinePen = 0.2;

    private static void DrawModel(PdfPage page, ElevationContent content, double scale, double originX,
        double originY, double faceFill, RvtDocument? document,
        (double X0, double Y0, double X1, double Y1)? clipRect, bool unshadedHiddenLine)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        // The pen widths of the document's Object Styles, when they decoded: a section cuts its mass with each
        // category's own cut pen (a railing cuts thinner than a wall), an elevation outlines it with the Walls
        // projection pen. Only the edges the cut created ink cut; the projected edges of a section ink thin
        // like the reference does.
        bool isSection = faceFill < 1;
        double faceLine = DefaultOutlinePen, projLine = 0.1;
        int penScale = (int)Math.Round(scale);
        if (document != null)
        {
            if (PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Walls, isSection, penScale, out double w))
                faceLine = w;
            if (isSection && PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Walls, false, penScale, out double p))
                projLine = p;
        }
        // An elevation has no cut: every edge inks with the face line as before.
        if (!isSection) projLine = faceLine;
        double clipX0 = clipRect?.X0 ?? content.MinX, clipY0 = clipRect?.Y0 ?? content.MinY;
        double clipX1 = clipRect?.X1 ?? content.MaxX, clipY1 = clipRect?.Y1 ?? content.MaxY;
        var groundPolygons = new List<List<(double X, double Y)>>();
        if (content.GroundHatch is { } terrain && terrain.BaseY < clipY1)
        {
            double baseY = Math.Max(terrain.BaseY, clipY0);
            foreach (var span in GroundSpans(terrain, baseY))
            {
                var polygon = ClipToWindow(span, clipX0, clipX1, baseY, clipY1);
                if (polygon.Count >= 3) groundPolygons.Add(polygon.Select(p => (ToX(p.X), ToY(p.Y))).ToList());
            }
        }
        double groundPen = document != null
            && PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_TopographySurface, true, penScale, out var soilPen)
            ? soilPen : 0.1;
        var groundTiles = content.GroundHatch is { } tiledGround && groundPolygons.Count > 0
            ? FillHatch.DraftingTiles(tiledGround.Grids, ToX(tiledGround.AnchorX), ToY(tiledGround.AnchorY), groundPen) : null;
        if (content.SkyGradient is { } gradient)
        {
            if (clipY1 > 0)
            {
                double bottom = Math.Max(0, clipY0);
                var horizon = Color(gradient.Horizon);
                var sky = Color(gradient.Sky);
                double t = bottom / clipY1;
                page.FillVerticalGradient(ToX(clipX0), ToY(bottom), (clipX1 - clipX0) / scale, (clipY1 - bottom) / scale,
                    new PdfColor(horizon.R + (sky.R - horizon.R) * t, horizon.G + (sky.G - horizon.G) * t,
                        horizon.B + (sky.B - horizon.B) * t), sky);
            }
            if (clipY0 < 0 && gradient.Ground is int groundColor)
            {
                double top = Math.Min(0, clipY1);
                page.DrawPolyline([(ToX(clipX0), ToY(clipY0)), (ToX(clipX1), ToY(clipY0)),
                    (ToX(clipX1), ToY(top)), (ToX(clipX0), ToY(top))], 0, true, Color(groundColor));
            }

            static PdfColor Color(int packed) => new((packed & 255) / 255d, ((packed >> 8) & 255) / 255d,
                (packed >> 16) / 255d);
        }
        // In opaque hidden line, the site surface covers the gradient behind the building.
        var terrainShadows = new List<List<(double X, double Y)>>();
        double terrainShadowFill = 1;
        foreach (var (contextPoints, _, isTerrain) in content.Context)
        {
            var points = clipRect.HasValue ? ClipToWindow(contextPoints, clipX0, clipX1, clipY0, clipY1) : contextPoints;
            if (points.Count < 3) continue;
            var polygon = points.Select(point => (X: ToX(point.X), Y: ToY(point.Y))).ToList();
            double fill = isTerrain && content.SkyGradient != null ? 1 : 0.9;
            page.DrawPolyline(polygon, 0, true, fill);
            if (content.CastShadows?.GetValueOrDefault(contextPoints) is not { } shadow) continue;
            double winding = Math.Sign(PlanPrinter.SignedArea(points));
            if (winding == 0) continue;
            var sides = new List<(double X, double Y, double C)>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                double x = (a.Y - b.Y) * winding, y = (b.X - a.X) * winding;
                sides.Add((x, y, -x * a.X - y * a.Y));
            }
            if (shadow.Openings.Count > 0 || sides.Any(side => points.Any(p =>
                side.X * p.X + side.Y * p.Y + side.C < -0.01)))
            {
                DrawShadow(contextPoints, polygon, fill);
                continue;
            }
            foreach (var ring in shadow.Rings)
            {
                var clipped = ring;
                foreach (var (x, y, c) in sides)
                {
                    double Distance((double X, double Y) p) => x * p.X + y * p.Y + c;
                    clipped = ClipHalfPlane(clipped, p => Distance(p) >= 0, Distance);
                }
                if (clipped.Count >= 3) terrainShadows.Add(clipped.Select(p => (ToX(p.X), ToY(p.Y))).ToList());
            }
            terrainShadowFill = fill * shadow.Light;
        }
        if (terrainShadows.Count > 0)
        {
            page.BeginClip(ToX(clipX0), ToY(clipY0), (clipX1 - clipX0) / scale, (clipY1 - clipY0) / scale);
            ClipShadowAtGround(ToX(clipX0), ToY(clipY0), ToX(clipX1), ToY(clipY1));
            page.FillPolygonUnion(terrainShadows, terrainShadowFill);
            page.EndClip();
        }
        if (content.SiteSilhouettes is { } silhouettes)
        {
            var background = silhouettes.Where(ring => content.ForegroundSite?.Sources.Contains(ring) != true).ToList();
            var rings = background.Select(ring => ClipToWindow(ring, clipX0, clipX1, clipY0, clipY1))
                .Where(ring => ring.Count >= 3).Select(ring =>
                    ring.Select(point => (ToX(point.X), ToY(point.Y))).ToList());
            double fill = content.SkyGradient != null ? 1 : 0.9;
            page.FillPolygonUnion(rings, fill);
            if (content.PlantTones is { } shades)
                foreach (var (gray, visible) in shades)
                    page.FillPolygonUnion(visible.Select(ring => ClipToWindow(ring, clipX0, clipX1, clipY0, clipY1))
                        .Where(ring => ring.Count >= 3).Select(ToPage), gray / 255.0);
            if (content.CastShadows != null)
            {
                var shadedPlants = background.Select(ring => content.CastShadows.GetValueOrDefault(ring))
                    .OfType<CastShadow>().ToList();
                if (shadedPlants.Count > 0)
                {
                    page.BeginClip(ToX(clipX0), ToY(clipY0), (clipX1 - clipX0) / scale, (clipY1 - clipY0) / scale);
                    ClipShadowAtGround(ToX(clipX0), ToY(clipY0), ToX(clipX1), ToY(clipY1));
                    page.FillPolygonUnion(shadedPlants.SelectMany(shadow => shadow.Rings)
                        .Select(ring => ring.Select(point => (ToX(point.X), ToY(point.Y))).ToList()),
                        fill * shadedPlants[0].Light);
                    page.EndClip();
                }
            }
        }
        var sectionMasks = content.GroundHatch?.SectionMasks;
        if (isSection && groundTiles != null && sectionMasks == null) DrawGroundTiles(groundTiles);
        // The faces only fill, far to near, so nearer mass covers what stands behind it; the shared edge
        // set below inks the visible lines, without the triangulation diagonals. A crop rectangle drops
        // the faces wholly outside it and clips the ones crossing it, so only the crop is drawn.
        // shortcut: use the saved opaque base in sections, until local ambient shadows are supported.
        double surfaceFill = unshadedHiddenLine || content.OpaqueSection ? 1 : faceFill;
        var surfaceTiles = new Dictionary<IReadOnlyList<Parsing.TfBlockDeserializer.PatternGrid>, List<PdfLinePattern>?>();
        var surfaceHatches = content.Hatch;
        foreach (var (points, _, _) in content.Faces)
        {
            var pagePoints = points;
            if (clipRect.HasValue)
            {
                double fx0 = double.MaxValue, fy0 = double.MaxValue, fx1 = double.MinValue, fy1 = double.MinValue;
                foreach (var point in points)
                {
                    fx0 = Math.Min(fx0, point.X); fy0 = Math.Min(fy0, point.Y);
                    fx1 = Math.Max(fx1, point.X); fy1 = Math.Max(fy1, point.Y);
                }
                if (fx1 < clipX0 || fx0 > clipX1 || fy1 < clipY0 || fy0 > clipY1) continue;
                if (fx0 < clipX0 || fx1 > clipX1 || fy0 < clipY0 || fy1 > clipY1)
                {
                    pagePoints = ClipToWindow(points, clipX0, clipX1, clipY0, clipY1);
                    if (pagePoints.Count < 3) continue;
                }
            }
            var polygon = pagePoints.Select(point => (X: ToX(point.X), Y: ToY(point.Y))).ToList();
            var modelHatch = content.ModelHatches?.GetValueOrDefault(points);
            double fill = modelHatch != null ? 1 : surfaceFill;
            double shadowBase = surfaceFill;
            if (content.FaceFills?.TryGetValue(points, out double? materialFill) == true)
            {
                if (!materialFill.HasValue) continue;
                fill = shadowBase = materialFill.Value;
            }
            var openings = content.FaceOpenings?.GetValueOrDefault(points);
            var rings = openings == null ? null : FaceRings(polygon, openings);
            var terrainVisible = content.TerrainClippedFaces?.GetValueOrDefault(points);
            if (terrainVisible is { Count: 0 }) continue;
            if (terrainVisible != null) page.BeginPolygonClip(terrainVisible.Select(ToPage), evenOdd: false);
            if (content.DetailGlass?.TryGetValue(points, out var pane) == true)
                page.FillPolygonUnion(rings ?? [polygon], pane.Gray, pane.Opacity);
            else if (rings == null)
                page.DrawPolyline(polygon, 0, true, fill);
            else
                page.FillPolygonUnion(rings, fill);
            var glazing = content.Glazing;
            bool paintPane = glazing?.IsPane(points) == true;
            if (!paintPane && modelHatch == null && content.AmbientTones?.GetValueOrDefault(points) is { } shade)
            {
                page.BeginPolygonClip(rings ?? [polygon]);
                double x = ToX(shade.X0), y = ToY(shade.Y0);
                double width = ToX(shade.X1) - x, height = ToY(shade.Y1) - y;
                page.DrawImage(shade.Image, x, y, width, height);
                if (shade.ThroughGlass is { } throughGlass && shade.Glass is { } glassMasks)
                {
                    foreach (var mask in glassMasks)
                    {
                        var paneRing = ToPage(mask.Polygon);
                        page.BeginPolygonClip(mask.Holes == null ? [paneRing] : FaceRings(paneRing, mask.Holes));
                        page.DrawImage(throughGlass, x, y, width, height);
                        page.EndClip();
                    }
                }
                page.EndClip();
            }
            if (paintPane)
                glazing!.Draw(page, content, points, rings ?? [polygon], ToX, ToY, FaceRings);
            if (modelHatch == null && content.PaneTones?.GetValueOrDefault(points) is { } paneTone)
            {
                page.BeginPolygonClip(rings ?? [polygon]);
                double x = ToX(paneTone.X0), y = ToY(paneTone.Y0);
                page.DrawImage(paneTone.Image, x, y, ToX(paneTone.X1) - x, ToY(paneTone.Y1) - y);
                page.EndClip();
            }
            if (content.ExteriorWallTones?.TryGetValue(points, out var tone) == true && tone.Light < 1)
            {
                double lower = Math.Max(tone.BaseY, pagePoints.Min(point => point.Y));
                double upper = pagePoints.Max(point => point.Y);
                double left = pagePoints.Min(point => point.X);
                double right = pagePoints.Max(point => point.X);
                if (upper > lower && right > left)
                {
                    page.BeginClip(ToX(left), ToY(lower), (right - left) / scale, (upper - lower) / scale);
                    page.FillPolygonUnion(rings ?? [polygon], 0, 1 - tone.Light);
                    page.EndClip();
                }
            }
            DrawShadow(points, polygon, shadowBase);
            if (surfaceHatches is { } hatch && hatch.Faces.TryGetValue(points, out var surfacePattern))
            {
                var (grid, width) = surfacePattern;
                double spacing = grid.Offset * FeetToMillimetres;
                double anchorX = ToX(hatch.AnchorX) + grid.OriginX * FeetToMillimetres;
                double anchorY = ToY(hatch.AnchorY) + grid.OriginY * FeetToMillimetres;
                if (hatch.UseClippedLines)
                    page.FillWithClippedParallelLines(rings ?? [polygon], spacing, grid.Angle, anchorX, anchorY, width);
                else
                    page.FillWithParallelLines(rings ?? [polygon], spacing, grid.Angle, anchorX, anchorY, width);
            }
            if (surfaceHatches?.Regions?.TryGetValue(points, out var regions) == true)
            {
                page.BeginPolygonClip(rings ?? [polygon]);
                foreach (var region in regions)
                {
                    var grid = region.Grid;
                    var regionRings = FaceRings(ToPage(region.Outer), region.Holes);
                    page.FillWithClippedParallelLines(regionRings, grid.Offset * FeetToMillimetres,
                        grid.Angle, ToX(surfaceHatches.AnchorX) + grid.OriginX * FeetToMillimetres,
                        ToY(surfaceHatches.AnchorY) + grid.OriginY * FeetToMillimetres, 0.1);
                }
                page.EndClip();
            }
            if (surfaceHatches?.DashedFaces.TryGetValue(points, out var grids) == true)
            {
                if (!surfaceTiles.TryGetValue(grids, out var tiles))
                {
                    tiles = FillHatch.DraftingTiles(grids, ToX(surfaceHatches.AnchorX), ToY(surfaceHatches.AnchorY), 0.1);
                    surfaceTiles.Add(grids, tiles);
                }
                    foreach (var tile in tiles ?? []) page.FillWithLinePattern([polygon], tile);
            }
            if (modelHatch != null)
            {
                var modelRings = FaceRings(polygon, modelHatch.Holes);
                foreach (var modelGrid in modelHatch.Grids)
                {
                    if (modelGrid.Segments == null)
                        page.FillWithParallelLines(modelRings, modelGrid.Spacing / scale, modelGrid.Angle, ToX(modelGrid.X), ToY(modelGrid.Y), 0.1);
                    else
                        page.FillWithLinePattern(modelRings, new PdfLinePattern(modelGrid.Spacing / scale, modelGrid.Angle,
                            ToX(modelGrid.X), ToY(modelGrid.Y), 0.1, modelGrid.Shift / scale,
                            modelGrid.Segments.Select(segment => segment / scale).ToArray()));
                }
            }
            if (terrainVisible != null) page.EndClip();
        }
        if (content.ForegroundSite is { Rings.Count: > 0 } site)
        {
            double fill = content.SkyGradient != null ? 1 : 0.9;
            page.BeginClip(ToX(clipX0), ToY(clipY0), (clipX1 - clipX0) / scale, (clipY1 - clipY0) / scale);
            page.FillPolygonUnion(site.Rings.Select(ToPage), fill);
            ClipShadowAtGround(ToX(clipX0), ToY(clipY0), ToX(clipX1), ToY(clipY1));
            page.FillPolygonUnion(site.Shade.Select(ToPage), fill * site.Light);
            page.EndClip();
        }

        List<(double X, double Y)> ToPage(List<(double X, double Y)> ring) =>
            ring.Select(point => (ToX(point.X), ToY(point.Y))).ToList();

        if (groundTiles != null && sectionMasks != null)
        {
            List<(double X, double Y)> frame =
                [(ToX(clipX0), ToY(clipY0)), (ToX(clipX1), ToY(clipY0)),
                 (ToX(clipX1), ToY(clipY1)), (ToX(clipX0), ToY(clipY1))];
            var caps = new List<List<(double X, double Y)>>();
            page.BeginPolygonClip([frame]);
            foreach (var mask in sectionMasks)
            {
                var clipped = ClipToWindow(mask, clipX0, clipX1, clipY0, clipY1);
                if (clipped.Count < 3) continue;
                var polygon = clipped.Select(point => (ToX(point.X), ToY(point.Y))).ToList();
                page.IntersectPolygonClip([frame, polygon]);
                if (isSection) continue;
                if (PlanPrinter.SignedArea(polygon) < 0) polygon.Reverse();
                caps.Add(polygon);
            }
            DrawGroundTiles(groundTiles, opaque: true);
            if (!isSection && document != null
                && PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Topography, true, penScale, out double terrainOutline))
            {
                var padCuts = content.Edges.Where(edge => edge.IsCut && edge.CategoryId == (int)BuiltInCategory.OST_BuildingPad
                    && terrainOutline > CutPen(edge)).ToList();
                if (padCuts.Count > 0)
                {
                    // The intersected ground adds its cut pen only on the soil side of the pad's contour.
                    page.BeginPolygonClip(groundPolygons, evenOdd: false);
                    foreach (var edge in padCuts)
                    {
                        double x1 = ToX(edge.X1), y1 = ToY(edge.Y1), x2 = ToX(edge.X2), y2 = ToY(edge.Y2);
                        double dx = x2 - x1, dy = y2 - y1;
                        double length = Math.Sqrt(dx * dx + dy * dy);
                        if (length == 0) continue;
                        double nx = -dy * terrainOutline / (2 * length), ny = dx * terrainOutline / (2 * length);
                        page.DrawPolyline([(x1 - nx, y1 - ny), (x2 - nx, y2 - ny),
                            (x2 + nx, y2 + ny), (x1 + nx, y1 + ny)], 0, closed: true, fillGray: 0);
                    }
                    page.EndClip();
                }
            }
            page.EndClip();
            if (caps.Count > 0) page.FillPolygonUnion(caps, 1);
        }
        if (content.SectionBillboards is { Count: > 0 } billboards)
        {
            page.BeginClip(ToX(clipX0), ToY(clipY0), (clipX1 - clipX0) / scale, (clipY1 - clipY0) / scale);
            page.FillPolygonUnion(billboards.Select(ToPage), 1);
            page.EndClip();
        }
        if (content.DetailCutFaces is { } cutFaces)
        {
            // Closed cuts cover projected surfaces before the cut patterns and linework are drawn.
            var caps = cutFaces.Select(face => ClipToWindow(face, clipX0, clipX1, clipY0, clipY1))
                .Where(face => face.Count >= 3).Select(ToPage).ToList();
            foreach (var cap in caps)
                if (PlanPrinter.SignedArea(cap) < 0) cap.Reverse();
            if (caps.Count > 0) page.FillPolygonUnion(caps, 1);
        }
        if (content.SiteOutlines is { Count: > 0 } siteEdges)
            foreach (var group in siteEdges.GroupBy(edge => edge.CategoryId))
            {
                double width = document != null
                    && PenWidths.TryGetWidth(document, group.Key, false, penScale, out double savedWidth)
                    ? savedWidth : 0.1;
                var inked = new HashSet<(long Ax, long Ay, long Bx, long By)>();
                foreach (var edge in group)
                {
                    if (ClipSegment(edge.X1, edge.Y1, edge.X2, edge.Y2, clipX0, clipY0, clipX1, clipY1,
                        out double x1, out double y1, out double x2, out double y2)
                        && inked.Add(ProjectedEdgeKey(x1, y1, x2, y2)))
                        page.DrawLine(ToX(x1), ToY(y1), ToX(x2), ToY(y2), width);
                }
            }
        List<IReadOnlyList<(double X, double Y)>> FaceRings(List<(double X, double Y)> polygon,
            IReadOnlyList<List<(double X, double Y)>> openings)
        {
            bool positive = PlanPrinter.SignedArea(polygon) > 0;
            var rings = new List<IReadOnlyList<(double X, double Y)>> { polygon };
            foreach (var hole in openings)
            {
                var clipped = clipRect.HasValue ? ClipToWindow(hole, clipX0, clipX1, clipY0, clipY1) : hole;
                if (clipped.Count < 3) continue;
                var ring = clipped.Select(point => (ToX(point.X), ToY(point.Y))).ToList();
                double area = PlanPrinter.SignedArea(ring);
                if (area == 0) continue;
                // Opposite winding keeps the opening clear in the face fill.
                if ((area > 0) == positive) ring.Reverse();
                rings.Add(ring);
            }
            return rings;
        }

        void DrawShadow(List<(double X, double Y)> face, List<(double X, double Y)> polygon, double fill)
        {
            if (content.CastShadows?.GetValueOrDefault(face) is not { } shadow) return;
            page.BeginPolygonClip([polygon, .. shadow.Openings.Select(ring =>
                ring.Select(point => (ToX(point.X), ToY(point.Y))).ToList())]);
            if (groundPolygons.Count > 0)
                ClipShadowAtGround(polygon.Min(p => p.X), polygon.Min(p => p.Y),
                    polygon.Max(p => p.X), polygon.Max(p => p.Y));
            page.FillPolygonUnion(shadow.Rings.Select(ring =>
                ring.Select(point => (ToX(point.X), ToY(point.Y))).ToList()), fill * shadow.Light);
            page.EndClip();
        }

        void ClipShadowAtGround(double left, double bottom, double right, double top)
        {
            // The terrain cut hides the projected shadows in its hatched area.
            List<(double X, double Y)> frame =
                [(ToX(clipX0), ToY(clipY0)), (ToX(clipX1), ToY(clipY0)),
                 (ToX(clipX1), ToY(clipY1)), (ToX(clipX0), ToY(clipY1))];
            foreach (var mask in groundPolygons)
                if (mask.Max(p => p.X) > left && mask.Min(p => p.X) < right
                    && mask.Max(p => p.Y) > bottom && mask.Min(p => p.Y) < top)
                    page.IntersectPolygonClip([frame, mask]);
        }

        if (groundTiles != null)
        {
            if (!isSection && sectionMasks == null) DrawGroundTiles(groundTiles, opaque: true);
        }
        else if (content.GroundHatch is { } ground && groundPolygons.Count > 0)
        {
            var strokes = FillHatch.Strokes(groundPolygons, ground.Grids, ToX(ground.AnchorX), ToY(ground.AnchorY))
                .Select(line => new CollectedEdge(line.X1, line.Y1, line.X2, line.Y2, 0, 0, -1, -1, 0, 0, 0, 0));
            // Covered portions must be absent from the PDF, including when compared as vectors.
            if (isSection)
                strokes = VisibleTerrainEdges(strokes,
                    content.Faces.Select(face => face.Points.Select(p => (ToX(p.X), ToY(p.Y))).ToList()),
                    [], 0.01 / (scale * scale));
            foreach (var line in strokes)
                page.DrawLine(line.X1, line.Y1, line.X2, line.Y2, groundPen);
        }
        void DrawGroundTiles(List<PdfLinePattern> tiles, bool opaque = false)
        {
            foreach (var polygon in groundPolygons)
                if (PlanPrinter.SignedArea(polygon) < 0) polygon.Reverse();
            if (opaque)
            {
                page.FillPolygonUnion(groundPolygons, 1);
                if (content.GroundHatch?.Shadow is { } soilShadow)
                {
                    page.BeginPolygonClip(groundPolygons, evenOdd: false);
                    page.FillPolygonUnion(soilShadow.Rings.Select(ToPage), soilShadow.Light);
                    page.EndClip();
                }
            }
            foreach (var tile in tiles) page.FillWithLinePattern(groundPolygons, tile);
        }
        var masks = content.DetailBreaks is { Count: > 0 } breaks
            ? breaks.Select(b => b.Mask).ToList() : null;
        if (masks != null)
            foreach (var polygon in masks)
            {
                var clipped = ClipToWindow(polygon, clipX0, clipX1, clipY0, clipY1);
                if (clipped.Count >= 3)
                    page.DrawPolyline(clipped.Select(p => (ToX(p.X), ToY(p.Y))).ToList(), 0, true, 1);
            }
        if (content.LayerHatches is { } hatches)
        {
            foreach (var group in hatches.GroupBy(p => (p.Grids, p.AnchorX, p.AnchorY, p.PenWidth)))
            {
                var polygons = group.Select(p => ClipToWindow(p.Polygon, clipX0, clipX1, clipY0, clipY1)
                    .Select(point => (ToX(point.X), ToY(point.Y))).ToList());
                double anchorX = ToX(group.Key.AnchorX), anchorY = ToY(group.Key.AnchorY);
                var grids = group.Key.Grids;
                double draftingPen = group.Key.PenWidth ?? 0.1;
                if ((!isSection || group.Key.PenWidth.HasValue) && masks == null
                    && FillHatch.DraftingTiles(grids, anchorX, anchorY, draftingPen) is { } tiles)
                {
                    var rings = polygons.Where(p => p.Count >= 3).ToList();
                    foreach (var ring in rings)
                        if (PlanPrinter.SignedArea(ring) < 0) ring.Reverse();
                    foreach (var tile in tiles) page.FillWithLinePattern(rings, tile);
                    foreach (var grid in grids.Where(g => g.Segments.Length == 0))
                        page.FillWithParallelLines(rings, Math.Abs(grid.Offset) * FeetToMillimetres, grid.Angle,
                            anchorX + grid.OriginX * FeetToMillimetres, anchorY + grid.OriginY * FeetToMillimetres, draftingPen);
                    continue;
                }
                var strokes = FillHatch.Strokes(polygons, grids, anchorX, anchorY, continuousLines: true)
                    .Select(s => new CollectedEdge(s.X1, s.Y1, s.X2, s.Y2, 0, 0, -1, -1, 0, 0, 0, 0));
                if (masks != null)
                    strokes = VisibleTerrainEdges(strokes, masks.Select(p => p.Select(point =>
                        (ToX(point.X), ToY(point.Y))).ToList()), [], 1e-9);
                foreach (var stroke in strokes)
                    page.DrawLine(stroke.X1, stroke.Y1, stroke.X2, stroke.Y2, group.Key.PenWidth ?? 0.18);
            }
        }
        HashSet<(long Ax, long Ay, long Bx, long By)>? doorProjections = content.KeepDoorDepths ? [] : null;
        var visibleEdges = Unmasked(content.Edges);
        if (isSection && clipRect.HasValue)
            visibleEdges = TrimProjectedEdgesAtCuts(visibleEdges.ToList(),
                edge => edge.ViewLineStyleId == 0 && CutPen(edge) >= projLine);
        foreach (var edge in visibleEdges)
        {
            double x1 = edge.X1, y1 = edge.Y1, x2 = edge.X2, y2 = edge.Y2;
            if ((clipRect.HasValue || edge.CategoryId == (int)BuiltInCategory.OST_Topography)
                && !ClipSegment(x1, y1, x2, y2, clipX0, clipY0, clipX1, clipY1,
                out x1, out y1, out x2, out y2)) continue;
            // Different physical door edges can leave the same visible span after clipping.
            if (doorProjections != null && edge.CategoryId == (int)BuiltInCategory.OST_Doors
                && !edge.IsCut && edge.ViewLineStyleId == 0
                && !doorProjections.Add(ProjectedEdgeKey(x1, y1, x2, y2))) continue;
            // A cut edge inks with its own category's cut pen (falling back to the Walls cut pen when the
            // category decodes none); anything else inks with the projection pen.
            double width = edge.IsCut ? CutPen(edge) : projLine;
            if (edge.ViewLineStyleId > 0)
            {
                if (document == null || document.RawData.GraphicsStylePens?.TryGetValue(edge.ViewLineStyleId, out var style) != true
                    || style.GStyleType != 1) continue;
                var color = new PdfColor((style.Color & 255) / 255d, ((style.Color >> 8) & 255) / 255d,
                    ((style.Color >> 16) & 255) / 255d);
                width = document.RawData.PenWidthTable is { } widths
                    && PenWidths.TryPenWidth(widths, style.PenNumber, penScale, out double patternPen) ? patternPen : projLine;
                if (LinePatterns.TryGetPatternDashes(document, style.LinePatternId, out var dashes))
                {
                    double dx = x2 - x1, dy = y2 - y1;
                    double length = Math.Sqrt(dx * dx + dy * dy);
                    double phase = 0;
                    if (edge.UnclippedViewLine is { } source)
                    {
                        dx = source.X2 - source.X1;
                        dy = source.Y2 - source.Y1;
                        length = Math.Sqrt(dx * dx + dy * dy);
                        double offset = ((x1 - source.X1) * dx + (y1 - source.Y1) * dy) / length;
                        phase = Math.Max(0, offset) / scale * 72 / 25.4;
                    }
                    page.SetDashPoints(LinePatterns.FitEndpointDashes(length / scale * 72 / 25.4, dashes), phase);
                }
                page.DrawLine(ToX(x1), ToY(y1), ToX(x2), ToY(y2), width, color);
                page.ResetDash();
                continue;
            }
            page.DrawLine(ToX(x1), ToY(y1), ToX(x2), ToY(y2), width);
        }
        if (content.Insulation != null)
        {
            double pen = 0.18;
            if (document != null && PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_InsulationLines,
                false, penScale, out double width))
                pen = width;
            foreach (var edge in Unmasked(content.Insulation))
                if (ClipSegment(edge.X1, edge.Y1, edge.X2, edge.Y2, clipX0, clipY0, clipX1, clipY1,
                    out double x1, out double y1, out double x2, out double y2))
                    page.DrawLine(ToX(x1), ToY(y1), ToX(x2), ToY(y2), pen);
        }
        if (content.DetailBreaks is { } detailBreaks)
            foreach (var detail in detailBreaks)
            for (int i = 1; i < detail.Line.Count; i++)
            {
                var a = detail.Line[i - 1];
                var b = detail.Line[i];
                if (ClipSegment(a.X, a.Y, b.X, b.Y, clipX0, clipY0, clipX1, clipY1,
                    out double x1, out double y1, out double x2, out double y2))
                    page.DrawLine(ToX(x1), ToY(y1), ToX(x2), ToY(y2), 0.35);
            }

        double CutPen(CollectedEdge edge) => document != null
            && PenWidths.TryGetWidth(document, edge.CategoryId, true, penScale, out double width) ? width : faceLine;

        IEnumerable<CollectedEdge> Unmasked(List<CollectedEdge> edges) =>
            masks == null ? edges : VisibleTerrainEdges(edges, masks, []);
    }

    private static void DrawFallbackDatums(PdfPage page, ElevationContent content, double scale, double originX,
        double originY, RvtDocument? document, (double X0, double Y0, double X1, double Y1)? clipRect)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        var (clipX0, clipY0, clipX1, clipY1) = clipRect ?? (content.MinX, content.MinY, content.MaxX, content.MaxY);
        // Grids first, then the levels: light over the faces the way an elevation prints them. The grids
        // dash coarsely from the grid type's line pattern ([36 9 9 9] pt on the sample, decoded below);
        // their bubbles print solid like the reference.
        if (content.Grids.Count > 0)
        {
            // Grids are datum elements: they ink with the annotation pen table (fixed weights), not the
            // scale-dependent model table вЂ” pen 1 annotates 0.1 mm (0.283 pt) at every scale on the reference.
            double gridLine = 0.1;
            if (document != null && PenWidths.TryGetPenNumber(document, (int)BuiltInCategory.OST_Grids, false, out int gridPen)
                && PenWidths.TryAnnotationWidth(document, gridPen, out double g))
                gridLine = g;
            double[]? dashes = document != null && LinePatterns.TryGetGridDashes(document, out var decoded)
                ? decoded
                // Measured on the reference export (the file's own "Grid Line" pattern decodes to the same
                // [36 9 9 9] pt): the grid type's center-segment pattern when its links did not decode.
                : [36.0, 9.0, 9.0, 9.0];
            double top = ToY(Math.Min(content.MaxY, clipY1)) - 8;
            double bottom = ToY(Math.Max(content.MinY, clipY0));
            foreach (var (x, name) in content.Grids)
            {
                if (content.NativeGrids?.ContainsKey(x) == true) continue;
                if (x < clipX0 || x > clipX1) continue;
                double pageX = ToX(x);
                // The dash pattern starts at the top of the line, as the reference grids do (a cut 36 pt
                // dash at the crop top where the line enters the viewport).
                if (dashes != null) page.SetDashPoints(dashes);
                page.DrawLine(pageX, top, pageX, bottom, gridLine, 0.55);
                if (dashes != null) page.ResetDash();
                var circle = new List<(double X, double Y)>(24);
                for (int i = 0; i < 24; i++)
                {
                    double angle = i * Math.PI * 2 / 24;
                    circle.Add((pageX + 3.5 * Math.Cos(angle), top + 3.5 + 3.5 * Math.Sin(angle)));
                }
                page.DrawPolyline(circle, 0.1, true, 1);
                if (name.Length > 0) page.DrawText(name, pageX, top + 2.4, 2.8, PdfTextAlignment.Center);
            }
        }
        if (content.Levels.Count > 0)
        {
            if (Environment.GetEnvironmentVariable("RVTIO_DEBUG_LEVELS") == "1")
                Console.WriteLine($"[levels] levels={content.Levels.Count} clip=[{clipX0:F0},{clipX1:F0}]x[{clipY0:F0},{clipY1:F0}] hasClip={clipRect.HasValue}");
            // A level line is thin (0.283 pt on the reference) and dashed short: 3 mm on, 1.5 mm off.
            // It follows its datum while it stays near the crop; only its height is always clipped to the crop.
            // The line stays near the crop: a datum far wider than a narrow crop (the stair at 1:50) would
            // otherwise run ten metres past the frame, while the reference ends its lines at the markers
            // just past the crop. The margin is the label strip the markers and labels take on the right.
            double marginContent = LabelMarginMillimetres * scale;
            double lastLabelY = double.NegativeInfinity;
            foreach (var (y, label, x0, x1) in content.Levels)
            {
                if (content.NativeLevels?.ContainsKey(y) == true) continue;
                // A level belongs to the crop, not to the collected model bounds: stairs-only content ends
                // above the Foundation while the crop reaches it, and the reference draws it.
                if (y < clipY0 - 1 || y > clipY1 + 1) continue;
                double pageY = ToY(y);
                // The line follows its datum envelope while it stays near the crop. A datum far wider than
                // a narrow crop (the stair at 1:50) is clipped to the crop plus a label-strip margin on each
                // side, the way the reference ends its lines at the markers just past the frame.
                double left = ToX(x0), right = ToX(x1);
                if (x1 - x0 > 2 * (clipX1 - clipX0))
                {
                    left = ToX(Math.Max(x0, clipX0 - marginContent));
                    right = ToX(Math.Min(x1, clipX1 + marginContent));
                }
                else
                {
                    left = ToX(Math.Min(x0, clipX0));
                }
                if (right <= left) continue;
                // The markers stand just past the crop frame (measured 10 mm on the sample), and each row
                // runs out to its marker instead of stopping at the datum end. Without a crop the marker
                // stays at the datum end as before.
                const double MarkerPastCropMillimetres = 10.0;
                double markerX = clipRect.HasValue
                    ? ToX(clipX1) + MarkerPastCropMillimetres
                    : right + 4;
                // Only the row itself dashes; the marker inks solid like the reference.
                page.SetDash(3.0, 1.5);
                page.DrawLine(left, pageY, markerX - 2.5, pageY, 0.1, 0.55);
                page.ResetDash();
                // The level marker at the right end of the line: a circle with an arrow head, the level name above its
                // height in millimetres, as an elevation notes them.
                var marker = new List<(double X, double Y)>(20);
                for (int i = 0; i < 20; i++)
                {
                    double angle = i * Math.PI * 2 / 20;
                    marker.Add((markerX + 2.5 * Math.Cos(angle), pageY + 2.5 * Math.Sin(angle)));
                }
                page.DrawPolyline(marker, 0.12, true, 1);
                page.DrawLine(markerX - 3.5, pageY, markerX, pageY, 0.15);
                page.DrawLine(markerX, pageY, markerX - 1.5, pageY + 1.1, 0.15);
                page.DrawLine(markerX, pageY, markerX - 1.5, pageY - 1.1, 0.15);
                // Two levels a few millimetres apart (Ceiling over Level 1) would print their labels on top of each
                // other; the label is pushed down instead.
                double labelY = pageY;
                if (labelY - lastLabelY < 6) labelY = lastLabelY + 6;
                if (label.Length > 0 && labelY - lastLabelY >= 6)
                {
                    page.DrawText(label, markerX + 4, labelY + 0.6, 2.2, PdfTextAlignment.Left, 0.4);
                    page.DrawText($"{y:0}", markerX + 4, labelY - 3.6, 2, PdfTextAlignment.Left, 0.4);
                    page.DrawLine(markerX + 2.5, pageY, markerX + 3.5, labelY + 0.4, 0.12, 0.4);
                    lastLabelY = labelY;
                }
            }
            page.ResetDash();
        }
    }

    internal static void DrawNativeDatums(PdfPage page, ElevationContent content, double scale, double originX,
        double originY, RvtDocument? document, (double X0, double Y0, double X1, double Y1)? crop)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        if (content.NativeGrids is { Count: > 0 } grids)
        {
            double[] pattern = document != null && LinePatterns.TryGetGridDashes(document, out var gridDashes)
                ? gridDashes.Select(p => p * 25.4 / 72).ToArray()
                : [];
            foreach (var (x, name) in content.Grids)
            {
                if (!grids.TryGetValue(x, out var g)) continue;
                if (crop is { } c && (x < c.X0 || x > c.X1 || g.Top < c.Y0 || g.Bottom > c.Y1)) continue;
                double pageX = ToX(x), top = ToY(g.Top), center = top + g.Radius;
                DrawDatumDashes(page, pageX, ToY(g.Bottom), pageX, top, pattern);
                page.DrawPolyline(DatumCircle(pageX, center, g.Radius, 25), 0.1, true);
                page.DrawText(name, pageX, center - 2.17, 18 * 25.4 / 72, PdfTextAlignment.Center);
            }
        }

        if (content.NativeLevels is not { Count: > 0 } lines) return;
        foreach (var (y, label, _, _) in content.Levels)
        {
            if (!lines.TryGetValue(y, out var line)) continue;
            if (crop is { } c && (y < c.Y0 - 1 || y > c.Y1 + 1 || line.HeadX < c.X0 || line.Left > c.X1)) continue;
            double left = ToX(line.Left), right = ToX(line.LineEnd), rowY = ToY(y);
            DrawDatumDashes(page, left, rowY, right, rowY, [3, 1.5]);

            double headLeft = ToX(line.HeadX), headY = ToY(line.HeadY);
            if (line.Elbow is { } e)
            {
                page.DrawLine(right, rowY, ToX(e.X), ToY(e.Y), 0.1);
                page.DrawLine(ToX(e.X), ToY(e.Y), headLeft, headY, 0.1);
            }
            double cx = headLeft + line.Radius;
            var outline = DatumCircle(cx, headY, line.Radius, 20);
            page.DrawPolyline(outline, 0.1, true);
            page.DrawPolyline([(cx, headY), .. outline.Skip(5).Take(6)], 0, true, 0);
            page.DrawPolyline([(cx, headY), .. outline.Skip(15), outline[0]], 0, true, 0);
            string elevation = $"{y:0}";
            if (DrawSavedLevelLabels(page, document, line.SymbolId, headLeft, headY, label, elevation)) continue;
            page.DrawText(label, headLeft - 4, headY + 1.85, 12 * 25.4 / 72, PdfTextAlignment.Right);
            page.DrawText(elevation, headLeft - 4, headY - 5.05, 12 * 25.4 / 72, PdfTextAlignment.Right);
        }
    }

    private static bool DrawSavedLevelLabels(PdfPage page, RvtDocument? document, int symbolId,
        double x, double y, string name, string elevation)
    {
        if (document?.RawData.TitleBlockLabels?.GetValueOrDefault(symbolId) is not [var first, var second] labels
            || first.ParamId == second.ParamId || page.Owner.Font is not { } font
            || labels.Any(label => (BuiltInParameter)label.ParamId is not (BuiltInParameter.DATUM_TEXT or BuiltInParameter.LEVEL_ELEV)
                || label is not { Bx: 1, By: 0, Hx: 0, Hy: 1 } || !(label.Height > 0)
                || !double.IsFinite(label.Height) || !double.IsFinite(label.Ox) || !double.IsFinite(label.Oy))) return false;

        foreach (var label in labels)
        {
            string value = label.Prefix + (label.ParamId == (int)BuiltInParameter.DATUM_TEXT ? name : elevation) + label.Suffix;
            double height = label.Height * FeetToMillimetres;
            double cap = document.RawData.TitleBlockCapHeightsFeet?.GetValueOrDefault(label.AttrId) ?? 0;
            double size = SheetComposer.SymbolLabelTextSize(font, height, cap);
            double width = label.Width * FeetToMillimetres;
            bool wrap = label.Wrap && width > 0 && page.MeasureText(value, size) > width;
            var lines = PlanPrinter.WrapPlanNoteText(page, value, size, width, wrap);
            bool opaque = document.RawData.OpaqueSymbolLabelStyles?.Contains(label.AttrId) == true;
            bool useArialLayout = document.RawData.RegularArialSymbolLabelStyles?.Contains(label.AttrId) == true
                && font.Name.Equals("Arial", StringComparison.OrdinalIgnoreCase);
            SheetComposer.DrawTitleBlockText(page, lines, x + label.Ox * FeetToMillimetres,
                y + label.Oy * FeetToMillimetres, size, height * lines.Count, label.Flags,
                label.Bx, label.By, label.Hx, label.Hy, opaque, useArialLayout);
        }
        return true;
    }

    internal static List<(double X, double Y)> DatumCircle(double x, double y, double radius, int count)
    {
        var circle = new List<(double X, double Y)>(count);
        for (int i = 0; i < count; i++)
        {
            double a = i * 2 * Math.PI / count;
            circle.Add((x + radius * Math.Cos(a), y + radius * Math.Sin(a)));
        }
        return circle;
    }

    internal static void DrawDatumDashes(PdfPage page, double x1, double y1, double x2, double y2,
        double[] pattern, double width = 0.1, bool roundCaps = false)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double length = Math.Sqrt(dx * dx + dy * dy), period = pattern.Sum();
        if (!(length > 0)) return;
        int cycles = period > 0 ? (int)Math.Floor(length / period) : 0;
        if (cycles <= 0)
        {
            Stroke(0, length);
            return;
        }
        // Revit balances the two end dashes around the full-length interior dashes.
        double endDash = (length - cycles * period + pattern[0]) / 2;
        Stroke(0, endDash);
        double cursor = endDash;
        for (int repeat = 0; repeat < cycles; repeat++)
        {
            for (int i = 1; i < pattern.Length; i++)
            {
                double next = cursor + pattern[i];
                if (i % 2 == 0) Stroke(cursor, next);
                cursor = next;
            }
            if (repeat + 1 < cycles)
            {
                Stroke(cursor, cursor + pattern[0]);
                cursor += pattern[0];
            }
        }
        Stroke(length - endDash, length);

        void Stroke(double from, double to)
        {
            var a = (X: x1 + from * dx / length, Y: y1 + from * dy / length);
            var b = (X: x1 + to * dx / length, Y: y1 + to * dy / length);
            if (roundCaps) page.DrawRoundDashedPolyline([a, b], width, []);
            else page.DrawLine(a.X, a.Y, b.X, b.Y, width);
        }
    }
}
