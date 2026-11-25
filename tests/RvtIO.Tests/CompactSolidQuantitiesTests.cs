using RvtIO.Compact;
using RvtIO.Internal;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The measurement of the solids of an element (<see cref="CompactSolidQuantities.Measure(GeometryData, int)"/>), the formulas the V lines of the
/// value gate name, and the V lines themselves.</summary>
public class CompactSolidQuantitiesTests
{
    private static GeoFace Face(params double[][] corners) => new() { OuterVertices = [.. corners], PlaneSurface = true };

    /// <summary>The six faces of an axis-parallel box from (x, y, z) to (x + a, y + b, z + c).</summary>
    private static List<GeoFace> Box(double x, double y, double z, double a, double b, double c)
    {
        double[] p000 = [x, y, z], p100 = [x + a, y, z], p110 = [x + a, y + b, z], p010 = [x, y + b, z];
        double[] p001 = [x, y, z + c], p101 = [x + a, y, z + c], p111 = [x + a, y + b, z + c], p011 = [x, y + b, z + c];
        return
        [
            Face(p000, p010, p110, p100), Face(p001, p101, p111, p011), Face(p000, p100, p101, p001),
            Face(p010, p011, p111, p110), Face(p000, p001, p011, p010), Face(p100, p110, p111, p101),
        ];
    }

    private static GeometryData Solids(bool closed, params List<GeoFace>[] boxes)
    {
        var geometry = new GeometryData();
        foreach (var box in boxes)
        {
            var solid = new GeoBrepSolid { IsClosed = closed };
            foreach (var face in box) { solid.FaceIndices.Add(geometry.Faces.Count); geometry.Faces.Add(face); }
            geometry.BrepSolids ??= [];
            geometry.BrepSolids.Add(solid);
        }
        return geometry;
    }

    [Fact]
    public void OneClosedBox_IsShapeOne_WithItsVolumeHalfSurfaceLargestFaceAndDepth()
    {
        var solids = CompactSolidQuantities.Measure(Solids(true, Box(0, 0, 0, 2, 3, 6)), 1);

        Assert.Equal("one", solids.Shape);
        Assert.Equal(1, solids.Level);
        Assert.Equal(36, CompactSolidQuantities.Value(solids, "volume")!.Value, 9);
        Assert.Equal(72, solids.Area, 9);                                              // 2 * (6 + 12 + 18)
        Assert.Equal(36, CompactSolidQuantities.Value(solids, "half")!.Value, 9);
        Assert.Equal(18, CompactSolidQuantities.Value(solids, "maxface")!.Value, 9);
        Assert.Equal(6, CompactSolidQuantities.Value(solids, "prism")!.Value, 9);           // the long axis of the bar
    }

    [Fact]
    public void TwoClosedBoxes_AreShapeMany_TheirVolumesAddUp_AndTheyAreNoPrism()
    {
        var solids = CompactSolidQuantities.Measure(Solids(true, Box(0, 0, 0, 1, 1, 1), Box(5, 0, 0, 1, 2, 3)), 2);

        Assert.Equal("many", solids.Shape);
        Assert.Equal(7, CompactSolidQuantities.Value(solids, "volume")!.Value, 9);
        Assert.Null(CompactSolidQuantities.Value(solids, "prism"));
    }

    [Fact]
    public void OpenShell_AndACurvedFace_AreShapeOther()
    {
        Assert.Equal("other", CompactSolidQuantities.Measure(Solids(false, Box(0, 0, 0, 1, 1, 1)), 2).Shape);
        var curved = Solids(true, Box(0, 0, 0, 1, 1, 1));
        curved.Faces[0].PlaneSurface = false;
        Assert.Equal("other", CompactSolidQuantities.Measure(curved, 2).Shape);
    }

    [Fact]
    public void NoSolid_IsShapeNone()
    {
        Assert.Equal("none", CompactSolidQuantities.Measure(new GeometryData(), 2).Shape);
        Assert.Throws<ArgumentException>(() => CompactSolidQuantities.Value(CompactSolidQuantities.Measure(new GeometryData(), 2), "perimeter"));
    }

