using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public class ElevationSheetLinesTests
{
    [Fact]
    public void SampleFile_SouthElevationInksTheHorizontalContours()
    {
        // Every visible edge inks, horizontals included: the printed reference shows the wall top, the roof and the
        // ground line as solid horizontals. (The reference's vector layer lacks them on A101/A103/A104/A105 because
        // those pages carry the drawing as a raster image, so a vector-only comparison must not be used to drop
        // them.) The sample's roof mass puts a 19.5 m top edge at z = 9199 mm into the South elevation.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "South");
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "elevation", false, view);

        Assert.NotNull(content);
        var edges = content!.Edges;
        Assert.Contains(edges, edge => Math.Abs(edge.Y2 - edge.Y1) < 0.5 && Math.Abs(edge.X2 - edge.X1) > 15000);
        // Horizontals at many heights, not one stray line: the contours of the drawing.
        int heights = edges
            .Where(edge => Math.Abs(edge.Y2 - edge.Y1) < 0.5 && Math.Abs(edge.X2 - edge.X1) > 1000)
            .Select(edge => (int)Math.Round((edge.Y1 + edge.Y2) / 2 / 100))
            .Distinct()
            .Count();
        Assert.True(heights >= 5, $"South elevation drew {heights} distinct horizontal bands");
        // The vertical linework stays as well.
        Assert.Contains(edges, edge => Math.Abs(edge.X2 - edge.X1) < 0.5 && Math.Abs(edge.Y2 - edge.Y1) > 1000);
    }

    [Fact]
    public void SampleFile_LongitudinalSectionKeepsItsCutProfile()
    {
        // A section shows the cut profile of every slab it passes through (the sample's roof slab at z = 6898 /
        // 7178 mm) plus the projected horizontals of the faces behind the plane. The printed reference draws them.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "Longitudinal Section");
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "section", false, view);

        Assert.NotNull(content);
        var edges = content!.Edges;
        Assert.Contains(edges, edge => edge.IsCut && Math.Abs(edge.Y2 - edge.Y1) < 0.5);
        Assert.Contains(edges, edge => !edge.IsCut && Math.Abs(edge.Y2 - edge.Y1) < 0.5);
        Assert.Contains(edges, edge => Math.Abs(edge.X2 - edge.X1) < 0.5 && Math.Abs(edge.Y2 - edge.Y1) > 1000);
    }

    [Fact]
    public void SampleFile_StairDetailKeepsItsRailDiagonals()
    {
        // A railing's continuous rail projects as a long diagonal across the stair; the printed reference shows it.
        // The vertical balusters stay as well.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "Main Stair Detail");
        Assert.NotNull(view);
        if (view!.ViewCrop is not { Active: true } crop)
            throw new SkipException("The sample's Main Stair Detail has no active crop.");

        var content = ElevationPrinter.Collect(document, "section", false, view,
            cropWindow: SheetComposer.CropContentRect(crop));

        Assert.NotNull(content);
        Assert.Contains(content!.Edges, edge =>
            edge.CategoryId == (int)BuiltInCategory.OST_StairsRailing
            && Math.Abs(edge.X2 - edge.X1) > 100 && Math.Abs(edge.Y2 - edge.Y1) > 100);
        Assert.Contains(content.Edges, edge =>
            edge.CategoryId == (int)BuiltInCategory.OST_StairsRailing
            && Math.Abs(edge.X2 - edge.X1) <= 100);
    }

    [Fact]
    public void SampleFile_FoundationBelowTheLowestLevelIsDrawn()
    {
        // Work below the lowest decoded level still inks: the printed reference shows the foundation walls and their
        // hatch below grade. The sample's foundation verticals run from z = -6302 to -150 mm.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "South");
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "elevation", false, view);

        Assert.NotNull(content);
        Assert.NotEmpty(content!.Levels);
        double lowest = content.Levels.Min(level => level.Y);
        Assert.Contains(content.Edges, edge => (edge.Y1 + edge.Y2) / 2 < lowest - 1);
        Assert.Contains(content.Edges, edge =>
            edge.CategoryId == (int)BuiltInCategory.OST_StructuralFoundation
            && (edge.Y1 + edge.Y2) / 2 < lowest - 1);
    }
}
