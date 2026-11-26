using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

internal static class ShadedViewport
{
    private readonly record struct Point(double X, double Y, double Depth);
    private sealed record Surface(Point[][] Rings, double DepthX, double DepthY, int Rgb, bool FacesLight, double Transmission,
        ViewportSurfacePattern? Pattern, ViewportSurfaceEdges? Edges);
    private readonly record struct Pane(Surface Surface, int Order, int FirstRow, int LastRow);
    private readonly record struct PaneSpan(Pane Pane, int Start, int End);
    private readonly record struct PaneHit(Pane Pane, double Depth, double PatternInk, double EdgeInk);

    internal static (double X, double Y, double Width, double Height)? Render(PdfPage page, RvtDocument document,
        Element view, double anchorX, double anchorY, int scale)
    {
        if (view.ViewCrop is not { } camera || view.OrthographicModelBounds is not { } box
            || scale <= 0 || !double.IsFinite(anchorX) || !double.IsFinite(anchorY)) return null;
        double factor = 304.8 / scale;
        (double X, double Y) ProjectVector(double[] point) =>
            (factor * Dot(point[0], point[1], point[2], camera.Right), factor * Dot(point[0], point[1], point[2], camera.Up));
        Point Project(double[] point)
        {
            double x = point[0] - camera.OriginX, y = point[1] - camera.OriginY, z = point[2] - camera.OriginZ;
            return new(anchorX + factor * Dot(x, y, z, camera.Right),
                anchorY + factor * Dot(x, y, z, camera.Up), Dot(x, y, z, camera.Look));
        }
        var solar = SolarStudyOverlay.Create(document, view, camera, factor, point =>
        {
            var p = Project(point);
            return (p.X, p.Y);
        });
        var shadows = view.SolarStudy?.ShadowIntensity is > 0 and <= 100 and int intensity
            && solar?.ModelSunDirection is { Z: > 0 } sun
            ? new ViewportShadows(camera, sun, intensity, solar.GroundElevation) : null;
        var annotation = solar is { AnnotationVisible: true } ? solar : null;
        bool showPatterns = view.SolarStudy?.ShowSurfacePatterns == true;
        bool showEdges = view.SolarStudy?.ShowEdges == true;
        var patternOrigin = Project([0, 0, 0]);
        var pens = new Dictionary<int, (double Width, int Color)>();
        (double Width, int Color) Pen(int category)
        {
            if (pens.TryGetValue(category, out var value)) return value;
            double width = PenWidths.TryGetWidth(document, category, false, scale, out double savedWidth) ? savedWidth : 0.1;
            value = (width, PenWidths.TryGetStyle(document, category, false, out _, out int color)
                && color is >= 0 and <= 0xffffff ? color : 0);
            pens.Add(category, value);
            return value;
        }

        var planes = Enumerable.Range(0, 6).Where(i => box.Active[i]).Select(i =>
        {
            double[] axis = box.Axes[i / 2];
            double sign = i % 2 == 0 ? 1 : -1;
            double[] outward = [axis[0] * sign, axis[1] * sign, axis[2] * sign];
            double offset = sign * box.Offsets[i] + Dot(box.Origin[0], box.Origin[1], box.Origin[2], outward);
            return (Index: i, Outward: outward, Offset: offset);
        }).ToArray();
        bool InsideModelBox(double x, double y, double z)
        {
            foreach (var (_, outward, offset) in planes)
                if (Dot(x, y, z, outward) > offset + 1e-7) return false;
            return true;
        }

        double Distance(double[] point, double[] outward, double offset) =>
            offset - Dot(point[0], point[1], point[2], outward);

        List<(double[] Point, bool IncomingEdge)> Clip(List<double[]> ring)
        {
            var clipped = ring.Select(point => (Point: point, IncomingEdge: true)).ToList();
            foreach (var (_, outward, offset) in planes)
            {
                if (clipped.Count < 3) break;
                clipped = ElevationPrinter.ClipHalfPlane3(clipped, point => Distance(point, outward, offset));
            }
            return clipped;
        }

        var materials = new Dictionary<int, (int Color, bool TransmitsLight, double Transmission)>();
        (int Color, bool TransmitsLight, double Transmission) Material(int id)
        {
            if (materials.TryGetValue(id, out var material)) return material;
            var element = document.GetElement(id);
            int rgb = element?.LookupParameter(BuiltInParameter.MATERIAL_PARAM_COLOR)?.Value
                is int value and >= 0 and <= 0xffffff ? value : 0xc0c0c0;
            // Reserve pure white for the image's transparent background.
            if (rgb == 0xffffff) rgb = 0xfefefe;
            double transparency = element?.SerializedMaterialTransparency is > 0 and <= 1 and double exact
                ? exact : element?.LookupParameter(BuiltInParameter.MATERIAL_PARAM_TRANSPARENCY)?.Value
                    is int percent and > 0 and <= 100 ? percent / 100d : 0;
            // Appearance assets can override the stored graphics transparency.
            material = (rgb, element?.SerializedMaterialTransparency > 0,
                element?.SerializedMaterialUsesRenderAppearance == true ? 0 : transparency);
            materials.Add(id, material);
            return material;
        }

        var surfaces = new List<Surface>();
        var hidden = document.RawData.ViewHiddenElements?.GetValueOrDefault(view.Id);
        var hiddenCategories = document.RawData.ViewHiddenCategories?.GetValueOrDefault(view.Id);
        double left = double.PositiveInfinity, right = double.NegativeInfinity;
        double bottom = double.PositiveInfinity, top = double.NegativeInfinity;
        void AddSurface(Point[][] rings, double[] normal, double facing, int rgb, double transmission = 0,
            ViewportSurfacePattern? pattern = null, ViewportSurfaceEdges? edges = null)
        {
            var outer = rings[0];
            if (transmission >= 1 || outer.Length < 3 || outer.Any(point => !double.IsFinite(point.X)
                || !double.IsFinite(point.Y) || !double.IsFinite(point.Depth))) return;
            left = Math.Min(left, outer.Min(p => p.X));
            right = Math.Max(right, outer.Max(p => p.X));
            bottom = Math.Min(bottom, outer.Min(p => p.Y));
            top = Math.Max(top, outer.Max(p => p.Y));
            surfaces.Add(new(rings, -Dot(normal[0], normal[1], normal[2], camera.Right) / (factor * facing),
                -Dot(normal[0], normal[1], normal[2], camera.Up) / (factor * facing), rgb,
                shadows?.FacesLight(normal) == true, transmission, pattern, edges));
        }

        double terrainBase = box.Origin[2] + box.Offsets[5];
        int cutMaterial = document.RawData.SiteCutGraphics?.MaterialId ?? -1;
        var terrainSides = box.Active[5] && box.Axes[2][2] > 1 - 1e-8 && cutMaterial > 0
            ? planes.Where(plane => plane.Index < 4 && Math.Abs(plane.Outward[2]) < 1e-8
                && Dot(plane.Outward[0], plane.Outward[1], plane.Outward[2], camera.Look) < -1e-8).ToArray()
            : [];
        foreach (var element in document.Elements)
        {
            if (element.Geometry is not { Faces.Count: > 0 } geometry || element.IsDiscovered
                || element.OwnerViewId is > 0 || element.SerializedUnplacedOwnerId is > 0 || element.NativeIsSymbol == true
                || element.RootClassIs("FamilySymbol")
                || element.CategoryId is not int category || category >= 0 || hidden?.Contains(element.Id) == true
                || hiddenCategories?.Contains(category) == true
                || element.CategoryId is (int)BuiltInCategory.OST_Constraints or (int)BuiltInCategory.OST_CLines
                    or (int)BuiltInCategory.OST_Cameras or (int)BuiltInCategory.OST_LightingFixtureSource) continue;
            foreach (var face in geometry.Faces)
            {
                if (face.OuterVertices is not { Count: >= 3 } ring) continue;
                var normal = face.Normal;
                double facing = Dot(normal[0], normal[1], normal[2], camera.Look);
                bool visible = facing < -1e-8, facesLight = shadows?.FacesLight(normal) == true;
                if (!visible && !facesLight) continue;
                var clipped = Clip(ring);
                if (clipped.Count < 3) continue;
                var rings = new List<double[][]> { clipped.Select(vertex => vertex.Point).ToArray() };
                if (face.InnerVertices != null)
                    rings.AddRange(face.InnerVertices.Select(Clip).Where(hole => hole.Count >= 3)
                        .Select(hole => hole.Select(vertex => vertex.Point).ToArray()));
                var material = Material(face.RenderStyleId ?? face.NestedRenderStyleId ?? 0);
                // shortcut: opaque casters, until the renderer can accumulate transmission through separate panes.
                if (facesLight && !material.TransmitsLight) shadows!.AddCaster(rings, normal);
                if (!visible) continue;
                ViewportSurfacePattern? pattern = null;
                if (showPatterns && face.ModelPattern != null)
                {
                    int pen = category == (int)BuiltInCategory.OST_Roofs ? (int)BuiltInCategory.OST_RoofsSurfacePattern : category;
                    pattern = ViewportSurfacePattern.Create(document, face, ProjectVector, patternOrigin.X, patternOrigin.Y,
                        Pen(pen).Width);
                }
                // shortcut: saved planar boundary loops, until non-planar source edges are projected.
                ViewportSurfaceEdges? edges = showEdges && face.PlaneSurface && face.SourceLoops is { Count: > 0 } loops
                    ? SourceEdges(loops, category) : null;
                AddSurface(rings.Select(loop => loop.Select(Project).ToArray()).ToArray(), normal, facing,
                    material.Color, material.Transmission, pattern, edges);
                if (terrainSides.Length == 0 || element.CategoryId != (int)BuiltInCategory.OST_Topography
                    || normal[2] <= 0) continue;
                int soil = Material(cutMaterial).Color;
                for (int i = 0; i < clipped.Count; i++)
                {
                    var a = clipped[i].Point;
                    var b = clipped[(i + 1) % clipped.Count].Point;
                    foreach (var (_, outward, offset) in terrainSides)
                    {
                        if (Math.Abs(Distance(a, outward, offset)) >= 1e-7
                            || Math.Abs(Distance(b, outward, offset)) >= 1e-7) continue;
                        AddSurface([[Project(a), Project(b), Project([b[0], b[1], terrainBase]),
                            Project([a[0], a[1], terrainBase])]], outward,
                            Dot(outward[0], outward[1], outward[2], camera.Look), soil);
                    }
                }
            }
        }
        ViewportSurfaceEdges? SourceEdges(IReadOnlyList<List<double[]>> loops, int category)
        {
            var lines = new List<((double X, double Y) Start, (double X, double Y) End)>();
            foreach (var loop in loops)
            {
                var clipped = Clip(loop);
                for (int i = 0; i < clipped.Count; i++)
                {
                    // The box can introduce a new side on the clipped face.
                    if (!clipped[i].IncomingEdge) continue;
                    var a = Project(clipped[(i + clipped.Count - 1) % clipped.Count].Point);
                    var b = Project(clipped[i].Point);
                    lines.Add(((a.X, a.Y), (b.X, b.Y)));
                }
            }
            if (lines.Count == 0) return null;
            var (edgeWidth, color) = Pen(category);
            return new(lines, edgeWidth, color);
        }
        if (shadows?.GroundShadowBounds is { } footprint)
        {
            left = Math.Min(left, anchorX + factor * footprint.Left);
            right = Math.Max(right, anchorX + factor * footprint.Right);
            bottom = Math.Min(bottom, anchorY + factor * footprint.Bottom);
            top = Math.Max(top, anchorY + factor * footprint.Top);
        }
        if (annotation != null)
        {
            double span = annotation.Radius * 1.18;
            left = Math.Min(left, annotation.X - span);
            right = Math.Max(right, annotation.X + span);
            bottom = Math.Min(bottom, annotation.Y - span);
            top = Math.Max(top, annotation.Y + span);
        }
        left = Math.Max(left, camera.Active ? Math.Max(0, anchorX + camera.X0 * factor) : 0);
        right = Math.Min(right, camera.Active ? Math.Min(page.Size.WidthMillimetres, anchorX + camera.X1 * factor) : page.Size.WidthMillimetres);
        bottom = Math.Max(bottom, camera.Active ? Math.Max(0, anchorY + camera.Y0 * factor) : 0);
        top = Math.Min(top, camera.Active ? Math.Min(page.Size.HeightMillimetres, anchorY + camera.Y1 * factor) : page.Size.HeightMillimetres);
        double width = right - left, height = top - bottom;
        if (!(width > 0 && height > 0)) return null;

        const double pixelsPerMillimetre = 144 / 25.4;
        int columns = (int)Math.Clamp(Math.Ceiling(width * pixelsPerMillimetre), 1, 2048);
        int rows = (int)Math.Clamp(Math.Ceiling(height * pixelsPerMillimetre), 1, 2048);
        var depth = new double[columns * rows];
        var rgbBytes = new byte[depth.Length * 3];
        var facesLightMask = shadows != null ? new bool[depth.Length] : null;
        Array.Fill(depth, double.PositiveInfinity);
        Array.Fill(rgbBytes, byte.MaxValue);
        double stepX = width / columns, stepY = height / rows;
        var crossings = new List<double>();
        int FirstRow(Surface surface) => RasterIndex((top - surface.Rings[0].Max(p => p.Y)) / stepY, rows);
        int LastRow(Surface surface) => RasterIndex((top - surface.Rings[0].Min(p => p.Y)) / stepY, rows);
        int AtColumn(double x) => RasterIndex((x - left) / stepX, columns);
        foreach (var surface in surfaces.Where(s => s.Transmission == 0))
        {
            var outer = surface.Rings[0];
            var origin = outer[0];
            int first = FirstRow(surface), last = LastRow(surface);
            for (int row = first; row < last; row++)
            {
                double y = top - (row + 0.5) * stepY;
                GetCrossings(surface, y, crossings);
                for (int i = 1; i < crossings.Count; i += 2)
                {
                    int start = AtColumn(crossings[i - 1]);
                    int end = AtColumn(crossings[i]);
                    for (int column = start; column < end; column++)
                    {
                        double x = left + (column + 0.5) * stepX;
                        double z = origin.Depth + (x - origin.X) * surface.DepthX + (y - origin.Y) * surface.DepthY;
                        int pixel = row * columns + column;
                        if (!(z < depth[pixel])) continue;
                        depth[pixel] = z;
                        if (facesLightMask != null) facesLightMask[pixel] = surface.FacesLight;
                        if (surface.Pattern == null && surface.Edges == null)
                        {
                            rgbBytes[3 * pixel] = (byte)surface.Rgb;
                            rgbBytes[3 * pixel + 1] = (byte)(surface.Rgb >> 8);
                            rgbBytes[3 * pixel + 2] = (byte)(surface.Rgb >> 16);
                            continue;
                        }
                        double patternInk = surface.Pattern?.Coverage(x, y, stepX, stepY) ?? 0;
                        double edgeInk = surface.Edges?.Coverage(x, y, stepX, stepY) ?? 0;
                        for (int channel = 0; channel < 3; channel++)
                        {
                            int tint = (surface.Rgb >> (channel * 8)) & 255;
                            rgbBytes[3 * pixel + channel] = (byte)Math.Round(ApplyInk(surface, channel, tint, patternInk, edgeInk));
                        }
                    }
                }
            }
        }
        if (shadows != null && facesLightMask != null)
            shadows.Apply(rgbBytes, depth, facesLightMask, columns, rows, left, top, stepX, stepY, anchorX, anchorY,
                factor, InsideModelBox);
        CompositePanes();
        annotation?.Paint(rgbBytes, columns, rows, left, top, stepX, stepY);
        page.DrawImage(new PdfRasterImage(columns, rows, rgbBytes) { TransparentWhite = true }, left, bottom, width, height);
        if (annotation != null)
        {
            page.BeginClip(left, bottom, width, height);
            annotation.DrawLabels(page);
            page.EndClip();
        }
        return (left, bottom, width, height);

        void CompositePanes()
        {
            var panes = surfaces.Select((surface, order) => (surface, order)).Where(pair => pair.surface.Transmission > 0)
                .Select(pair => new Pane(pair.surface, pair.order, FirstRow(pair.surface), LastRow(pair.surface)))
                .Where(pane => pane.FirstRow < pane.LastRow).OrderBy(pane => pane.FirstRow).ToArray();
            if (panes.Length == 0) return;
            var activePanes = new List<Pane>();
            var spans = new List<PaneSpan>();
            var activeSpans = new List<PaneSpan>();
            var hits = new List<PaneHit>();
            int nextPane = 0, lastRow = panes.Max(pane => pane.LastRow);
            for (int row = panes[0].FirstRow; row < lastRow; row++)
            {
                activePanes.RemoveAll(pane => pane.LastRow <= row);
                while (nextPane < panes.Length && panes[nextPane].FirstRow == row)
                    activePanes.Add(panes[nextPane++]);
                double y = top - (row + 0.5) * stepY;
                spans.Clear();
                foreach (var pane in activePanes)
                {
                    GetCrossings(pane.Surface, y, crossings);
                    for (int i = 1; i < crossings.Count; i += 2)
                    {
                        int start = AtColumn(crossings[i - 1]), end = AtColumn(crossings[i]);
                        if (start < end) spans.Add(new(pane, start, end));
                    }
                }
                if (spans.Count == 0) continue;
                spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
                activeSpans.Clear();
                int nextSpan = 0, lastColumn = spans.Max(span => span.End);
                for (int column = spans[0].Start; column < lastColumn; column++)
                {
                    activeSpans.RemoveAll(span => span.End <= column);
                    while (nextSpan < spans.Count && spans[nextSpan].Start == column)
                        activeSpans.Add(spans[nextSpan++]);
                    hits.Clear();
                    int pixel = row * columns + column;
                    double x = left + (column + 0.5) * stepX;
                    foreach (var span in activeSpans)
                    {
                        var surface = span.Pane.Surface;
                        var origin = surface.Rings[0][0];
                        double z = origin.Depth + (x - origin.X) * surface.DepthX + (y - origin.Y) * surface.DepthY;
                        if (z < depth[pixel]) hits.Add(new(span.Pane, z, surface.Pattern?.Coverage(x, y, stepX, stepY) ?? 0,
                            surface.Edges?.Coverage(x, y, stepX, stepY) ?? 0));
                    }
                    if (hits.Count == 0) continue;
                    // Surfaces can cross in the view. Sort where each output ray actually meets them.
                    hits.Sort(static (a, b) => a.Depth == b.Depth
                        ? b.Pane.Order.CompareTo(a.Pane.Order) : b.Depth.CompareTo(a.Depth));
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double color = rgbBytes[3 * pixel + channel];
                        foreach (var hit in hits)
                        {
                            var surface = hit.Pane.Surface;
                            int tint = (surface.Rgb >> (channel * 8)) & 255;
                            color = tint * (1 - surface.Transmission) + color * surface.Transmission;
                            color = ApplyInk(surface, channel, color, hit.PatternInk, hit.EdgeInk);
                        }
                        rgbBytes[3 * pixel + channel] = (byte)Math.Round(color);
                    }
                }
            }
        }
    }

    private static double ApplyInk(Surface surface, int channel, double color, double patternInk, double edgeInk)
    {
        if (surface.Pattern is { } pattern)
            color += (((pattern.Color >> (channel * 8)) & 255) - color) * patternInk;
        if (surface.Edges is { } edges)
            color += (((edges.Color >> (channel * 8)) & 255) - color) * edgeInk;
        return color;
    }

    private static void GetCrossings(Surface surface, double y, List<double> crossings)
    {
        crossings.Clear();
        // The even-odd crossings include the face's opening loops.
        foreach (var ring in surface.Rings)
        {
            var previous = ring[^1];
            foreach (var next in ring)
            {
                if ((previous.Y > y) != (next.Y > y))
                    crossings.Add(previous.X + (next.X - previous.X) * (y - previous.Y) / (next.Y - previous.Y));
                previous = next;
            }
        }
        crossings.Sort();
    }

    private static double Dot(double x, double y, double z, double[] axis) => x * axis[0] + y * axis[1] + z * axis[2];

    private static int RasterIndex(double coordinate, int count) => (int)Math.Clamp(Math.Ceiling(coordinate - 0.5), 0, count);
}
