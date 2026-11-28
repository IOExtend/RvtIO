using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class PlanPrinter
{
    internal static void DrawPlanNotes(PdfPage page, RvtDocument document, int viewId, ViewCropFrame crop,
        PlanContent content, double scale, double originX, double originY)
    {
        if (page.Owner.Font == null || !crop.Right.SequenceEqual(new double[] { 1, 0, 0 })
            || !crop.Up.SequenceEqual(new double[] { 0, 1, 0 })
            || document.RawData.PlanNotes?.GetValueOrDefault(viewId) is not { Count: > 0 } notes) return;

        double X(double feet) => (feet * FeetToMillimetres - content.MinX) / scale + originX;
        double Y(double feet) => (feet * FeetToMillimetres - content.MinY) / scale + originY;

        foreach (var note in notes)
            DrawTextNote(page, document, note, X, Y);
    }

    internal static bool DrawTextNote(PdfPage page, RvtDocument document, RevitFileInfo.PlanNote note,
        Func<double, double> toX, Func<double, double> toY)
    {
        if (page.Owner.Font is not { } font) return false;
        (double X, double Y) Point(double[] feet) => (toX(feet[0]), toY(feet[1]));
        double width = note.WidthFeet * FeetToMillimetres, height = note.HeightFeet * FeetToMillimetres;
        double padding = note.PaddingFeet * FeetToMillimetres;
        double x = toX(note.X), y = toY(note.Y);
        double pen = PlanAnnotationWidth(document, note.CategoryId, 0.1);
        foreach (var leader in note.Leaders)
            DrawPlanNoteLeader(page, Point(leader.End), Point(leader.Elbow), Point(leader.Anchor),
                note.ArcLeaders, note.ArrowSizeFeet * FeetToMillimetres, note.ArrowAngle, pen);
        if (note.Opaque)
            page.DrawRectangle(x, y - height, width, height, 0, 1);
        if (note.HasBorder)
            page.DrawRectangle(x - padding, y - height - padding, width + 2 * padding, height + 2 * padding, pen);

        double size = note.FontPoints * 25.4 / 72;
        var lines = WrapPlanNoteText(page, note.Text, size, width, note.Wrap);
        double baseline = y - size * font.Ascent / font.UnitsPerEm;
        for (int i = 0; i < lines.Count; i++)
            page.DrawText(lines[i], x, baseline - i * height / lines.Count, size);
        return true;
    }

    internal static IReadOnlyList<string> WrapPlanNoteText(PdfPage page, string text, double size, double width, bool wrap)
    {
        var paragraphs = text.Split('\n');
        if (!wrap) return paragraphs;

        var lines = new List<string>();
        foreach (string paragraph in paragraphs)
        {
            if (page.MeasureText(paragraph, size) <= width)
            {
                lines.Add(paragraph);
                continue;
            }
            string line = "";
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                // The space after a word counts toward the saved wrapping width.
                string token = i == words.Length - 1 ? words[i] : words[i] + " ";
                if (line.Length > 0 && page.MeasureText(line + token, size) > width)
                {
                    lines.Add(line.TrimEnd());
                    line = token;
                }
                else line += token;
            }
            lines.Add(line.TrimEnd());
        }
        return lines;
    }

    private static void DrawPlanNoteLeader(PdfPage page, (double X, double Y) end, (double X, double Y) elbow,
        (double X, double Y) anchor, bool arc, double arrowSize, double arrowAngle, double pen)
    {
        double ux = elbow.X - end.X, uy = elbow.Y - end.Y;
        double vx = anchor.X - end.X, vy = anchor.Y - end.Y;
        double det = 2 * (ux * vy - uy * vx);
        double direction = Math.Atan2(uy, ux);
        if (arc && Math.Abs(det) > 1e-8)
        {
            double u = ux * ux + uy * uy, v = vx * vx + vy * vy;
            double cx = (u * vy - v * uy) / det, cy = (ux * v - vx * u) / det;
            double radius = Math.Sqrt(cx * cx + cy * cy);
            double start = Math.Atan2(-cy, -cx), through = Math.Atan2(uy - cy, ux - cx);
            double finish = Math.Atan2(vy - cy, vx - cx);
            double sweep = (finish - start + 2 * Math.PI) % (2 * Math.PI);
            if ((through - start + 2 * Math.PI) % (2 * Math.PI) > sweep) sweep -= 2 * Math.PI;
            page.DrawArc(end.X + cx, end.Y + cy, radius, start * 180 / Math.PI, (start + sweep) * 180 / Math.PI, pen);
            direction = start + Math.CopySign(Math.PI / 2, sweep);
        }
        else
        {
            page.DrawLine(end.X, end.Y, elbow.X, elbow.Y, pen);
            page.DrawLine(elbow.X, elbow.Y, anchor.X, anchor.Y, pen);
        }

        if (arrowSize <= 0) return;
        double a = direction - arrowAngle / 2, b = direction + arrowAngle / 2;
        page.DrawLine(end.X, end.Y, end.X + arrowSize * Math.Cos(a), end.Y + arrowSize * Math.Sin(a), pen);
        page.DrawLine(end.X, end.Y, end.X + arrowSize * Math.Cos(b), end.Y + arrowSize * Math.Sin(b), pen);
    }
}
