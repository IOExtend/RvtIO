using RvtIO.BuiltIns;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

public sealed class ParameterMeasurementTests
{
    private const int UnitsClass = 1, FormatClass = 2, TypeIdClass = 3;
    private static readonly WideBlockWalker Walker = new(new SchemaDocument
    {
        Classes = [new(UnitsClass, "AUnits", 0, 1, [], []), new(FormatClass, "FormatOptions", 0, 1, [], []),
            new(TypeIdClass, "ForgeTypeId", 0, 1, [], [])],
    }, (_, _) => null, []);

    [Theory]
    [InlineData(UnitType.UT_Length, "feet", DisplayUnitType.DUT_DECIMAL_FEET, "footSingleQuote", UnitSymbolType.UST_FOOT_SINGLE_QUOTE, "'", "ft", "'")]
    [InlineData(UnitType.UT_Length, "inches", DisplayUnitType.DUT_DECIMAL_INCHES, "inchDoubleQuote", UnitSymbolType.UST_INCH_DOUBLE_QUOTE, "\"", "in", "\"")]
    [InlineData(UnitType.UT_Area, "squareFeet", DisplayUnitType.DUT_SQUARE_FEET, "sf", UnitSymbolType.UST_SF, "SF", "ft²", " SF")]
    [InlineData(UnitType.UT_Volume, "cubicFeet", DisplayUnitType.DUT_CUBIC_FEET, "cf", UnitSymbolType.UST_CF, "CF", "ft³", " CF")]
    public void FormatsDecimalImperialFamilyMetadata(UnitType spec, string unitId, DisplayUnitType legacyUnit,
        string symbolId, UnitSymbolType legacySymbol, string label, string alternate, string suffix)
    {
        bool inches = legacyUnit == DisplayUnitType.DUT_DECIMAL_INCHES;
        foreach (bool legacy in new[] { false, true })
        foreach (bool showSymbol in new[] { false, true })
        {
            string expectedSuffix = showSymbol ? suffix : "";
            var units = DisplayUnits(spec, unitId, legacyUnit, showSymbol ? symbolId : "",
                showSymbol ? legacySymbol : UnitSymbolType.UST_NONE, legacy);
            var display = ParameterMeasurement.Display(spec, Walker, units, inches ? 0.09375000000000001 : 1.125,
                inches ? -1.0416666666666668E-05 : -0.000125);
            Assert.True(display.HasValue);
            Assert.Equal(label, display.Value.Unit);
            Assert.True(display.Value.MatchesUnit(label));
            Assert.Equal(!showSymbol, display.Value.MatchesUnit(alternate));
            Assert.False(display.Value.MatchesUnit(null));
            Assert.False(display.Value.MatchesUnit("m"));
            Assert.True(display.Value.Matches("1.13" + expectedSuffix));
            Assert.Equal("0.00" + expectedSuffix, display.Value.After);
        }
    }

    [Theory]
    [InlineData("1.125 in", 0.09375000000000001)]
    [InlineData("8.5 in", 0.7083333333333334)]
    [InlineData("-2.375 in", -0.19791666666666669)]
    public void InchesAreConvertedToTheValueStoredByRevit(string input, double expected) =>
        Assert.Equal(expected, ParameterMeasurement.Parse(UnitType.UT_Length, input));

    [Theory]
    [InlineData("22.125 deg", 0.38615409700374503)]
    [InlineData("-13.375 deg", -0.23343778745424135)]
    public void DegreesAreConvertedToTheValueStoredByRevit(string input, double expected) =>
        Assert.Equal(expected, ParameterMeasurement.Parse(UnitType.UT_Angle, input));

    [Theory]
    [InlineData(1, 0, 1, "12.345.678,63", "0,00")]
    [InlineData(1, 2, 1, "12 345 678,63", "0,00")]
    [InlineData(1, 3, 0, "1'23'45'678,63", "0,00")]
    [InlineData(0, 1, 1, "12,345,678.63", "0.00")]
    public void FormatsGroupedFamilyMetadata(long decimalSymbol, long groupSymbol, long groupSize, string expected, string zero)
    {
        foreach (bool legacy in new[] { false, true })
        {
            var layout = DisplayUnits(UnitType.UT_Length, "feet", DisplayUnitType.DUT_DECIMAL_FEET, "",
                UnitSymbolType.UST_NONE, legacy);
            SetGrouping(layout, decimalSymbol, groupSymbol, groupSize);
            var display = ParameterMeasurement.Display(UnitType.UT_Length, Walker, layout, 12345678.625, -0.000125);
            Assert.True(display.HasValue);
            Assert.True(display.Value.Matches(expected));
            Assert.False(display.Value.Matches("12345678.63"));
            Assert.Equal(zero, display.Value.After);
        }
    }

