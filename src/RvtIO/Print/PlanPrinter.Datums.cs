using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    internal sealed record PlanGrid(int TypeId, string Name, double Radius,
        (double X, double Y) Start, (double X, double Y) End,
        bool StartBubble, bool EndBubble, TfBlockDeserializer.DatumLeader? StartLeader,
        TfBlockDeserializer.DatumLeader? EndLeader);

    internal static List<PlanGrid>? CollectPlanGrids(RvtDocument document, Element view, ViewCropFrame crop, double scale)
    {
        if (!crop.Right.SequenceEqual(new double[] { 1, 0, 0 }) || !crop.Up.SequenceEqual(new double[] { 0, 1, 0 })
            || document.RawData.DatumExtents?.GetValueOrDefault(view.Id) is not { Normal: [0, 0, -1] } extents)
            return null;
        var box = SheetComposer.CropContentRect(crop);
        var grids = new List<PlanGrid>();
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Grids
                || element.Location is not { CurveType: "Line", StartPoint.Length: >= 2, EndPoint.Length: >= 2 } line)
                continue;
            double x0 = line.StartPoint[0] * FeetToMillimetres, y0 = line.StartPoint[1] * FeetToMillimetres;
            double x1 = line.EndPoint[0] * FeetToMillimetres, y1 = line.EndPoint[1] * FeetToMillimetres;
            if (!ElevationPrinter.ClipSegment(x0, y0, x1, y1, box.X0, box.Y0, box.X1, box.Y1,
                    out _, out _, out _, out _)) continue;
            var extent = extents.Datums.GetValueOrDefault(element.Id);
            byte flags = extent?.Overrides ?? 0;
            // shortcut: use the fallback plan drawing until per-view 2D endpoints are supported.
            if ((flags & 0xF0) != 0 || (flags & 3) == 3 || ((flags >> 2) & 3) == 3
                || document.RawData.DatumHeads?.GetValueOrDefault(element.TypeId) is not
                    { EndSegmentLength: > 0 and < 1, BubbleAtEnd1: bool endBubble, BubbleAtEnd2: bool startBubble } head
                || ElevationPrinter.CircleHeadRadius(document, head.SymbolId, "M_Grid Head - Circle") is not double radius)
                return null;

            // Revit applies a fixed paper-space margin in a cropped plan with annotation cropping off.
            const double modelCropDatumMarginFeetOnPaper = 43d / 512;
            double margin = (view.ViewAnnotationCropActive == false ? modelCropDatumMarginFeetOnPaper
                : head.EndSegmentLength.Value) * FeetToMillimetres * scale;
            if (!ElevationPrinter.ClipSegment(x0, y0, x1, y1, box.X0 - margin, box.Y0 - margin,
                    box.X1 + margin, box.Y1 + margin, out x0, out y0, out x1, out y1)) continue;
            grids.Add(new PlanGrid(element.TypeId, element.Name ?? "", radius, (x0, y0), (x1, y1),
                BubbleVisible((flags >> 2) & 3, startBubble), BubbleVisible(flags & 3, endBubble),
                extent?.BubbleLeader, extent?.FreeEndLeader));
        }
        return grids;

        static bool BubbleVisible(int flag, bool visible) => flag == 0 ? visible : flag == 1;
    }

    internal static void DrawPlanGrids(PdfPage page, RvtDocument document, List<PlanGrid> grids,
        PlanContent content, double scale, double originX, double originY)
    {
        const double ptToMm = 25.4 / 72;
        double ToX(double x) => (x - content.MinX) / scale + originX;
        double ToY(double y) => (y - content.MinY) / scale + originY;
        foreach (var grid in grids)
        {
            double dx = grid.End.X - grid.Start.X, dy = grid.End.Y - grid.Start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (!(length > 0)) continue;
            dx /= length;
            dy /= length;
            var start = DrawEnd(grid.Start, -1, grid.StartBubble, grid.StartLeader);
            var end = DrawEnd(grid.End, 1, grid.EndBubble, grid.EndLeader);
            var pattern = document.RawData.GridCenterCategories?.GetValueOrDefault(grid.TypeId) is int category
                && LinePatterns.TryGetDashes(document, category, out var dashes)
                ? dashes.Select(pt => pt * ptToMm).ToArray() : [];
            ElevationPrinter.DrawDatumDashes(page, start.X, start.Y, end.X, end.Y, pattern);

            (double X, double Y) DrawEnd((double X, double Y) point, int side, bool show,
                TfBlockDeserializer.DatumLeader? leader)
            {
                var endOfLine = (X: ToX(point.X), Y: ToY(point.Y));
                if (!show) return endOfLine;
                var anchor = endOfLine;
                if (leader != null)
                {
                    endOfLine = (ToX(leader.End[0] * FeetToMillimetres), ToY(leader.End[1] * FeetToMillimetres));
                    anchor = (ToX(leader.Anchor[0] * FeetToMillimetres), ToY(leader.Anchor[1] * FeetToMillimetres));
                    double x = ToX(leader.Elbow[0] * FeetToMillimetres), y = ToY(leader.Elbow[1] * FeetToMillimetres);
                    page.DrawLine(endOfLine.X, endOfLine.Y, x, y, 0.1);
                    page.DrawLine(x, y, anchor.X, anchor.Y, 0.1);
                }
                double cx = anchor.X + side * dx * grid.Radius, cy = anchor.Y + side * dy * grid.Radius;
                page.DrawPolyline(ElevationPrinter.DatumCircle(cx, cy, grid.Radius, 25), 0.1, true);
                if (page.Owner.Font != null || grid.Name.All(c => c is >= ' ' and <= '~'))
                    page.DrawText(grid.Name, cx, cy - 2.17, 18 * ptToMm, PdfTextAlignment.Center);
                return endOfLine;
            }
        }
    }
}
