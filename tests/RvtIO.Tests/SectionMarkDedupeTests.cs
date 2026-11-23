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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SectionMarks_ExtendsBothEndsAlongTheirOwnLegsOutsideAnUncroppedAnnotationView(bool annotationCrop)
    {
        const int plan = 200, viewer = 300;
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            {
                new Element
                {
                    Id = plan, ViewAnnotationCropActive = annotationCrop,
                    ViewCrop = new(0, 0, 0, [1, 0, 0], [0, 1, 0], [0, 0, 1], -30, -30, 30, 30, true),
                },
                SectionView(100, "Jogged Section", [10, 0, 6], viewer),
            },
            SectionViews = new Dictionary<int, (int, double, IReadOnlyList<double[]>?)>
            {
                [viewer] = (-1, 0.01, [[10, 6, 0], [10, 0, 0], [14, 0, 0], [14, -5, 0]]),
            },
            SectionTraceOrigins = new Dictionary<int, (double, double)> { [viewer] = (10, 8) },
            DatumExtents = new Dictionary<int, TfBlockDeserializer.ViewDatumExtents>
            {
                [plan] = new(plan, [0, 0, -1], [],
                    new Dictionary<int, TfBlockDeserializer.SectionTraceExtent> { [viewer] = new(-4, 24, 3) }),
            },
        });

        var mark = Assert.Single(ElevationPrinter.SectionMarks(document, plan, 100));

        Assert.Equal(!annotationCrop, mark.UsesPlanExtent);
        Assert.Equal(new[] { 10.0, 10, 14, 14 }.Select(feet => feet * 304.8), mark.Points.Select(p => p.X));
        double[] expectedY = annotationCrop ? [6, 0, 0, -5] : [12, 0, 0, -16];
        for (int i = 0; i < expectedY.Length; i++)
            Assert.Equal(expectedY[i] * 304.8, mark.Points[i].Y, 8);
    }

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
