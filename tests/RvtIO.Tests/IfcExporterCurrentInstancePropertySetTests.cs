using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Occurrence property sets where the current profile differs from the legacy profile reference: Manufacturer and
/// flow-segment Length sets, Pset_DistributionFlowElementCommon on doors and fittings, LoadBearing of
/// walls from the Structural box and the usage, no ProvisionForVoid, and IsExternal as a plain function of the
/// type and host. The legacy profile contract stays in the original per-set test classes.</summary>
public class IfcExporterCurrentInstancePropertySetTests
{
    private const int PhaseId = 3;
    private const int AllModelManufacturerParamId = -1010108;
    private const int CurveElemLengthParamId = -1004005;

    [Theory]
    [InlineData(IfcProfile.Current, true)]
    [InlineData(IfcProfile.Legacy, false)]
    public void Door_ReceivesDistributionFlowElementCommonOnlyInCurrent(IfcProfile version, bool expected)
    {
        string ifc = Export(version, Flow(100, -2000023));
        Assert.Equal(expected, SetsOf(ifc, "IFCDOOR").Any(set => set.Name == "Pset_DistributionFlowElementCommon"));
    }

    [Theory]
    [InlineData(IfcProfile.Current, true)]
    [InlineData(IfcProfile.Legacy, false)]
    public void FlowFitting_ReceivesDistributionFlowElementCommonOnlyInCurrent(IfcProfile version, bool expected)
    {
        string ifc = Export(version, Flow(100, -2008049));
        Assert.Equal(expected, SetsOf(ifc, "IFCFLOWFITTING").Any(set => set.Name == "Pset_DistributionFlowElementCommon"));
    }

    [Fact]
    public void FlowSegment_ReceivesBothLengthSets_FromTheCurveLengthInMillimetres()
    {
        var segment = Flow(100, -2008000);
        segment.Parameters.Add(new Parameter { Id = CurveElemLengthParamId, StorageType = StorageType.Double, Value = 10.0 });
        var sets = SetsOf(Export(IfcProfile.Current, segment), "IFCFLOWSEGMENT");
        foreach (string name in new[] { "Pset_FlowSegmentDuctSegment", "Pset_FlowSegmentPipeSegment" })
            Assert.Equal("IFCPOSITIVELENGTHMEASURE(3048.)", sets.Single(set => set.Name == name).Properties["Length"]);
        Assert.DoesNotContain(SetsOf(Export(IfcProfile.Legacy, segment), "IFCFLOWSEGMENT"),
            set => set.Name.StartsWith("Pset_FlowSegment", StringComparison.Ordinal));
    }

    [Fact]
    public void FlowSegment_WithoutPositiveLength_ReceivesNoLengthSet()
    {
        var segment = Flow(100, -2008000);
        segment.Parameters.Add(new Parameter { Id = CurveElemLengthParamId, StorageType = StorageType.Double, Value = 0.0 });
        Assert.DoesNotContain(SetsOf(Export(IfcProfile.Current, segment), "IFCFLOWSEGMENT"),
            set => set.Name.StartsWith("Pset_FlowSegment", StringComparison.Ordinal));
    }

    [Fact]
    public void Manufacturer_IsWrittenForAHeldParameter_AndNotForTheParserDefault()
    {
        var held = Flow(100, -2001160, typeId: 901);
        var type = new Element { Id = 901, CategoryId = -2001160, Name = "Fixture", References = [AllModelManufacturerParamId] };
        type.Parameters.Add(Text(AllModelManufacturerParamId, "Kohler"));
        var sets = SetsOf(Export(IfcProfile.Current, type, held), "IFCFLOWTERMINAL");
        Assert.Equal("IFCLABEL('Kohler')", sets.Single(set => set.Name == "Pset_ManufacturerTypeInformation").Properties["Manufacturer"]);

        var defaulted = Flow(110, -2001160, typeId: 911);
        var plain = new Element { Id = 911, CategoryId = -2001160, Name = "Fixture" };
        plain.Parameters.Add(Text(AllModelManufacturerParamId, ""));
        Assert.DoesNotContain(SetsOf(Export(IfcProfile.Current, plain, defaulted), "IFCFLOWTERMINAL"),
            set => set.Name == "Pset_ManufacturerTypeInformation");
        Assert.DoesNotContain(SetsOf(Export(IfcProfile.Legacy, type, held), "IFCFLOWTERMINAL"),
            set => set.Name == "Pset_ManufacturerTypeInformation");
    }

