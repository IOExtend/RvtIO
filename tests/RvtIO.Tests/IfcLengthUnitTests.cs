using RvtIO.Ifc;
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
        Assert.DoesNotContain("SQUARE_METRE", file);
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
    [InlineData("m", IfcUnitFamily.Metre)]
    [InlineData("metre", IfcUnitFamily.Metre)]
    [InlineData("meter", IfcUnitFamily.Metre)]
    [InlineData("metres", IfcUnitFamily.Metre)]
    [InlineData("meters", IfcUnitFamily.Metre)]
    [InlineData("ft", IfcUnitFamily.Foot)]
    [InlineData("FT", IfcUnitFamily.Foot)]
    [InlineData("foot", IfcUnitFamily.Foot)]
    [InlineData("feet", IfcUnitFamily.Foot)]
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
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-length-unit", "cm" }];
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-length-unit", "centimetre" }];
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-length-unit", "in" }];
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-length-unit", "inch" }];
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
    public void Export_AcrossMillimetreMetreAndFoot_DeclaresMatchingUnitsAndPreservesPhysicalGeometryAndQuantities(IfcProfile profile)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rvtio-unit-equiv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var outputs = new Dictionary<IfcUnitFamily, string>();
            foreach (var family in new[] { IfcUnitFamily.Millimetre, IfcUnitFamily.Metre, IfcUnitFamily.Foot })
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
            Assert.DoesNotContain("SQUARE_METRE", outputs[IfcUnitFamily.Foot]);

            var mmMetrics = ExtractPhysicalMetricsInSi(outputs[IfcUnitFamily.Millimetre], IfcUnitFamily.Millimetre);
            var mMetrics = ExtractPhysicalMetricsInSi(outputs[IfcUnitFamily.Metre], IfcUnitFamily.Metre);
            var ftMetrics = ExtractPhysicalMetricsInSi(outputs[IfcUnitFamily.Foot], IfcUnitFamily.Foot);

            Assert.NotEmpty(mmMetrics.Points);
            Assert.NotEmpty(mmMetrics.StoreyElevations);
            Assert.NotEmpty(mmMetrics.Lengths);
            Assert.NotEmpty(mmMetrics.Areas);
            Assert.NotEmpty(mmMetrics.Volumes);

            AssertListsClose(mmMetrics.StoreyElevations, mMetrics.StoreyElevations, 1e-9);
            AssertListsClose(mmMetrics.StoreyElevations, ftMetrics.StoreyElevations, 1e-9);
            AssertPointListsClose(mmMetrics.Points, mMetrics.Points, 1e-9);
            AssertPointListsClose(mmMetrics.Points, ftMetrics.Points, 1e-9);
            AssertListsClose(mmMetrics.Lengths, mMetrics.Lengths, 1e-9);
            AssertListsClose(mmMetrics.Lengths, ftMetrics.Lengths, 1e-9);
            AssertListsClose(mmMetrics.Areas, mMetrics.Areas, 1e-9);
            AssertListsClose(mmMetrics.Areas, ftMetrics.Areas, 1e-9);
            AssertListsClose(mmMetrics.Volumes, mMetrics.Volumes, 1e-9);
            AssertListsClose(mmMetrics.Volumes, ftMetrics.Volumes, 1e-9);
        }
        finally { Directory.Delete(dir, recursive: true); }
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

            var (badExit, badErr) = RunCli(rvt, Path.Combine(dir, "bad.ifc"), "--ifc-length-unit", "cm");
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
            Assert.Equal(RvtExitCodes.Success, RunCli(rvt, mmPath, "--ifc-length-unit", "mm").ExitCode);
            Assert.Equal(RvtExitCodes.Success, RunCli(rvt, mPath, "--ifc-length-unit", "m").ExitCode);
            Assert.Equal(RvtExitCodes.Success, RunCli(rvt, ftPath, "--ifc-length-unit", "ft").ExitCode);

            Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", File.ReadAllText(mmPath));
            Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);", File.ReadAllText(mPath));
            Assert.Contains("'FOOT'", File.ReadAllText(ftPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
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
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5], Volume = 100.0, SurfaceArea = 240.0 }],
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
                Location = new LocationCurve { Start = [2, 3.5, 10], End = [12, 3.5, 10] },
                Parameters =
                [
                    new Parameter { Id = -1012100, Definition = new Definition { Name = "PHASE_CREATED" }, StorageType = StorageType.ElementId, Value = 3 },
                    new Parameter { Id = -1001107, Definition = new Definition { Name = "WALL_BASE_CONSTRAINT" }, StorageType = StorageType.ElementId, Value = 10 },
                    new Parameter { Id = -1001105, Definition = new Definition { Name = "WALL_USER_HEIGHT_PARAM" }, StorageType = StorageType.Double, Value = 10.0, SpecType = "autodesk.spec.aec:length-2.0.0", UnitSymbol = "mm", ConversionFactor = 304.8 },
                ],
            },
            new Element
            {
                Id = 200, Name = "Generic - 300mm", CategoryId = -2000011, Category = "OST_Walls",
                Layers = [new MaterialLayer { Function = 1, Width = 1.0 }],
                Parameters =
                [
                    new Parameter { Id = -1002050, Definition = new Definition { Name = "WALL_ATTR_WIDTH_PARAM" }, StorageType = StorageType.Double, Value = 1.0, SpecType = "autodesk.spec.aec:length-2.0.0", UnitSymbol = "mm", ConversionFactor = 304.8 },
                ],
            },
        ]);
        return info;
    }

    private sealed record PhysicalMetrics(
        List<double[]> Points, List<double> StoreyElevations, List<double> Lengths, List<double> Areas, List<double> Volumes);

    private static PhysicalMetrics ExtractPhysicalMetricsInSi(string ifc, IfcUnitFamily family)
    {
        double lengthToMetres = family switch
        {
            IfcUnitFamily.Millimetre => 0.001,
            IfcUnitFamily.Metre => 1.0,
            IfcUnitFamily.Foot => IfcLengthUnit.MetresPerFoot,
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
        double areaToSqMetres = family == IfcUnitFamily.Foot ? IfcLengthUnit.SquareMetresPerSquareFoot : 1.0;
        double volumeToCuMetres = family == IfcUnitFamily.Foot ? IfcLengthUnit.CubicMetresPerCubicFoot : 1.0;

        var points = new List<double[]>();
        var elevations = new List<double>();
        var lengths = new List<double>();
        var areas = new List<double>();
        var volumes = new List<double>();

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
        return new PhysicalMetrics(points, elevations, lengths, areas, volumes);
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
