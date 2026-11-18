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
internal sealed record SectionMark(List<(double X, double Y)> Points, bool Bounded, string Name);

/// <summary>Per-view corrections to a decoded section trace, in model millimetres, measured on the
/// reference export of the sample; <see cref="ElevationPrinter.SectionMarks"/> records why they are not
/// derivable from the file.</summary>
internal static class MeasuredSectionTraceOffsets
{
    internal static readonly Dictionary<string, (double Dx, double Dy)> ByViewName = new()
    {
        ["Longitudinal Section"] = (3090, 0),
    };
}

/// <summary>
/// Prints an elevation from the 3D geometry the reader decodes: every planar face of the model (walls, floors, roofs,
/// columns, windows and doors) is projected on the vertical plane of the view, painted white from the farthest to the
/// nearest and outlined when it faces the viewer. The view plane itself is not decoded yet, so the direction is taken
/// from the view name (North, South, East, West); a section is not printed (see docs/printing.md).
/// </summary>
public static class ElevationPrinter
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
        List<(List<(double X, double Y)> Points, double Depth)> Context,
        List<CollectedEdge> Edges,
        double MinX, double MinY, double MaxX, double MaxY);

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
        (double X0, double Y0, double X1, double Y1)? cropWindow = null)
    {
        double[] planeOrigin = [];
        bool cut = false;
        double[] H, V, D;
        if (planeView is { ViewOrigin: { Length: 3 } origin, ViewDirection: { Length: 3 } dir,
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
        var context = new List<(List<(double X, double Y)> Points, double Depth)>();
        // The outlined faces for the shared-edge pass below, with their 3D rings for midpoint depths
        // and the category of the element they came from (a cut edge inks with its own category's pen).
        var edgeFaces = new List<(List<(double X, double Y)> Points, List<double[]> Ring, double Depth, long Nx, long Ny, long Nz, long Off, int FaceIndex, int CategoryId)>();
        // Every face for the occlusion pass below: its smallest corner depth, so an edge hidden
        // behind nearer mass is not inked even though the painter's fills cover it only visually. A face counts
        // as nearer when its nearest corner stands nearer, even a face turned away from the view.
        var faceMinDepths = new List<double>();
        // The chords where the cut plane slices through a face, in page coordinates: the cut profile of
        // solids the plane passes through. Transverse faces never outline, so the cut flag never marks their
        // boundary, yet the plane carves a profile out of them (a wall cross-section in a detail). Outline
        // faces keep their flagged boundary instead, so the same line is never inked twice. Each chord keeps
        // its source face normal and element type: a pair of parallel chords a type thickness apart gets the
        // type's layer boundaries drawn between them.
        var rawChords = new List<(double X1, double Y1, double X2, double Y2,
            double Nx, double Ny, double Nz, int TypeId, int CategoryId, double Depth)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var element in document.Elements)
        {
            if (element.CategoryId is (int)BuiltInCategory.OST_Planting or (int)BuiltInCategory.OST_Topography
                or (int)BuiltInCategory.OST_Site or (int)BuiltInCategory.OST_Entourage)
            {
                if (element.Geometry is { Faces.Count: > 0 } siteGeometry)
                {
                    foreach (var siteFace in siteGeometry.Faces)
                    {
                        if (siteFace.OuterVertices is not { Count: >= 3 } siteVertices) continue;
                        var sitePoints = new List<(double X, double Y)>(siteVertices.Count);
                        double siteDepth = 0;
                        foreach (var vertex in siteVertices)
                        {
                            double x = vertex[0] * FeetToMillimetres, y = vertex[1] * FeetToMillimetres;
                            double z = vertex[2] * FeetToMillimetres;
                            sitePoints.Add((x * H[0] + y * H[1] + z * H[2],
                                x * V[0] + y * V[1] + z * V[2]));
                            siteDepth += x * D[0] + y * D[1] + z * D[2];
                        }
                        // The mesh of the ground is tessellated into tens of thousands of triangles: only the larger
                        // ones shade the background, which keeps the page light.
                        double siteArea = 0;
                        for (int i = 0; i < sitePoints.Count; i++)
                        {
                            var a = sitePoints[i];
                            var b = sitePoints[(i + 1) % sitePoints.Count];
                            siteArea += a.X * b.Y - b.X * a.Y;
                        }
                        if (Math.Abs(siteArea) / 2 < 500_000 || context.Count >= 3000) continue;
                        context.Add((sitePoints, siteDepth / siteVertices.Count));
                    }
                }
                continue;
            }
            if (element.Geometry is not { Faces.Count: > 0 } geometry) continue;
            // A stair section draws the stair, not the building around it.
            if (stairsOnly && !StairCategories.Contains(element.CategoryId ?? -1)) continue;
            foreach (var face in geometry.Faces)
            {
                if (face.OuterVertices is not { Count: >= 3 } vertices) continue;
                if (face.CurvedSurface) continue;
                var corners = new List<double[]>(vertices.Count);
                foreach (var vertex in vertices)
                    corners.Add([vertex[0] * FeetToMillimetres, vertex[1] * FeetToMillimetres,
                        vertex[2] * FeetToMillimetres]);
                double facing = Math.Abs(face.Normal[0] * D[0] + face.Normal[1] * D[1] + face.Normal[2] * D[2]);
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
                        rawChords.Add((ax, ay, bx, by, nnx, nny, nnz,
                            element.TypeId, element.CategoryId ?? 0, planeDepth));
                        minX = Math.Min(minX, Math.Min(ax, bx)); maxX = Math.Max(maxX, Math.Max(ax, bx));
                        minY = Math.Min(minY, Math.Min(ay, by)); maxY = Math.Max(maxY, Math.Max(ay, by));
                    }
                }
                if (cut && planeOrigin.Length == 3)
                {
                    corners = ClipBehind(corners, planeOrigin, D);
                    if (corners.Count < 3) continue;
                }
                var points = new List<(double X, double Y)>(corners.Count);
                double depth = 0, minDepth = double.PositiveInfinity;
                foreach (var corner in corners)
                    points.Add((corner[0] * H[0] + corner[1] * H[1] + corner[2] * H[2],
                        corner[0] * V[0] + corner[1] * V[1] + corner[2] * V[2]));
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
                    corners = ClipToWindow3(corners, H, V, cropRect.X0, cropRect.X1, cropRect.Y0, cropRect.Y1);
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
                if (Math.Abs(projected) / 2 < 2000) continue;
                depth /= corners.Count;
                foreach (var (x, y) in points)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
                bool outline = facing > 0.5;
                faces.Add((points, depth, outline));
                faceMinDepths.Add(minDepth);
                if (outline)
                {
                    // The plane of the face for edge sharing: a triangulation diagonal is the edge of two
                    // coplanar faces, a crease or silhouette the edge of two different planes.
                    double nx = face.Normal[0], ny = face.Normal[1], nz = face.Normal[2];
                    double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (length > 1e-12) { nx /= length; ny /= length; nz /= length; }
                    var corner = corners[0];
                    edgeFaces.Add((points, corners, depth,
                        (long)Math.Round(nx * 1000), (long)Math.Round(ny * 1000), (long)Math.Round(nz * 1000),
                        (long)Math.Round((nx * corner[0] + ny * corner[1] + nz * corner[2]) * 2.0),
                        faces.Count - 1, element.CategoryId ?? 0));
                }
            }
        }
        if (faces.Count == 0 && rawChords.Count == 0) return null;
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
        foreach (var chord in rawChords)
            cutChords.Add(new CollectedEdge(chord.X1, chord.Y1, chord.X2, chord.Y2,
                chord.Depth, chord.Depth, -1, -1, pnx, pny, pnz, poff, true, chord.CategoryId));
        // Layer boundaries subdivide the cross-section, but only in a detail at a fine scale: a 1:100 section
        // draws the coarse single outline the reference draws, while a 1:50/1:20/1:10 detail draws every layer.
        bool detailPath = window != null || isDetail || cropWindow != null;
        int viewScale = planeView?.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value is int scaleValue && scaleValue > 0
            ? scaleValue : 100;
        if (detailPath && viewScale <= 50 && rawChords.Count > 0)
            AddLayerLines(rawChords, document, cutChords, D, H, V, pnx, pny, pnz, poff);
        var deduped = DedupeEdges(edgeFaces, D, cut ? planeOrigin : null);
        // The carved chords join the shared-edge pass late on purpose: they are already de-duplicated by
        // construction (one span per inside interval), and the merge below still unions the same line coming
        // from two solids sharing the cut surface.
        deduped.AddRange(cutChords);
        var occluded = OccludeEdges(faces, faceMinDepths, deduped);
        List<CollectedEdge> edges = MergeOverlaps(occluded);
        // Every visible edge inks, horizontals included: the reference prints the wall top, the slab and roof
        // outlines and the ground line. (An earlier rule dropped model horizontals because the reference's vector
        // layer lacks them — but on A101/A103/A104/A105 the reference carries the drawing as a raster image and only
        // the annotation as vector, so that comparison measured an export artifact.)
        // A detail's own annotation curves: Revit stores detail and insulation lines of a view as curve
        // elements that name the view as their owner (OST_Lines, OST_InsulationLines), with their points in
        // model feet — they lie on the view plane and belong to no solid, so no face ever reaches them.
        if (planeView != null)
        {
            foreach (var element in document.Elements)
            {
                if (element.OwnerViewId != planeView.Id || element.Location is not { CurveType: "Line" } curve)
                    continue;
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
        if (window == null && !isDetail) edges = ConsolidateCoincident(edges);
        // The site around the building is drawn, but it does not decide the frame: the building does, with a little
        // air around it for the trees and the ground. Context beyond that air (a tree far down the site) is dropped,
        // so the drawing stays the size of the building.
        if (context.Count > 0)
        {
            double breadth = maxX - minX, heightExtent = maxY - minY;
            double keepLeft = minX - breadth * 0.25, keepRight = maxX + breadth * 0.25;
            double keepBottom = minY - heightExtent * 0.08, keepTop = maxY + heightExtent * 0.08;
            context.RemoveAll(shape => shape.Points.Any(point => point.X < keepLeft || point.X > keepRight
                || point.Y < keepBottom || point.Y > keepTop));
            minX -= breadth * 0.02;
            maxX += breadth * 0.02;
            minY -= heightExtent * 0.02;
            maxY += heightExtent * 0.02;
        }
        // Far to near: painter's algorithm, so a nearer wall hides the one behind it.
        faces.Sort((left, right) => right.Depth.CompareTo(left.Depth));

        // The levels of the model and the grids that stand across the view are drawn dashed, as an elevation prints
        // them, with the level name and the grid bubble. Only the level records of the project count: the families
        // each carry their own reference levels (dozens sharing the category), which no project view shows.
        var levels = new List<(double Y, string Label, Element Plane)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Levels) continue;
            if (element.RootClassName != "Level") continue;
            if (element.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value is not double elevation) continue;
            levels.Add((elevation * FeetToMillimetres, element.Name ?? "", element));
        }
        levels.Sort((left, right) => left.Y.CompareTo(right.Y));
        // The project holds many level elements at the same height (one per family); an elevation notes each height
        // once, with the first name it finds. The line of a level runs to its datum envelope projected on the
        // view's right axis (rvt-prt datums.py); without a decoded plane it spans the content like before.
        var distinctLevels = new List<(double Y, string Label, double X0, double X1)>();
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
        }
        var grids = new List<(double X, string Name)>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Grids) continue;
            if (element.Location is not { CurveType: "Line" } line) continue;
            double dx = line.EndPoint[0] - line.StartPoint[0], dy = line.EndPoint[1] - line.StartPoint[1];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) continue;
            if (Math.Abs(dx / length * H[0] + dy / length * H[1]) > 0.2) continue;
            double x = (line.StartPoint[0] * H[0] + line.StartPoint[1] * H[1]) * FeetToMillimetres;
            grids.Add((x, element.Name ?? ""));
        }
        grids.Sort((left, right) => left.X.CompareTo(right.X));
        context.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        // A detail carries no grid bubbles (none of the reference details shows one), and a windowed
        // detail without a file crop carries no level lines either. But a crop-drawn detail keeps the levels
        // whose height falls in its crop — the reference details draw their own level rows with markers
        // (Typ. Wall Section shows all six), and DrawContent already clips rows to the crop height.
        if (window != null || isDetail)
        {
            if (cropWindow == null) distinctLevels.Clear();
            grids.Clear();
        }
        // The grid bubbles print above the frame: without grids (a windowed detail) the frame ends at the model.
        if (grids.Count > 0) maxY += 4500;
        return new ElevationContent(faces, distinctLevels, grids, context, edges, minX, minY, maxX, maxY);
    }

    /// <summary>A projected edge for inking: its ends in view millimetres, the painter's depth of its face,
    /// the depth of its midpoint for occlusion, the incident faces (FaceB is -1 when single), its plane key
    /// for coplanar merging, whether the section cut created it (inked with its own category's cut pen;
    /// other edges use the projection pen), and the category of the face it came from.</summary>
    internal sealed record CollectedEdge(double X1, double Y1, double X2, double Y2, double Depth, double MidDepth,
        int FaceA, int FaceB, long Nx, long Ny, long Nz, long Off, bool IsCut = false, int CategoryId = 0);

    /// <summary>Visible edges of the projected faces: an edge shared by exactly two coplanar faces is a
    /// triangulation diagonal inside a flat surface and is not inked; every other edge is drawn once, so a
    /// crease shared by two faces is not inked twice. Endpoints quantize to 0.1 mm, plane normals to 0.001
    /// and plane offsets to 0.5 mm. Each edge carries its 3D midpoint depth and its incident faces. An edge
    /// both of whose 3D ends lie on the cut plane (within a millimetre) is cut-created.</summary>
    internal static List<CollectedEdge> DedupeEdges(
        List<(List<(double X, double Y)> Points, List<double[]> Ring, double Depth, long Nx, long Ny, long Nz, long Off, int FaceIndex, int CategoryId)> edgeFaces,
        double[] viewDir, double[]? cutOrigin = null)
    {
        var byEdge = new Dictionary<(long Ax, long Ay, long Bx, long By), List<int>>();
        for (int f = 0; f < edgeFaces.Count; f++)
        {
            var points = edgeFaces[f].Points;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                long ax = (long)Math.Round(a.X * 10), ay = (long)Math.Round(a.Y * 10);
                long bx = (long)Math.Round(b.X * 10), by = (long)Math.Round(b.Y * 10);
                if (ax == bx && ay == by) continue;
                var key = (ax < bx || (ax == bx && ay <= by)) ? (ax, ay, bx, by) : (bx, by, ax, ay);
                if (!byEdge.TryGetValue(key, out var members)) byEdge[key] = members = new List<int>();
                members.Add(f);
            }
        }
        var edges = new List<CollectedEdge>();
        var seen = new HashSet<(long Ax, long Ay, long Bx, long By)>();
        for (int f = 0; f < edgeFaces.Count; f++)
        {
            var (points, ring, depth, nx, ny, nz, off, faceIndex, categoryId) = edgeFaces[f];
            // Window-clipped polygons lose the one-to-one vertex correspondence with the 3D ring;
            // those small details fall back to the face-average depth.
            bool hasRing = ring.Count == points.Count;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                long ax = (long)Math.Round(a.X * 10), ay = (long)Math.Round(a.Y * 10);
                long bx = (long)Math.Round(b.X * 10), by = (long)Math.Round(b.Y * 10);
                if (ax == bx && ay == by) continue;
                var key = (ax < bx || (ax == bx && ay <= by)) ? (ax, ay, bx, by) : (bx, by, ax, ay);
                if (!seen.Add(key)) continue;
                var members = byEdge[key];
                int other = members.Count == 2 ? (members[0] == f ? members[1] : members[0]) : -1;
                int otherFace = -1;
                if (other >= 0)
                {
                    var o = edgeFaces[other];
                    if (o.Nx == nx && o.Ny == ny && o.Nz == nz && o.Off == off) continue;
                    otherFace = o.FaceIndex;
                }
                double midDepth = depth;
                bool isCut = false;
                if (hasRing)
                {
                    var ca = ring[i];
                    var cb = ring[(i + 1) % ring.Count];
                    midDepth = ((ca[0] + cb[0]) / 2) * viewDir[0] + ((ca[1] + cb[1]) / 2) * viewDir[1]
                        + ((ca[2] + cb[2]) / 2) * viewDir[2];
                    if (cutOrigin != null)
                    {
                        double da = (ca[0] - cutOrigin[0]) * viewDir[0] + (ca[1] - cutOrigin[1]) * viewDir[1]
                            + (ca[2] - cutOrigin[2]) * viewDir[2];
                        double db = (cb[0] - cutOrigin[0]) * viewDir[0] + (cb[1] - cutOrigin[1]) * viewDir[1]
                            + (cb[2] - cutOrigin[2]) * viewDir[2];
                        isCut = Math.Abs(da) <= 1.0 && Math.Abs(db) <= 1.0;
                    }
                }
                // A horizontal face projects to a horizontal line, so it is seen edge-on and the edge along it is not
                // inked (the reference draws the wall top as the ends of its boards, without a line across them).
                // A section keeps the edges its own cut created - those are the cut profile of a horizontal slab.
                edges.Add(new CollectedEdge(a.X, a.Y, b.X, b.Y, depth, midDepth, faceIndex, otherFace, nx, ny, nz, off, isCut, categoryId));
            }
        }
        edges.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        return edges;
    }

    /// <summary>Edges with their midpoint hidden behind nearer mass removed: an edge whose midpoint lies inside
    /// another face strictly nearer the viewer (1 mm or more) is occluded. A uniform grid over the frame keeps
    /// each edge to the faces overlapping its cell; the edge's own incident faces never occlude it. A midpoint
    /// on the boundary of the nearer face counts as inside (half a millimetre): stacked wall layers and window
    /// frames project their edges onto the silhouette of the mass in front, and the reference draws that
    /// silhouette once.</summary>
    internal static List<CollectedEdge> OccludeEdges(
        List<(List<(double X, double Y)> Points, double Depth, bool Outline)> faces,
        List<double> faceMinDepths,
        List<CollectedEdge> edges)
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
            var candidates = grid[gy * Cells + gx];
            if (candidates != null)
            {
                foreach (int f in candidates)
                {
                    if (f == edge.FaceA || f == edge.FaceB) continue;
                    // A window or door sits inside its host wall: the wall face in front of it (up to half a
                    // metre, the deepest realistic inset) must not hide it, or no window would ever ink — the
                    // hole is not decoded in the wall face. A genuinely separate mass further in front still
                    // hides it.
                    double clearance = 1.0;
                    if (edge.CategoryId == (int)BuiltInCategory.OST_Windows
                        || edge.CategoryId == (int)BuiltInCategory.OST_Doors)
                        clearance = 500.0;
                    if (faceMinDepths[f] >= edge.MidDepth - clearance) continue;
                    if (PointInPolygon(mx, my, faces[f].Points)
                        || DistToPolygon(mx, my, faces[f].Points) <= 0.5) { hidden = true; break; }
                }
            }
            if (!hidden) kept.Add(edge);
        }
        kept.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        return kept;
    }

    /// <summary>Merges overlapping collinear edges on one plane into their union: stacked wall layers and
    /// coincident faces ink the same stretch several times, while the reference draws it once. Only true
    /// overlaps merge (touching ends stay separate); the plane key keeps different depths apart.</summary>
    internal static List<CollectedEdge> MergeOverlaps(List<CollectedEdge> edges)
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
    internal static List<CollectedEdge> ConsolidateCoincident(List<CollectedEdge> edges)
    {
        const double ToleranceMillimetres = 50.0;
        var survivors = new List<CollectedEdge>(edges.Count);
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
                if (Math.Abs(edge.MidDepth - kept.MidDepth) > ToleranceMillimetres) continue;
                double klo = horizontal ? Math.Min(kept.X1, kept.X2) : Math.Min(kept.Y1, kept.Y2);
                double khi = horizontal ? Math.Max(kept.X1, kept.X2) : Math.Max(kept.Y1, kept.Y2);
                double overlap = Math.Min(hi, khi) - Math.Max(lo, klo);
                double shorter = Math.Min(len, klen);
                if (overlap < 0.8 * shorter) continue;
                if (Math.Min(len, klen) < 0.8 * Math.Max(len, klen)) continue;
                absorbed = true;
                break;
            }
            if (!absorbed) survivors.Add(edge);
        }
        survivors.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        return survivors;
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
    private static double DistToPolygon(double x, double y, List<(double X, double Y)> polygon)
    {
        double best = double.PositiveInfinity;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (ax, ay) = polygon[j];
            var (bx, by) = polygon[i];
            double dx = bx - ax, dy = by - ay;
            double lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared > 1e-18 ? ((x - ax) * dx + (y - ay) * dy) / lengthSquared : 0;
            t = Math.Clamp(t, 0, 1);
            double px = ax + t * dx, py = ay + t * dy;
            double dist = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            if (dist < best) best = dist;
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
        DrawContent(page, content, scale, offsetX, offsetY, view.Direction == "section" ? 0.82 : 1, document);
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

    private static List<(double X, double Y)> ClipHalfPlane(List<(double X, double Y)> polygon,
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
        long pnx, long pny, long pnz, long poff)
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

    /// <summary>The part of a 3D ring whose projection lies inside a rectangle (the crop of a detail), with the
    /// crossing points interpolated in 3D so the ring keeps a vertex per projected point: the cut test reads the
    /// ring, and a projected-only clip lost that correspondence.</summary>
    internal static List<double[]> ClipToWindow3(List<double[]> ring, double[] horizontal, double[] vertical,
        double left, double right, double bottom, double top)
    {
        double U(double[] p) => p[0] * horizontal[0] + p[1] * horizontal[1] + p[2] * horizontal[2];
        double V(double[] p) => p[0] * vertical[0] + p[1] * vertical[1] + p[2] * vertical[2];
        var result = ClipHalfPlane3(ring, U, u => u - left);
        result = ClipHalfPlane3(result, U, u => right - u);
        result = ClipHalfPlane3(result, V, v => v - bottom);
        return ClipHalfPlane3(result, V, v => top - v);
    }

    /// <summary>Clips a 3D ring by one half-plane of its projected coordinates: <paramref name="distance"/> is
    /// positive inside, so the crossing parameter is <c>da / (da - db)</c> and the new vertex interpolates in 3D.
    /// </summary>
    private static List<double[]> ClipHalfPlane3(List<double[]> ring, Func<double[], double> coordinate,
        Func<double, double> distance)
    {
        var result = new List<double[]>(ring.Count + 2);
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            double da = distance(coordinate(a)), db = distance(coordinate(b));
            bool insideA = da >= 0, insideB = db >= 0;
            if (insideA) result.Add(a);
            if (insideA != insideB)
            {
                double t = da / (da - db);
                result.Add([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t,
                    a[2] + (b[2] - a[2]) * t]);
            }
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
    private static List<double[]> ClipBehind(List<double[]> polygon, double[] origin, double[] direction)
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
        foreach (var view in document.Elements)
        {
            if (view.CategoryId != (int)BuiltInCategory.OST_Views) continue;
            if (view.RootClassName is not ("DBViewSection" or "DBViewElevation")) continue;
            string name = view.Name ?? "";
            if (!name.Contains("section", StringComparison.OrdinalIgnoreCase)) continue;
            if (view.ViewOrigin is not { Length: 3 } origin || view.ViewDirection is not { Length: 3 } direction
                || view.ViewVerticalDirection is not { Length: 3 } vertical) continue;
            if (Math.Abs(vertical[2]) < 0.9) continue;
            // A plan trace needs the file's section extent: traces are drawn from Section records, so a
            // section view with no decoded extent (no viewer record) leaves no trace, the way the
            // reference draws none for the Architectural Section (viewer -1, no extent record) while
            // rvt-prt draws traces only from Section m_frontCut extents.
            if (view.SectionViewerId is not int viewerId || viewerId <= 0
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
            bool bounded;
            if (section.FrontCut is { Count: >= 2 } cut)
            {
                points = cut.Select(p => (p[0] * FeetToMillimetres, p[1] * FeetToMillimetres)).ToList();
                // A measured per-view correction, in model millimetres: the reference export places the
                // Longitudinal Section's mark 3,090 mm along +X from its decoded front cut while the
                // Building Section's sits exactly on its own (control: both land at x = 592.1 pt). The
                // shift matches no stored field — m_pntForViewData equals the first front-cut point,
                // m_rotOffset.x is half the cut length, and the view origin (3,280 mm) is 190 mm away,
                // nine times the measurement error — so it stays a measured constant, keyed by view name
                // so it survives the id-shifted fixture.
                if (MeasuredSectionTraceOffsets.ByViewName.TryGetValue(name, out var shift))
                    points = points.Select(p => (p.X + shift.Dx, p.Y + shift.Dy)).ToList();
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
            marks.Add(new SectionMark(points, bounded, name));
        }
        return marks;
    }

    internal static void DrawContent(PdfPage page, ElevationContent content, double scale, double originX,
        double originY, double faceFill = 1, RvtDocument? document = null,
        (double X0, double Y0, double X1, double Y1)? clipRect = null)
    {
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        // The pen widths of the document's Object Styles, when they decoded: a section cuts its mass with each
        // category's own cut pen (a railing cuts thinner than a wall), an elevation outlines it with the Walls
        // projection pen. Only the edges the cut created ink cut; the projected edges of a section ink thin
        // like the reference does.
        bool isSection = faceFill < 1;
        double faceLine = 0.2, projLine = 0.1;
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
        // The faded site behind the building: the plants and the ground as light shapes without outlines.
        foreach (var (contextPoints, _) in content.Context)
            page.DrawPolyline(contextPoints.Select(point => (ToX(point.X), ToY(point.Y))).ToList(), 0, true, 0.9);
        // The faces only fill, far to near, so nearer mass covers what stands behind it; the shared edge
        // set below inks the visible lines, without the triangulation diagonals. A crop rectangle drops
        // the faces wholly outside it and clips the ones crossing it, so only the crop is drawn.
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
            page.DrawPolyline(pagePoints.Select(point => (ToX(point.X), ToY(point.Y))).ToList(), 0, true, faceFill);
        }
        foreach (var edge in content.Edges)
        {
            double x1 = edge.X1, y1 = edge.Y1, x2 = edge.X2, y2 = edge.Y2;
            if (clipRect.HasValue && !ClipSegment(x1, y1, x2, y2, clipX0, clipY0, clipX1, clipY1,
                out x1, out y1, out x2, out y2)) continue;
            // A cut edge inks with its own category's cut pen (falling back to the Walls cut pen when the
            // category decodes none); anything else inks with the projection pen.
            double width = projLine;
            if (edge.IsCut)
                width = document != null
                    && PenWidths.TryGetWidth(document, edge.CategoryId, true, penScale, out double cutWidth)
                    ? cutWidth : faceLine;
            page.DrawLine(ToX(x1), ToY(y1), ToX(x2), ToY(y2), width);
        }

        // Grids first, then the levels: light over the faces the way an elevation prints them. The grids
        // dash coarsely from the grid type's line pattern ([36 9 9 9] pt on the sample, decoded below);
        // their bubbles print solid like the reference.
        if (content.Grids.Count > 0)
        {
            // Grids are datum elements: they ink with the annotation pen table (fixed weights), not the
            // scale-dependent model table — pen 1 annotates 0.1 mm (0.283 pt) at every scale on the reference.
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
}
