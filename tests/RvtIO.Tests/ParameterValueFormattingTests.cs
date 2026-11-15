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

        var paramEmptySymbol = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 0.85,
            UnitSymbol = "",
            ConversionFactor = 1.0,
            Definition = new Definition { Name = "Factor Param" }
        };

        Assert.Equal("0.85", paramEmptySymbol.AsValueString());
    }

    [Fact]
    public void Parameter_AsValueString_DimensionlessCoefficientsAndSpecsRemainScalar()
    {
        // TEXT_WIDTH_SCALE: BIP contains "WIDTH", but represents a dimensionless scale factor
        var textWidthScale = new Parameter
        {
            Id = -1006327,
            StorageType = StorageType.Double,
            Value = 0.85,
            Definition = new Definition
            {
                Id = -1006327,
                Name = "Text scaling factor",
                BuiltInParameter = "TEXT_WIDTH_SCALE"
            }
        };
        Assert.Equal("0.85", textWidthScale.AsValueString());

        // Number-spec parameter named "Length factor"
        var lengthFactorWithSpec = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 1.25,
            SpecType = "autodesk.spec:number-1.0.0",
            Definition = new Definition { Name = "Length factor" }
        };
        Assert.Equal("1.25", lengthFactorWithSpec.AsValueString());

        // Name indicating a factor or ratio without explicit spec
        var lengthFactorNoSpec = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 1.25,
            Definition = new Definition { Name = "Length factor" }
        };
        Assert.Equal("1.25", lengthFactorNoSpec.AsValueString());

        var aspectRatio = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 1.777,
            Definition = new Definition { Name = "Height aspect ratio", BuiltInParameter = "ASPECT_RATIO" }
        };
        Assert.Equal("1.777", aspectRatio.AsValueString());

        var dischargeCoeff = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 0.62,
            Definition = new Definition { Name = "Width discharge coefficient" }
        };
        Assert.Equal("0.62", dischargeCoeff.AsValueString());
    }
}
