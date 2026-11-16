using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The elevation printer (src/RvtIO/Print/ElevationPrinter): the direction from the view name, the projected
/// faces of the model and the composer placing an elevation on a sheet.</summary>
public class ElevationPrinterTests
{
    private const double FeetToMillimetres = 304.8;

    private static void Add(Element element, BuiltInParameter id, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, id.ToString(), storage, value));

    [Theory]
    [InlineData("South", "south")]
    [InlineData("North Elevation", "north")]
    [InlineData("East", "east")]
    [InlineData("West", "west")]
    [InlineData("Longitudinal Section", "section")]
    [InlineData("Level 1", null)]
    public void DirectionOf_ReadsTheCompassName(string name, string? expected) =>
        Assert.Equal(expected, ElevationPrinter.DirectionOf(name));

    private static Element ElevationView(int id, string name, int scale)
    {
        var view = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Views, NativeRecordClass = 1016 };
        Add(view, BuiltInParameter.VIEW_NAME, StorageType.String, name);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, scale);
        return view;
    }

    private static Element WallWithFace(int id)
    {
        var face = new GeoFace
        {
            Origin = [0, 0, 0],
            XVector = [1, 0, 0],
            YVector = [0, 0, 1],
            Normal = [0, 1, 0],
            OuterVertices =
            [
                [0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3],
            ],
        };
        return new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Geometry = new GeometryData { Faces = [face] },
        };
    }

    private static Element QuadWall(int id, double[][] vertices, double[] normal)
    {
        var face = new GeoFace
        {
            Origin = [0, 0, 0],
            XVector = [1, 0, 0],
            YVector = [0, 0, 1],
            Normal = [normal[0], normal[1], normal[2]],
            OuterVertices = vertices.Select(v => new[] { v[0], v[1], v[2] }).ToList(),
        };
        return new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Geometry = new GeometryData { Faces = [face] },
        };
    }

    private static RvtDocument Document(params Element[] elements) =>
        RvtDocument.FromInfo(new RevitFileInfo { Elements = elements.ToList() });

    [Fact]
    public void Views_ListsCompassElevationsWithTheirScale()
    {
        var document = Document(
            ElevationView(400, "South", 100), ElevationView(401, "Longitudinal Section", 100),
            new Element { Id = 402, CategoryId = (int)BuiltInCategory.OST_Views });

        var views = ElevationPrinter.Views(document);

        // The plan view is not an elevation; the section is one (projected along the short axis). The list is sorted by
        // name, so the views are found by id.
        Assert.Equal(2, views.Count);
        ElevationView south = Assert.Single(views.Where(view => view.ViewId == 400));
        Assert.Equal("south", south.Direction);
        Assert.Equal(100, south.Scale);
        ElevationView section = Assert.Single(views.Where(view => view.ViewId == 401));
        Assert.Equal("section", section.Direction);
    }

    [Fact]
    public void Collect_ProjectsTheVerticalFaceOnTheViewPlane()
    {
        var document = Document(WallWithFace(600));

        var content = ElevationPrinter.Collect(document, "south");

        Assert.NotNull(content);
        Assert.Single(content!.Faces);
        Assert.Equal(0, content.MinX, 3);
        Assert.Equal(4 * FeetToMillimetres, content.MaxX, 3);
        Assert.Equal(0, content.MinY, 3);
        Assert.Equal(3 * FeetToMillimetres, content.MaxY, 3);
        Assert.True(content.Faces[0].Outline);
    }

    [Fact]
    public void Collect_ReturnsNullWithoutAFace()
    {
        Assert.Null(ElevationPrinter.Collect(Document(), "south"));
    }

    [Fact]
    public void DrawContent_WritesTheProjectedPolygons()
    {
        var document = Document(WallWithFace(600));
        var content = ElevationPrinter.Collect(document, "south")!;
        var pdf = new PdfDocument();
        PdfPage page = pdf.AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 100, 10, 10);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string text = PdfTestContent.Read(stream.ToArray());

        // The face fills without stroking its loop ("f"); the shared edge set inks the outline (" l ").
        // A single quad face keeps its four boundary edges.
        Assert.Contains(" l ", text);
        Assert.Contains(" f\n", text);
        Assert.Equal(4, content.Edges.Count);
    }

    [Fact]
    public void Collect_RemovesTheSharedDiagonalOfCoplanarFaces()
    {
        // Two side-by-side quads in one plane: the shared edge is interior triangulation, not inked.
        var document = Document(
            QuadWall(600, [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]], [0, 1, 0]),
            QuadWall(601, [[4, 0, 0], [8, 0, 0], [8, 0, 3], [4, 0, 3]], [0, 1, 0]));

        var content = ElevationPrinter.Collect(document, "south")!;

        Assert.Equal(2, content.Faces.Count);
        Assert.Equal(6, content.Edges.Count);
    }

    [Fact]
    public void Collect_KeepsTheCreaseBetweenDifferentPlanes()
    {
        // Two quads sharing an edge at 45 degrees: the crease is drawn once, not twice and not removed --
        // but the leaning-back top of the tilted quad hides behind the front wall, leaving five segments.
        var document = Document(
            QuadWall(600, [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]], [0, 1, 0]),
            QuadWall(601, [[0, 0, 0], [4, 0, 0], [4, 1, 1], [0, 1, 1]], [0, -0.7071, 0.7071]));

        var content = ElevationPrinter.Collect(document, "south")!;

        Assert.Equal(2, content.Faces.Count);
        Assert.Equal(5, content.Edges.Count);
    }

    [Fact]
    public void Collect_MergesOverlappingCoplanarEdges()
    {
        // Two overlapping coplanar quads: the shared stretch is inked once, not twice.
        var document = Document(
            QuadWall(600, [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]], [0, 1, 0]),
            QuadWall(601, [[2, 0, 0], [6, 0, 0], [6, 0, 3], [2, 0, 3]], [0, 1, 0]));

        var content = ElevationPrinter.Collect(document, "south")!;

        Assert.Equal(2, content.Faces.Count);
        Assert.Equal(6, content.Edges.Count);
    }

    [Fact]
    public void DrawContent_ClipsEdgesToTheCropRect()
    {
        // A second wall far to the right of the crop rectangle contributes fills but no ink.
        var document = Document(
            QuadWall(600, [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]], [0, 1, 0]),
            QuadWall(601, [[40, 0, 0], [44, 0, 0], [44, 0, 3], [40, 0, 3]], [0, 1, 0]));
        var content = ElevationPrinter.Collect(document, "south")!;
        Assert.Equal(8, content.Edges.Count);
        var pdf = new PdfDocument();
        PdfPage page = pdf.AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 100, 10, 10, 1, null, (0, 0, 5000, 3000));
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string text = PdfTestContent.Read(stream.ToArray());

        Assert.Equal(4, CountOccurrences(text, " l S"));
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    [Fact]
    public void SheetComposer_DrawsAnElevationViewport()
    {
        var sheet = new Element { Id = 100, CategoryId = (int)BuiltInCategory.OST_Sheets };
        Add(sheet, BuiltInParameter.SHEET_NUMBER, StorageType.String, "A103");
        Add(sheet, BuiltInParameter.SHEET_NAME, StorageType.String, "Elevations");
        var title = new Element
        {
            Id = 301,
            CategoryId = (int)BuiltInCategory.OST_TitleBlocks,
            SerializedOwnerDbViewId = 100,
            TypeId = 300,
        };
        var type = new Element
        {
            Id = 300,
            CategoryId = (int)BuiltInCategory.OST_TitleBlocks,
            BoundingBox = [0, 0, 0, 841 / FeetToMillimetres, 594 / FeetToMillimetres, 0],
        };
        var viewport = new Element
        {
            Id = 700,
            CategoryId = (int)BuiltInCategory.OST_Viewports,
            SerializedOwnerDbViewId = 100,
            ViewportViewId = 400,
            ViewportOffset = [400 / FeetToMillimetres, 300 / FeetToMillimetres, 0],
        };
        var document = Document(sheet, title, type, viewport, ElevationView(400, "South", 100), WallWithFace(600));

        PdfDocument pdf = SheetComposer.Render(document, 100);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string text = PdfTestContent.Read(stream.ToArray());

        Assert.Contains(" l ", text);
    }
}
