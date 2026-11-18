using RvtIO.Geometry;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>RE-486: a ceiling in pieces takes the piece holding its first top face, not the whole surface.
/// The piece is a connected component of the horizontal top at one height; a solid whose first top face
/// is sloped, a stepped solid, or one without a single matching underside piece, keeps the whole upper surface.</summary>
public class CoveringFirstPieceTests
{
    private const double SquareFeet = 0.3048 * 0.3048;

    private static GeoFace Square(double x0, double y0, double size, double z, uint source = 0)
    {
        var face = new GeoFace();
        face.OuterVertices = new List<double[]>
        {
            new double[3] { x0, y0, z },
            new double[3] { x0 + size, y0, z },
            new double[3] { x0 + size, y0 + size, z },
            new double[3] { x0, y0 + size, z },
        };
        face.GeometryNode = 1;
        face.SourceFace = source;
        return face;
    }

    private static GeometryData Slabs(params GeoFace[] faces) => new() { Faces = faces.ToList() };

    private static GeometryData TwoPieceSlab() => Slabs(
        Square(0, 0, 10, 10, 1),
        Square(20, 0, 10, 10, 2),
        Square(0, 0, 10, 0, 1),
        Square(20, 0, 10, 0, 2));

    [Fact]
    public void FirstPiece_IsOnePieceNotTheWholeSurface()
    {
        var shells = BrepMeasures.ShellsOfCurrent(TwoPieceSlab());

        double? piece = IfcExporter.FirstTopPieceAreaCurrent(shells);
        double whole = IfcExporter.TopSurfaceAreaCurrent(shells);

        Assert.NotNull(piece);
        Assert.Equal(100, piece!.Value, 6);
        Assert.Equal(200, whole, 6);
    }

    [Fact]
    public void SharedSerializedFace_StillCountsConnectedComponents()
    {
        // Two disconnected top regions cut from one serialized face, with matching undersides.
        var geometry = Slabs(
            Square(0, 0, 10, 10, 1),
            Square(20, 0, 15, 10, 1),
            Square(0, 0, 10, 0, 2),
            Square(20, 0, 15, 0, 2));
        var shells = BrepMeasures.ShellsOfCurrent(geometry);

        double? piece = IfcExporter.FirstTopPieceAreaCurrent(shells);

        Assert.NotNull(piece);
        Assert.Equal(100, piece!.Value, 6);
    }

    [Fact]
    public void BottomOrder_DoesNotDecideTheMatch()
    {
        // The underside piece matching the first top piece is listed last.
        var geometry = Slabs(
            Square(0, 0, 10, 10, 1),
            Square(20, 0, 15, 10, 2),
            Square(20, 0, 15, 0, 3),
            Square(0, 0, 10, 0, 4));
        var shells = BrepMeasures.ShellsOfCurrent(geometry);

        double? piece = IfcExporter.FirstTopPieceAreaCurrent(shells);

        Assert.NotNull(piece);
        Assert.Equal(100, piece!.Value, 6);
    }

    [Fact]
    public void SinglePiece_ReturnsNullSoCallerKeepsTheWholeSurface()
    {
        var geometry = Slabs(
            Square(0, 0, 10, 10, 1),
            Square(0, 0, 10, 0, 1));
        var shells = BrepMeasures.ShellsOfCurrent(geometry);

        Assert.Null(IfcExporter.FirstTopPieceAreaCurrent(shells));
    }

    [Fact]
    public void MismatchedBottom_ReturnsNull()
    {
        var geometry = Slabs(
            Square(0, 0, 10, 10, 1),
            Square(20, 0, 10, 10, 2),
            Square(0, 0, 5, 0, 3));
        var shells = BrepMeasures.ShellsOfCurrent(geometry);

        Assert.Null(IfcExporter.FirstTopPieceAreaCurrent(shells));
    }

    [Fact]
    public void SteppedTop_ReturnsNull()
    {
        // Two top pieces at different heights are not one constant-height primitive.
        var geometry = Slabs(
            Square(0, 0, 10, 10, 1),
            Square(20, 0, 10, 12, 2),
            Square(0, 0, 10, 0, 3),
            Square(20, 0, 10, 2, 4));
        var shells = BrepMeasures.ShellsOfCurrent(geometry);

        Assert.Null(IfcExporter.FirstTopPieceAreaCurrent(shells));
    }

    private static GeoFace Sloped(double x, double size, double z, uint source, double slope)
    {
        var face = new GeoFace();
        face.OuterVertices = new List<double[]>
        {
            new double[3] { x, 0, z },
            new double[3] { x + size, 0, z },
            new double[3] { x + size, size, z + slope * size },
            new double[3] { x, size, z + slope * size },
        };
        face.GeometryNode = 1;
        face.SourceFace = source;
        return face;
    }

