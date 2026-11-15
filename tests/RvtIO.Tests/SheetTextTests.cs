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

    [SkippableFact]
    public void Render_DrawsTheSheetTextAtItsPosition()
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
            Text = "Лист",
            TextPosition = [20 / 304.8, 20 / 304.8, 0],
            TextFontSizePoints = 11,
        };
        var info = new RevitFileInfo { Elements = { sheet, border, note } };

        PdfDocument pdf = SheetPrinter.Render(RvtDocument.FromInfo(info), 100);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string content = PdfTestContent.Read(stream.ToArray());

        Assert.Contains("/Type0", content);
        Assert.Contains("BT /F2", content);
        Assert.Contains(" Tj ET", content);
    }
}
