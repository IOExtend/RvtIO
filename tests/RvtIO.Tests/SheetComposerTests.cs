using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The sheet composer (src/RvtIO/Print/SheetComposer): the page from the title block extent, the viewports
/// placed at their stored offset and the plan drawn for them.</summary>
public class SheetComposerTests
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

    private static Element TitleBlock(int id, int owner, int typeId) => new()
    {
        Id = id,
        CategoryId = (int)BuiltInCategory.OST_TitleBlocks,
        SerializedOwnerDbViewId = owner,
        TypeId = typeId,
    };

    private static Element TitleBlockType(int id) => new()
    {
        Id = id,
        CategoryId = (int)BuiltInCategory.OST_TitleBlocks,
        BoundingBox = [0, 0, 0, 841 / FeetToMillimetres, 594 / FeetToMillimetres, 0],
    };

    private static Element Viewport(int id, int owner, int viewId, double x, double y) => new()
    {
        Id = id,
        CategoryId = (int)BuiltInCategory.OST_Viewports,
        SerializedOwnerDbViewId = owner,
        ViewportViewId = viewId,
        ViewportOffset = [x / FeetToMillimetres, y / FeetToMillimetres, 0],
    };

    private static Element View(int id, string name, int levelId)
    {
        var view = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Views, NativeRecordClass = 1005 };
        Add(view, BuiltInParameter.VIEW_NAME, StorageType.String, name);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 100);
        Add(view, BuiltInParameter.LEVEL_ELEV, StorageType.Double, 0.0);
        return view;
    }

    private static Element Level(int id, string name) 
    {
        var level = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Levels, Name = name };
        Add(level, BuiltInParameter.LEVEL_ELEV, StorageType.Double, 0.0);
        return level;
    }

    private static Element Wall(int id, int levelId, double x1, double y1, double x2, double y2)
    {
        var wall = new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [x1 / FeetToMillimetres, y1 / FeetToMillimetres, 0],
                EndPoint = [x2 / FeetToMillimetres, y2 / FeetToMillimetres, 0],
            },
        };
        Add(wall, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, levelId);
        Add(wall, BuiltInParameter.WALL_ATTR_WIDTH_PARAM, StorageType.Double, 200 / FeetToMillimetres);
        return wall;
    }

    private static RvtDocument Document(params Element[] elements) =>
        RvtDocument.FromInfo(new RevitFileInfo { Elements = elements.ToList() });

    [Fact]
    public void Sheets_ReadsThePageFromTheTitleBlockExtent()
    {
        var document = Document(
            Sheet(100, "A101", "Plans"), TitleBlockType(300), TitleBlock(301, 100, 300));

        ComposedSheet sheet = Assert.Single(SheetComposer.Sheets(document));

        Assert.Equal(100, sheet.SheetId);
        Assert.Equal("A101", sheet.Number);
        Assert.Equal("Plans", sheet.Name);
        Assert.Equal(PdfPageSize.A1.Landscape.WidthMillimetres, sheet.Page.WidthMillimetres);
        Assert.Equal(594, sheet.Page.HeightMillimetres, 0);
    }

    [Fact]
    public void Sheets_ListsOnlyNumberedSheets()
    {
        var unnamed = new Element { Id = 101, CategoryId = (int)BuiltInCategory.OST_Sheets };
        Add(unnamed, BuiltInParameter.SHEET_NAME, StorageType.String, "No number");
        var document = Document(Sheet(100, "A101", "Plans"), unnamed, TitleBlockType(300), TitleBlock(301, 100, 300));

        Assert.Single(SheetComposer.Sheets(document));
    }

    [Fact]
    public void Render_UsesTheTitleBlockPaper()
    {
        var document = Document(
            Sheet(100, "A101", "Plans"), TitleBlockType(300), TitleBlock(301, 100, 300));

        PdfDocument pdf = SheetComposer.Render(document, 100);

        PdfPage page = Assert.Single(pdf.Pages);
        Assert.Equal(841, page.Size.WidthMillimetres, 0);
        Assert.Equal(594, page.Size.HeightMillimetres, 0);
    }

    [Fact]
    public void Render_DrawsTheViewportPlanAndItsLabel()
    {
        var document = Document(
            Sheet(100, "A101", "Plans"),
            TitleBlockType(300), TitleBlock(301, 100, 300),
            View(400, "Level 1", 500), Level(500, "Level 1"),
            Wall(600, 500, 0, 0, 10000, 0), Wall(601, 500, 0, 0, 0, 8000),
            Viewport(700, 100, 400, 300, 300));

        PdfDocument pdf = SheetComposer.Render(document, 100);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string content = System.Text.Encoding.ASCII.GetString(stream.ToArray());

        Assert.Contains(" l S", content);
        Assert.Contains(" re S", content);
        if (PdfFonts.TryFind(out _) != null)
        {
            Assert.Contains("BT /F2", content);
            Assert.Contains(" Tj ET", content);
        }
    }

    [Fact]
    public void Render_RejectsAnElementThatIsNotANumberedSheet()
    {
        var document = Document(TitleBlockType(300));

        var error = Assert.Throws<InvalidOperationException>(() => SheetComposer.Render(document, 300));
        Assert.Contains("not a numbered sheet", error.Message);
    }
}
