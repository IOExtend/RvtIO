using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The declared IFC length unit follows the stored Length format option of every unit layout, and an option that cannot be
/// used is reported with its reason.</summary>
public class ProjectLengthUnitTests
{
    private const string LengthSpec = "autodesk.spec.aec:length-1.0.0";

    private static TfBlockDeserializer.UnitOptions Options(params (string Spec, string Unit, byte UseDefault)[] options) =>
        new(options.Select(o => new TfBlockDeserializer.UnitFormatOption(o.Spec, "autodesk.unit.symbol:", o.Unit, 0.001, 0, 0, 0, 0, o.UseDefault, 0, 0)).ToArray(), 0, 0, 0, 0);

    [Theory]
    [InlineData("autodesk.unit.unit:meters-1.0.0", IfcUnitFamily.Metre)]
    [InlineData("autodesk.unit.unit:feetFractionalInches-1.0.0", IfcUnitFamily.Foot)]
    [InlineData("autodesk.unit.unit:millimeters-1.0.1", IfcUnitFamily.Millimetre)]
    public void StoredOption_GivesTheFamilyOfTheProject(string unit, IfcUnitFamily expected)
    {
        var options = Options(("autodesk.spec.aec:area-1.0.0", "autodesk.unit.unit:squareMeters-1.0.1", 0), (LengthSpec, unit, 0));
        Assert.Equal(unit, ProjectLengthUnits.StoredLengthUnit(options, out string? issue));
        Assert.Null(issue);
        Assert.Equal(expected, IfcLengthUnitChoice.Family(unit));
    }

    [Fact]
    public void UnusableOptions_AreNullWithTheirReason()
    {
        Assert.Null(ProjectLengthUnits.StoredLengthUnit(null, out string? issue));
        Assert.Contains("not read", issue);
        Assert.Null(ProjectLengthUnits.StoredLengthUnit(Options(("autodesk.spec.aec:area-1.0.0", "x", 0)), out issue));
        Assert.Contains("no Length", issue);
        Assert.Null(ProjectLengthUnits.StoredLengthUnit(Options((LengthSpec, "autodesk.unit.unit:meters-1.0.0", 1)), out issue));
        Assert.Contains("default", issue);
        Assert.Null(ProjectLengthUnits.StoredLengthUnit(Options((LengthSpec, "", 0)), out issue));
        Assert.Contains("no unit", issue);
    }

    private static IReadOnlyList<(string Class, Dictionary<string, object?> Values)> LegacyRecords(long displayUnit, long useDefault = 0) =>
    [
        ("AUnits", new() { ["m_universalUnitSystem"] = 1L }),
        ("FormatOptions", new() { ["m_displayUnits"] = displayUnit, ["m_bUseDefault"] = useDefault }),
        ("FormatOptions", new() { ["m_displayUnits"] = 12L, ["m_bUseDefault"] = 0L }),
    ];

    [Theory]
    [InlineData(0L, IfcUnitFamily.Metre)]
    [InlineData(2L, IfcUnitFamily.Millimetre)]
    [InlineData(3L, IfcUnitFamily.Foot)]
    [InlineData(4L, IfcUnitFamily.Foot)]
    [InlineData(6L, IfcUnitFamily.Millimetre)]
    [InlineData(9L, IfcUnitFamily.Metre)]      // metres and centimetres
    [InlineData(605L, IfcUnitFamily.Foot)]     // US survey feet
    public void DisplayUnitLayout_ReadsTheFirstOptionAsLength(long displayUnit, IfcUnitFamily expected)
    {
        string? unit = WideDocument.LegacyLengthUnit(LegacyRecords(displayUnit), out string? issue);
        Assert.Null(issue);
        Assert.Equal(expected, IfcLengthUnitChoice.Family(unit));
    }

    [Theory]
    [InlineData(12L, 0L, "not a length unit")]
    [InlineData(0L, 1L, "default")]
    public void DisplayUnitLayout_RejectsAnOptionThatIsNotAUsableLength(long displayUnit, long useDefault, string reason)
    {
        Assert.Null(WideDocument.LegacyLengthUnit(LegacyRecords(displayUnit, useDefault), out string? issue));
        Assert.Contains(reason, issue);
    }

