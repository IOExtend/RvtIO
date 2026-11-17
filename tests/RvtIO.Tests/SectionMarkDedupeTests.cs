using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The section marks of a plan (ElevationPrinter.SectionMarks): a detail section that shares the plane of the
/// building section marks the same cut, so only one mark is drawn.
/// </summary>
public class SectionMarkDedupeTests
{
    private static Element SectionView(int id, string name, double[] origin, int viewer) => new()
    {
        Id = id,
        CategoryId = (int)BuiltInCategory.OST_Views,
        NativeRootClassName = "DBViewSection",
        NativeRecordClass = 1016,
        Name = name,
        ViewOrigin = origin,
        ViewDirection = [1, 0, 0],
        ViewVerticalDirection = [0, 0, 1],
        ViewHorizontalDirection = [0, -1, 0],
        SectionViewerId = viewer,
    };

    [Fact]
    public void SectionMarks_DrawsOneMarkForOnePlane()
    {
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            {
                SectionView(100, "Building Section", [10, -1.5, 6], 1000),
                SectionView(101, "Typ. Wall Section", [10, -1.5, 6], 1001),
                SectionView(102, "Longitudinal Section", [4, 8, 6], 1002),
            },
            SectionViews = new Dictionary<int, (int ParentViewId, double CoarserScale, IReadOnlyList<double[]>? FrontCut)>
            {
                [1000] = (-1, 0.01, new List<double[]> { new[] { 10.0, -1.5, 6.0 }, new[] { 10.0, 15.0, 6.0 } }),
                [1001] = (-1, 0.01, new List<double[]> { new[] { 10.0, -1.5, 6.0 }, new[] { 10.0, 15.0, 6.0 } }),
                [1002] = (-1, 0.01, new List<double[]> { new[] { 4.0, 8.0, 6.0 }, new[] { 20.0, 8.0, 6.0 } }),
            },
        });

        var marks = ElevationPrinter.SectionMarks(document);

        Assert.Equal(2, marks.Count);
        Assert.Contains(marks, mark => mark.Name == "Building Section");
        Assert.Contains(marks, mark => mark.Name == "Longitudinal Section");
    }

    [Fact]
    public void SectionMarks_SkipsViewsWithoutAnExtentRecord()
    {
        // A section view with no decoded extent record (no viewer record) leaves no trace: the file
        // gives it no trace geometry, the way the Architectural Section draws none.
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            {
                SectionView(100, "Building Section", [10, -1.5, 6], 1000),
                SectionView(103, "Architectural Section", [45, 0, 4], -1),
            },
            SectionViews = new Dictionary<int, (int ParentViewId, double CoarserScale, IReadOnlyList<double[]>? FrontCut)>
            {
                [1000] = (-1, 0.01, new List<double[]> { new[] { 10.0, -1.5, 6.0 }, new[] { 10.0, 15.0, 6.0 } }),
            },
        });

        var marks = ElevationPrinter.SectionMarks(document);

        Assert.Single(marks);
        Assert.Contains(marks, mark => mark.Name == "Building Section");
    }
}
