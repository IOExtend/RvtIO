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
