using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class SolidQuantitiesTests
{
    private static List<double[]> Quad(double[] a, double[] b, double[] c, double[] d) => [a, b, c, d];

    /// <summary>Axis-aligned box with the six faces wound without regard to a common orientation (as decoded shells are).</summary>
    internal static GeometryData Box(double x0, double y0, double z0, double x1, double y1, double z1, params int[] reversedFaces)
    {
        double[] p000 = [x0, y0, z0], p100 = [x1, y0, z0], p110 = [x1, y1, z0], p010 = [x0, y1, z0];
        double[] p001 = [x0, y0, z1], p101 = [x1, y0, z1], p111 = [x1, y1, z1], p011 = [x0, y1, z1];
        var loops = new List<List<double[]>>
        {
            Quad(p000, p100, p110, p010), Quad(p001, p101, p111, p011), Quad(p000, p100, p101, p001),
            Quad(p010, p110, p111, p011), Quad(p000, p010, p011, p001), Quad(p100, p110, p111, p101),
        };
        foreach (int face in reversedFaces) loops[face].Reverse();
        return Geometry(loops.Select(loop => new GeoFace { OuterVertices = loop }).ToArray());
    }

    private static GeometryData Geometry(params GeoFace[] faces) => new()
    {
        Faces = faces.ToList(),
        BrepSolids = [new GeoBrepSolid { FaceIndices = Enumerable.Range(0, faces.Length).ToList(), IsClosed = true }],
    };

    [Theory]
    [InlineData()]
    [InlineData(0)]
    [InlineData(1, 4)]
    [InlineData(0, 1, 2, 3, 4, 5)]
    public void BoxGivesItsExactQuantitiesWhateverTheWindingOfItsFaces(params int[] reversed)
    {
        var shell = SolidQuantities.OfSingleClosedShell(Box(40, -41, -3.5, 42, -39, -2.5, reversed));
        Assert.NotNull(shell);
        Assert.Equal(4.0, shell.Value.Volume, 9);
        Assert.Equal(2 * (4 + 2 + 2), shell.Value.SurfaceArea, 9);
        Assert.Equal(4.0, shell.Value.LargestFaceArea, 9);
    }

    [Fact]
    public void QuantityCalculationsLeaveArchivedFacesInScratch()
    {
        var box = Box(0, 0, 0, 2, 3, 4, 1, 4);
        foreach (var face in box.Faces) face.PlaneSurface = true;
        var expected = SolidQuantities.OfSingleClosedShell(box);
        Assert.NotNull(expected);
        using var scratch = new MemoryStream();
        new GeometryFaceStore(new PartitionChunks.Backing(scratch)).Archive(new Element { Geometry = box });
        var archived = box.ReadFaces;
        Assert.True(scratch.Length > 0);

        Assert.Equal(expected, SolidQuantities.OfSingleClosedShell(box));
        Assert.Equal(expected, SolidQuantities.OfAllSolids(box));
        Assert.Equal(6, SolidQuantities.RoomPlanArea(box)!.Value, 9);
        Assert.Same(archived, box.ReadFaces);
    }

    [Fact]
    public void HoleThroughAFacePairIsSubtracted()
    {
        // 4 x 4 x 1 plate with a 2 x 2 hole: the top and bottom faces carry the hole, four more faces line it.
        var plate = Box(0, 0, 0, 4, 4, 1);
        double[] h00 = [1, 1, 0], h10 = [3, 1, 0], h11 = [3, 3, 0], h01 = [1, 3, 0];
        double[] t00 = [1, 1, 1], t10 = [3, 1, 1], t11 = [3, 3, 1], t01 = [1, 3, 1];
        plate.Faces[0].InnerVertices = [[h00, h10, h11, h01]];
        plate.Faces[1].InnerVertices = [[t00, t10, t11, t01]];
        plate.Faces.AddRange([
            new GeoFace { OuterVertices = Quad(h00, h10, t10, t00) }, new GeoFace { OuterVertices = Quad(h10, h11, t11, t10) },
            new GeoFace { OuterVertices = Quad(h11, h01, t01, t11) }, new GeoFace { OuterVertices = Quad(h01, h00, t00, t01) }]);
        plate.BrepSolids![0].FaceIndices = Enumerable.Range(0, plate.Faces.Count).ToList();
        var shell = SolidQuantities.OfSingleClosedShell(plate);
        Assert.NotNull(shell);
        Assert.Equal(16 - 4, shell.Value.Volume, 9);
        Assert.Equal(2 * (16 - 4) + 4 * 4 + 4 * 2 * 1, shell.Value.SurfaceArea, 9); // top and bottom, outer sides, hole walls
        Assert.Equal(12, shell.Value.LargestFaceArea, 9);
    }

    [Fact]
    public void OpenShellGivesNothing()
    {
        var box = Box(0, 0, 0, 1, 1, 1);
        box.Faces.RemoveAt(5);
        box.BrepSolids![0].FaceIndices = Enumerable.Range(0, 5).ToList();
        Assert.Null(SolidQuantities.OfSingleClosedShell(box));
    }

    [Fact]
    public void ShellNotMarkedClosedGivesNothing()
    {
        var box = Box(0, 0, 0, 1, 1, 1);
        box.BrepSolids![0].IsClosed = false;
        Assert.Null(SolidQuantities.OfSingleClosedShell(box));
    }

    [Fact]
    public void SeveralShellsGiveNothing()
    {
        var box = Box(0, 0, 0, 1, 1, 1);
        box.BrepSolids!.Add(new GeoBrepSolid { FaceIndices = [0, 1, 2], IsClosed = true });
        Assert.Null(SolidQuantities.OfSingleClosedShell(box));
    }

    [Fact]
    public void NoGeometryGivesNothing() => Assert.Null(SolidQuantities.OfSingleClosedShell(null));

    [Fact]
    public void TwoFacesOnOneEdgeInTheSameDirectionAreFlippedNotRejected()
    {
        // A wound-inside-out shell is the same shell: the volume is positive.
        var shell = SolidQuantities.OfSingleClosedShell(Box(0, 0, 0, 2, 3, 4, 0, 1, 2, 3, 4, 5));
        Assert.Equal(24, shell!.Value.Volume, 9);
    }

    [Fact]
    public void RoomAreaIsHalfTheProjectedAreaOfTheShell()
    {
        GeoFace Horizontal(double z, double width, double depth, double normalZ, double hole = 0) => new()
        {
            Normal = [0, 0, normalZ],
            OuterVertices = Quad([0, 0, z], [width, 0, z], [width, depth, z], [0, depth, z]),
            InnerVertices = hole == 0 ? null : [Quad([1, 1, z], [1 + hole, 1, z], [1 + hole, 1 + hole, z], [1, 1 + hole, z])],
        };
        var wall = new GeoFace { Normal = [1, 0, 0], OuterVertices = Quad([0, 0, 0], [0, 5, 0], [0, 5, 3], [0, 0, 3]) };
        // a floor with a hole and the ceiling above it: the hole is not part of the room
        var holed = new GeometryData { Faces = [Horizontal(3, 5, 5, -1, hole: 2), wall, Horizontal(0, 5, 5, 1, hole: 2)] };
        Assert.Equal(25 - 4, SolidQuantities.RoomPlanArea(holed)!.Value, 9);
        // a stepped floor: two floor levels under one ceiling are one plan area
        var stepped = new GeometryData { Faces = [Horizontal(3, 5, 3, -1), Horizontal(0, 3, 3, 1), Horizontal(0.5, 2, 3, 1)] };
        Assert.Equal(15, SolidQuantities.RoomPlanArea(stepped)!.Value, 9);
        Assert.Null(SolidQuantities.RoomPlanArea(new GeometryData()));
        Assert.Null(SolidQuantities.RoomPlanArea(null));
    }
}
