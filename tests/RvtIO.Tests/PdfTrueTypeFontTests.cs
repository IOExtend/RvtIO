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

    [SkippableFact]
    public void Document_EmbedsBoldTextWithItsOwnCharacterMap()
    {
        var bold = PdfFonts.TryFindBold() ?? throw new SkipException("No bold TrueType font was found on this machine.");
        var document = new PdfDocument { Font = Font(), BoldFont = bold };
        var page = document.AddPage(PdfPageSize.A4);
        page.DrawText("A", 10, 10);
        page.DrawStyledText("?", 30, 30, 5, PdfTextAlignment.Center, 0, true, new PdfColor(.9, .9, .9));

        using var stream = new MemoryStream();
        document.Save(stream);
        string pdf = Encoding.ASCII.GetString(stream.ToArray());

        Assert.Contains("BT /F2", pdf);
        Assert.Contains("0.9 0.9 0.9 rg", pdf);
        Assert.Contains("BT /F3", pdf);
        Assert.Contains($"<{bold.GlyphOf('?'):X4}> <003F>", pdf);
    }

    [SkippableFact]
    public void Document_EscapesEmbeddedFontNamesInAllFontDictionaries()
    {
        var font = new PdfTrueTypeFont(Font().File_, "Arial Bold/#é");
        var document = new PdfDocument { Font = font };
        document.AddPage(PdfPageSize.A4).DrawText("A", 10, 10);
        using var stream = new MemoryStream();
        document.Save(stream);

        string pdf = Encoding.ASCII.GetString(stream.ToArray());
        const string name = "/Arial#20Bold#2F#23#C3#A9";
        Assert.Contains($"/Subtype /Type0 /BaseFont {name} /Encoding", pdf);
        Assert.Contains($"/Subtype /CIDFontType2 /BaseFont {name} /CIDSystemInfo", pdf);
        Assert.Contains($"/Type /FontDescriptor /FontName {name} /Flags", pdf);
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
