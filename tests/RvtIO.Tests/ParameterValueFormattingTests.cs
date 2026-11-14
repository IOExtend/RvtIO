using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class ParameterValueFormattingTests
{
    [Fact]
    public void Parameter_AsValueString_CaseInsensitiveAndBipFallback()
    {
        var paramLength = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 10.0,
            Definition = new Definition { Name = "Length", BuiltInParameter = "INSTANCE_LENGTH_PARAM" }
        };
        var paramArea = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 10.0,
            Definition = new Definition { Name = "Gross Area" }
        };
        var paramVolume = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 10.0,
            Definition = new Definition { Name = "Total Volume" }
        };
        var paramAngle = new Parameter
        {
            StorageType = StorageType.Double,
            Value = Math.PI,
            Definition = new Definition { Name = "Rotation Angle" }
        };

        Assert.Equal($"{10.0 * 304.8:F1} mm", paramLength.AsValueString());
        Assert.Equal($"{10.0 * 0.09290304:F3} m²", paramArea.AsValueString());
        Assert.Equal($"{10.0 * 0.0283168466:F4} m³", paramVolume.AsValueString());
        Assert.Equal($"{180.0:F2}°", paramAngle.AsValueString());
    }

    [Fact]
    public void Parameter_AsValueString_SpecAndUnitSymbolTakesPrecedence()
    {
        var param = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 5.0,
            UnitSymbol = "mm",
            ConversionFactor = 304.8,
            Definition = new Definition { Name = "Custom Dimension" }
        };

        Assert.Equal($"{5.0 * 304.8:G6} mm", param.AsValueString());
    }
}
