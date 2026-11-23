using System.Text;
using RvtIO.Pdf;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The PDF writer (src/RvtIO/Pdf): page sizes, content operators, the cross-reference table and the text
/// restriction of the standard font. The document is parsed back from its bytes; no viewer is needed.</summary>
public class PdfWriterTests
{
    private static string Write(PdfDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        return Encoding.ASCII.GetString(stream.ToArray());
    }

    [Fact]
    public void EmptyDocument_IsAValidSkeleton()
    {
        string pdf = Write(new PdfDocument());

        Assert.StartsWith("%PDF-1.4\n", pdf);
        Assert.Contains("/Type /Catalog", pdf);
        Assert.Contains("/Count 0", pdf);
        Assert.EndsWith("%%EOF\n", pdf);
        Assert.Contains("xref\n0 4\n", pdf);
    }

    [Fact]
    public void Page_UsesThePaperSizeInPoints()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4.Landscape);

        string pdf = Write(document);

        Assert.Contains("/Count 1", pdf);
        Assert.Contains("/MediaBox [0 0 841.8898 595.2756]", pdf);
    }

    [Fact]
    public void Line_IsStrokedWithItsMillimetreWidth()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPageSize.A4);
        page.DrawLine(10, 20, 30, 40, widthMillimetres: 0.5);

        string pdf = Write(document);

        // 0.5 mm = 1.4173 pt
        Assert.Contains("1.4173 w", pdf);
        Assert.Contains("28.3465 56.6929 m 85.0394 113.3858 l S", pdf);
    }

    [Fact]
    public void Rectangle_IsFilledAndStroked()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).DrawRectangle(5, 6, 7, 8, widthMillimetres: 0.25, fillGray: 0.5);

        string pdf = Write(document);

        Assert.Contains("0.5 g", pdf);
        Assert.Contains("14.1732 17.0079 19.8425 22.6772 re B", pdf);
    }

    [Fact]
    public void Polyline_ClosesAndFills()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4)
            .DrawPolyline([(0, 0), (10, 0), (10, 10)], widthMillimetres: 0.2, closed: true, fillGray: 1);

        string pdf = Write(document);

        Assert.Contains("0 0 m 28.3465 0 l 28.3465 28.3465 l h B", pdf);
    }

    [Fact]
    public void Polyline_FillsWithAColour()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4)
            .DrawPolyline([(0, 0), (10, 0), (10, 10)], 0, true, new PdfColor(0.2, 0.4, 0.6));

        string pdf = Write(document);

        Assert.Contains("0.2 0.4 0.6 rg", pdf);
        Assert.Contains("f\n", pdf);
    }

    [Fact]
    public void Text_IsDrawnWithTheStandardFontAndEscaped()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).DrawText("A101 (1)", 10, 20, heightMillimetres: 3.5);

        string pdf = Write(document);

        Assert.Contains("BT /F1 9.9213 Tf", pdf);
        Assert.Contains("A101 \\(1\\)", pdf);
    }

    [Fact]
    public void Text_AlignmentMovesTheAnchor()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPageSize.A4);
        page.DrawText("AAAA", 100, 50, heightMillimetres: 10, PdfTextAlignment.Center);
        page.DrawText("AAAA", 100, 50, heightMillimetres: 10, PdfTextAlignment.Right);

        string pdf = Write(document);

        // 10 mm font size; four characters at the 0.5 em width estimate = 20 mm wide
        Assert.Contains("255.1181 141.7323 Td (AAAA)", pdf);
        Assert.Contains("226.7717 141.7323 Td (AAAA)", pdf);
    }

    [Fact]
    public void RotatedText_WritesATextMatrix()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).DrawTextRotated("3000", 100, 50, 3, 90);

        string pdf = Write(document);

        Assert.Contains(" Tm ", pdf);
        Assert.Contains("0 1 -1 0", pdf);
    }

    [Fact]
    public void Text_RejectsCharactersOutsideTheStandardFont()
    {
        var document = new PdfDocument();
        PdfPage page = document.AddPage(PdfPageSize.A4);

        ArgumentException error = Assert.Throws<ArgumentException>(() => page.DrawText("Лист", 10, 10));
        Assert.Contains("U+041B", error.Message);
    }

    [Fact]
    public void Image_IsWrittenAsAnXObject()
    {
        var document = new PdfDocument();
        var image = new PdfRasterImage(2, 2, [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]);
        document.AddPage(PdfPageSize.A4).DrawImage(image, 10, 20, 30, 40);

        string pdf = Write(document);

        Assert.Contains("/Subtype /Image", pdf);
        Assert.Contains("/Width 2 /Height 2", pdf);
        Assert.Contains("/XObject", pdf);
        Assert.Contains("/Im0 Do", pdf);
    }

    [Fact]
    public void JpegImage_IsWrittenWithDctDecode()
    {
        // A one-pixel JPEG: the writer embeds it as it is, without decoding the picture.
        byte[] jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwc"
            + "KDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAA"
            + "AAAAAAAAAAD/2gAIAQEAAD8AKp//2Q==");
        var image = new PdfJpegImage(jpeg);
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).DrawImage(image, 10, 10, 20, 20);

        string pdf = Write(document);

        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(1, image.Components);
        Assert.Equal("/DeviceGray", image.ColorSpace);
        Assert.Contains("/Filter /DCTDecode", pdf);
        Assert.Contains("/Subtype /Image", pdf);
    }

    [Fact]
    public void Compress_WritesFlateStreamsThatShrinkTheFile()
    {
        static PdfDocument Build(bool compress)
        {
            var document = new PdfDocument { Compress = compress };
            PdfPage page = document.AddPage(PdfPageSize.A4);
            for (int i = 0; i < 500; i++)
                page.DrawLine(10, 10 + i * 0.1, 100, 10 + i * 0.1, 0.2);
            return document;
        }

        string raw = Write(Build(false));
        string packed = Write(Build(true));

        Assert.Contains("/Filter /FlateDecode", packed);
        Assert.True(packed.Length < raw.Length, $"{packed.Length} is not smaller than {raw.Length}");
    }

    [Fact]
    public void TwoPages_HaveTwoContentStreams()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).DrawLine(0, 0, 10, 10);
        document.AddPage(PdfPageSize.A3).DrawLine(0, 0, 20, 20);

        string pdf = Write(document);

        Assert.Contains("/Count 2", pdf);
        Assert.Contains("5 0 obj", pdf);
        Assert.Contains("7 0 obj", pdf);
    }

    [Fact]
    public void TitledPages_GetBookmarksPointingAtTheirPages()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4);
        document.AddPage(PdfPageSize.A4).OutlineTitle = "A102 - Plans";

        string pdf = Write(document);

        // One bookmark, pointing at the second page (object 6 without an embedded font).
        Assert.Contains("/Outlines", pdf);
        Assert.Contains("/Count 1", pdf);
        Assert.Contains("/Title (A102 - Plans)", pdf);
        Assert.Contains("/Dest [6 0 R /Fit]", pdf);
    }

    [Fact]
    public void NonAsciiTitle_IsWrittenAsUtf16()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).OutlineTitle = "А101 - План";

        string pdf = Write(document);

        Assert.Contains("<FEFF", pdf);
        Assert.DoesNotContain("/Title (", pdf);
    }

    [Fact]
    public void ParallelLineFill_ReusesItsTileAndKeepsPageOutlines()
    {
        var pdf = new PdfDocument();
        var page = pdf.AddPage(PdfPageSize.A4);
        page.OutlineTitle = "Stair detail";
        var polygon = new List<(double X, double Y)> { (0, 0), (50, 0), (50, 50), (0, 50) };
        var hole = new List<(double X, double Y)> { (10, 10), (10, 20), (20, 20), (20, 10) };
        page.FillWithParallelLines(polygon, 3, Math.PI / 2, 15, 0, 0.1);
        page.FillWithParallelLines([polygon, hole], 3, Math.PI / 2, 15, 0, 0.1);
        page.DrawText("A105", 30, 30);

        string bytes = Write(pdf);

        Assert.Single(pdf.LinePatterns);
        Assert.Contains("/Pattern << /P0 6 0 R >>", bytes);
        Assert.Contains("/XStep 8.5039 /YStep 2874.3307", bytes);
        Assert.Contains("0 g 4.1102 -1437.1654 0.2835 2874.3307 re f", bytes);
        Assert.Contains("q /Pattern cs /P0 scn", page.Content);
        Assert.Contains("h 28.3465 28.3465 m 28.3465 56.6929 l 56.6929 56.6929 l 56.6929 28.3465 l h f Q", page.Content);
        Assert.Contains("h f Q\n0 g\nBT /F1", page.Content);
        Assert.Contains("/Outlines 7 0 R", bytes);
        Assert.Contains("/Dest [4 0 R /Fit]", bytes);
    }

    [Fact]
    public void ClippedParallelLines_FillsOnlyTheSavedStripsThroughTheFaceClip()
    {
        var document = new PdfDocument();
        var page = document.AddPage(PdfPageSize.A4);
        List<(double X, double Y)> face = [(0, 0), (12, 0), (12, 10), (0, 10)];
        List<(double X, double Y)> hole = [(4, 2), (4, 8), (6, 8), (6, 2)];

        page.FillWithClippedParallelLines([face, hole], 3, Math.PI / 2, -1, 0, 0.2);

        Assert.Empty(document.LinePatterns);
        string content = page.Content.Replace(" -0 ", " 0 ");
        Assert.Contains("h W n\n0 g\n5.3858 0 m 5.9528 0 l 5.9528 28.3465 l 5.3858 28.3465 l h ", content);
        Assert.Contains("30.8976 0 m 31.4646 0 l 31.4646 28.3465 l 30.8976 28.3465 l h f\nQ\n", content);
        Assert.Contains("h 11.3386 5.6693 m 11.3386 22.6772 l 17.0079 22.6772 l 17.0079 5.6693 l h W n", page.Content);
    }

    [Fact]
    public void ClippedParallelLines_UsesATileForDensePatterns()
    {
        var document = new PdfDocument();
        var page = document.AddPage(PdfPageSize.A4);
        List<(double X, double Y)> face = [(0, 0), (80, 0), (80, 10), (0, 10)];

        page.FillWithClippedParallelLines([face], 0.01, Math.PI / 2, 0, 0, 0.002);

        Assert.Single(document.LinePatterns);
        Assert.Contains("q /Pattern cs /P0 scn", page.Content);
    }

    [Fact]
    public void DashedLineFill_UsesTheSavedPeriodAndSignedRowShift()
    {
        var pdf = new PdfDocument();
        var page = pdf.AddPage(PdfPageSize.A4);
        List<(double X, double Y)> polygon = [(0, 0), (50, 0), (50, 50), (0, 50)];
        page.FillWithLinePattern([polygon], new PdfLinePattern(3, 0, 0, 0, 0.2, -1, [2, 3]));

        string bytes = Write(pdf);

        Assert.Contains("/XStep 14.173228346 /YStep 8.503937008 /Matrix [1 0 -0.333333333 1 0 0]", bytes);
        Assert.Contains("/Pattern << /P0 6 0 R >>", bytes);
        Assert.Contains("q /Pattern cs /P0 scn", page.Content);
    }

    [Fact]
    public void Save_WritesACrossReferenceThatPointsAtEveryObject()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).DrawText("A1", 1, 1);
        string pdf = Write(document);

        int xrefAt = pdf.LastIndexOf("\nxref\n", StringComparison.Ordinal);
        Assert.True(xrefAt > 0);
        string[] lines = pdf[(xrefAt + 6)..].Split('\n');
        Assert.Equal("0 6", lines[0]);
        Assert.Equal("0000000000 65535 f ", lines[1]);
        for (int id = 1; id <= 5; id++)
        {
            long offset = long.Parse(lines[1 + id][..10], System.Globalization.CultureInfo.InvariantCulture);
            Assert.StartsWith($"{id} 0 obj", pdf[(int)offset..]);
        }
    }
}
