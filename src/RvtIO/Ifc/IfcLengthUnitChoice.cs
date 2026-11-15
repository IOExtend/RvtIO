using RvtIO.Parsing;

namespace RvtIO.Ifc;

/// <summary>The IFC length unit family the file declares, decided by <see cref="IfcExporter.LengthUnit"/> or by the document's stored Length format option.</summary>
public enum IfcUnitFamily
{
    /// <summary>Lengths are written in millimetres.</summary>
    Millimetre,
    /// <summary>Lengths are written in metres.</summary>
    Metre,
    /// <summary>Lengths are written in feet.</summary>
    Foot,
}

internal static class IfcLengthUnitChoice
{
    private const string UnitPrefix = "autodesk.unit.unit:";

    /// <summary>The unit id of the single Length option, null when it is missing, ambiguous or the library default.</summary>
    internal static string? StoredLengthUnit(TfBlockDeserializer.UnitOptions? options) => StoredLengthUnit(options, out _);

    /// <summary><paramref name="issue"/> says why the option is not usable (null when it is).</summary>
    internal static string? StoredLengthUnit(TfBlockDeserializer.UnitOptions? options, out string? issue)
    {
        issue = "the document unit options were not read";
        if (options == null) return null;
        var matches = options.Options.Where(option =>
            UnitSchemaEvaluator.DisplayNameKey(option.Spec) == IfcNumericUnitPlan.LengthSpecName).ToArray();
        if (matches.Length != 1) issue = matches.Length == 0 ? "no Length format option" : "several Length format options";
        else if (matches[0].UseDefault != 0) issue = "the Length format option follows the library default";
        else if (string.IsNullOrEmpty(matches[0].Unit)) issue = "the Length format option has no unit";
        else
        {
            issue = null;
            return matches[0].Unit;
        }
        return null;
    }

    /// <summary>The family the stored unit is written in, as Revit's own IFC export declares it: feet, feet-and-fractional-inches
    /// and US survey feet as FOOT, meters and meters-and-centimeters as METRE, millimeters as MILLI METRE. Null for every
    /// other unit (centimeters and inches among them, which have no family here) and for an unreadable option.</summary>
    internal static IfcUnitFamily? OwnFamily(string? storedLengthUnit) =>
        storedLengthUnit == null ? null
        : UnitSchemaEvaluator.DisplayNameKey(storedLengthUnit) switch
        {
            UnitPrefix + "feet" or UnitPrefix + "feetFractionalInches" or UnitPrefix + "usSurveyFeet" => IfcUnitFamily.Foot,
            UnitPrefix + "meters" or UnitPrefix + "metersCentimeters" => IfcUnitFamily.Metre,
            UnitPrefix + "millimeters" => IfcUnitFamily.Millimetre,
            _ => null,
        };

    /// <summary>The family of the stored unit (<see cref="OwnFamily"/>); a unit without one and an unreadable option are
    /// written as MILLI METRE, the documented default.</summary>
    internal static IfcUnitFamily Family(string? storedLengthUnit) => OwnFamily(storedLengthUnit) ?? IfcUnitFamily.Millimetre;
}
