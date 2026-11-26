using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public class PlanSvgTests
{
    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";
    private static readonly XNamespace RvtioNs = "https://ioextend.github.io/rvtio/svg";
    private const double PtToMm = 25.4 / 72.0;

    private static void Add(Element element, BuiltInParameter id, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, id.ToString(), storage, value));

    private static Element Level(int id, string name, double elevationFeet)
    {
        var level = new Element { Id = id, Name = name, CategoryId = (int)BuiltInCategory.OST_Levels };
        Add(level, BuiltInParameter.LEVEL_ELEV, StorageType.Double, elevationFeet);
        return level;
    }

    private static Element Wall(int id, int levelId, double x1, double y1, double x2, double y2, double widthFeet = 1.0)
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

    private static Element Grid(int id, string name, double x1, double y1, double x2, double y2) => new()
    {
        Id = id,
        Name = name,
        CategoryId = (int)BuiltInCategory.OST_Grids,
        Location = new LocationCurve { CurveType = "Line", StartPoint = [x1, y1, 0], EndPoint = [x2, y2, 0] },
    };

    private static Element Door(int id, int hostId, double centerXFeet, string mark)
    {
        var door = new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Doors,
            HostId = hostId,
            OwnSingleFamilyCutout = (300, hostId,
                new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, centerXFeet, 0, 0 },
                new FamilyCutLoopData(
                    [[-0.5, 0.0, 0.0], [0.5, 0.0, 0.0], [0.5, 0.0, 3.0], [-0.5, 0.0, 3.0]],
                    [0.0, 1.0, 0.0], 0)),
        };
        Add(door, BuiltInParameter.ALL_MODEL_MARK, StorageType.String, mark);
        return door;
    }

    private static RvtDocument SamplePlanDocument(string levelName = "Уровень 1") =>
        RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            [
                Level(10, levelName, 0),
                Wall(100, 10, 0, 0, 20, 0, widthFeet: 1.0),
                Wall(101, 10, 20, 0, 20, 12, widthFeet: 1.0),
                Door(200, 100, 8.0, "D-1"),
                Column(300, 10, 4, 4, 5, 5),
                Grid(400, "А", -50, 0, 50, 0),
                Grid(401, "1", 0, -50, 0, 50),
            ],
        });

    [Fact]
    public void RenderSvg_MatchesPdfCoordinatesLayoutPaperAndScale()
    {
        var document = SamplePlanDocument("Level 1");
        var options = new PlanPrintOptions { PaperSize = PdfPageSize.A3.Landscape };

        PdfDocument pdf = PlanPrinter.Render(document, levelId: 10, scale: 100, options);
        string svgText = PlanPrinter.RenderSvg(document, levelId: 10, scale: 100, options);

        using var ms = new MemoryStream();
        pdf.Save(ms);
        string pdfStream = PdfTestContent.Read(ms.ToArray());

        XDocument xml = XDocument.Parse(svgText);
        XElement svg = xml.Root!;
        Assert.Equal("svg", svg.Name.LocalName);
        Assert.Equal("420mm", svg.Attribute("width")?.Value);
        Assert.Equal("297mm", svg.Attribute("height")?.Value);
        Assert.Equal("0 0 420 297", svg.Attribute("viewBox")?.Value);
        Assert.Equal("100", svg.Attribute("data-scale")?.Value);
        Assert.Equal("100", svg.Attribute("data-nominal-scale")?.Value);
        Assert.Equal("420", svg.Attribute("data-width-mm")?.Value);
        Assert.Equal("297", svg.Attribute("data-height-mm")?.Value);

        XElement meta = svg.Descendants(RvtioNs + "plan").Single();
        Assert.Equal("10", meta.Attribute("levelId")?.Value);
        Assert.Equal("100", meta.Attribute("scale")?.Value);
        Assert.Equal("420", meta.Attribute("widthMm")?.Value);
        Assert.Equal("297", meta.Attribute("heightMm")?.Value);

        double H = 297.0;
        var svgPolygons = svg.Elements(SvgNs + "polygon").ToList();
        var svgLines = svg.Elements(SvgNs + "line").ToList();
        var svgRects = svg.Elements(SvgNs + "rect").ToList();
        var svgPaths = svg.Elements(SvgNs + "path").ToList();

        // Background + frame + column = 3 rects (white sheet + frame border + column).
        Assert.Equal(3, svgRects.Count);
        XElement colSvg = svgRects[2];
        double colSvgX = double.Parse(colSvg.Attribute("x")!.Value, CultureInfo.InvariantCulture);
        double colSvgY = double.Parse(colSvg.Attribute("y")!.Value, CultureInfo.InvariantCulture);
        double colSvgW = double.Parse(colSvg.Attribute("width")!.Value, CultureInfo.InvariantCulture);
        double colSvgH = double.Parse(colSvg.Attribute("height")!.Value, CultureInfo.InvariantCulture);

        // Extract column rectangle (third "re" after page border/frame? First "re" in PDF content is frame, column is later "re B").
        var pdfRectMatches = Regex.Matches(pdfStream, @"([\d.-]+) ([\d.-]+) ([\d.-]+) ([\d.-]+) re B");
        Assert.True(pdfRectMatches.Count >= 1);
        var lastRect = pdfRectMatches[^1];
        double pdfColX = double.Parse(lastRect.Groups[1].Value, CultureInfo.InvariantCulture) * PtToMm;
        double pdfColY = double.Parse(lastRect.Groups[2].Value, CultureInfo.InvariantCulture) * PtToMm;
        double pdfColW = double.Parse(lastRect.Groups[3].Value, CultureInfo.InvariantCulture) * PtToMm;
        double pdfColH = double.Parse(lastRect.Groups[4].Value, CultureInfo.InvariantCulture) * PtToMm;

        Assert.Equal(pdfColX, colSvgX, 2);
        Assert.Equal(H - (pdfColY + pdfColH), colSvgY, 2);
        Assert.Equal(pdfColW, colSvgW, 2);
        Assert.Equal(pdfColH, colSvgH, 2);

        // Walls cut by the door: 2 stretches on wall 100 + 1 wall 101 -> 3 wall poche polygons (#b8b8b8 = 0.72 gray).
        var wallPolygons = svgPolygons.Where(p => p.Attribute("fill")?.Value == "#b8b8b8").ToList();
        Assert.Equal(3, wallPolygons.Count);

        // Compare first wall polygon vertices against PDF polygon vertices!
        var pdfWallMatch = Regex.Match(pdfStream,
            @"0\.72 0\.72 0\.72 rg\s+([\d.-]+) ([\d.-]+) m ([\d.-]+) ([\d.-]+) l ([\d.-]+) ([\d.-]+) l ([\d.-]+) ([\d.-]+) l h B");
        Assert.True(pdfWallMatch.Success);
        double pdfWx0 = double.Parse(pdfWallMatch.Groups[1].Value, CultureInfo.InvariantCulture) * PtToMm;
        double pdfWy0 = double.Parse(pdfWallMatch.Groups[2].Value, CultureInfo.InvariantCulture) * PtToMm;
        string[] firstSvgVerts = wallPolygons[0].Attribute("points")!.Value.Split(' ')[0].Split(',');
        double svgWx0 = double.Parse(firstSvgVerts[0], CultureInfo.InvariantCulture);
        double svgWy0 = double.Parse(firstSvgVerts[1], CultureInfo.InvariantCulture);
        Assert.Equal(pdfWx0, svgWx0, 2);
        Assert.Equal(H - pdfWy0, svgWy0, 2);

        // Door leaf + quarter arc + grids present.
        Assert.Single(svgPaths);
        Assert.Contains(" A ", svgPaths[0].Attribute("d")!.Value);
        Assert.True(svgLines.Count >= 3); // 2 grids + door leaf
    }

    [Fact]
    public void RenderSvg_AutoFitsOversizedPlanAndRecordsEffectiveScaleInMetadataAndText()
    {
        // A huge 200 ft (~61 m) wall at 1:100 (~610 mm) will be scaled down to fit A4 Landscape (297x210).
        var doc = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [Level(1, "Большой план", 0), Wall(100, 1, 0, 0, 200, 0)],
        });

        string svg = PlanPrinter.RenderSvg(doc, levelId: 1, scale: 100, new PlanPrintOptions { PaperSize = PdfPageSize.A4.Landscape });
        XDocument xml = XDocument.Parse(svg);
        XElement root = xml.Root!;

        double nominal = double.Parse(root.Attribute("data-nominal-scale")!.Value, CultureInfo.InvariantCulture);
        double effective = double.Parse(root.Attribute("data-scale")!.Value, CultureInfo.InvariantCulture);
        Assert.Equal(100, nominal);
        Assert.True(effective > 200, $"Expected downscaled effective scale > 200, got {effective}");
        Assert.Equal("297", root.Attribute("data-width-mm")?.Value);
        Assert.Equal("210", root.Attribute("data-height-mm")?.Value);

        string scaleText = root.Elements(SvgNs + "text").Select(t => t.Value).First(v => v.StartsWith("1:", StringComparison.Ordinal));
        Assert.Equal($"1:{(int)Math.Round(effective)}", scaleText);
    }

    [Fact]
    public void RenderSvg_SupportsCyrillicXmlSpecialCharactersAndControlCharSanitization()
    {
        const string trickyLevel = "План <Этаж 1> & \"Корпус\" 'А'\u0001";
        const string trickyGrid = "Ось & <1>";
        var doc = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            [
                Level(10, trickyLevel, 0),
                Wall(100, 10, 0, 0, 20, 0),
                Door(200, 100, 10.0, "Д<1>&\"2\""),
                Grid(400, trickyGrid, -20, 0, 40, 0),
            ],
        });

        string svg = PlanPrinter.RenderSvg(doc, levelId: 10, scale: 100);
        XDocument xml = XDocument.Parse(svg);
        var texts = xml.Root!.Elements(SvgNs + "text").Select(t => t.Value).ToList();

        Assert.Contains("План <Этаж 1> & \"Корпус\" 'А'", texts);
        Assert.Contains("Ось & <1>", texts);
        Assert.Contains("Д<1>&\"2\"", texts);
        Assert.Contains("1:100", texts);
        Assert.Equal("План <Этаж 1> & \"Корпус\" 'А'", xml.Root!.Element(SvgNs + "title")!.Value);
    }

    [Fact]
    public void RenderSvg_ThrowsOnEmptyPlanOrMissingLevel()
    {
        var emptyDoc = RvtDocument.FromInfo(new RevitFileInfo { Elements = [Level(1, "Пустой", 0)] });
        Assert.Throws<InvalidOperationException>(() => PlanPrinter.RenderSvg(emptyDoc));
        Assert.Throws<InvalidOperationException>(() => PlanPrinter.RenderSvg(emptyDoc, levelId: 1));

        var missingLevelDoc = SamplePlanDocument();
        Assert.Throws<InvalidOperationException>(() => PlanPrinter.RenderSvg(missingLevelDoc, levelId: 9999));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanPrinter.RenderSvg(missingLevelDoc, levelId: 10, scale: 0));
    }

    [Fact]
    public void WriteSvg_MatchesTheStringApiAndLeavesTheStreamOpen()
    {
        var doc = SamplePlanDocument();
        var options = new PlanPrintOptions { MarginMillimetres = 0 };
        using var output = new MemoryStream();

        PlanPrinter.WriteSvg(doc, output, options: options);

        Assert.True(output.CanWrite);
        Assert.Equal(PlanPrinter.RenderSvg(doc, null, options: options),
            System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    [Theory]
    [InlineData(0, 297, 10)]
    [InlineData(210, -1, 10)]
    [InlineData(double.NaN, 297, 10)]
    [InlineData(210, double.PositiveInfinity, 10)]
    [InlineData(210, 297, -1)]
    [InlineData(210, 297, double.NaN)]
    [InlineData(210, 297, double.PositiveInfinity)]
    [InlineData(210, 297, 105)]
    [InlineData(297, 210, 106)]
    [InlineData(double.Epsilon, 297, 0)]
    public void PlanPrinting_RejectsInvalidLayoutBeforeWritingSvg(double width, double height, double margin)
    {
        var doc = SamplePlanDocument();
        var options = new PlanPrintOptions { PaperSize = new PdfPageSize(width, height), MarginMillimetres = margin };
        using var output = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>("options", () => PlanPrinter.WriteSvg(doc, output, options: options));
        Assert.Equal(0, output.Length);
        Assert.Throws<ArgumentOutOfRangeException>("options", () => PlanPrinter.Render(doc, options: options));
    }

    [Fact]
    public void CliPrintCommand_WritesValidSvgAndHandlesErrors()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rvtio-print-svg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Missing level & empty document on a readable compound file: use CliExitCodeTests helper.
            string rvt = Path.Combine(dir, "minimal.rvt");
            CliExitCodeTests.WriteSyntheticRvt(rvt);
            string emptySvg = Path.Combine(dir, "empty.svg");
            var err = new StringWriter();
            int exitEmpty = RvtIO.Cli.PrintCommand.Run([rvt, emptySvg], TextWriter.Null, err);
            Assert.Equal(RvtExitCodes.DecodeError, exitEmpty);
            Assert.False(File.Exists(emptySvg));

            // View/sheet flags are refused when .svg is requested (level plan scope only).
            string viewSvg = Path.Combine(dir, "view.svg");
            var errView = new StringWriter();
            int exitView = RvtIO.Cli.PrintCommand.Run([rvt, viewSvg, "--view", "Level 1"], TextWriter.Null, errView);
            Assert.Equal(RvtExitCodes.Usage, exitView);
            Assert.Contains("level plan", errView.ToString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [SkippableFact]
    public void SampleRvtFilesFromTestRoots_RenderMatchingPdfAndSvgPlansAndRejectMissingLevel()
    {
        string? firstRvt = TestFiles.Roots()
            .SelectMany(root => Directory.EnumerateFiles(root, "*.rvt", SearchOption.AllDirectories))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (firstRvt is null)
            throw new SkipException("No .rvt fixture files found under RVTIO_TEST_FILES.");

        var doc = RvtDocument.Open(firstRvt);
        var levels = PlanPrinter.Levels(doc);
        if (levels.Count == 0)
            throw new SkipException("Sample .rvt has no printable level.");

        string dir = Path.Combine(Path.GetTempPath(), "rvtio-print-svg-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string pdfPath = Path.Combine(dir, "plan.pdf");
            string svgPath = Path.Combine(dir, "plan.svg");
            string levelArg = levels[0].LevelId.ToString(CultureInfo.InvariantCulture);

            var pdfErr = new StringWriter();
            Assert.Equal(RvtExitCodes.Success, RvtIO.Cli.PrintCommand.Run([firstRvt, pdfPath, "--level", levelArg, "--scale", "100"], TextWriter.Null, pdfErr));
            var svgErr = new StringWriter();
            Assert.Equal(RvtExitCodes.Success, RvtIO.Cli.PrintCommand.Run([firstRvt, svgPath, "--level", levelArg, "--scale", "100"], TextWriter.Null, svgErr));

            XDocument xml = XDocument.Parse(File.ReadAllText(svgPath));
            Assert.Equal("svg", xml.Root!.Name.LocalName);
            Assert.Equal(levelArg, xml.Root.Attribute("data-level-id")?.Value);

            string missingSvg = Path.Combine(dir, "missing.svg");
            var missingErr = new StringWriter();
            Assert.Equal(RvtExitCodes.Usage, RvtIO.Cli.PrintCommand.Run([firstRvt, missingSvg, "--level", "NoSuchLevel_999"], TextWriter.Null, missingErr));
            Assert.False(File.Exists(missingSvg));
            Assert.Contains("No printable level", missingErr.ToString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
