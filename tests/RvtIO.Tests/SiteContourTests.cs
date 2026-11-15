using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The contours of a site plan (SitePrinter): the terrain mesh is sliced at every half metre, so the site
/// plan shows the fall of the ground.
/// </summary>
public class SiteContourTests
{
    private static Element Topography(int id, params double[][] vertices) => new()
    {
        Id = id,
        CategoryId = (int)BuiltInCategory.OST_Topography,
        Geometry = new GeometryData
        {
            Faces = [new GeoFace { Normal = [0, 0, 1], OuterVertices = vertices.ToList() }],
        },
    };

    [Fact]
    public void Collect_SlicesTheTerrainIntoContours()
    {
        // A triangle rising ten feet (three metres and a bit): the metre contours at 1000, 2000 and 3000 mm cross it.
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            {
                Topography(100, [0, 0, 0], [10, 0, 10], [0, 10, 10]),
            },
        });

        var site = SitePrinter.Collect(document);

        Assert.NotNull(site);
        Assert.Equal(3, site!.Contours.Count);
        Assert.All(site.Contours, contour => Assert.InRange(contour.Z, 1000, 3000));
    }
}
