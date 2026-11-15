using System.Globalization;
using System.Text.Json;
using RvtIO.Ifc;
using Xunit;

namespace RvtIO.Tests;

public class IfcUnfilledOpeningRegressionTests
{
    [Theory]
    [InlineData(0, IfcUnitFamily.Metre, false)]
    [InlineData(37, IfcUnitFamily.Metre, false)]
    [InlineData(90, IfcUnitFamily.Metre, false)]
    [InlineData(180, IfcUnitFamily.Metre, false)]
    [InlineData(37, IfcUnitFamily.Millimetre, false)]
    [InlineData(37, IfcUnitFamily.Foot, false)]
    [InlineData(0, IfcUnitFamily.Metre, true)]
    public void PublicSampleOpeningMeasuresAllSidesInTheWallFrameAndKeepsItsPlacement(double turn, IfcUnitFamily unit, bool legacy)
    {
        using var stream = typeof(IfcUnfilledOpeningRegressionTests).Assembly
            .GetManifestResourceStream("RvtIO.Tests.Fixtures.issue6-unfilled-opening.json")!;
        using var fixture = JsonDocument.Parse(stream);
        var root = fixture.RootElement;
        var writer = new StepWriter();
        foreach (var entity in root.GetProperty("entities").EnumerateArray())
            Assert.Equal(entity.GetProperty("id").GetInt32(), writer.Add(entity.GetProperty("type").GetString()!, entity.GetProperty("args").GetString()!));

        // Rotate the common parent, preserving the captured opening and host placements relative to it.
        int parent = root.GetProperty("buildingPlacement").GetInt32();
        int parentIndex = Enumerable.Range(0, writer.EntityCount).Single(i => writer.EntityAt(i).Id == parent);
        string[] placement = IfcExporter.SplitStepArguments(writer.EntityAt(parentIndex).Args);
        int axisIndex = Enumerable.Range(0, writer.EntityCount).Single(i => writer.EntityAt(i).Id == int.Parse(placement[1][1..]));
        string[] axis = IfcExporter.SplitStepArguments(writer.EntityAt(axisIndex).Args);
        int direction = writer.Add("IFCDIRECTION", $"({StepWriter.Real(Math.Cos(turn * Math.PI / 180))},{StepWriter.Real(Math.Sin(turn * Math.PI / 180))},0.)");
        axis[2] = $"#{direction}";
        writer.ReplaceArgs(axisIndex, string.Join(',', axis));
        var before = Enumerable.Range(0, writer.EntityCount).Select(writer.EntityAt).ToArray();

        var exporter = new IfcExporter { OpeningQuantitiesSwapped = legacy };
        Assert.Equal(1, exporter.ApplyCurrentBaseQuantities(writer, new Dictionary<int, RvtIO.Model.Element>(),
            new Dictionary<int, int>(), new Dictionary<int, (int, int, string)>(), 0));
        // The fix changes only quantities: no placement, profile, solid, product or relationship is rewritten.
        Assert.Equal(before, Enumerable.Range(0, before.Length).Select(writer.EntityAt));
        writer.RewriteArguments(IfcLengthUnit.Converter(unit, exactQuantities: true));
        double factor = unit == IfcUnitFamily.Millimetre ? 1000 : unit == IfcUnitFamily.Foot ? 1 / .3048 : 1;
        var quantities = Enumerable.Range(0, writer.EntityCount).Select(writer.EntityAt).Where(e => e.Type == "IFCQUANTITYLENGTH")
            .Select(e => IfcExporter.SplitStepArguments(e.Args)).ToDictionary(a => a[0].Trim('\''), a => double.Parse(a[3], CultureInfo.InvariantCulture));
        Assert.Equal(3, quantities.Count);
        foreach (var expected in root.GetProperty(legacy ? "legacy" : "expected").EnumerateObject())
            Assert.Equal(expected.Value.GetDouble() * factor, quantities[expected.Name], 8);
    }
}