    [Fact]
    public void Manufacturer_OfAPipe_ComesFromItsMepCurveType_NotFromAnInstanceValue()
    {
        var curveType = new Element { Id = 950, CategoryId = -2008000, Name = "Steel pipe" };
        curveType.Parameters.Add(Text(AllModelManufacturerParamId, "PRRC"));
        var pipe = Flow(100, -2008000);
        pipe.SerializedMepCurveTypeId = 950;
        pipe.Parameters.Add(Text(AllModelManufacturerParamId, "Instance only"));
        var sets = SetsOf(Export(IfcProfile.Current, curveType, pipe), "IFCFLOWSEGMENT");
        Assert.Equal("IFCLABEL('PRRC')", sets.Single(set => set.Name == "Pset_ManufacturerTypeInformation").Properties["Manufacturer"]);

        // A curve type without the parameter leaves the pipe without the set, whatever the instance holds.
        var bareType = new Element { Id = 951, CategoryId = -2008000, Name = "Plain pipe" };
        var other = Flow(110, -2008000);
        other.SerializedMepCurveTypeId = 951;
        other.Parameters.Add(Text(AllModelManufacturerParamId, "Instance only"));
        Assert.DoesNotContain(SetsOf(Export(IfcProfile.Current, bareType, other), "IFCFLOWSEGMENT"),
            set => set.Name == "Pset_ManufacturerTypeInformation");

        // Without a curve type link the instance and its family type are still read.
        var unlinked = Flow(120, -2008000);
        unlinked.Parameters.Add(Text(AllModelManufacturerParamId, "Kept"));
        Assert.Equal("IFCLABEL('Kept')", SetsOf(Export(IfcProfile.Current, unlinked), "IFCFLOWSEGMENT")
            .Single(set => set.Name == "Pset_ManufacturerTypeInformation").Properties["Manufacturer"]);
    }

    [Fact]
    public void Manufacturer_ReferencedButUndecoded_IsAnEmptyLabel()
    {
        var held = Flow(100, -2001160, typeId: 901);
        var type = new Element { Id = 901, CategoryId = -2001160, Name = "Fixture", References = [AllModelManufacturerParamId] };
        var sets = SetsOf(Export(IfcProfile.Current, type, held), "IFCFLOWTERMINAL");
        Assert.Equal("IFCLABEL('')", sets.Single(set => set.Name == "Pset_ManufacturerTypeInformation").Properties["Manufacturer"]);
    }

    [Fact]
    public void Manufacturer_OfAStairFlight_ComesFromItsStairComponentType_WhenElemTypeParamIsAbsent()
    {
        const int runsCategory = -2000919;
        var runType = new Element
        {
            Id = 300,
            CategoryId = runsCategory,
            Name = "Monolithic Run Type",
            ValidatedStairComponentTypeName = "Monolithic Run Type",
            References = [AllModelManufacturerParamId],
        };
        runType.Parameters.Add(Text(AllModelManufacturerParamId, "StairMaster"));

        var run = IfcExporterStairOccurrenceCommonPropertySetTests.Product(200, runType.Id, runsCategory, "OST_StairsRuns", null);

        var sets = SetsOf(Export(IfcProfile.Current, runType, run), "IFCSTAIRFLIGHT");
        Assert.Equal("IFCLABEL('StairMaster')", sets.Single(set => set.Name == "Pset_ManufacturerTypeInformation").Properties["Manufacturer"]);
    }

