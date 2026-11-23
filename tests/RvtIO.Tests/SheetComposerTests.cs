using System.Globalization;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;
using Xunit.Sdk;

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
        string content = PdfTestContent.Read(stream.ToArray());

        Assert.Contains(" h B", content);  // the walls of the plan
        Assert.Contains(" re S", content);
        if (PdfFonts.TryFind(out _) != null)
        {
            Assert.Contains(" l S", content);  // the rule under the viewport title, which needs the font
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

    [SkippableFact]
    public void Render_DrawsViewportTitlesAtTheirStoredOriginAndLength()
    {
        // The South viewport's title extension line: the file stores its origin on the sheet and its length
        // (m_labelOrigin, m_labelLength), and the reference inks it 1485 pt long at y579.4 (x102.9..1588.1).
        // In PDF points (bottom-up): x 102.9, y 1104.3, length 1485.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        Element? sheet = null;
        foreach (var e in document.Elements)
            if (e.CategoryId == (int)BuiltInCategory.OST_Sheets
                && e.LookupParameter(BuiltInParameter.SHEET_NUMBER)?.Value as string == "A103") sheet = e;
        Assert.NotNull(sheet);

        var titled = document.Elements.Where(element => element.OwnerViewId == sheet.Id
            && element.CategoryId == (int)BuiltInCategory.OST_Viewports
            && element.LookupParameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.Value is string { Length: > 0 }).ToList();
        Assert.Equal(2, titled.Count);
        foreach (var viewport in titled)
        {
            var title = Assert.Contains(viewport.Id, document.RawData.ViewportTitles!);
            Assert.True(title.ShowLabel);
            Assert.True(title.ShowExtensionLine);
            var style = Assert.Contains(title.SymbolId, document.RawData.ViewportTitleStyles!);
            Assert.Equal(2, style.Arcs.Count);
            Assert.Contains(style.Labels, entry => entry.Label.ParamId == (int)BuiltInParameter.VIEWPORT_VIEW_NAME
                && entry.FontPoints == 24 && entry.Wrap);
            Assert.Contains(style.Labels, entry => entry.Label.ParamId == (int)BuiltInParameter.VIEWPORT_SCALE
                && entry.FontPoints == 16);
        }

        PdfDocument pdf = SheetComposer.Render(document, sheet.Id);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string content = PdfTestContent.Read(stream.ToArray());
        if (pdf.Font != null)
        {
            Assert.Contains("BT /F2 24 Tf ", content);
            Assert.Contains("BT /F2 16 Tf ", content);
        }

        bool found = false;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            content, @"([\d.]+) ([\d.]+) m ([\d.]+) ([\d.]+) l S"))
        {
            double x1 = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y1 = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            double x2 = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y2 = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (Math.Abs(y2 - y1) < 0.5 && Math.Abs(x2 - x1) is >= 1475 and <= 1495 && Math.Abs(y1 - 1104.3) < 12)
                found = true;
        }
        Assert.True(found, "South title extension line (1485 pt at y1104.3) not drawn.");
    }

    [SkippableFact]
    public void Render_DrawsOffPageWindowedDetailsAboveTheirTitles()
    {
        // The four A104 wall/roof/foundation details sit off the drawable page in the file, but Revit prints
        // them in a bottom row above their own titles. The composer anchors each frame on its decoded title
        // label instead of dropping the viewport.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        Element? sheet = null;
        foreach (var e in document.Elements)
            if (e.CategoryId == (int)BuiltInCategory.OST_Sheets
                && e.LookupParameter(BuiltInParameter.SHEET_NUMBER)?.Value as string == "A104") sheet = e;
        Assert.NotNull(sheet);

        var views = ElevationPrinter.Views(document);
        var unshaded = Assert.IsAssignableFrom<IReadOnlySet<int>>(document.RawData.UnshadedHiddenLineViews);
        var roof = Assert.Single(views, view => view.Name == "Typical Wall Roof Connection");
        var foundation = Assert.Single(views, view => view.Name == "Typical Foundation Detail");
        var section = Assert.Single(views, view => view.Name == "Building Section");
        Assert.Contains(roof.ViewId, unshaded);
        Assert.Contains(foundation.ViewId, unshaded);
        Assert.DoesNotContain(section.ViewId, unshaded);

        PdfDocument pdf = SheetComposer.Render(document, sheet.Id);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string content = PdfTestContent.Read(stream.ToArray());

        // Detail linework inks in the bottom band (above the titles at y~106): any stroked segment there
        // comes from a relocated detail, since no other A104 viewport draws that low.
        bool found = false;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            content, @"([\d.]+) ([\d.]+) m ([\d.]+) ([\d.]+) l S"))
        {
            double y1 = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y2 = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (y1 > 150 && y1 < 320 && y2 > 150 && y2 < 320) found = true;
        }
        Assert.True(found, "No relocated detail linework in the bottom band.");
        Assert.Matches(@"BT /F\d+ 8 Tf 64\d\.\d+ 88\d\.\d+ Td (?:\(A104\)|<[0-9A-F]+>) Tj ET", content);
    }

    private static SitePrinter.SiteContent Site(
        List<List<(double X, double Y)>>? topography = null,
        List<(double X1, double Y1, double X2, double Y2, double Z)>? contours = null,
        List<SitePrinter.SitePlant>? plants = null,
        List<List<(double X, double Y)>>? entourage = null)
    {
        topography ??= new();
        contours ??= new();
        plants ??= new();
        entourage ??= new();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(double x, double y)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        foreach (var polygon in topography) foreach (var point in polygon) Grow(point.X, point.Y);
        foreach (var line in contours) { Grow(line.X1, line.Y1); Grow(line.X2, line.Y2); }
        foreach (var plant in plants) { Grow(plant.X - plant.Radius, plant.Y - plant.Radius); Grow(plant.X + plant.Radius, plant.Y + plant.Radius); }
        foreach (var polygon in entourage) foreach (var point in polygon) Grow(point.X, point.Y);
        if (minX > maxX) { minX = minY = 0; maxX = maxY = 1; }
        return new SitePrinter.SiteContent(topography, contours, plants, entourage, minX, minY, maxX, maxY);
    }

    private static PlanPrinter.PlanContent Building(double minX, double minY, double maxX, double maxY) =>
        new([], [], [], new(), new(), new(), new(), new(), new(), new(), new(),
            minX, minY, maxX, maxY);

    [Fact]
    public void KeepSiteNearBuilding_DropsContextBeyondTheBuildingAir()
    {
        var site = Site(
            topography: new()
            {
                new() { (50000, 50000), (60000, 50000), (55000, 60000) },
                new() { (4000, 3000), (6000, 3000), (5000, 5000) },
            },
            contours: new() { (50000, 50000, 51000, 51000, 0), (4500, 3500, 5500, 3500, 0) },
            plants: [new(80000, 80000, 500, "Tree", "T1"), new(5000, 4000, 500, "Bush", "T2")],
            entourage: new() { new() { (90000, 90000), (91000, 90000) }, new() { (4800, 4200), (4900, 4200) } });
        var building = Building(0, 0, 10000, 8000);

        SitePrinter.SiteContent kept = SheetComposer.KeepSiteNearBuilding(site, building);

        Assert.Equal(1, kept.Topography.Count);
        Assert.Single(kept.Contours);
        Assert.Single(kept.Plants);
        Assert.Single(kept.Entourage);
        // The kept context stays inside the building plus its air; the bounds still hold the building.
        Assert.Equal(0, kept.MinX);
        Assert.Equal(0, kept.MinY);
        Assert.Equal(10000, kept.MaxX);
        Assert.Equal(8000, kept.MaxY);
    }

    [Fact]
    public void KeepSiteNearBuilding_KeepsEverythingWhenNothingWouldRemain()
    {
        var site = Site(
            topography: new() { new() { (50000, 50000), (60000, 50000), (55000, 60000) } },
            plants: [new(80000, 80000, 500, "Tree", "T1")]);
        var building = Building(0, 0, 10000, 8000);

        SitePrinter.SiteContent kept = SheetComposer.KeepSiteNearBuilding(site, building);

        Assert.Same(site, kept);
    }

    [Fact]
    public void KeepSiteNearBuilding_PreservesAuthoredPropertyBoundary()
    {
        var site = Site(topography: [[(4000, 3000), (6000, 3000), (5000, 5000)]]) with
        {
            PropertyLines = [[(-20000, -15000), (30000, 25000)]],
        };

        var kept = SheetComposer.KeepSiteNearBuilding(site, Building(0, 0, 10000, 8000));

        Assert.Same(site.PropertyLines, kept.PropertyLines);
        Assert.Equal((-20000, -15000, 30000, 25000), (kept.MinX, kept.MinY, kept.MaxX, kept.MaxY));
    }

    [SkippableFact]
    public void SampleFile_PrintReadsRenderSizeSiteBoundaryAndRoomGraphics()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var title = Assert.Single(SheetComposer.Sheets(document), sheet => sheet.Number == "A001");
        var page = Assert.Single(SheetComposer.RenderSheets(document, [title.SheetId]).Pages);
        Assert.Equal(6, page.Images.Count);
        Assert.Contains("1275.84 0 0 696.96", page.Content);
        var logo = Assert.Single(page.Images, image => image.Width == 1063);
        Assert.Equal(206, logo.Height);
        string drawing = $" /Im{page.Images.IndexOf(logo)} Do Q";
        string[] logoMatrix = page.Content.Split('\n')
            .Single(line => line.EndsWith(drawing, StringComparison.Ordinal)).Split(' ');
        Assert.InRange(double.Parse(logoMatrix[1], System.Globalization.CultureInfo.InvariantCulture), 251.63, 251.66);
        Assert.InRange(double.Parse(logoMatrix[4], System.Globalization.CultureInfo.InvariantCulture), 48.75, 48.78);
        var shapes = Assert.IsAssignableFrom<IReadOnlyDictionary<int, List<RevitFileInfo.CircularAnnotation>>>(
            document.RawData.CircularAnnotations);
        Assert.Equal(41, shapes.Values.Sum(group => group.Count));
        var siteView = Assert.Single(document.Elements, view => view.Name == "Site" && view.ViewCrop != null);
        var siteCrop = Assert.IsType<ViewCropFrame>(siteView.ViewCrop);
        Assert.False(siteView.ViewAnnotationCropActive);
        Assert.Equal(4, CircularAnnotations.Project(document, siteView.Id, siteCrop.Right, siteCrop.Up).Count);
        var light = Assert.Contains(siteView.Id, document.RawData.ViewGroundShadows!);
        Assert.Equal(135 * Math.PI / 180, light.Azimuth, 9);
        Assert.Equal(35 * Math.PI / 180, light.Altitude, 9);
        Assert.Equal(17, SitePrinter.CollectTags(document, siteView.Id, siteCrop).Count);
        var compass = Assert.Single(Assert.Contains(siteView.Id, document.RawData.PlanOutlineAnnotations!),
            outline => outline.Lines.Count == 3 && outline.Arcs.Count == 2);
        var compassPose = CircularAnnotations.ProjectTransform(compass.Transform, siteCrop.Right, siteCrop.Up);
        Assert.Equal(200, compassPose!.Value.Factor, 8);
        var contourSite = Assert.IsType<SitePrinter.SiteContent>(SitePrinter.Collect(document, siteView.Id));
        Assert.Contains(0, contourSite.NativePrimaryLevels!);
        Assert.InRange(contourSite.Contours.Count, 240, 250);
        Assert.True(PenWidths.TryGetPenNumber(document, (int)BuiltInCategory.OST_TopographyContours, false, out int primaryPen));
        Assert.Equal(6, primaryPen);
        Assert.True(PenWidths.TryGetPenNumber(document, (int)BuiltInCategory.OST_SecondaryTopographyContours, false, out int secondaryPen));
        Assert.Equal(1, secondaryPen);
        var contourLabels = Assert.Contains(siteView.Id, document.RawData.SiteContourLabels!);
        Assert.Equal([-5000, -5000, 0, 0, 1000, 2000, 3000, 4000],
            contourLabels.Select(label => (int)Math.Round(label.Position[2] * FeetToMillimetres)).Order().ToArray());
        Assert.All(contourLabels, label => Assert.Equal(1.5, label.TextHeightFeet * FeetToMillimetres, 8));
        Assert.Equal((1, 1), document.RawData.PropertyBearingFormat);
        var propertyTags = Assert.Contains(siteView.Id, document.RawData.SitePropertyTags!);
        Assert.Equal(6, propertyTags.Count);
        Assert.Single(propertyTags, tag => document.GetElement(tag.ElementId)?.SitePropertyArcCenter != null);
        var propertyStyle = Assert.Contains(Assert.Single(propertyTags.Select(tag => tag.SymbolId).Distinct()),
            document.RawData.SitePropertyTagStyles!);
        var distanceLabel = Assert.Single(propertyStyle, entry =>
            entry.Label.ParamId == (int)BuiltInParameter.PROPERTY_SEGMENT_DISTANCE);
        Assert.Equal(0.01, distanceLabel.Label.SingleParameterFormat!.Accuracy);
        var roof = Assert.IsType<SitePrinter.SiteGraphics>(SitePrinter.CollectOpaqueGraphics(document, siteView.Id, siteCrop));
        var roofFaces = roof.Surfaces.Where(face => face.Category == BuiltInCategory.OST_Roofs).ToArray();
        Assert.Contains(roofFaces, face => face.Rings.Count > 1 && face.Grid != null);
        var roofGrids = roofFaces.Select(face => face.Grid).OfType<FillHatch.ModelGrid>().ToArray();
        Assert.Equal(2, roofGrids.Length);
        Assert.All(roofGrids, grid =>
        {
            Assert.Equal(2 * FeetToMillimetres, grid.Spacing, 6);
            Assert.Equal(0, Math.Cos(grid.Angle) * siteCrop.Right[0] + Math.Sin(grid.Angle) * siteCrop.Up[0], 8);
            var worldX = grid.X * siteCrop.Right[0] + grid.Y * siteCrop.Up[0];
            var phase = (worldX % grid.Spacing + grid.Spacing) % grid.Spacing;
            Assert.Equal(0.72121917874 * FeetToMillimetres, phase, 4);
        });
        Assert.NotEmpty(roof.GroundShadows);
        Assert.Equal(6, page.Content.Split('\n').Count(line => line == "0.3137 0.5333 0.8157 rg"));
        var schedule = Assert.Single(SheetComposer.SchedulesOnSheet(document, document.GetElement(title.SheetId)!),
            schedule => schedule.Title == "How do I");
        var guide = Assert.Contains(schedule.ScheduleId, document.RawData.EvaluatedScheduleRows!);
        Assert.Equal(41, guide.Count);
        Assert.All(guide[0], value => Assert.Equal(new RevitFileInfo.ScheduleCellValue(""), value));
        Assert.Equal([new("01 - Basic"), new("Add levels to the project."), new("Elevations - East"), new("", false)], guide[1]);
        Assert.Equal([new("03 - Advanced"), new("Use the sun path."), new("Sheet - A101"), new("", false)], guide[^1]);

        var site = SitePrinter.Collect(document);
        Assert.NotNull(site);
        Assert.NotNull(site.PropertyLines);
        Assert.Equal(6, site.PropertyLines.Count);
        var arc = Assert.Single(site.PropertyLines, line => line.Count > 2);
        Assert.Equal(83.41777 * FeetToMillimetres, arc[0].X, 2);
        Assert.Equal(-32.11147 * FeetToMillimetres, arc[^1].X, 2);
        var sitePage = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        SitePrinter.DrawContent(sitePage, site, 200, 0, 0, document);
        Assert.Contains("q 1 J 1 j\n[", sitePage.Content);
        Assert.Contains(" 13.5 18 13.5 18 13.5 ", sitePage.Content);

        var legends = document.Elements.Where(e => e.RoomLegendOrigin.HasValue).OrderBy(e => e.Id).ToList();
        Assert.Equal(2, legends.Count);
        var origin = Assert.IsType<(double X, double Y)>(legends[0].RoomLegendOrigin);
        Assert.Equal(-12.42004, origin.X, 3);
        var style = Assert.IsType<(double Width, double Height)>(document.GetElement(legends[0].TypeId)?.ColorLegendSwatch);
        Assert.Equal(9.5, style.Width * FeetToMillimetres, 2);
        Assert.Equal(9.5, style.Height * FeetToMillimetres, 2);

        var plan = PlanPrinter.Views(document).Single(view => view.ViewId == legends[0].OwnerViewId);
        var rooms = PlanPrinter.CollectLevel(document, plan.LevelId, plan.ViewId);
        Assert.NotNull(rooms);
        var kitchen = Assert.Single(rooms.Rooms, room => room.Name == "Kitchen & Dining");
        Assert.Equal("101", kitchen.Number);
        Assert.InRange(kitchen.AreaSquareFeet * 0.09290304, 72.5, 73.5);
        Assert.NotNull(rooms.RoomColors);
        Assert.Equal(new PdfColor(209 / 255.0, 203 / 255.0, 182 / 255.0),
            PlanPrinter.RoomFill(kitchen.Name, rooms.RoomColors));

        foreach (var level in PlanPrinter.Views(document).Where(view => view.Name is "Level 1" or "Level 2"))
        {
            var view = Assert.IsType<Element>(document.GetElement(level.ViewId));
            var crop = Assert.IsType<ViewCropFrame>(view.ViewCrop);
            Assert.False(view.ViewAnnotationCropActive);
            var grids = PlanPrinter.CollectPlanGrids(document, view, crop, level.Scale);
            Assert.NotNull(grids);
            bool firstFloor = level.Name == "Level 1";
            Assert.Contains(level.ViewId, document.RawData.OpaquePlanViews!);
            var savedRange = Assert.Contains(level.ViewId, document.RawData.PlanViewRanges!);
            Assert.Equal(firstFloor ? -4 : level.LevelId, savedRange.LevelIds[2]);
            if (!firstFloor)
            {
                var section = Assert.Single(document.Elements, element =>
                    element.Name == "Building Section" && element.SectionViewerId.HasValue);
                Assert.Contains(section.SectionViewerId!.Value, document.RawData.ViewHiddenElements![level.ViewId]);
                Assert.DoesNotContain(ElevationPrinter.SectionMarks(document, level.ViewId, level.Scale),
                    mark => mark.Name == "Building Section");
            }

            var dimensions = PlanPrinter.CollectPlanDimensions(document, level.ViewId, crop);
            Assert.Equal(2, dimensions.Count);
            var topChain = Assert.Single(dimensions, dimension => dimension.Labels.Length == 6);
            Assert.All(topChain.Labels, label => Assert.Equal("3000", label));
            Assert.Equal(firstFloor ? 23.638116 : 15.232923, topChain.Dimension.Origin[1], 6);
            var sideChain = Assert.Single(dimensions, dimension => dimension.Labels.Length != 6);
            Assert.Equal(firstFloor ? new[] { "3000", "3000" } : ["3000", "3000", "3000", "3000", "6000"],
                sideChain.Labels);

            var openingTags = Assert.Contains(level.ViewId, document.RawData.PlanOpeningTags!);
            Assert.Equal(firstFloor ? 13 : 18, openingTags.Count);
            Assert.Equal(firstFloor ? 8 : 7, openingTags.Count(tag =>
                document.GetElement(tag.ElementId)?.CategoryId == (int)BuiltInCategory.OST_Windows));
            foreach (var openingTag in openingTags)
            {
                var element = Assert.IsType<Element>(document.GetElement(openingTag.ElementId));
                var openingStyle = Assert.Contains(openingTag.SymbolId, document.RawData.PlanOpeningTagStyles!);
                Assert.Equal(12, openingStyle.FontPoints);
                bool window = element.CategoryId == (int)BuiltInCategory.OST_Windows;
                Assert.Equal(window ? (int)BuiltInParameter.ALL_MODEL_TYPE_MARK : (int)BuiltInParameter.ALL_MODEL_MARK,
                    openingStyle.Label.ParamId);
                Assert.Equal(window ? 6 : 2, openingStyle.Lines.Count);
                Assert.Equal(window ? 0 : 2, openingStyle.Arcs.Count);
                var valueOwner = window ? document.GetElement(element.TypeId) : element;
                Assert.False(string.IsNullOrEmpty(valueOwner?.LookupParameter(
                    (BuiltInParameter)openingStyle.Label.ParamId)?.Value as string));
            }

            var tags = Assert.Contains(level.ViewId, document.RawData.PlanRoomTags!);
            var roomTagStyles = Assert.IsAssignableFrom<IReadOnlyDictionary<int, RevitFileInfo.PlanRoomTagStyle>>(
                document.RawData.PlanRoomTagStyles);
            Assert.Equal(firstFloor ? 6 : 8, tags.Count);
            Assert.Equal(firstFloor ? 3 : 4, tags.Count(tag => roomTagStyles[tag.SymbolId].Lines.Count == 4));
            Assert.Equal(firstFloor ? 0 : 1, tags.Count(tag => tag.LeaderEnd != null));
            if (!firstFloor)
            {
                var linen = Assert.Single(tags, tag => tag.LeaderEnd != null);
                Assert.Equal(17.710364, linen.LeaderEnd!.Value.X, 6);
                Assert.Equal(3.123749, linen.LeaderEnd.Value.Y, 6);
                Assert.Equal(2.387087, linen.LeaderAnchor!.Value.Y, 6);
            }
            foreach (var tag in tags)
            {
                var tagStyle = Assert.Contains(tag.SymbolId, roomTagStyles);
                Assert.Equal(tagStyle.Lines.Count == 4 ? 3 : 1, tagStyle.Labels.Count);
                Assert.All(tagStyle.Labels, label => Assert.Equal(12, label.FontPoints));
            }
            if (firstFloor)
            {
                var kitchenTag = Assert.Single(tags, tag => tag.RoomId == 857279);
                Assert.Equal(1.974727, kitchenTag.X, 6);
                Assert.Equal(4.187897, kitchenTag.Y, 6);
            }

            var notes = Assert.Contains(level.ViewId, document.RawData.PlanNotes!);
            Assert.Equal(firstFloor ? 3 : 4, notes.Count);
            Assert.All(notes, note =>
            {
                Assert.True(note.HasBorder);
                Assert.Equal(14, note.FontPoints);
            });
            Assert.Equal(2, notes.Sum(note => note.Leaders.Count));
            Assert.All(notes.Where(note => note.Leaders.Count > 0), note => Assert.True(note.ArcLeaders));

            Assert.Equal(firstFloor ? 17 : 13, grids.Sum(grid => (grid.StartBubble ? 1 : 0) + (grid.EndBubble ? 1 : 0)));
            var gridA = Assert.Single(grids, grid => grid.Name == "A");
            Assert.Equal(firstFloor, gridA.StartBubble);
            Assert.True(gridA.EndBubble);
            Assert.Equal(6.5, gridA.Radius, 5);
            Assert.Null(gridA.StartLeader);
            if (firstFloor)
            {
                Assert.NotNull(gridA.EndLeader);
                Assert.Equal(12.29395011, gridA.EndLeader.Anchor[1], 7);
            }
            else
            {
                Assert.Null(gridA.EndLeader);
            }
        }
    }

    [SkippableFact]
    public void Render_UsesSavedViewportTitleAndRuleVisibility()
    {
        if (PdfFonts.TryFind(out _) == null) throw new SkipException("An embedded PDF font is required.");
        var viewport = Viewport(200, 100, 300, 50, 60);
        viewport.ViewportLabelOrigin = [0.25, 0.25, 0];
        viewport.ViewportLabelLength = 0.50;
        var view = View(300, "Plan", 0);
        var titles = new Dictionary<int, RevitFileInfo.ViewportTitle> { [200] = new(400, true, false, null) };
        var label = new TfBlockDeserializer.TitleBlockLabel(0, 0, 0, 0, 1, 1, 0, 0.03, 0.3, 0, 0,
            (int)BuiltInParameter.VIEWPORT_VIEW_NAME, "", "");
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [Sheet(100, "A101", "Plan"), viewport, view],
            ViewportTitles = titles,
            ViewportTitleStyles = new Dictionary<int, RevitFileInfo.ViewportTitleStyle>
            {
                [400] = new([(label, 24, false)], [], [])
            }
        });
        var image = new PdfRasterImage(1, 1, [20, 40, 60]);
        var options = new SheetComposeOptions
        {
            DrawPageFrame = false,
            ViewportImages = new Dictionary<int, SheetViewportImage> { [200] = new(image, 20, 30, 40, 50) }
        };
        string titleOnly = Assert.Single(SheetComposer.Render(document, 100, options).Pages).Content;
        Assert.Contains("BT /F2 24 Tf ", titleOnly);
        Assert.DoesNotContain(" l S", titleOnly);

        titles[200] = titles[200] with { ShowExtensionLine = true };
        string withRule = Assert.Single(SheetComposer.Render(document, 100, options).Pages).Content;
        Assert.Contains("216 216 m 648 216 l S", withRule);

        titles[200] = titles[200] with { ShowLabel = false };
        string hidden = Assert.Single(SheetComposer.Render(document, 100, options).Pages).Content;
        options.DrawViewLabels = false;
        Assert.Equal(Assert.Single(SheetComposer.Render(document, 100, options).Pages).Content, hidden);
    }

    [SkippableFact]
    public void Render_SavedTitleNamesTheScaleTheDrawingWasFittedTo()
    {
        var font = PdfFonts.TryFind(out _) ?? throw new SkipException("An embedded PDF font is required.");
        var viewport = Viewport(700, 100, 400, 300, 300);
        viewport.ViewportLabelOrigin = [0.25, 0.25, 0];
        var label = new TfBlockDeserializer.TitleBlockLabel(0, 0, 0, 0, 1, 1, 0, 0.03, 0.3, 0, 0,
            (int)BuiltInParameter.VIEWPORT_SCALE, "", "");
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            // A 200 m wall is 2000 mm at the view's 1:100, wider than the 648 mm the sheet leaves for a drawing.
            Elements =
            [
                Sheet(100, "A101", "Plans"), TitleBlockType(300), TitleBlock(301, 100, 300),
                View(400, "Level 1", 500), Level(500, "Level 1"), Wall(600, 500, 0, 0, 200000, 0), viewport,
            ],
            ViewportTitles = new Dictionary<int, RevitFileInfo.ViewportTitle> { [700] = new(800, true, false, null) },
            ViewportTitleStyles = new Dictionary<int, RevitFileInfo.ViewportTitleStyle>
            {
                [800] = new([(label, 24, false)], [], [])
            },
        });
        var content = PlanPrinter.CollectLevel(document, 500, 400)!;
        double fitted = 100 * Math.Max((content.MaxX - content.MinX) / 100 / 648, (content.MaxY - content.MinY) / 100 / 584);
        int printed = (int)Math.Round(fitted);
        Assert.True(printed > 150, "The drawing must be reduced for the test to tell the scales apart.");
        string Glyphs(string text) => "<" + string.Concat(text.Select(c => font.GlyphOf(c).ToString("X4"))) + "> Tj";

        string pageContent = Assert.Single(SheetComposer.Render(document, 100).Pages).Content;

        Assert.Contains(Glyphs($" 1 : {printed}"), pageContent);
        Assert.DoesNotContain(Glyphs(" 1 : 100"), pageContent);
    }

    [Fact]
    public void Render_DrawsTheSuppliedImageOnItsViewport()
    {
        var image = new PdfRasterImage(1, 1, [20, 40, 60]);
        var document = Document(Sheet(100, "A101", "Site"), Sheet(101, "A102", "Plans"),
            Viewport(200, 100, 300, 50, 60), View(300, "Analysis", 0));
        var options = new SheetComposeOptions
        {
            ViewportImages = new Dictionary<int, SheetViewportImage> { [200] = new(image, 20, 30, 40, 50) },
        };

        var pages = SheetComposer.RenderSheets(document, [100, 101], options).Pages;

        Assert.Same(image, Assert.Single(pages[0].Images));
        Assert.Empty(pages[1].Images);
        Assert.Throws<ArgumentException>(() => SheetComposer.RenderSheets(document, [101], options));
    }

    private static RvtDocument TwoSheetsWithViewports() => Document(
        Sheet(100, "A101", "Plans"), Sheet(101, "A102", "Sections"),
        Viewport(200, 100, 300, 50, 60), Viewport(201, 101, 300, 50, 60), View(300, "Analysis", 0));

    private static readonly PdfRasterImage SuppliedImage = new(1, 1, [20, 40, 60]);

    [Fact]
    public void ViewportImageProblem_AcceptsAnImageForAViewportOfTheSelectedSheets()
    {
        var document = TwoSheetsWithViewports();
        var images = new Dictionary<int, SheetViewportImage> { [200] = new(SuppliedImage, 20, 30, 40, 50) };

        Assert.Null(SheetComposer.ViewportImageProblem(document, [100], images));
        Assert.Null(SheetComposer.ViewportImageProblem(document, [100], null));
        Assert.Null(SheetComposer.ViewportImageProblem(document, [100, 101], images));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(300)]
    [InlineData(201)]
    public void RenderSheets_RefusesAnImageForAnElementThatIsNotAViewportOfTheSelectedSheets(int id)
    {
        var document = TwoSheetsWithViewports();
        var options = new SheetComposeOptions
        {
            ViewportImages = new Dictionary<int, SheetViewportImage> { [id] = new(SuppliedImage, 20, 30, 40, 50) },
        };

        var exception = Assert.Throws<ArgumentException>(() => SheetComposer.RenderSheets(document, [100], options));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains($"Element {id} is not a viewport on the selected sheets.", exception.Message);
    }

    [Fact]
    public void RenderSheets_RefusesAnImageForAnElementOfTheSheetThatPlacesNoView()
    {
        // The element belongs to the selected sheet, but it is not a viewport: it places no view.
        var document = Document(Sheet(100, "A101", "Plans"),
            new Element { Id = 210, CategoryId = (int)BuiltInCategory.OST_Viewports, SerializedOwnerDbViewId = 100 });
        var options = new SheetComposeOptions
        {
            ViewportImages = new Dictionary<int, SheetViewportImage> { [210] = new(SuppliedImage, 20, 30, 40, 50) },
        };

        var exception = Assert.Throws<ArgumentException>(() => SheetComposer.RenderSheets(document, [100], options));

        Assert.Contains("Element 210 is not a viewport on the selected sheets.", exception.Message);
    }

    [Fact]
    public void RenderSheets_RefusesAViewportWithoutAnImage()
    {
        var document = TwoSheetsWithViewports();
        foreach (var entry in new SheetViewportImage?[] { null, new(null!, 20, 30, 40, 50) })
        {
            var options = new SheetComposeOptions
            {
                ViewportImages = new Dictionary<int, SheetViewportImage> { [200] = entry! },
            };

            var exception = Assert.Throws<ArgumentException>(() => SheetComposer.RenderSheets(document, [100], options));

            Assert.Contains("Viewport 200 has no image.", exception.Message);
        }
    }

    [Theory]
    [InlineData(double.NaN, 30, 40, 50)]
    [InlineData(20, double.PositiveInfinity, 40, 50)]
    [InlineData(20, 30, 0, 50)]
    [InlineData(20, 30, -40, 50)]
    [InlineData(20, 30, double.NaN, 50)]
    [InlineData(20, 30, 40, 0)]
    [InlineData(20, 30, 40, -50)]
    [InlineData(20, 30, 40, double.PositiveInfinity)]
    public void RenderSheets_RefusesAnImageWithBoundsThatCannotBeDrawn(double x, double y, double width, double height)
    {
        var document = TwoSheetsWithViewports();
        var options = new SheetComposeOptions
        {
            ViewportImages = new Dictionary<int, SheetViewportImage> { [200] = new(SuppliedImage, x, y, width, height) },
        };

        var exception = Assert.Throws<ArgumentException>(() => SheetComposer.RenderSheets(document, [100], options));

        Assert.Contains("Viewport 200 needs finite image bounds with a positive width and height.", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Render_RejectsInvalidViewportImages(int invalid)
    {
        var document = Document(Sheet(100, "A101", "Site"),
            Viewport(200, 100, 300, 50, 60), View(300, "Analysis", 0));
        var valid = new SheetViewportImage(new PdfRasterImage(1, 1, [20, 40, 60]), 20, 30, 40, 50);
        var image = invalid switch
        {
            0 => null!,
            1 => valid with { Image = null! },
            2 => valid with { X = double.NaN },
            _ => valid with { Height = 0 },
        };
        var options = new SheetComposeOptions
        {
            ViewportImages = new Dictionary<int, SheetViewportImage> { [200] = image },
        };

        Assert.Throws<ArgumentException>(() => SheetComposer.Render(document, 100, options));
    }

    [SkippableTheory]
    [InlineData("en-US", 2, 0)]
    [InlineData("ru-RU", 1, 1)]
    public void Render_LocalizesEvaluatedScheduleBooleansAndKeepsLiteralText(string culture, int englishCopies, int russianCopies)
    {
        if (PdfFonts.TryFind(out _) is not { } font || font.GlyphOf('Н') == 0)
            throw new SkipException("A PDF font that supports Cyrillic is required.");
        var document = Document(Sheet(100, "A100", "Test schedules"), TitleBlockType(300), TitleBlock(301, 100, 300),
            new Element { Id = 200, CategoryId = (int)BuiltInCategory.OST_ScheduleGraphics, SerializedOwnerDbViewId = 100 });
        document.RawData.ScheduleInstances = new Dictionary<int, (int, double, double)> { [200] = (210, 0.4, 1.4) };
        document.RawData.ScheduleTables = new Dictionary<int, TfBlockDeserializer.ScheduleTableDef>
        {
            [210] = new([("Status", 0.3), ("Comment", 0.6)], (int)BuiltInCategory.OST_GenericModel,
                false, false, true, true, 0, 0, 0, [], [new([], 0, 2)], 0),
        };
        document.RawData.EvaluatedScheduleRows = new Dictionary<int, List<List<RevitFileInfo.ScheduleCellValue>>>
        {
            [210] = [[new("", false), new("No")], [new("", true), new("Yes")]],
        };

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var page = Assert.Single(SheetComposer.Render(document, 100).Pages);

            Assert.Equal(englishCopies, Count("No"));
            Assert.Equal(englishCopies, Count("Yes"));
            Assert.Equal(russianCopies, Count("Нет"));
            Assert.Equal(russianCopies, Count("Да"));

            int Count(string text)
            {
                string glyphs = string.Concat(text.Select(c => font.GlyphOf(c).ToString("X4", CultureInfo.InvariantCulture)));
                return page.Content.Split('\n').Count(line => line.EndsWith($"<{glyphs}> Tj ET", StringComparison.Ordinal));
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Render_UsesSavedCameraAndModelBoundsForAnalysis()
    {
        var view = View(300, "Solar Analysis", 0);
        view.Name = "Solar Analysis";
        view.SerializedOwnElementClassId = 986;
        view.ViewCrop = new(0, 0, 10, [0, 1, 0], [-1, 0, 0], [0, 0, -1], -100, -100, 100, 100, false);
        view.OrthographicModelBounds = new([0, 0, 0], [[1, 0, 0], [0, 1, 0], [0, 0, 1]],
            [3, 0, 4, 0, 4, 0], [true, true, true, true, true, true]);
        var viewport = Viewport(200, 100, 300, 50, 60);
        viewport.ViewportScale = 200;
        var foreground = new GeoFace
        {
            Normal = [0, 0, 1], RenderStyleId = 501,
            OuterVertices = [[0, 0, 2], [4, 0, 2], [4, 4, 2], [0, 4, 2]],
            InnerVertices = [[[1, 1, 2], [1, 2, 2], [2, 2, 2], [2, 1, 2]]],
        };
        var background = new GeoFace
        {
            Normal = [0, 0, 1], RenderStyleId = 502,
            OuterVertices = [[0, 0, 1], [4, 0, 1], [4, 4, 1], [0, 4, 1]],
        };
        var model = new Element
        {
            Id = 400,
            CategoryId = (int)BuiltInCategory.OST_Roofs,
            Geometry = new() { Faces = [foreground, background] },
        };
        var red = new Element { Id = 501 };
        Add(red, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0x0000ff);
        var blue = new Element { Id = 502 };
        Add(blue, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0xff0000);
        var document = Document(Sheet(100, "A101", "Site"), viewport, view, model, red, blue);

        var page = Assert.Single(SheetComposer.Render(document, 100).Pages);
        var image = Assert.IsType<PdfRasterImage>(Assert.Single(page.Images));
        Assert.True(image.TransparentWhite);
        Assert.Equal(35, image.Width);
        Assert.Equal(26, image.Height);
        Assert.Equal(new byte[] { 255, 0, 0 }, image.Rgb[..3]);
        int hole = 3 * (13 * image.Width + 13);
        Assert.Equal(new byte[] { 0, 0, 255 }, image.Rgb[hole..(hole + 3)]);

        var supplied = new PdfRasterImage(1, 1, [20, 40, 60]);
        var options = new SheetComposeOptions
        {
            ViewportImages = new Dictionary<int, SheetViewportImage> { [200] = new(supplied, 20, 30, 40, 50) },
        };
        var withOverride = Assert.Single(SheetComposer.Render(document, 100, options).Pages);
        Assert.Same(supplied, Assert.Single(withOverride.Images));
    }

    [Fact]
    public void ShadedViewport_CompositesIntersectingPanesInPixelDepthOrder()
    {
        var view = View(300, "Solar Analysis", 0);
        view.ViewCrop = new(0, 0, 10, [1, 0, 0], [0, 1, 0], [0, 0, -1], -100, -100, 100, 100, false);
        view.OrthographicModelBounds = new([0, 0, 0], [[1, 0, 0], [0, 1, 0], [0, 0, 1]],
            [2, -2, 1, -1, 6, -2], [true, true, true, true, true, true]);
        GeoFace Surface(int material, double left, double right, double height, double slope) => new()
        {
            RenderStyleId = material,
            Normal = [-slope, 0, 1],
            OuterVertices = [[left, -1, height + slope * left], [right, -1, height + slope * right],
                [right, 1, height + slope * right], [left, 1, height + slope * left]],
        };
        var redPane = Surface(501, -2, 2, 2, -1);
        redPane.InnerVertices =
        [
            [[-1.5, 0.2, 3.5], [-0.5, 0.2, 2.5], [-0.5, 0.8, 2.5], [-1.5, 0.8, 3.5]],
        ];
        var model = new Element
        {
            Id = 400,
            CategoryId = (int)BuiltInCategory.OST_Windows,
            Geometry = new()
            {
                Faces = [Surface(502, -2, 2, 2, 1), Surface(503, -2, 2, -1, 0),
                    Surface(504, 1.45, 2, 5, 0), redPane, Surface(505, -2, 2, 6, 0)],
            },
        };
        var red = new Element { Id = 501, SerializedMaterialTransparency = 0.5, SerializedMaterialUsesRenderAppearance = false };
        var green = new Element { Id = 504, SerializedMaterialTransparency = 0.75, SerializedMaterialUsesRenderAppearance = true };
        var blue = new Element { Id = 502 };
        var black = new Element { Id = 503 };
        var clear = new Element { Id = 505 };
        Add(red, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0x0000ff);
        Add(blue, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0xff0000);
        Add(blue, BuiltInParameter.MATERIAL_PARAM_TRANSPARENCY, StorageType.Integer, 50);
        Add(green, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0x00ff00);
        Add(black, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0);
        Add(clear, BuiltInParameter.MATERIAL_PARAM_TRANSPARENCY, StorageType.Integer, 100);
        var document = Document(view, model, red, green, blue, black, clear);
        var page = new PdfDocument().AddPage(new PdfPageSize(200, 200));

        ShadedViewport.Render(page, document, view, 100, 100, 200);

        var image = Assert.IsType<PdfRasterImage>(Assert.Single(page.Images));
        Assert.Equal(new byte[] { 128, 0, 64 }, Pixel(image.Width / 4, image.Height / 2));
        Assert.Equal(new byte[] { 64, 0, 128 }, Pixel(3 * image.Width / 4, image.Height / 2));
        Assert.Equal(new byte[] { 0, 0, 128 }, Pixel(image.Width / 4, image.Height / 4));
        Assert.Equal(new byte[] { 0, 255, 0 }, Pixel(image.Width - 1, image.Height / 2));

        byte[] Pixel(int x, int y)
        {
            int i = 3 * (y * image.Width + x);
            return image.Rgb[i..(i + 3)];
        }
    }

    [Fact]
    public void ShadedViewport_PrintsModelPatternsAndSavedEdgesOnlyWhereTheirFaceIsVisible()
    {
        var view = View(300, "Solar Analysis", 0);
        view.ViewCrop = new(0, 0, 10, [1, 0, 0], [0, 1, 0], [0, 0, -1], -100, -100, 100, 100, false);
        view.OrthographicModelBounds = new([0, 0, 0], [[1, 0, 0], [0, 1, 0], [0, 0, 1]],
            [2, -1, 2, -2, 5, 0], [true, true, true, true, true, true]);
        view.SolarStudy = new([-2, -2, 0, 2, 2, 3], 150, 0, 0, 0, 0, 0, 0,
            ShowSurfacePatterns: true, ShowEdges: true);
        List<double[]> loop = [[-2, -2, 1], [2, -2, 1], [2, 2, 1], [-2, 2, 1]];
        var roof = new GeoFace
        {
            Normal = [0, 0, 1], OuterVertices = loop, SourceLoops = [loop], PlaneSurface = true,
            ModelPattern = new(501, [0, 0, 1], [1, 0, 0], [0, 1, 0]),
        };
        var cover = new GeoFace
        {
            Normal = [0, 0, 1], RenderStyleId = 502,
            OuterVertices = [[0.6, -0.5, 2], [1.4, -0.5, 2], [1.4, 0.5, 2], [0.6, 0.5, 2]],
        };
        var red = new Element { Id = 502 };
        Add(red, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0x0000ff);
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [view, red, new Element
            {
                Id = 400, CategoryId = (int)BuiltInCategory.OST_Roofs, Geometry = new() { Faces = [roof, cover] },
            }],
            FillPatterns = new Dictionary<int, (string Name, IReadOnlyList<TfBlockDeserializer.PatternGrid> Grids)>
            {
                [501] = ("Seam", [new(0, 0, 0, 0, 1, [], 1, 0)]),
            },
        });
        var page = new PdfDocument().AddPage(new PdfPageSize(200, 200));
        ShadedViewport.Render(page, document, view, 100, 100, 100);
        var image = Assert.IsType<PdfRasterImage>(Assert.Single(page.Images));

        byte Red(int x, int y) => image.Rgb[3 * (y * image.Width + x)];
        Assert.True(Red(image.Width / 3, image.Height / 2) < 192);
        Assert.Equal((byte)255, Red(2 * image.Width / 3, image.Height / 2));
        Assert.Equal((byte)192, Red(0, 3 * image.Height / 8));
        Assert.True(Red(image.Width - 1, 3 * image.Height / 8) < 192);
    }

    [Fact]
    public void ShadedViewport_UsesTheModelBoxBottomForTerrainCuts()
    {
        var view = View(300, "Solar Analysis", 0);
        view.ViewCrop = new(0, 0, 10, [1, 0, 0], [0, 0.6, 0.8], [0, 0.8, -0.6], -100, -100, 100, 100, false);
        view.OrthographicModelBounds = new([0, 1, 0], [[1, 0, 0], [0, 1, 0], [0, 0, 1]],
            [4, 0, 3, 0, 4, 0], [true, true, true, true, true, true]);
        var terrain = new Element
        {
            Id = 400,
            CategoryId = (int)BuiltInCategory.OST_Topography,
            Geometry = new()
            {
                Faces = [new() { Normal = [0, 0, 1], OuterVertices = [[0, 0, 2], [4, 0, 2], [4, 4, 2], [0, 4, 2]] }],
            },
        };
        var soil = new Element { Id = 501 };
        Add(soil, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0x243858);
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [view, terrain, soil],
            SiteCutGraphics = (-10, 501),
        });
        var page = new PdfDocument().AddPage(new PdfPageSize(200, 200));

        var frame = ShadedViewport.Render(page, document, view, 50, 60, 100);

        var bounds = Assert.IsType<(double X, double Y, double Width, double Height)>(frame);
        Assert.Equal(60 + 0.6 * 3.048 - 8 * 3.048, bounds.Y, 8);
        var image = Assert.IsType<PdfRasterImage>(Assert.Single(page.Images));
        Assert.Equal(new byte[] { 88, 56, 36 }, image.Rgb[^3..]);
    }

    [Fact]
    public void KeepSiteNearBuilding_KeepsEverythingWithoutABuildingExtent()
    {
        var site = Site(topography: new() { new() { (0, 0), (100, 0), (50, 50) } });

        SitePrinter.SiteContent kept = SheetComposer.KeepSiteNearBuilding(site, Building(5, 5, 5, 5));

        Assert.Same(site, kept);
    }
}
