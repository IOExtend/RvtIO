using System.Text.Json;
using RvtIO.BuiltIns;
using RvtIO.Cli;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using RvtIO.Sheets;
using Xunit;

namespace RvtIO.Tests;

public class NativeIdSheetsAndPrintTests
{
    private const long WideSheetNativeId = 5_000_000_101L;
    private const long WideViewportNativeId = 5_000_000_102L;
    private const long WideLevelNativeId = 5_000_000_103L;
    private const long WidePlanViewNativeId = 5_000_000_104L;
    private const long WideElevationViewNativeId = 5_000_000_105L;
    private const long WideSheetCurveNativeId = 5_000_000_106L;

    private static RevitFileInfo BuildWideSyntheticProject()
    {
        var info = new RevitFileInfo();

        var normalSheet = new Element
        {
            Id = 10,
            CategoryId = -2003100,
            Parameters =
            [
                BipHelpers.MakeBipParam((int)BuiltInParameter.SHEET_NUMBER, "SHEET_NUMBER", StorageType.String, "A100"),
                BipHelpers.MakeBipParam((int)BuiltInParameter.SHEET_NAME, "SHEET_NAME", StorageType.String, "Cover"),
                BipHelpers.MakeBipParam((int)BuiltInParameter.VIEW_SCALE, "VIEW_SCALE", StorageType.Integer, 100),
            ],
        };
        var normalCurve = new Element
        {
            Id = 11,
            SerializedOwnerDbViewId = 10,
            Location = new LocationCurve { CurveType = "Line", StartPoint = [0, 0, 0], EndPoint = [1.0, 1.0, 0] },
        };

        var wideSheet = new Element
        {
            Id = 201,
            NativeId = WideSheetNativeId,
            CategoryId = -2003100,
            Parameters =
            [
                BipHelpers.MakeBipParam((int)BuiltInParameter.SHEET_NUMBER, "SHEET_NUMBER", StorageType.String, "A101"),
                BipHelpers.MakeBipParam((int)BuiltInParameter.SHEET_NAME, "SHEET_NAME", StorageType.String, "Ground Plan"),
                BipHelpers.MakeBipParam((int)BuiltInParameter.VIEW_SCALE, "VIEW_SCALE", StorageType.Integer, 100),
                new Parameter
                {
                    Id = 1000, Definition = new Definition { Id = 1000, Name = "Zone" },
                    StorageType = StorageType.String, Value = "North wing",
                },
            ],
        };
        var wideSheetCurve = new Element
        {
            Id = 211,
            NativeId = WideSheetCurveNativeId,
            SerializedOwnerDbViewId = 201,
            Location = new LocationCurve { CurveType = "Line", StartPoint = [0, 0, 0], EndPoint = [1.0, 1.0, 0] },
        };
        var wideViewport = new Element
        {
            Id = 202,
            NativeId = WideViewportNativeId,
            CategoryId = -2000510,
            References = [201],
            Parameters =
            [
                BipHelpers.MakeBipParam((int)BuiltInParameter.VIEWPORT_DETAIL_NUMBER, "VIEWPORT_DETAIL_NUMBER", StorageType.String, "1"),
            ],
        };

        var wideLevel = new Element
        {
            Id = 203,
            NativeId = WideLevelNativeId,
            CategoryId = -2000240,
            Name = "Level 1",
            IsNativeLevel = true,
            Parameters =
            [
                BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV, "LEVEL_ELEV", StorageType.Double, 0.0),
            ],
        };
        var wall = new Element
        {
            Id = 212,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Location = new LocationCurve { CurveType = "Line", StartPoint = [0, 0, 0], EndPoint = [10, 0, 0] },
            Parameters =
            [
                BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_PARAM, "LEVEL_PARAM", StorageType.ElementId, 203),
            ],
        };
        var widePlanView = new Element
        {
            Id = 204,
            NativeId = WidePlanViewNativeId,
            CategoryId = (int)BuiltInCategory.OST_Views,
            NativeRecordClass = 1005,
            Name = "Level 1",
            Parameters =
            [
                BipHelpers.MakeBipParam((int)BuiltInParameter.VIEW_NAME, "VIEW_NAME", StorageType.String, "Level 1"),
                BipHelpers.MakeBipParam((int)BuiltInParameter.VIEW_SCALE, "VIEW_SCALE", StorageType.Integer, 100),
            ],
        };
        var wideElevationView = new Element
        {
            Id = 205,
            NativeId = WideElevationViewNativeId,
            CategoryId = (int)BuiltInCategory.OST_Views,
            NativeRecordClass = 1016,
            Name = "North",
            Parameters =
            [
                BipHelpers.MakeBipParam((int)BuiltInParameter.VIEW_NAME, "VIEW_NAME", StorageType.String, "North"),
                BipHelpers.MakeBipParam((int)BuiltInParameter.VIEW_SCALE, "VIEW_SCALE", StorageType.Integer, 100),
            ],
        };

        info.Elements.AddRange([normalSheet, normalCurve, wideSheet, wideSheetCurve, wideViewport, wideLevel, wall, widePlanView, wideElevationView]);
        return info;
    }

    [Fact]
    public void SheetCatalog_And_SheetsCommand_ExposeLosslessSourceElementId_InJsonAndCsv()
    {
        RevitFileInfo info = BuildWideSyntheticProject();
        SheetCatalog catalog = SheetCatalog.Extract(info);

        SheetRecord normal = Assert.Single(catalog.Sheets, s => s.ElementId == 10);
        Assert.Null(normal.SourceElementId);

        SheetRecord wide = Assert.Single(catalog.Sheets, s => s.ElementId == 201);
        Assert.Equal("5000000101", wide.SourceElementId);

        ViewportRecord vp = Assert.Single(catalog.Viewports);
        Assert.Equal(202, vp.ElementId);
        Assert.Equal("5000000102", vp.SourceElementId);
        Assert.Equal(201, vp.SheetId);
        Assert.Equal("5000000101", vp.SourceSheetId);

        // Owned elements of a sheet map back to Revit too; only the renumbered ones are listed.
        Assert.Null(normal.OwnedElementSourceIds);
        Assert.Equal("5000000106", Assert.Single(wide.OwnedElementSourceIds!, pair => pair.Key == 211).Value);

        string json = SheetsCommand.ToJson(catalog);
        using var parsed = JsonDocument.Parse(json);
        var jsonSheets = parsed.RootElement.GetProperty("Sheets").EnumerateArray().ToList();
        Assert.False(jsonSheets[0].TryGetProperty("SourceElementId", out _));
        Assert.Equal(JsonValueKind.String, jsonSheets[1].GetProperty("SourceElementId").ValueKind);
        Assert.Equal("5000000101", jsonSheets[1].GetProperty("SourceElementId").GetString());

        Assert.False(jsonSheets[0].TryGetProperty("OwnedElementSourceIds", out _));
        Assert.Equal("5000000106", jsonSheets[1].GetProperty("OwnedElementSourceIds").GetProperty("211").GetString());

        var jsonVp = parsed.RootElement.GetProperty("Viewports").EnumerateArray().Single();
        Assert.Equal("5000000102", jsonVp.GetProperty("SourceElementId").GetString());
        Assert.Equal("5000000101", jsonVp.GetProperty("SourceSheetId").GetString());

        string csv = SheetsCommand.ToCsv(catalog);
        string[] lines = csv.Split(Environment.NewLine);
        // The new column is last, so the position of every earlier column is unchanged.
        Assert.Equal("ElementId,Number,Name,IssueDate,Scale,DrawnBy,DesignedBy,CheckedBy,ApprovedBy,Zone,SourceElementId", lines[0]);
        Assert.Equal("10,A100,Cover,,100,,,,,,", lines[1]);
        Assert.Equal("201,A101,Ground Plan,,100,,,,,North wing,5000000101", lines[2]);
    }

    [Fact]
    public void PrintRecords_OmitTheSourceIdFromJson_WhenTheIdWasNotRenumbered()
    {
        // ComposedSheet is left out: its PdfPageSize.Landscape makes System.Text.Json report a cycle.
        object[] unchanged =
        [
            new PlanLevel(1, "Level 1", 0), new PlanView(2, "Level 1", 1, 100), new ElevationView(3, "North", "north", 100),
            new PrintableSheet(4, "A1", "Cover", 1, 1, 1, 0),
        ];

        foreach (object record in unchanged)
        {
            string json = JsonSerializer.Serialize(record, record.GetType());
            Assert.DoesNotContain("SourceElementId", json);
            Assert.DoesNotContain("SourceLevelId", json);
        }
        Assert.Contains("\"SourceLevelId\":\"5000000103\"",
            JsonSerializer.Serialize(new PlanView(2, "Level 1", 1, 100) { SourceLevelId = "5000000103" }));
    }

    [Fact]
    public void PrintModels_And_CliListingsAndSelectors_ExposeAndAcceptNative64BitIds()
    {
        RvtDocument document = RvtDocument.FromInfo(BuildWideSyntheticProject());

        IReadOnlyList<PlanLevel> levels = PlanPrinter.Levels(document);
        PlanLevel level = Assert.Single(levels);
        Assert.Equal(203, level.LevelId);
        Assert.Equal("5000000103", level.SourceElementId);

        IReadOnlyList<PlanView> planViews = PlanPrinter.Views(document);
        PlanView planView = Assert.Single(planViews);
        Assert.Equal(204, planView.ViewId);
        Assert.Equal("5000000104", planView.SourceElementId);
        Assert.Equal("5000000103", planView.SourceLevelId);

        IReadOnlyList<ElevationView> elevations = ElevationPrinter.Views(document);
        ElevationView elevation = Assert.Single(elevations);
        Assert.Equal(205, elevation.ViewId);
        Assert.Equal("5000000105", elevation.SourceElementId);

        IReadOnlyList<PrintableSheet> printableSheets = SheetPrinter.Sheets(document);
        PrintableSheet widePrintable = Assert.Single(printableSheets, s => s.SheetId == 201);
        Assert.Equal("5000000101", widePrintable.SourceElementId);
        Assert.Null(Assert.Single(printableSheets, s => s.SheetId == 10).SourceElementId);

        IReadOnlyList<ComposedSheet> composedSheets = SheetComposer.Sheets(document);
        ComposedSheet wideComposed = Assert.Single(composedSheets, s => s.SheetId == 201);
        Assert.Equal("5000000101", wideComposed.SourceElementId);

        var sheetsOut = new StringWriter();
        var sheetsErr = new StringWriter();
        PrintCommand.WriteListedSheets(printableSheets, composedSheets, sheetsOut, sheetsErr);
        string sheetLines = sheetsOut.ToString();
        string normalSheetLine = sheetLines.Split('\n').First(l => l.StartsWith("10\t", StringComparison.Ordinal));
        Assert.DoesNotContain("source=", normalSheetLine);
        string wideSheetLine = sheetLines.Split('\n').First(l => l.StartsWith("201\t", StringComparison.Ordinal));
        Assert.Contains("source=5000000101", wideSheetLine);

        var levelsOut = new StringWriter();
        PrintCommand.WriteListedLevels(levels, levelsOut);
        Assert.Contains("203\tLevel 1\t0 mm\tsource=5000000103", levelsOut.ToString());

        var viewsOut = new StringWriter();
        var viewsErr = new StringWriter();
        PrintCommand.WriteListedViews(document, planViews, elevations, viewsOut, viewsErr);
        string viewLines = viewsOut.ToString();
        Assert.Contains("204\tLevel 1\t1:100\tlevel 203\tplan\tsource=5000000104\tlevel-source=5000000103", viewLines);
        Assert.Contains("205\tNorth\t1:100\tnorth\televation\tsource=5000000105", viewLines);

        // Selectors accept local 32-bit key, wide 64-bit native ID, explicit prefixes, and numbers/names.
        Assert.Equal([201], PrintCommand.SelectExplicitSheets(["201"], printableSheets, composedSheets, null).ComposedIds);
        Assert.Equal([201], PrintCommand.SelectExplicitSheets(["5000000101"], printableSheets, composedSheets, null).ComposedIds);
        Assert.Equal([201], PrintCommand.SelectExplicitSheets(["source:5000000101"], printableSheets, composedSheets, null).ComposedIds);
        Assert.Equal([201], PrintCommand.SelectExplicitSheets(["native:5000000101"], printableSheets, composedSheets, null).ComposedIds);
        Assert.Equal([10], PrintCommand.SelectExplicitSheets(["source:10"], printableSheets, composedSheets, null).ComposedIds);
        Assert.Equal("source:201", PrintCommand.SelectExplicitSheets(["source:201"], printableSheets, composedSheets, null).Missing);
        Assert.Equal("5000000999", PrintCommand.SelectExplicitSheets(["5000000999"], printableSheets, composedSheets, null).Missing);

        Assert.Equal(203, PrintCommand.ResolveLevelId("203", levels));
        Assert.Equal(203, PrintCommand.ResolveLevelId("5000000103", levels));
        Assert.Equal(203, PrintCommand.ResolveLevelId("source:5000000103", levels));
        Assert.Equal(203, PrintCommand.ResolveLevelId("native:5000000103", levels));
        Assert.Null(PrintCommand.ResolveLevelId("source:203", levels));

        Assert.Equal(204, PrintCommand.ResolveView("5000000104", planViews, elevations));
        Assert.Equal(204, PrintCommand.ResolveView("source:5000000104", planViews, elevations));
        Assert.Equal(205, PrintCommand.ResolveView("5000000105", planViews, elevations));
        Assert.Equal(205, PrintCommand.ResolveView("native:5000000105", planViews, elevations));
        Assert.Null(PrintCommand.ResolveView("source:205", planViews, elevations));
    }
}