    [Fact]
    public void Manufacturer_OfAStairLanding_ComesFromItsStairComponentType_WhenElemTypeParamIsAbsent()
    {
        const int landingsCategory = -2000920;
        var landingType = new Element
        {
            Id = 301,
            CategoryId = landingsCategory,
            Name = "Monolithic Landing Type",
            ValidatedStairComponentTypeName = "Monolithic Landing Type",
            References = [AllModelManufacturerParamId],
        };
        landingType.Parameters.Add(Text(AllModelManufacturerParamId, "LandingMaster"));

        var landing = IfcExporterStairOccurrenceCommonPropertySetTests.Product(201, landingType.Id, landingsCategory, "OST_StairsLandings", null);

        var sets = SetsOf(Export(IfcProfile.Current, landingType, landing), "IFCSLAB");
        Assert.Equal("IFCLABEL('LandingMaster')", sets.Single(set => set.Name == "Pset_ManufacturerTypeInformation").Properties["Manufacturer"]);
    }

    [Fact]
    public void Manufacturer_OfAStairFlight_WithoutManufacturerParameter_EmitsNoManufacturerSet()
    {
        const int runsCategory = -2000919;
        var runType = new Element
        {
            Id = 300,
            CategoryId = runsCategory,
            Name = "Monolithic Run Type",
            ValidatedStairComponentTypeName = "Monolithic Run Type",
        };

        var run = IfcExporterStairOccurrenceCommonPropertySetTests.Product(200, runType.Id, runsCategory, "OST_StairsRuns", null);

        var sets = SetsOf(Export(IfcProfile.Current, runType, run), "IFCSTAIRFLIGHT");
        Assert.DoesNotContain(sets, set => set.Name == "Pset_ManufacturerTypeInformation");
    }

    [Theory]
    [InlineData(IfcProfile.Legacy, 0, null, "IFCBOOLEAN(.F.)")]
    [InlineData(IfcProfile.Current, 1, 1, "IFCBOOLEAN(.T.)")]
    [InlineData(IfcProfile.Current, 1, 0, "IFCBOOLEAN(.F.)")]
    [InlineData(IfcProfile.Current, 0, 1, "IFCBOOLEAN(.F.)")]
    [InlineData(IfcProfile.Current, 2, 1, "IFCBOOLEAN(.F.)")]
    [InlineData(IfcProfile.Current, 1, null, null)]
    public void WallLoadBearing_InCurrentNeedsTheStructuralBoxAndBearingUsage(
        IfcProfile version, int structuralUsage, int? structuralBox, string? expected)
    {
        var wall = Wall(110, structuralUsage);
        if (structuralBox is int box)
            wall.Parameters.Add(new Parameter { Id = -1001596, Definition = new Definition { Id = -1001596, Name = "WALL_STRUCTURAL_SIGNIFICANT" },
                StorageType = StorageType.Integer, Value = box });
        var sets = SetsOf(Export(version, Level(), wall), "IFCWALLSTANDARDCASE");
        Assert.Equal(expected, sets.Single(set => set.Name == "Pset_WallCommon").Properties.GetValueOrDefault("LoadBearing"));
    }

    [Theory]
    [InlineData("LoadBearing", 1, 0, 0, "IFCBOOLEAN(.T.)")]
    [InlineData("LoadBearing", 1, 0, null, "IFCBOOLEAN(.T.)")]
    [InlineData("LoadBearing", 0, 1, 1, "IFCBOOLEAN(.F.)")]
    [InlineData("Pset_WallCommon.LoadBearing", 1, 0, 0, "IFCBOOLEAN(.T.)")]
    [InlineData("Pset_WallCommon.LoadBearing", 0, 1, 1, "IFCBOOLEAN(.F.)")]
    public void WallLoadBearing_InCurrentANamedParameterWinsOverTheStructuralFlags(
        string name, int named, int structuralUsage, int? structuralBox, string expected)
    {
        var wall = Wall(110, structuralUsage);
        if (structuralBox is int box)
            wall.Parameters.Add(new Parameter { Id = -1001596, Definition = new Definition { Id = -1001596, Name = "WALL_STRUCTURAL_SIGNIFICANT" },
                StorageType = StorageType.Integer, Value = box });
        wall.Parameters.Add(new Parameter { Id = 900001, Definition = new Definition { Id = 900001, Name = name },
            StorageType = StorageType.Integer, Value = named });
        var sets = SetsOf(Export(IfcProfile.Current, Level(), wall), "IFCWALLSTANDARDCASE");
        Assert.Equal(expected, sets.Single(set => set.Name == "Pset_WallCommon").Properties["LoadBearing"]);
    }

