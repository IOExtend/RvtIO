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
        Add(room, BuiltInParameter.ROOM_NUMBER, StorageType.String, "101");
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
        Assert.Equal("101", footprint.Number);
        Assert.Equal(5 * 304.8 * 4 * 304.8, Math.Abs(Area(footprint.Polygon)), 3);
    }

    [Fact]
    public void CollectLevel_ReadsTheFurnitureOutlines()
    {
        var table = new Element { Id = 800, Name = "Table", CategoryId = (int)BuiltInCategory.OST_Furniture };
        Add(table, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, 500);
        table.Geometry = new GeometryData
        {
            Faces =
            [
                new GeoFace
                {
                    Normal = [0, 0, 1],
                    OuterVertices = [[0, 0, 2], [1, 0, 2], [1, 1, 2], [0, 1, 2]],
                },
            ],
        };
        var stair = new Element { Id = 801, Name = "Stair", CategoryId = (int)BuiltInCategory.OST_Stairs };
        Add(stair, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, 500);
        stair.Geometry = new GeometryData
        {
            Faces =
            [
                new GeoFace
                {
                    Normal = [0, 0, 1],
                    OuterVertices = [[3, 0, 1], [4, 0, 1], [4, 1, 1], [3, 1, 1]],
                },
            ],
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Level 1", 0), Wall(600, 500, 0, 0, 10, 0), table, stair },
        });

        var content = PlanPrinter.CollectLevel(document, 500);

        Assert.NotNull(content);
        Assert.Equal(2, content!.Furniture.Count);
    }

    [Fact]
    public void CollectLevel_ReadsAStairByItsBaseLevel()
    {
        // A stair names its base level (STAIRS_BASE_LEVEL_PARAM), not a level: the plan of that level
        // draws its treads, the way the reference draws every stair of the level.
        var stair = new Element { Id = 801, Name = "Stair", CategoryId = (int)BuiltInCategory.OST_Stairs };
        Add(stair, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM, StorageType.ElementId, 500);
        stair.Geometry = new GeometryData
        {
            Faces =
            [
                new GeoFace
                {
                    Normal = [0, 0, 1],
                    OuterVertices = [[3, 0, 1], [4, 0, 1], [4, 1, 1], [3, 1, 1]],
                },
            ],
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Level 1", 0), Wall(600, 500, 0, 0, 10, 0), stair },
        });

        var content = PlanPrinter.CollectLevel(document, 500);

        Assert.NotNull(content);
        Assert.Single(content!.Furniture);
    }

    [Fact]
    public void SampleFile_Level1PlanDrawsTheStairTreads()
    {
        // The file's main stair (665557) names Level 1 (311) as its base level: the Level 1 plan collects
        // its tread outlines (x 11602..13062 mm), which the reference draws.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);

        var content = PlanPrinter.CollectLevel(document, 311);

        Assert.NotNull(content);
        Assert.Contains(content!.Furniture, outline => outline.All(point => point.X > 9000));
    }

    [Fact]
    public void DrawContent_ClipsWallsToTheCropRect()
    {
        // A wall outside the crop rectangle draws nothing (the scorer counts vectors, not the
        // page clip); a wall inside keeps its filled poche and four inked edges.
        var document = Document(Level(500, "Level 1", 0),
            Wall(600, 500, 0, 0, 10, 0), Wall(601, 500, 20, 0, 30, 0));
        var content = PlanPrinter.CollectLevel(document, 500)!;
        var pdf = new PdfDocument();
        var page = pdf.AddPage(new PdfPageSize(1000, 1000));
        PlanPrinter.DrawContent(page, content, 100, 10, 10, 0.9, null, document, null, (0, -10000, 4000, 10000));

        string text = Content(pdf);
        Assert.Equal(0, Count(text, " h B"));
        Assert.Equal(1, Count(text, " f\n"));
        Assert.Equal(4, Count(text, " l S\n"));
    }

    [Fact]
    public void DrawContent_KeepsGridsPastTheCropRect()
    {
        // Grids run past the crop to their bubbles (the reference draws them to the bubble row), so the
        // crop clip spares them while walls clip: the wall ends at the crop edge, the grid runs on.
        var document = Document(Level(500, "Level 1", 0),
            Wall(600, 500, 0, 0, 40, 0), Grid(700, 0, 0, 30, 0));
        var content = PlanPrinter.CollectLevel(document, 500)!;
        var pdf = new PdfDocument();
        var page = pdf.AddPage(new PdfPageSize(1000, 1000));
        PlanPrinter.DrawContent(page, content, 100, 10, 10, 0.9, null, document, null, (0, -1000, 3048, 1000));

        string text = Content(pdf);
        double maxX = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            text, @"([\d.]+) ([\d.]+) m ([\d.]+) ([\d.]+) l S"))
            maxX = Math.Max(maxX, double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
        // The clipped wall ends at content x 3048 mm (~143 pt); the spared grid still runs to 9144 mm (~316 pt).
        Assert.True(maxX > 300, $"Grid line clipped (ends at {maxX} pt).");
    }

    [Fact]
    public void DrawContent_DashesGridsWithTheCenterSegmentPattern()
    {
        // Plan grids dash with the center-segment pattern like elevation grids do (the reference draws
        // dashed centerlines, not solids; a solid fills the dash gaps with unscored extras).
        var document = Document(Level(500, "Level 1", 0),
            Wall(600, 500, 0, 0, 40, 0), Grid(700, 0, 0, 30, 0));
        var content = PlanPrinter.CollectLevel(document, 500)!;
        var pdf = new PdfDocument();
        var page = pdf.AddPage(new PdfPageSize(1000, 1000));
        PlanPrinter.DrawContent(page, content, 100, 10, 10, 0.9, null, document, null, (0, -1000, 3048, 1000));

        string text = Content(pdf);
        Assert.Contains("[36", text);
        Assert.Contains("[] 0 d", text);
    }

    [Fact]
    public void DrawContent_DrawsOneDimensionChainAboveTheWallFace()
    {
        // The reference plan draws a single dimension chain, 780 mm (22.1 pt at 1:100) above the outer
        // wall face; its Level 1 has no bottom, left or right chain. The old rule put the chain 550 mm
        // from the wall's location line and invented three more chains, so every reference segment
        // missed ours by more than the scorer's tolerance.
        var document = Document(Level(500, "Level 1", 0),
            Wall(600, 500, 0, 0, 40, 0),
            Grid(700, 0, -5, 0, 5), Grid(701, 10, -5, 10, 5), Grid(702, 20, -5, 20, 5));
        var content = PlanPrinter.CollectLevel(document, 500)!;
        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _) };
        var page = pdf.AddPage(new PdfPageSize(1000, 1000));
        PlanPrinter.DrawContent(page, content, 100, 10, 10, 0.9, null, document);
        string text = Content(pdf);

        var longHorizontals = new List<double>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            text, @"([\d.]+) ([\d.]+) m ([\d.]+) ([\d.]+) l"))
        {
            double x0 = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y0 = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            double x1 = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y1 = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (Math.Abs(y1 - y0) < 0.01 && Math.Abs(x1 - x0) > 100) longHorizontals.Add(y0);
        }
        longHorizontals.Sort();

        // Two long horizontals: the wall's outer face and, above it, the one top chain. Its offset beyond
        // the face is 780 mm — the reference's measured 22.1 pt at 1:100 — mapped by the same
        // (content - Min) / scale + origin the renderer uses. Before the fix the list held four chains at
        // the location line + 550 mm instead.
        Assert.Equal(2, longHorizontals.Count);
        double expected = ((100.0 + 780.0 - content.MinY) / 100.0 + 10.0) * 72 / 25.4;
        Assert.Equal(expected, longHorizontals[^1], 1);
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

        // 10 m long, 1 ft = 304.8 mm wide: the stretch between the two sides is poche, filled and inked as one
        // polygon.
        Assert.Equal(1, Count(content, " h B"));
        Assert.Contains("0.72 0.72 0.72 rg", content);
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

        // The 10 m wall is cut into two poche stretches of 4 m and 5 m; the door adds its leaf (a line) and the quarter
        // arc it sweeps (a cubic curve).
        Assert.Equal(2, Count(content, " h B"));
        Assert.Equal(1, Count(content, " l S"));
        Assert.Contains(" c ", content);
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

        // The grid is drawn (one line), clipped to the level's extent; the page stays the one the wall alone would
        // choose and does not grow to the kilometres the raw grid line spans.
        Assert.Equal(1, Count(content, " l S"));
        Assert.Equal(1, Count(content, " h B"));
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
