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
