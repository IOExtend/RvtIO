using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Schedule instances and their tables (TfBlockDeserializer.TryExtractScheduleInstance /
/// TryExtractScheduleTable): per-sheet ScheduleInstance placed at its instance origin, revision schedules
/// with the model Revisions() rows, header and grid always drawn (rvt-prt schedules.py + tables.py).</summary>
public class ScheduleTableTests
{
    [SkippableFact]
    public void ScheduleTextSize_UsesCapHeightAndRejectsUnsupportedStyles()
    {
        var font = PdfFonts.TryFind(out _) ?? throw new SkipException("No TrueType font was found on this machine.");
        if (font.CapHeight <= 0) throw new SkipException("The available font does not declare its cap height.");
        double pixels = 16 * 96d / 72 * font.CapHeight / font.UnitsPerEm;

        var centered = new TfBlockDeserializer.ScheduleTextStyle(pixels, 1, 4);
        Assert.Equal(16 * 25.4 / 72, SheetComposer.ScheduleTextSize(font, centered)!.Value, 6);
        Assert.Equal(16 * 25.4 / 72, SheetComposer.ScheduleTextSize(font, centered with { VerticalAlignment = 8 })!.Value, 6);
        Assert.Null(SheetComposer.ScheduleTextSize(font, centered with { VerticalAlignment = 0 }));
        Assert.Null(SheetComposer.ScheduleTextSize(font, centered with { CapHeightPixels = double.NaN }));
        Assert.Null(SheetComposer.ScheduleTextSize(null, centered));
    }

    [SkippableFact]
    public void SampleFile_DecodesScheduleInstancesAtTheirOrigins()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var instances = document.RawData.ScheduleInstances;
        Assert.NotNull(instances);
        // The instance records include the title-block revision template and the schedules placed on sheets.
        Assert.Equal(9, instances!.Count);
        Assert.True(instances.ContainsKey(970093), "How-do-I instance 970093 did not decode.");
        Assert.Equal(970087, instances[970093].ScheduleId);
        Assert.Equal(0.1175, instances[970093].Ox, 3);
        Assert.Equal(1.0111, instances[970093].Oy, 3);
        Assert.True(instances.ContainsKey(959497), "Planting instance 959497 did not decode.");
        Assert.Equal(959491, instances[959497].ScheduleId);
        var tables = Assert.IsAssignableFrom<IReadOnlyDictionary<int, TfBlockDeserializer.ScheduleTableDef>>(
            document.RawData.ScheduleTables);
        var revisions = instances.Where(pair =>
            tables.GetValueOrDefault(pair.Value.ScheduleId)?.CategoryId == (int)BuiltInCategory.OST_Revisions);
        var sheets = SheetComposer.Sheets(document);
        Assert.Equal(6, sheets.Count);
        foreach (var sheet in sheets)
        {
            var entry = Assert.Single(revisions, pair => document.GetElement(pair.Key)?.OwnerViewId == sheet.SheetId);
            Assert.Equal(2.3770, entry.Value.Ox, 3);
            Assert.Equal(1.0458, entry.Value.Oy, 3);
        }
    }

    [SkippableFact]
    public void SampleFile_DecodesScheduleTablesWithColumnsAndFlags()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var tables = document.RawData.ScheduleTables;
        Assert.NotNull(tables);
        Assert.Equal(9, tables!.Count);

        // The revision schedules: No. / Description / Date, headers and grid shown, no title or outline.
        var revisions = tables.Values.Where(table => table.CategoryId == (int)BuiltInCategory.OST_Revisions);
        Assert.Equal(7, revisions.Count());
        Assert.All(revisions, revision =>
        {
            Assert.False(revision.ShowTitle);
            Assert.True(revision.ShowHeaders);
            Assert.True(revision.ShowGridLines);
            Assert.False(revision.ShowOutline);
            Assert.Equal(["No.", "Description", "Date"], revision.Columns.Select(column => column.Heading).ToList());
            Assert.Equal(0.05886, revision.Columns[0].WidthFt, 4);
            Assert.Equal(0.17060, revision.Columns[1].WidthFt, 4);
            Assert.Equal(0.07894, revision.Columns[2].WidthFt, 4);
            // The fixed height the empty body grid pads to (ScheduleSegmentRec, 21 rows at 16.30 pt).
            Assert.Equal(0.4340, revision.FixedHeightFt, 4);
            var caps = Assert.IsType<(double Header, double Body)>(revision.RevisionCapHeightsFt);
            Assert.Equal(5, caps.Header * 304.8, 8);
            Assert.Equal(3, caps.Body * 304.8, 8);
            Assert.Equal(0, revision.TitleHeightFt);
            Assert.Empty(revision.TitleBands);
            var revisionHeader = Assert.Single(revision.BodySections);
            Assert.Equal(["No.", "Description", "Date"], revisionHeader.Header);
            Assert.All(revisionHeader.HeaderStyles!, style =>
            {
                Assert.Equal(5 * 96 / 25.4, style.CapHeightPixels, 8);
                Assert.Equal(new TfBlockDeserializer.ScheduleTextStyle(0, 1, 8, "Arial", 1), style with { CapHeightPixels = 0 });
            });
        });

        // The How-do-I table: Level / Topic / What view / Reviewed? with title, headers, grid and outline.
        Assert.True(tables.ContainsKey(970087), "How-do-I table 970087 did not decode.");
        var howdoi = tables[970087];
        Assert.Equal(-2000150, howdoi.CategoryId);
        Assert.True(howdoi.ShowTitle && howdoi.ShowHeaders && howdoi.ShowGridLines && howdoi.ShowOutline);
        Assert.Equal(["Level", "Topic", "What view", "Reviewed?"],
            howdoi.Columns.Select(column => column.Heading).ToList());
        // The title area: the 0.0234 ft title row and the merged long-text band of the next two stored
        // rows (0.041 ft each); the body section holds the header and 41 body rows.
        Assert.Equal(0.0234, howdoi.TitleHeightFt, 4);
        Assert.Equal(new TfBlockDeserializer.ScheduleTextStyle(12, 1, 4, "Arial", 1), howdoi.TitleStyle);
        var band = Assert.Single(howdoi.TitleBands);
        Assert.Equal(0.0820, band.HeightFt, 4);
        Assert.Contains("To learn more", band.Text);
        Assert.Equal(4 * 96 / 25.4, band.Style.CapHeightPixels, 8);
        Assert.Equal(0, band.Style.HorizontalAlignment);
        Assert.Equal(4, band.Style.VerticalAlignment);
        var body = Assert.Single(howdoi.BodySections);
        Assert.Equal(["Level", "Topic", "What view", "Reviewed?"], body.Header);
        Assert.Equal(41, body.BodyRows);
        Assert.Equal(41, body.LeadingEmpty);
        Assert.Equal(new TfBlockDeserializer.ScheduleTextStyle(12, 0, 0, "Arial", 1), body.UniformBodyStyle);

        // The planting schedule: Type Mark / Common Name / Count.
        Assert.True(tables.ContainsKey(959491), "Planting table 959491 did not decode.");
        var planting = tables[959491];
        Assert.Equal(["Type Mark", "Common Name", "Count"],
            planting.Columns.Select(column => column.Heading).ToList());
        // The stored title height (0.0347 ft = 30 pt) and the body section with its header, one leading
        // empty row and 8 body rows in total (the empty row plus the 7 type rows with their counts).
        Assert.Equal(0.0347, planting.TitleHeightFt, 4);
        Assert.Equal(5 * 96 / 25.4, planting.TitleStyle.CapHeightPixels, 8);
        Assert.Empty(planting.TitleBands);
        var plantingBody = Assert.Single(planting.BodySections);
        Assert.Equal(["Type Mark", "Common Name", "Count"], plantingBody.Header);
        Assert.NotNull(plantingBody.HeaderStyles);
        Assert.Equal(3, plantingBody.HeaderStyles.Count);
        Assert.All(plantingBody.HeaderStyles, style =>
            Assert.Equal(new TfBlockDeserializer.ScheduleTextStyle(12, 1, 8, "Arial", 1), style));
        Assert.Equal([0, 0, 0], plantingBody.BodyAlignment);
        Assert.Equal(1, plantingBody.LeadingEmpty);
        Assert.Equal(8, plantingBody.BodyRows);
    }

    [SkippableFact]
    public void SampleFile_GroupsPlantingRowsByType()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);

        // Grouped by the resolved planting type, with the type mark and common name: T1 Honey Locust 8,
        // T2 Hawthorn 1, T3 Large Tooth Aspen 1, T4 Red Maple 2, T5 Scarlet Oak 2, T6 Red Ash 1,
        // T7 Lombardy Poplar 1 (the "Type Mark" and "Common Name" the schedule binds to).
        var rows = SheetComposer.PlantingRows(document);

        Assert.Equal(
            new[] { "T1", "T2", "T3", "T4", "T5", "T6", "T7" },
            rows.Select(row => row.Mark).ToArray());
        Assert.Equal(
            new[] { "Honey Locust", "Hawthorn", "Large Tooth Aspen", "Red Maple", "Scarlet Oak", "Red Ash", "Lombardy Poplar" },
            rows.Select(row => row.Name).ToArray());
        Assert.Equal(new[] { "8", "1", "1", "2", "2", "1", "1" }, rows.Select(row => row.Count).ToArray());
    }

    [SkippableFact]
    public void SampleFile_PlacesSchedulesOnTheirSheets()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var byId = document.Elements.ToDictionary(element => element.Id);
        // A001 (959510) carries the How-do-I table and its revision schedule; A101 (959135) the planting
        // schedule and its revision schedule.
        var a001 = SheetComposer.SchedulesOnSheet(document, byId[959510]);
        Assert.Contains(a001, schedule => schedule.ScheduleId == 970087);
        Assert.Contains(a001, schedule => schedule.CategoryId == -2006070);
        var a101 = SheetComposer.SchedulesOnSheet(document, byId[959135]);
        Assert.Contains(a101, schedule => schedule.ScheduleId == 959491);
    }

    [SkippableFact]
    public void SampleFile_DecodesNoRevisionsOnAnySheet()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        // The sample's single revision is not explicitly present on any sheet (rvt-prt sheet_revs are all
        // empty, and the reference lists no revision rows): every sheet decodes an empty set.
        var revisions = document.RawData.ScheduleSheetRevisions;
        Assert.NotNull(revisions);
        foreach (int sheetId in new[] { 382617, 503701, 958184, 958251, 959135, 959510 })
            Assert.True(revisions!.TryGetValue(sheetId, out var ids) && ids.Count == 0,
                $"Sheet {sheetId} should decode an empty revision set.");
    }
}
