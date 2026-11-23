using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>Header data and unit definitions of the exporter: the unit assignment, shared conversion-based unit builders,
/// and the current-profile project and building names. Current-only rules keep <see cref="IfcProfile.Legacy"/> unchanged.</summary>
public partial class IfcExporter
{
    /// <summary>Radians per degree as the conversion based unit DEGREE prints it, not <c>Math.PI / 180</c> (which ends in ...295).</summary>
    private const string DegreeInRadiansCurrent = "0.017453292519943278";

    /// <summary>The unit entities written up front and the position of the assignment that
    /// <see cref="FinalizeUnitAssignmentCurrent"/> completes.</summary>
    internal sealed record UnitAssignmentCurrent(
        int Length, int Degree, int Area, int Volume, int AssignmentIndex, int AssignmentId);

    private static int AddConversionBasedUnit(
        StepWriter w, string exponents, string unitType, string name, string measure, string factor, int baseUnitId)
    {
        int dimensionsId = w.Add("IFCDIMENSIONALEXPONENTS", exponents);
        int measureWithUnitId = w.Add("IFCMEASUREWITHUNIT", $"{measure}({factor}),{R(baseUnitId)}");
        return w.Add("IFCCONVERSIONBASEDUNIT", $"{R(dimensionsId)},.{unitType}UNIT.,'{name}',{R(measureWithUnitId)}");
    }

    private static int AddProjectLengthUnit(StepWriter w, int siLength, IfcUnitFamily family) => family switch
    {
        IfcUnitFamily.Foot => AddConversionBasedUnit(w, "1,0,0,0,0,0,0", "LENGTH", "FOOT", "IFCLENGTHMEASURE",
            StepWriter.Real(IfcLengthUnit.MetresPerFoot, "G17"), siLength),
        IfcUnitFamily.Inch => AddConversionBasedUnit(w, "1,0,0,0,0,0,0", "LENGTH", "INCH", "IFCLENGTHMEASURE",
            StepWriter.Real(IfcLengthUnit.MetresPerInch, "G17"), siLength),
        _ => siLength,
    };

    // Centimetre areas and volumes are the CENTI prefix on SQUARE_METRE and CUBIC_METRE, read as (0.01 m)^2 and (0.01 m)^3 with the values
    // scaled to match. A reader may instead apply the prefix to the area or volume unit itself (0.01 m2, 0.01 m3; IfcOpenShell 0.8.4 does)
    // and then reads the areas 100 times and the volumes 10000 times too large. Only that reader was checked.
    private static int AddProjectAreaUnit(StepWriter w, IfcUnitFamily family) => family switch
    {
        IfcUnitFamily.Foot => AddConversionBasedUnit(w, "2,0,0,0,0,0,0", "AREA", "SQUARE FOOT", "IFCAREAMEASURE",
            StepWriter.Real(IfcLengthUnit.SquareMetresPerSquareFoot, "G17"), w.Add("IFCSIUNIT", "*,.AREAUNIT.,$,.SQUARE_METRE.")),
        IfcUnitFamily.Inch => AddConversionBasedUnit(w, "2,0,0,0,0,0,0", "AREA", "SQUARE INCH", "IFCAREAMEASURE",
            StepWriter.Real(IfcLengthUnit.SquareMetresPerSquareInch, "G17"), w.Add("IFCSIUNIT", "*,.AREAUNIT.,$,.SQUARE_METRE.")),
        IfcUnitFamily.Centimetre => w.Add("IFCSIUNIT", "*,.AREAUNIT.,.CENTI.,.SQUARE_METRE."),
        _ => w.Add("IFCSIUNIT", "*,.AREAUNIT.,$,.SQUARE_METRE."),
    };

    private static int AddProjectVolumeUnit(StepWriter w, IfcUnitFamily family) => family switch
    {
        IfcUnitFamily.Foot => AddConversionBasedUnit(w, "3,0,0,0,0,0,0", "VOLUME", "CUBIC FOOT", "IFCVOLUMEMEASURE",
            StepWriter.Real(IfcLengthUnit.CubicMetresPerCubicFoot, "G17"), w.Add("IFCSIUNIT", "*,.VOLUMEUNIT.,$,.CUBIC_METRE.")),
        IfcUnitFamily.Inch => AddConversionBasedUnit(w, "3,0,0,0,0,0,0", "VOLUME", "CUBIC INCH", "IFCVOLUMEMEASURE",
            StepWriter.Real(IfcLengthUnit.CubicMetresPerCubicInch, "G17"), w.Add("IFCSIUNIT", "*,.VOLUMEUNIT.,$,.CUBIC_METRE.")),
        IfcUnitFamily.Centimetre => w.Add("IFCSIUNIT", "*,.VOLUMEUNIT.,.CENTI.,.CUBIC_METRE."),
        _ => w.Add("IFCSIUNIT", "*,.VOLUMEUNIT.,$,.CUBIC_METRE."),
    };

