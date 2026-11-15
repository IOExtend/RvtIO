using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The entourage of a site plan (SitePrinter): a car prints as its footprint, a person as a small circle.
/// </summary>
public class SiteEntourageTests
{
    private static Element Entourage(int id, double x, double y, double widthFeet, double depthFeet)
    {
        var element = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Entourage };
        element.BoundingBox =
        [
            x, y, 0,
            x + widthFeet, y + depthFeet, 5,
        ];
        return element;
    }

    [Fact]
    public void Collect_ProjectsTheEntourageOfTheSite()
    {
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            {
                new Element
                {
                    Id = 100,
                    CategoryId = (int)BuiltInCategory.OST_Topography,
                    Geometry = new GeometryData
                    {
                        Faces = [new GeoFace { Normal = [0, 0, 1], OuterVertices = [[-5, -5, 0], [5, -5, 0], [5, 5, 0], [-5, 5, 0]] }],
                    },
                },
                Entourage(200, 0, 0, 15, 6),
            },
        });

        var site = SitePrinter.Collect(document);

        Assert.NotNull(site);
        var car = Assert.Single(site!.Entourage);
        Assert.Equal(4, car.Count);
        Assert.Equal(15 * 304.8, car[2].X - car[0].X, 3);
    }
}
