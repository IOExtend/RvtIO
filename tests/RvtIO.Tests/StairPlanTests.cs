using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public class StairPlanTests
{
    [Fact]
    public void SampleFile_Level2DrawsTheArrivingStair()
    {
        // The main stair runs from Level 1 to Level 2 (STAIRS_BASE_LEVEL_PARAM / STAIRS_TOP_LEVEL_PARAM), so
        // the Level 2 plan draws the flight arriving there as its tread courses, as the reference does; the
        // old level rule (base level only) left the whole stair off the Level 2 plan.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var room = document.Elements.First(e => e.CategoryId == (int)BuiltInCategory.OST_Rooms
            && e.Name == "Entry Hall" && e.LookupParameter(BuiltInParameter.ROOM_LEVEL_ID)?.Value is int lv
            && document.GetElement(lv)?.Name == "Level 2");
        int levelId = (int)room.LookupParameter(BuiltInParameter.ROOM_LEVEL_ID)!.Value!;

        var content = PlanPrinter.CollectLevel(document, levelId);

        Assert.NotNull(content);
        Assert.NotNull(content!.StairLines);
        Assert.True(content.StairLines!.Count >= 10,
            $"Level 2 stair drew {content.StairLines.Count} tread lines");
        double x0 = content.StairLines.Min(l => Math.Min(l.X1, l.X2));
        double x1 = content.StairLines.Max(l => Math.Max(l.X1, l.X2));
        double y0 = content.StairLines.Min(l => Math.Min(l.Y1, l.Y2));
        double y1 = content.StairLines.Max(l => Math.Max(l.Y1, l.Y2));
        Assert.InRange(x1 - x0, 1000, 12000);
        Assert.InRange(y1 - y0, 1000, 12000);
    }
}