    [Fact]
    public void DisplayUnitLayout_RejectsRecordsOfAnotherShape()
    {
        Assert.Null(WideDocument.LegacyLengthUnit([("AUnits", [])], out string? issue));
        Assert.NotNull(issue);
        Assert.Null(WideDocument.LegacyLengthUnit([("AUnits", []), ("ForgeTypeId", []), ("FormatOptions", [])], out issue));
        Assert.NotNull(issue);
    }

    private const string FeetInches = "autodesk.unit.unit:feetFractionalInches-1.0.0";

    private static TfBlockDeserializer.UnitsElemOptions Elem(uint owner, int family, int access) =>
        new(owner, family, access, 0, 0, Options((LengthSpec, FeetInches, 0)));

    [Theory]
    [InlineData(-1)]
    [InlineData(17)]
    public void SelectedUnitsElem_ServesProjectAndFamilyDocuments(int family)
    {
        var state = new ParsingState { OwnDocumentUnitsElemId = 542 };
        state.OwnUnitOptions[542] = (1, Elem(542, family, 1));
        var options = state.OwnDocumentLengthUnitOptions(out string? issue);
        Assert.Null(issue);
        Assert.Equal(FeetInches, ProjectLengthUnits.StoredLengthUnit(options!.Value, out _));
    }

    [Theory]
    [InlineData(0, 542u, 1, "no decoded")]
    [InlineData(2, 542u, 1, "2 blocks")]
    [InlineData(1, 543u, 1, "malformed")]
    [InlineData(1, 542u, 0, "not document accessible")]
    public void SelectedUnitsElem_RejectsMissingDuplicateForeignAndInaccessible(int count, uint owner, int access, string reason)
    {
        var state = new ParsingState { OwnDocumentUnitsElemId = 542 };
        state.OwnUnitOptions[542] = (count, Elem(owner, 17, access));
        Assert.Null(state.OwnDocumentLengthUnitOptions(out string? issue));
        Assert.Contains(reason, issue);
        state.OwnUnitOptions[542] = (1, null);
        Assert.Null(state.OwnDocumentLengthUnitOptions(out issue));
        Assert.Contains("malformed", issue);
        Assert.Null(new ParsingState().OwnDocumentLengthUnitOptions(out issue));
        Assert.Contains("names no units element", issue);
    }

    [SkippableFact]
    public void FamilyFile_DeclaresTheStoredFoot()
    {
        var info = new RvtFileReader().Read(TestFiles.Find("One Way Sign in Route Analysis.rfa"));
        Assert.Equal(FeetInches, info.SerializedLengthUnit);
        Assert.Null(info.SerializedLengthUnitIssue);
    }

    private static string Export(string? lengthUnit, string? issue, out string diagnostics)
    {
        var diag = new StringWriter();
        var text = new StringWriter();
        new IfcExporter(diag).Export(new RevitFileInfo { SerializedLengthUnit = lengthUnit, SerializedLengthUnitIssue = issue }, text);
        diagnostics = diag.ToString();
        return text.ToString();
    }

    [Fact]
    public void MetreProject_DeclaresMetre()
    {
        string ifc = Export("autodesk.unit.unit:meters-1.0.0", null, out _);
        Assert.Contains("IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)", ifc);
        Assert.DoesNotContain("'FOOT'", ifc);
    }

    [Fact]
    public void FootProject_DeclaresFoot()
    {
        string ifc = Export("autodesk.unit.unit:feet-1.0.1", null, out _);
        Assert.Contains("'FOOT'", ifc);
        Assert.DoesNotContain(".MILLI.,.METRE.", ifc);
    }

    [Fact]
    public void MillimetreProject_DeclaresMillimetre()
    {
        string ifc = Export("autodesk.unit.unit:millimeters-1.0.1", null, out string diagnostics);
        Assert.Contains("IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.)", ifc);
        Assert.DoesNotContain("unreadable", diagnostics);
    }

    [Fact]
    public void UnreadableOption_FallsBackToMillimetresAndSaysWhy()
    {
        string ifc = Export(null, "no Length format option", out string diagnostics);
        Assert.Contains("IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.)", ifc);
        Assert.Contains("unreadable (no Length format option); using the default Millimetre", diagnostics);
    }
}
