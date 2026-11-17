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
    private static Element SectionView(int id, string name, double[] origin) => new()
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
    };

    [Fact]
    public void SectionMarks_DrawsOneMarkForOnePlane()
    {
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            {
                SectionView(100, "Building Section", [10, -1.5, 6]),
                SectionView(101, "Typ. Wall Section", [10, -1.5, 6]),
                SectionView(102, "Longitudinal Section", [4, 8, 6]),
            },
        });

        var marks = ElevationPrinter.SectionMarks(document);

        Assert.Equal(2, marks.Count);
        Assert.Contains(marks, mark => mark.Name == "Building Section");
        Assert.Contains(marks, mark => mark.Name == "Longitudinal Section");
    }
}
