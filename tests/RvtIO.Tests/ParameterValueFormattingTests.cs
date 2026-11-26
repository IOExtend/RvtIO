using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class ParameterValueFormattingTests
{
    [Fact]
    public void ParsedNumericValuesShareExactBoxesAndPreserveTheirTypes()
    {
        var first = BipHelpers.MakeBipParam(-1002108, "HOST_ID_PARAM", StorageType.ElementId, -1, hasNoRevitValue: true);
        var second = BipHelpers.MakeBipParam(-1002108, "HOST_ID_PARAM", StorageType.ElementId, -1, hasNoRevitValue: true);
        Assert.Same(first.Value, second.Value);
        Assert.IsType<int>(first.Value);
        Assert.True(first.HasNoRevitValue);
        Assert.Same(Parameter.BoxParsedValue(0.0), Parameter.BoxParsedValue(0.0));
        Assert.IsType<short>(Parameter.BoxParsedValue((short)1));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(
            Assert.IsType<double>(Parameter.BoxParsedValue(BitConverter.Int64BitsToDouble(long.MinValue)))));

        object callerValue = -1;
        first.Value = callerValue;
        Assert.Same(callerValue, first.Value);
        Assert.False(first.HasNoRevitValue);
        Assert.True(second.HasNoRevitValue);
        Assert.Equal(-1, Assert.IsType<int>(second.Value));
    }

    [Fact]
    public void ExternalIdsCanBeFilledAndEditedIndependentlyOfTheSharedDefinition()
    {
        var guid = Guid.NewGuid();
        var definition = new Parameter.DefinitionData(7, "Width", guid: guid, forgeId: "source");
        var original = new Parameter(definition) { NativeElementId = long.MaxValue };
        var copy = original.ShallowCopy(1);

        copy.Guid = null;
        copy.ForgeId = "override";
        copy.FillMissingExternalIds(definition);
        Assert.Equal("override", copy.ForgeId);
        Assert.Equal(guid, copy.Guid);

        copy.Guid = Guid.Empty;
        copy.ForgeId = null;
        copy.FillMissingExternalIds(definition);
        Assert.Equal("source", copy.ForgeId);
        Assert.Equal(Guid.Empty, copy.Guid);
        Assert.Equal(long.MaxValue, copy.NativeElementId);
        copy.NativeElementId = null;
        Assert.Equal(Guid.Empty, copy.Guid);
        Assert.Equal("source", copy.ForgeId);
        Assert.Equal(long.MaxValue, original.NativeElementId);
        Assert.Equal("source", original.ForgeId);
        Assert.Equal(guid, original.Guid);
    }

    [Fact]
    public void OptionalGuidCanBeUpdatedOrClearedIndependentlyOnAShallowCopy()
    {
        var first = new Parameter();
        Assert.Null(first.Guid);
        first.Guid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var copy = first.ShallowCopy(7.0);
        Assert.Equal(first.Guid, copy.Guid);

        copy.Guid = Guid.Empty;
        Assert.Equal(Guid.Empty, copy.Guid);
        Assert.NotEqual(first.Guid, copy.Guid);
        copy.Guid = null;
        Assert.Null(copy.Guid);
        Assert.NotNull(first.Guid);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void NativeReferenceCanBeClearedWithoutChangingTheCopy(long id)
    {
        var parameter = new Parameter { StorageType = StorageType.ElementId, Value = 7, NativeElementId = id };
        var copy = parameter.ShallowCopy(parameter.Value);
        parameter.NativeElementId = null;
        Assert.Equal(7L, parameter.AsElementId64());
        Assert.Null(parameter.SourceElementId);
        Assert.Equal(id, copy.AsElementId64());
        Assert.Equal(id.ToString(System.Globalization.CultureInfo.InvariantCulture), copy.SourceElementId);
    }

    [Fact]
    public void SharedDisplayUnitsCanBeEditedOnEachParameterIndependently()
    {
        var units = new Parameter.Metadata(SpecType: "length", Symbol: "mm", Factor: 304.8);
        var original = new Parameter();
        original.SetDisplayUnits(units);
        var clone = original.ShallowCopy(7.0);

        clone.UnitSymbol = "cm";
        clone.ConversionFactor = 30.48;
        original.SpecType = null;

        Assert.Equal("length", clone.SpecType);
        Assert.Equal("cm", clone.UnitSymbol);
        Assert.Equal(30.48, clone.ConversionFactor);
        Assert.Null(original.SpecType);
        Assert.Equal("mm", original.UnitSymbol);
        Assert.Equal(304.8, original.ConversionFactor);
    }

    [Fact]
    public void ChangingUnitsPreservesSourceIdsAndChangingIdsPreservesUnits()
    {
        var guid = Guid.Parse("57534d27-cdfe-486a-a5a2-bcc93db60035");
        var definition = new Parameter.DefinitionData(9, "Height", guid: guid, forgeId: "spec");
        var units = new Parameter.Metadata(SpecType: "length", Symbol: "mm", Factor: 304.8);
        var first = new Parameter(definition) { NativeElementId = 1L << 40 };
        first.SetDisplayUnits(units);
        Assert.Equal((guid, "spec", 1L << 40), (first.Guid, first.ForgeId, first.NativeElementId));

        var second = first.ShallowCopy(first.Value);
        second.Guid = null;
        second.ForgeId = null;
        second.UnitSymbol = "cm";
        second.FillMissingExternalIds(definition);
        Assert.Equal((guid, "spec", 1L << 40), (second.Guid, second.ForgeId, second.NativeElementId));
        Assert.Equal(("length", "cm", 304.8), (second.SpecType, second.UnitSymbol, second.ConversionFactor));
        Assert.Equal("mm", first.UnitSymbol);

        var third = new Parameter();
        third.SetDisplayUnits(units);
        third.FillMissingExternalIds(definition);
        Assert.Equal((guid, "spec", "mm"), (third.Guid, third.ForgeId, third.UnitSymbol));
    }

    [Fact]
    public void BuiltInDefinitionsStayIndependentAndShallowCopiesShareTheDefinition()
    {
        const int id = -1004005;
        const string name = "CURVE_ELEM_LENGTH";
        var first = BipHelpers.MakeBipParam(id, name, StorageType.Double, 3.0);
        var second = BipHelpers.MakeBipParam(id, name, StorageType.Double, 5.0);
        first.Id = -1;
        Assert.Equal(id, first.DefinitionId);
        var clone = first.ShallowCopy(7.0);
        Assert.Same(first.Definition, clone.Definition);
        Assert.NotSame(first.Definition, second.Definition);
        first.Definition.Name = "Edited";
        Assert.Equal("Edited", clone.DefinitionName);
        Assert.Equal(name, second.DefinitionName);
        Assert.Equal(name, first.DefinitionBuiltIn);
    }

    [Fact]
    public void UserDefinitionDataPreservesIndependentParameterEdits()
    {
        var guid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var definition = new Parameter.DefinitionData(123, "Width", guid: guid);
        object value = 3.0;
        var first = new Parameter(definition) { Value = value };
        var second = new Parameter(definition) { Value = value };

        first.Definition.Name = "Edited";
        first.Guid = null;
        first.Value = 5.0;

        Assert.Equal("Width", second.Definition.Name);
        Assert.Equal(123, second.Definition.Id);
        Assert.Null(second.Definition.BuiltInParameter);
        Assert.Equal(guid, second.Guid);
        Assert.Equal(3.0, second.AsDouble());
    }

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

    [Fact]
    public void Parameter_AsValueString_WordsContainingRatioSubstringDoNotTriggerScalarHeuristic()
    {
        // OPERATION_HEIGHT contains "RATIO" inside "OPERATION", but is a height (length)
        var operationHeight = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 2.0,
            Definition = new Definition { Name = "OPERATION_HEIGHT" }
        };
        Assert.Equal($"{2.0 * 304.8:F1} mm", operationHeight.AsValueString());

        // CONFIGURATION_WIDTH contains "RATIO" inside "CONFIGURATION", but is a width (length)
        var configurationWidth = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 2.0,
            Definition = new Definition { Name = "CONFIGURATION_WIDTH" }
        };
        Assert.Equal($"{2.0 * 304.8:F1} mm", configurationWidth.AsValueString());
    }

    [Fact]
    public void Parameter_AsValueString_ExplicitLengthSpecTypeFormatsAsLength()
    {
        // User parameter with explicit SpecType of length without pre-populated UnitSymbol
        var userLengthParam = new Parameter
        {
            Id = 54321,
            StorageType = StorageType.Double,
            Value = 2.0,
            SpecType = "autodesk.spec.aec:length-2.0.0",
            Definition = new Definition { Id = 54321, Name = "Custom User Length" }
        };
        Assert.Equal($"{2.0 * 304.8:G6} mm", userLengthParam.AsValueString());

        // User parameter with explicit length spec and name containing "OPERATION_HEIGHT"
        var userOperationHeight = new Parameter
        {
            Id = 54322,
            StorageType = StorageType.Double,
            Value = 2.0,
            SpecType = "autodesk.spec.aec:length-2.0.0",
            Definition = new Definition { Id = 54322, Name = "OPERATION_HEIGHT" }
        };
        Assert.Equal($"{2.0 * 304.8:G6} mm", userOperationHeight.AsValueString());
    }

    [Fact]
    public void Parameter_AsValueString_UnsupportedCompositeSpecifications_RemainUnconverted()
    {
        // Composite dimensions (mass/length, force/length^3, mass/length^2) must not be converted to length or area
        var massPerUnitLengthParam = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 2.0,
            SpecType = "autodesk.spec.aec.structural:massPerUnitLength-2.0.0",
            Definition = new Definition { Name = "Custom value" }
        };
        Assert.Equal("2", massPerUnitLengthParam.AsValueString());

        var areaSpringCoeffParam = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 2.0,
            SpecType = "autodesk.spec.aec.structural:areaSpringCoefficient-2.0.0",
            Definition = new Definition { Name = "Custom value" }
        };
        Assert.Equal("2", areaSpringCoeffParam.AsValueString());

        var massPerUnitAreaParam = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 2.0,
            SpecType = "autodesk.spec.aec.structural:massPerUnitArea-2.0.0",
            Definition = new Definition { Name = "Custom value" }
        };
        Assert.Equal("2", massPerUnitAreaParam.AsValueString());

        // Even with names containing "Length" or "Area", explicit composite specs remain unconverted
        var compositeWithLengthName = new Parameter
        {
            StorageType = StorageType.Double,
            Value = 2.0,
            SpecType = "autodesk.spec.aec.structural:massPerUnitLength-2.0.0",
            Definition = new Definition { Name = "Structural Mass Per Unit Length" }
        };
        Assert.Equal("2", compositeWithLengthName.AsValueString());
    }

    [Fact]
    public void Parameter_AsValueString_ElementId_ResolvesName()
    {
        var targetElement = new Element
        {
            Id = 12345,
            Name = "Exterior Brick",
            Category = "Walls"
        };

        var param = new Parameter
        {
            StorageType = StorageType.ElementId,
            Value = 12345,
            Definition = new Definition { Name = "Base Wall" }
        };

        // Resolved with resolver
        Assert.Equal("Exterior Brick", param.AsValueString(id => id == 12345 ? targetElement : null));

        // Unresolved returns ID
        Assert.Equal("12345", param.AsValueString((Func<int, Element?>?)null));
        Assert.Equal("12345", param.AsValueString(_ => null));

        // Negative ID without element: resolves to category name or string representation
        var catParam = new Parameter
        {
            StorageType = StorageType.ElementId,
            Value = -2000011, // OST_Walls
            Definition = new Definition { Name = "Category Ref" }
        };
        Assert.Equal("Walls", catParam.AsValueString());
        Assert.Equal("Walls, #-2000011", catParam.AsNavisworksValueString());

        // Valid phase with ElementId 0
        var phase0 = new Element
        {
            Id = 0,
            Name = "Existing",
            Category = "Phases"
        };
        var phaseParam = new Parameter
        {
            StorageType = StorageType.ElementId,
            Value = 0,
            Definition = new Definition { Name = "Phase Created" }
        };
        Assert.Equal("Existing", phaseParam.AsValueString(id => id == 0 ? phase0 : null));
        Assert.Equal("Phases \"Existing\", #0", phaseParam.AsNavisworksValueString(id => id == 0 ? phase0 : null));
        // Unresolved phase 0
        Assert.Equal("0", phaseParam.AsValueString());
        Assert.Equal("#0", phaseParam.AsNavisworksValueString());

        // Negative non-category ID returns string representation
        var negParam = new Parameter
        {
            StorageType = StorageType.ElementId,
            Value = -1,
            Definition = new Definition { Name = "Unassigned Ref" }
        };
        Assert.Equal("-1", negParam.AsValueString());
        Assert.Equal("#-1", negParam.AsNavisworksValueString());
    }

    [Fact]
    public void Parameter_AsNavisworksValueString_ElementId_FormatsNamedConstant()
    {
        var targetWall = new Element
        {
            Id = 55443,
            Name = "Timber Clad",
            Category = "Walls"
        };

        var param = new Parameter
        {
            StorageType = StorageType.ElementId,
            Value = 55443,
            Definition = new Definition { Name = "Wall Type" }
        };

        // Matching Autodesk Navisworks Exporter format: {Category} "{Name}", #{Id}
        Assert.Equal("Walls \"Timber Clad\", #55443", param.AsNavisworksValueString(id => id == 55443 ? targetWall : null));

        // When element has no name: {Category}, #{Id}
        var unnamedElement = new Element { Id = 55444, Category = "Floors" };
        var unnamedParam = new Parameter
        {
            StorageType = StorageType.ElementId,
            Value = 55444,
            Definition = new Definition { Name = "Floor Ref" }
        };
        Assert.Equal("Floors, #55444", unnamedParam.AsNavisworksValueString(id => id == 55444 ? unnamedElement : null));

        // When unresolved: #{Id}
        Assert.Equal("#55443", param.AsNavisworksValueString(null));

        // Non-elementId parameter delegates to AsValueString()
        var textParam = new Parameter
        {
            StorageType = StorageType.String,
            Value = "Sample Text",
            Definition = new Definition { Name = "Comments" }
        };
        Assert.Equal("Sample Text", textParam.AsNavisworksValueString());
    }

    [Fact]
    public void RvtDocument_ResolveElementNames_ResolvesPartOriginalParameters()
    {
        var partElement = new Element
        {
            Id = 9901,
            CategoryId = (int)BuiltIns.BuiltInCategory.OST_Parts,
            Parameters = new List<Parameter>
            {
                new()
                {
                    Id = (int)BuiltIns.BuiltInParameter.DPART_ORIGINAL_CATEGORY,
                    Definition = new Definition { Name = "DPART_ORIGINAL_CATEGORY" },
                    StorageType = StorageType.String,
                    Value = "Walls"
                },
                new()
                {
                    Id = (int)BuiltIns.BuiltInParameter.DPART_ORIGINAL_FAMILY,
                    Definition = new Definition { Name = "DPART_ORIGINAL_FAMILY" },
                    StorageType = StorageType.String,
                    Value = "Basic Wall"
                },
                new()
                {
                    Id = (int)BuiltIns.BuiltInParameter.DPART_ORIGINAL_TYPE,
                    Definition = new Definition { Name = "DPART_ORIGINAL_TYPE" },
                    StorageType = StorageType.String,
                    Value = "Generic - 200mm"
                }
            }
        };

        var info = new RevitFileInfo
        {
            Elements = new List<Element> { partElement }
        };

        var doc = RvtDocument.FromInfo(info);
        var resolvedPart = doc.GetElement(9901);

        Assert.NotNull(resolvedPart);
        Assert.Equal("Walls", resolvedPart.Category);
        Assert.Equal("Basic Wall", resolvedPart.FamilyName);
        Assert.Equal("Generic - 200mm", resolvedPart.Name);
    }
}
