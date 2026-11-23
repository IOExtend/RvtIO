using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The plan drawing of a fixture or a piece of furniture (ElementBuilder.ApplyPlanFixtureGraphics): the lines and
/// masks stored with the family symbol are moved to the place of the one instance the element has. An element whose
/// placement cannot be applied exactly keeps no drawing, so the plan falls back to its model geometry.</summary>
public class PlanFixtureGraphicsTests
{
    private const int Symbol = 900, Id = 50;

    private static readonly double[] Move = [1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 20, 30];

    private static (Element Element, ParsingState State) Fixture(int category = (int)BuiltInCategory.OST_Furniture,
        double[]? transform = null, bool withMedium = true, bool withCoarse = true)
    {
        var state = new ParsingState();
        state.GrepBlocks[Id] = new GRepData { Instances = [new GRepInstance { SymbolElemId = Symbol, Transform = transform ?? Move }] };
        state.PlanFixtureGraphicsBySymbol[Symbol] = new PlanFixtureGraphics(
            [new([0, 0, 0], [1, 0, 0], 7)], [[[0, 0, 0], [1, 0, 0], [1, 1, 0]]],
            withMedium ? new PlanFixtureProjection([new([0, 0, 0], [0, 2, 0], 8)], [[[0, 0, 0], [0, 2, 0], [2, 2, 0]]]) : null,
            withCoarse ? new PlanFixtureProjection([new([0, 0, 0], [0, 3, 0], 9)], []) : null);
        var element = new Element { Id = Id, CategoryId = category, ValidatedOriginalFamilySymbolId = Symbol };
        return (element, state);
    }

    private static void Apply(Element element, ParsingState state) => ElementBuilder.ApplyPlanFixtureGraphics([element], state);

    [Fact]
    public void Graphics_AreMovedToThePlacementOfTheInstance()
    {
        var (element, state) = Fixture();

        Apply(element, state);

        var placed = element.PlacedPlanFixtureGraphics;
        Assert.NotNull(placed);
        var line = Assert.Single(placed.Lines);
        Assert.Equal(new double[] { 10, 20, 30 }, line.Start);
        Assert.Equal(new double[] { 11, 20, 30 }, line.End);
        Assert.Equal(7, line.StyleId);
        Assert.Equal(new double[] { 11, 21, 30 }, Assert.Single(placed.Masks)[2]);
        Assert.NotNull(placed.MediumProjection);
        Assert.Equal(new double[] { 10, 22, 30 }, Assert.Single(placed.MediumProjection.Lines).End);
        Assert.Equal(new double[] { 12, 22, 30 }, Assert.Single(placed.MediumProjection.Masks)[2]);
    }

    [Fact]
    public void Graphics_FollowARotationAndAMirroring()
    {
        // A quarter turn about z sends x to y; the mirrored placement sends x to -x. The z axis stays up in both.
        var (turned, turnedState) = Fixture(transform: [0, -1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0]);
        var (mirrored, mirroredState) = Fixture(transform: [-1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]);

        Apply(turned, turnedState);
        Apply(mirrored, mirroredState);

        Assert.Equal(new double[] { 0, 1, 0 }, Assert.Single(turned.PlacedPlanFixtureGraphics!.Lines).End);
        Assert.Equal(new double[] { -1, 0, 0 }, Assert.Single(mirrored.PlacedPlanFixtureGraphics!.Lines).End);
    }

    [Fact]
    public void CoarseProjection_IsPlacedForSiteEquipmentOnly()
    {
        var (furniture, furnitureState) = Fixture();
        var (equipment, equipmentState) = Fixture(category: (int)BuiltInCategory.OST_ElectricalEquipment);
        var (site, siteState) = Fixture(category: (int)BuiltInCategory.OST_Site);

        Apply(furniture, furnitureState);
        Apply(equipment, equipmentState);
        Apply(site, siteState);

        Assert.Null(furniture.PlacedPlanFixtureGraphics!.CoarseProjection);
        Assert.Equal(new double[] { 10, 23, 30 }, Assert.Single(equipment.PlacedPlanFixtureGraphics!.CoarseProjection!.Lines).End);
        Assert.NotNull(site.PlacedPlanFixtureGraphics!.CoarseProjection);
    }