    private static int AddDegreeUnit(StepWriter w) =>
        AddConversionBasedUnit(w, "0,0,0,0,0,0,0", "PLANEANGLE", "DEGREE", "IFCPLANEANGLEMEASURE",
            DegreeInRadiansCurrent, w.Add("IFCSIUNIT", "*,.PLANEANGLEUNIT.,$,.RADIAN."));

    /// <summary>A unit is created when the first measure of its kind is made; the created ones are assigned, in creation order,
    /// in <c>IfcProject.UnitsInContext</c> at the end of the export. The default project units are millimetre, degree, square metre
    /// and cubic metre. Metric units are written as <c>IfcSIUnit</c>, imperial units as <c>IfcConversionBasedUnit</c>, and
    /// the degree as <c>IfcConversionBasedUnit</c> of the radian with the factor <see cref="DegreeInRadiansCurrent"/>.
    /// All four are always present, so they are written up front; the assignment lists the defaults until the final order is known.</summary>
    internal static UnitAssignmentCurrent CreateUnitAssignmentCurrent(StepWriter w, int lengthUnitId, IfcUnitFamily family = IfcUnitFamily.Millimetre)
    {
        int length = AddProjectLengthUnit(w, lengthUnitId, family);
        int degree = AddDegreeUnit(w);
        int area = AddProjectAreaUnit(w, family);
        int volume = AddProjectVolumeUnit(w, family);
        int index = w.EntityCount;
        int assignment = w.Add("IFCUNITASSIGNMENT", StepWriter.List(R(length), R(degree), R(area), R(volume)));
        return new UnitAssignmentCurrent(length, degree, area, volume, index, assignment);
    }

    /// <summary>Completes the unit assignment from the finished property values: the thermal transmittance unit
    /// (an <c>IfcDerivedUnit</c> of kilogram, kelvin^-1 and second^-3) exists when a
    /// <c>IfcThermalTransmittanceMeasure</c> was made, the <c>WATT</c> power unit when an <c>IfcPowerMeasure</c>
    /// was; the assignment is ordered by first use. Spatial elements are exported before all others, so their area and
    /// volume quantities create those two units before the first extrusion creates the degree; otherwise the first
    /// extrusion does. The measures come in the order the property values were written.</summary>
    internal static void FinalizeUnitAssignmentCurrent(StepWriter w, UnitAssignmentCurrent units)
    {
        var (assignmentId, assignmentType, _) = w.EntityAt(units.AssignmentIndex);
        if (assignmentType != "IFCUNITASSIGNMENT" || assignmentId != units.AssignmentId)
            throw new InvalidOperationException("The unit assignment moved: entity " + assignmentId + " is " + assignmentType + ".");

        bool spaces = false;
        int thermalAt = -1, powerAt = -1;
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (_, type, args) = w.EntityAt(i);
            if (type == "IFCSPACE") spaces = true;
            else if (type == "IFCPROPERTYSINGLEVALUE")
            {
                if (thermalAt < 0 && args.Contains("IFCTHERMALTRANSMITTANCEMEASURE(", StringComparison.Ordinal)) thermalAt = i;
                if (powerAt < 0 && args.Contains("IFCPOWERMEASURE(", StringComparison.Ordinal)) powerAt = i;
            }
        }

        var assigned = new List<string> { R(units.Length) };
        if (spaces) assigned.AddRange([R(units.Area), R(units.Volume), R(units.Degree)]);
        else assigned.AddRange([R(units.Degree), R(units.Area), R(units.Volume)]);

