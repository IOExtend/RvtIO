using System.Globalization;
using System.Text.RegularExpressions;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The text notes on a printed sheet: the decoded text is drawn at its stored position with the font size the
/// RTF declares. The test needs a TrueType font and is skipped otherwise.</summary>
public class SheetTextTests
{
    private static void Add(Element element, BuiltInParameter id, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, id.ToString(), storage, value));

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_DrawsTheSheetTextAtItsPosition(bool nativeFrame)
    {
        if (PdfFonts.TryFind(out _) is null) throw new SkipException("No TrueType font was found on this machine.");
        var sheet = new Element { Id = 100, CategoryId = (int)BuiltInCategory.OST_Sheets };
        Add(sheet, BuiltInParameter.SHEET_NUMBER, StorageType.String, "A101");
        Add(sheet, BuiltInParameter.SHEET_NAME, StorageType.String, "План");
        var border = new Element
        {
            Id = 200,
            CategoryId = -2003100,
            SerializedOwnerDbViewId = 100,
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [0, 0, 0],
                EndPoint = [210 / 304.8, 0, 0],
            },
        };
        var note = new Element
        {
            Id = 300,
            CategoryId = (int)BuiltInCategory.OST_TextNotes,
            SerializedOwnerDbViewId = 100,
            Text = "Лист  A\n®",
            TextPosition = [20 / 304.8, 20 / 304.8, 0],
            TextFontSizePoints = 11,
        };
        var info = new RevitFileInfo { Elements = { sheet, border, note } };
        if (nativeFrame)
        {
            info.PlanNotes = new Dictionary<int, List<RevitFileInfo.PlanNote>>
            {
                [sheet.Id] = [new(note.Text!, 11, 20 / 304.8, 20 / 304.8, 80 / 304.8, 10 / 304.8,
                    true, false, false, 0, -2000300, false, [], 0, 0, note.Id)],
            };
        }

        PdfDocument pdf = SheetPrinter.Render(RvtDocument.FromInfo(info), 100,
            new SheetPrintOptions { PaperSize = new PdfPageSize(210, 100) });
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string content = PdfTestContent.Read(stream.ToArray());

        Assert.Contains("/Type0", content);
        var runs = Regex.Matches(pdf.Pages[0].Content, @"BT /F2 11 Tf ([\d.]+) ([\d.]+) Td <([0-9A-F]+)> Tj ET");
        Assert.Equal(2, runs.Count);
        var font = pdf.Font!;
        Assert.Equal(Glyphs("Лист  A"), runs[0].Groups[3].Value);
        Assert.Equal(Glyphs("®"), runs[1].Groups[3].Value);
        double firstBaseline = 70 * 72 / 25.4 - (nativeFrame ? 11d * font.Ascent / font.UnitsPerEm : 0);
        double spacing = nativeFrame ? 5 * 72 / 25.4 : 11 * 1.25;
        Assert.InRange(Math.Abs(Y(0) - firstBaseline), 0, 0.001);
        Assert.InRange(Math.Abs(Y(1) - firstBaseline + spacing), 0, 0.001);

        double Y(int i) => double.Parse(runs[i].Groups[2].Value, CultureInfo.InvariantCulture);
        string Glyphs(string text) => string.Concat(text.Select(c => font.GlyphOf(c).ToString("X4", CultureInfo.InvariantCulture)));
    }
}
