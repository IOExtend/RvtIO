using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public sealed class WideSolidQuantitiesTests
{
    [SkippableTheory]
    [InlineData("2021")]
    [InlineData("2023")]
    public void HistoricalDuctFittingVolumeIncludesItsTrimmedRuledBodies(string year)
    {
        // Autodesk Revit Samples. Revit 2027.2 measures three Medium solids per instance.
        var doc = RvtDocument.Open(TestFiles.FindRelative($"{year}/BIM_Projekt_Golden_Nugget-Gebaeudetechnik.rvt"));
        const BuiltInParameter volume = BuiltInParameter.HOST_VOLUME_COMPUTED;
        var small = doc.GetElement("9ef96ed7-810a-4c84-bf8d-0f8f28aadfdf-0025016c");
        var large = doc.GetElement("870e077d-411d-47d4-a648-448dde90dfcc-002fdc4a");
        Assert.Equal(0.20801932072778165, Assert.IsType<double>(small?.LookupParameter(volume)?.Value), 10);
        Assert.Equal(2.6587743659848972, Assert.IsType<double>(large?.LookupParameter(volume)?.Value), 10);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void CableTraysExposeTheirCoarseBodyVolumesInThe2022JpSample(bool resolveCurrentCopies)
    {
        // Autodesk Revit 2022 Samples/サンプル設備.rvt, copied to this fixture name.
        var doc = RvtDocument.Open(TestFiles.FindRelative("2022/jp_sample_3.rvt"),
            new RvtReaderOptions { ResolveCurrentCopies = resolveCurrentCopies });
        var trays = doc.Collector().OfCategory(BuiltInCategory.OST_CableTray).WhereElementIsNotElementType().ToElements();
        var types = doc.Collector().OfCategory(BuiltInCategory.OST_CableTray).WhereElementIsElementType().ToElements();
        Assert.Equal(47, trays.Count);
        Assert.Equal(9, types.Count);
        Assert.All(types, type =>
        {
            Assert.Equal("RbsCableTrayType", type.RootClassName);
            Assert.Null(type.LookupParameter(BuiltInParameter.HOST_VOLUME_COMPUTED));
        });
        Assert.All(trays, tray =>
        {
            Assert.Equal("CableTray", tray.RootClassName);
            var area = tray.LookupParameter(BuiltInParameter.HOST_AREA_COMPUTED);
            Assert.NotNull(area);
            Assert.Equal(StorageType.Double, area.StorageType);
            Assert.False(area.HasRevitValue);
            Assert.Null(area.Value);
        });
        var volumes = trays.Select(tray =>
            Assert.IsType<double>(tray.LookupParameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.Value)).ToArray();
        // Revit 2027.2, internal cubic feet.
        Assert.Equal(0.08306293994227595, volumes.Min(), 10);
        Assert.Equal(26.62019577465626, volumes.Max(), 10);
        Assert.Equal(339.6598724599558, volumes.Sum(), 9);
    }

    [SkippableFact]
    public void StandaloneDuctFittingsExposeTheirOwnVolumeAndAreaInThe2022MepSample()
    {
        // Autodesk Revit 2022 Sample Project Files, Mechanical, Electrical, and Plumbing.
        var doc = RvtDocument.Open(TestFiles.FindRelative("2022/rme_basic_sample_project.rvt"));
        // Revit 2027.2 reports no fitting center line for either instance.
        var withArea = doc.GetElement("7485c179-9364-48d7-b68b-6736718012c4-000600c6");
        var withUnsetArea = doc.GetElement("7485c179-9364-48d7-b68b-6736718012c4-0005fc0b");
        Assert.NotNull(withArea);
        Assert.NotNull(withUnsetArea);
        const BuiltInParameter volume = BuiltInParameter.HOST_VOLUME_COMPUTED;
        const BuiltInParameter area = BuiltInParameter.HOST_AREA_COMPUTED;
        Assert.Equal(4.124314503041933, Assert.IsType<double>(withArea.LookupParameter(volume)?.Value), 10);
        Assert.Equal(2.628232886538773, Assert.IsType<double>(withArea.LookupParameter(area)?.Value), 10);
        Assert.Equal(4.767480007400812, Assert.IsType<double>(withUnsetArea.LookupParameter(volume)?.Value), 10);
        var emptyArea = withUnsetArea.LookupParameter(area);
        Assert.NotNull(emptyArea);
        Assert.Equal(StorageType.Double, emptyArea.StorageType);
        Assert.False(emptyArea.HasRevitValue);
        Assert.Null(emptyArea.Value);
    }

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
    public void RunVolumeUsesOnlyItsOwnClosedCoarseBodies()
    {
        var fine = new GRepData { SourceBodyMeasures = (7, 18, 1) };
        Assert.Equal(7d, WideSolidQuantities.OwnCoarseVolume(fine));
        fine.Coarse = new GRepData { SourceBodyMeasures = (3, 10, 1) };
        Assert.Equal(3d, WideSolidQuantities.OwnCoarseVolume(fine));
        fine.Coarse.IsOpenSurface = true;
        Assert.Null(WideSolidQuantities.OwnCoarseVolume(fine));
        fine.Coarse.IsOpenSurface = false;
        fine.Coarse.Instances.Add(new());
        Assert.Null(WideSolidQuantities.OwnCoarseVolume(fine));
        fine.Coarse.Instances.Clear();
        fine.Coarse.SourceBodyMeasures = null;
        Assert.Null(WideSolidQuantities.OwnCoarseVolume(fine));
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
