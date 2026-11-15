using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The sheet printer (src/RvtIO/Print/SheetPrinter): the page size from the decoded linework, the lines drawn,
/// the small drawing frames filtered out and the errors.</summary>
public class SheetPrinterTests
{
    private const double FeetToMillimetres = 304.8;

    private static void Add(Element element, BuiltInParameter id, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, id.ToString(), storage, value));

    private static Element Sheet(int id, string number, string name)
    {
        var sheet = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Sheets };
        Add(sheet, BuiltInParameter.SHEET_NUMBER, StorageType.String, number);
        Add(sheet, BuiltInParameter.SHEET_NAME, StorageType.String, name);
        return sheet;
    }

    private static Element Line(int id, int owner, double x1, double y1, double x2, double y2)
    {
        var line = new Element
        {
            Id = id,
            CategoryId = -2003100,
            SerializedOwnerDbViewId = owner,
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [x1 / FeetToMillimetres, y1 / FeetToMillimetres, 0],
                EndPoint = [x2 / FeetToMillimetres, y2 / FeetToMillimetres, 0],
            },
        };
        return line;
    }

    private static RvtDocument Document(params Element[] elements) =>
        RvtDocument.FromInfo(new RevitFileInfo { Elements = elements.ToList() });

    [Fact]
    public void Sheets_ReportsTheExtentAndFiltersSmallFrames()
    {
        var document = Document(
            Sheet(100, "A101", "First"),
            Line(200, 100, 0, 0, 210, 0), Line(201, 100, 210, 0, 210, 297),
            Line(202, 100, 210, 297, 0, 297), Line(203, 100, 0, 297, 0, 0),
            Sheet(101, "X", "Small"),
            Line(210, 101, 0, 0, 20, 0), Line(211, 101, 20, 0, 20, 20));

        var sheets = SheetPrinter.Sheets(document);

        var sheet = Assert.Single(sheets);
        Assert.Equal(100, sheet.SheetId);
        Assert.Equal("A101", sheet.Number);
        Assert.Equal("First", sheet.Name);
        Assert.Equal(210, sheet.WidthMillimetres, 3);
        Assert.Equal(297, sheet.HeightMillimetres, 3);
        Assert.Equal(4, sheet.CurveCount);
    }

    [Fact]
    public void Render_UsesThePageTheLineworkSpans()
    {
        var document = Document(
            Sheet(100, "A101", "First"),
            Line(200, 100, 0, 0, 210, 0), Line(201, 100, 210, 0, 210, 297),
            Line(202, 100, 210, 297, 0, 297), Line(203, 100, 0, 297, 0, 0));

        PdfDocument pdf = SheetPrinter.Render(document, 100);

        PdfPage page = Assert.Single(pdf.Pages);
        Assert.Equal(PdfPageSize.A4, page.Size);
        Assert.Equal(297, page.Size.HeightMillimetres);
    }

    [Fact]
    public void Render_DrawsTheSheetLines()
    {
        var document = Document(
            Sheet(100, "A101", "First"),
            Line(200, 100, 0, 0, 210, 0), Line(201, 100, 0, 0, 0, 297),
            Line(202, 100, 0, 297, 210, 297), Line(203, 100, 210, 0, 210, 297));

        PdfDocument pdf = SheetPrinter.Render(document, 100);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string content = System.Text.Encoding.ASCII.GetString(stream.ToArray());

        int lines = 0, at = 0;
        while ((at = content.IndexOf(" l S", at, StringComparison.Ordinal)) >= 0) { lines++; at += 4; }
        Assert.Equal(4, lines);
    }

    [Fact]
    public void Render_PrintsSeveralSheetsAsSeveralPages()
    {
        var document = Document(
            Sheet(100, "A101", "First"), Sheet(101, "A102", "Second"),
            Line(200, 100, 0, 0, 210, 0), Line(201, 100, 0, 0, 0, 297),
            Line(202, 100, 0, 297, 210, 297), Line(203, 100, 210, 0, 210, 297),
            Line(210, 101, 0, 0, 210, 0), Line(211, 101, 0, 0, 0, 297),
            Line(212, 101, 0, 297, 210, 297), Line(213, 101, 210, 0, 210, 297));

        PdfDocument pdf = SheetPrinter.RenderSheets(document, [100, 101]);

        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public void Render_RejectsAnElementThatIsNotASheet()
    {
        var document = Document(Line(200, 0, 0, 0, 210, 0));

        var error = Assert.Throws<InvalidOperationException>(() => SheetPrinter.Render(document, 200));
        Assert.Contains("not a sheet", error.Message);
    }

    [Fact]
    public void Render_RejectsASheetWithoutLinework()
    {
        var document = Document(Sheet(100, "A101", "First"));

        var error = Assert.Throws<InvalidOperationException>(() => SheetPrinter.Render(document, 100));
        Assert.Contains("no decoded linework", error.Message);
    }
}
