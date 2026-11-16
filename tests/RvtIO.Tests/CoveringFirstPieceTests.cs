using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>RE-486: a ceiling in pieces takes the piece holding its first top face, not the whole surface.</summary>
public class CoveringFirstPieceTests
{
    private static GeoFace MakeFace(double x0, double y0, double size, double z, int node, uint source)
    {
        var face = new GeoFace();
        face.OuterVertices = new List<double[]>();
        face.OuterVertices.Add(new double[3] { x0, y0, z });
        face.OuterVertices.Add(new double[3] { x0 + size, y0, z });
        face.OuterVertices.Add(new double[3] { x0 + size, y0 + size, z });
        face.OuterVertices.Add(new double[3] { x0, y0 + size, z });
        face.GeometryNode = node;
        face.SourceFace = source;
        return face;
    }

    private static GeometryData TwoPieceGeometry()
    {
        var geometry = new GeometryData();
        geometry.Faces = new List<GeoFace>();
        geometry.Faces.Add(MakeFace(0, 0, 10, 10, 1, 1));
        geometry.Faces.Add(MakeFace(20, 0, 10, 10, 1, 2));
        geometry.Faces.Add(MakeFace(0, 0, 10, 0, 1, 1));
        geometry.Faces.Add(MakeFace(20, 0, 10, 0, 1, 2));
        return geometry;
    }

    [Fact]
    public void FirstPiece_IsOnePieceNotTheWholeSurface()
    {
        var shells = IfcExporter.ShellsOfCurrent(TwoPieceGeometry());

        double? piece = IfcExporter.FirstTopPieceAreaCurrent(shells);
        double whole = IfcExporter.TopSurfaceAreaCurrent(shells);

        Assert.NotNull(piece);
        Assert.Equal(100, piece.Value, 6);
        Assert.Equal(200, whole, 6);
    }

    [Fact]
    public void SinglePiece_ReturnsNullSoCallerKeepsTheWholeSurface()
    {
        var geometry = new GeometryData();
        geometry.Faces = new List<GeoFace>();
        geometry.Faces.Add(MakeFace(0, 0, 10, 10, 1, 1));
        geometry.Faces.Add(MakeFace(0, 0, 10, 0, 1, 1));
        var shells = IfcExporter.ShellsOfCurrent(geometry);

        Assert.Null(IfcExporter.FirstTopPieceAreaCurrent(shells));
    }

    [Fact]
    public void MismatchedBottom_ReturnsNull()
    {
        // Bottom piece of a different area: no matching counterpart, keep the whole surface.
        var geometry = new GeometryData();
        geometry.Faces = new List<GeoFace>();
        geometry.Faces.Add(MakeFace(0, 0, 10, 10, 1, 1));
        geometry.Faces.Add(MakeFace(20, 0, 10, 10, 1, 2));
        geometry.Faces.Add(MakeFace(0, 0, 5, 0, 1, 1));
        var shells = IfcExporter.ShellsOfCurrent(geometry);

        Assert.Null(IfcExporter.FirstTopPieceAreaCurrent(shells));
    }
}
