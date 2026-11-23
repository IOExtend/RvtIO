using RvtIO.BuiltIns;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    private static List<(int CategoryId, bool IsCut, List<(double X, double Y)> Ring)> CollectCurtainProfiles(
        RvtDocument document, int viewId, double cutHeight, double? bottom)
    {
        var result = new List<(int CategoryId, bool IsCut, List<(double X, double Y)> Ring)>();
        var hiddenElements = document.RawData.ViewHiddenElements?.GetValueOrDefault(viewId);
        var hiddenCategories = document.RawData.ViewHiddenCategories?.GetValueOrDefault(viewId);
        double[] origin = [0, 0, cutHeight * FeetToMillimetres];
        double[] horizontal = [1, 0, 0], vertical = [0, 1, 0], direction = [0, 0, 1];
        foreach (var part in document.Elements)
        {
            if (part.CategoryId is not ((int)BuiltInCategory.OST_CurtainWallPanels or (int)BuiltInCategory.OST_CurtainWallMullions)
                || part.IsDiscovered || part.NativeIsSymbol == true || part.SerializedUnplacedOwnerId is > 0
                || part.SerializedHostId is not > 0
                || part.BoundingBox is not { Length: 6 } bounds || !(bounds[2] < cutHeight)
                || (bounds[5] <= cutHeight
                    && (part.CategoryId != (int)BuiltInCategory.OST_CurtainWallMullions || !(bounds[5] > bottom)))
                || hiddenCategories?.Contains(part.CategoryId.Value) == true
                || hiddenElements?.Contains(part.Id) == true || hiddenElements?.Contains(part.SerializedHostId.Value) == true
                || part.Geometry is not { BrepSolids: [{ IsClosed: true } solid] } geometry
                || solid.FaceIndices.Count != geometry.Faces.Count
                || geometry.Faces.Any(face => face.CurvedSurface || face.InnerLoops is { Count: > 0 }
                    || face.InnerVertices is { Count: > 0 } || face.OuterVertices is not { Count: >= 3 } vertices
                    || vertices.Any(p => p.Length != 3 || !p.All(double.IsFinite))))
                continue;

            if (cutHeight < bounds[5])
            {
                foreach (var ring in ElevationPrinter.ClosedSectionRings(geometry.Faces, origin, direction,
                    horizontal, vertical, requireClosed: true))
                    result.Add((part.CategoryId.Value, true, ring));
            }
            else
            {
                // A mullion below the cut projects its saved horizontal top face.
                var tops = geometry.Faces.Where(face => face.PlaneSurface && !face.ExcludedFromTopPlan
                    && face.Normal is [_, _, > 0] && face.OuterVertices!.All(p => Math.Abs(p[2] - bounds[5]) < 1e-7))
                    .Take(2).ToArray();
                if (tops is [var top])
                    result.Add((part.CategoryId.Value, false, top.OuterVertices!.Select(InPlan).ToList()));
            }
        }
        return result;
    }

    private static Dictionary<int, List<(double X, double Y)>> CollectProjectedWallTops(
        RvtDocument document, IEnumerable<int> wallIds, Dictionary<int, List<(double From, double To)>> gaps,
        double bottom, double cutHeight)
    {
        var result = new Dictionary<int, List<(double X, double Y)>>();
        foreach (int id in wallIds)
        {
            if (gaps.TryGetValue(id, out var openings) && openings.Count > 0) continue;
            if (document.GetElement(id) is not { BoundingBox: { Length: 6 } bounds, Geometry: { Faces.Count: > 0 } geometry }
                || !(bounds[2] < bounds[5] && bounds[5] >= bottom && bounds[5] < cutHeight)
                || geometry.Faces.Any(face => face.CurvedSurface || !UsableWallRing(face.OuterVertices))) continue;

            var tops = geometry.Faces.Where(face => face.PlaneSurface && !face.ExcludedFromTopPlan
                && face.InnerLoops is not { Count: > 0 } && face.InnerVertices is not { Count: > 0 }
                && face.Normal is [_, _, > 0] && face.OuterVertices!.All(p => Math.Abs(p[2] - bounds[5]) < 1e-7))
                .Take(2).ToArray();
            if (tops is [{ OuterVertices: { } top }] && HorizontalMaskCoversBody(top, geometry))
                result.Add(id, top.Select(InPlan).ToList());
        }
        return result;
    }

    private static Dictionary<int, (int TypeId, List<List<(double X, double Y)>> Rings)> CollectWallCuts(
        RvtDocument document, int viewId, PlanContent content)
    {
        var result = new Dictionary<int, (int TypeId, List<List<(double X, double Y)>> Rings)>();
        if (PlanCutHeight(document, viewId) is not double cutHeight) return result;
        double cut = cutHeight * FeetToMillimetres;
        if (!double.IsFinite(cut)) return result;
        double[] origin = [0, 0, cut];
        double[] horizontal = [1, 0, 0], vertical = [0, 1, 0], direction = [0, 0, 1];
        foreach (var wall in content.Walls)
        {
            if (document.GetElement(wall.Id) is not
                    { TypeId: > 0, Geometry: { Faces.Count: > 0 } geometry } element
                || geometry.Faces.Any(face => face.CurvedSurface || !UsableWallRing(face.OuterVertices)
                    || (face.InnerLoops?.Count ?? 0) > (face.InnerVertices?.Count ?? 0)
                    || (face.InnerVertices is { Count: > 0 } holes && (!face.PlaneSurface || holes.Any(hole => !UsableWallRing(hole))))))
                continue;

            var rings = ElevationPrinter.ClosedSectionRings(geometry.Faces, origin, direction, horizontal, vertical,
                requireClosed: true, includeFaceOpenings: true).ToList();
            if (rings.Count == 0 || !PreserveWallOpenings(wall, rings, content.Gaps.GetValueOrDefault(wall.Id))) continue;
            foreach (var ring in rings)
                if (SignedArea(ring) < 0) ring.Reverse();
            result[wall.Id] = (element.TypeId, rings);
        }
        return result;
    }

    private static bool UsableWallRing(List<double[]>? ring) =>
        ring is { Count: >= 3 } && ring.All(point => point.Length == 3 && point.All(double.IsFinite));

    private static bool PreserveWallOpenings((int Id, double X1, double Y1, double X2, double Y2, double Width) wall,
        List<List<(double X, double Y)>> rings, List<(double From, double To)>? gaps)
    {
        if (rings.Count == 1 && gaps is not { Count: > 0 }) return true;
        double dx = wall.X2 - wall.X1, dy = wall.Y2 - wall.Y1;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length > 0) || !double.IsFinite(length)) return false;
        double Along((double X, double Y) point) => ((point.X - wall.X1) * dx + (point.Y - wall.Y1) * dy) / length;
        // The plan draws its saved hosted openings even when the model cut passes below a sash.
        const double endpointResolution = 0.1; // Same model millimetre resolution as ClosedSectionRings.
        double coveredThrough = double.NegativeInfinity;
        foreach (var (from, to) in rings.Select(ring => (From: ring.Min(Along), To: ring.Max(Along))).OrderBy(span => span.From))
        {
            if (coveredThrough - from > endpointResolution
                || gaps?.Any(gap => Math.Min(gap.To, to) - Math.Max(gap.From, from) > endpointResolution) == true)
                return false;
            coveredThrough = Math.Max(coveredThrough, to);
        }
        return true;
    }

    private static void DrawWallCuts(PdfPage page, Dictionary<int, (int TypeId, List<List<(double X, double Y)>> Rings)> cuts,
        Func<double, double> toX, Func<double, double> toY, double width,
        (double X0, double Y0, double X1, double Y1)? clip)
    {
        foreach (var ring in cuts.Values.SelectMany(cut => cut.Rings))
        {
            var fill = clip is { } frame
                ? ElevationPrinter.ClipToWindow(ring, frame.X0, frame.Y0, frame.X1, frame.Y1) : ring;
            if (fill.Count >= 3)
                page.DrawPolyline(fill.Select(p => (toX(p.X), toY(p.Y))).ToList(), 0, true, 1);
        }
        foreach (var sameType in cuts.Values.GroupBy(cut => cut.TypeId))
        {
            var rings = sameType.SelectMany(cut => cut.Rings).ToList();
            foreach (var edge in ElevationPrinter.OutlineJoinedRings(rings, (int)BuiltInCategory.OST_Walls,
                coverOppositeEdges: true))
            {
                double x0 = edge.X1, y0 = edge.Y1, x1 = edge.X2, y1 = edge.Y2;
                if (clip is { } frame && !ElevationPrinter.ClipSegment(x0, y0, x1, y1, frame.X0, frame.Y0,
                    frame.X1, frame.Y1, out x0, out y0, out x1, out y1)) continue;
                page.DrawLine(toX(x0), toY(y0), toX(x1), toY(y1), width);
            }
        }
    }
}