    [Fact]
    public void WallLoadBearing_InCurrentTheSetQualifiedNameWinsOverThePlainName()
    {
        var wall = Wall(110, 1);
        wall.Parameters.Add(new Parameter { Id = 900001, Definition = new Definition { Id = 900001, Name = "LoadBearing" },
            StorageType = StorageType.Integer, Value = 1 });
        wall.Parameters.Add(new Parameter { Id = 900002, Definition = new Definition { Id = 900002, Name = "Pset_WallCommon.LoadBearing" },
            StorageType = StorageType.Integer, Value = 0 });
        var sets = SetsOf(Export(IfcProfile.Current, Level(), wall), "IFCWALLSTANDARDCASE");
        Assert.Equal("IFCBOOLEAN(.F.)", sets.Single(set => set.Name == "Pset_WallCommon").Properties["LoadBearing"]);
    }

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void Column_ReceivesBarPitchOfColumnInBothProfiles(IfcProfile version)
    {
        var column = Flow(100, -2001330);
        string ifc = Export(version, column);
        Assert.Contains(SetsOf(ifc, "IFCCOLUMN"), set => set.Name == "Pset_ReinforcementBarPitchOfColumn");
    }

    [Fact]
    public void ProvisionForVoid_IsNotWrittenInCurrent()
    {
        Assert.DoesNotContain("Pset_ProvisionForVoid", Export(IfcProfile.Current, Flow(100, -2000151)));
    }

    [Fact]
    public void IsExternal_DependsOnTheTypeFunctionThenTheCategoryThenTheHost()
    {
        var exteriorWallType = new Element { Id = 10, CategoryId = -2000011, SerializedBasicWallTypeFunction = 1 };
        var interiorWallType = new Element { Id = 11, CategoryId = -2000011, SerializedBasicWallTypeFunction = 0 };
        var exteriorWall = Typed(20, -2000011, 10);
        var interiorWall = Typed(21, -2000011, 11);
        var roof = new Element { Id = 22, CategoryId = -2000035 };
        var windowInExterior = new Element { Id = 30, CategoryId = -2000014, SerializedHostId = 20 };
        var windowInInterior = new Element { Id = 31, CategoryId = -2000014, SerializedHostId = 21 };
        var windowInRoof = new Element { Id = 32, CategoryId = -2000014, SerializedHostId = 22 };
        var loose = new Element { Id = 33, CategoryId = -2000014 };
        var cycleA = new Element { Id = 40, CategoryId = -2000014, SerializedHostId = 41 };
        var cycleB = new Element { Id = 41, CategoryId = -2000014, SerializedHostId = 40 };
        var byId = new[] { exteriorWallType, interiorWallType, exteriorWall, interiorWall, roof, windowInExterior,
            windowInInterior, windowInRoof, loose, cycleA, cycleB }.ToDictionary(element => element.Id);
        Assert.True(IfcExporter.PureIsElementExternalCurrent(exteriorWall, byId));
        Assert.False(IfcExporter.PureIsElementExternalCurrent(interiorWall, byId));
        Assert.True(IfcExporter.PureIsElementExternalCurrent(roof, byId));
        Assert.True(IfcExporter.PureIsElementExternalCurrent(windowInExterior, byId));
        Assert.False(IfcExporter.PureIsElementExternalCurrent(windowInInterior, byId));
        Assert.True(IfcExporter.PureIsElementExternalCurrent(windowInRoof, byId));
        Assert.False(IfcExporter.PureIsElementExternalCurrent(loose, byId));
        Assert.False(IfcExporter.PureIsElementExternalCurrent(cycleA, byId));
    }

