using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public class PlanViewRangeTests
{
    [Fact]
    public void SampleFile_DecodesTheLevel2PlanViewRange()
    {
        // PlanViewRange2 is a nested object of the view's block, not an element block: its five offsets are the
        // cut plane, the top clip, the bottom clip (feet) and the view depth, measured from the level ids.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.First(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "Level 2");
        int levelId = (int)(view.LookupParameter(BuiltInParameter.LEVEL_PARAM)?.Value
            ?? document.Elements.First(e => e.CategoryId == (int)BuiltInCategory.OST_Levels
                && e.Name == "Level 2").Id);

        Assert.NotNull(document.RawData.PlanViewRanges);
        Assert.True(document.RawData.PlanViewRanges!.TryGetValue(view.Id, out var range),
            $"no view range decoded for view {view.Id}");
        Assert.Equal(5, range.Offsets.Length);
        Assert.Equal(3.937, range.Offsets[0], 3);   // cut plane 4'-0"
        Assert.Equal(7.546, range.Offsets[1], 3);   // top clip 7'-6"
        Assert.Equal(0.0, range.Offsets[2], 6);     // bottom clip at the level
        Assert.Contains(levelId, range.LevelIds);
    }

    [Fact]
    public void SampleFile_Level2DrawsTheDeckTopRail()
    {
        // The observation deck's top rail stands on the roof below Level 2 (its base is the ceiling level), so it
        // is outside a base-level-only plan. The view range's top and bottom clips show it, and its horizontal
        // faces outline the rail path as the double line along the deck edge.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.First(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "Level 2");
        int levelId = (int)document.Elements.First(e => e.CategoryId == (int)BuiltInCategory.OST_Levels
            && e.Name == "Level 2").Id;

        var content = PlanPrinter.CollectLevel(document, levelId, view.Id);

        Assert.NotNull(content);
        Assert.NotNull(content!.RailLines);
        Assert.True(content.RailLines!.Count >= 4, $"deck rail drew {content.RailLines.Count} lines");
        // The deck lies south of the building (model Y below -6000 mm); its rail runs along the deck edge.
        var deck = content.RailLines.Where(l => (l.Y1 + l.Y2) / 2 < -6000).ToList();
        Assert.True(deck.Count >= 2, $"deck rail drew {deck.Count} lines south of the building");
        Assert.True(deck.Any(l => Math.Abs(l.X2 - l.X1) > 5000),
            "no long top-rail run along the deck edge");
    }
}
