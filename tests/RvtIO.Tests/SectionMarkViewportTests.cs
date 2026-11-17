using RvtIO.Model;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Section marks drawn on plans (ElevationPrinter.SectionMarks): the root view must be a section or
/// elevation (undecodable ghosts draw nothing), a mark whose section answers to another section or elevation
/// view stays in that view (a wall-section callout is not drawn on a plan), and a section coarser than the
/// viewport scale stays off it.</summary>
public class SectionMarkViewportTests
{
    private static RvtDocument SampleFile()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        return RvtDocument.Open(path);
    }

    [Fact]
    public void SampleFile_DecodesSectionViewdata()
    {
        var document = SampleFile();
        var sections = document.RawData.SectionViews;
        Assert.NotNull(sections);
        // The Building Section answers to no parent view and shows at 1:100.
        Assert.True(sections!.TryGetValue(243253, out var building));
        Assert.Equal(-1, building.ParentViewId);
        Assert.Equal(0.01, building.CoarserScale, 4);
        // The wall section answers to the Building Section; the stair section hides coarser than 1:50.
        Assert.True(sections.TryGetValue(958267, out var wall));
        Assert.Equal(243254, wall.ParentViewId);
        Assert.True(sections.TryGetValue(771512, out var stair));
        Assert.Equal(0.02, stair.CoarserScale, 4);
    }

    [Fact]
    public void SampleFile_ShowsOnlyVisibleMarksOnLevel1()
    {
        var document = SampleFile();

        var marks = ElevationPrinter.SectionMarks(document, viewportViewId: 312, viewportScaleDenominator: 100);

        Assert.Contains(marks, mark => mark.Name == "Building Section");
        Assert.Contains(marks, mark => mark.Name == "Longitudinal Section");
        // A wall-section callout belongs to its parent section, a stair section hides coarser than 1:50,
        // and the undecodable ghost views mark nothing.
        Assert.DoesNotContain(marks, mark => mark.Name == "Typ. Wall Section");
        Assert.DoesNotContain(marks, mark => mark.Name == "Stair Section");
        Assert.DoesNotContain(marks, mark => mark.Name == "Section");
        Assert.DoesNotContain(marks, mark => mark.Name == "Section 1");
        // The Architectural Section has no decoded extent record (viewer -1): the file gives it no
        // trace geometry, so it marks nothing instead of inventing a full-height vertical.
        Assert.DoesNotContain(marks, mark => mark.Name == "Architectural Section");
    }
}
