using System.Text;
using RvtIO.Pdf;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The embedded TrueType font (src/RvtIO/Pdf/PdfTrueTypeFont): cmap lookup, advances and the Type0 objects
/// written into the PDF. The tests need a TrueType font on the machine and are skipped otherwise.</summary>
public class PdfTrueTypeFontTests
{
    private static PdfTrueTypeFont Font() =>
        PdfFonts.TryFind(out _) ?? throw new SkipException("No TrueType font was found on this machine.");

    [SkippableFact]
    public void Font_ParsesTheMetrics()
    {
        PdfTrueTypeFont font = Font();

        Assert.True(font.UnitsPerEm > 0);
        Assert.True(font.GlyphOf('A') > 0);
        Assert.True(font.GlyphOf('А') > 0); // Cyrillic A
        Assert.True(font.AdvanceOf(font.GlyphOf('A')) > 0);
        Assert.False(string.IsNullOrWhiteSpace(font.Name));
    }

    [SkippableFact]
    public void Document_EmbedsTheFontAndWritesCyrillicAsGlyphIds()
    {
        PdfTrueTypeFont font = Font();
        var document = new PdfDocument { Font = font };
        document.AddPage(PdfPageSize.A4).DrawText("Лист 1", 10, 10, 5);

        using var stream = new MemoryStream();
        document.Save(stream);
        string pdf = Encoding.ASCII.GetString(stream.ToArray());

        Assert.Contains("/Type0", pdf);
        Assert.Contains("/Identity-H", pdf);
        Assert.Contains("/ToUnicode", pdf);
        Assert.Contains("/FontFile2", pdf);
        Assert.Contains("/F2", pdf);
        Assert.Contains(" Tj ET", pdf);
        // Each character is a four-digit glyph id; six characters make 24 hex digits.
        Assert.Matches("<[0-9A-F]{24}> Tj", pdf);
    }

    [SkippableFact]
    public void Document_ScalesTheFontInPoints()
    {
        var document = new PdfDocument { Font = Font() };
        document.AddPage(PdfPageSize.A4).DrawText("A", 0, 0, heightMillimetres: 10);

        using var stream = new MemoryStream();
        document.Save(stream);
        string pdf = Encoding.ASCII.GetString(stream.ToArray());

        Assert.Contains("BT /F2 28.3465 Tf", pdf);
    }

    [Fact]
    public void Document_WithoutAFont_StillUsesHelvetica()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).DrawText("A101", 10, 10);

        using var stream = new MemoryStream();
        document.Save(stream);
        string pdf = Encoding.ASCII.GetString(stream.ToArray());

        Assert.Contains("BT /F1", pdf);
        Assert.DoesNotContain("/Type0", pdf);
    }
}
