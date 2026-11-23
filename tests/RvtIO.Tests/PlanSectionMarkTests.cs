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

    [Fact]
    public void DrawContent_InksTracesWithTheSectionsProjectionPen()
    {
        // A section trace inks thin like the reference (0.283 pt, annotation pen 1) instead of the old
        // hardcoded 0.22 mm that inked every trace heavy.
        var content = new PlanPrinter.PlanContent(
            [], [], [], [], [], [], [], [], [], [], [], 0, 0, 10000, 10000);
        var marks = new List<SectionMark>
        {
            new([(5000, 5000), (5001, 5000)], false, "Building Section"),
        };

        string text;
        using (var stream = new MemoryStream())
        {
            Drawn(content, marks).Save(stream);
            text = PdfTestContent.Read(stream.ToArray());
        }

        Assert.Contains("0.2835 w", text);
        Assert.DoesNotContain("0.6236 w", text);
    }


    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DrawContent_UsesPlacedViewNumbersForTheSavedFilledSectionMark(bool knownFamily, bool jogged)
    {
        const int planId = 30, sectionId = 31, typeId = 32, tagId = 33, headId = 34, tailId = 35, sheetId = 36;
        double tip = 0.02 * Math.Sqrt(2);
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            {
                new Element { Id = planId, ViewCrop = new(0, 0, 0, [1, 0, 0], [0, 1, 0], [0, 0, 1], -20, -20, 20, 20, true) },
                new Element { Id = sectionId, TypeId = typeId, ViewDirection = jogged ? [1, 0, 0] : [0, 1, 0] },
                new Element { Id = typeId, Parameters = { Param(BuiltInParameter.SECTION_TAG, tagId, StorageType.ElementId) } },
                new Element { Id = headId, FamilyName = knownFamily ? "M_Section Head - Filled" : "Custom",
                    BoundingBox = [-0.02 - tip, -0.02, 0, -0.02 + tip, tip, 0] },
                new Element { Id = tailId, FamilyName = "M_Section Tail - Filled", BoundingBox = [-0.007, 0, 0, 0, 0.03, 0] },
                new Element { Id = sheetId, Parameters = { Param(BuiltInParameter.SHEET_NUMBER, "S07", StorageType.String) } },
                new Element { Id = 38, ViewportViewId = sectionId },
                new Element { Id = 37, ViewportViewId = sectionId, SerializedOwnerDbViewId = sheetId,
                    Parameters = { Param(BuiltInParameter.VIEWPORT_DETAIL_NUMBER, "42", StorageType.String) } },
            },
            SectionMarkerSymbols = new Dictionary<int, (int Head, int Tail)> { [tagId] = (headId, tailId) },
            TitleBlockLabels = new Dictionary<int, IReadOnlyList<TfBlockDeserializer.TitleBlockLabel>>
            {
                [headId] =
                [
                    new(-0.02, 0.009, 0, 0, 1, 1, 0, 0.01, 0.08, 1, 0x480,
                        (int)BuiltInParameter.VIEWER_DETAIL_NUMBER, "", ""),
                    new(-0.02, -0.009, 0, 0, 1, 1, 0, 0.01, 0.08, 1, 0x480,
                        (int)BuiltInParameter.VIEWER_SHEET_NUMBER, "", ""),
                ],
            },
        });
        var content = new PlanPrinter.PlanContent([], [], [], [], [], [], [], [], [], [], [], 0, 0, 10000, 10000);
        var font = PdfFonts.TryFind(out _) ?? throw new SkipException("No TrueType font was found on this machine.");
        var page = new PdfDocument { Font = font }.AddPage(PdfPageSize.A3);
        SectionMark mark = jogged
            ? new([(3000, 9000), (3000, 6000), (4000, 6000), (4000, 1000)], true, "Section", sectionId, true)
            : new([(1000, 3000), (9000, 3000)], true, "Longitudinal Section", sectionId);
        PlanPrinter.DrawContent(page, content, 100, 20, 20, sectionMarks: [mark], document: document, planViewId: planId);
        Assert.Equal(knownFamily, HasText("42"));
        Assert.Equal(knownFamily, HasText("S07"));
        Assert.Equal(!knownFamily, HasText("L"));

        bool HasText(string value) => page.Content.Contains(
            $"<{string.Concat(value.Select(character => font.GlyphOf(character).ToString("X4")))}> Tj",
            StringComparison.Ordinal);
        static Parameter Param(BuiltInParameter id, object value, StorageType storage) =>
            new() { Id = (int)id, Definition = new() { Id = (int)id }, StorageType = storage, Value = value };
    }

    private static PdfDocument Drawn(PlanPrinter.PlanContent content,
        IReadOnlyList<SectionMark>? marks)
    {
        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _) };
        PdfPage page = pdf.AddPage(PdfPageSize.A3);
        PlanPrinter.DrawContent(page, content, 100, 20, 20, 0.9, marks);
        return pdf;
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