        int DerivedElement(string siUnit, int exponent) => w.Add("IFCDERIVEDUNITELEMENT", $"{R(w.Add("IFCSIUNIT", siUnit))},{exponent}");
        int Thermal()
        {
            int mass = DerivedElement("*,.MASSUNIT.,.KILO.,.GRAM.", 1);
            int kelvin = DerivedElement("*,.THERMODYNAMICTEMPERATUREUNIT.,$,.KELVIN.", -1);
            int second = DerivedElement("*,.TIMEUNIT.,$,.SECOND.", -3);
            return w.Add("IFCDERIVEDUNIT", $"{StepWriter.List(R(mass), R(kelvin), R(second))},.THERMALTRANSMITTANCEUNIT.,''");
        }
        int Power() => w.Add("IFCSIUNIT", "*,.POWERUNIT.,$,.WATT.");

        var used = new List<(int At, Func<int> Create)>();
        if (thermalAt >= 0) used.Add((thermalAt, Thermal));
        if (powerAt >= 0) used.Add((powerAt, Power));
        foreach (var (_, create) in used.OrderBy(unit => unit.At))
            assigned.Add(R(create()));

        w.ReplaceArgs(units.AssignmentIndex, StepWriter.List(assigned));
    }

    /// <summary>A property of the project information element (<c>OST_ProjectInformation</c>): its own text parameter,
    /// whatever the language of the file, else the text of the ProjectInformation stream. Null when neither has one.</summary>
    private static string? ProjectInformationTextCurrent(RevitFileInfo info, int parameterId, string? streamValue)
    {
        const int projectInformationCategoryId = -2003101;
        foreach (Element element in info.Elements)
            if (element.CategoryId == projectInformationCategoryId
                && element.Parameters.FirstOrDefault(parameter => parameter.Id == parameterId)?.Value is string { Length: > 0 } text)
                return text;
        return string.IsNullOrEmpty(streamValue) ? null : streamValue;
    }

    private const int ProjectNameParameterId = -1006317, ProjectNumberParameterId = -1006316,
        ProjectStatusParameterId = -1006320, ProjectBuildingNameParameterId = -1019006;

    /// <summary>IfcProject Name, LongName and Phase of the current profile: the project number, the project
    /// name and the project status, each as it is and <c>''</c> when empty. There is no fallback to another name, except
    /// that an object without a name is named after its element: the project information element has no name, so the
    /// name is its element id, as text.</summary>
    internal static (string Name, string LongName, string Phase) ResolveProjectAttributesCurrent(RevitFileInfo info) =>
        (ProjectInformationTextCurrent(info, ProjectNumberParameterId, info.ProjectInfo?.Number)
            ?? SoleProjectInformation(info)?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            ProjectInformationTextCurrent(info, ProjectNameParameterId, info.ProjectInfo?.Name) ?? "",
            ProjectInformationTextCurrent(info, ProjectStatusParameterId, info.ProjectInfo?.Status) ?? "");

    /// <summary>The building name of the current profile : PROJECT_BUILDING_NAME of the project
    /// information, <c>''</c> when it is empty. Null here means empty.</summary>
    internal static string? ResolveBuildingNameCurrent(RevitFileInfo info) =>
        ProjectInformationTextCurrent(info, ProjectBuildingNameParameterId, info.ProjectInfo?.BuildingName);

    private const int ProjectAuthorParameterId = -1019005, ProjectOrganizationNameParameterId = -1019008,
        ProjectOrganizationDescriptionParameterId = -1019007, ProjectAddressParameterId = -1006318;

    /// <summary>Splits a person name: a name is cut at spaces (and at the first
    /// comma, after which the following words are put in front of what was read), words that end in a dot at the
    /// start are prefix titles, the first other word is the given name, trailing dotted words are suffix titles, the
    /// last remaining word is the family name and what lies between are middle names. A single word is a given name.</summary>
    internal static (string Family, string Given, List<string> Middle, List<string> Prefixes, List<string> Suffixes)
        ParsePersonNameCurrent(string author)
    {
        var names = new List<string>();
        string current = author;
        int noEndlessLoop = 0, index = 0;
        bool foundComma = false;
        do
        {
            int insertAt = index;
            current = current.TrimStart(' ');
            if (current.Length == 0) break;
            int comma = foundComma ? current.Length : current.IndexOf(',');
            if (comma == -1) comma = current.Length;
            int space = current.IndexOf(' ');
            if (space == -1) space = current.Length;
            if (comma < space)
            {
                foundComma = true;
                index = -1;
                space = comma;
            }
            if (space == current.Length)
            {
                names.Add(current);
                break;
            }
            if (space == 0)
            {
                // A leading comma separates nothing, so it is dropped.
                current = current[1..];
                continue;
            }
            names.Insert(Math.Clamp(insertAt, 0, names.Count), current[..space]);
            index++;
            current = current[(space + 1)..];
            noEndlessLoop++;
        } while (noEndlessLoop < 100);

        string family = "", given = "";
        var prefixes = new List<string>();
        var suffixes = new List<string>();
        var middle = new List<string>();
        static bool EndsWithDot(string name) => name.Length == 0 || name[^1] == '.';
        int first = 0;
        while (first < names.Count && EndsWithDot(names[first])) prefixes.Add(names[first++]);
        if (first < names.Count) given = names[first++];
        int last = names.Count - 1;
        while (last >= first && EndsWithDot(names[last])) suffixes.Insert(0, names[last--]);
        if (last >= first) family = names[last--];
        for (; first <= last; first++) middle.Add(names[first]);
        return (family, given, middle, prefixes, suffixes);
    }

    /// <summary>An IfcPerson of the current profile: the project author (PROJECT_AUTHOR), parsed into family
    /// and given name; the names are empty when the author is empty (no file records the operating system user).</summary>
    internal static string ProjectPersonArgumentsCurrent(RevitFileInfo info)
    {
        var (family, given, middle, prefixes, suffixes) = ParsePersonNameCurrent(
            ProjectInformationTextCurrent(info, ProjectAuthorParameterId, info.ProjectInfo?.Author) ?? "");
        string List(List<string> items) => items.Count == 0 ? "$" : StepWriter.List(items.Select(S));
        return $"'',{S(family)},{S(given)},{List(middle)},{List(prefixes)},{List(suffixes)},$,$";
    }

    /// <summary>The IfcOrganization the owner history belongs to: PROJECT_ORGANIZATION_NAME and
    /// PROJECT_ORGANIZATION_DESCRIPTION, <c>''</c> each when empty.</summary>
    internal static string ProjectOrganizationArgumentsCurrent(RevitFileInfo info) =>
        $"'',{S(ProjectInformationTextCurrent(info, ProjectOrganizationNameParameterId, info.ProjectInfo?.OrganizationName) ?? "")},"
        + $"{S(ProjectInformationTextCurrent(info, ProjectOrganizationDescriptionParameterId, null) ?? "")},$,$";

    /// <summary>An IfcGeometricRepresentationSubContext of the current profile: the identifier names the view, the type is "Model", there
    /// is no target scale and the user defined target view is empty.</summary>
    internal static string SubContextArgumentsCurrent(string identifier, int contextId, string targetView) =>
        $"{S(identifier)},{S("Model")},*,*,*,*,{R(contextId)},$,.{targetView}.,''";

    /// <summary>The IfcPostalAddress of the current profile: the first line is the project address
    /// (PROJECT_ADDRESS), the rest come from the place name of the active GeoSite, a comma separated list read from the
    /// end: country, then the region (the part up to the first space; the part after it is the postal code), then the
    /// town, and what is left stays address lines. Purpose is unset and every text is <c>''</c> when empty.</summary>
    internal static string PostalAddressArgumentsCurrent(RevitFileInfo info)
    {
        var parsed = new List<string>();
        string place = info.SitePlaceName ?? "";
        int comma;
        do
        {
            comma = place.IndexOf(',');
            if (comma >= 0)
            {
                if (comma > 0) parsed.Add(place[..comma]);
                place = place[(comma + 1)..].TrimStart();
            }
            else if (place.Length > 0) parsed.Add(place);
        } while (comma >= 0);

        int lines = parsed.Count;
        string country = "", region = "", postCode = "", town = "";
        if (lines > 0) country = parsed[--lines];
        if (lines > 0)
        {
            string part = parsed[--lines];
            int space = part.IndexOf(' ');
            if (space > 0) (region, postCode) = (part[..space], part[(space + 1)..]);
            else region = part;
        }
        if (lines > 0) town = parsed[--lines];

        var addressLines = new List<string>();
        if (ProjectInformationTextCurrent(info, ProjectAddressParameterId, info.ProjectInfo?.Address) is { } projectAddress)
            addressLines.Add(projectAddress);
        addressLines.AddRange(parsed.Take(lines));
        return $"$,'','','',{(addressLines.Count == 0 ? "$" : StepWriter.List(addressLines.Select(S)))},'',{S(town)},{S(region)},"
            + $"{S(postCode)},{S(country)}";
    }
}
