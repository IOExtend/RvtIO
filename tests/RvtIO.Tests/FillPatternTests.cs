using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public class FillPatternTests
{
    [Fact]
    public void ProjectDashedModelGrids_PreservesDashesAndOffsetsInAReflectedView()
    {
        var doc = RvtDocument.FromInfo(new RevitFileInfo());
        var face = new GeoFace { ModelPattern = new(8, [5, 1, 2], [1, 0, 0], [0, 0, 1]) };
        doc.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
        {
            [8] = ("Glass", [new(0, 1, 2, 3, 4, [0.25, 0.75], Target: 1, Orientation: 0)]),
        };

        var grid = Assert.Single(FillHatch.ProjectDashedModelGrids(doc, face, p => (2 * p[0] + p[2], -3 * p[2]))!);
        Assert.Equal((16d, -12d, 0d, 12d, -10d), (grid.X, grid.Y, grid.Angle, grid.Spacing, grid.Shift));
        Assert.Equal(new[] { 0.5, 1.5 }, grid.Segments);
    }

    [Theory]
    [InlineData(1.0, 1.0, true)]
    [InlineData(0.0, 1.0, false)]
    [InlineData(1.0, 0.0, false)]
    public void ProjectModelGrid_RequiresTwoIndependentProjectedAxes(double xScale, double yScale, bool valid)
    {
        var doc = RvtDocument.FromInfo(new RevitFileInfo());
        var face = new GeoFace { ModelPattern = new(8, [5, 1, 2], [1, 0, 0], [0, 0, 1]) };
        doc.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
        {
            [8] = ("Lines", [new(0, 2, 3, 0, 4, [], Target: 1, Orientation: 0)]),
        };

        var grid = FillHatch.ProjectModelGrid(doc, face, p => (p[0] * xScale, p[2] * yScale));

        Assert.Equal(valid ? new FillHatch.ModelGrid(7, 5, 0, 4) : null, grid);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    public void DraftingCutProjectsTheMaterialGridOntoTheFace(int target, int orientation, bool supported)
    {
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.02, [0.02, 0.02],
            Target: target, Orientation: orientation);
        var doc = DocumentWithPattern(grid);
        doc.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [10] = (8, -1, null) };
        doc.RawData.MaterialCutPatternColors = new Dictionary<int, int> { [10] = 0x124fd0 };

        var pattern = ViewportSurfacePattern.CreateDraftingCut(doc, 10, (20, 15), (304.8, 120), (0, 152.4), 0.1);

        if (!supported)
        {
            Assert.Null(pattern);
            return;
        }
        Assert.NotNull(pattern);
        Assert.Equal(0x124fd0, pattern.Color);
        Assert.True(pattern.Coverage(23.048, 16.2, 0.05, 0.05) > 0.99);
        Assert.Equal(0, pattern.Coverage(29.144, 18.6, 0.05, 0.05), 8);
        Assert.Equal(0, pattern.Coverage(23.048, 17.7, 0.05, 0.05), 8);
    }

    private static RvtDocument DocumentWithPattern(params TfBlockDeserializer.PatternGrid[] grids)
    {
        var doc = RvtDocument.FromInfo(new RevitFileInfo());
        doc.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
        {
            [8] = ("Pattern", grids),
        };
        return doc;
    }

    [Theory]
    [InlineData(1, 4, 0, 1, true)]
    [InlineData(0, 4, 0, 1, false)]
    [InlineData(1, 0, 0, 1, false)]
    [InlineData(1, -4, 0, 1, false)]
    [InlineData(1, 4, 2, 1, false)]
    [InlineData(1, 4, 0, 2, false)]
    public void ProjectModelGrid_KeepsOnlyOneContinuousGridOfTheFaceTarget(
        int target, double offset, int dashCount, int gridCount, bool valid)
    {
        double[] segments = dashCount == 0 ? [] : [0.25, 0.75];
        var grids = Enumerable.Range(0, gridCount)
            .Select(_ => new TfBlockDeserializer.PatternGrid(0, 2, 3, 0, offset, segments, Target: target, Orientation: 0))
            .ToArray();
        var face = new GeoFace { ModelPattern = new(8, [5, 1, 2], [1, 0, 0], [0, 0, 1]) };

        var grid = FillHatch.ProjectModelGrid(DocumentWithPattern(grids), face, p => (p[0], p[2]));

        Assert.Equal(valid, grid != null);
    }

    [Fact]
    public void ProjectModelGrid_NeedsTheFacePlacementAndItsPattern()
    {
        var grid = new TfBlockDeserializer.PatternGrid(0, 2, 3, 0, 4, [], Target: 1, Orientation: 0);
        var placed = new GeoFace { ModelPattern = new(8, [5, 1, 2], [1, 0, 0], [0, 0, 1]) };
        var unknownPattern = new GeoFace { ModelPattern = new(9, [5, 1, 2], [1, 0, 0], [0, 0, 1]) };

        Assert.Null(FillHatch.ProjectModelGrid(DocumentWithPattern(grid), new GeoFace(), p => (p[0], p[2])));
        Assert.Null(FillHatch.ProjectModelGrid(DocumentWithPattern(grid), unknownPattern, p => (p[0], p[2])));
        Assert.NotNull(FillHatch.ProjectModelGrid(DocumentWithPattern(grid), placed, p => (p[0], p[2])));
    }

    [Theory]
    [InlineData(1, 0, 0.25, 0.75, true)]
    [InlineData(2, 0, 0.25, 0.75, false)]
    [InlineData(1, 1, 0.25, 0.75, false)]
    [InlineData(1, 0, 0.0, 0.0, false)]
    [InlineData(1, 0, -0.25, 1.0, false)]
    public void ProjectDashedModelGrids_KeepsOnlyDashesWithAPositivePeriodAndNoNegativeSegment(
        int target, int orientation, double dash, double gap, bool valid)
    {
        var grid = new TfBlockDeserializer.PatternGrid(0, 2, 3, 0, 4, [dash, gap], Target: target, Orientation: orientation);
        var face = new GeoFace { ModelPattern = new(8, [5, 1, 2], [1, 0, 0], [0, 0, 1]) };

        var grids = FillHatch.ProjectDashedModelGrids(DocumentWithPattern(grid), face, p => (p[0], p[2]));

        Assert.Equal(valid, grids != null);
    }

    [Fact]
    public void ProjectDashedModelGrids_RefusesAContinuousGrid()
    {
        var continuous = new TfBlockDeserializer.PatternGrid(0, 2, 3, 0, 4, [], Target: 1, Orientation: 0);
        var face = new GeoFace { ModelPattern = new(8, [5, 1, 2], [1, 0, 0], [0, 0, 1]) };

        Assert.Null(FillHatch.ProjectDashedModelGrids(DocumentWithPattern(continuous), face, p => (p[0], p[2])));
    }

    [Fact]
    public void ProjectDashedModelGrids_RefusesAPatternWhenOneOfItsGridsIsRefused()
    {
        var dashed = new TfBlockDeserializer.PatternGrid(0, 2, 3, 0, 4, [0.25, 0.75], Target: 1, Orientation: 0);
        var degenerate = dashed with { Offset = 0 };
        var face = new GeoFace { ModelPattern = new(8, [5, 1, 2], [1, 0, 0], [0, 0, 1]) };

        Assert.Equal(2, FillHatch.ProjectDashedModelGrids(DocumentWithPattern(dashed, dashed with { Angle = 1 }), face, p => (p[0], p[2]))!.Count);
        Assert.Null(FillHatch.ProjectDashedModelGrids(DocumentWithPattern(dashed, degenerate), face, p => (p[0], p[2])));
        Assert.Null(FillHatch.ProjectDashedModelGrids(DocumentWithPattern(), face, p => (p[0], p[2])));
    }

    [Theory]
    [InlineData(-0.02, -0.01)]
    [InlineData(0.02, 0.01)]
    public void DraftingTiles_ShiftFollowsTheSignOfTheOffset(double offset, double expectedShift)
    {
        var grid = new TfBlockDeserializer.PatternGrid(0.5, 0.001, 0.002, 0.01, offset, [0.1, 0.2]);

        var tile = Assert.Single(FillHatch.DraftingTiles([grid], 15, 25, 0.1)!);

        Assert.Equal(expectedShift * 304.8, tile.Shift, 8);
        Assert.Equal(0.02 * 304.8, tile.Spacing, 8);
        Assert.Equal(0.5, tile.Angle);
        Assert.Equal((15 + 0.001 * 304.8, 25 + 0.002 * 304.8), (tile.X, tile.Y));
        Assert.NotNull(tile.Segments);
        Assert.Equal([0.1 * 304.8, 0.2 * 304.8], tile.Segments);
    }

    [Fact]
    public void DraftingTiles_RefuseALineSpacingThatDoesNotExceedThePenWidth()
    {
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.01, [0.1, 0.2]);
        double spacing = 0.01 * 304.8;

        Assert.Null(FillHatch.DraftingTiles([grid], 0, 0, spacing));
        Assert.Null(FillHatch.DraftingTiles([grid], 0, 0, spacing * 2));
        Assert.Single(FillHatch.DraftingTiles([grid], 0, 0, spacing / 2)!);
    }

    [Fact]
    public void DraftingTiles_SkipsContinuousGridsAndRefusesAZeroPeriod()
    {
        var continuous = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.02, []);
        var dashed = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.02, [0.1, 0.2]);
        var zeroPeriod = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.02, [0, 0]);

        Assert.Empty(FillHatch.DraftingTiles([continuous], 0, 0, 0.1)!);
        Assert.Single(FillHatch.DraftingTiles([continuous, dashed], 0, 0, 0.1)!);
        Assert.Null(FillHatch.DraftingTiles([dashed, zeroPeriod], 0, 0, 0.1));
        Assert.Null(FillHatch.DraftingTiles([dashed with { Angle = double.NaN }], 0, 0, 0.1));
    }

    [Theory]
    [InlineData(-0.02, 0.1, true)]
    [InlineData(0.02, -0.1, false)]
    [InlineData(0.0, 0.1, false)]
    public void DraftingTiles_UsesPaperLengthsAndRejectsInvalidGrids(double offset, double dash, bool valid)
    {
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0.01, offset, [dash, 0.2]);
        var tiles = FillHatch.DraftingTiles([grid], 15, 25, 0.1);
        if (!valid)
        {
            Assert.Null(tiles);
            return;
        }

        var tile = Assert.Single(tiles!);
        Assert.Equal(0.02 * 304.8, tile.Spacing, 8);
        Assert.Equal(-0.01 * 304.8, tile.Shift, 8);
        Assert.Equal((15d, 25d), (tile.X, tile.Y));
    }

    [Fact]
    public void Strokes_UnionOverlappingAndTouchingRegionsWithoutBridgingGaps()
    {
        IReadOnlyList<(double X, double Y)>[] polygons =
        [
            [(0, -1), (4, -1), (4, 1), (0, 1)],
            [(3, -1), (8, -1), (8, 1), (3, 1)],
            [(8, -1), (12, -1), (12, 1), (8, 1)],
            [(15, -1), (17, -1), (17, 1), (15, 1)],
        ];
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 10 / 304.8, [5 / 304.8, 2 / 304.8]);

        var strokes = FillHatch.Strokes(polygons, [grid], 0, 0).ToArray();

        Assert.Equal(3, strokes.Length);
        Assert.Equal((0d, 0d, 5d, 0d), strokes[0]);
        Assert.Equal((7d, 0d, 12d, 0d), strokes[1]);
        Assert.Equal((15d, 0d, 17d, 0d), strokes[2]);
    }

    [Theory]
    [InlineData(-1, 202, -12.5, true)]
    [InlineData(303, 202, -12.5, false)]
    [InlineData(-1, -1, -12.5, false)]
    [InlineData(-1, 202, double.NaN, false)]
    public void TrueNorth_ReadsOnlyProjectSiteGraphics(int family, int material, double depth, bool expected)
    {
        const ushort classId = 4007;
        const uint size = 101;
        const int owner = 101;
        byte[] block = new byte[16 + size];
        using var writer = new BinaryWriter(new MemoryStream(block));
        writer.Write(owner);
        writer.Seek(8, SeekOrigin.Begin);
        writer.Write(size);
        writer.Write(classId);
        // Synthetic TrueNorth root with empty cells and parameter stores.
        writer.Seek(16 + 0x22, SeekOrigin.Begin);
        writer.Write(owner);
        writer.Write(-1); // associated level
        writer.Write(family);
        writer.Seek(16 + 0x3e, SeekOrigin.Begin);
        writer.Write(-4); // design option
        writer.Seek(16 + 0x4d, SeekOrigin.Begin);
        writer.Write(depth);
        writer.Write(material);
        writer.Write(1); // quadrant bearing
        writer.Write(1); // degrees, minutes and seconds
        writer.Seek(block.Length - sizeof(uint), SeekOrigin.Begin);
        writer.Write(size);

        var result = TfBlockDeserializer.ExtractSiteCutGraphics(block, 0, size, classId, out var bearing);

        Assert.Equal(expected ? (depth, material) : ((double, int)?)null, result);
        Assert.Equal(family == -1 ? (1, 1) : ((int, int)?)null, bearing);
    }

    [SkippableFact]
    public void SampleFile_DecodesMaterialSurfacePatternAndItsGrids()
    {
        // A floor type's top compound layer is the material that prints in plan; a carpet names the "Sand"
        // surface pattern, whose three line families are the specks the reference draws in the living room.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var type = document.Elements.FirstOrDefault(e =>
            e.RootClassName == "FloorAttributes" && e.Name == "Timber Suspended Floor");
        Assert.NotNull(type);
        var layers = type!.SerializedHostTypeLayers;
        Assert.NotNull(layers);
        Assert.NotEmpty(layers!);
        int materialId = layers![0].RawMaterialId;
        Assert.True(materialId > 0);

        Assert.NotNull(document.RawData.MaterialPatterns);
        Assert.True(document.RawData.MaterialPatterns!.TryGetValue(materialId, out var material));
        Assert.True(material.SurfacePatternId > 0, $"material {materialId} has no surface pattern");

        Assert.NotNull(document.RawData.FillPatterns);
        Assert.True(document.RawData.FillPatterns!.TryGetValue(material.SurfacePatternId, out var pattern));
        Assert.Equal("Sand", pattern.Name);
        Assert.Equal(3, pattern.Grids.Count);
        Assert.Equal(0.006667, pattern.Grids[0].Offset, 6);
        Assert.Equal(12, pattern.Grids[0].Segments.Length);
        Assert.Equal(0.0011, pattern.Grids[0].Segments[0], 6);
    }

    [SkippableFact]
    public void SampleFile_DecodesTheTerrainCutPatternColor()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("The Revit basic sample is not available.");
        var document = RvtDocument.Open(path);
        var (baseElevation, materialId) = document.RawData.SiteCutGraphics!.Value;
        var material = document.RawData.MaterialPatterns![materialId];

        Assert.True(double.IsFinite(baseElevation));
        Assert.Equal("Earth", document.RawData.FillPatterns![material.CutPatternId].Name);
        Assert.Equal(0, document.RawData.MaterialCutPatternColors![materialId]);
    }

    [SkippableFact]
    public void SampleFile_LivingRoomCarriesItsFloorPattern()
    {
        // The Living room stands on the sunken floor whose carpet names the Sand pattern, so its plan fill
        // carries that hatch; the other rooms' floors have no surface pattern and get none.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var living = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Rooms
            && e.Name == "Living");
        Assert.NotNull(living);
        int levelId = (int)(living!.LookupParameter(BuiltInParameter.ROOM_LEVEL_ID)?.Value ?? 0);
        Assert.True(levelId > 0);

        var content = PlanPrinter.CollectLevel(document, levelId);

        Assert.NotNull(content);
        var patterned = Assert.Single(content!.RoomPatterns!);
        Assert.Equal(3, patterned.Grids.Count);
        Assert.Equal(7, patterned.Polygon.Count);
    }
}
