using System.Globalization;
using RvtIO.BuiltIns;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static class ParameterMeasurement
{
    private const double MetresPerFoot = 0.3048;
    private const string SpecPrefix = "autodesk.spec.aec:";
    private const string UnitPrefix = "autodesk.unit.unit:";
    internal const string LegacyFormats = WideBlockWalker.AllItemsKeyPrefix + "legacy-unit-options";
    private sealed record Unit(UnitType Spec, string Input, string Forge, DisplayUnitType? Legacy, string Atom, double ToInternal);
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
        new(UnitType.UT_Length, "ft", "feet", DisplayUnitType.DUT_DECIMAL_FEET, "ft", 1),
        new(UnitType.UT_Length, "in", "inches", DisplayUnitType.DUT_DECIMAL_INCHES, "in", 1.0 / 12),
        new(UnitType.UT_Area, "m2", "squareMeters", DisplayUnitType.DUT_SQUARE_METERS, "m²", 1 / (MetresPerFoot * MetresPerFoot)),
        new(UnitType.UT_Area, "ft2", "squareFeet", DisplayUnitType.DUT_SQUARE_FEET, "ft²", 1),
        new(UnitType.UT_Volume, "m3", "cubicMeters", DisplayUnitType.DUT_CUBIC_METERS, "m³", 1 / (MetresPerFoot * MetresPerFoot * MetresPerFoot)),
        new(UnitType.UT_Volume, "ft3", "cubicFeet", DisplayUnitType.DUT_CUBIC_FEET, "ft³", 1),
        new(UnitType.UT_Angle, "deg", "degrees", DisplayUnitType.DUT_DECIMAL_DEGREES, "°", Math.PI / 180),
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

    private static double? ReadNumber(ReadOnlySpan<char> text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;

    internal readonly record struct DisplayValue(string Type, string Unit, string Symbol, double Before, string After)
    {
        internal bool Matches(string text) => text.EndsWith(Symbol, StringComparison.Ordinal)
            && ReadNumber(text.AsSpan(0, text.Length - Symbol.Length)) == Before;
    }

    internal static DisplayValue? Display(UnitType spec, WideBlockWalker walker, WideBlockWalker.Walk units, double before, double after)
    {
        var records = units.Records.Select(r => (Class: walker.Class(r.ClassId)!.Name, r.Values)).ToArray();
        if (records.Where(r => r.Class == "AUnits").ToArray() is not [var aUnits]
            || aUnits.Values.GetValueOrDefault("m_decimalSymbol") is not 0L)
            return null;
        Unit? unit;
        double accuracy;
        string symbol;
        var additional = AdditionalSpecs.SingleOrDefault(entry => entry.Unit == spec);
        if (WideDocument.UnitOptions(records) is { } modern)
        {
            string specName = additional?.Forge ?? (spec == UnitType.UT_Currency ? "autodesk.spec.measurable:currency"
                : SpecPrefix + spec.ToString()[3..].ToLowerInvariant());
            if (modern.Options.Where(o => UnitSchemaEvaluator.DisplayNameKey(o.Spec) == specName).ToArray() is not [var option]
                || option.UseDefault != 0 || option.RoundingMethod != 0 || option.UseGrouping != 0 || option.UsePlusPrefix != 0)
                return null;
            if ((spec == UnitType.UT_Currency || additional is not null) &&
                (option.SuppressLeadingZeros != 0 || option.SuppressTrailingZeros != 0))
                return null;
            unit = Units.SingleOrDefault(u => u.Spec == spec && UnitPrefix + u.Forge == UnitSchemaEvaluator.DisplayNameKey(option.Unit));
            accuracy = option.Accuracy;
            symbol = UnitSchemaEvaluator.DisplayNameKey(option.Symbol);
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
                || new[] { "m_bUseDefault", "m_roundingMethod", "m_bUseGrouping", "m_bUsePlusPrefix" }.Any(f => option.GetValueOrDefault(f) is not 0L))
                return null;
            if ((spec == UnitType.UT_Currency || additional is not null) && new[] { "m_bSuppressLeadingZeros", "m_bSuppressTrailingZeros" }
                .Any(f => option.GetValueOrDefault(f) is not 0L))
                return null;
            unit = Units.SingleOrDefault(u => u.Spec == spec && u.Legacy == (DisplayUnitType)display);
            accuracy = precision;
            symbol = legacySymbol switch { 0 => "", (int)UnitSymbolType.UST_DEGREE_SYMBOL => "autodesk.unit.symbol:degree", _ => "unsupported" };
        }
        if (unit is null || (symbol != "" && (unit.Input != "deg" || symbol != "autodesk.unit.symbol:degree")))
            return null;
        if (spec == UnitType.UT_Currency && accuracy != 0.01)
            return null;
        int digits = 0;
        while (digits <= 8 && accuracy != Math.Pow(10, -digits))
            digits++;
        if (digits > 8)
            return null;
        string suffix = symbol == "" ? "" : "°";
        double Round(double number) => Math.Round(number / unit.ToInternal, digits, MidpointRounding.AwayFromZero);
        double oldValue = Round(before), newValue = Round(after);
        if (!double.IsFinite(oldValue) || !double.IsFinite(newValue))
            return null;
        if (spec == UnitType.UT_Currency && newValue == 0)
            newValue = 0; // Revit writes 0.00 for amounts rounded to zero on either side.
        return new(additional?.Atom ?? spec.ToString()[3..], unit.Atom, suffix, oldValue,
            newValue.ToString($"F{digits}", CultureInfo.InvariantCulture) + suffix);
    }
}
