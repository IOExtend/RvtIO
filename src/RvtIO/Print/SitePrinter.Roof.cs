using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class SitePrinter
{
    internal sealed record SiteGraphics(
        IReadOnlyList<SiteSurface> Surfaces,
        IReadOnlyList<List<(double X, double Y)>> GroundShadows,
        double ShadowGray,
        PlanFixtureProjection? Equipment);
    internal sealed record SiteSurface(List<List<(double X, double Y)>> Rings, FillHatch.ModelGrid? Grid,
        IReadOnlyList<List<(double X, double Y)>>? Glazing = null,
        IReadOnlyList<List<(double X, double Y)>>? Shadows = null,
        IReadOnlyList<List<(double X, double Y)>>? Visible = null,
        BuiltInCategory Category = BuiltInCategory.OST_Roofs,
        int RailId = 0);

    internal static SiteGraphics? CollectOpaqueGraphics(RvtDocument document, int viewId, ViewCropFrame? basis)
    {
        if (PlanPrinter.GetPlanRange(document, -1, viewId) is not { } range) return null;
        var hidden = document.RawData.ViewHiddenElements?.GetValueOrDefault(viewId);
        var hiddenCategories = document.RawData.ViewHiddenCategories?.GetValueOrDefault(viewId);
        bool showPatterns = document.RawData.OpaqueViewsWithSurfacePatterns?.Contains(viewId) == true;
        var roofWindows = document.Elements.Where(window =>
                window.CategoryId == (int)BuiltInCategory.OST_Windows && !window.IsDiscovered
                && window.NativeIsSymbol != true && window.SerializedUnplacedOwnerId is not > 0
                && !window.RootClassIs("FamilySymbol") && window.SerializedHostId is > 0
                && hidden?.Contains(window.Id) != true
                && hiddenCategories?.Contains((int)BuiltInCategory.OST_Windows) != true
                && window.Geometry is { Faces.Count: > 0 })
            .ToLookup(window => window.SerializedHostId!.Value);
        var surfaces = new List<(double Height, GeoFace Face, SiteSurface Surface)>();
        var clippedFaces = new HashSet<GeoFace>();
        var addedCasters = new Dictionary<int, List<GeoFace>>();
        var opaquePanes = new List<GeoFace>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId is not ((int)BuiltInCategory.OST_Roofs or (int)BuiltInCategory.OST_Stairs
                    or (int)BuiltInCategory.OST_Floors
                    or (int)BuiltInCategory.OST_RailingTopRail)
                || element.IsDiscovered || hidden?.Contains(element.Id) == true
                || element.Geometry is not { Faces.Count: > 0 } geometry)
                continue;
            var category = (BuiltInCategory)element.CategoryId.Value;
            bool horizontal = category is BuiltInCategory.OST_Stairs or BuiltInCategory.OST_Floors;
            bool rail = category == BuiltInCategory.OST_RailingTopRail;
            if (horizontal && (element.NativeIsSymbol == true || element.SerializedUnplacedOwnerId is > 0
                || !element.RootClassIs(category == BuiltInCategory.OST_Stairs ? "StairsElement" : "Floor")
                || hiddenCategories?.Contains((int)category) == true))
                continue;
            if (rail && !VisibleTopRail(element)) continue;
            var railFaces = rail ? new List<GeoFace>() : null;
            List<List<(double X, double Y)>>? glazing = null;
            foreach (var face in geometry.Faces)
            {
                if (face.CurvedSurface || face.OuterVertices is not { Count: >= 3 } outer || !InRange(outer)
                    || face.InnerVertices?.Any(hole => hole.Any(p => !Valid(p))) == true) continue;
                if (rail)
                {
                    if (!face.PlaneSurface || face.InnerLoops is { Count: > 0 } && face.InnerVertices is not { Count: > 0 }
                        || document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1)
                            ?.SerializedMaterialTransparency is > 0) continue;
                    railFaces!.Add(face);
                }
                if (face.Normal is not { Length: 3 } normal || !(normal[2] > (rail ? 1e-9 : 0))) continue;
                if (horizontal && (!face.PlaneSurface || outer.Any(point => Math.Abs(point[2] - outer[0][2]) > 1e-8)
                    || face.InnerLoops is { Count: > 0 } && face.InnerVertices is not { Count: > 0 })) continue;
                var rings = Rings(face);
                if (rings.Count == 0) continue;
                if (category == BuiltInCategory.OST_Roofs && rings.Count > 1)
                    glazing ??= CollectSkylights(element.Id);
                if (horizontal || rail) clippedFaces.Add(face);
                surfaces.Add((outer.Average(p => p[2]), face, new SiteSurface(rings,
                    showPatterns && category == BuiltInCategory.OST_Roofs
                        ? FillHatch.ProjectModelGrid(document, face, InView) : null,
                    rings.Count > 1 && glazing is { Count: > 0 } ? glazing : null,
                    Category: category, RailId: rail ? element.Id : 0)));
            }
            if (railFaces is { Count: > 0 }) addedCasters.Add(element.Id, railFaces);
        }

        var equipment = CollectEquipment(document, viewId, range.Bottom, range.Cut, hidden, out var equipmentCasters);
        var surfaceFaces = surfaces.Select(entry => entry.Face).ToHashSet();
        var (shadows, gray) = CollectShadows(document, viewId, basis, range.Cut, hidden, equipmentCasters,
            addedCasters, opaquePanes, surfaceFaces, clippedFaces, out var surfaceShadows, out var visibility);
        return surfaces.Count == 0 && shadows.Count == 0 && equipment == null ? null
            : new SiteGraphics(surfaces.OrderBy(entry => entry.Surface.Category != BuiltInCategory.OST_Roofs)
                .ThenBy(entry => entry.Height).Select(entry =>
                entry.Surface with
                {
                    Shadows = surfaceShadows.GetValueOrDefault(entry.Face),
                    Visible = visibility.GetValueOrDefault(entry.Face),
                }).ToList(),
                shadows, gray, equipment);

        bool VisibleTopRail(Element rail) =>
            rail is { RootClassName: "TopRail", NativeIsSymbol: not true, SerializedUnplacedOwnerId: not > 0,
                SerializedOwningElementId: > 0 and int ownerId }
            && hiddenCategories?.Contains((int)BuiltInCategory.OST_RailingTopRail) != true
            && hidden?.Contains(ownerId) != true
            && document.GetElement(ownerId) is { RootClassName: "BaseRailing", CategoryId: int ownerCategory,
                IsDiscovered: false, NativeIsSymbol: not true, SerializedUnplacedOwnerId: not > 0 }
            && hiddenCategories?.Contains(ownerCategory) != true;

        List<List<(double X, double Y)>> CollectSkylights(int hostId)
        {
            var panes = new List<List<(double X, double Y)>>();
            foreach (var window in roofWindows[hostId])
            {
                var frameFaces = new List<GeoFace>();
                foreach (var face in window.Geometry!.Faces)
                {
                    if (!face.PlaneSurface || face.CurvedSurface
                        || face.OuterVertices is not { Count: >= 3 } vertices || !InRange(vertices)) continue;
                    var material = document.GetElement(face.RenderStyleId ?? face.NestedRenderStyleId ?? -1);
                    if (material?.SerializedMaterialTransparency == 0)
                        frameFaces.Add(face);
                    else if (material?.SerializedMaterialTransparency is > 0 and <= 1
                        && face.Normal is [_, _, > 0]
                        && face.InnerVertices is not { Count: > 0 } && face.InnerLoops is not { Count: > 0 }
                        && Project(vertices, true) is { } pane)
                    {
                        panes.Add(pane);
                        opaquePanes.Add(face);
                    }
                }
                if (frameFaces.Count > 0) addedCasters.Add(window.Id, frameFaces);
            }
            return panes;
        }

        static bool Valid(double[] point) => point.Length == 3 && point.All(double.IsFinite);
        bool InRange(List<double[]> vertices) =>
            vertices.All(p => Valid(p) && p[2] >= range.Bottom - 1e-9 && p[2] <= range.Cut + 1e-9);

        (double X, double Y) InView(double[] point) => basis == null
            ? (point[0] * FeetToMillimetres, point[1] * FeetToMillimetres)
            : ((point[0] * basis.Right[0] + point[1] * basis.Right[1]) * FeetToMillimetres,
                (point[0] * basis.Up[0] + point[1] * basis.Up[1]) * FeetToMillimetres);

        List<List<(double X, double Y)>> Rings(GeoFace face)
        {
            var outer = Project(face.OuterVertices!, true);
            if (outer == null) return [];
            var result = new List<List<(double X, double Y)>> { outer };
            foreach (var hole in face.InnerVertices ?? [])
                if (Project(hole, false) is { } ring) result.Add(ring);
            return result;
        }

        List<(double X, double Y)>? Project(List<double[]> vertices, bool exterior)
        {
            var ring = vertices.Select(InView).ToList();
            double area = PlanPrinter.SignedArea(ring);
            if (!double.IsFinite(area) || Math.Abs(area) < 1e-6) return null;
            if ((area > 0) != exterior) ring.Reverse();
            return ring;
        }
    }

    internal static void DrawSurfaces(PdfPage page, RvtDocument document, SiteGraphics graphics, SiteContent site,
        double scale, double originX, double originY)
    {
        int printedScale = (int)Math.Round(scale);
        double roofPen = PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Roofs, false,
            printedScale, out double width) ? width : 0.1;
        double stairPen = PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Stairs, false,
            printedScale, out width) ? width : 0.1;
        double floorPen = PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_Floors, false,
            printedScale, out width) ? width : 0.1;
        double railPen = PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_RailingTopRail, false,
            printedScale, out width) ? width : stairPen;
        foreach (var surface in graphics.Surfaces)
        {
            if (surface.Visible is { Count: 0 }) continue;
            double pen = surface.Category switch
            {
                BuiltInCategory.OST_Stairs => stairPen,
                BuiltInCategory.OST_Floors => floorPen,
                _ => roofPen,
            };
            var projected = surface.Rings.Select(OnPage).ToList();
            if (surface.Visible is { } visible) page.BeginPolygonClip(visible.Select(OnPage), evenOdd: false);
            page.FillPolygonUnion(projected, 1);
            if (surface.Shadows is { Count: > 0 } shadows)
            {
                page.BeginPolygonClip(projected);
                page.FillPolygonUnion(shadows.Select(OnPage), graphics.ShadowGray);
                page.EndClip();
            }
            if (surface.Grid is { } grid)
                page.FillWithParallelLines(projected, grid.Spacing / scale, grid.Angle,
                    originX + (grid.X - site.MinX) / scale, originY + (grid.Y - site.MinY) / scale, pen);
            if (surface.Glazing is { Count: > 0 } glazing)
            {
                // The hidden-line pane covers the view through the opening. Keep the roof and its
                // pattern above panes that project beyond the opening's perimeter.
                page.BeginPolygonClip(projected.Skip(1));
                page.FillPolygonUnion(glazing.Select(OnPage), 1);
                page.EndClip();
            }
            if (surface.RailId == 0)
                foreach (var ring in projected) page.DrawPolyline(ring, pen, true);
            if (surface.Visible != null) page.EndClip();
        }
        foreach (var rail in graphics.Surfaces.Where(surface => surface.RailId > 0).GroupBy(surface => surface.RailId))
        {
            var visible = rail.SelectMany(surface => surface.Visible ?? surface.Rings).ToList();
            if (visible.Count == 0) continue;
            bool clipped = rail.Any(surface => surface.Visible != null);
            if (clipped) page.BeginPolygonClip(visible.Select(OnPage), evenOdd: false);
            var outlines = ElevationPrinter.OutlineJoinedRings(rail.SelectMany(surface => surface.Rings).ToList(),
                (int)BuiltInCategory.OST_RailingTopRail);
            foreach (var edge in outlines)
            {
                var (x1, y1) = PagePoint((edge.X1, edge.Y1));
                var (x2, y2) = PagePoint((edge.X2, edge.Y2));
                page.DrawLine(x1, y1, x2, y2, railPen);
            }
            if (clipped) page.EndClip();
        }

        List<(double X, double Y)> OnPage(List<(double X, double Y)> ring) => ring.Select(PagePoint).ToList();
        (double X, double Y) PagePoint((double X, double Y) point) =>
            (originX + (point.X - site.MinX) / scale, originY + (point.Y - site.MinY) / scale);
    }
}
