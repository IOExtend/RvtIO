using System.Text.Json;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class IfcNumericUnitPlanTests
{
    private const string Prefix = "autodesk.unit.unit:";
    private const string Length = "autodesk.spec.aec:length-1.0.0";
    private static readonly UnitSchemaEvaluator.UnitConversions Sdk = UnitSchemaEvaluator.EvaluateDisplay(
        new(0,0,[]),out _) ?? throw new InvalidDataException("SDK numeric display unavailable");
    private static TfBlockDeserializer.UnitFormatOption Option(string unit = "millimeters-1.0.1",
        string spec = Length, byte useDefault = 0)
        => new(spec,"",Prefix+unit,1,0,0,0,0,useDefault,0,0);
    private static TfBlockDeserializer.UnitOptions Options(params TfBlockDeserializer.UnitFormatOption[] options)
        => new(options,1,0,0,3);
    private static IfcNumericUnitPlan Plan(TfBlockDeserializer.UnitOptions options,
        UnitSchemaEvaluator.UnitConversions? conversions = null)
        => IfcNumericUnitPlan.Create(options,conversions ?? Sdk,out string? failure)
            ?? throw new InvalidDataException(failure);
    private static ulong Bits(double value) => BitConverter.DoubleToUInt64Bits(value);

    [Theory]
    [InlineData("meters-1.0.0",null,null)]
    [InlineData("metersCentimeters-1.0.0",null,null)]
    [InlineData("centimeters-1.0.1","CENTI",null)]
    [InlineData("millimeters-1.0.1","MILLI",null)]
    [InlineData("feet-1.0.1",null,"FOOT")]
    [InlineData("feetFractionalInches-1.0.0",null,"FOOT")]
    [InlineData("inches-1.0.1",null,"INCH")]
    [InlineData("fractionalInches-1.0.0",null,"INCH")]
    public void OriginalEightLengthBranchesUseTheStoredUnitAndFromOne(string unit,string? prefix,string? name)
    {
        var plan=Plan(Options(Option(unit)));
        Assert.Equal(Prefix+unit,plan.LengthOptionUnit);Assert.Equal(Prefix+unit,plan.Length.RequestedUnitId);
        Assert.Equal(prefix,plan.Length.SiPrefix);Assert.Equal(name,plan.Length.ConversionName);
        Assert.Equal("LENGTHUNIT",plan.Length.IfcUnitType);
        Assert.True(Sdk.TryFromInternal(1,Prefix+unit,out double expected));
        Assert.Equal(Bits(expected),Bits(plan.Length.Scale));Assert.Equal(0ul,Bits(plan.Length.Offset));
        if (name==null) Assert.Null(plan.Length.MetresPerUnit);
        else
        {
            Assert.True(Sdk.TryFromInternal(1,Prefix+"meters-1.0.0",out double metres));
            Assert.Equal(Bits(metres/expected),Bits(plan.Length.MetresPerUnit!.Value));
        }
    }

    [Theory]
    [InlineData("unsupported-1.0.0")]
    [InlineData("stationingMeters-1.0.0")]
    [InlineData("Meters-1.0.0")]
    [InlineData("unregistered-per-second-1.0.0")]
    public void SourceUnknownUnitBranchUsesFeetNotMetricOrStoredNumericInfo(string unit)
    {
        var plan=Plan(Options(Option(unit)));
        Assert.Equal(Prefix+unit,plan.LengthOptionUnit);
        Assert.Equal(Prefix+"feet-1.0.1",plan.Length.RequestedUnitId);
        Assert.Equal("FOOT",plan.Length.ConversionName);Assert.Null(plan.Length.SiPrefix);
        Assert.Equal(1.0,plan.Length.Scale);
    }

    [Fact]
    public void NameLookupIgnoresVersionAtFirstDashButNotCaseOrPrefix()
    {
        var plan=Plan(Options(Option("millimeters-99.7.0", "autodesk.spec.aec:length-2.0.0")));
        Assert.Equal("autodesk.spec.aec:length-2.0.0",plan.LengthOptionSpec);
        Assert.Equal(Prefix+"millimeters-99.7.0",plan.Length.RequestedUnitId);
        Assert.Equal(Prefix+"millimeters-1.0.1",plan.Length.ResolvedUnitId);
        Assert.Equal(0x40730CCCCCCCCCCDul,Bits(plan.Length.Scale));
        Assert.Null(IfcNumericUnitPlan.Create(Options(Option(spec:"autodesk.spec.aec:Length-1.0.0")),Sdk,out _));
        Assert.Null(IfcNumericUnitPlan.Create(Options(Option(spec:"length-1.0.0")),Sdk,out _));
    }

    [Fact]
    public void UnitBranchAlsoUsesTheFirstDashNotTheLastVersionDash()
    {
        var plan=Plan(Options(Option("meters-per-second-1.0.0")));
        Assert.Null(plan.Length.ConversionName);
        Assert.Equal(Prefix+"meters-1.0.0",plan.Length.ResolvedUnitId);
        Assert.Equal(0x3FD381D7DBF487FDul,Bits(plan.Length.Scale));
    }

    [Fact]
    public void MissingOrAmbiguousOwnedLengthDoesNotInventLibraryDefault()
    {
        Assert.Null(IfcNumericUnitPlan.Create(Options(),Sdk,out string? missing));Assert.Contains("fallback",missing);
        Assert.Null(IfcNumericUnitPlan.Create(Options(Option(),Option(spec:"autodesk.spec.aec:length-2.0.0")),Sdk,out string? ambiguous));
        Assert.Contains("ambiguous",ambiguous);
        Assert.Null(IfcNumericUnitPlan.Create(Options(Option(),Option()),Sdk,out _));
        Assert.Null(IfcNumericUnitPlan.Create(null,Sdk,out _));
        Assert.Null(IfcNumericUnitPlan.Create(Options(Option()),null,out _));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(255)]
    public void DefaultGetterRejectsBeforeUnknownUnitFootFallback(byte value)
    {
        Assert.Null(IfcNumericUnitPlan.Create(Options(Option("unsupported-1.0.0",useDefault:value)),Sdk,out string? failure));
        Assert.Contains("getter",failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("autodesk.unit.unit:millimeters\0-1.0.1")]
    public void InvalidUnitIdsRejectTheBoundedPlan(string unit)
    {
        Assert.Null(IfcNumericUnitPlan.Create(Options(Option() with { Unit=unit }),Sdk,out _));
    }

    [Fact]
    public void InvalidSpecAndUnrelatedSpecCannotSupplyLength()
    {
        Assert.Null(IfcNumericUnitPlan.Create(Options(Option(spec:"autodesk.spec.aec:length-1.0.0\0")),Sdk,out _));
        Assert.Null(IfcNumericUnitPlan.Create(Options(Option(spec:"autodesk.spec.aec:reinforcementLength-1.0.0")),Sdk,out _));
    }

    [Theory]
    [InlineData("millimeters-1.0.1","millimeters","Length scale")]
    [InlineData("meters-1.0.0","meters","Length scale")]
    [InlineData("feet-1.0.1","meters","Metre base")]
    [InlineData("millimeters-1.0.1","wattsPerSquareMeterKelvin","Thermal scale")]
    public void MissingNumericInfoRejectsInsteadOfUsingAConstant(string unit,string removed,string reason)
    {
        var available=new UnitSchemaEvaluator.UnitConversions(Sdk.NormalByName
            .Where(x => x.Key!=Prefix+removed).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal),new());
        Assert.Null(IfcNumericUnitPlan.Create(Options(Option(unit)),available,out string? failure));
        Assert.Contains(reason,failure);
    }

    [Theory]
    [InlineData(0.0)] [InlineData(-1.0)] [InlineData(double.NaN)]
    public void InvalidOrNonpositiveCacheScaleRejectsTheBoundedPlan(double internalScale)
    {
        var normal=Sdk.NormalByName.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
        var old=normal[Prefix+"millimeters"];
        normal[Prefix+"millimeters"]=old with { Core=old.Core with { InternalScale=internalScale } };
        var available=new UnitSchemaEvaluator.UnitConversions(normal,new());
        Assert.Null(IfcNumericUnitPlan.Create(Options(Option()),available,out string? failure));
        Assert.Contains("Length scale",failure);
    }

    [Fact]
    public void ThermalAlwaysUsesWattsPerSquareMetreKelvinNotStoredThermalOption()
    {
        var option=Option("btuPerHourSquareFootFahrenheit-1.0.1",IfcNumericUnitPlan.ThermalSpecName+"-1.0.0",1);
        var plan=Plan(Options(Option(),option));
        Assert.Equal(Prefix+"wattsPerSquareMeterKelvin-1.0.1",plan.ThermalTransmittance.RequestedUnitId);
        Assert.Equal("THERMALTRANSMITTANCEUNIT",plan.ThermalTransmittance.IfcUnitType);
        Assert.Equal(1.0,plan.ThermalTransmittance.Scale);Assert.Equal(0ul,Bits(plan.ThermalTransmittance.Offset));
        Assert.Equal(plan.ThermalTransmittance,Plan(Options(Option())).ThermalTransmittance);
    }

    [Fact]
    public void DisplayAccuracyRoundingSymbolFlagsAndUnitSystemDoNotRoundIfcValues()
    {
        var baseline=Plan(Options(Option()));
        var changed=Options(Option() with { Accuracy=0.01,RoundingMethod=2,Symbol="arbitrary",SuppressLeadingZeros=1,
            SuppressSpaces=1,SuppressTrailingZeros=1,UseGrouping=1,UsePlusPrefix=1 }) with
            { UniversalUnitSystem=123,DecimalSymbol=9,DigitGroupingSymbol=4,DigitGroupingAmount=8 };
        Assert.Equal(baseline,Plan(changed));
    }

    [Fact]
    public void CacheMultiplyIsNotDirectConversionAndZeroOffsetNormalizesNegativeZero()
    {
        var unit=Plan(Options(Option())).Length;
        double input=BitConverter.UInt64BitsToDouble(0x3FA5D657595D6575ul);
        Assert.True(unit.TryScale(input,out double cached));Assert.Equal(0x402A000000000000ul,Bits(cached));
        Assert.True(Sdk.TryFromInternal(input,unit.RequestedUnitId,out double direct));
        Assert.Equal(0x4029FFFFFFFFFFFFul,Bits(direct));
        Assert.True(unit.TryScale(BitConverter.UInt64BitsToDouble(0x8000000000000000ul),out double zero));
        Assert.Equal(0ul,Bits(zero));
        Assert.True(unit.TryUnscale(cached,out double unscaled));Assert.Equal(Bits(cached/unit.Scale),Bits(unscaled));
    }

    [Fact]
    public void CacheOffsetUsesAddAfterMultiplyAndSubtractBeforeDivide()
    {
        var unit=new IfcNumericUnitPlan.CachedUnit("test","test","test",null,null,null,2,-273.15);
        Assert.True(unit.TryScale(10,out double value));Assert.Equal(Bits(10*2-273.15),Bits(value));
        Assert.True(unit.TryUnscale(value,out double back));Assert.Equal(Bits((value+273.15)/2),Bits(back));
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void NonfiniteNumericInputsAreRejectedNotSerialized(double value)
    {
        var unit=Plan(Options(Option())).Length;
        Assert.False(unit.TryScale(value,out double scale));Assert.Equal(0.0,scale);
        Assert.False(unit.TryUnscale(value,out double unscale));Assert.Equal(0.0,unscale);
    }

    [Fact]
    public void OverflowRejectsWithoutChangingSuccessfulUnderflowOrFiniteNegativeValues()
    {
        var unit=Plan(Options(Option())).Length;
        Assert.False(unit.TryScale(double.MaxValue,out _));
        Assert.True(unit.TryUnscale(double.Epsilon,out double small));Assert.Equal(0.0,small);
        Assert.True(unit.TryScale(-3.25,out double negative));Assert.Equal(Bits(-3.25*unit.Scale),Bits(negative));
        var tiny=unit with { Scale=double.Epsilon };Assert.False(tiny.TryUnscale(double.MaxValue,out _));
    }

    [Fact]
    public void ExactLegacyUnitWinsOverNewerNormalInfo()
    {
        string own=Prefix+"millimeters-1.0.2";
        string json=JsonSerializer.Serialize(new { typeid=own,inherits=new[]{"autodesk.unit:unit.derived-1.0.0"},
            constants=new object[]{new{id="parentUnits",value=new Dictionary<string,int>{{Prefix+"kilograms-1.0.0",1}}},new{id="factor",value=2}} });
        var units=UnitSchemaEvaluator.EvaluateDisplay(new(0,0,[new(own,json,0,0)]),out string? failure);
        Assert.NotNull(units);Assert.Null(failure);
        Assert.Equal(0.5,1.0/units.GetNormal(Prefix+"millimeters-1.0.1")!.Core.InternalScale);
        var plan=Plan(Options(Option()),units);
        Assert.Equal(Prefix+"millimeters-1.0.1",plan.Length.ResolvedUnitId);
        Assert.Equal(0x40730CCCCCCCCCCDul,Bits(plan.Length.Scale));
    }

    [Fact]
    public void ComputedOwnedPlanRevokesOnDuplicateMalformedForeignOrMissingSource()
    {
        var owned=new TfBlockDeserializer.UnitsElemOptions(77,-1,1,10,50,Options(Option()));
        var state=new ParsingState { OwnDocumentUnitsElemId=77,OwnDocumentUnitConversions=Sdk };
        IfcNumericUnitPlan? PlanFromState() =>
            IfcNumericUnitPlan.Create(state.OwnDocumentUnitOptions?.Value, state.OwnDocumentUnitConversions, out _);

        state.OwnUnitOptions.Add(77,(1,owned));Assert.NotNull(PlanFromState());
        state.OwnUnitOptions[77]=(2,owned);Assert.Null(PlanFromState());
        state.OwnUnitOptions[77]=(1,null);Assert.Null(PlanFromState());
        state.OwnUnitOptions[77]=(1,owned with {FamilyId=321});Assert.Null(PlanFromState());
        state.OwnUnitOptions[77]=(1,owned with {DocumentAccess=2});Assert.Null(PlanFromState());
        state.OwnUnitOptions[77]=(1,owned);state.OwnDocumentUnitConversions=null;Assert.Null(PlanFromState());
        state.OwnDocumentUnitConversions=Sdk;state.OwnDocumentUnitsElemId=88;
        Assert.Null(PlanFromState());
    }
}
