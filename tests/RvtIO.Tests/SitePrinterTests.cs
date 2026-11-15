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
}
