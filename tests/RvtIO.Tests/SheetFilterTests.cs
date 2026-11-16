using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Sheets;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Sheet --where filters and extra parameters (ported from rvt-prt's list --params/--where).</summary>
public class SheetFilterTests
{
    private static void Add(Element element, BuiltInParameter id, string name, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, name, storage, value));

    private static Element Sheet(int id, string? number, string? group = null, int scheduled = 1)
    {
        var sheet = new Element { Id = id, CategoryId = -2003100 };
        Add(sheet, BuiltInParameter.SHEET_NUMBER, "SHEET_NUMBER", StorageType.String, number ?? string.Empty);
        Add(sheet, BuiltInParameter.SHEET_NAME, "SHEET_NAME", StorageType.String, "Plan");
        Add(sheet, BuiltInParameter.SHEET_SCHEDULED, "SHEET_SCHEDULED", StorageType.Integer, scheduled);
        if (group != null)
            sheet.Parameters.Add(BipHelpers.MakeBipParam(9001, "Sheet Group", StorageType.String, group));
        return sheet;
    }

    private static SheetRecord Record(Element element) =>
        SheetCatalog.Extract(new RevitFileInfo { Elements = [element] }).Sheets[0];

    [Fact]
    public void ExtraParameters_HoldProjectParamsAndScheduledAsYesNo()
    {
        var record = Record(Sheet(100, "A101", "Architectural"));

        Assert.Equal("Architectural", record.Parameters["Sheet Group"]);
        Assert.Equal("Yes", record.Parameters["Appears In Sheet List"]);
    }

    [Fact]
    public void ExtraParameters_SkipsBuiltinNonSheetParams()
    {
        var sheet = Sheet(100, "A101");
        sheet.Parameters.Add(BipHelpers.MakeBipParam(-1140362, "ELEM_CATEGORY_PARAM", StorageType.Integer, -2003100));
        var record = SheetCatalog.Extract(new RevitFileInfo { Elements = [sheet] }).Sheets[0];

        Assert.DoesNotContain("ELEM_CATEGORY_PARAM", record.Parameters.Keys);
    }

    [Fact]
    public void Filter_EqualsSupportsWildcardsCaseInsensitively()
    {
        var record = Record(Sheet(100, "A101", "Architectural"));

        Assert.True(SheetFilter.TryParse("Sheet Number=A1*", out _)!.Matches(record));
        Assert.True(SheetFilter.TryParse("sheet group=architectural", out _)!.Matches(record));
        Assert.False(SheetFilter.TryParse("Sheet Number=A2*", out _)!.Matches(record));
    }

    [Fact]
    public void Filter_NotEqualsAndContains()
    {
        var record = Record(Sheet(100, "A101", "Architectural"));

        Assert.True(SheetFilter.TryParse("Sheet Number!=A102", out _)!.Matches(record));
        Assert.True(SheetFilter.TryParse("Sheet Group~tect", out _)!.Matches(record));
        Assert.False(SheetFilter.TryParse("Sheet Group~mech", out _)!.Matches(record));
    }

    [Fact]
    public void Filter_YesNoAliases()
    {
        var record = Record(Sheet(100, "A101"));

        Assert.True(SheetFilter.TryParse("Appears In Sheet List=true", out _)!.Matches(record));
        Assert.True(SheetFilter.TryParse("Appears In Sheet List=1", out _)!.Matches(record));
        Assert.False(SheetFilter.TryParse("Appears In Sheet List=No", out _)!.Matches(record));
    }

    [Fact]
    public void Filter_BadTextFails()
    {
        Assert.Null(SheetFilter.TryParse("no-operator-here", out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
