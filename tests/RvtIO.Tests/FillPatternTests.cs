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
    [InlineData(-0.02, 0.1, true)]
    [InlineData(0.02, -0.1, false)]
    [InlineData(0.0, 0.1, false)]
    public void DraftingTiles_UsesPaperLengthsAndRejectsInvalidDashes(double offset, double dash, bool valid)
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
