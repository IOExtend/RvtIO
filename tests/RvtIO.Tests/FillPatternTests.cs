using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public class FillPatternTests
{
    [Fact]
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

    [Fact]
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
