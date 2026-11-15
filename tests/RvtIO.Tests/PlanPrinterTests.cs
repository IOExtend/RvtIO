using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The first plan renderer (src/RvtIO/Print): level selection, wall and column drawing, grid clipping, the
/// paper chosen for the drawing, and the errors of a level with nothing to draw.</summary>
public class PlanPrinterTests
{
    [Fact]
    public void CollectLevel_ReadsTheRoomFootprints()
    {
        var room = new Element { Id = 700, Name = "Living", CategoryId = (int)BuiltInCategory.OST_Rooms };
        Add(room, BuiltInParameter.ROOM_LEVEL_ID, StorageType.ElementId, 500);
        Add(room, BuiltInParameter.ROOM_AREA, StorageType.Double, 200.0);
        room.SerializedSpaceBoundary = new GeometryData
        {
            Faces =
            [
                new GeoFace
                {
                    Normal = [0, 0, 1],
                    OuterVertices = [[0, 0, 0], [5, 0, 0], [5, 4, 0], [0, 4, 0]],
                },
            ],
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Level 1", 0), Wall(600, 500, 0, 0, 10, 0), room },
        });

        var content = PlanPrinter.CollectLevel(document, 500);

        Assert.NotNull(content);
        var footprint = Assert.Single(content!.Rooms);
        Assert.Equal("Living", footprint.Name);
        Assert.Equal(5 * 304.8 * 4 * 304.8, Math.Abs(Area(footprint.Polygon)), 3);
    }

    private static double Area(List<(double X, double Y)> points)
    {
        double sum = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var (x1, y1) = points[i];
            var (x2, y2) = points[(i + 1) % points.Count];
            sum += x1 * y2 - x2 * y1;
        }
        return sum / 2;
    }

    private static void Add(Element element, BuiltInParameter id, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, id.ToString(), storage, value));

    private static Element Level(int id, string name, double elevationFeet)
    {
        var level = new Element { Id = id, Name = name, CategoryId = (int)BuiltInCategory.OST_Levels };
        Add(level, BuiltInParameter.LEVEL_ELEV, StorageType.Double, elevationFeet);
        return level;
    }

    private static Element Wall(int id, int levelId, double x1, double y1, double x2, double y2, double widthFeet = 0.656168)
    {
        var wall = new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [x1, y1, 0],
                EndPoint = [x2, y2, 0],
            },
        };
        Add(wall, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, levelId);
        Add(wall, BuiltInParameter.WALL_ATTR_WIDTH_PARAM, StorageType.Double, widthFeet);
        return wall;
    }

    private static Element Column(int id, int levelId, double minX, double minY, double maxX, double maxY)
    {
        var column = new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Columns,
            BoundingBox = [minX, minY, 0, maxX, maxY, 10],
        };
        Add(column, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, levelId);
        return column;
    }

    private static Element Grid(int id, double x1, double y1, double x2, double y2) => new()
    {
        Id = id,
        CategoryId = (int)BuiltInCategory.OST_Grids,
        Location = new LocationCurve { CurveType = "Line", StartPoint = [x1, y1, 0], EndPoint = [x2, y2, 0] },
    };

    private static RvtDocument Document(params Element[] elements) =>
        RvtDocument.FromInfo(new RevitFileInfo { Elements = elements.ToList() });

    [Fact]
    public void Levels_ListsOnlyLevelsWithADrawableWallOrColumn()
    {
        var document = Document(
            Level(1, "First", 0), Level(2, "Second", 10), Level(3, "Empty", 20),
            Wall(100, 1, 0, 0, 10, 0), Wall(101, 1, 10, 0, 10, 5), Wall(102, 2, 0, 0, 3, 0),
            new Element { Id = 200, CategoryId = (int)BuiltInCategory.OST_Walls });

        var levels = PlanPrinter.Levels(document);

        Assert.Equal(new[] { 1, 2 }, levels.Select(level => level.LevelId));
        Assert.Equal("First", levels[0].Name);
        Assert.Equal(0d, levels[0].ElevationFeet);
    }

    [Fact]
    public void Render_DrawsTheLevelWithTheMostWallsByDefault()
    {
        var document = Document(
            Level(1, "First", 0), Level(2, "Second", 10),
            Wall(100, 1, 0, 0, 10, 0),
            Wall(101, 2, 0, 0, 10, 0), Wall(102, 2, 10, 0, 10, 5));

        PdfDocument pdf = PlanPrinter.Render(document);

        Assert.Single(pdf.Pages);
        // 10 m at 1:100 is 100 mm, which fits A4 landscape with a 10 mm margin.
        Assert.Equal(PdfPageSize.A4.Landscape, pdf.Pages[0].Size);
    }

    [Fact]
    public void Render_DrawsTwoSidesOfAWallAndItsEndCaps()
    {
        var document = Document(Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0, widthFeet: 1));

        PdfDocument pdf = PlanPrinter.Render(document, levelId: 1);
        string content = Content(pdf);

        // 10 m long, 1 ft = 304.8 mm wide: two sides 304.8 mm apart and two end caps.
        Assert.Equal(4, Count(content, " l S"));
    }

    [Fact]
    public void Render_CutsAWallAtAHostedDoor()
    {
        var wall = Wall(100, 1, 0, 0, 10, 0, widthFeet: 1);
        var door = new Element
        {
            Id = 200,
            CategoryId = (int)BuiltInCategory.OST_Doors,
            HostId = 100,
            OwnSingleFamilyCutout = (300, 100, new double[]
                {
                    1, 0, 0,
                    0, 1, 0,
                    0, 0, 1,
                    4.5, 0, 0,
                },
                new FamilyCutLoopData(
                    new[] { new[] { -0.5, 0.0, 0.0 }, new[] { 0.5, 0.0, 0.0 },
                            new[] { 0.5, 0.0, 3.0 }, new[] { -0.5, 0.0, 3.0 } },
                    new[] { 0.0, 1.0, 0.0 }, 0)),
        };
        var document = Document(Level(1, "First", 0), wall, door);

        PdfDocument pdf = PlanPrinter.Render(document, levelId: 1);
        string content = Content(pdf);

        // The 10 m wall is cut into two stretches of 4 m and 5 m, each with two sides and two caps: eight lines where an
        // uncut wall draws four.
        Assert.Equal(8, Count(content, " l S"));
    }

    [Fact]
    public void Render_DrawsAColumnAsAFilledRectangle()
    {
        var document = Document(Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0), Column(200, 1, 2, 2, 2.5, 2.5));

        PdfDocument pdf = PlanPrinter.Render(document, levelId: 1);
        string content = Content(pdf);

        Assert.Contains(" re B", content);
    }

    [Fact]
    public void Render_ClipsAGridThatRunsFarBeyondTheLevel()
    {
        var document = Document(
            Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0),
            Grid(300, -1000, 0, 1000, 0));

        PdfDocument pdf = PlanPrinter.Render(document, levelId: 1);
        string content = Content(pdf);

        // The grid is drawn (one more line), clipped to the level's extent; the page stays the one the wall alone
        // would choose and does not grow to the kilometres the raw grid line spans.
        Assert.Equal(5, Count(content, " l S"));
        Assert.Equal(PdfPageSize.A4.Landscape, pdf.Pages[0].Size);
    }

    [Fact]
    public void Render_UsesTheGivenPaperAndScale()
    {
        var document = Document(Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0));

        PdfDocument pdf = PlanPrinter.Render(document, 1, scale: 200, new PlanPrintOptions { PaperSize = PdfPageSize.A3 });
        string content = Content(pdf);

        Assert.Equal(PdfPageSize.A3, pdf.Pages[0].Size);
        Assert.Contains("/MediaBox [0 0 841.8898 1190.5512]", content);
    }

    [Fact]
    public void Render_ThrowsWhenTheLevelHasNoDrawableElement()
    {
        var document = Document(Level(1, "First", 0), new Element { Id = 5, CategoryId = (int)BuiltInCategory.OST_Walls });

        var error = Assert.Throws<InvalidOperationException>(() => PlanPrinter.Render(document));

        Assert.Contains("no level", error.Message);
    }

    [Fact]
    public void Render_RejectsANegativeScale()
    {
        var document = Document(Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => PlanPrinter.Render(document, 1, scale: 0));
    }

    private static string Content(PdfDocument pdf)
    {
        using var stream = new MemoryStream();
        pdf.Save(stream);
        return PdfTestContent.Read(stream.ToArray());
    }

    private static int Count(string text, string value)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(value, at, StringComparison.Ordinal)) >= 0) { count++; at += value.Length; }
        return count;
    }
}
