using System.Globalization;
using RvtIO.BuiltIns;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static class ParameterMeasurement
{
    private const double MetresPerFoot = 0.3048;
    private const double InternalFeetPerInch = 0.08333333333333334; // Revit's stored conversion differs from 1.0 / 12.
    private const double RadiansPerDegree = 3.14159265358979 / 180; // The Revit API uses this precision for degree assignments.
    private const string SpecPrefix = "autodesk.spec.aec:";
    private const string UnitPrefix = "autodesk.unit.unit:";
    private const string SymbolPrefix = "autodesk.unit.symbol:";
    private const string FeetFractionalInches = "feetFractionalInches";
    private const string FractionalInches = "fractionalInches";
    private const string DegreesMinutes = "degreesMinutes";
    private static readonly NumberFormatInfo DecimalComma = NumberFormatInfo.ReadOnly(new() { NumberDecimalSeparator = "," });
    internal const string LegacyFormats = WideBlockWalker.AllItemsKeyPrefix + "legacy-unit-options";
    private sealed record Unit(UnitType Spec, string? Input, string Forge, DisplayUnitType? Legacy, string? Atom, double ToInternal,
        string? AlternateAtom = null);
    private sealed record MeasurableSpec(UnitType Unit, string Forge, string Atom);

    private static readonly MeasurableSpec[] AdditionalSpecs =
    [
        new(UnitType.UT_Mass, "autodesk.spec.aec.structural:mass", "Mass"),
        new(UnitType.UT_Force, "autodesk.spec.aec.structural:force", "Force"),
        new(UnitType.UT_MassDensity, SpecPrefix + "massDensity", "Mass Density"),
        new(UnitType.UT_Speed, SpecPrefix + "speed", "Speed"),
        new(UnitType.UT_Slope, SpecPrefix + "slope", "Slope"),
        new(UnitType.UT_TimeInterval, SpecPrefix + "time", "Time"),
    ];

    private static readonly Unit[] Units =
    [
        new(UnitType.UT_Length, "mm", "millimeters", DisplayUnitType.DUT_MILLIMETERS, "mm", 0.001 / MetresPerFoot),
        new(UnitType.UT_Length, "cm", "centimeters", DisplayUnitType.DUT_CENTIMETERS, "cm", 0.01 / MetresPerFoot),
        new(UnitType.UT_Length, "m", "meters", DisplayUnitType.DUT_METERS, "m", 1 / MetresPerFoot),
        new(UnitType.UT_Length, "ft", "feet", DisplayUnitType.DUT_DECIMAL_FEET, "'", 1, "ft"),
        new(UnitType.UT_Length, "in", "inches", DisplayUnitType.DUT_DECIMAL_INCHES, "\"", InternalFeetPerInch, "in"),
        new(UnitType.UT_Length, null, FeetFractionalInches, DisplayUnitType.DUT_FEET_FRACTIONAL_INCHES, null, 1),
        new(UnitType.UT_Length, null, FractionalInches, DisplayUnitType.DUT_FRACTIONAL_INCHES, "\"", InternalFeetPerInch),
        new(UnitType.UT_Area, "m2", "squareMeters", DisplayUnitType.DUT_SQUARE_METERS, "m²", 1 / (MetresPerFoot * MetresPerFoot)),
        new(UnitType.UT_Area, "ft2", "squareFeet", DisplayUnitType.DUT_SQUARE_FEET, "SF", 1, "ft²"),
        new(UnitType.UT_Volume, "m3", "cubicMeters", DisplayUnitType.DUT_CUBIC_METERS, "m³", 1 / (MetresPerFoot * MetresPerFoot * MetresPerFoot)),
        new(UnitType.UT_Volume, "ft3", "cubicFeet", DisplayUnitType.DUT_CUBIC_FEET, "CF", 1, "ft³"),
        new(UnitType.UT_Angle, "deg", "degrees", DisplayUnitType.DUT_DECIMAL_DEGREES, "°", RadiansPerDegree),
        new(UnitType.UT_Angle, null, DegreesMinutes, DisplayUnitType.DUT_DEGREES_AND_MINUTES, null, RadiansPerDegree),
        new(UnitType.UT_Angle, "rad", "radians", null, "rad", 1),
        new(UnitType.UT_Currency, "", "currency", DisplayUnitType.DUT_CURRENCY, "$", 1),
        new(UnitType.UT_Mass, "kg", "kilograms", DisplayUnitType.DUT_KILOGRAMS_MASS, "kg", 1),
        new(UnitType.UT_Force, "kN", "kilonewtons", DisplayUnitType.DUT_KILONEWTONS, "kN", 1000 / MetresPerFoot),
        new(UnitType.UT_MassDensity, "kg/m3", "kilogramsPerCubicMeter", DisplayUnitType.DUT_KILOGRAMS_PER_CUBIC_METER,
            "kg/m³", MetresPerFoot * MetresPerFoot * MetresPerFoot),
        new(UnitType.UT_Speed, "m/s", "metersPerSecond", DisplayUnitType.DUT_METERS_PER_SECOND, "m/s", 1 / MetresPerFoot),
        new(UnitType.UT_Slope, "%", "percentage", DisplayUnitType.DUT_PERCENTAGE, "%", 0.01),
        new(UnitType.UT_TimeInterval, "s", "seconds", DisplayUnitType.DUT_SECONDS, "s", 1),
    ];

    internal static bool IsAdditionalSpec(UnitType? unit) => unit is { } value && AdditionalSpecs.Any(spec => spec.Unit == value);

    internal static UnitType? Spec(WideBlockWalker.Walk definition, int slot, string declaringClass = "ParamDefValue",
        bool includeCurrency = false, bool includeAdditional = false)
    {
        var fields = definition.Captures.Where(c => c.QueueIndex == slot && c.DeclaringClass == declaringClass
            && c.Field is "m_unitType" or "m_specTypeId").ToArray();
        return fields switch
        {
            [{ Field: "m_unitType", Kind: WideBlockWalker.CaptureKind.Integer, Integer: >= 0 and <= 4 and long legacy }] => (UnitType)legacy,
            [{ Field: "m_unitType", Kind: WideBlockWalker.CaptureKind.Integer, Integer: (int)UnitType.UT_Currency }] when includeCurrency
                => UnitType.UT_Currency,
            [{ Field: "m_unitType", Kind: WideBlockWalker.CaptureKind.Integer, Integer: >= 0 and <= int.MaxValue and long legacy }]
                when includeAdditional && IsAdditionalSpec((UnitType)legacy) => (UnitType)legacy,
            [{ Field: "m_specTypeId", Text: string forge }] => UnitSchemaEvaluator.DisplayNameKey(forge) switch
            {
                SpecPrefix + "length" => UnitType.UT_Length,
                SpecPrefix + "area" => UnitType.UT_Area,
                SpecPrefix + "volume" => UnitType.UT_Volume,
                SpecPrefix + "angle" => UnitType.UT_Angle,
                SpecPrefix + "number" => UnitType.UT_Number,
                "autodesk.spec.measurable:currency" when includeCurrency => UnitType.UT_Currency,
                string name when includeAdditional => AdditionalSpecs.SingleOrDefault(spec => spec.Forge == name)?.Unit,
                _ => null,
            },
            _ => null,
        };
    }

    internal static double? Parse(UnitType spec, string input)
    {
        var text = input.AsSpan().Trim();
        if (spec is UnitType.UT_Number or UnitType.UT_Currency)
            return ReadNumber(text);
        int separator = text.LastIndexOfAny(' ', '\t');
        if (separator < 0 || ReadNumber(text[..separator]) is not double number)
            return null;
        string suffix = text[(separator + 1)..].ToString().Replace('²', '2').Replace('³', '3').Replace("°", "deg", StringComparison.Ordinal);
        var unit = Units.SingleOrDefault(unit => unit.Spec == spec && unit.Input == suffix);
        if (unit is null)
            return null;
        double value = number * unit.ToInternal;
        return double.IsFinite(value) ? value : null;
    }

    private static double? ReadNumber(ReadOnlySpan<char> text, NumberFormatInfo? format = null) =>
        double.TryParse(text, NumberStyles.Float, format ?? NumberFormatInfo.InvariantInfo, out double value) && double.IsFinite(value)
            ? value : null;

    /// <summary>The shortest text that reads back as the number, or null when that text is in exponent notation: how Revit writes such
    /// a value into the family metadata has not been verified.</summary>
    internal static string? PlainNumber(double value, NumberFormatInfo format)
    {
        string text = value.ToString("R", format);
        return text.Contains('E') ? null : text;
    }

    internal readonly record struct DisplayValue(string Type, string? Unit, string Symbol, double Before, string After,
        NumberFormatInfo Format, string? AlternateUnit = null, string? ExactBefore = null, bool InchSuffixWithoutUnit = false)
    {
        internal bool MatchesUnit(string? unit) => unit == Unit || (AlternateUnit is not null && unit == AlternateUnit);

        // Older fractional-inch entries carry the quote in the value instead of the units attribute.
        internal DisplayValue ForStoredUnit(string? unit) => InchSuffixWithoutUnit && unit is null
            ? this with { Unit = null, ExactBefore = ExactBefore + "\"", After = After + "\"", InchSuffixWithoutUnit = false }
            : this;

        internal bool Matches(string text) => ExactBefore is not null ? text == ExactBefore
            : text.EndsWith(Symbol, StringComparison.Ordinal) && ReadNumber(text.AsSpan(0, text.Length - Symbol.Length), Format) == Before;

        internal string CurrencyZero => 0d.ToString("F2", Format) + Symbol;
    }

    internal static DisplayValue? Display(UnitType spec, WideBlockWalker walker, WideBlockWalker.Walk units, double before, double after)
    {
        var records = units.Records.Select(r => (Class: walker.Class(r.ClassId)!.Name, r.Values)).ToArray();
        if (records.Where(r => r.Class == "AUnits").ToArray() is not [var aUnits])
            return null;
        var format = aUnits.Values.GetValueOrDefault("m_decimalSymbol") switch
        {
            0L => NumberFormatInfo.InvariantInfo,
            1L => DecimalComma,
            _ => null,
        };
        if (format is null || !double.IsFinite(before) || !double.IsFinite(after))
            return null;
        if (spec == UnitType.UT_Number)
            return PlainNumber(after, format) is { } plain ? new("Number", null, "", before, plain, format) : null;
        Unit? unit;
        double accuracy;
        string symbol;
        bool grouping;
        bool suppressLeadingZeros;
        bool suppressTrailingZeros;
        bool suppressSpaces;
        bool validDisplayFlags = true;
        var additional = AdditionalSpecs.SingleOrDefault(entry => entry.Unit == spec);
        var modern = WideDocument.UnitOptions(records);
        if (modern is not null)
        {
            string specName = additional?.Forge ?? (spec == UnitType.UT_Currency ? "autodesk.spec.measurable:currency"
                : SpecPrefix + spec.ToString()[3..].ToLowerInvariant());
            if (modern.Options.Where(o => UnitSchemaEvaluator.DisplayNameKey(o.Spec) == specName).ToArray() is not [var option]
                || option.UseDefault != 0 || option.RoundingMethod != 0 || option.UsePlusPrefix != 0)
                return null;
            unit = Units.SingleOrDefault(u => u.Spec == spec && UnitPrefix + u.Forge == UnitSchemaEvaluator.DisplayNameKey(option.Unit));
            accuracy = option.Accuracy;
            symbol = UnitSchemaEvaluator.DisplayNameKey(option.Symbol);
            grouping = option.UseGrouping != 0;
            suppressLeadingZeros = option.SuppressLeadingZeros != 0;
            suppressTrailingZeros = option.SuppressTrailingZeros != 0;
            suppressSpaces = option.SuppressSpaces != 0;
        }
        else
        {
            if (records.Any(r => r.Class == "ForgeTypeId")
                || units.Captures.Where(c => c.Key == LegacyFormats + "@" + ((int)spec).ToString(CultureInfo.InvariantCulture)).ToArray()
                    is not [{ Kind: WideBlockWalker.CaptureKind.PointerClass, Integer: > 0 and long tag, QueuePosition: >= 0 } pointer]
                || units.Records.Where(r => r.QueueIndex == pointer.QueuePosition && r.ClassId == tag && walker.Class(r.ClassId)?.Name == "FormatOptions")
                    .ToArray() is not [{ Values: var option }])
                return null;
            if (option.GetValueOrDefault("m_accuracy") is not double precision || option.GetValueOrDefault("m_displayUnits") is not long display
                || option.GetValueOrDefault("m_unitSymbol") is not long legacySymbol
                || option.GetValueOrDefault("m_bUseGrouping") is not (0L or 1L) ||
                new[] { "m_bUseDefault", "m_roundingMethod", "m_bUsePlusPrefix" }.Any(f => option.GetValueOrDefault(f) is not 0L))
                return null;
            unit = Units.SingleOrDefault(u => u.Spec == spec && u.Legacy == (DisplayUnitType)display);
            accuracy = precision;
            symbol = legacySymbol switch
            {
                0 => "",
                (int)UnitSymbolType.UST_DEGREE_SYMBOL => SymbolPrefix + "degree",
                (int)UnitSymbolType.UST_FOOT_SINGLE_QUOTE => SymbolPrefix + "footSingleQuote",
                (int)UnitSymbolType.UST_INCH_DOUBLE_QUOTE => SymbolPrefix + "inchDoubleQuote",
                (int)UnitSymbolType.UST_SF => SymbolPrefix + "sf",
                (int)UnitSymbolType.UST_CF => SymbolPrefix + "cf",
                _ => "unsupported",
            };
            grouping = option.GetValueOrDefault("m_bUseGrouping") is 1L;
            object? leading = option.GetValueOrDefault("m_bSuppressLeadingZeros");
            object? trailing = option.GetValueOrDefault("m_bSuppressTrailingZeros");
            object? spaces = option.GetValueOrDefault("m_bSuppressSpaces");
            suppressLeadingZeros = leading is not 0L;
            suppressTrailingZeros = trailing is not 0L;
            suppressSpaces = spaces is not 0L;
            validDisplayFlags = leading is 0L or 1L && trailing is 0L or 1L && spaces is 0L or 1L;
        }
        if (unit is null)
            return null;
        if (grouping)
        {
            format = GroupedFormat(aUnits.Values, format);
            if (format is null)
                return null;
        }
        if (unit.Forge is FeetFractionalInches or FractionalInches)
        {
            bool inchesOnly = unit.Forge == FractionalInches;
            if (!validDisplayFlags || suppressTrailingZeros
                || (symbol != "" && !(inchesOnly && modern is not null && symbol == SymbolPrefix + "inchDoubleQuote"))
                || (inchesOnly && (suppressLeadingZeros || suppressSpaces)))
                return null;
            string inchSuffix = inchesOnly && symbol == "" ? "" : "\"";
            return DisplayFractionalLength(before, after, accuracy, inchesOnly, suppressLeadingZeros, suppressSpaces,
                modern is null ? null : unit.Atom, format, inchSuffix, grouping);
        }
        if (unit.Forge == DegreesMinutes)
            return validDisplayFlags && symbol == "" && !grouping && !suppressLeadingZeros && !suppressTrailingZeros && !suppressSpaces
                ? DisplayDegreesMinutes(before, after, accuracy, format)
                : null;
        if ((suppressLeadingZeros || suppressTrailingZeros)
            && (spec == UnitType.UT_Currency || additional is not null || unit.AlternateAtom is not null))
            return null;
        string? suffix = (unit.Input, symbol) switch
        {
            (_, "") => "",
            ("deg", SymbolPrefix + "degree") => "°",
            ("ft", SymbolPrefix + "footSingleQuote") => "'",
            ("in", SymbolPrefix + "inchDoubleQuote") => "\"",
            ("ft2", SymbolPrefix + "sf") => " SF",
            ("ft3", SymbolPrefix + "cf") => " CF",
            _ => null,
        };
        if (suffix is null || (suppressSpaces && suffix.StartsWith(' ')))
            return null;
        if (spec == UnitType.UT_Currency && accuracy != 0.01)
            return null;
        int digits = 0;
        while (digits <= 8 && accuracy != Math.Pow(10, -digits))
            digits++;
        if (digits > 8)
            return null;
        double Round(double number) => Math.Round(number / unit.ToInternal, digits, MidpointRounding.AwayFromZero);
        double oldValue = Round(before), newValue = Round(after);
        if (!double.IsFinite(oldValue) || !double.IsFinite(newValue))
            return null;
        string pattern = $"{(grouping ? 'N' : 'F')}{digits}";
        bool normalizeZero = grouping || format == DecimalComma || spec == UnitType.UT_Currency || unit.AlternateAtom is not null;
        string Render(double value) => (normalizeZero && value == 0 ? 0 : value).ToString(pattern, format) + suffix;
        return new(additional?.Atom ?? spec.ToString()[3..], unit.Atom, suffix, oldValue, Render(newValue), format,
            symbol == "" ? unit.AlternateAtom : null, grouping ? Render(oldValue) : null);
    }

    private static DisplayValue? DisplayFractionalLength(double before, double after, double accuracy, bool inchesOnly,
        bool suppressLeadingZeros, bool suppressSpaces, string? atomUnit, NumberFormatInfo format, string inchSuffix, bool grouping)
    {
        const int InchesPerFoot = 12;
        const int TicksPerInch = 1 << 20;
        const int TicksPerFoot = InchesPerFoot * TicksPerInch;
        int ticksPerUnit = inchesOnly ? TicksPerInch : TicksPerFoot;
        long maxTicks = (long)int.MaxValue * ticksPerUnit;
        // Revit stores fractional accuracy in display units on a grid of 1/2^20 inch.
        if (AccuracyTicks(accuracy, ticksPerUnit, maxTicks) is not long ticksPerStep)
            return null;
        bool wholeFeet = !inchesOnly && ticksPerStep % TicksPerFoot == 0;
        string integerPattern = grouping ? "N0" : "D";

        string? oldText = Render(before), newText = Render(after);
        return oldText is not null && newText is not null
            ? new("Length", atomUnit, "", 0, newText, format, ExactBefore: oldText, InchSuffixWithoutUnit: inchesOnly && inchSuffix == "")
            : null;

        string? Render(double feetValue)
        {
            double displayValue = Math.Abs(feetValue) * (inchesOnly ? InchesPerFoot : 1);
            if (displayValue > int.MaxValue || RoundedTicks(displayValue, accuracy, ticksPerStep, maxTicks) is not long ticks)
                return null;
            long wholeInches = ticks / TicksPerInch;
            long feet = inchesOnly ? 0 : wholeInches / InchesPerFoot;
            long inches = inchesOnly ? wholeInches : wholeInches % InchesPerFoot;
            string text = feetValue < 0 && ticks != 0 ? "-" : "";
            if (wholeFeet)
                return text + feet.ToString(integerPattern, format) + "'";
            bool showFeet = !inchesOnly && (feet != 0 || !suppressLeadingZeros);
            long numerator = ticks % TicksPerInch;
            if (showFeet)
                text += feet.ToString(integerPattern, format) + (suppressSpaces ? "'-" : "' - ");
            if (showFeet || inches != 0 || numerator == 0)
                text += inches.ToString(integerPattern, format);
            if (numerator != 0)
            {
                int reducedDenominator = TicksPerInch;
                while ((numerator & 1) == 0)
                {
                    numerator /= 2;
                    reducedDenominator /= 2;
                }
                if (showFeet || inches != 0)
                    text += " ";
                text += FormattableString.Invariant($"{numerator}/{reducedDenominator}");
            }
            return text + inchSuffix;
        }
    }

    private static DisplayValue? DisplayDegreesMinutes(double before, double after, double accuracy, NumberFormatInfo format)
    {
        const long TicksPerSecond = 100_000_000;
        const long TicksPerMinute = 60 * TicksPerSecond;
        const long TicksPerDegree = 60 * TicksPerMinute;
        const long MaxTicks = long.MaxValue / 2;
        // Revit stores the step in degrees on a grid of 10^-8 arcsecond.
        if (AccuracyTicks(accuracy, TicksPerDegree, MaxTicks) is not long ticksPerStep)
            return null;
        int digits = 8;
        for (long decimalTicks = ticksPerStep; digits > 0 && decimalTicks % 10 == 0; decimalTicks /= 10)
            digits--;
        string secondsPattern = digits == 0 ? "00" : "00." + new string('0', digits);
        string? oldText = Render(before), newText = Render(after);
        return oldText is not null && newText is not null
            ? new("Angle", null, "", 0, newText, format, ExactBefore: oldText)
            : null;

        string? Render(double radians)
        {
            double degreesValue = Math.Abs(radians) / RadiansPerDegree;
            if (RoundedTicks(degreesValue, accuracy, ticksPerStep, MaxTicks) is not long ticks)
                return null;
            string sign = radians < 0 && ticks != 0 ? "-" : "";
            long degrees = ticks / TicksPerDegree;
            long minutes = ticks % TicksPerDegree / TicksPerMinute;
            double seconds = (double)(ticks % TicksPerMinute) / TicksPerSecond;
            return FormattableString.Invariant($"{sign}{degrees}° {minutes:00}' ") + seconds.ToString(secondsPattern, format) + "\"";
        }
    }

    private static long? AccuracyTicks(double accuracy, long ticksPerUnit, long maxTicks)
    {
        double encodedTicks = accuracy * ticksPerUnit;
        double integralTicks = Math.Round(encodedTicks);
        return integralTicks >= 1 && integralTicks <= maxTicks
            && (integralTicks == encodedTicks || integralTicks / ticksPerUnit == accuracy)
            ? (long)integralTicks
            : null;
    }

    private static long? RoundedTicks(double displayValue, double accuracy, long ticksPerStep, long maxTicks)
    {
        double steps = displayValue / accuracy;
        if (steps >= (1L << 52))
            return null;
        // The same midpoint tolerance as the Revit fractional display reader.
        long roundedSteps = (long)Math.Floor(steps + 0.5 + 1e-9);
        return roundedSteps <= maxTicks / ticksPerStep ? roundedSteps * ticksPerStep : null;
    }

    private static NumberFormatInfo? GroupedFormat(Dictionary<string, object?> units, NumberFormatInfo format)
    {
        string? separator = units.GetValueOrDefault("m_digitGroupingSymbol") switch
        {
            0L => ".", 1L => ",", 2L => " ", 3L => "'", _ => null,
        };
        int[]? sizes = units.GetValueOrDefault("m_digitGroupingAmount") switch
        {
            0L => [3, 2], 1L => [3], _ => null,
        };
        if (separator is null || sizes is null)
            return null;
        var grouped = (NumberFormatInfo)format.Clone();
        grouped.NumberGroupSeparator = separator;
        grouped.NumberGroupSizes = sizes;
        return grouped;
    }
}
