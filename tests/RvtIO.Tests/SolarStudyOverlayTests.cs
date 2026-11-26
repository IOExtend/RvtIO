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
        Assert.Contains("(Mar 24) Tj", page.Content);
        Assert.Contains("(N) Tj", page.Content);
        var raster = Assert.IsType<PdfRasterImage>(Assert.Single(page.Images));
        int center = 3 * (raster.Width * (raster.Height / 2) + raster.Width / 2);
        Assert.True(raster.Rgb[center] > raster.Rgb[center + 1] && raster.Rgb[center + 1] > raster.Rgb[center + 2]);

        info.ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view.Id] = [annotationId] };
        var hiddenDocument = RvtDocument.FromInfo(info);
        var pageWithoutStudy = new PdfDocument().AddPage(new PdfPageSize(300, 300));
        Assert.Null(ShadedViewport.Render(pageWithoutStudy, hiddenDocument, view, 150, 150, 200));
    }

    [Fact]
    public void ActiveDayDoesNotExtendAGuideOutsideTheStudyHours()
    {
        var camera = new ViewCropFrame(0, 0, 10, [1, 0, 0], [0, 1, 0], [0, 0, -1], -80, -80, 80, 80, false);
        var ground = new Element { Id = 30 };
        ground.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV, "LEVEL_ELEV", StorageType.Double, 0d));
        var view = new Element
        {
            Id = 10,
            SolarStudy = new([-20, -20, 0, 20, 20, 15], 150, ground.Id, 20,
                Packed(2024, 3, 1, 12, 0), Packed(2024, 7, 1, 13, 0), 3, 101),
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [view, ground],
            SharedTransformIsActiveLocation = true,
            SharedTransform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
            SiteLatitudeRadians = 0,
            SiteLongitudeRadians = 0,
            SiteTimeZoneHours = 0,
        });
        var overlay = Assert.IsType<SolarStudyOverlay>(SolarStudyOverlay.Create(document, view, camera, 1,
            point => (point[0], point[1], 10 - point[2])));
        var rgb = new byte[15 * 18 * 3];
        Array.Fill(rgb, byte.MaxValue);

        // At this latitude the April 20 declination passes through this morning patch.
        overlay.Paint(rgb, 15, 18, overlay.Radius * 0.76, overlay.Radius * 0.25, 0.23, 0.23);

        Assert.All(rgb, channel => Assert.Equal(byte.MaxValue, channel));
    }

    [Theory]
    [InlineData(0.8, false, null)]
    [InlineData(-0.8, true, null)]
    [InlineData(0.8, false, 30)]
    [InlineData(-0.8, true, 30)]
    public void ShadesExposedDomeOnTheSideFacingAwayFromTheSun(double east, bool unlit, int? sunlightIntensity)
    {
        var camera = new ViewCropFrame(0, 0, 10, [1, 0, 0], [0, 1, 0], [0, 0, -1], -80, -80, 80, 80, false);
        var ground = new Element { Id = 30 };
        ground.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV, "LEVEL_ELEV", StorageType.Double, 0d));
        var view = new Element
        {
            Id = 10,
            SolarStudy = new([-20, -20, 0, 20, 20, 15], 150, ground.Id, 20,
                Packed(2024, 3, 18, 7, 0), Packed(2024, 4, 20, 17, 0), 3, 1,
                SunlightIntensity: sunlightIntensity),
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [view, ground],
            SharedTransformIsActiveLocation = true,
            SharedTransform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
            SiteLatitudeRadians = 0,
            SiteLongitudeRadians = 0,
            SiteTimeZoneHours = 0,
        });
        var overlay = Assert.IsType<SolarStudyOverlay>(SolarStudyOverlay.Create(document, view, camera, 1,
            point => (point[0], point[1], 10 - point[2])));
        byte[] background = [255, 255, 255];

        overlay.Paint(background, 1, 1, overlay.X + east * overlay.Radius - 0.5,
            overlay.Y + 0.1 * overlay.Radius + 0.5, 1, 1);

        if (sunlightIntensity.HasValue)
        {
            byte[] terrain = [79, 95, 63];
            overlay.Paint(terrain, 1, 1, overlay.X + east * overlay.Radius - 0.5,
                overlay.Y + 0.1 * overlay.Radius + 0.5, 1, 1);
            Assert.Equal(44, background[0] - terrain[0]);
            Assert.Equal(40, background[1] - terrain[1]);
            Assert.Equal(48, background[2] - terrain[2]);
            Assert.InRange(background[0], unlit ? 103 : 245, unlit ? 115 : 254);
        }
        else if (unlit)
            Assert.Equal(new byte[] { 135, 105, 62 }, background);
        else
        {
            Assert.InRange(background[0], 230, 254);
            Assert.InRange(background[2], 0, 90);
        }
    }

    [Fact]
    public void CompassRingUsesItsGroundPlaneDepthAtEachPoint()
    {
        var camera = new ViewCropFrame(0, 0, 10, [1, 0, 0], [0, 0.6, 0.8], [0, 0.8, -0.6],
            -80, -80, 80, 80, false);
        var ground = new Element { Id = 30 };
        ground.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV, "LEVEL_ELEV", StorageType.Double, 0d));
        var view = new Element
        {
            Id = 10,
            SolarStudy = new([-20, -20, 0, 20, 20, 15], 150, ground.Id, 20, 0, 0, 0, 0),
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [view, ground],
            SharedTransformIsActiveLocation = true,
            SharedTransform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
        });
        var overlay = Assert.IsType<SolarStudyOverlay>(SolarStudyOverlay.Create(document, view, camera, 1,
            point => (point[0], 0.6 * point[1] + 0.8 * point[2], 0.8 * point[1] - 0.6 * (point[2] - 10))));
        byte[] nearerRing = [24, 48, 72], fartherRing = [24, 48, 72], openBackground = [24, 48, 72];
        double ringY = overlay.Radius * 1.025 * 0.6;

        overlay.Paint(nearerRing, 1, 1, -0.05, -ringY + 0.05, 0.1, 0.1, [6]);
        overlay.Paint(fartherRing, 1, 1, -0.05, ringY + 0.05, 0.1, 0.1, [6]);
        overlay.Paint(openBackground, 1, 1, -0.05, ringY + 0.05, 0.1, 0.1, [double.PositiveInfinity]);

        Assert.Equal(new byte[] { 24, 48, 72 }, fartherRing);
        Assert.Equal(nearerRing[0], nearerRing[1]);
        Assert.Equal(nearerRing[1], nearerRing[2]);
        Assert.Equal(openBackground[0], openBackground[1]);
        Assert.Equal(openBackground[1], openBackground[2]);
    }

    private static uint Packed(int year, int month, int day, int hour, int minute) =>
        (uint)(year << 20 | month << 16 | day << 11 | hour << 6 | minute);
}
