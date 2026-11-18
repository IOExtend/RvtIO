using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The site printer (src/RvtIO/Print/SitePrinter): the topography faces and the planting of a site view.
/// </summary>
public class SitePrinterTests
{
    private const double FeetToMillimetres = 304.8;

    private static Element Topography(int id, params double[][] vertices) => new()
    {
        Id = id,
        CategoryId = (int)BuiltInCategory.OST_Topography,
        Geometry = new GeometryData
        {
            Faces = [new GeoFace { Normal = [0, 0, 1], OuterVertices = vertices.ToList() }],
        },
    };

    private static Element Plant(int id, double x, double y, double size)
    {
        var plant = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Planting };
        plant.BoundingBox =
        [
            x - size / 2, y - size / 2, 0,
            x + size / 2, y + size / 2, 10,
        ];
        return plant;
    }

    [Fact]
    public void Collect_ProjectsTheTopographyAndThePlanting()
    {
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            {
                Topography(100, [-5, -5, 0], [5, -5, 0], [5, 5, 0], [-5, 5, 0]),
                Plant(200, 2, 2, 4),
            },
        });

        var site = SitePrinter.Collect(document);

        Assert.NotNull(site);
        Assert.Single(site!.Topography);
        var plant = Assert.Single(site.Plants);
        Assert.Equal(2 * FeetToMillimetres, plant.X, 3);
        Assert.Equal(2 * FeetToMillimetres, plant.Radius, 3);
        Assert.Equal(-5 * FeetToMillimetres, site.MinX, 3);
    }

    [Fact]
    public void Collect_ReturnsNullWithoutSiteEntities()
    {
        Assert.Null(SitePrinter.Collect(RvtDocument.FromInfo(new RevitFileInfo())));
    }

    [Fact]
    public void InViewBasis_RotatesThePlanOntoTheViewAxes()
    {
        // A plan whose crop basis is turned (the Site view of the sample: right = (0.7986, -0.6018)) prints turned
        // with the view. A quarter turn maps (x, y) to (y, -x), so the square's bounds swap and its corners move.
        var site = new SitePrinter.SiteContent(
            Topography: [[(0, 0), (1000, 0), (1000, 500), (0, 500)]],
            Contours: [(0, 0, 500, 0, 100)],
            Plants: [(200, 300, 150, "Maple", "T1")],
            Entourage: [[(0, 0), (100, 0), (100, 100)]],
            MinX: 0, MinY: 0, MaxX: 1000, MaxY: 500);

        var turned = SitePrinter.InViewBasis(site, [0, 1, 0], [-1, 0, 0]);

        var polygon = Assert.Single(turned.Topography);
        Assert.Contains(polygon, point => Math.Abs(point.X - 0) < 1e-6 && Math.Abs(point.Y - 0) < 1e-6);
        Assert.Contains(polygon, point => Math.Abs(point.X - 500) < 1e-6 && Math.Abs(point.Y + 1000) < 1e-6);
        var plant = Assert.Single(turned.Plants);
        Assert.Equal(300, plant.X, 6);
        Assert.Equal(-200, plant.Y, 6);
        Assert.Equal(-1000, turned.MinY, 6);
        Assert.Equal(500, turned.MaxX, 6);
    }
}