    [Fact]
    public void FormatsLocalizedUnitlessNumbersWithoutGroupingOrRounding()
    {
        var layout = DisplayUnits(UnitType.UT_Number, "", DisplayUnitType.DUT_GENERAL, "", UnitSymbolType.UST_NONE, legacy: false);
        SetGrouping(layout, decimalSymbol: 1, groupSymbol: 2, groupSize: 1);
        var display = ParameterMeasurement.Display(UnitType.UT_Number, Walker, layout, 12345.625, -0.000125);
        Assert.True(display.HasValue);
        Assert.True(display.Value.MatchesUnit(null));
        Assert.True(display.Value.Matches("12345,625"));
        Assert.False(display.Value.Matches("12 345,625"));
        Assert.Equal("-0,000125", display.Value.After);
        Assert.Null(ParameterMeasurement.Parse(UnitType.UT_Number, "12345,625"));
    }

    [Theory]
    [InlineData(2, 0, 1)]
    [InlineData(1, 4, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 1, 1)]
    public void RefusesUnverifiedDecimalAndGroupingOptions(long decimalSymbol, long groupSymbol, long groupSize)
    {
        var layout = DisplayUnits(UnitType.UT_Length, "feet", DisplayUnitType.DUT_DECIMAL_FEET, "", UnitSymbolType.UST_NONE, legacy: false);
        SetGrouping(layout, decimalSymbol, groupSymbol, groupSize);
        Assert.Null(ParameterMeasurement.Display(UnitType.UT_Length, Walker, layout, 12345.625, 2));
    }

    [Theory]
    [InlineData("footSingleQuote", false, false)]
    [InlineData("cf", true, false)]
    [InlineData("cf", false, true)]
    public void RefusesUnverifiedImperialPartAtomFormats(string symbol, bool suppressZeros, bool suppressSpaces)
    {
        var units = DisplayUnits(UnitType.UT_Volume, "cubicFeet", DisplayUnitType.DUT_CUBIC_FEET, symbol,
            UnitSymbolType.UST_NONE, legacy: false);
        var format = Assert.Single(units.Records, record => record.ClassId == FormatClass).Values;
        format["m_bSuppressTrailingZeros"] = suppressZeros ? 1L : 0L;
        format["m_bSuppressSpaces"] = suppressSpaces ? 1L : 0L;
        Assert.Null(ParameterMeasurement.Display(UnitType.UT_Volume, Walker, units, 1, 2));
    }

    private static void SetGrouping(WideBlockWalker.Walk layout, long decimalSymbol, long groupSymbol, long groupSize)
    {
        var units = Assert.Single(layout.Records, record => record.ClassId == UnitsClass).Values;
        units["m_decimalSymbol"] = decimalSymbol;
        units["m_digitGroupingSymbol"] = groupSymbol;
        units["m_digitGroupingAmount"] = groupSize;
        Assert.Single(layout.Records, record => record.ClassId == FormatClass).Values["m_bUseGrouping"] = 1L;
    }

    private static WideBlockWalker.Walk DisplayUnits(UnitType spec, string unitId, DisplayUnitType legacyUnit, string symbol,
        UnitSymbolType legacySymbol, bool legacy)
    {
        var layout = new WideBlockWalker.Walk();
        var format = new Dictionary<string, object?>
        {
            ["m_accuracy"] = 0.01, ["m_roundingMethod"] = 0L, ["m_bUseDefault"] = 0L, ["m_bUseGrouping"] = 0L,
            ["m_bUsePlusPrefix"] = 0L, ["m_bSuppressLeadingZeros"] = 0L, ["m_bSuppressTrailingZeros"] = 0L, ["m_bSuppressSpaces"] = 0L,
        };
        if (legacy)
        {
            format["m_displayUnits"] = (long)legacyUnit;
            format["m_unitSymbol"] = (long)legacySymbol;
            layout.Captures.Add(new(ParameterMeasurement.LegacyFormats + "@" + (int)spec, WideBlockWalker.CaptureKind.PointerClass,
                FormatClass, 0, null, true, 0, "AUnits", "m_formatOptionsMap", QueuePosition: 1));
        }
        else
        {
            string name = spec.ToString()[3..].ToLowerInvariant();
            foreach (string id in new[] { $"autodesk.spec.aec:{name}-1.0.0", symbol == "" ? "" : $"autodesk.unit.symbol:{symbol}-1.0.1",
                         $"autodesk.unit.unit:{unitId}-1.0.1" })
                layout.Records.Add((TypeIdClass, 0, new() { ["m_typeId"] = id }));
        }
        layout.Records.Add((FormatClass, 1, format));
        layout.Records.Add((UnitsClass, 0, new()
        {
            ["m_universalUnitSystem"] = 1L, ["m_decimalSymbol"] = 0L, ["m_digitGroupingSymbol"] = 1L, ["m_digitGroupingAmount"] = 0L,
        }));
        return layout;
    }
}