    [Fact]
    public void IsExternal_OfAFunctionlessTypeIgnoresTheHostsOwnTypeCache()
    {
        // Every component of a curtain wall reads its host again; the legacy profile caches the first answer per type.
        var curtainType = new Element { Id = 10, CategoryId = -2000011, SerializedCurtainWallFunction = 1 };
        var curtain = Typed(20, -2000011, 10);
        var mullionType = new Element { Id = 12, CategoryId = -2000171 };
        var mullions = Enumerable.Range(0, 3).Select(index =>
        {
            var mullion = Typed(30 + index, -2000171, 12);
            mullion.SerializedHostId = 20;
            return mullion;
        }).ToArray();
        var byId = new[] { curtainType, curtain, mullionType }.Concat(mullions).ToDictionary(element => element.Id);
        var values = IfcExporter.ResolveOccurrenceIsExternalValuesCurrent(byId, mullions.Select(m => m.Id).ToList());
        Assert.All(values.Values, Assert.True);
        Assert.Equal(3, values.Count);
    }

    [Fact]
    public void WindowThermalTransmittance_ComesFromTheStoredCoefficientOfTheType()
    {
        var type = new Element { Id = 9, CategoryId = -2000014 };
        type.Parameters.Add(new Parameter { Id = -1005430, StorageType = StorageType.Double, Value = 3.6886 });
        var window = new Element { Id = 1, CategoryId = -2000014 };
        Assert.Equal(3.6886, IfcExporter.ResolveOpeningThermalTransmittanceCurrent(window, type));
        type.Parameters[0].IsApproximate = true;
        Assert.Null(IfcExporter.ResolveOpeningThermalTransmittanceCurrent(window, type));
        type.Parameters[0].IsApproximate = false;
        type.Parameters[0].Value = 0.0;
        Assert.Null(IfcExporter.ResolveOpeningThermalTransmittanceCurrent(window, type));
        var wall = new Element { Id = 2, CategoryId = -2000011 };
        type.Parameters[0].Value = 1.0;
        Assert.Null(IfcExporter.ResolveOpeningThermalTransmittanceCurrent(wall, type));
    }

    [Fact]
    public void CoveringTotalThickness_ComesFromTheCeilingTypeLayers()
    {
        var type = new Element { Id = 9, CategoryId = -2000038, ValidatedCeilingTypeThicknessFeet = 0.5 };
        var ceiling = new Element { Id = 1, CategoryId = -2000038 };
        Assert.Equal(152.4, IfcExporter.ResolveCoveringTotalThicknessMillimetresCurrent(ceiling, type));
        Assert.Null(IfcExporter.ResolveCoveringTotalThicknessMillimetresCurrent(ceiling, null));
        type.ValidatedCeilingTypeThicknessFeet = 0;
        Assert.Null(IfcExporter.ResolveCoveringTotalThicknessMillimetresCurrent(ceiling, type));
    }

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void RoofArea_IsProjectedAreaInBothProfiles(IfcProfile version)
    {
        // Revit's exports write the stored host area as ProjectedArea; TotalArea is the live sloped area.
        var roof = Flow(100, -2000035);
        roof.Parameters.Add(new Parameter { Id = -1012805, StorageType = StorageType.Double, Value = 100.0 });
        var set = SetsOf(Export(version, roof), "IFCROOF").Single(candidate => candidate.Name == "Pset_RoofCommon");
        Assert.Contains("ProjectedArea", set.Properties.Keys);
        Assert.DoesNotContain("TotalArea", set.Properties.Keys);
    }

    [Fact]
    public void FireRating_HeldByTheWallType_ReachesTheCommonSetAndConcreteElementGeneral()
    {
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall", References = [-1001206] };
        type.Parameters.Add(Text(-1001206, "EI 60"));
        var sets = SetsOf(Export(IfcProfile.Current, Level(), type, TypedWall(110, 500)), "IFCWALLSTANDARDCASE");
        Assert.Equal("IFCLABEL('EI 60')", sets.Single(set => set.Name == "Pset_WallCommon").Properties["FireRating"]);
        Assert.Equal("IFCLABEL('EI 60')", sets.Single(set => set.Name == "Pset_ConcreteElementGeneral").Properties["FireRating"]);
    }

