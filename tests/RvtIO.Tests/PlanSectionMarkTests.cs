using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The section marks of a plan (PlanPrinter): the trace of a section plane across the drawing with a bubble at
/// both ends, so a plan shows where it is cut.
/// </summary>
public class PlanSectionMarkTests
{
    [Fact]
    public void DrawContent_DrawsTheTraceOfASectionPlane()
    {
        var content = new PlanPrinter.PlanContent(
            [], [], [], [], [], [], [], [], [], [], [], 0, 0, 10000, 10000);
        var marks = new List<SectionMark>
        {
            new([(5000, 5000), (5001, 5000)], false, "Building Section"),
        };

        int withMarks = LineCount(content, marks);
        int without = LineCount(content, null);

        // The trace adds its line, two ticks and two bubbles (circles drawn as curves, so a line count grows).
        Assert.True(withMarks > without);
    }

    private static int LineCount(PlanPrinter.PlanContent content,
        IReadOnlyList<SectionMark>? marks)
    {
        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _) };
        PdfPage page = pdf.AddPage(PdfPageSize.A3);
        PlanPrinter.DrawContent(page, content, 100, 20, 20, 0.9, marks);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string text = PdfTestContent.Read(stream.ToArray());
        int count = 0;
        for (int at = text.IndexOf(" l ", StringComparison.Ordinal); at >= 0;
            at = text.IndexOf(" l ", at + 3, StringComparison.Ordinal))
            count++;
        return count;
    }
}