    [Theory]
    [InlineData((int)BuiltInCategory.OST_PlumbingFixtures)]
    [InlineData((int)BuiltInCategory.OST_Furniture)]
    [InlineData((int)BuiltInCategory.OST_ElectricalEquipment)]
    [InlineData((int)BuiltInCategory.OST_Site)]
    public void Graphics_AreAppliedToTheCategoriesTheyDraw(int category)
    {
        var (element, state) = Fixture(category);

        Apply(element, state);

        Assert.NotNull(element.PlacedPlanFixtureGraphics);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("no_symbol")]
    [InlineData("other_symbol")]
    [InlineData("no_graphics")]
    [InlineData("no_placement")]
    [InlineData("two_placements")]
    [InlineData("scaled")]
    [InlineData("sheared")]
    [InlineData("flipped_up")]
    [InlineData("not_finite")]
    [InlineData("short_transform")]
    public void Graphics_OfAPlacementTheyCannotFollowAreLeftOut(string defect)
    {
        var (element, state) = Fixture();
        switch (defect)
        {
            case "category": element.CategoryId = (int)BuiltInCategory.OST_Doors; break;
            case "no_symbol": element.ValidatedOriginalFamilySymbolId = null; break;
            case "other_symbol": state.GrepBlocks[Id].Instances[0].SymbolElemId = 901; break;
            case "no_graphics": state.PlanFixtureGraphicsBySymbol.Remove(Symbol); break;
            case "no_placement": state.GrepBlocks.Remove(Id); break;
            case "two_placements": state.GrepBlocks[Id].Instances.Add(new GRepInstance { SymbolElemId = Symbol, Transform = Move }); break;
            case "scaled": state.GrepBlocks[Id].Instances[0].Transform = [2, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0]; break;
            case "sheared": state.GrepBlocks[Id].Instances[0].Transform = [1, 0.5, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]; break;
            case "flipped_up": state.GrepBlocks[Id].Instances[0].Transform = [1, 0, 0, 0, -1, 0, 0, 0, -1, 0, 0, 0]; break;
            case "not_finite": state.GrepBlocks[Id].Instances[0].Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, double.NaN, 0, 0]; break;
            case "short_transform": state.GrepBlocks[Id].Instances[0].Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1]; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        Apply(element, state);

        Assert.Null(element.PlacedPlanFixtureGraphics);
    }

    [Fact]
    public void Graphics_OfAnElementWithModelFacesNeedAProjectionOfTheDetailLevel()
    {
        var (without, withoutState) = Fixture(withMedium: false);
        var (with, withState) = Fixture();
        without.Geometry = new GeometryData { Faces = [new GeoFace()] };
        with.Geometry = new GeometryData { Faces = [new GeoFace()] };

        Apply(without, withoutState);
        Apply(with, withState);

        Assert.Null(without.PlacedPlanFixtureGraphics);
        Assert.NotNull(with.PlacedPlanFixtureGraphics);
    }

    [Fact]
    public void Graphics_OfSiteEquipmentWithModelFacesMayUseTheCoarseProjectionInstead()
    {
        var (equipment, equipmentState) = Fixture(category: (int)BuiltInCategory.OST_ElectricalEquipment, withMedium: false);
        var (bare, bareState) = Fixture(category: (int)BuiltInCategory.OST_ElectricalEquipment, withMedium: false, withCoarse: false);
        equipment.Geometry = new GeometryData { Faces = [new GeoFace()] };
        bare.Geometry = new GeometryData { Faces = [new GeoFace()] };

        Apply(equipment, equipmentState);
        Apply(bare, bareState);

        Assert.NotNull(equipment.PlacedPlanFixtureGraphics);
        Assert.Null(equipment.PlacedPlanFixtureGraphics.MediumProjection);
        Assert.Null(bare.PlacedPlanFixtureGraphics);
    }

    [Fact]
    public void Graphics_OfAnElementWithoutModelFacesNeedNoProjection()
    {
        var (element, state) = Fixture(withMedium: false, withCoarse: false);
        element.Geometry = new GeometryData();

        Apply(element, state);

        Assert.NotNull(element.PlacedPlanFixtureGraphics);
    }
}