    [Fact]
    public void FireRating_HeldButEmpty_IsAnEmptyLabel_AndTheParserDefaultWritesNothing()
    {
        var held = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall", References = [-1001206] };
        var sets = SetsOf(Export(IfcProfile.Current, Level(), held, TypedWall(110, 500)), "IFCWALLSTANDARDCASE");
        Assert.Equal("IFCLABEL('')", sets.Single(set => set.Name == "Pset_WallCommon").Properties["FireRating"]);

        var defaulted = new Element { Id = 501, CategoryId = -2000011, Name = "Basic wall" };
        defaulted.Parameters.Add(Text(-1001206, ""));
        sets = SetsOf(Export(IfcProfile.Current, Level(), defaulted, TypedWall(111, 501)), "IFCWALLSTANDARDCASE");
        Assert.DoesNotContain("FireRating", sets.Single(set => set.Name == "Pset_WallCommon").Properties.Keys);
        Assert.DoesNotContain(sets, set => set.Name == "Pset_ConcreteElementGeneral");
    }

    [Fact]
    public void FireRating_Is_AbsentInLegacy()
    {
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall", References = [-1001206] };
        type.Parameters.Add(Text(-1001206, "EI 60"));
        var sets = SetsOf(Export(IfcProfile.Legacy, Level(), type, TypedWall(110, 500)), "IFCWALLSTANDARDCASE");
        Assert.DoesNotContain(sets, set => set.Name == "Pset_ConcreteElementGeneral");
        Assert.DoesNotContain("FireRating", sets.Single(set => set.Name == "Pset_WallCommon").Properties.Keys);
    }

    [Fact]
    public void FireRating_StoredByTheTypeInItsValueSets_IsAnEmptyLabelEvenWithoutAReference()
    {
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall", NativeStoredParameterIds = [-1001206] };
        type.Parameters.Add(Text(-1001206, ""));
        var sets = SetsOf(Export(IfcProfile.Current, Level(), type, TypedWall(110, 500)), "IFCWALLSTANDARDCASE");
        Assert.Equal("IFCLABEL('')", sets.Single(set => set.Name == "Pset_WallCommon").Properties["FireRating"]);
        Assert.Equal("IFCLABEL('')", sets.Single(set => set.Name == "Pset_ConcreteElementGeneral").Properties["FireRating"]);

        // Another parameter stored by the type holds nothing for FireRating.
        var other = new Element { Id = 501, CategoryId = -2000011, Name = "Basic wall", NativeStoredParameterIds = [-1140422] };
        other.Parameters.Add(Text(-1001206, ""));
        sets = SetsOf(Export(IfcProfile.Current, Level(), other, TypedWall(111, 501)), "IFCWALLSTANDARDCASE");
        Assert.DoesNotContain(sets, set => set.Name == "Pset_ConcreteElementGeneral");
    }

    [Fact]
    public void Manufacturer_StoredEmptyByTheInstance_IsAnEmptyLabel()
    {
        var held = Flow(100, -2001160, typeId: 901);
        held.NativeStoredParameterIds = [AllModelManufacturerParamId];
        var type = new Element { Id = 901, CategoryId = -2001160, Name = "Fixture" };
        var sets = SetsOf(Export(IfcProfile.Current, type, held), "IFCFLOWTERMINAL");
        Assert.Equal("IFCLABEL('')", sets.Single(set => set.Name == "Pset_ManufacturerTypeInformation").Properties["Manufacturer"]);

        var untouched = Flow(110, -2001160, typeId: 911);
        var plain = new Element { Id = 911, CategoryId = -2001160, Name = "Fixture" };
        Assert.DoesNotContain(SetsOf(Export(IfcProfile.Current, plain, untouched), "IFCFLOWTERMINAL"),
            set => set.Name == "Pset_ManufacturerTypeInformation");
    }

