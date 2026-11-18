using RvtIO.Parsing;

namespace RvtIO.Ifc;

/// <summary>The numeric length/thermal subset of the units cache of the Revit IFC exporter.
/// This is a plan, not an IFC unit assignment:
/// the existing metre-based geometry writer must not emit these values with a
/// missing unit. Other specs, library-default format options and COBie are separate.</summary>
internal sealed record IfcNumericUnitPlan(
    string LengthOptionSpec,
    string LengthOptionUnit,
    IfcNumericUnitPlan.CachedUnit Length,
    IfcNumericUnitPlan.CachedUnit ThermalTransmittance)
{
    internal const string ThermalSpecName = "autodesk.spec.aec.energy:heatTransferCoefficient";
    private const string UnitPrefix = "autodesk.unit.unit:";
    private const string Feet = UnitPrefix + "feet-1.0.1";
    private const string Metres = UnitPrefix + "meters-1.0.0";
    private const string Thermal = UnitPrefix + "wattsPerSquareMeterKelvin-1.0.1";

    internal sealed record CachedUnit(string RequestedUnitId, string ResolvedUnitId,
        string IfcUnitType, string? SiPrefix, string? ConversionName, double? MetresPerUnit,
        double Scale, double Offset)
    {
        // Multiply and add, subtract and divide respectively.
        // Do not replace this cache operation with ConvertFromInternalUnits(value).
        internal bool TryScale(double value, out double result)
        {
            result = default;
            if (!double.IsFinite(value)) return false;
            double converted = value * Scale + Offset;
            if (!double.IsFinite(converted)) return false;
            result = converted; return true;
        }

        internal bool TryUnscale(double value, out double result)
        {
            result = default;
            if (!double.IsFinite(value)) return false;
            double converted = (value - Offset) / Scale;
            if (!double.IsFinite(converted)) return false;
            result = converted; return true;
        }
    }

    internal static IfcNumericUnitPlan? Create(TfBlockDeserializer.UnitOptions? options,
        UnitSchemaEvaluator.UnitConversions? conversions, out string? failure)
    {
        failure = null;
        if (options == null || conversions == null) return Reject("Own current options or numeric display unavailable", out failure);

        // The spec map compares ordinally (UTF-16) up to the
        // first dash. Reject ambiguity instead of choosing a version or a family root.
        var matches = options.Options.Where(x =>
            UnitSchemaEvaluator.DisplayNameKey(x.Spec) == ProjectLengthUnits.LengthSpecName).ToArray();
        if (matches.Length != 1) return Reject("Length option absent or name-ambiguous; library fallback not reproduced", out failure);
        var format = matches[0];
        if (format.Spec.Contains('\0')) return Reject("Invalid length option spec ID", out failure);
        // A format that uses the default unit cannot be read here.
        // This is NOT the unknown-unit foot fallback of the default units.
        if (format.UseDefault != 0) return Reject("Default FormatOptions unit getter unavailable", out failure);
        if (string.IsNullOrEmpty(format.Unit) || format.Unit.Contains('\0')) return Reject("Invalid length option unit ID", out failure);

        string requested = format.Unit;
        string? prefix = null, conversionName = null;
        switch (UnitSchemaEvaluator.DisplayNameKey(requested))
        {
            case UnitPrefix + "meters":
            case UnitPrefix + "metersCentimeters": break;
            case UnitPrefix + "centimeters": prefix = "CENTI"; break;
            case UnitPrefix + "millimeters": prefix = "MILLI"; break;
            case UnitPrefix + "feet":
            case UnitPrefix + "feetFractionalInches": conversionName = "FOOT"; break;
            case UnitPrefix + "inches":
            case UnitPrefix + "fractionalInches": conversionName = "INCH"; break;
            default: requested = Feet; conversionName = "FOOT"; break;
        } // Fallback for unknown units, not a guessed metric default.

        var lengthInfo = conversions.GetLegacy(requested);
        if (lengthInfo == null || !conversions.TryFromInternal(1.0, requested, out double scale)
            || !double.IsFinite(scale) || scale <= 0) return Reject("Length scale unavailable", out failure);
        double? metresPerUnit = null;
        if (conversionName != null)
        {
            if (!conversions.TryFromInternal(1.0, Metres, out double metreScale)) return Reject("Metre base scale unavailable", out failure);
            double ratio = metreScale / scale; // not a decimal rounded ratio.
            if (!double.IsFinite(ratio) || ratio <= 0) return Reject("Length conversion ratio unavailable", out failure);
            metresPerUnit = ratio;
        }
        // Always selects W/(m^2 K), not the stored thermal
        // display option, and caches From(1) with offset zero.
        var thermalInfo = conversions.GetLegacy(Thermal);
        if (thermalInfo == null || !conversions.TryFromInternal(1.0, Thermal, out double thermalScale)
            || !double.IsFinite(thermalScale) || thermalScale <= 0) return Reject("Thermal scale unavailable", out failure);
        return new(format.Spec, format.Unit,
            new(requested, lengthInfo.FullId, "LENGTHUNIT", prefix, conversionName, metresPerUnit, scale, 0.0),
            new(Thermal, thermalInfo.FullId, "THERMALTRANSMITTANCEUNIT", null, null, null, thermalScale, 0.0));
    }

    private static IfcNumericUnitPlan? Reject(string reason, out string? failure)
    { failure = reason; return null; }
}
