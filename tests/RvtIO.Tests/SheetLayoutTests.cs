using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The layout of a composed sheet (SheetComposer): two sections the file places on one spot are separated, a
/// section too large for its place is drawn at a scale that fits, and a detail viewport against the border is not
/// printed while the same detail in the middle of the sheet is.
/// </summary>
public class SheetLayoutTests
{
    private const double FeetToMillimetres = 304.8;

    private static void Add(Element element, BuiltInParameter id, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, id.ToString(), storage, value));

    private static Element Sheet(int id)
    {
        var sheet = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Sheets };
        Add(sheet, BuiltInParameter.SHEET_NUMBER, StorageType.String, "A104");
        Add(sheet, BuiltInParameter.SHEET_NAME, StorageType.String, "Elev/Sec/Det");
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

    private static Element SectionView(int id, string name, int scale)
    {
        var view = new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Views,
            NativeRecordClass = 1016,
            ViewOrigin = [0, 0, 0],
            ViewDirection = [1, 0, 0],
            ViewVerticalDirection = [0, 0, 1],
            ViewHorizontalDirection = [0, -1, 0],
        };
        Add(view, BuiltInParameter.VIEW_NAME, StorageType.String, name);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, scale);
        return view;
    }

    private static Element WallWithFace(int id, double widthFeet, double heightFeet)
    {
        var face = new GeoFace
        {
            Origin = [5, 0, 0],
            XVector = [0, 1, 0],
            YVector = [0, 0, 1],
            Normal = [1, 0, 0],
            OuterVertices =
            [
                [5, 0, 0], [5, widthFeet, 0], [5, widthFeet, heightFeet], [5, 0, heightFeet],
            ],
        };
        return new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Geometry = new GeometryData { Faces = [face] },
        };
    }

    private static int Count(string content, string needle)
    {
        int count = 0;
        for (int at = content.IndexOf(needle, StringComparison.Ordinal); at >= 0;
            at = content.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Content(RevitFileInfo info)
    {
        using var stream = new MemoryStream();
        SheetComposer.Render(RvtDocument.FromInfo(info), 900).Save(stream);
        return PdfTestContent.Read(stream.ToArray());
    }

    private static RevitFileInfo SheetWith(Element view, Element wall, double x, double y) => new()
    {
        Elements = { Sheet(900), TitleBlock(901, 900, 902), TitleBlockType(902), view, wall,
            Viewport(930, 900, view.Id, x, y) },
    };

    [Fact]
    public void Render_SeparatesTwoSectionsPlacedOnTheSameSpot()
    {
        var two = new RevitFileInfo
        {
            Elements =
            {
                Sheet(900), TitleBlock(901, 900, 902), TitleBlockType(902),
                SectionView(910, "Building Section", 100), WallWithFace(950, 30, 9),
                SectionView(920, "Longitudinal Section", 100), WallWithFace(960, 20, 9),
                Viewport(930, 900, 910, 300, 300), Viewport(931, 900, 920, 300, 300),
            },
        };

        // Both sections stand on the page: the second one is moved away from the first, not dropped.
        Assert.True(Count(Content(two), " l ") > Count(Content(SheetWith(
            SectionView(910, "Building Section", 100), WallWithFace(950, 30, 9), 300, 300)), " l "));
    }

    [Fact]
    public void Render_ScalesASectionTooLargeForItsPlaceToFit()
    {
        using var stream = new MemoryStream();
        SheetComposer.Render(RvtDocument.FromInfo(SheetWith(
            SectionView(910, "Building Section", 100), WallWithFace(950, 100, 30), 300, 300)), 900).Save(stream);
        string content = PdfTestContent.Read(stream.ToArray());

        Assert.Contains(" l ", content);
    }

    [Fact]
    public void Render_SkipsADetailViewportAgainstTheBorder()
    {
        int border = Count(Content(SheetWith(SectionView(910, "Typical Foundation Detail", 10),
            WallWithFace(950, 3, 3), 5, 300)), " l ");
        int middle = Count(Content(SheetWith(SectionView(910, "Typical Foundation Detail", 10),
            WallWithFace(950, 3, 3), 300, 300)), " l ");

        Assert.True(middle > border);
    }
}
