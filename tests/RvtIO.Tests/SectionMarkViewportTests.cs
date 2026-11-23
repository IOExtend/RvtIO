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

    [SkippableFact]
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

    [SkippableFact]
    public void SampleFile_DecodesSectionFrontCut()
    {
        // The Section m_frontCut polyline in model feet, cross-checked against rvt-prt's datums.read:
        // the Building Section jogs (10.14,15.14)-(10.14,-13.32)-(25.40,-13.32)-(25.40,-20.37),
        // the Longitudinal Section runs straight (-40.27,-7.16)-(61.79,-7.16).
        var document = SampleFile();
        var sections = document.RawData.SectionViews;
        Assert.NotNull(sections);
        Assert.True(sections!.TryGetValue(243253, out var building));
        Assert.NotNull(building.FrontCut);
        Assert.Equal(4, building.FrontCut!.Count);
        Assert.Equal(10.14, building.FrontCut[0][0], 2);
        Assert.Equal(15.14, building.FrontCut[0][1], 2);
        Assert.Equal(-13.32, building.FrontCut[1][1], 2);
        Assert.Equal(25.40, building.FrontCut[2][0], 2);
        Assert.Equal(-20.37, building.FrontCut[3][1], 2);
        Assert.True(sections.TryGetValue(247013, out var longitudinal));
        Assert.NotNull(longitudinal.FrontCut);
        Assert.Equal(2, longitudinal.FrontCut!.Count);
        Assert.Equal(-40.27, longitudinal.FrontCut[0][0], 2);
        Assert.Equal(61.79, longitudinal.FrontCut[1][0], 2);
        Assert.Equal(-7.16, longitudinal.FrontCut[0][1], 2);
    }

    [SkippableFact]
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

    [SkippableFact]
    public void SampleFile_Level1MarksFollowDecodedExtents()
    {
        // The Building mark draws its decoded jog (four points), not an infinite line: the trace turns
        // at the jog instead of running the full frame height. The Longitudinal mark runs straight.
        var document = SampleFile();

        var marks = ElevationPrinter.SectionMarks(document, viewportViewId: 312, viewportScaleDenominator: 100);

        var building = Assert.Single(marks, mark => mark.Name == "Building Section");
        Assert.True(building.Bounded);
        Assert.Equal(4, building.Points.Count);
        Assert.Equal(10.14, building.Points[0].X / 304.8, 2);
        var longitudinal = Assert.Single(marks, mark => mark.Name == "Longitudinal Section");
        Assert.True(longitudinal.Bounded);
        Assert.Equal(2, longitudinal.Points.Count);
    }

    [SkippableFact]
    public void SampleFile_StraightMarkUsesThePlanOwnedExtent()
    {
        var document = SampleFile();
        var marks = ElevationPrinter.SectionMarks(document, viewportViewId: 312, viewportScaleDenominator: 100);
        var mark = Assert.Single(marks, candidate => candidate.Name == "Longitudinal Section");
        var viewer = document.GetElement(mark.ViewId)!.SectionViewerId!.Value;
        var extent = document.RawData.DatumExtents![312].Sections![viewer];
        var origin = document.RawData.SectionTraceOrigins![viewer];
        var cut = document.RawData.SectionViews![viewer].FrontCut!;

        Assert.True(mark.UsesPlanExtent);
        Assert.Equal((origin.X + extent.Start) * 304.8, mark.Points[0].X, 8);
        Assert.Equal((origin.X + extent.End) * 304.8, mark.Points[^1].X, 8);
        Assert.Equal(cut[0][1] * 304.8, mark.Points[0].Y, 8);
    }
}