    [Theory]
    [InlineData(true, true, 0, 1)]
    [InlineData(false, true, 1, 8)]
    [InlineData(false, false, 2, 27)]
    public void ElementMeasurementChoosesTheFirstDetailLevelWithSolids(bool coarse, bool medium, int level, double volume)
    {
        static GRepData Block(List<GeoFace> box) => new()
        {
            Faces = box.Select(face => new GRepFace { Vertices = face.OuterVertices, PlaneSurface = true }).ToList(),
            Solids = [new GRepSolid { FaceIndices = [0, 1, 2, 3, 4, 5], IsClosed = true }],
        };
        var state = new ParsingState();
        state.GrepBlocks[10] = new GRepData
        {
            Coarse = coarse ? Block(Box(0, 0, 0, 1, 1, 1)) : new GRepData(),
            Medium = medium ? Block(Box(0, 0, 0, 2, 2, 2)) : new GRepData(),
        };
        var element = new Element { Id = 10, Geometry = Solids(true, Box(0, 0, 0, 3, 3, 3)) };

        var result = CompactSolidQuantities.Measure(element, state);

        Assert.Equal(level, result.Level);
        Assert.Equal(volume, result.Volume, 9);
    }

    private const string Lines = "V\tFamilyInstance\t-1012806\t-2001330\tsolid\tvolume\t120\n"
        + "V\tFamilyInstance\t-1012806\t-2001330\tnone\tempty\t9\n"
        + "V\tFamilyInstance\t-1012805\t-2000170\tone\tmaxface\t40\n"
        + "V\tFamilyInstance\t-1012805\t-2000170\tany\tempty\t3\n";

    [Fact]
    public void SolidRule_IsFoundByCategoryAndShape_AndTheWiderShapesAreTriedAfterTheExactOne()
    {
        var gate = CompactValueGate.Parse(Lines);

        Assert.Equal("volume", gate.SolidRule("FamilyInstance", -1012806, -2001330, "one"));
        Assert.Equal("volume", gate.SolidRule("FamilyInstance", -1012806, -2001330, "many"));
        Assert.Equal("empty", gate.SolidRule("FamilyInstance", -1012806, -2001330, "none"));
        Assert.Null(gate.SolidRule("FamilyInstance", -1012806, -2001330, "other"));
        Assert.Equal("maxface", gate.SolidRule("FamilyInstance", -1012805, -2000170, "one"));
        Assert.Equal("empty", gate.SolidRule("FamilyInstance", -1012805, -2000170, "other"));    // any
        Assert.Equal("empty", gate.SolidRule("FamilyInstance", -1012805, -2000170, "none"));
        Assert.Null(gate.SolidRule("FamilyInstance", -1012806, -2000170, "one"));
        Assert.Null(gate.SolidRule("Wall", -1012806, -2001330, "one"));
        Assert.True(gate.HasSolidRules(-2001330));
        Assert.False(gate.HasSolidRules(-2000011));
    }

    [Theory]
    [InlineData("V\tFamilyInstance\t-1012806\t-2001330\tsolid\n")]
    [InlineData("V\tFamilyInstance\t-1012806\tfloors\tsolid\tvolume\n")]
    [InlineData("V\tFamilyInstance\t-1012806\t-2001330\tcurved\tvolume\n")]
    [InlineData("V\tFamilyInstance\t-1012806\t-2001330\tsolid\tperimeter\n")]
    public void MalformedSolidLine_IsRefused(string text) => Assert.Throws<InvalidDataException>(() => CompactValueGate.Parse(text));

    [Fact]
    public void ShippedGate_HasSolidRules_ForTheCurtainWallMembers()
    {
        var gate = CompactValueGate.ForLayout(wide: true);

        Assert.Equal("volume", gate.SolidRule("FamilyInstance", -1012806, -2000171, "one"));
        Assert.Equal("half", gate.SolidRule("FamilyInstance", -1012805, -2000171, "many"));
        Assert.Null(gate.SolidRule("FamilyInstance", -1012805, -2000170, "one"));    // the area of a panel is the largest face in most files, not in all
        Assert.Null(gate.SolidRule("FamilyInstance", -1012806, -2000171, "other"));    // the facets of a curved face are no volume
    }

    [Fact]
    public void CurveElementLists_NameTheLengthOfTheCurve_NotTheSystemLength()
    {
        // The expected export prints the Length of a line under (Dimensions, "Length"); id -1001375 prints "System Length" since the caption was renamed
        var element = new Element
        {
            Id = 1, SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = 5, SerializedCreatedPhaseId = -1,
            NativeCellListTag = 0, NativeParameterStoreIds = [], NativeParameterStoresComplete = true,
        };
        var result = CompactParameterLists.GetBuiltinIds("CurveElem", new ElementListFacts(element, -2000051, new CompactListFileFacts(false, true, false)));

        Assert.Null(result.Unknown);
        Assert.Contains(-1004005, result.Ids!);
        Assert.DoesNotContain(-1001375, result.Ids!);
        Assert.Contains(-1004011, result.Ids!);
        Assert.Equal(result.Ids!.Order().ToArray(), result.Ids);
    }
}
