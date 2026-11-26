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
    [InlineData(false, false, false, 1.0 / 96, -0.005208333333333333, "-0' - 0 1/8\"", 0.9973958333333334, "1' - 0\"")]
    [InlineData(false, false, true, 1.0 / 192, 0.046875, "9/16\"", -0.000125, "0\"")]
    [InlineData(true, false, false, 0.125, -0.005208333333333334, "-1/8", 0.9947916666666667, "12")]
    [InlineData(true, true, false, 0.125, -0.005208333333333334, "-1/8\"", 0.9947916666666667, "12\"")]
    [InlineData(false, false, false, 0.03125, 0.015625, "0' - 0 3/8\"", 0.984375, "1' - 0\"")]
    [InlineData(true, false, false, 1.5, -0.0625, "-1 1/2", 0.9375, "12")]
    [InlineData(true, false, false, 6, -0.25, "-6", 1.25, "18")]
    [InlineData(false, false, false, 1, -0.5, "-1'", 1.5, "2'")]
    [InlineData(false, false, true, 1, -0.01, "0'", 1.5, "2'")]
    public void FormatsFractionalLengthsInFamilyMetadata(bool inches, bool symbol, bool compactFeet, double accuracy,
        double before, string oldText, double after, string newText)
    {
        foreach (bool legacy in symbol ? new[] { false } : new[] { false, true })
        {
            var layout = FractionalLayout(inches, symbol, legacy, accuracy);
            var options = Assert.Single(layout.Records, record => record.ClassId == FormatClass).Values;
            options["m_bSuppressLeadingZeros"] = compactFeet ? 1L : 0L;
            options["m_bSuppressSpaces"] = compactFeet ? 1L : 0L;
            var display = ParameterMeasurement.Display(UnitType.UT_Length, Walker, layout, before, after);
            Assert.True(display.HasValue);
            foreach (string? unit in inches && !legacy && !symbol ? new string?[] { "\"", null }
                         : new[] { inches && !legacy ? "\"" : null })
            {
                string suffix = inches && unit is null ? "\"" : "";
                var stored = display.Value.ForStoredUnit(unit);
                Assert.True(stored.MatchesUnit(unit));
                Assert.False(stored.MatchesUnit("in"));
                Assert.True(stored.Matches(oldText + suffix));
                Assert.False(stored.Matches(oldText + suffix + " "));
                Assert.Equal(newText + suffix, stored.After);
            }
        }
    }

    [Theory]
    [InlineData(1, 0, "22.1250345", "22° 07' 30\"", "-13.3754", "-13° 22' 31\"")]
    [InlineData(0.01, 1, "22.1250345", "22° 07' 30,12\"", "-0.000125", "-0° 00' 00,45\"")]
    [InlineData(1, 0, "-0.000125", "0° 00' 00\"", "12.99986111111111", "13° 00' 00\"")]
    [InlineData(60, 0, "22.1250345", "22° 08' 00\"", "-13.3754", "-13° 23' 00\"")]
    [InlineData(3600, 0, "-13.3754", "-13° 00' 00\"", "12.5", "13° 00' 00\"")]
    public void FormatsDegreesMinutesAndSeconds(double stepSeconds, long decimalSymbol, string before, string oldText,
        string after, string newText)
    {
        foreach (bool legacy in new[] { false, true })
        {
            var options = DisplayUnits(UnitType.UT_Angle, "degreesMinutes", DisplayUnitType.DUT_DEGREES_AND_MINUTES,
                "", UnitSymbolType.UST_NONE, legacy);
            Assert.Single(options.Records, record => record.ClassId == FormatClass).Values["m_accuracy"] = stepSeconds / 3600;
            Assert.Single(options.Records, record => record.ClassId == UnitsClass).Values["m_decimalSymbol"] = decimalSymbol;
            var display = ParameterMeasurement.Display(UnitType.UT_Angle, Walker, options,
                ParameterMeasurement.Parse(UnitType.UT_Angle, before + " deg")!.Value,
                ParameterMeasurement.Parse(UnitType.UT_Angle, after + " deg")!.Value);
            Assert.True(display.HasValue);
            Assert.Equal("Angle", display.Value.Type);
            Assert.True(display.Value.MatchesUnit(null));
            Assert.False(display.Value.MatchesUnit("°"));
            Assert.True(display.Value.Matches(oldText));
            Assert.False(display.Value.Matches(oldText + " "));
            Assert.Equal(newText, display.Value.After);
        }
    }

    [Theory]
    [InlineData(1e-13, "m_bUseGrouping", 0L)]
    [InlineData(1.0 / 3600, "m_bUseGrouping", 1L)]
    [InlineData(1.0 / 3600, "m_bSuppressSpaces", 1L)]
    public void RefusesUnverifiedDegreesMinutesOptions(double accuracy, string field, long value)
    {
        var layout = DisplayUnits(UnitType.UT_Angle, "degreesMinutes", DisplayUnitType.DUT_DEGREES_AND_MINUTES,
            "", UnitSymbolType.UST_NONE, legacy: true);
        var options = Assert.Single(layout.Records, record => record.ClassId == FormatClass).Values;
        options["m_accuracy"] = accuracy;
        options[field] = value;
        Assert.Null(ParameterMeasurement.Display(UnitType.UT_Angle, Walker, layout, 1, 2));
    }

    [Theory]
    [InlineData(0, "m_bUseGrouping", 0L)]
    [InlineData(0.3, "m_bUseGrouping", 0L)]
    [InlineData(0.125, "m_bUseGrouping", 2L)]
    [InlineData(0.125, "m_bSuppressTrailingZeros", 1L)]
    [InlineData(0.125, "m_bSuppressSpaces", 2L)]
    public void RefusesUnverifiedFractionalOptions(double accuracy, string field, long value)
    {
        var layout = FractionalLayout(inches: true, symbol: false, legacy: true, accuracy);
        Assert.Single(layout.Records, record => record.ClassId == FormatClass).Values[field] = value;
        Assert.Null(ParameterMeasurement.Display(UnitType.UT_Length, Walker, layout, 1, 2));
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

    // Expected feet (or square or cubic feet) from the definition of the foot as 0.3048 m, computed outside the code under test.
    [Theory]
    [InlineData(UnitType.UT_Length, "1 mm", 0.0032808398950131233)]
    [InlineData(UnitType.UT_Length, "2.5 cm", 0.08202099737532809)]
    [InlineData(UnitType.UT_Length, "1 m", 3.2808398950131235)]
    [InlineData(UnitType.UT_Length, "-3 m", -9.84251968503937)]
    [InlineData(UnitType.UT_Length, "1 ft", 1)]
    [InlineData(UnitType.UT_Length, "  2.5\tmm ", 0.008202099737532808)]
    [InlineData(UnitType.UT_Area, "1 m2", 10.763910416709722)]
    [InlineData(UnitType.UT_Area, "4 m²", 43.05564166683889)]
    [InlineData(UnitType.UT_Area, "3 ft2", 3)]
    [InlineData(UnitType.UT_Area, "3 ft²", 3)]
    [InlineData(UnitType.UT_Volume, "1 m3", 35.31466672148859)]
    [InlineData(UnitType.UT_Volume, "0.5 m³", 17.657333360744296)]
    [InlineData(UnitType.UT_Volume, "2 ft3", 2)]
    [InlineData(UnitType.UT_Volume, "2 ft³", 2)]
    [InlineData(UnitType.UT_Angle, "0.5 rad", 0.5)]
    [InlineData(UnitType.UT_Angle, "-2 rad", -2)]
    [InlineData(UnitType.UT_Angle, "90 deg", 1.5707963267948966)]
    [InlineData(UnitType.UT_Angle, "90 °", 1.5707963267948966)]
    [InlineData(UnitType.UT_Currency, "12.5", 12.5)]
    [InlineData(UnitType.UT_Currency, "-0.25", -0.25)]
    [InlineData(UnitType.UT_Number, "1e3", 1000)]
    [InlineData(UnitType.UT_Number, " 7 ", 7)]
    public void UnitsAreConvertedToTheInternalValueRevitStores(UnitType spec, string input, double expected)
    {
        double? parsed = ParameterMeasurement.Parse(spec, input);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.Value, Math.Abs(expected) * 1e-12);
    }

    [Theory]
    [InlineData(UnitType.UT_Length, "")]
    [InlineData(UnitType.UT_Length, "5")]
    [InlineData(UnitType.UT_Length, "5 furlongs")]
    [InlineData(UnitType.UT_Length, "5 m2")]
    [InlineData(UnitType.UT_Length, "abc m")]
    [InlineData(UnitType.UT_Length, "NaN m")]
    [InlineData(UnitType.UT_Length, "1e400 m")]
    [InlineData(UnitType.UT_Length, "1e308 m")]
    [InlineData(UnitType.UT_Area, "5 m")]
    [InlineData(UnitType.UT_Volume, "5 ft2")]
    [InlineData(UnitType.UT_Angle, "5 mm")]
    [InlineData(UnitType.UT_Currency, "12.5 $")]
    [InlineData(UnitType.UT_Currency, "1e400")]
    [InlineData(UnitType.UT_Number, "12,5")]
    [InlineData(UnitType.UT_Number, "NaN")]
    public void RefusesAValueWithoutAKnownUnitOrOutsideTheDoubleRange(UnitType spec, string input) =>
        Assert.Null(ParameterMeasurement.Parse(spec, input));

    [Theory]
    [InlineData(1E-05)]
    [InlineData(-2.5E-07)]
    [InlineData(1.5E+21)]
    [InlineData(1E+30)]
    public void RefusesNumbersWhoseMetadataTextNeedsAnExponent(double value)
    {
        Assert.Null(ParameterMeasurement.PlainNumber(value, System.Globalization.NumberFormatInfo.InvariantInfo));
        var layout = DisplayUnits(UnitType.UT_Number, "", DisplayUnitType.DUT_GENERAL, "", UnitSymbolType.UST_NONE, legacy: false);
        Assert.Null(ParameterMeasurement.Display(UnitType.UT_Number, Walker, layout, 1, value));
    }

    [Theory]
    [InlineData(0.0001, "0.0001")]
    [InlineData(123456789012345d, "123456789012345")]
    [InlineData(-12.5, "-12.5")]
    public void WritesNumbersThatNeedNoExponentAsTheirShortestRoundTripText(double value, string expected)
    {
        Assert.Equal(expected, ParameterMeasurement.PlainNumber(value, System.Globalization.NumberFormatInfo.InvariantInfo));
        var layout = DisplayUnits(UnitType.UT_Number, "", DisplayUnitType.DUT_GENERAL, "", UnitSymbolType.UST_NONE, legacy: false);
        Assert.Equal(expected, ParameterMeasurement.Display(UnitType.UT_Number, Walker, layout, 1, value)?.After);
    }

    [Theory]
    [InlineData(false, 0, 1, 1, 12345.625, 999.9973958333334, "12,345' - 7 1/2\"", "1,000' - 0\"")]
    [InlineData(false, 0, 0, 1, 12345.625, 999.9973958333334, "12.345' - 7 1/2\"", "1.000' - 0\"")]
    [InlineData(false, 1, 3, 0, 12345678.625, 999.9973958333334, "1'23'45'678' - 7 1/2\"", "1'000' - 0\"")]
    [InlineData(true, 1, 2, 1, 1028.8020833333333, 83.328125, "12 345 5/8", "1 000")]
    [InlineData(true, 1, 1, 1, 1028.8020833333333, 83.328125, "12,345 5/8", "1,000")]
    public void GroupsWholeUnitsInFractionalLengths(bool inches, long decimalSymbol, long groupSymbol, long groupSize,
        double before, double after, string oldText, string newText)
    {
        foreach (bool legacy in new[] { false, true })
        {
            var layout = FractionalLayout(inches, symbol: false, legacy, inches ? 0.125 : 1.0 / 96);
            SetGrouping(layout, decimalSymbol, groupSymbol, groupSize);
            var display = ParameterMeasurement.Display(UnitType.UT_Length, Walker, layout, before, after);
            Assert.True(display.HasValue);
            string? unit = inches && !legacy ? "\"" : null;
            string suffix = inches && legacy ? "\"" : "";
            var stored = display.Value.ForStoredUnit(unit);
            Assert.True(stored.MatchesUnit(unit));
            Assert.True(stored.Matches(oldText + suffix));
            Assert.False(stored.Matches(oldText + suffix + " "));
            Assert.Equal(newText + suffix, stored.After);
        }
    }

    [Theory]
    [InlineData(1, 0, 1, "12.345.678,63", "0,00")]
    [InlineData(1, 2, 1, "12 345 678,63", "0,00")]
    [InlineData(1, 3, 0, "1'23'45'678,63", "0,00")]
    [InlineData(0, 1, 1, "12,345,678.63", "0.00")]
    [InlineData(0, 0, 1, "12.345.678.63", "0.00")]
    [InlineData(1, 1, 1, "12,345,678,63", "0,00")]
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

    private static WideBlockWalker.Walk FractionalLayout(bool inches, bool symbol, bool legacy, double accuracy)
    {
        var layout = DisplayUnits(UnitType.UT_Length, inches ? "fractionalInches" : "feetFractionalInches",
            inches ? DisplayUnitType.DUT_FRACTIONAL_INCHES : DisplayUnitType.DUT_FEET_FRACTIONAL_INCHES,
            symbol ? "inchDoubleQuote" : "", UnitSymbolType.UST_NONE, legacy);
        Assert.Single(layout.Records, record => record.ClassId == FormatClass).Values["m_accuracy"] = accuracy;
        return layout;
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
