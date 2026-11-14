using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Representation decisions of the current profile for bodies whose faces are shared or whose Edge topology is incomplete.</summary>
public class IfcExporterDecisionsCurrentTests
{
    private static uint handle = 5000;

    private static GeoFace Quad(double[] a, double[] b, double[] c, double[] d, int node = 0) =>
        new() { OuterVertices = [a, b, c, d], GeometryNode = node, SourceFace = ++handle };

    /// <summary>The six faces of a box x, y, z about its corner.</summary>
    private static List<GeoFace> BoxFaces(double x, double y, double z, double ox = 0, double oy = 0, double oz = 0, int node = 0)
    {
        double[] P(double u, double v, double w) => [ox + u * x, oy + v * y, oz + w * z];
        return
        [
            Quad(P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0), node),
            Quad(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1), node),
            Quad(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1), node),
            Quad(P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1), node),
            Quad(P(1, 1, 0), P(0, 1, 0), P(0, 1, 1), P(1, 1, 1), node),
            Quad(P(0, 1, 0), P(0, 0, 0), P(0, 0, 1), P(0, 1, 1), node),
        ];
    }

    [Fact]
    public void ShellOfAnOpenTopologyThatIsAnExactClosedPolygonShell_IsClosed()
    {
        var faces = BoxFaces(2, 1, 1);
        var geometry = new GeometryData { Faces = faces, BrepSolids = [new() { IsClosed = false, FaceIndices = [0, 1, 2, 3, 4, 5] }] };

        Assert.True(IfcExporter.IsExactClosedDespiteOpenTopologyCurrent(geometry, geometry.BrepSolids[0], false));

        faces.RemoveAt(5);
        geometry.BrepSolids = [new() { IsClosed = false, FaceIndices = [0, 1, 2, 3, 4] }];
        Assert.False(IfcExporter.IsExactClosedDespiteOpenTopologyCurrent(geometry, geometry.BrepSolids[0], false));
    }

    [Fact]
    public void ANodeThatSharesAFaceWithAnotherNode_HasItInItsSolid()
    {
        // The decoded faces are kept once (under the node that comes first) while each node has the face in its own shell.
        var first = BoxFaces(2, 1, 1, node: 1);
        var second = BoxFaces(2, 1, 1, oy: 1, node: 2).Skip(1).ToList();
        second.Insert(0, first[4]);
        var geometry = new GeometryData
        {
            Faces = [.. first, .. second.Skip(1)],
            BrepSolids =
            [
                new() { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] },
                new() { IsClosed = true, FaceIndices = [4, 6, 7, 8, 9, 10] },
            ],
        };

        var solids = IfcExporter.OthersCurrentSolids(geometry);

        Assert.NotNull(solids);
        Assert.Equal([6, 6], solids.Select(solid => solid.Faces.Count));
        Assert.Contains(geometry.Faces[4], solids[1].Faces);
    }

    [Fact]
    public void FacesWithTheSameOutlineButDifferentOpenings_AreTwoFaces()
    {
        // The cap of a window frame and the cap of the glazing bar behind it are the same rectangle with different holes.
        List<double[]> Outline() => [[0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0]];
        GRepFace Face(List<List<double[]>>? holes) => new()
        {
            Vertices = Outline(), Origin = [0, 0, 0], XVector = [0, 0, 1], InnerVertices = holes, SourceFace = ++handle,
        };
        List<List<double[]>> Hole(double size) => [[[1, 1, 0], [1 + size, 1, 0], [1 + size, 1 + size, 0], [1, 1 + size, 0]]];

        GeometryData Build(bool separate) => ElementBuilder.BuildGeometryData(new GRepData
        {
            BoundingBox = [0, 0, 0, 4, 3, 0],
            Faces = [Face(Hole(1)), Face(Hole(0.5)), Face(Hole(1)), Face(null)],
        }, separate)!;

        // The legacy profile export keeps the geometry it was built on (the outline alone is the key): one face.
        Assert.False(new RvtReaderOptions().SeparateFacesWithDistinctOpenings);
        Assert.Single(Build(separate: false).Faces);
        Assert.Equal(3, Build(separate: true).Faces.Count);
    }
}
