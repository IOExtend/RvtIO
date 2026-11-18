using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public class ElevationSheetLinesTests
{
    [Fact]
    public void SampleFile_SouthElevationDoesNotInkHorizontalFaceEdges()
    {
        // A horizontal face is seen edge-on in an elevation: its boundary is not inked (the reference draws the
        // wall top as the ends of its vertical boards, without a line across them). The sample's roof mass puts
        // a 19.5 m top edge at z = 9199 mm in the South elevation; measured against the reference that row is
        // 95% unmatched while the model's vertical-face horizontals match, so the elevation drops it.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "South");
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "elevation", false, view);

        Assert.NotNull(content);
        var edges = content!.Edges;
        // No inked edge runs along a horizontal face's plane: every kept horizontal comes from a vertical face.
        Assert.DoesNotContain(edges, edge =>
            Math.Abs(edge.Y2 - edge.Y1) < 0.5 && Math.Abs(edge.Nz) > 0.9);
        // The elevation still inks the horizontals of vertical faces (sills, floor lines): the filter is not total.
        Assert.Contains(edges, edge => Math.Abs(edge.Y2 - edge.Y1) < 0.5 && Math.Abs(edge.Nz) <= 0.9);
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
}
