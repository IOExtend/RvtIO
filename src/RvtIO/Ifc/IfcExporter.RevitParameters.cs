using System.Collections.Frozen;
using System.Globalization;
using RvtIO.Compact;
using RvtIO.Lookup;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    private enum RevitSpecKind
    {
        Length,
        Area,
        Volume,
        Angle,
        SiMeasure,
    }

    private readonly record struct SpecConversion(RevitSpecKind Kind, string MeasureName = "", int LengthExponent = 0);

    private static readonly FrozenDictionary<string, SpecConversion> SpecConversions =
        new Dictionary<string, SpecConversion>(StringComparer.Ordinal)
        {
            // Length specs -> IFCLENGTHMEASURE(mm), then IfcLengthUnit.ToFamily scales to the file's length unit.
            ["autodesk.spec.aec:length"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec:sheetLength"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec:decimalSheetLength"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec:distance"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.electrical:cableTraySize"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.electrical:conduitSize"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.electrical:wireDiameter"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.hvac:ductInsulationThickness"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.hvac:ductLiningThickness"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.hvac:ductSize"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.hvac:roughness"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.piping:pipeInsulationThickness"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.piping:pipeSize"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.piping:pipeDimension"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.piping:roughness"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.structural:barDiameter"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.structural:crackWidth"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.structural:displacement"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.structural:reinforcementCover"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.structural:reinforcementLength"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.structural:reinforcementSpacing"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.structural:sectionDimension"] = new(RevitSpecKind.Length),
            ["autodesk.spec.aec.structural:sectionProperty"] = new(RevitSpecKind.Length),

            // Area specs -> IFCAREAMEASURE(m^2), then IfcLengthUnit.ToFamily scales to the file's area unit.
            ["autodesk.spec.aec:area"] = new(RevitSpecKind.Area),
            ["autodesk.spec.aec.structural:crossSection"] = new(RevitSpecKind.Area),
            ["autodesk.spec.aec.structural:sectionArea"] = new(RevitSpecKind.Area),

            // Volume specs -> IFCVOLUMEMEASURE(m^3), then IfcLengthUnit.ToFamily scales to the file's volume unit.
            ["autodesk.spec.aec:volume"] = new(RevitSpecKind.Volume),
            ["autodesk.spec.aec.piping:volume"] = new(RevitSpecKind.Volume),
            ["autodesk.spec.aec.structural:reinforcementVolume"] = new(RevitSpecKind.Volume),

            // Angle specs -> IFCPLANEANGLEMEASURE(deg in Current, rad in Legacy).
            ["autodesk.spec.aec:angle"] = new(RevitSpecKind.Angle),
            ["autodesk.spec.aec:siteAngle"] = new(RevitSpecKind.Angle),

            // Physical specs converted to SI units (Revit internal units are SI except feet^L for length^L).
            ["autodesk.spec.aec.structural:stress"] = new(RevitSpecKind.SiMeasure, "IFCPRESSUREMEASURE", -1),
            ["autodesk.spec.aec.hvac:pressure"] = new(RevitSpecKind.SiMeasure, "IFCPRESSUREMEASURE", -1),
            ["autodesk.spec.aec.piping:pressure"] = new(RevitSpecKind.SiMeasure, "IFCPRESSUREMEASURE", -1),
            ["autodesk.spec.aec.structural:areaForce"] = new(RevitSpecKind.SiMeasure, "IFCPLANARFORCEMEASURE", -1),
            ["autodesk.spec.aec.hvac:viscosity"] = new(RevitSpecKind.SiMeasure, "IFCDYNAMICVISCOSITYMEASURE", -1),
            ["autodesk.spec.aec.piping:viscosity"] = new(RevitSpecKind.SiMeasure, "IFCDYNAMICVISCOSITYMEASURE", -1),
            ["autodesk.spec.aec.energy:permeability"] = new(RevitSpecKind.SiMeasure, "IFCVAPORPERMEABILITYMEASURE", -1),
            ["autodesk.spec.aec.structural:massPerUnitLength"] = new(RevitSpecKind.SiMeasure, "IFCMASSPERLENGTHMEASURE", -1),
            ["autodesk.spec.aec.structural:momentScale"] = new(RevitSpecKind.SiMeasure, "IFCREAL", -1),

            ["autodesk.spec.aec.structural:force"] = new(RevitSpecKind.SiMeasure, "IFCFORCEMEASURE", 1),
            ["autodesk.spec.aec.hvac:velocity"] = new(RevitSpecKind.SiMeasure, "IFCLINEARVELOCITYMEASURE", 1),
            ["autodesk.spec.aec.piping:velocity"] = new(RevitSpecKind.SiMeasure, "IFCLINEARVELOCITYMEASURE", 1),
            ["autodesk.spec.aec:speed"] = new(RevitSpecKind.SiMeasure, "IFCLINEARVELOCITYMEASURE", 1),
            ["autodesk.spec.aec.energy:thermalConductivity"] = new(RevitSpecKind.SiMeasure, "IFCTHERMALCONDUCTIVITYMEASURE", 1),
            ["autodesk.spec.aec.structural:linearMoment"] = new(RevitSpecKind.SiMeasure, "IFCLINEARMOMENTMEASURE", 1),
            ["autodesk.spec.aec.hvac:airFlowDensity"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 1),
            ["autodesk.spec.aec.structural:reinforcementAreaPerUnitLength"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 1),
            ["autodesk.spec.aec.structural:surfaceAreaPerUnitLength"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 1),
            ["autodesk.spec.aec.structural:linearForceScale"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 1),

            ["autodesk.spec.aec.electrical:power"] = new(RevitSpecKind.SiMeasure, "IFCPOWERMEASURE", 2),
            ["autodesk.spec.aec.electrical:wattage"] = new(RevitSpecKind.SiMeasure, "IFCPOWERMEASURE", 2),
            ["autodesk.spec.aec.hvac:coolingLoad"] = new(RevitSpecKind.SiMeasure, "IFCPOWERMEASURE", 2),
            ["autodesk.spec.aec.hvac:heatingLoad"] = new(RevitSpecKind.SiMeasure, "IFCPOWERMEASURE", 2),
            ["autodesk.spec.aec.hvac:heatGain"] = new(RevitSpecKind.SiMeasure, "IFCPOWERMEASURE", 2),
            ["autodesk.spec.aec.electrical:apparentPower"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 2),
            ["autodesk.spec.aec.structural:areaForceScale"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 2),
            ["autodesk.spec.aec.electrical:potential"] = new(RevitSpecKind.SiMeasure, "IFCELECTRICVOLTAGEMEASURE", 2),
            ["autodesk.spec.aec.energy:specificHeat"] = new(RevitSpecKind.SiMeasure, "IFCSPECIFICHEATCAPACITYMEASURE", 2),
            ["autodesk.spec.aec.energy:specificHeatOfVaporization"] = new(RevitSpecKind.SiMeasure, "IFCHEATINGVALUEMEASURE", 2),
            ["autodesk.spec.aec.structural:moment"] = new(RevitSpecKind.SiMeasure, "IFCTORQUEMEASURE", 2),
            ["autodesk.spec.aec:energy"] = new(RevitSpecKind.SiMeasure, "IFCENERGYMEASURE", 2),
            ["autodesk.spec.aec.structural:energy"] = new(RevitSpecKind.SiMeasure, "IFCENERGYMEASURE", 2),

            ["autodesk.spec.aec.electrical:illuminance"] = new(RevitSpecKind.SiMeasure, "IFCILLUMINANCEMEASURE", -2),
            ["autodesk.spec.aec.electrical:efficacy"] = new(RevitSpecKind.SiMeasure, "IFCREAL", -2),
            ["autodesk.spec.aec.hvac:friction"] = new(RevitSpecKind.SiMeasure, "IFCREAL", -2),
            ["autodesk.spec.aec.piping:friction"] = new(RevitSpecKind.SiMeasure, "IFCREAL", -2),
            ["autodesk.spec.aec.structural:unitWeight"] = new(RevitSpecKind.SiMeasure, "IFCREAL", -2),
            ["autodesk.spec.aec.structural:massPerUnitArea"] = new(RevitSpecKind.SiMeasure, "IFCREAL", -2),

            ["autodesk.spec.aec.hvac:airFlow"] = new(RevitSpecKind.SiMeasure, "IFCVOLUMETRICFLOWRATEMEASURE", 3),
            ["autodesk.spec.aec.piping:flow"] = new(RevitSpecKind.SiMeasure, "IFCVOLUMETRICFLOWRATEMEASURE", 3),
            ["autodesk.spec.aec.structural:sectionModulus"] = new(RevitSpecKind.SiMeasure, "IFCSECTIONMODULUSMEASURE", 3),
            ["autodesk.spec.aec.electrical:resistivity"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 3),

            ["autodesk.spec.aec:massDensity"] = new(RevitSpecKind.SiMeasure, "IFCMASSDENSITYMEASURE", -3),
            ["autodesk.spec.aec.piping:density"] = new(RevitSpecKind.SiMeasure, "IFCMASSDENSITYMEASURE", -3),

            ["autodesk.spec.aec.structural:momentOfInertia"] = new(RevitSpecKind.SiMeasure, "IFCMOMENTOFINERTIAMEASURE", 4),
            ["autodesk.spec.aec.structural:warpingConstant"] = new(RevitSpecKind.SiMeasure, "IFCWARPINGCONSTANTMEASURE", 6),

            // Specs whose Revit internal unit is already SI (lengthExponent == 0).
            ["autodesk.spec.aec.electrical:powerDensity"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec.structural:forceScale"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec.energy:heatTransferCoefficient"] = new(RevitSpecKind.SiMeasure, "IFCTHERMALTRANSMITTANCEMEASURE", 0),
            ["autodesk.spec.aec.energy:thermalResistance"] = new(RevitSpecKind.SiMeasure, "IFCTHERMALRESISTANCEMEASURE", 0),
            ["autodesk.spec.aec.energy:heatCapacityPerArea"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec.electrical:current"] = new(RevitSpecKind.SiMeasure, "IFCELECTRICCURRENTMEASURE", 0),
            ["autodesk.spec.aec.electrical:luminousFlux"] = new(RevitSpecKind.SiMeasure, "IFCLUMINOUSFLUXMEASURE", 0),
            ["autodesk.spec.aec.electrical:luminousIntensity"] = new(RevitSpecKind.SiMeasure, "IFCLUMINOUSINTENSITYMEASURE", 0),
            ["autodesk.spec.aec.electrical:colorTemperature"] = new(RevitSpecKind.SiMeasure, "IFCTHERMODYNAMICTEMPERATUREMEASURE", 0),
            ["autodesk.spec.aec.hvac:temperature"] = new(RevitSpecKind.SiMeasure, "IFCTHERMODYNAMICTEMPERATUREMEASURE", 0),
            ["autodesk.spec.aec.piping:temperature"] = new(RevitSpecKind.SiMeasure, "IFCTHERMODYNAMICTEMPERATUREMEASURE", 0),
            ["autodesk.spec.aec.structural:mass"] = new(RevitSpecKind.SiMeasure, "IFCMASSMEASURE", 0),
            ["autodesk.spec.aec.structural:linearForce"] = new(RevitSpecKind.SiMeasure, "IFCLINEARFORCEMEASURE", 0),
            ["autodesk.spec.aec.structural:weightPerUnitLength"] = new(RevitSpecKind.SiMeasure, "IFCLINEARFORCEMEASURE", 0),
            ["autodesk.spec.aec.structural:thermalExpansionCoefficient"] = new(RevitSpecKind.SiMeasure, "IFCTHERMALEXPANSIONCOEFFICIENTMEASURE", 0),
            ["autodesk.spec.aec:time"] = new(RevitSpecKind.SiMeasure, "IFCTIMEMEASURE", 0),
            ["autodesk.spec.aec:frequency"] = new(RevitSpecKind.SiMeasure, "IFCFREQUENCYMEASURE", 0),
            ["autodesk.spec.measurable:currency"] = new(RevitSpecKind.SiMeasure, "IFCMONETARYMEASURE", 0),
            ["autodesk.spec.aec:number"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec.hvac:factor"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec.electrical:demandFactor"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec:slope"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec.hvac:slope"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec.piping:slope"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec.hvac:coolingLoadDividedByArea"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec.hvac:heatingLoadDividedByArea"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec.hvac:areaDividedByCoolingLoad"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
            ["autodesk.spec.aec.hvac:areaDividedByHeatingLoad"] = new(RevitSpecKind.SiMeasure, "IFCREAL", 0),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    // The unit of each measure SpecConversions writes: a named SI unit, or the powers of kilogram, metre,
    // second and kelvin of a derived one. IfcMonetaryMeasure has none: the currency is not decoded.
    private static readonly (string Measure, string UnitType, string SiUnit)[] NamedMeasureUnits =
    [
        ("IFCPRESSUREMEASURE", "PRESSUREUNIT", "$,.PASCAL."),
        ("IFCFORCEMEASURE", "FORCEUNIT", "$,.NEWTON."),
        ("IFCPOWERMEASURE", "POWERUNIT", "$,.WATT."),
        ("IFCENERGYMEASURE", "ENERGYUNIT", "$,.JOULE."),
        ("IFCELECTRICVOLTAGEMEASURE", "ELECTRICVOLTAGEUNIT", "$,.VOLT."),
        ("IFCELECTRICCURRENTMEASURE", "ELECTRICCURRENTUNIT", "$,.AMPERE."),
        ("IFCILLUMINANCEMEASURE", "ILLUMINANCEUNIT", "$,.LUX."),
        ("IFCLUMINOUSFLUXMEASURE", "LUMINOUSFLUXUNIT", "$,.LUMEN."),
        ("IFCLUMINOUSINTENSITYMEASURE", "LUMINOUSINTENSITYUNIT", "$,.CANDELA."),
        ("IFCTHERMODYNAMICTEMPERATUREMEASURE", "THERMODYNAMICTEMPERATUREUNIT", "$,.KELVIN."),
        ("IFCMASSMEASURE", "MASSUNIT", ".KILO.,.GRAM."),
        ("IFCTIMEMEASURE", "TIMEUNIT", "$,.SECOND."),
        ("IFCFREQUENCYMEASURE", "FREQUENCYUNIT", "$,.HERTZ."),
    ];

    private static readonly (string Measure, string UnitType, int Kilogram, int Metre, int Second, int Kelvin)[] DerivedMeasureUnits =
    [
        ("IFCPLANARFORCEMEASURE", "PLANARFORCEUNIT", 1, -1, -2, 0),
        ("IFCDYNAMICVISCOSITYMEASURE", "DYNAMICVISCOSITYUNIT", 1, -1, -1, 0),
        ("IFCVAPORPERMEABILITYMEASURE", "VAPORPERMEABILITYUNIT", 0, -1, 1, 0),
        ("IFCMASSPERLENGTHMEASURE", "MASSPERLENGTHUNIT", 1, -1, 0, 0),
        ("IFCLINEARVELOCITYMEASURE", "LINEARVELOCITYUNIT", 0, 1, -1, 0),
        ("IFCTHERMALCONDUCTIVITYMEASURE", "THERMALCONDUCTANCEUNIT", 1, 1, -3, -1),
        ("IFCLINEARMOMENTMEASURE", "LINEARMOMENTUNIT", 1, 1, -2, 0),
        ("IFCSPECIFICHEATCAPACITYMEASURE", "SPECIFICHEATCAPACITYUNIT", 0, 2, -2, -1),
        ("IFCHEATINGVALUEMEASURE", "HEATINGVALUEUNIT", 0, 2, -2, 0),
        ("IFCTORQUEMEASURE", "TORQUEUNIT", 1, 2, -2, 0),
        ("IFCVOLUMETRICFLOWRATEMEASURE", "VOLUMETRICFLOWRATEUNIT", 0, 3, -1, 0),
        ("IFCSECTIONMODULUSMEASURE", "SECTIONMODULUSUNIT", 0, 3, 0, 0),
        ("IFCMASSDENSITYMEASURE", "MASSDENSITYUNIT", 1, -3, 0, 0),
        ("IFCMOMENTOFINERTIAMEASURE", "MOMENTOFINERTIAUNIT", 0, 4, 0, 0),
        ("IFCWARPINGCONSTANTMEASURE", "WARPINGCONSTANTUNIT", 0, 6, 0, 0),
        ("IFCTHERMALTRANSMITTANCEMEASURE", "THERMALTRANSMITTANCEUNIT", 1, 0, -3, -1),
        ("IFCTHERMALRESISTANCEMEASURE", "THERMALRESISTANCEUNIT", -1, 0, 3, 1),
        ("IFCLINEARFORCEMEASURE", "LINEARFORCEUNIT", 1, 0, -2, 0),
        ("IFCTHERMALEXPANSIONCOEFFICIENTMEASURE", "THERMALEXPANSIONCOEFFICIENTUNIT", 0, 0, 0, -1),
    ];

    // the measure types written with RevitParameterUnits = Ifc in the running export
    private readonly HashSet<string> _revitParameterMeasures = new(StringComparer.Ordinal);

    /// <summary>Assigns the project unit of every measure the Revit parameters were converted to. The values are
    /// in unprefixed SI units whatever the length unit of the file is.</summary>
    private void DeclareRevitParameterUnits(StepWriter w)
    {
        // assigned elsewhere: the current profile adds power and thermal transmittance from the finished
        // property values, the legacy header has the second
        bool AlreadyAssigned(string unitType) => CurrentProfile
            ? unitType is "POWERUNIT" or "THERMALTRANSMITTANCEUNIT"
            : unitType is "TIMEUNIT";
        int SiUnit(string unitType, string unit) => w.Add("IFCSIUNIT", $"*,.{unitType}.,{unit}");

        var added = new List<string>();
        foreach (var (measure, unitType, unit) in NamedMeasureUnits)
        {
            if (_revitParameterMeasures.Contains(measure) && !AlreadyAssigned(unitType))
                added.Add(R(SiUnit(unitType, unit)));
        }
        foreach (var (measure, unitType, kilogram, metre, second, kelvin) in DerivedMeasureUnits)
        {
            if (!_revitParameterMeasures.Contains(measure) || AlreadyAssigned(unitType)) continue;
            var elements = new List<string>();
            void Factor(int exponent, string baseType, string baseUnit)
            {
                if (exponent != 0)
                    elements.Add(R(w.Add("IFCDERIVEDUNITELEMENT", $"{R(SiUnit(baseType, baseUnit))},{exponent}")));
            }
            Factor(kilogram, "MASSUNIT", ".KILO.,.GRAM.");
            Factor(metre, "LENGTHUNIT", "$,.METRE.");
            Factor(second, "TIMEUNIT", "$,.SECOND.");
            Factor(kelvin, "THERMODYNAMICTEMPERATUREUNIT", "$,.KELVIN.");
            added.Add(R(w.Add("IFCDERIVEDUNIT", $"{StepWriter.List(elements)},.{unitType}.,$")));
        }
        if (added.Count == 0) return;

        for (int i = 0; i < w.EntityCount; i++)
        {
            var (_, type, args) = w.EntityAt(i);
            if (type != "IFCUNITASSIGNMENT") continue;
            // the attribute text is the one list "(#1,#2)"
            w.ReplaceArgs(i, $"{args[..^1]},{string.Join(",", added)})");
            return;
        }
        throw new InvalidOperationException("The export has no IfcUnitAssignment for the Revit parameter units.");
    }

    private static double PowerOfFeetToMetres(int positiveExponent) => positiveExponent switch
    {
        1 => FeetToMetres,
        2 => FeetToMetres * FeetToMetres,
        3 => FeetToMetres * FeetToMetres * FeetToMetres,
        4 => FeetToMetres * FeetToMetres * FeetToMetres * FeetToMetres,
        6 => FeetToMetres * FeetToMetres * FeetToMetres * FeetToMetres * FeetToMetres * FeetToMetres,
        _ => Math.Pow(FeetToMetres, positiveExponent),
    };

    private static double ConvertInternalToSi(double value, int lengthExponent) => lengthExponent switch
    {
        0 => value,
        > 0 => value * PowerOfFeetToMetres(lengthExponent),
        < 0 => value / PowerOfFeetToMetres(-lengthExponent),
    };

    internal static string? ConvertDoubleToIfcOrSi(double value, string? spec, bool currentProfile)
    {
        if (!double.IsFinite(value) || string.IsNullOrWhiteSpace(spec))
            return null;

        string key = UnitSchemaEvaluator.DisplayNameKey(spec);
        if (!SpecConversions.TryGetValue(key, out var conversion))
            return null;

        string Real(double v) => currentProfile ? D17(v) : D(v);
        return conversion.Kind switch
        {
            RevitSpecKind.Length => $"IFCLENGTHMEASURE({D17(value * 304.8)})",
            RevitSpecKind.Area => $"IFCAREAMEASURE({Real(value * FeetToMetres * FeetToMetres)})",
            RevitSpecKind.Volume => $"IFCVOLUMEMEASURE({Real(value * FeetToMetres * FeetToMetres * FeetToMetres)})",
            RevitSpecKind.Angle => $"IFCPLANEANGLEMEASURE({(currentProfile ? D17(value * (180.0 / Math.PI)) : D(value))})",
            RevitSpecKind.SiMeasure => $"{conversion.MeasureName}({Real(ConvertInternalToSi(value, conversion.LengthExponent))})",
            _ => null,
        };
    }

    private string? ResolveParameterSpec(Parameter p, string? apiDataType)
    {
        if (!string.IsNullOrEmpty(apiDataType))
            return apiDataType;
        if (p.Id > 0 && _userParameters?.TryGetDefinition(p.Id, out var userDef) == true && !string.IsNullOrEmpty(userDef.Spec))
            return userDef.Spec;
        // a parameter without a known spec keeps its stored number: its name does not say what it measures
        return string.IsNullOrEmpty(p.SpecType) ? null : p.SpecType;
    }

    private string? FormatRevitParameterValue(
        Element elem,
        Parameter p,
        string? apiDataType,
        IReadOnlyDictionary<int, Element> elemById,
        bool useIfcUnits,
        bool useInternalUnits,
        bool isGrouped)
    {
        string rawValue;
        switch (p.StorageType)
        {
            case StorageType.Double:
                if (p.Value is double dv && double.IsFinite(dv))
                    rawValue = $"IFCREAL({(CurrentProfile ? D17(dv) : D(dv))})";
                else
                    return null;
                break;
            case StorageType.String:
                rawValue = $"IFCLABEL({S(p.Value?.ToString() ?? "")})";
                break;
            case StorageType.Integer:
                rawValue = p.Value is bool bv
                    ? $"IFCINTEGER({(bv ? 1 : 0)})"
                    : $"IFCINTEGER({p.Value})";
                break;
            case StorageType.ElementId:
                rawValue = $"IFCINTEGER({p.Value})";
                break;
            default:
                return null;
        }

        if (useIfcUnits)
        {
            if (isGrouped && !string.IsNullOrEmpty(apiDataType)
                && TypedApiParameterValue(elem, p, apiDataType, elemById, CurrentProfile, convertAllSpecs: true) is { } groupedTyped)
            {
                return groupedTyped;
            }
            if (p.StorageType == StorageType.Double && p.Value is double d && double.IsFinite(d))
            {
                string? spec = ResolveParameterSpec(p, apiDataType);
                return ConvertDoubleToIfcOrSi(d, spec, CurrentProfile) ?? rawValue;
            }
            if (isGrouped && p.Id < 0
                && TypedApiParameterValue(elem, p, apiDataType ?? "", elemById, CurrentProfile, convertAllSpecs: true) is { } groupedRef)
            {
                return groupedRef;
            }
            return rawValue;
        }

        if (useInternalUnits)
        {
            if (isGrouped && p.StorageType != StorageType.Double && p.Id < 0)
                return TypedApiParameterValue(elem, p, apiDataType ?? "", elemById, CurrentProfile) ?? rawValue;
            return rawValue;
        }

        // Unspecified mode: keep existing RevitParameterGroups vs RevitParameters behavior unchanged.
        if (isGrouped && p.Id < 0 && apiDataType != null)
            return TypedApiParameterValue(elem, p, apiDataType, elemById, CurrentProfile) ?? rawValue;

        return rawValue;
    }

    private (string Name, string? Description) ResolveRevitParameterNameAndDescription(
        Parameter p,
        bool hasApiDef,
        string apiName,
        bool useDisplayNames,
        bool useBuiltInNames,
        bool isGrouped)
    {
        static bool IsPlaceholderName(string? candidate, int id) =>
            string.IsNullOrWhiteSpace(candidate)
            || candidate == id.ToString(CultureInfo.InvariantCulture)
            || candidate == $"Param_{id}";

        if (useDisplayNames)
        {
            if (p.Id < 0)
            {
                string? bipName = p.DefinitionBuiltIn ?? (BipNames.Lookup.TryGetValue(p.Id, out var foundBip) ? foundBip : null);
                string displayName = p.Id switch
                {
                    -1002502 => "Classification Number",
                    -1002503 => "Classification Title",
                    _ when hasApiDef && !string.IsNullOrWhiteSpace(apiName) => apiName,
                    _ when CompactLabels.TryGetParameterLabel(p.Id, out var compactLabel) && !string.IsNullOrWhiteSpace(compactLabel) => compactLabel,
                    _ => p.DefinitionName ?? bipName ?? $"Param_{p.Id}",
                };
                return (displayName, bipName);
            }

            if (_userParameters?.TryGetDefinition(p.Id, out var userDef) == true && !string.IsNullOrWhiteSpace(userDef.Caption))
                return (userDef.Caption, null);

            if (!IsPlaceholderName(p.DefinitionName, p.Id))
                return (p.DefinitionName!, null);

            return ($"Param_{p.Id}", null);
        }

        if (useBuiltInNames)
        {
            if (p.Id < 0)
            {
                string builtIn = p.DefinitionBuiltIn
                    ?? (BipNames.Lookup.TryGetValue(p.Id, out var foundBip) ? foundBip : null)
                    ?? p.DefinitionName
                    ?? $"Param_{p.Id}";
                return (builtIn, null);
            }
            return (p.DefinitionName ?? $"Param_{p.Id}", null);
        }

        if (isGrouped && hasApiDef)
        {
            string groupedName = p.Id switch
            {
                -1002502 => "Classification Number",
                -1002503 => "Classification Title",
                _ => apiName,
            };
            return (groupedName, null);
        }

        return (p.DefinitionName ?? $"Param_{p.Id}", null);
    }
}
