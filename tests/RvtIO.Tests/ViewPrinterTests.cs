using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The mixed view printer (src/RvtIO/Print/ViewPrinter): a plan and an elevation in one document, and the
/// single-elevation page.</summary>
public class ViewPrinterTests
{
    private const double FeetToMillimetres = 304.8;

    private static void Add(Element element, BuiltInParameter id, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, id.ToString(), storage, value));

    private static Element PlanView(int id, string name, int scale)
    {
        var view = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Views, NativeRecordClass = 1005 };
        Add(view, BuiltInParameter.VIEW_NAME, StorageType.String, name);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, scale);
        Add(view, BuiltInParameter.LEVEL_ELEV, StorageType.Double, 0.0);
        return view;
    }

    private static Element ElevationView(int id, string name, int scale)
    {
        var view = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Views, NativeRecordClass = 1016 };
        Add(view, BuiltInParameter.VIEW_NAME, StorageType.String, name);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, scale);
        return view;
    }

    private static Element Level(int id, string name)
    {
        var level = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Levels, Name = name };
        Add(level, BuiltInParameter.LEVEL_ELEV, StorageType.Double, 0.0);
        return level;
    }

    private static Element Wall(int id, int levelId)
    {
        var wall = new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [0, 0, 0],
                EndPoint = [10, 0, 0],
            },
        };
        Add(wall, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, levelId);
        Add(wall, BuiltInParameter.WALL_ATTR_WIDTH_PARAM, StorageType.Double, 200 / FeetToMillimetres);
        return wall;
    }

    private static Element WallWithFace(int id)
    {
        var face = new GeoFace
        {
            Origin = [0, 0, 0],
            XVector = [1, 0, 0],
            YVector = [0, 0, 1],
            Normal = [0, 1, 0],
            OuterVertices = [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]],
        };
        return new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Geometry = new GeometryData { Faces = [face] },
        };
    }

    private static RvtDocument Document(params Element[] elements) =>
        RvtDocument.FromInfo(new RevitFileInfo { Elements = elements.ToList() });

    [Fact]
    public void Render_PrintsAPlanAndAnElevationAsTwoPages()
    {
        var document = Document(
            PlanView(312, "Level 1", 100), Level(500, "Level 1"), Wall(600, 500),
            ElevationView(98984, "South", 100), WallWithFace(700));

        PdfDocument pdf = ViewPrinter.Render(document, [312, 98984]);

        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public void Render_RejectsAViewThatIsNeitherPlanNorElevation()
    {
        var document = Document(Level(500, "Level 1"));

        var error = Assert.Throws<InvalidOperationException>(() => ViewPrinter.Render(document, [312]));
        Assert.Contains("not a printable plan or elevation", error.Message);
    }

    [Fact]
    public void ElevationRender_UsesThePaperAndDrawsTheFaces()
    {
        var document = Document(ElevationView(98984, "South", 100), WallWithFace(700));

        PdfDocument pdf = ElevationPrinter.Render(document, 98984);
        Assert.Single(pdf.Pages);
        Assert.Equal(PdfPageSize.A4.Landscape, pdf.Pages[0].Size);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        Assert.Contains(" l ", PdfTestContent.Read(stream.ToArray()));
    }
}
