using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public class ElevationSheetLinesTests
{
    [Fact]
    public void SampleFile_SouthElevationInksNoModelHorizontal()
    {
        // A vertical view inks no model horizontal: the reference draws the wall top as the ends of its vertical
        // boards with no line across them, and its level rows as their own dashes (drawn separately). The sample's
        // roof mass puts a 19.5 m top edge at z = 9199 mm in the South elevation; measured against the reference
        // that row is 95% unmatched while the vertical linework matches.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "South");
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "elevation", false, view);

        Assert.NotNull(content);
        var edges = content!.Edges;
        Assert.DoesNotContain(edges, edge => Math.Abs(edge.Y2 - edge.Y1) < 0.5);
        // The vertical linework stays: the filter drops horizontals, not the drawing.
        Assert.Contains(edges, edge => Math.Abs(edge.X2 - edge.X1) < 0.5 && Math.Abs(edge.Y2 - edge.Y1) > 1000);
        // The roof mass's top edge (the longest horizontal the model has in this view) is gone.
        var roof = document.Elements.First(e => e.CategoryId == (int)BuiltInCategory.OST_Roofs
            && e.Geometry is { Faces.Count: > 0 });
        double roofTop = roof.Geometry!.Faces
            .Where(face => face.OuterVertices != null)
            .SelectMany(face => face.OuterVertices!)
            .Max(vertex => vertex[2]) * 304.8;
        Assert.DoesNotContain(edges, edge => Math.Abs(edge.Y2 - edge.Y1) < 0.5
            && Math.Abs((edge.Y1 + edge.Y2) / 2 - roofTop) < 5 && Math.Abs(edge.X2 - edge.X1) > 15000);
    }

    [Fact]
    public void SampleFile_LongitudinalSectionKeepsItsCutProfile()
    {
        // A section drops the projected horizontals like an elevation, but keeps the edges its own cut created:
        // those are the cut profile of a horizontal slab (the sample's roof slab at z = 6898 / 7178 mm).
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "Longitudinal Section");
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "section", false, view);

        Assert.NotNull(content);
        var edges = content!.Edges;
        Assert.DoesNotContain(edges, edge => !edge.IsCut && Math.Abs(edge.Y2 - edge.Y1) < 0.5);
        Assert.Contains(edges, edge => edge.IsCut && Math.Abs(edge.Y2 - edge.Y1) < 0.5);
        Assert.Contains(edges, edge => Math.Abs(edge.X2 - edge.X1) < 0.5 && Math.Abs(edge.Y2 - edge.Y1) > 1000);
    }
}
