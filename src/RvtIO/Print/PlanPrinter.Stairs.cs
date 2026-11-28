using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using Point = (double X, double Y);

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    internal static void DrawStairPaths(PdfPage page, RvtDocument document, int viewId, int levelId,
        PlanContent content, double scale, double originX, double originY)
    {
        if (document.RawData.PlanStairPaths?.GetValueOrDefault(viewId) is not { Count: > 0 } paths
            || GetPlanRange(document, levelId, viewId) is not { } range
            || !PenWidths.TryGetStyle(document, (int)BuiltInCategory.OST_StairsPaths, false, out int pen, out int rgb))
            return;
        double width = PenWidths.TryAnnotationWidth(document, pen, out double saved) ? saved : 0.1;
        var color = new PdfColor((rgb & 255) / 255d, ((rgb >> 8) & 255) / 255d, ((rgb >> 16) & 255) / 255d);
        var hidden = document.RawData.ViewHiddenElements?.GetValueOrDefault(viewId);
        Point Paper(Point p) => ((p.X * FeetToMillimetres - content.MinX) / scale + originX,
            (p.Y * FeetToMillimetres - content.MinY) / scale + originY);
        static Point Mid(TfBlockDeserializer.StairPlanLine line) =>
            ((line.X1 + line.X2) / 2, (line.Y1 + line.Y2) / 2);
        static bool Same(Point a, Point b) => Math.Abs(a.X - b.X) < 1e-7 && Math.Abs(a.Y - b.Y) < 1e-7;
        foreach (var path in paths)
        {
            if (hidden?.Contains(path.Id) == true || hidden?.Contains(path.HostId) == true
                || document.GetElement(path.HostId) is not { SerializedStairCore: [{ ClassId: 3708 } component] } stair
                || stair.LookupParameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM)?.Value is not int top || top != levelId
                || stair.LookupParameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.Value is not int bottom || bottom == levelId
                || document.GetElement(component.Id)?.SerializedStairRunInput is not { Numbers: { } levels, Plan: { } plan } run
                || levels.TopElevation > range.Cut || levels.BeginWithRiser != 1 || levels.EndWithRiser != 0 || plan.Treads.Length < 3)
                continue;

            if (run.Style == TfBlockDeserializer.StairRunStyle.Sketched)
            {
                // For a descending flight that crosses the lower clip, locate the first riser at the
                // view's cut offset measured from the base level and the last physical riser at the top.
                if (levels.BottomElevation < range.Bottom && levels.TopElevation >= range.Bottom
                    && document.RawData.PlanViewRanges?.TryGetValue(viewId, out var savedRange) == true
                    && (savedRange.LevelIds[0] == levelId || savedRange.LevelIds[0] == -1)
                    && document.GetElement(bottom)?.LookupParameter(BuiltInParameter.LEVEL_ELEV)?.Value is double baseHeight
                    && SketchedStairPath(plan, baseHeight + savedRange.Offsets[0]) is { Count: >= 2 } points)
                {
                    var paper = points.Select(Paper).ToList();
                    double dxToTail = paper[1].X - paper[0].X, dyToTail = paper[1].Y - paper[0].Y;
                    double headLength = Math.Sqrt(dxToTail * dxToTail + dyToTail * dyToTail);
                    if (!(headLength > 0 && double.IsFinite(headLength))) continue;
                    for (int i = 1; i < paper.Count; i++)
                        page.DrawLine(paper[i - 1].X, paper[i - 1].Y, paper[i].X, paper[i].Y, width, color);
                    DrawArrowhead(paper[0], dxToTail / headLength, dyToTail / headLength, path);
                }
                continue;
            }

            if (run.Style != TfBlockDeserializer.StairRunStyle.Straight || plan.Center is not [var axis]
                || levels.BottomElevation < range.Bottom || !Same(Mid(plan.Treads[0]), (axis.X1, axis.Y1))
                || !Same(Mid(plan.Treads[^1]), (axis.X2, axis.Y2)))
                continue;

            Point tip = Paper(Mid(plan.Treads[0])), focus = Paper((axis.X2, axis.Y2));
            double dx = focus.X - tip.X, dy = focus.Y - tip.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (!(length > 0 && double.IsFinite(length))) continue;

            Point previous = tip;
            // The upper envelope ends one pitch beyond the last physical nosing when End With Riser is off.
            for (int i = 1; i < plan.Treads.Length - 1; i++)
            {
                Point landing = Paper(Mid(plan.Treads[i]));
                page.DrawLine(previous.X, previous.Y, landing.X, landing.Y, width, color);
                previous = landing;
            }
            double ux = dx / length, uy = dy / length;
            DrawArrowhead(tip, ux, uy, path);
            if (path.DownText is { } text && page.Owner.Font is { CapHeight: > 0 } font
                && Math.Abs(ux) < 1e-7 && uy > 0)
            {
                Point label = Paper((axis.X2 + path.TextX, axis.Y2 + path.TextY));
                double size = SheetComposer.SymbolLabelTextSize(font, 0, path.CapHeight);
                page.DrawText(text, label.X, label.Y - path.CapHeight * FeetToMillimetres / 2, size, PdfTextAlignment.Right);
            }
        }

        void DrawArrowhead(Point tip, double ux, double uy, RevitFileInfo.PlanStairPath path)
        {
            double arrow = path.ArrowSize * FeetToMillimetres;
            double halfSpan = arrow * Math.Tan(path.ArrowAngle / 2);
            double x = tip.X + ux * arrow, y = tip.Y + uy * arrow;
            page.DrawLine(tip.X, tip.Y, x - uy * halfSpan, y + ux * halfSpan, width, color);
            page.DrawLine(tip.X, tip.Y, x + uy * halfSpan, y - ux * halfSpan, width, color);
        }
    }

    private static List<Point>? SketchedStairPath(TfBlockDeserializer.StairPlanInput plan, double cut)
    {
        const double tolerance = 1e-7;
        var treads = plan.Treads;
        if (plan.Center.Length < 2 || !(treads[0].Elevation < cut && cut <= treads[^2].Elevation)) return null;
        var first = Array.Find(treads, tread => tread.Elevation >= cut);
        if (Crossing(first) is not { } tip || Crossing(treads[^2]) is not { } tail
            || tip.Segment > tail.Segment || tip.Segment == tail.Segment && tip.Fraction >= tail.Fraction)
            return null;

        List<Point> path = [tip.Position];
        for (int i = tip.Segment; i < tail.Segment; i++)
        {
            var line = plan.Center[i];
            var next = plan.Center[i + 1];
            Point end = (line.X2, line.Y2);
            if (!SamePoint(end, (next.X1, next.Y1))) return null;
            if (!SamePoint(path[^1], end)) path.Add(end);
        }
        if (!SamePoint(path[^1], tail.Position)) path.Add(tail.Position);
        return path;

        (int Segment, double Fraction, Point Position)? Crossing(TfBlockDeserializer.StairPlanLine tread)
        {
            (int Segment, double Fraction, Point Position)? hit = null;
            double rx = tread.X2 - tread.X1, ry = tread.Y2 - tread.Y1;
            for (int i = 0; i < plan.Center.Length; i++)
            {
                var line = plan.Center[i];
                double sx = line.X2 - line.X1, sy = line.Y2 - line.Y1;
                double det = sx * ry - sy * rx;
                if (Math.Abs(det) < tolerance) continue;
                double x = tread.X1 - line.X1, y = tread.Y1 - line.Y1;
                double alongPath = (x * ry - y * rx) / det, alongTread = (x * sy - y * sx) / det;
                if (alongPath < -tolerance || alongPath > 1 + tolerance
                    || alongTread < -tolerance || alongTread > 1 + tolerance) continue;
                double fraction = Math.Clamp(alongPath, 0, 1);
                Point position = (line.X1 + fraction * sx, line.Y1 + fraction * sy);
                if (hit is { } other && !SamePoint(other.Position, position)) return null;
                hit = (i, fraction, position);
            }
            return hit;
        }

        static bool SamePoint(Point a, Point b) => Math.Abs(a.X - b.X) < tolerance && Math.Abs(a.Y - b.Y) < tolerance;
    }

    private static bool CollectSavedStair(RvtDocument document, Element stair, int viewId, int levelId,
        (double Bottom, double Cut, double Top) range,
        IEnumerable<Element> topRails, HashSet<int> savedTopRails,
        List<List<Point>> fills, ref List<(double X1, double Y1, double X2, double Y2, int StyleId)>? lines)
    {
        // shortcut: one run with linear side paths, extend when other run layouts decode.
        if (stair.SerializedStairCore is not [{ ClassId: 3708 } component]
            || document.GetElement(component.Id)?.SerializedStairRunInput is not { Plan: { } plan } run)
            return false;
        var start = plan.Treads[0];
        var end = plan.Treads[^1];
        if (!JoinsTread(start, (plan.Left[0].X1, plan.Left[0].Y1), (plan.Right[0].X1, plan.Right[0].Y1))
            || !JoinsTread(end, (plan.Left[^1].X2, plan.Left[^1].Y2), (plan.Right[^1].X2, plan.Right[^1].Y2)))
            return false;
        bool straightBelowCut = run is { Style: TfBlockDeserializer.StairRunStyle.Straight, Numbers: { } straightLevels }
            && plan.Left.Length == 1 && plan.Right.Length == 1 && straightLevels.BottomElevation >= range.Bottom;
        // The upper plan's own stair path identifies a descending projection through the opening,
        // including lower treads outside the plan's ordinary bottom clip.
        bool sketchedDescending = run.Style == TfBlockDeserializer.StairRunStyle.Sketched
            && stair.LookupParameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM)?.Value is int top && top == levelId
            && stair.LookupParameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.Value is int bottom && bottom != levelId
            && document.RawData.PlanStairPaths?.GetValueOrDefault(viewId)?.Any(path => path.HostId == stair.Id) == true;
        if ((straightBelowCut || sketchedDescending) && run.Numbers is { } levels
            && levels.TopElevation >= range.Bottom && levels.TopElevation <= range.Cut && end.Elevation <= range.Cut)
        {
            var leftBoundary = ReadSidePath(plan.Left);
            var rightBoundary = ReadSidePath(plan.Right);
            int edgeStyle = Style(BuiltInCategory.OST_StairsOutlines);
            int treadStyle = Style(BuiltInCategory.OST_StairsNosingLines);
            if (leftBoundary == null || rightBoundary == null || edgeStyle == 0 || treadStyle == 0) return false;
            var projected = lines ??= [];
            var railTops = new List<List<double[]>>();
            const int railCategory = (int)BuiltInCategory.OST_RailingTopRail;
            int projectionRailStyle = sketchedDescending ? Style(BuiltInCategory.OST_RailingTopRail) : 0;
            if (projectionRailStyle != 0)
                foreach (var rail in topRails)
                {
                    if (rail.BoundingBox is not { Length: 6 } bounds || !(bounds[5] <= range.Cut)) continue;
                    var tops = rail.Geometry!.Faces.Where(face => face.Normal is [_, _, > 1e-7]).ToList();
                    if (tops.Count == 0 || tops.Any(face => face.OuterVertices is not { Count: >= 3 }
                        || face.OuterVertices.Any(point => point.Length != 3 || point.Any(value => !double.IsFinite(value)))
                        || face.InnerVertices is { Count: > 0 } || face.InnerLoops is { Count: > 0 })) continue;
                    var rings = tops.Select(face => ProjectRailRing(face.OuterVertices!)).ToList();
                    fills.AddRange(rings);
                    railTops.AddRange(tops.Select(face => face.OuterVertices!));
                    foreach (var edge in ElevationPrinter.OutlineJoinedRings(rings, railCategory))
                        projected.Add((edge.X1, edge.Y1, edge.X2, edge.Y2, projectionRailStyle));
                    savedTopRails.Add(rail.Id);
                }
            foreach (var edge in plan.Treads)
            {
                if (railTops.Count == 0)
                {
                    AddStairLine(projected, (edge.X1, edge.Y1), (edge.X2, edge.Y2), treadStyle);
                    continue;
                }
                // The attached rail can hide part of a nosing even when the run projects below the plan range.
                var masks = railTops.Select(ring => ElevationPrinter.ClipBehind(ring, [0, 0, edge.Elevation], [0, 0, 1]))
                    .Where(ring => ring.Count >= 3).Select(ProjectRailRing);
                var line = new ElevationPrinter.CollectedEdge(edge.X1 * FeetToMillimetres, edge.Y1 * FeetToMillimetres,
                    edge.X2 * FeetToMillimetres, edge.Y2 * FeetToMillimetres, 0, 0, -1, -1, 0, 0, 0, 0);
                foreach (var span in ElevationPrinter.VisibleTerrainEdges([line], masks, [], coverBoundary: false))
                    projected.Add((span.X1, span.Y1, span.X2, span.Y2, treadStyle));
            }
            foreach (var edge in plan.Left.Concat(plan.Right))
                AddStairLine(projected, (edge.X1, edge.Y1), (edge.X2, edge.Y2), edgeStyle);
            AddMask(leftBoundary.Concat(rightBoundary.AsEnumerable().Reverse()));
            return true;
        }

        double cut = range.Cut;
        if (!(start.Elevation < cut && cut < end.Elevation)
            || document.GetElement(stair.TypeId) is not { } stairType
            || stairType.LookupParameter(BuiltInParameter.STAIRSTYPE_SHOW_CUTLINE)?.Value is 0
            || stairType.LookupParameter(BuiltInParameter.STAIRSTYPE_CUTMARK_TYPE)?.Value is not int typeId
            || document.GetElement(typeId) is not { } markType
            || markType.LookupParameter(BuiltInParameter.CUT_LINE_TYPE)?.Value is not 0
            || markType.LookupParameter(BuiltInParameter.CUT_LINE_ANGLE)?.Value is not double angle
            || markType.LookupParameter(BuiltInParameter.CUT_LINE_EXTENSION)?.Value is not 0d
            || markType.LookupParameter(BuiltInParameter.CUT_MARK_SYMBOL)?.Value is not int symbol || symbol is not (0 or 1)
            || document.GetElement(viewId)?.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value is not int scale || scale <= 0)
            return false;

        int tread = 0;
        for (int i = 1; i < plan.Treads.Length; i++)
        {
            if (plan.Treads[i].Elevation <= plan.Treads[i - 1].Elevation) return false;
            if (plan.Treads[i].Elevation <= cut) tread = i;
        }
        var atCut = plan.Treads[tread];
        Point center = ((atCut.X1 + atCut.X2) / 2, (atCut.Y1 + atCut.Y2) / 2);
        double direction = Math.Atan2(atCut.Y2 - atCut.Y1, atCut.X2 - atCut.X1) + angle;
        Point along = (Math.Cos(direction), Math.Sin(direction));
        double Side(Point point) => along.X * (point.Y - center.Y) - along.Y * (point.X - center.X);
        double lower = Side(((start.X1 + start.X2) / 2, (start.Y1 + start.Y2) / 2));
        if (!double.IsFinite(lower) || Math.Abs(lower) < 1e-7) return false;
        double sign = Math.Sign(lower);
        double Distance(Point point) => Side(point) * sign;
        Point Intersect(Point a, Point b, double da, double db)
        {
            double fraction = da / (da - db);
            return (a.X + (b.X - a.X) * fraction, a.Y + (b.Y - a.Y) * fraction);
        }

        if (!TrySplitSide(plan.Left, out var left, out var leftAbove)
            || !TrySplitSide(plan.Right, out var right, out var rightAbove)) return false;
        Point a = left[^1], b = right[^1];
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length > 1e-7) || !double.IsFinite(length)) return false;

        List<Point> mark = [a, b];
        if (symbol == 1)
        {
            if (markType.LookupParameter(BuiltInParameter.CUT_MARK_SYMBOL_SIZE)?.Value is not double size
                || !(size > 0) || !double.IsFinite(size)) return false;
            double span = size * scale;
            if (span >= length) return false;
            double ux = dx / length, uy = dy / length;
            Point mid = ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            double halfDepth = span * 5 / 8;
            mark = [a, (mid.X - ux * span / 2, mid.Y - uy * span / 2),
                (mid.X - uy * halfDepth, mid.Y + ux * halfDepth),
                (mid.X + uy * halfDepth, mid.Y - ux * halfDepth),
                (mid.X + ux * span / 2, mid.Y + uy * span / 2), b];
        }

        int outline = Style(BuiltInCategory.OST_StairsOutlines), outlineAbove = Style(BuiltInCategory.OST_StairsOutlinesAboveCut);
        int nosing = Style(BuiltInCategory.OST_StairsNosingLines), nosingAbove = Style(BuiltInCategory.OST_StairsNosingLinesAboveCut);
        int cutMark = Style(BuiltInCategory.OST_StairsCutMarks);
        if (outline == 0 || outlineAbove == 0 || nosing == 0 || nosingAbove == 0 || cutMark == 0) return false;

        var strokes = lines ??= [];
        DrawPath(left, outline);
        DrawPath(right, outline);
        DrawPath(leftAbove, outlineAbove);
        DrawPath(rightAbove, outlineAbove);
        DrawPath(mark, cutMark);
        foreach (var edge in plan.Treads)
            DrawSplit((edge.X1, edge.Y1), (edge.X2, edge.Y2), nosing, nosingAbove);
        AddMask(left.Concat(mark.Skip(1)).Concat(right.AsEnumerable().Reverse().Skip(1)));

        int railStyle = Style(BuiltInCategory.OST_RailingTopRail), railAbove = Style(BuiltInCategory.OST_RailingTopRailAboveCut);
        if (railStyle != 0 && railAbove != 0)
        {
            foreach (var rail in topRails)
            {
                var faces = FurnitureFaces(rail.Geometry!.Faces.Where(face => face.Normal is [_, _, > 1e-7]));
                if (faces.Count == 0 || faces.Any(face => face.Holes.Count > 0)) continue;
                var rings = faces.Select(face => face.Outer).ToList();
                foreach (var ring in rings)
                {
                    var mask = ElevationPrinter.ClipHalfPlane(ring, point => RailDistance(point) >= 0, RailDistance);
                    if (mask.Count >= 3) fills.Add(mask);
                }
                foreach (var edge in ElevationPrinter.OutlineJoinedRings(rings, (int)BuiltInCategory.OST_RailingTopRail))
                    DrawSplit(ToFeet((edge.X1, edge.Y1)), ToFeet((edge.X2, edge.Y2)), railStyle, railAbove);
                savedTopRails.Add(rail.Id);
            }
        }
        return true;

        static Point ToFeet(Point point) => (point.X / FeetToMillimetres, point.Y / FeetToMillimetres);
        static List<Point> ProjectRailRing(List<double[]> vertices)
        {
            var ring = vertices.Select(point => (point[0] * FeetToMillimetres, point[1] * FeetToMillimetres)).ToList();
            if (SignedArea(ring) < 0) ring.Reverse();
            return ring;
        }
        double RailDistance(Point point) => Distance(ToFeet(point));

        void DrawSplit(Point p, Point q, int belowStyle, int aboveStyle)
        {
            double dp = Distance(p), dq = Distance(q);
            if (dp * dq < 0)
            {
                Point split = Intersect(p, q, dp, dq);
                AddStairLine(strokes, p, split, dp > 0 ? belowStyle : aboveStyle);
                AddStairLine(strokes, split, q, dq > 0 ? belowStyle : aboveStyle);
            }
            else AddStairLine(strokes, p, q, dp + dq >= 0 ? belowStyle : aboveStyle);
        }

        int Style(BuiltInCategory category) => document.RawData.GraphicsStylePens?
            .FirstOrDefault(entry => entry.Value.CategoryId == (int)category && entry.Value.OwnerId == -1
                && entry.Value.GStyleType == 1).Key ?? 0;

        void DrawPath(List<Point> path, int style)
        {
            for (int i = 1; i < path.Count; i++) AddStairLine(strokes, path[i - 1], path[i], style);
        }

        void AddMask(IEnumerable<Point> boundary)
        {
            var mask = boundary.Select(p => (p.X * FeetToMillimetres, p.Y * FeetToMillimetres)).ToList();
            if (SignedArea(mask) < 0) mask.Reverse();
            fills.Add(mask);
        }

        static List<Point>? ReadSidePath(TfBlockDeserializer.StairPlanLine[] side)
        {
            List<Point> points = [(side[0].X1, side[0].Y1)];
            foreach (var edge in side)
            {
                if (Math.Abs(points[^1].X - edge.X1) > 1e-7 || Math.Abs(points[^1].Y - edge.Y1) > 1e-7)
                    return null;
                points.Add((edge.X2, edge.Y2));
            }
            return points;
        }

        bool TrySplitSide(TfBlockDeserializer.StairPlanLine[] side, out List<Point> below, out List<Point> above)
        {
            below = [];
            above = [];
            Point previous = (side[0].X1, side[0].Y1);
            if (Distance(previous) <= 0) return false;
            below.Add(previous);
            foreach (var edge in side)
            {
                if (Math.Abs(previous.X - edge.X1) > 1e-7 || Math.Abs(previous.Y - edge.Y1) > 1e-7) return false;
                Point next = (edge.X2, edge.Y2);
                double dp = Distance(previous), dq = Distance(next);
                if (above.Count > 0 && dq >= 0) return false;
                if (dp > 0 && dq < 0)
                {
                    Point intersection = Intersect(previous, next, dp, dq);
                    below.Add(intersection);
                    above.Add(intersection);
                }
                if (dq == 0) return false;
                (dq > 0 ? below : above).Add(next);
                previous = next;
            }
            return above.Count > 1;
        }
    }

    private static void AddStairLine(List<(double X1, double Y1, double X2, double Y2, int StyleId)> lines,
        Point from, Point to, int style) => lines.Add((from.X * FeetToMillimetres, from.Y * FeetToMillimetres,
        to.X * FeetToMillimetres, to.Y * FeetToMillimetres, style));

    private static bool JoinsTread(TfBlockDeserializer.StairPlanLine tread, Point a, Point b)
    {
        static bool Same(Point p, Point q) => Math.Abs(p.X - q.X) <= 1e-7 && Math.Abs(p.Y - q.Y) <= 1e-7;
        Point start = (tread.X1, tread.Y1), end = (tread.X2, tread.Y2);
        return Same(a, start) && Same(b, end) || Same(a, end) && Same(b, start);
    }
}
