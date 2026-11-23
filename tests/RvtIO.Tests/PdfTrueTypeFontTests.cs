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

    [SkippableFact]
    public void Document_WiresTheRegularAndBoldFontsThroughThePageResourcesAndTheCrossReference()
    {
        byte[] file = Font().File_;
        // A macOS style name with a space, on both fonts, so the names are escaped in every dictionary of each.
        var regular = new PdfTrueTypeFont(file, "Arial Regular");
        var bold = new PdfTrueTypeFont(file, "Arial Bold");
        var document = new PdfDocument { Font = regular, BoldFont = bold };
        var page = document.AddPage(PdfPageSize.A4);
        page.DrawText("A", 10, 10);
        page.DrawStyledText("B", 30, 30, 5, PdfTextAlignment.Center, 0, true, new PdfColor(.9, .9, .9));

        // The table is checked object by object: every offset must point at the object it names.
        var objects = PdfObjects.Write(document);

        string pageObject = objects[objects.IdOf("/Type /Page /Parent")];
        int helvetica = objects.Reference(pageObject, "/F1 ");
        int regularId = objects.Reference(pageObject, "/F2 ");
        int boldId = objects.Reference(pageObject, "/F3 ");
        Assert.Equal(3, new[] { helvetica, regularId, boldId }.Distinct().Count());
        Assert.StartsWith("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica", objects[helvetica]);
        foreach (var (id, name, characters) in new[] { (regularId, "Arial#20Regular", "A"), (boldId, "Arial#20Bold", "B") })
        {
            // Type0 font, its CID font, the descriptor, the font file and the character map each in an object of its own.
            string type0 = objects[id];
            Assert.StartsWith($"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H", type0);
            string cid = objects[objects.Reference(type0, "/DescendantFonts ")];
            Assert.StartsWith($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} ", cid);
            string descriptor = objects[objects.Reference(cid, "/FontDescriptor ")];
            Assert.StartsWith($"<< /Type /FontDescriptor /FontName /{name} ", descriptor);
            string fontFile = objects[objects.Reference(descriptor, "/FontFile2 ")];
            Assert.StartsWith($"<< /Length {file.Length} /Length1 {file.Length} >>\nstream\n", fontFile);
            string toUnicode = objects[objects.Reference(type0, "/ToUnicode ")];
            Assert.Contains($"<{(id == regularId ? regular : bold).GlyphOf(characters[0]):X4}> <00{(int)characters[0]:X2}>", toUnicode);
        }
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
