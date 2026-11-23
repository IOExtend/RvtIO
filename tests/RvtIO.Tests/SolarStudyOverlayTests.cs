using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public class SolarStudyOverlayTests
{
    [Fact]
    public void DateDecodingRejectsCalendarAndClockValuesOutsideTheirRanges()
    {
        Assert.True(SolarStudyOverlay.TryDecodeDate(Packed(2024, 2, 29, 23, 59), out var leapDay));
        Assert.Equal(new DateTime(2024, 2, 29, 23, 59, 0), leapDay);
        Assert.False(SolarStudyOverlay.TryDecodeDate(Packed(2023, 2, 29, 23, 59), out _));
        Assert.False(SolarStudyOverlay.TryDecodeDate(Packed(2024, 2, 29, 24, 0), out _));
        Assert.False(SolarStudyOverlay.TryDecodeDate(Packed(2024, 2, 29, 23, 60), out _));
    }

    [Fact]
    public void DrawsTheSavedMultiDayStudyWithTheActiveFrameDate()
    {
        const int annotationId = 20;
        var camera = new ViewCropFrame(0, 0, 10, [1, 0, 0], [0, 1, 0], [0, 0, -1], -80, -80, 80, 80, false);
        var ground = new Element { Id = 30 };
        ground.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV, "LEVEL_ELEV", StorageType.Double, 0d));
        var view = new Element
        {
            Id = 10,
            ViewCrop = camera,
            OrthographicModelBounds = new([0, 0, 0], [[1, 0, 0], [0, 1, 0], [0, 0, 1]],
                [50, -50, 50, -50, 20, -20], [true, true, true, true, true, true]),
            SolarStudy = new([-20, -20, 0, 20, 20, 15], 150, ground.Id, annotationId,
                Packed(2024, 3, 18, 8, 0), Packed(2024, 3, 24, 14, 0), 3, 8),
        };
        var info = new RevitFileInfo
        {
            Elements = [view, ground],
            SharedTransformIsActiveLocation = true,
            SharedTransform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
            SiteLatitudeRadians = 0,
            SiteLongitudeRadians = 0,
            SiteTimeZoneHours = 0,
        };
        var document = RvtDocument.FromInfo(info);
        var page = new PdfDocument().AddPage(new PdfPageSize(300, 300));

        Assert.NotNull(ShadedViewport.Render(page, document, view, 150, 150, 200));
        Assert.Contains("(Mar 18) Tj", page.Content);
        Assert.Contains("(Mar 19) Tj", page.Content);
        Assert.Contains("(N) Tj", page.Content);
        var raster = Assert.IsType<PdfRasterImage>(Assert.Single(page.Images));
        int center = 3 * (raster.Width * (raster.Height / 2) + raster.Width / 2);
        Assert.True(raster.Rgb[center] > raster.Rgb[center + 1] && raster.Rgb[center + 1] > raster.Rgb[center + 2]);

        info.ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view.Id] = [annotationId] };
        var hiddenDocument = RvtDocument.FromInfo(info);
        var pageWithoutStudy = new PdfDocument().AddPage(new PdfPageSize(300, 300));
        Assert.Null(ShadedViewport.Render(pageWithoutStudy, hiddenDocument, view, 150, 150, 200));
    }

    private static uint Packed(int year, int month, int day, int hour, int minute) =>
        (uint)(year << 20 | month << 16 | day << 11 | hour << 6 | minute);
}
