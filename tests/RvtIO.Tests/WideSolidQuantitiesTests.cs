using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public sealed class WideSolidQuantitiesTests
{
    [Theory]
    [InlineData(1, 2, 5d)]
    [InlineData(3, 4, 0d)]
    [InlineData(1, 3, null)]
    public void ExcludesCoarseVolumeOnlyWhenTheCompleteSourceBodyIsReused(uint firstFace, uint secondFace, double? expected)
    {
        var coarse = Body(1, 2);
        var detailed = Body(firstFace, secondFace);
        detailed.Faces.AddRange([new GRepFace { SourceFace = 8 }, new GRepFace { SourceFace = 9 }]);
        detailed.Solids.Add(new GRepSolid { IsClosed = true, FaceIndices = [2, 3] });

        double? excluded = WideSolidQuantities.SharedLocalVolume(coarse, detailed, new WideSolidQuantities.Solid(5, 12, 4, 1));

        Assert.Equal(expected, excluded);
    }

    [Fact]
    public void OpenRepresentationHasNoSolidQuantities()
    {
        var representation = Body(1, 2);
        representation.Solids[0].IsClosed = false;

        Assert.Null(WideSolidQuantities.Local(representation));
    }

    [Fact]
    public void BasicWallUsesTheLargerNetAreaOfTheTwoFinishPlanes()
    {
        var inner = Side(1, 0, 2) with { InnerVertices = [[[1, 0, 0], [1, 0, 1], [2, 0, 1], [2, 0, 0]]] };
        var source = new GRepData { Faces = [inner, Side(2, 1, 1), Side(3, 1, 1, 3), Side(4, 0.5, 20)] };
        Assert.Equal(6, WideSolidQuantities.WallSideArea(source, 1, 0, 0, 1, false));
        Assert.Null(WideSolidQuantities.WallSideArea(source, 1, 0, 0, 2, false));
    }

    [Fact]
    public void CurtainWallUsesOneCompleteProfileAndIgnoresGridFaces()
    {
        var source = new GRepData
        {
            Faces = [Side(1, 0, 2), Side(2, 1, 2), Side(3, 0, 2), Side(4, 1, 2), Side(5, 1, 3)],
            Solids = [new() { IsClosed = true, FaceIndices = [0, 1] }, new() { IsClosed = true, FaceIndices = [2, 3] },
                new() { IsClosed = false, FaceIndices = [4] }],
        };
        Assert.Equal(6, WideSolidQuantities.WallSideArea(source, 1, 0, 0, null, true));
        source.Faces[3] = Side(4, 1, 3);
        Assert.Null(WideSolidQuantities.WallSideArea(source, 1, 0, 0, null, true));
    }

    [Fact]
    public void LightingFixtureUsesMeasuredBodiesWithAnOpenMeshPresent()
    {
        var source = new GRepData
        {
            Faces = [new() { Vertices = [[0, 0, 4], [1, 0, 4], [0, 1, 4]] }],
            LightingBodyMeasures = (6, 22, 1),
            IsOpenSurface = true,
        };
        Assert.Null(WideSolidQuantities.Local(source));
        var measured = WideSolidQuantities.Local(source, WideSolidQuantities.Source.Directional)!.Value;
        Assert.Equal(6, measured.Volume, 12);
        Assert.Equal(22, measured.Surface, 12);
        source.LightingBodyMeasures = null;
        Assert.Null(WideSolidQuantities.Local(source, WideSolidQuantities.Source.Directional));
    }

    [Fact]
    public void VisibleVolumeRequiresACompleteSourceBodyMeasurement()
    {
        var source = Body(1, 2);
        Assert.Null(WideSolidQuantities.Local(source, WideSolidQuantities.Source.VisibleVolume));

        source.SourceBodyMeasures = (4, 0, 1);
        var measured = WideSolidQuantities.Local(source, WideSolidQuantities.Source.VisibleVolume)!.Value;
        Assert.Equal(4, measured.Volume);
        Assert.Equal(1, measured.BodyCount);
    }

    [Fact]
    public void ExtrusionRoofUsesTaggedLowerFacesAndIgnoresCutReveals()
    {
        var bottom = Side(1, 0, 2) with { GInfoTag = 19, XVector = [0, 0.6, -0.8], QuadratureArea = 7 };
        var reveal = bottom with { SourceFace = 2, GInfoTag = 20, QuadratureArea = 2 };
        var top = bottom with { SourceFace = 3, GInfoTag = 21, XVector = [0, -0.6, 0.8] };
        var source = new GRepData
        {
            Faces = [bottom, reveal, top],
            Solids = [new() { IsClosed = true, FaceIndices = [0, 1, 2] }],
        };
        Assert.Equal(7, WideSolidQuantities.RoofArea(source, new HashSet<int> { 19, 21 }));
        Assert.Null(WideSolidQuantities.RoofArea(source with { Faces = [bottom, reveal, top with { GInfoTag = null }] },
            new HashSet<int> { 19, 21 }));
    }

    [Fact]
    public void PlanarMassUsesBothInclinedAndHorizontalFacesOfCompleteShells()
    {
        var slope = Side(1, 0, 1) with { XVector = [0, 0.6, 0.8], QuadratureArea = 7 };
        var baseFace = slope with { SourceFace = 2, XVector = [0, 0, -1], QuadratureArea = 4 };
        var wall = slope with { SourceFace = 3, XVector = [0, 1, 0], QuadratureArea = 25 };
        var source = new GRepData
        {
            Faces = [slope, baseFace, wall],
            Solids = [new() { IsClosed = true, FaceIndices = [0, 1, 2] }],
        };
        Assert.Equal(11, WideSolidQuantities.HorizontalArea(source, bottom: false, planarMass: true));
        source.Solids[0].IsClosed = false;
        Assert.Null(WideSolidQuantities.HorizontalArea(source, bottom: false, planarMass: true));
    }

    [Fact]
    public void HorizontalAreaCountsEachTrimmedSourceFaceOnce()
    {
        var top = new GRepFace { SourceFace = 1, PlaneSurface = true, XVector = [0, 0, 1], QuadratureArea = 7,
            Vertices = [[0, 0, 2], [3, 0, 2], [3, 2, 2], [0, 2, 2]] };
        var bottom = top with { SourceFace = 2, XVector = [0, 0, -1], QuadratureArea = 3 };
        var source = new GRepData { Faces = [top, top, bottom, Side(3, 0, 2)] };
        Assert.Equal(7, WideSolidQuantities.HorizontalArea(source, bottom: false));
        Assert.Equal(3, WideSolidQuantities.HorizontalArea(source, bottom: true));
    }

    [Fact]
    public void CurtainDoorAreaUsesTheOpeningSpannedByRectangularLeaves()
    {
        var source = new GRepData
        {
            Faces = [.. Leaf(0, 3, 2), .. Leaf(3.1, 6.1, 2)],
            Solids = [new() { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] },
                new() { IsClosed = true, FaceIndices = [6, 7, 8, 9, 10, 11] }],
        };
        Assert.Equal(12.2, WideSolidQuantities.CurtainDoorArea(source)!.Value, 12);
        source.Faces = [.. Leaf(0, 3, 2), .. Leaf(3.1, 6.1, 3)];
        Assert.Null(WideSolidQuantities.CurtainDoorArea(source));

        static IEnumerable<GRepFace> Leaf(double left, double right, double height) =>
            SolidQuantitiesTests.Box(left, 0, 0, right, 0.1, height).Faces.Select((face, i) => new GRepFace
            {
                PlaneSurface = true, Vertices = face.OuterVertices, Origin = face.OuterVertices![0],
                XVector = i == 3 ? [0, 1, 0] : [0, 0, 0],
            });
    }

    [Fact]
    public void MassAreaSelectsMatchingCapsOrTheSurfaceOfAPitchedPlanarBody()
    {
        var solid = new WideSolidQuantities.Solid(12, 40, 10, 1);
        Assert.Equal(10, WideSolidQuantities.MassArea(solid, 10, 10));
        Assert.Equal(20, WideSolidQuantities.MassArea(solid, 0, 10));
        Assert.Null(WideSolidQuantities.MassArea(solid, 9, 10));
        Assert.Null(WideSolidQuantities.MassArea(solid with { BodyCount = 2 }, 10, 10));
    }

    private static GRepFace Side(uint id, double offset, double height, double start = 0) => new()
    {
        PlaneSurface = true, SourceFace = id, Origin = [start, offset, 0], XVector = [0, 1, 0],
        Vertices = [[start, offset, 0], [start + 3, offset, 0], [start + 3, offset, height], [start, offset, height]],
    };

    private static GRepData Body(uint first, uint second) => new()
    {
        Faces = [new GRepFace { SourceFace = first }, new GRepFace { SourceFace = second }],
        Solids = [new GRepSolid { IsClosed = true, FaceIndices = [0, 1] }],
    };
}
