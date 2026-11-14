using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Sheets;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The sheet catalog (src/RvtIO/Sheets): sheet identity, the viewports of a sheet, the elements a sheet owns
/// and the limits this release reports. The elements are built here; no Revit file is needed.</summary>
public class SheetCatalogTests
{
    private static void Add(Element element, BuiltInParameter id, string name, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, name, storage, value));

    /// <summary>A sheet: an element with the Sheet Number and Sheet Name parameters (the reader creates them only for
    /// the sheet class of the file).</summary>
    private static Element Sheet(int id, string? number = "", string? name = "", string? issueDate = null)
    {
        var sheet = new Element { Id = id, CategoryId = -2003100 };
        Add(sheet, BuiltInParameter.SHEET_NUMBER, "SHEET_NUMBER", StorageType.String, number ?? string.Empty);
        Add(sheet, BuiltInParameter.SHEET_NAME, "SHEET_NAME", StorageType.String, name ?? string.Empty);
        if (issueDate != null) Add(sheet, BuiltInParameter.SHEET_ISSUE_DATE, "SHEET_ISSUE_DATE", StorageType.String, issueDate);
        return sheet;
    }

    private static Element Viewport(int id, string detailNumber, params int[] references)
    {
        var viewport = new Element { Id = id, CategoryId = -2000510, References = references.ToList() };
        Add(viewport, BuiltInParameter.VIEWPORT_DETAIL_NUMBER, "VIEWPORT_DETAIL_NUMBER", StorageType.String, detailNumber);
        return viewport;
    }

    private static RevitFileInfo Info(params Element[] elements) => new() { Elements = elements.ToList() };

    [Fact]
    public void Extract_ReadsSheetIdentityAndByLines()
    {
        var sheet = Sheet(100, "A101", "First floor", "2026-01-31");
        Add(sheet, BuiltInParameter.VIEW_SCALE, "VIEW_SCALE", StorageType.Integer, 100);
        Add(sheet, BuiltInParameter.SHEET_DRAWN_BY, "SHEET_DRAWN_BY", StorageType.String, "AB");
        Add(sheet, BuiltInParameter.SHEET_DESIGNED_BY, "SHEET_DESIGNED_BY", StorageType.String, "CD");
        Add(sheet, BuiltInParameter.SHEET_CHECKED_BY, "SHEET_CHECKED_BY", StorageType.String, "EF");
        Add(sheet, BuiltInParameter.SHEET_APPROVED_BY, "SHEET_APPROVED_BY", StorageType.String, "GH");

        var catalog = SheetCatalog.Extract(Info(sheet));

        var record = Assert.Single(catalog.Sheets);
        Assert.Equal(100, record.ElementId);
        Assert.Equal("A101", record.Number);
        Assert.Equal("First floor", record.Name);
        Assert.Equal("2026-01-31", record.IssueDate);
        Assert.Equal(100, record.Scale);
        Assert.Equal("AB", record.DrawnBy);
        Assert.Equal("CD", record.DesignedBy);
        Assert.Equal("EF", record.CheckedBy);
        Assert.Equal("GH", record.ApprovedBy);
        Assert.Empty(record.ViewportIds);
        Assert.Empty(record.OwnedElementIds);
    }

    [Fact]
    public void Extract_EmptyNumberAndNameAreNull()
    {
        var catalog = SheetCatalog.Extract(Info(Sheet(100, "", "")));

        var record = Assert.Single(catalog.Sheets);
        Assert.Null(record.Number);
        Assert.Null(record.Name);
        Assert.Null(record.IssueDate);
        Assert.Null(record.Scale);
    }

    [Fact]
    public void Extract_QuestionMarksAreStoredValues()
    {
        // A project stores "???" as a sheet name; it is data, not a decode failure.
        var catalog = SheetCatalog.Extract(Info(Sheet(100, "???", "???")));

        var record = Assert.Single(catalog.Sheets);
        Assert.Equal("???", record.Number);
        Assert.Equal("???", record.Name);
    }

    [Fact]
    public void Extract_CategoryAloneIsNotASheet()
    {
        // Thousands of elements take the OST_Sheets category from a block class word; only the parameters make a sheet.
        var notASheet = new Element { Id = 500, CategoryId = -2003100 };

        var catalog = SheetCatalog.Extract(Info(notASheet));

        Assert.Empty(catalog.Sheets);
        Assert.Empty(catalog.Viewports);
    }

    [Fact]
    public void Extract_LinksViewportsToTheirSheet()
    {
        var sheet = Sheet(100, "A101");
        var viewport = Viewport(200, "1", 100, 200);
        var unplaced = Viewport(201, "2", 999);

        var catalog = SheetCatalog.Extract(Info(sheet, viewport, unplaced));

        var record = Assert.Single(catalog.Sheets);
        Assert.Equal(new[] { 200 }, record.ViewportIds);
        var placed = Assert.Single(catalog.Viewports);
        Assert.Equal(200, placed.ElementId);
        Assert.Equal(100, placed.SheetId);
        Assert.Equal("1", placed.DetailNumber);
        Assert.Null(placed.DisplayedViewId);
    }

    [Fact]
    public void Extract_ListsElementsOwnedByASheet()
    {
        var sheet = Sheet(100, "A101");
        Element Note(int id, int owner) => new() { Id = id, SerializedOwnerDbViewId = owner };

        var catalog = SheetCatalog.Extract(Info(sheet,
            Note(300, 100), Note(302, 100), Note(301, 100),
            Note(400, -1), Note(401, 0), Note(402, 1000), Note(100, 100)));

        var record = Assert.Single(catalog.Sheets);
        Assert.Equal(new[] { 300, 301, 302 }, record.OwnedElementIds);
    }

    [Fact]
    public void Extract_OrdersSheetsByNumberThenId()
    {
        var catalog = SheetCatalog.Extract(Info(
            Sheet(3, "B1"), Sheet(1, "A10"), Sheet(2, "A2"), Sheet(4), Sheet(5)));

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, catalog.Sheets.Select(sheet => sheet.ElementId));
        Assert.Equal(new string?[] { "A10", "A2", "B1", null, null }, catalog.Sheets.Select(sheet => sheet.Number));
    }

    [Fact]
    public void Extract_ReportsTheLimitsOfThisRelease()
    {
        var catalog = SheetCatalog.Extract(Info(Sheet(100, "A101")));

        Assert.Equal(
            new[] { SheetIssueKind.PageSizeNotDecoded, SheetIssueKind.ViewPlacementNotDecoded,
                SheetIssueKind.DisplayedViewNotDecoded, SheetIssueKind.ViewContentNotDecoded,
                SheetIssueKind.CropRegionNotDecoded },
            catalog.Issues.Select(issue => issue.Kind));
        Assert.All(catalog.Issues, issue => Assert.False(string.IsNullOrWhiteSpace(issue.Message)));
    }

    [Fact]
    public void Extract_EmptyFileHasNoSheetsAndStillReportsTheLimits()
    {
        var catalog = SheetCatalog.Extract(new RevitFileInfo());

        Assert.Empty(catalog.Sheets);
        Assert.Empty(catalog.Viewports);
        Assert.Equal(Enum.GetValues<SheetIssueKind>().Length, catalog.Issues.Count);
    }

    [Fact]
    public void Extract_FromDocumentUsesTheSameData()
    {
        var catalog = SheetCatalog.Extract(RvtDocument.FromInfo(Info(Sheet(100, "A101"))));

        Assert.Equal("A101", Assert.Single(catalog.Sheets).Number);
    }

    [Fact]
    public void Extract_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => SheetCatalog.Extract((RvtDocument)null!));
        Assert.Throws<ArgumentNullException>(() => SheetCatalog.Extract((RevitFileInfo)null!));
    }

    [SkippableFact]
    public void Extract_OnARealProject_KeepsTheCrossReferences()
    {
        // Needs the private fixture folder (RVTIO_TEST_FILES or a test_files folder): the file-independent invariants
        // are checked on a real project rather than on the elements built above.
        string path = TestFiles.FindRelative(Path.Combine("2022", "TESTFILE.rvt"));
        SheetCatalog catalog = SheetCatalog.Extract(RvtDocument.Open(path));

        var sheets = catalog.Sheets.ToDictionary(sheet => sheet.ElementId);
        Assert.All(catalog.Viewports, viewport =>
        {
            Assert.True(sheets.ContainsKey(viewport.SheetId), $"Viewport {viewport.ElementId} names a sheet that is not listed.");
            Assert.Contains(viewport.ElementId, sheets[viewport.SheetId].ViewportIds);
        });
        Assert.All(catalog.Sheets, sheet =>
        {
            Assert.DoesNotContain(sheet.ElementId, sheet.OwnedElementIds);
            Assert.Equal(sheet.ViewportIds.OrderBy(id => id), sheet.ViewportIds);
            Assert.Equal(sheet.OwnedElementIds.OrderBy(id => id), sheet.OwnedElementIds);
            Assert.All(sheet.OwnedElementIds, id => Assert.NotEqual(0, id));
        });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5)]
    public void OwnerViewId_IgnoresNonViewOwners(int stored) =>
        Assert.Null(new Element { Id = 5, SerializedOwnerDbViewId = stored }.OwnerViewId);

    [Fact]
    public void OwnerViewId_ReturnsTheStoredView() =>
        Assert.Equal(100, new Element { Id = 5, SerializedOwnerDbViewId = 100 }.OwnerViewId);
}
