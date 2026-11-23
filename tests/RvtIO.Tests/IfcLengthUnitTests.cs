using System.Globalization;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class IfcLengthUnitTests
{
    [Theory]
    [InlineData("0.3048", "304.8")]
    [InlineData("3.", "3000.")]
    [InlineData("-0.25", "-250.")]
    [InlineData("0.", "0.")]
    [InlineData("1.5", "1500.")]
    [InlineData("104723.066996631", "104723066.996631")]
    [InlineData("0.30000000000000004", "300.00000000000004")]
    [InlineData("0.076200000000000003", "76.200000000000003")]
    [InlineData("5.0E-06", "0.005")]
    [InlineData("2.5e+2", "250000.")]
    [InlineData(" 2.7 ", "2700.")]
    public void ShiftNumber_MovesTheDecimalPointThreePlacesWithoutRounding(string input, string expected) =>
        Assert.Equal(expected, IfcLengthUnit.ShiftNumber(input));

    [Theory]
    [InlineData("$")]
    [InlineData("*")]
    [InlineData("#12")]
    [InlineData(".T.")]
    [InlineData("")]
    [InlineData("'1.5'")]
    public void ShiftNumber_LeavesWhatIsNotANumber(string input) =>
        Assert.Equal(input, IfcLengthUnit.ShiftNumber(input));

    [Theory]
    [InlineData("-0.", "-0.")]      // the written negative zero of a building pad's usage offset survives the shift
    [InlineData("0.", "0.")]
    [InlineData("0.076200000000000003", "76.200000000000003")]
    public void LayerSetUsage_ShiftsItsOffsetButKeepsANegativeZero(string offset, string expected) =>
        Assert.Equal($"#5,.AXIS3.,.NEGATIVE.,{expected}",
            IfcLengthUnit.ToMillimetres("IFCMATERIALLAYERSETUSAGE", $"#5,.AXIS3.,.NEGATIVE.,{offset}"));

    [Fact]
    public void Points_ScaleEveryCoordinate()
    {
        Assert.Equal("(304.8,609.6,1000.)", IfcLengthUnit.ToMillimetres("IFCCARTESIANPOINT", "(0.3048,0.6096,1.)"));
        Assert.Equal("(1000.,-500.)", IfcLengthUnit.ToMillimetres("IFCCARTESIANPOINT", "(1.,-0.5)"));
    }

    [Fact]
    public void ListedAttributes_ScaleAndTheRestIsKept()
    {
        Assert.Equal("#1,#2,#3,2700.", IfcLengthUnit.ToMillimetres("IFCEXTRUDEDAREASOLID", "#1,#2,#3,2.7"));
        Assert.Equal(".AREA.,$,#5,1500.,200.", IfcLengthUnit.ToMillimetres("IFCRECTANGLEPROFILEDEF", ".AREA.,$,#5,1.5,0.2"));
        Assert.Equal(".AREA.,$,#5,6.", IfcLengthUnit.ToMillimetres("IFCCIRCLEPROFILEDEF", ".AREA.,$,#5,0.006"));
        Assert.Equal("#9,25.,$", IfcLengthUnit.ToMillimetres("IFCMATERIALLAYER", "#9,0.025,$"));
    }

    [Fact]
    public void StringsWithSeparatorsDoNotShiftAttributeIndices()
    {
        string args = "'1bOe',#5,'Level, (1) it''s','','Level',#4960,$,'Level',.ELEMENT.,-0.8";
        Assert.Equal("'1bOe',#5,'Level, (1) it''s','','Level',#4960,$,'Level',.ELEMENT.,-800.",
            IfcLengthUnit.ToMillimetres("IFCBUILDINGSTOREY", args));
    }

    [Fact]
    public void StairFlightLengthsAreTheTenthAndEleventhAttributes()
    {
        Assert.Equal("'g',#5,'n',$,$,#1,#2,'t',18,17,180.56,300.",
            IfcLengthUnit.ToMillimetres("IFCSTAIRFLIGHT", "'g',#5,'n',$,$,#1,#2,'t',18,17,0.18056,0.3"));
    }

    [Theory]
    [InlineData("IFCDOOR", "'g',#5,'n',$,$,#1,#2,'t',2180.,1032.5")]
    [InlineData("IFCWINDOW", "'g',#5,'n',$,$,#1,#2,'t',2710.,1500.")]
    [InlineData("IFCPROPERTYSINGLEVALUE", "'Span',$,IFCPOSITIVELENGTHMEASURE(750.),$")]
    [InlineData("IFCDIRECTION", "(0.,0.,1.)")]
    [InlineData("IFCCARTESIANTRANSFORMATIONOPERATOR3D", "#1,#2,#3,1.,#4")]
    public void EntitiesWrittenInMillimetresOrDimensionlessAreNotTouched(string type, string args) =>
        Assert.Equal(args, IfcLengthUnit.ToMillimetres(type, args));

    [Theory]
    [InlineData(IfcProfile.Current, "0.1", "''")]
    [InlineData(IfcProfile.Legacy, "0.01", "$")]
    public void Export_DeclaresMillimetresAndMovesTheStoreyElevation(IfcProfile version, string precision, string identifier)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 10, CategoryId = -2000240, Category = "OST_Levels", Name = "Level 1" });
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-length-unit-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            string ifc = File.ReadAllText(path);
            Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", ifc);
            Assert.DoesNotContain("=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);", ifc);
            Assert.Contains($"IFCGEOMETRICREPRESENTATIONCONTEXT({identifier},'Model',3,{precision},", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public class IfcLengthUnitFamilyTests
{
    [Fact]
    public void Foot_DividesMetreAttributesByTheFootAndMillimetreAttributesBy304_8()
    {
        Assert.Equal("#9,1.", IfcLengthUnit.ToFamily("IFCVECTOR", "#9,0.3048", IfcUnitFamily.Foot));
        Assert.Equal("(0.,0.,-6.)", IfcLengthUnit.ToFamily("IFCCARTESIANPOINT", "(0.,0.,-1.8288)", IfcUnitFamily.Foot));
        Assert.Equal("#1,#2,'a','','',#3,#4,'5',6.999999999999999,3.", IfcLengthUnit.ToFamily("IFCDOOR", "#1,#2,'a','','',#3,#4,'5',2133.6,914.4", IfcUnitFamily.Foot));
    }

    [Fact]
    public void Foot_ScalesAreasVolumesAndTypedPropertyValuesOnly()
    {
        Assert.Equal("'Q','',$,1.", IfcLengthUnit.ToFamily("IFCQUANTITYAREA", "'Q','',$,0.092903040000000006", IfcUnitFamily.Foot));
        Assert.Equal("'Q','',$,1.", IfcLengthUnit.ToFamily("IFCQUANTITYVOLUME", "'Q','',$,0.028316846592000004", IfcUnitFamily.Foot));
        Assert.Equal("'Span','',IFCPOSITIVELENGTHMEASURE(1.),$", IfcLengthUnit.ToFamily("IFCPROPERTYSINGLEVALUE", "'Span','',IFCPOSITIVELENGTHMEASURE(304.8),$", IfcUnitFamily.Foot));
        const string thermal = "'U','',IFCTHERMALTRANSMITTANCEMEASURE(0.5),$";
        Assert.Equal(thermal, IfcLengthUnit.ToFamily("IFCPROPERTYSINGLEVALUE", thermal, IfcUnitFamily.Foot));
        Assert.Equal("#1,.AREA.,'',#2,$", IfcLengthUnit.ToFamily("IFCMEASUREWITHUNIT", "#1,.AREA.,'',#2,$", IfcUnitFamily.Foot));
    }

    [Fact]
    public void Metre_KeepsMetresAndMovesMillimetreAttributes()
    {
        Assert.Equal("(1.8034,2.)", IfcLengthUnit.ToFamily("IFCCARTESIANPOINT", "(1.8034,2.)", IfcUnitFamily.Metre));
        Assert.Equal("'Q','',$,2.2352", IfcLengthUnit.ToFamily("IFCQUANTITYLENGTH", "'Q','',$,2.2352", IfcUnitFamily.Metre));
        Assert.Equal("'P','',IFCLENGTHMEASURE(0.0254),$", IfcLengthUnit.ToFamily("IFCPROPERTYSINGLEVALUE", "'P','',IFCLENGTHMEASURE(25.4),$", IfcUnitFamily.Metre));
    }

    [Theory]
    [InlineData(IfcUnitFamily.Centimetre, 0.01, 0.0001, 0.000001)]
    [InlineData(IfcUnitFamily.Inch, 0.0254, 0.00064516, 0.000016387064)]
    public void ExplicitUnits_ScaleMillimetreDoorSizesAndTypedProperties(
        IfcUnitFamily family, double lengthFactor, double areaFactor, double volumeFactor)
    {
        string door = IfcLengthUnit.ToFamily("IFCDOOR", "#1,#2,'Door','','',#3,#4,'5',2000.,800.", family);
        var sizes = IfcLengthUnit.SplitTopLevel(door);
        Assert.Equal(2.0, double.Parse(sizes[8], CultureInfo.InvariantCulture) * lengthFactor, 12);
        Assert.Equal(0.8, double.Parse(sizes[9], CultureInfo.InvariantCulture) * lengthFactor, 12);

        foreach (var (measure, source, factor, expected) in new[]
        {
            ("IFCLENGTHMEASURE", "25.4", lengthFactor, 0.0254),
            ("IFCAREAMEASURE", "2.5", areaFactor, 2.5),
            ("IFCVOLUMEMEASURE", "1.25", volumeFactor, 1.25),
        })
        {
            string args = IfcLengthUnit.ToFamily("IFCPROPERTYSINGLEVALUE", $"'P',$,{measure}({source}),$", family);
            string nominal = IfcLengthUnit.SplitTopLevel(args)[2];
            Assert.StartsWith(measure + "(", nominal);
            Assert.Equal(expected, double.Parse(nominal.AsSpan(measure.Length + 1, nominal.Length - measure.Length - 2),
                CultureInfo.InvariantCulture) * factor, 12);
        }
    }

    // The centimetre conversions are decimal shifts of the literal, as the millimetre ones are: each expected text is the exact
    // decimal result, where a division by the double 0.01, 0.0001 or 0.000001 would leave low-digit noise (0.29 / 0.01 is 28.999999999999996).
    [Theory]
    [InlineData("IFCCARTESIANPOINT", "(0.29,1.15,2.3)", "(29.,115.,230.)")]
    [InlineData("IFCVECTOR", "#9,0.07", "#9,7.")]
    [InlineData("IFCDOOR", "#1,#2,'a','','',#3,#4,'5',2133.6,914.4", "#1,#2,'a','','',#3,#4,'5',213.36,91.44")]
    [InlineData("IFCQUANTITYAREA", "'Q','',$,1.2", "'Q','',$,12000.")]
    [InlineData("IFCQUANTITYVOLUME", "'Q','',$,0.07", "'Q','',$,70000.")]
    [InlineData("IFCPROPERTYSINGLEVALUE", "'P','',IFCLENGTHMEASURE(8.2),$", "'P','',IFCLENGTHMEASURE(0.82),$")]
    [InlineData("IFCPROPERTYSINGLEVALUE", "'P','',IFCAREAMEASURE(0.29),$", "'P','',IFCAREAMEASURE(2900.),$")]
    [InlineData("IFCPROPERTYSINGLEVALUE", "'P','',IFCVOLUMEMEASURE(2.2),$", "'P','',IFCVOLUMEMEASURE(2200000.),$")]
    public void Centimetre_MovesTheDecimalPointWithoutNoise(string type, string args, string expected) =>
        Assert.Equal(expected, IfcLengthUnit.ToFamily(type, args, IfcUnitFamily.Centimetre));

    // The exact quantities of the current profile print the double itself, which is multiplied by an exact 100, 10000 or 1000000
    // and so rounds once; these inputs give the round result, a division by 0.0001 or 0.000001 gives 11999.999999999998 and 2200000.0000000005.
    [Theory]
    [InlineData("IFCQUANTITYAREA", "'Q','',$,1.2", "'Q','',$,12000.")]
    [InlineData("IFCQUANTITYVOLUME", "'Q','',$,2.2", "'Q','',$,2200000.")]
    [InlineData("IFCPROPERTYSINGLEVALUE", "'P','',IFCAREAMEASURE(1.2),$", "'P','',IFCAREAMEASURE(12000.),$")]
    [InlineData("IFCPROPERTYSINGLEVALUE", "'P','',IFCVOLUMEMEASURE(2.2),$", "'P','',IFCVOLUMEMEASURE(2200000.),$")]
    public void Centimetre_ExactQuantitiesMultiplyByAnExactPowerOfTen(string type, string args, string expected) =>
        Assert.Equal(expected, IfcLengthUnit.ToFamily(type, args, IfcUnitFamily.Centimetre, exact: true));

    [Fact]
    public void Centimetre_LeavesALiteralTooLargeForADecimalToTheDoubleInsteadOfOverflowing()
    {
        // 1e27 is a decimal, but 1e27 * 100 is more than a decimal holds (7.9e28)
        string text = IfcLengthUnit.ToFamily("IFCVECTOR", "#9,1.E+27", IfcUnitFamily.Centimetre);
        Assert.Equal(1e29, double.Parse(text[3..], CultureInfo.InvariantCulture), 1e15);
    }

    [Fact]
    public void FootUnitAssignment_NamesTheFootAndTheSquareAndCubicFoot()
    {
        var w = new StepWriter();
        int length = w.Add("IFCSIUNIT", "*,.LENGTHUNIT.,$,.METRE.");
        IfcExporter.CreateUnitAssignmentCurrent(w, length, IfcUnitFamily.Foot);
        var text = new System.IO.StringWriter();
        w.WriteTo(text, "f", "IFC2X3", "d", "a");
        string file = text.ToString();
        Assert.Contains("'FOOT'", file);
        Assert.Contains("'SQUARE FOOT'", file);
        Assert.Contains("'CUBIC FOOT'", file);
        Assert.Contains("IFCLENGTHMEASURE(0.30480000000000002)", file);
        Assert.Contains("IFCAREAMEASURE(0.092903040000000006)", file);
        Assert.Contains("IFCVOLUMEMEASURE(0.028316846592000004)", file);
        Assert.Contains("=IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.);", file);
        Assert.Contains("=IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.);", file);
        IfcLengthUnitChoiceOptionTests.AssertValidIfcUnitGraph(file, requireProjectReference: false);
        string assignmentArgs = Regex.Match(file, @"=IFCUNITASSIGNMENT\(\(([^)]*)\)\);").Groups[1].Value;
        var assignedRefs = assignmentArgs.Split(',').Select(r => r.TrimStart('#')).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(4, assignedRefs.Count);
        foreach (string id in assignedRefs)
            Assert.Contains($"#{id}=IFCCONVERSIONBASEDUNIT(", file);
    }

    [Fact]
    public void Millimetre_IsTheUnchangedShift() =>
        Assert.Equal(IfcLengthUnit.ToMillimetres("IFCVECTOR", "#9,0.3048"), IfcLengthUnit.ToFamily("IFCVECTOR", "#9,0.3048", IfcUnitFamily.Millimetre));

    [Theory]
    [InlineData(null, IfcUnitFamily.Millimetre)]
    [InlineData("autodesk.unit.unit:millimeters-1.0.1", IfcUnitFamily.Millimetre)]
    [InlineData("autodesk.unit.unit:feetFractionalInches-1.0.0", IfcUnitFamily.Foot)]
    [InlineData("autodesk.unit.unit:feet-1.0.1", IfcUnitFamily.Foot)]
    [InlineData("autodesk.unit.unit:meters-1.0.0", IfcUnitFamily.Metre)]
    [InlineData("autodesk.unit.unit:metersCentimeters-1.0.0", IfcUnitFamily.Metre)]
    [InlineData("autodesk.unit.unit:usSurveyFeet-1.0.0", IfcUnitFamily.Foot)]
    [InlineData("autodesk.unit.unit:centimeters-1.0.1", IfcUnitFamily.Millimetre)]
    [InlineData("autodesk.unit.unit:decimeters-1.0.1", IfcUnitFamily.Millimetre)]
    [InlineData("autodesk.unit.unit:inches-1.0.1", IfcUnitFamily.Millimetre)]
    public void Family_FollowsTheStoredLengthUnit(string? unit, IfcUnitFamily expected) =>
        Assert.Equal(expected, IfcLengthUnitChoice.Family(unit));

    [Theory]
    [InlineData("autodesk.unit.unit:millimeters-1.0.1", true)]
    [InlineData("autodesk.unit.unit:metersCentimeters-1.0.0", true)]
    [InlineData("autodesk.unit.unit:usSurveyFeet-1.0.0", true)]
    [InlineData("autodesk.unit.unit:centimeters-1.0.1", false)]
    [InlineData("autodesk.unit.unit:fractionalInches-1.0.0", false)]
    [InlineData(null, false)]
    public void OwnFamily_IsNullForAUnitWithoutAFamilyAndForAnUnreadableOption(string? unit, bool own) =>
        Assert.Equal(own, IfcLengthUnitChoice.OwnFamily(unit) != null);

    [Theory]
    [InlineData(IfcProfile.Current, "autodesk.unit.unit:metersCentimeters-1.0.0", "=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);", "metersCentimeters-1.0.0 -> Metre")]
    [InlineData(IfcProfile.Current, "autodesk.unit.unit:centimeters-1.0.1", "=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", "centimeters-1.0.1 has no unit family of its own; using the default Millimetre")]
    [InlineData(IfcProfile.Current, null, "=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", "stored option unreadable (")]
    [InlineData(IfcProfile.Legacy, "autodesk.unit.unit:centimeters-1.0.1", "=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", "centimeters-1.0.1 -> Millimetre")]
    [InlineData(IfcProfile.Legacy, "autodesk.unit.unit:metersCentimeters-1.0.0", "=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", "metersCentimeters-1.0.0 -> Millimetre")]
    public void Export_DeclaresTheFamilyOfTheStoredUnitAndNamesTheDefaultInTheDiagnostics(IfcProfile profile, string? unit, string declared, string diagnostic)
    {
        var info = new RevitFileInfo { SerializedLengthUnit = unit };
        info.Elements.Add(new Element { Id = 10, CategoryId = -2000240, Category = "OST_Levels", Name = "Level 1" });
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-length-unit-{Guid.NewGuid():N}.ifc");
        var diagnostics = new StringWriter();
        try
        {
            new IfcExporter { Profile = profile, DiagnosticOutput = diagnostics }.Export(info, path);
            Assert.Contains(declared, File.ReadAllText(path));
            string line = Assert.Single(diagnostics.ToString().Split(Environment.NewLine), text => text.StartsWith("IFC length unit:", StringComparison.Ordinal));
            Assert.Contains(diagnostic, line);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

[Collection("ElemTableStaticMaps")]
public class IfcLengthUnitChoiceOptionTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 1, 15, 12, 30, 45, TimeSpan.Zero);

    [Fact]
    public void IfcLengthUnitOption_WithoutOption_ReturnsNull()
    {
        Assert.True(RvtIO.Cli.IfcLengthUnitOption.TryParse(["project.rvt", "project.ifc"], out var unit, out string? error));
        Assert.Null(unit);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("mm", IfcUnitFamily.Millimetre)]
    [InlineData("MM", IfcUnitFamily.Millimetre)]
    [InlineData("millimetre", IfcUnitFamily.Millimetre)]
    [InlineData("millimeter", IfcUnitFamily.Millimetre)]
    [InlineData("millimetres", IfcUnitFamily.Millimetre)]
    [InlineData("millimeters", IfcUnitFamily.Millimetre)]
    [InlineData("cm", IfcUnitFamily.Centimetre)]
    [InlineData("CM", IfcUnitFamily.Centimetre)]
    [InlineData("centimetre", IfcUnitFamily.Centimetre)]
    [InlineData("centimeters", IfcUnitFamily.Centimetre)]
    [InlineData("m", IfcUnitFamily.Metre)]
    [InlineData("metre", IfcUnitFamily.Metre)]
    [InlineData("meter", IfcUnitFamily.Metre)]
    [InlineData("metres", IfcUnitFamily.Metre)]
    [InlineData("meters", IfcUnitFamily.Metre)]
    [InlineData("ft", IfcUnitFamily.Foot)]
    [InlineData("FT", IfcUnitFamily.Foot)]
    [InlineData("foot", IfcUnitFamily.Foot)]
    [InlineData("feet", IfcUnitFamily.Foot)]
    [InlineData("in", IfcUnitFamily.Inch)]
    [InlineData("IN", IfcUnitFamily.Inch)]
    [InlineData("inch", IfcUnitFamily.Inch)]
    [InlineData("inches", IfcUnitFamily.Inch)]
    public void IfcLengthUnitOption_SupportedSpellings_MapToExpectedFamily(string raw, IfcUnitFamily expected)
    {
        Assert.True(RvtIO.Cli.IfcLengthUnitOption.TryParse(["project.rvt", "project.ifc", "--ifc-length-unit", raw], out var unit, out string? error));
        Assert.Null(error);
        Assert.Equal(expected, unit);
    }

    public static IEnumerable<object[]> InvalidLengthUnitArguments()
    {
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-length-unit" }];
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-length-unit", "--ifc-revit-parameters" }];
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-length-unit", "km" }];
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-length-unit", "m", "--ifc-length-unit", "ft" }];
    }

    [Theory]
    [MemberData(nameof(InvalidLengthUnitArguments))]
    public void IfcLengthUnitOption_UnsupportedOrDuplicateInput_FailsWithMessage(string[] args)
    {
        Assert.False(RvtIO.Cli.IfcLengthUnitOption.TryParse(args, out var unit, out string? error));
        Assert.Null(unit);
        Assert.NotNull(error);
        Assert.Contains("--ifc-length-unit", error);
    }

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void Export_WithInvalidEnumValue_ThrowsArgumentOutOfRangeException(IfcProfile profile)
    {
        var info = BuildSampleModel("autodesk.unit.unit:millimeters-1.0.1");
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-unit-bad-{Guid.NewGuid():N}.ifc");
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new IfcExporter { Profile = profile, LengthUnit = (IfcUnitFamily)99 }.Export(info, path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(IfcProfile.Current, "autodesk.unit.unit:millimeters-1.0.1", IfcUnitFamily.Millimetre)]
    [InlineData(IfcProfile.Current, "autodesk.unit.unit:meters-1.0.0", IfcUnitFamily.Metre)]
    [InlineData(IfcProfile.Current, "autodesk.unit.unit:feet-1.0.1", IfcUnitFamily.Foot)]
    [InlineData(IfcProfile.Current, "autodesk.unit.unit:centimeters-1.0.1", IfcUnitFamily.Millimetre)]
    [InlineData(IfcProfile.Current, "autodesk.unit.unit:inches-1.0.1", IfcUnitFamily.Millimetre)]
    [InlineData(IfcProfile.Legacy, "autodesk.unit.unit:meters-1.0.0", IfcUnitFamily.Millimetre)]
    public void Export_WithoutExplicitSetting_MatchesMatchingExplicitFamilyByteForByte(
        IfcProfile profile, string storedUnit, IfcUnitFamily matchingFamily)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rvtio-unit-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string dirA = Path.Combine(dir, "a");
            string dirB = Path.Combine(dir, "b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            string defaultPath = Path.Combine(dirA, "model.ifc");
            string explicitPath = Path.Combine(dirB, "model.ifc");

            new IfcExporter(includeRevitPropertySets: true)
            {
                Profile = profile,
                Timestamp = FixedUtc,
            }.Export(BuildSampleModel(storedUnit), defaultPath);

            new IfcExporter(includeRevitPropertySets: true)
            {
                Profile = profile,
                Timestamp = FixedUtc,
                LengthUnit = matchingFamily,
            }.Export(BuildSampleModel(storedUnit), explicitPath);

            Assert.Equal(File.ReadAllBytes(defaultPath), File.ReadAllBytes(explicitPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void Export_AcrossUnitFamilies_DeclaresMatchingUnitsAndPreservesPhysicalGeometryAndQuantities(IfcProfile profile)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rvtio-unit-equiv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var outputs = new Dictionary<IfcUnitFamily, string>();
            foreach (var family in Enum.GetValues<IfcUnitFamily>())
            {
                string path = Path.Combine(dir, $"{family}.ifc");
                var diag = new StringWriter();
                new IfcExporter(diag, includeRevitPropertySets: true)
                {
                    Profile = profile,
                    BaseQuantities = true,
                    Timestamp = FixedUtc,
                    LengthUnit = family,
                }.Export(BuildSampleModel("autodesk.unit.unit:feetFractionalInches-1.0.0"), path);

                outputs[family] = File.ReadAllText(path);
                Assert.Contains($"IFC length unit: explicit choice -> {family}", diag.ToString());
            }

            Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", outputs[IfcUnitFamily.Millimetre]);
            Assert.Contains("=IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.);", outputs[IfcUnitFamily.Millimetre]);
            Assert.Contains("=IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.);", outputs[IfcUnitFamily.Millimetre]);

            Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);", outputs[IfcUnitFamily.Metre]);
            Assert.DoesNotContain("=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", outputs[IfcUnitFamily.Metre]);
            Assert.Contains("=IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.);", outputs[IfcUnitFamily.Metre]);
            Assert.Contains("=IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.);", outputs[IfcUnitFamily.Metre]);

            Assert.Contains("'FOOT'", outputs[IfcUnitFamily.Foot]);
            Assert.Contains("'SQUARE FOOT'", outputs[IfcUnitFamily.Foot]);
            Assert.Contains("'CUBIC FOOT'", outputs[IfcUnitFamily.Foot]);
            Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);", outputs[IfcUnitFamily.Foot]);
            Assert.Contains("=IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.);", outputs[IfcUnitFamily.Foot]);
            Assert.Contains("=IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.);", outputs[IfcUnitFamily.Foot]);

            string cm = outputs[IfcUnitFamily.Centimetre];
            Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,.CENTI.,.METRE.);", cm);
            Assert.Contains("=IFCSIUNIT(*,.AREAUNIT.,.CENTI.,.SQUARE_METRE.);", cm);
            Assert.Contains("=IFCSIUNIT(*,.VOLUMEUNIT.,.CENTI.,.CUBIC_METRE.);", cm);
            string inch = outputs[IfcUnitFamily.Inch];
            Assert.Contains(",'INCH',", inch);
            Assert.Contains(",'SQUARE INCH',", inch);
            Assert.Contains(",'CUBIC INCH',", inch);
            AssertConversionFactor(inch, "IFCLENGTHMEASURE", 0.0254);
            AssertConversionFactor(inch, "IFCAREAMEASURE", 0.00064516);
            AssertConversionFactor(inch, "IFCVOLUMEMEASURE", 0.000016387064);

            foreach (string ifc in outputs.Values)
                AssertValidIfcUnitGraph(ifc);

            var mmMetrics = ExtractPhysicalMetricsInSi(outputs[IfcUnitFamily.Millimetre], IfcUnitFamily.Millimetre);
            Assert.NotEmpty(mmMetrics.Points);
            Assert.NotEmpty(mmMetrics.StoreyElevations);
            if (profile == IfcProfile.Current)
            {
                Assert.NotEmpty(mmMetrics.Lengths);
                Assert.NotEmpty(mmMetrics.Areas);
                Assert.NotEmpty(mmMetrics.Volumes);
            }

            foreach (var family in Enum.GetValues<IfcUnitFamily>().Where(family => family != IfcUnitFamily.Millimetre))
            {
                var metrics = ExtractPhysicalMetricsInSi(outputs[family], family);
                AssertListsClose(mmMetrics.StoreyElevations, metrics.StoreyElevations, 1e-9);
                AssertPointListsClose(mmMetrics.Points, metrics.Points, 1e-9);
                AssertListsClose(mmMetrics.Depths, metrics.Depths, 1e-9);
                AssertListsClose(mmMetrics.Lengths, metrics.Lengths, 1e-9);
                AssertListsClose(mmMetrics.Areas, metrics.Areas, 1e-9);
                AssertListsClose(mmMetrics.Volumes, metrics.Volumes, 1e-9);
                if (family is IfcUnitFamily.Centimetre or IfcUnitFamily.Inch)
                {
                    double precision = profile == IfcProfile.Current ? 0.0001 : 0.00001;
                    Assert.Equal(precision, metrics.Precision, 13);
                }
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static void AssertConversionFactor(string ifc, string measure, double expected)
    {
        var match = Assert.Single(Regex.Matches(ifc, $@"=IFCMEASUREWITHUNIT\({measure}\(([^)]+)\),"));
        Assert.Equal(expected, double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), 15);
    }

    [Theory]
    [InlineData(IfcUnitFamily.Centimetre, 0.01, 30.48)]
    [InlineData(IfcUnitFamily.Inch, 0.0254, 12.0)]
    public void Export_ScalesRebarRadiusAndLineParameterButKeepsTheArcAngle(
        IfcUnitFamily family, double metresPerUnit, double lineParameter)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(RebarReaderTests.Bar(500, 0));
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-unit-rebar-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { LengthUnit = family }.Export(info, path);
            string ifc = File.ReadAllText(path);
            var disks = Regex.Matches(ifc, @"=IFCSWEPTDISKSOLID\(#\d+,([^,]+),\$,0\.,([^)]+)\);");
            Assert.Equal(2, disks.Count);
            foreach (Match disk in disks)
            {
                Assert.Equal(0.004, double.Parse(disk.Groups[1].Value, CultureInfo.InvariantCulture) * metresPerUnit, 12);
                Assert.Equal(lineParameter + 90, double.Parse(disk.Groups[2].Value, CultureInfo.InvariantCulture), 12);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void CliExport_WithIfcLengthUnit_WritesChosenUnitAndRejectsUnknownUnitsOrJsonOutput()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rvtio-cli-unit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string rvt = Path.Combine(dir, "synthetic.rvt");
            IfcDeterminismTests.WriteSyntheticRvt(rvt);

            var (badExit, badErr) = RunCli(rvt, Path.Combine(dir, "bad.ifc"), "--ifc-length-unit", "km");
            Assert.Equal(RvtExitCodes.Usage, badExit);
            Assert.Contains("--ifc-length-unit", badErr);
            Assert.False(File.Exists(Path.Combine(dir, "bad.ifc")));

            var (jsonExit, jsonErr) = RunCli(rvt, Path.Combine(dir, "out.json"), "--ifc-length-unit", "m");
            Assert.Equal(RvtExitCodes.Usage, jsonExit);
            Assert.Contains("--ifc-length-unit requires IFC output.", jsonErr);
            Assert.False(File.Exists(Path.Combine(dir, "out.json")));

            string mmPath = Path.Combine(dir, "mm.ifc");
            string mPath = Path.Combine(dir, "m.ifc");
            string ftPath = Path.Combine(dir, "ft.ifc");
            string cmPath = Path.Combine(dir, "cm.ifc");
            string inPath = Path.Combine(dir, "in.ifc");
            Assert.Equal(RvtExitCodes.Success, RunCli(rvt, mmPath, "--ifc-length-unit", "mm").ExitCode);
            Assert.Equal(RvtExitCodes.Success, RunCli(rvt, mPath, "--ifc-length-unit", "m").ExitCode);
            Assert.Equal(RvtExitCodes.Success, RunCli(rvt, ftPath, "--ifc-length-unit", "ft").ExitCode);
            Assert.Equal(RvtExitCodes.Success, RunCli(rvt, cmPath, "--ifc-length-unit", "cm").ExitCode);
            Assert.Equal(RvtExitCodes.Success, RunCli(rvt, inPath, "--ifc-length-unit", "in").ExitCode);

            Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", File.ReadAllText(mmPath));
            Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);", File.ReadAllText(mPath));
            Assert.Contains("'FOOT'", File.ReadAllText(ftPath));
            Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,.CENTI.,.METRE.);", File.ReadAllText(cmPath));
            Assert.Contains("'INCH'", File.ReadAllText(inPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("cm")]
    [InlineData("in")]
    public void CliExport_WithIfcLengthUnit_LeavesBothJsonOutputsUnchanged(string unit)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rvtio-cli-unit-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string rvt = Path.Combine(dir, "synthetic.rvt");
            IfcDeterminismTests.WriteSyntheticRvt(rvt);
            string ifc = Path.Combine(dir, "model.ifc");
            string compact = Path.Combine(dir, "compact.json");
            string full = Path.Combine(dir, "full.json");
            string[] args = [rvt, "--ifc", ifc, "--compact-json", compact, "--full-json", full];
            Assert.Equal(RvtExitCodes.Success, RunCli(args).ExitCode);
            byte[] compactDefault = File.ReadAllBytes(compact), fullDefault = File.ReadAllBytes(full);

            Assert.Equal(RvtExitCodes.Success, RunCli([.. args, "--ifc-length-unit", unit]).ExitCode);

            Assert.Equal(compactDefault, File.ReadAllBytes(compact));
            Assert.Equal(fullDefault, File.ReadAllBytes(full));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(IfcProfile.Current, IfcUnitFamily.Millimetre)]
    [InlineData(IfcProfile.Current, IfcUnitFamily.Metre)]
    [InlineData(IfcProfile.Current, IfcUnitFamily.Foot)]
    [InlineData(IfcProfile.Current, IfcUnitFamily.Centimetre)]
    [InlineData(IfcProfile.Current, IfcUnitFamily.Inch)]
    [InlineData(IfcProfile.Legacy, IfcUnitFamily.Millimetre)]
    [InlineData(IfcProfile.Legacy, IfcUnitFamily.Metre)]
    [InlineData(IfcProfile.Legacy, IfcUnitFamily.Foot)]
    [InlineData(IfcProfile.Legacy, IfcUnitFamily.Centimetre)]
    [InlineData(IfcProfile.Legacy, IfcUnitFamily.Inch)]
    public void Export_EmptyAndPopulatedModels_EmitSchemaValidUnitGraphAcrossProfilesAndFamilies(
        IfcProfile profile, IfcUnitFamily family)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rvtio-unit-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string emptyPath = Path.Combine(dir, "empty.ifc");
            new IfcExporter { Profile = profile, Timestamp = FixedUtc, LengthUnit = family }
                .Export(new RevitFileInfo(), emptyPath);
            string emptyIfc = File.ReadAllText(emptyPath);
            AssertValidIfcUnitGraph(emptyIfc);

            string populatedPath = Path.Combine(dir, "populated.ifc");
            new IfcExporter(includeRevitPropertySets: true)
            {
                Profile = profile,
                BaseQuantities = true,
                Timestamp = FixedUtc,
                LengthUnit = family,
            }.Export(BuildPopulatedModelWithThermalAndPower(), populatedPath);
            string populatedIfc = File.ReadAllText(populatedPath);
            AssertValidIfcUnitGraph(populatedIfc);
            if (profile == IfcProfile.Current)
            {
                Assert.Contains("IFCTHERMALTRANSMITTANCEMEASURE(", populatedIfc);
                Assert.Contains("IFCPOWERMEASURE(", populatedIfc);
                Assert.Contains("IFCDERIVEDUNIT(", populatedIfc);
                Assert.Contains(".WATT.", populatedIfc);
            }

            if (family is IfcUnitFamily.Foot or IfcUnitFamily.Inch)
            {
                foreach (string ifc in new[] { emptyIfc, populatedIfc })
                {
                    Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);", ifc);
                    Assert.Contains("=IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.);", ifc);
                    Assert.Contains("=IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.);", ifc);
                    string assignmentArgs = Regex.Match(ifc, @"=IFCUNITASSIGNMENT\(\(([^)]*)\)\);").Groups[1].Value;
                    var assignedUnits = assignmentArgs.Split(',').Select(r => Regex.Match(ifc, $@"^{Regex.Escape(r)}=([A-Z0-9_]+)\(", RegexOptions.Multiline).Groups[1].Value).ToList();
                    Assert.Contains("IFCCONVERSIONBASEDUNIT", assignedUnits);
                }
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void UnitGraphValidator_RejectsMissingMandatorySiUnitNameAndMalformedConversionGraph()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-unit-bad-si-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy, Timestamp = FixedUtc, LengthUnit = IfcUnitFamily.Foot }
                .Export(new RevitFileInfo(), path);
            string validIfc = File.ReadAllText(path);
            AssertValidIfcUnitGraph(validIfc);

            string unnamedAreaAndVolume = validIfc
                .Replace("=IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.);", "=IFCSIUNIT(*,.AREAUNIT.,$,$);")
                .Replace("=IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.);", "=IFCSIUNIT(*,.VOLUMEUNIT.,$,$);");
            var unnamedErrors = ValidateIfcUnitGraph(unnamedAreaAndVolume);
            Assert.Equal(2, unnamedErrors.Count);
            Assert.Contains(unnamedErrors, error => error.Contains("AREAUNIT", StringComparison.Ordinal) && error.Contains("Name", StringComparison.Ordinal));
            Assert.Contains(unnamedErrors, error => error.Contains("VOLUMEUNIT", StringComparison.Ordinal) && error.Contains("Name", StringComparison.Ordinal));

            string wrongExponentsAndMeasure = validIfc
                .Replace("IFCDIMENSIONALEXPONENTS(2,0,0,0,0,0,0)", "IFCDIMENSIONALEXPONENTS(1,0,0,0,0,0,0)")
                .Replace("IFCMEASUREWITHUNIT(IFCLENGTHMEASURE(", "IFCMEASUREWITHUNIT(");
            var badGraphErrors = ValidateIfcUnitGraph(wrongExponentsAndMeasure);
            Assert.True(badGraphErrors.Count >= 2);
            Assert.Contains(badGraphErrors, error => error.Contains("IFCDIMENSIONALEXPONENTS", StringComparison.Ordinal));
            Assert.Contains(badGraphErrors, error => error.Contains("IFCMEASUREWITHUNIT", StringComparison.Ordinal));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    internal static void AssertValidIfcUnitGraph(string ifc, bool requireProjectReference = true)
    {
        var errors = ValidateIfcUnitGraph(ifc, requireProjectReference);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    internal static IReadOnlyList<string> ValidateIfcUnitGraph(string ifc, bool requireProjectReference = true)
    {
        var errors = new List<string>();
        var entities = new Dictionary<int, (string Type, string Args)>();
        var order = new List<int>();
        foreach (string rawLine in ifc.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            int open = eq > 1 ? line.IndexOf('(', eq + 1) : -1;
            if (open < 0 || !line.EndsWith(");", StringComparison.Ordinal)
                || !int.TryParse(line.AsSpan(1, eq - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                continue;
            string type = line[(eq + 1)..open].Trim();
            string args = line[(open + 1)..^2];
            if (!entities.TryAdd(id, (type, args)))
                errors.Add($"Duplicate entity id #{id}.");
            order.Add(id);
        }

        string? SiUnitType(int id)
        {
            if (!entities.TryGetValue(id, out var ent))
            {
                errors.Add($"Referenced unit #{id} does not exist.");
                return null;
            }
            if (ent.Type is not ("IFCSIUNIT" or "IFCCONVERSIONBASEDUNIT" or "IFCDERIVEDUNIT"))
            {
                errors.Add($"Entity #{id} ({ent.Type}) is not a valid IfcUnit subtype.");
                return null;
            }
            string[] a = IfcExporter.SplitStepArguments(ent.Args);
            return a.Length >= 2 ? a[1].Trim() : null;
        }

        var assignments = order.Where(id => entities[id].Type == "IFCUNITASSIGNMENT").ToList();
        if (assignments.Count != 1)
        {
            errors.Add($"Expected 1 IFCUNITASSIGNMENT entity, found {assignments.Count}.");
        }
        else
        {
            int assignmentId = assignments[0];
            if (requireProjectReference)
            {
                var projects = order.Where(id => entities[id].Type == "IFCPROJECT").ToList();
                if (projects.Count != 1)
                {
                    errors.Add($"Expected 1 IFCPROJECT entity, found {projects.Count}.");
                }
                else
                {
                    string[] projArgs = IfcExporter.SplitStepArguments(entities[projects[0]].Args);
                    if (projArgs.Length != 9 || !TryParseRef(projArgs[8], out int unitsRef) || unitsRef != assignmentId)
                        errors.Add($"IFCPROJECT #{projects[0]} UnitsInContext '{(projArgs.Length > 8 ? projArgs[8] : "")}' does not reference IFCUNITASSIGNMENT #{assignmentId}.");
                }
            }

            string[] uaArgs = IfcExporter.SplitStepArguments(entities[assignmentId].Args);
            if (uaArgs.Length != 1 || !TryParseRefList(uaArgs[0], out var unitRefs) || unitRefs.Count == 0)
            {
                errors.Add($"IFCUNITASSIGNMENT #{assignmentId} has malformed Units list '{entities[assignmentId].Args}'.");
            }
            else
            {
                var seenIds = new HashSet<int>();
                var seenUnitTypes = new HashSet<string>(StringComparer.Ordinal);
                foreach (int unitId in unitRefs)
                {
                    if (!seenIds.Add(unitId))
                        errors.Add($"IFCUNITASSIGNMENT #{assignmentId} lists unit #{unitId} more than once.");
                    string? unitType = SiUnitType(unitId);
                    if (unitType != null && !seenUnitTypes.Add(unitType))
                        errors.Add($"IFCUNITASSIGNMENT #{assignmentId} lists multiple units for {unitType}.");
                }
            }
        }

        var expectedSiNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [".LENGTHUNIT."] = [".METRE."],
            [".AREAUNIT."] = [".SQUARE_METRE."],
            [".VOLUMEUNIT."] = [".CUBIC_METRE."],
            [".PLANEANGLEUNIT."] = [".RADIAN."],
            [".SOLIDANGLEUNIT."] = [".STERADIAN."],
            [".TIMEUNIT."] = [".SECOND."],
            [".MASSUNIT."] = [".GRAM."],
            [".THERMODYNAMICTEMPERATUREUNIT."] = [".KELVIN.", ".DEGREE_CELSIUS."],
            [".POWERUNIT."] = [".WATT."],
            [".ELECTRICCURRENTUNIT."] = [".AMPERE."],
            [".LUMINOUSINTENSITYUNIT."] = [".CANDELA."],
            [".AMOUNTOFSUBSTANCEUNIT."] = [".MOLE."],
            [".FORCEUNIT."] = [".NEWTON."],
            [".PRESSUREUNIT."] = [".PASCAL."],
            [".ENERGYUNIT."] = [".JOULE."],
        };
        var validPrefixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "$", ".EXA.", ".PETA.", ".TERA.", ".GIGA.", ".MEGA.", ".KILO.", ".HECTO.", ".DECA.",
            ".DECI.", ".CENTI.", ".MILLI.", ".MICRO.", ".NANO.", ".PICO.", ".FEMTO.", ".ATTO.",
        };
        var expectedDimensions = new Dictionary<string, int[]>(StringComparer.Ordinal)
        {
            [".LENGTHUNIT."] = [1, 0, 0, 0, 0, 0, 0],
            [".AREAUNIT."] = [2, 0, 0, 0, 0, 0, 0],
            [".VOLUMEUNIT."] = [3, 0, 0, 0, 0, 0, 0],
            [".PLANEANGLEUNIT."] = [0, 0, 0, 0, 0, 0, 0],
        };
        var expectedMeasureTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".LENGTHUNIT."] = "IFCLENGTHMEASURE",
            [".AREAUNIT."] = "IFCAREAMEASURE",
            [".VOLUMEUNIT."] = "IFCVOLUMEMEASURE",
            [".PLANEANGLEUNIT."] = "IFCPLANEANGLEMEASURE",
        };

        foreach (int id in order)
        {
            var (type, rawArgs) = entities[id];
            string[] a = IfcExporter.SplitStepArguments(rawArgs);
            switch (type)
            {
                case "IFCSIUNIT":
                    if (a.Length != 4)
                    {
                        errors.Add($"IFCSIUNIT #{id} expected 4 attributes, got {a.Length}.");
                        break;
                    }
                    if (a[0] != "*")
                        errors.Add($"IFCSIUNIT #{id} Dimensions must be '*', got '{a[0]}'.");
                    if (!expectedSiNames.TryGetValue(a[1], out var allowedNames))
                        errors.Add($"IFCSIUNIT #{id} has unsupported UnitType '{a[1]}'.");
                    if (!validPrefixes.Contains(a[2]))
                        errors.Add($"IFCSIUNIT #{id} has invalid Prefix '{a[2]}'.");
                    if (a[3] is "$" or "*")
                        errors.Add($"IFCSIUNIT #{id} ({a[1]}) is missing mandatory Name attribute.");
                    else if (allowedNames != null && !allowedNames.Contains(a[3]))
                        errors.Add($"IFCSIUNIT #{id} ({a[1]}) has invalid Name '{a[3]}'.");
                    break;

                case "IFCDIMENSIONALEXPONENTS":
                    if (a.Length != 7 || a.Any(x => !int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                        errors.Add($"IFCDIMENSIONALEXPONENTS #{id} must have 7 integer exponents, got '{rawArgs}'.");
                    break;

                case "IFCCONVERSIONBASEDUNIT":
                    if (a.Length != 4)
                    {
                        errors.Add($"IFCCONVERSIONBASEDUNIT #{id} expected 4 attributes, got {a.Length}.");
                        break;
                    }
                    if (!TryParseRef(a[0], out int dimId) || !entities.TryGetValue(dimId, out var dimEnt) || dimEnt.Type != "IFCDIMENSIONALEXPONENTS")
                    {
                        errors.Add($"IFCCONVERSIONBASEDUNIT #{id} Dimensions '{a[0]}' does not reference IFCDIMENSIONALEXPONENTS.");
                    }
                    else if (expectedDimensions.TryGetValue(a[1], out var reqDims))
                    {
                        string[] dArgs = IfcExporter.SplitStepArguments(dimEnt.Args);
                        if (dArgs.Length != 7 || dArgs.Where((s, idx) => !int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v != reqDims[idx]).Any())
                            errors.Add($"IFCCONVERSIONBASEDUNIT #{id} ({a[1]}) references IFCDIMENSIONALEXPONENTS #{dimId} ({dimEnt.Args}) instead of ({string.Join(",", reqDims)}).");
                    }
                    if (!(a[2].Length >= 3 && a[2][0] == '\'' && a[2][^1] == '\''))
                        errors.Add($"IFCCONVERSIONBASEDUNIT #{id} Name '{a[2]}' must be a non-empty STEP string.");
                    if (!TryParseRef(a[3], out int mwuId) || !entities.TryGetValue(mwuId, out var mwuEnt) || mwuEnt.Type != "IFCMEASUREWITHUNIT")
                    {
                        errors.Add($"IFCCONVERSIONBASEDUNIT #{id} ConversionFactor '{a[3]}' does not reference IFCMEASUREWITHUNIT.");
                    }
                    else
                    {
                        string[] mArgs = IfcExporter.SplitStepArguments(mwuEnt.Args);
                        if (mArgs.Length == 2)
                        {
                            if (expectedMeasureTypes.TryGetValue(a[1], out string? reqMeasure)
                                && !mArgs[0].StartsWith(reqMeasure + "(", StringComparison.Ordinal))
                                errors.Add($"IFCCONVERSIONBASEDUNIT #{id} ({a[1]}) IFCMEASUREWITHUNIT #{mwuId} must use {reqMeasure}, got '{mArgs[0]}'.");
                            if (TryParseRef(mArgs[1], out int baseId) && SiUnitType(baseId) is string baseType && baseType != a[1])
                                errors.Add($"IFCCONVERSIONBASEDUNIT #{id} ({a[1]}) base unit #{baseId} has UnitType '{baseType}'.");
                        }
                    }
                    break;

                case "IFCMEASUREWITHUNIT":
                    if (a.Length != 2)
                    {
                        errors.Add($"IFCMEASUREWITHUNIT #{id} expected 2 attributes, got {a.Length}.");
                        break;
                    }
                    var match = Regex.Match(a[0], @"^(IFC[A-Z0-9_]+MEASURE)\(([^()]+)\)$");
                    if (!match.Success
                        || !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val)
                        || !double.IsFinite(val) || val <= 0)
                        errors.Add($"IFCMEASUREWITHUNIT #{id} ValueComponent '{a[0]}' must be a positive typed IFC measure literal.");
                    if (!TryParseRef(a[1], out int targetUnitId))
                        errors.Add($"IFCMEASUREWITHUNIT #{id} UnitComponent '{a[1]}' must be an entity reference.");
                    else
                        _ = SiUnitType(targetUnitId);
                    break;

                case "IFCDERIVEDUNIT":
                    if (a.Length != 3 || !TryParseRefList(a[0], out var elems) || elems.Count == 0)
                    {
                        errors.Add($"IFCDERIVEDUNIT #{id} has invalid Elements '{rawArgs}'.");
                        break;
                    }
                    foreach (int elemId in elems)
                        if (!entities.TryGetValue(elemId, out var elemEnt) || elemEnt.Type != "IFCDERIVEDUNITELEMENT")
                            errors.Add($"IFCDERIVEDUNIT #{id} element #{elemId} is not IFCDERIVEDUNITELEMENT.");
                    if (!(a[1].Length >= 6 && a[1].StartsWith('.') && a[1].EndsWith("UNIT.", StringComparison.Ordinal) || a[1] == ".USERDEFINED."))
                        errors.Add($"IFCDERIVEDUNIT #{id} has invalid UnitType '{a[1]}'.");
                    break;

                case "IFCDERIVEDUNITELEMENT":
                    if (a.Length != 2 || !TryParseRef(a[0], out int siId)
                        || !entities.TryGetValue(siId, out var siEnt) || siEnt.Type != "IFCSIUNIT"
                        || !int.TryParse(a[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int exp) || exp == 0)
                        errors.Add($"IFCDERIVEDUNITELEMENT #{id} is malformed: '{rawArgs}'.");
                    break;
            }
        }
        return errors;
    }

    private static bool TryParseRef(string token, out int id)
    {
        token = token.Trim();
        if (token.Length >= 2 && token[0] == '#')
            return int.TryParse(token.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        id = 0;
        return false;
    }

    private static bool TryParseRefList(string token, out List<int> ids)
    {
        ids = [];
        token = token.Trim();
        if (token.Length < 3 || token[0] != '(' || token[^1] != ')') return false;
        foreach (string item in IfcExporter.SplitStepArguments(token[1..^1]))
        {
            if (!TryParseRef(item, out int id)) return false;
            ids.Add(id);
        }
        return true;
    }

    private static RevitFileInfo BuildPopulatedModelWithThermalAndPower()
    {
        var info = BuildSampleModel("autodesk.unit.unit:feetFractionalInches-1.0.0");
        var wall = info.Elements.Single(e => e.Id == 100);
        wall.SerializedSystemTypeId = 200;
        var wallType = info.Elements.Single(e => e.Id == 200);
        wallType.SerializedSystemTypeClassId = 512;
        wallType.SerializedHostTypeLayers = [new RawCompoundStructureLayer(BitConverter.DoubleToInt64Bits(1.0), 4, 0, 300, -1, 0, 0)];
        wallType.Layers = [new MaterialLayer { MaterialId = 300, Function = "Structure", ThicknessMm = 304.8 }];
        info.Elements.AddRange(
        [
            new Element
            {
                Id = 300, Name = "Concrete", CategoryId = -2000700, Category = "OST_Materials",
                References = [300, 301],
            },
            new Element
            {
                Id = 301, Name = "Concrete Thermal Asset", CategoryId = -2000924, Category = "OST_PropertySetElement",
                Parameters =
                [
                    new Parameter { Id = -1152320, Definition = new Definition { Name = "THERMAL_MATERIAL_PARAM_EMISSIVITY" }, StorageType = StorageType.Double, Value = 0.9 },
                    new Parameter { Id = -1152308, Definition = new Definition { Name = "THERMAL_MATERIAL_PARAM_THERMAL_CONDUCTIVITY" }, StorageType = StorageType.Double, Value = 1.0 },
                ],
            },
            new Element
            {
                Id = 400, Name = "Downlight Type", CategoryId = -2001120, Category = "OST_LightingFixtures",
                References = [-1010500],
                Parameters =
                [
                    new Parameter { Id = -1010500, Definition = new Definition { Name = "RBS_LIGHTING_FIXTURE_WATTAGE" }, StorageType = StorageType.String, Value = "" },
                ],
            },
            new Element
            {
                Id = 401, Name = "Downlight 1", CategoryId = -2001120, Category = "OST_LightingFixtures",
                TypeId = 400, NativeMasterSymbolId = 400, IsDiscovered = false, References = [3, 10],
                BoundingBox = wall.BoundingBox,
                Geometry = wall.Geometry,
                Parameters =
                [
                    new Parameter { Id = -1002050, Definition = new Definition { Name = "ELEM_TYPE_PARAM" }, StorageType = StorageType.ElementId, Value = 400 },
                    new Parameter { Id = -1012100, Definition = new Definition { Name = "PHASE_CREATED" }, StorageType = StorageType.ElementId, Value = 3 },
                ],
            },
        ]);
        return info;
    }

    private static (int ExitCode, string StdErr) RunCli(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(CliExitCodeTests.DotnetExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RvtIO.Cli.dll"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "The tool did not end within two minutes.");
        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stderr.Result);
    }

    private static RevitFileInfo BuildSampleModel(string storedUnit)
    {
        double[][] p =
        [
            [2, 3, 10], [12, 3, 10], [12, 4, 10], [2, 4, 10],
            [2, 3, 20], [12, 3, 20], [12, 4, 20], [2, 4, 20],
        ];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        var geometry = new GeometryData
        {
            BoundingBox = [2, 3, 10, 12, 4, 20],
            Faces = faces.Select(idx => new GeoFace { OuterVertices = idx.Select(i => p[i]).ToList(), PlaneSurface = true }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
        var info = new RevitFileInfo
        {
            SerializedLengthUnit = storedUnit,
            SerializedProjectPhaseIds = [3],
            SerializedDefaultExportPhaseId = 3,
        };
        info.Elements.AddRange(
        [
            new Element { Id = 3, Name = "New Construction", CategoryId = -2000112, Category = "OST_Phases" },
            new Element
            {
                Id = 10, Name = "Level 2", CategoryId = -2000240, Category = "OST_Levels", IsNativeLevel = true,
                SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
                Parameters =
                [
                    new Parameter { Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = 10.0 },
                    new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
                ],
            },
            new Element
            {
                Id = 100, Name = "Basic Wall:Generic - 300mm:100", CategoryId = -2000011, Category = "OST_Walls",
                TypeId = 200, IsDiscovered = false, References = [3, 10],
                BoundingBox = [2, 3, 10, 12, 4, 20],
                Geometry = geometry,
                Location = new LocationCurve { CurveType = "Line", StartPoint = [2, 3.5, 10], EndPoint = [12, 3.5, 10], Direction = [1, 0, 0], Length = 10.0 },
                Parameters =
                [
                    new Parameter { Id = -1012100, Definition = new Definition { Name = "PHASE_CREATED" }, StorageType = StorageType.ElementId, Value = 3 },
                    new Parameter { Id = -1001107, Definition = new Definition { Name = "WALL_BASE_CONSTRAINT" }, StorageType = StorageType.ElementId, Value = 10 },
                    new Parameter { Id = -1001105, Definition = new Definition { Name = "WALL_USER_HEIGHT_PARAM" }, StorageType = StorageType.Double, Value = 10.0, SpecType = "autodesk.spec.aec:length-2.0.0", UnitSymbol = "mm", ConversionFactor = 304.8 },
                    new Parameter { Id = -1004005, Definition = new Definition { Name = "CURVE_ELEM_LENGTH" }, StorageType = StorageType.Double, Value = 10.0 },
                    new Parameter { Id = -1012805, Definition = new Definition { Name = "HOST_AREA_COMPUTED" }, StorageType = StorageType.Double, Value = 100.0 },
                    new Parameter { Id = -1012806, Definition = new Definition { Name = "HOST_VOLUME_COMPUTED" }, StorageType = StorageType.Double, Value = 100.0 },
                ],
            },
            new Element
            {
                Id = 200, Name = "Generic - 300mm", CategoryId = -2000011, Category = "OST_Walls",
                Layers = [new MaterialLayer { Function = "Structure", ThicknessMm = 304.8 }],
                Parameters =
                [
                    new Parameter { Id = -1001000, Definition = new Definition { Name = "WALL_ATTR_WIDTH_PARAM" }, StorageType = StorageType.Double, Value = 1.0, SpecType = "autodesk.spec.aec:length-2.0.0", UnitSymbol = "mm", ConversionFactor = 304.8 },
                ],
            },
        ]);
        return info;
    }

    private sealed record PhysicalMetrics(
        List<double[]> Points, List<double> StoreyElevations, List<double> Depths, List<double> Lengths,
        List<double> Areas, List<double> Volumes, double Precision);

    private static PhysicalMetrics ExtractPhysicalMetricsInSi(string ifc, IfcUnitFamily family)
    {
        double lengthToMetres = family switch
        {
            IfcUnitFamily.Millimetre => 0.001,
            IfcUnitFamily.Metre => 1.0,
            IfcUnitFamily.Foot => IfcLengthUnit.MetresPerFoot,
            IfcUnitFamily.Centimetre => 0.01,
            IfcUnitFamily.Inch => 0.0254,
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
        double areaToSqMetres = family is IfcUnitFamily.Millimetre or IfcUnitFamily.Metre ? 1.0 : lengthToMetres * lengthToMetres;
        double volumeToCuMetres = family is IfcUnitFamily.Millimetre or IfcUnitFamily.Metre ? 1.0 : areaToSqMetres * lengthToMetres;

        var points = new List<double[]>();
        var elevations = new List<double>();
        var depths = new List<double>();
        var lengths = new List<double>();
        var areas = new List<double>();
        var volumes = new List<double>();
        double precision = 0;

        foreach (string rawLine in ifc.Split('\n'))
        {
            string line = rawLine.Trim();
            int eq = line.IndexOf('=');
            int open = eq > 0 ? line.IndexOf('(', eq) : -1;
            if (open < 0 || !line.EndsWith(");", StringComparison.Ordinal)) continue;
            string type = line[(eq + 1)..open].Trim();
            string args = line[(open + 1)..^2];
            switch (type)
            {
                case "IFCCARTESIANPOINT" when args.StartsWith('(') && args.EndsWith(')'):
                    points.Add(args[1..^1].Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture) * lengthToMetres).ToArray());
                    break;
                case "IFCBUILDINGSTOREY":
                {
                    string last = args[(args.LastIndexOf(',') + 1)..];
                    if (double.TryParse(last, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double elev))
                        elevations.Add(elev * lengthToMetres);
                    break;
                }
                case "IFCGEOMETRICREPRESENTATIONCONTEXT":
                    precision = double.Parse(IfcExporter.SplitStepArguments(args)[3], CultureInfo.InvariantCulture) * lengthToMetres;
                    break;
                case "IFCEXTRUDEDAREASOLID":
                    depths.Add(double.Parse(IfcExporter.SplitStepArguments(args)[3], CultureInfo.InvariantCulture) * lengthToMetres);
                    break;
                case "IFCQUANTITYLENGTH":
                    lengths.Add(double.Parse(args[(args.LastIndexOf(',') + 1)..], System.Globalization.CultureInfo.InvariantCulture) * lengthToMetres);
                    break;
                case "IFCQUANTITYAREA":
                    areas.Add(double.Parse(args[(args.LastIndexOf(',') + 1)..], System.Globalization.CultureInfo.InvariantCulture) * areaToSqMetres);
                    break;
                case "IFCQUANTITYVOLUME":
                    volumes.Add(double.Parse(args[(args.LastIndexOf(',') + 1)..], System.Globalization.CultureInfo.InvariantCulture) * volumeToCuMetres);
                    break;
            }
        }
        return new PhysicalMetrics(points, elevations, depths, lengths, areas, volumes, precision);
    }

    private static void AssertListsClose(List<double> expected, List<double> actual, double relativeTolerance)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            double scale = Math.Max(1.0, Math.Max(Math.Abs(expected[i]), Math.Abs(actual[i])));
            Assert.True(Math.Abs(expected[i] - actual[i]) <= relativeTolerance * scale,
                $"At {i}: expected {expected[i]:G17}, actual {actual[i]:G17}");
        }
    }

    private static void AssertPointListsClose(List<double[]> expected, List<double[]> actual, double relativeTolerance)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Length, actual[i].Length);
            for (int j = 0; j < expected[i].Length; j++)
            {
                double scale = Math.Max(1.0, Math.Max(Math.Abs(expected[i][j]), Math.Abs(actual[i][j])));
                Assert.True(Math.Abs(expected[i][j] - actual[i][j]) <= relativeTolerance * scale,
                    $"Point {i}[{j}]: expected {expected[i][j]:G17}, actual {actual[i][j]:G17}");
            }
        }
    }
}