    [Theory]
    [InlineData(1, "IFCBOOLEAN(.T.)")]
    [InlineData(0, "IFCBOOLEAN(.F.)")]
    public void DoorIsExternal_FollowsTheFunctionStoredBySymbol_AheadOfOtherDecodedParameters(int function, string expected)
    {
        var door = Flow(100, -2000023, typeId: 901);
        var type = new Element { Id = 901, CategoryId = -2000023, Name = "Door type", SerializedFamilySymbolFunction = function };
        // A parameter other decoders derived for the same symbol must not outvote the stored record.
        type.Parameters.Add(new Parameter { Id = -1001006, StorageType = StorageType.Integer, Value = 1 - function, IsApproximate = true });
        var set = SetsOf(Export(IfcProfile.Current, type, door), "IFCDOOR").Single(candidate => candidate.Name == "Pset_DoorCommon");
        Assert.Equal(expected, set.Properties["IsExternal"]);
    }

    [Fact]
    public void DoorIsExternal_WithoutAStoredSymbolFunction_StaysBlockedByAnApproximateParameter()
    {
        var door = Flow(100, -2000023, typeId: 901);
        var type = new Element { Id = 901, CategoryId = -2000023, Name = "Door type" };
        type.Parameters.Add(new Parameter { Id = -1001006, StorageType = StorageType.Integer, Value = 1, IsApproximate = true });
        var set = SetsOf(Export(IfcProfile.Current, type, door), "IFCDOOR").Single(candidate => candidate.Name == "Pset_DoorCommon");
        Assert.Equal("IFCBOOLEAN(.F.)", set.Properties["IsExternal"]);
    }

    [Fact]
    public void Beam_ReceivesIsExternalInCurrent_FromTheSymbolFunction()
    {
        var beam = Flow(100, -2001320, typeId: 901);
        var type = new Element { Id = 901, CategoryId = -2001320, Name = "Beam type" };
        string Common(IfcProfile version) => SetsOf(Export(version, type, beam), "IFCBEAM")
            .Single(set => set.Name == "Pset_BeamCommon").Properties.GetValueOrDefault("IsExternal", "");
        Assert.Equal("IFCBOOLEAN(.F.)", Common(IfcProfile.Current));
        type.SerializedFamilySymbolFunction = 1;
        Assert.Equal("IFCBOOLEAN(.T.)", Common(IfcProfile.Current));
        Assert.Equal("", Common(IfcProfile.Legacy));
    }

    [Theory]
    [InlineData(IfcProfile.Current, null, "IFCBOOLEAN(.F.)")]
    [InlineData(IfcProfile.Current, true, "IFCBOOLEAN(.T.)")]
    [InlineData(IfcProfile.Current, false, "IFCBOOLEAN(.F.)")]
    [InlineData(IfcProfile.Legacy, null, null)]
    [InlineData(IfcProfile.Legacy, true, "IFCBOOLEAN(.T.)")]
    public void WallExtendToStructure_IsWrittenForEveryWallInCurrent(IfcProfile version, bool? joinedToTop, string? expected)
    {
        var wall = Wall(110, 0);
        wall.SerializedWallJoinedToTop = joinedToTop;
        var set = SetsOf(Export(version, Level(), wall), "IFCWALLSTANDARDCASE").Single(candidate => candidate.Name == "Pset_WallCommon");
        Assert.Equal(expected, set.Properties.GetValueOrDefault("ExtendToStructure"));
    }

    private static Element TypedWall(int id, int typeId)
    {
        var wall = Wall(id, 0);
        wall.Parameters.Add(new Parameter
        {
            Id = -1002050, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
            StorageType = StorageType.ElementId, Value = typeId,
        });
        return wall;
    }


    private sealed record Pset(string Name, Dictionary<string, string> Properties);

    /// <summary>An occurrence whose ELEM_TYPE_PARAM names the type.</summary>
    private static Element Typed(int id, int categoryId, int typeId) => new()
    {
        Id = id,
        CategoryId = categoryId,
        Parameters =
        [
            new Parameter
            {
                Id = -1002050, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId, Value = typeId,
            },
        ],
    };