    private static GeometryData SlopedBox(double x, double size, double z, uint source, double slope)
    {
        var top = Sloped(x, size, z, source, slope);
        var bottom = Sloped(x, size, 0, source + 1, slope);
        var result = new List<GeoFace>
        {
            top,
            new GeoFace
            {
                GeometryNode = 1,
                SourceFace = source + 1,
                OuterVertices = bottom.OuterVertices!.AsEnumerable().Reverse().ToList(),
            },
        };
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            result.Add(new GeoFace
            {
                GeometryNode = 1,
                SourceFace = source + 2 + (uint)i,
                OuterVertices = [bottom.OuterVertices![i], bottom.OuterVertices[j], top.OuterVertices![j], top.OuterVertices[i]],
            });
        }
        return new GeometryData
        {
            Faces = result,
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(0, result.Count).ToList() }],
        };
    }

    [Fact]
    public void SlopedSolid_KeepsTheWholeUpperSurface()
    {
        // Two disjoint closed sloped boxes: the top is not horizontal, so no piece is taken.
        var first = SlopedBox(0, 10, 10, 1, 0.5);
        var second = SlopedBox(20, 15, 10, 10, 0.5);
        var geometry = new GeometryData
        {
            Faces = first.Faces.Concat(second.Faces).ToList(),
            BrepSolids =
            [
                new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(0, first.Faces.Count).ToList() },
                new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(first.Faces.Count, second.Faces.Count).ToList() },
            ],
        };
        var shells = BrepMeasures.ShellsOfCurrent(geometry);

        Assert.Null(IfcExporter.FirstTopPieceAreaCurrent(shells));
        Assert.Equal(111.803398875 + 251.557647469, IfcExporter.TopSurfaceAreaCurrent(shells), 6);
    }

    private static List<(string Name, double Value)> QuantitiesOf(Element element)
    {
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.NOCHANGE.,$,$,$,0");
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int axis = w.Add("IFCAXIS2PLACEMENT3D", $"#{origin},$,$");
        int placement = w.Add("IFCLOCALPLACEMENT", $"$,#{axis}");
        int product = w.Add("IFCCOVERING", $"'QZGD7y6S5209$mGLi_sPlj',#{owner},'P',$,$,#{placement},$,'1'");
        new IfcExporter().ApplyCurrentBaseQuantities(
            w, new Dictionary<int, Element> { [element.Id] = element }, new Dictionary<int, int> { [product] = element.Id },
            new Dictionary<int, (int, int, string)>(), owner, null);
        return Enumerable.Range(0, w.EntityCount).Select(w.EntityAt).Where(e => e.Type.StartsWith("IFCQUANTITY", StringComparison.Ordinal))
            .Select(e =>
            {
                var parts = IfcExporter.SplitStepArguments(e.Args);
                return (parts[0].Trim('\''), double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
            }).ToList();
    }

    [Fact]
    public void Caller_TwoPiecesWithoutHostArea_WritesTheFirstPiece()
    {
        var ceiling = new Element { Id = 3, CategoryId = -2000038, Geometry = TwoPieceSlab() };

        var quantities = QuantitiesOf(ceiling);

        Assert.Equal(100 * SquareFeet, quantities.Single(q => q.Name == "GrossCeilingArea").Value, 9);
    }

    [Fact]
    public void Caller_SlopedFirstThenHorizontalPieces_WritesTheWholeUpperSurface()
    {
        // A sloped first top face disqualifies the piece rule even where later horizontal parts
        // with matching undersides follow: the whole upper surface stays.
        var sloped = SlopedBox(50, 10, 10, 20, 0.5);
        var first = SlopedBox(0, 10, 10, 1, 0);
        var second = SlopedBox(20, 15, 10, 10, 0);
        var geometry = new GeometryData
        {
            Faces = sloped.Faces.Concat(first.Faces).Concat(second.Faces).ToList(),
            BrepSolids =
            [
                new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(0, sloped.Faces.Count + first.Faces.Count + second.Faces.Count).ToList() },
            ],
        };

        Assert.Null(IfcExporter.FirstTopPieceAreaCurrent(BrepMeasures.ShellsOfCurrent(geometry)));
        var ceiling = new Element { Id = 5, CategoryId = -2000038, Geometry = geometry };

        var quantities = QuantitiesOf(ceiling);

        Assert.Equal((111.803398875 + 100 + 225) * SquareFeet, quantities.Single(q => q.Name == "GrossCeilingArea").Value, 6);
    }

    [Fact]
    public void Caller_SlopedSolidWithoutHostArea_WritesTheWholeUpperSurface()
    {
        var first = SlopedBox(0, 10, 10, 1, 0.5);
        var second = SlopedBox(20, 15, 10, 10, 0.5);
        var ceiling = new Element
        {
            Id = 4,
            CategoryId = -2000038,
            Geometry = new GeometryData
            {
                Faces = first.Faces.Concat(second.Faces).ToList(),
                BrepSolids =
                [
                    new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(0, first.Faces.Count).ToList() },
                    new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(first.Faces.Count, second.Faces.Count).ToList() },
                ],
            },
        };

        var quantities = QuantitiesOf(ceiling);

        Assert.Equal((111.803398875 + 251.557647469) * SquareFeet, quantities.Single(q => q.Name == "GrossCeilingArea").Value, 6);
    }
}