    private static Parameter Text(int id, string value) =>
        new() { Id = id, StorageType = StorageType.String, Value = value };

    private static Element Flow(int id, int categoryId, int typeId = 0)
    {
        var element = new Element
        {
            Id = id,
            TypeId = typeId > 0 ? typeId : id + 1,
            IsDiscovered = true,
            SuperInstanceId = 999,
            CategoryId = categoryId,
            Category = "OST_Test",
            Name = "Type name",
            References = [PhaseId],
            Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = PhaseId }],
            BoundingBox = [0, 0, 0, 4, 3, 2],
            Geometry = SolidPrism(),
        };
        if (typeId > 0)
            element.Parameters.Add(new Parameter
            {
                Id = -1002050, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId, Value = typeId,
            });
        return element;
    }

    private static Element Level() => new()
    {
        Id = 1, TypeId = 1001, CategoryId = -2000240, Category = "OST_Levels", Name = "Ground", IsNativeLevel = true,
        SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter { Id = -1007102, StorageType = StorageType.Double, Value = 0.0 },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
        ],
    };

    private static Element Wall(int id, int structuralUsage) => new()
    {
        Id = id, TypeId = id + 1, CategoryId = -2000011, Category = "OST_Walls", Name = "Wall type",
        References = [PhaseId],
        Parameters =
        [
            new Parameter { Id = -1012100, StorageType = StorageType.Integer, Value = PhaseId },
            new Parameter { Id = -1001952, Definition = new Definition { Name = "LEVEL_PARAM" },
                StorageType = StorageType.ElementId, Value = 1 },
            new Parameter { Id = -1001000, Definition = new Definition { Name = "WALL_ATTR_WIDTH_PARAM" },
                StorageType = StorageType.Double, Value = 1.0 },
            new Parameter { Id = -1001119, Definition = new Definition { Id = -1001119, Name = "WALL_STRUCTURAL_USAGE_PARAM" },
                StorageType = StorageType.Integer, Value = structuralUsage },
        ],
        Location = new LocationCurve { CurveType = "Line", StartPoint = [0, 0, 0], EndPoint = [4, 0, 0] },
        BoundingBox = [0, -0.5, 0, 4, 0.5, 3],
    };

    private static GeometryData SolidPrism()
    {
        double[][] points =
        [
            [0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0],
            [0, 0, 2], [4, 0, 2], [4, 3, 2], [0, 3, 2],
        ];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new GeometryData
        {
            BoundingBox = [0, 0, 0, 4, 3, 2],
            Faces = faces.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => points[index]).ToList(),
            }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    private static string Export(IfcProfile version, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-currentProfile-instances-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>The property sets attached by IfcRelDefinesByProperties to the sole product of the class.</summary>
    private static List<Pset> SetsOf(string ifc, string productClass)
    {
        int product = int.Parse(Assert.Single(Regex.Matches(ifc, $@"#(\d+)={productClass}\(")).Groups[1].Value);
        var sets = new List<Pset>();
        foreach (Match rel in Regex.Matches(ifc, $@"IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,(?:\$|''),(?:\$|''),\(#{product}\),#(\d+)\);"))
        {
            int psetId = int.Parse(rel.Groups[1].Value);
            var setMatches = Regex.Matches(ifc, $@"#{psetId}=IFCPROPERTYSET\('[^']*',#\d+,'([^']*)',(?:\$|''),\(([^)]*)\)\);");
            if (setMatches.Count == 0) continue; // a quantity set attaches through the same relation
            Match pset = Assert.Single(setMatches);
            var properties = new Dictionary<string, string>();
            foreach (Match property in Regex.Matches(pset.Groups[2].Value, @"#(\d+)"))
            {
                Match single = Assert.Single(Regex.Matches(ifc,
                    $@"#{property.Groups[1].Value}=IFCPROPERTYSINGLEVALUE\('([^']*)',(?:\$|''),(.*),\$\);"));
                properties[single.Groups[1].Value] = single.Groups[2].Value;
            }
            sets.Add(new Pset(pset.Groups[1].Value, properties));
        }
        return sets;
    }
}
