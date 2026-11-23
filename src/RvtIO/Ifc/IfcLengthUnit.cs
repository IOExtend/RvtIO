using System.Globalization;
using System.Text;

namespace RvtIO.Ifc;

/// <summary>
/// The exporter builds its geometry and spatial attributes in metres; the IFC length unit is written as MILLI METRE.
/// The conversion happens once, on the finished entity list, by moving the decimal point of every length-valued
/// attribute three places (exact for the 15-17 digit literals the writer prints). The table lists the IFC2X3
/// attributes of the entities the exporter writes in metres; ratios, angles, counts, the overall dimensions of doors
/// and windows and the property values typed IFC(POSITIVE)LENGTHMEASURE are already written in millimetres at their
/// source and are left alone.
/// </summary>
internal static class IfcLengthUnit
{
    /// <summary>Attribute indices (0 = first attribute, GlobalId of rooted entities) that hold one length.</summary>
    private static readonly Dictionary<string, int[]> Scalars = new()
    {
        ["IFCVECTOR"] = [1],
        ["IFCRECTANGLEPROFILEDEF"] = [3, 4],
        ["IFCCIRCLEPROFILEDEF"] = [3],
        ["IFCCIRCLEHOLLOWPROFILEDEF"] = [3, 4],
        ["IFCCIRCLE"] = [1],
        ["IFCEXTRUDEDAREASOLID"] = [3],
        ["IFCSITE"] = [11],
        ["IFCBUILDING"] = [9, 10],
        ["IFCBUILDINGSTOREY"] = [9],
        ["IFCSPACE"] = [10],
        ["IFCSTAIRFLIGHT"] = [10, 11],
        ["IFCMATERIALLAYER"] = [1],
        ["IFCMATERIALLAYERSETUSAGE"] = [3],
        ["IFCDOORLININGPROPERTIES"] = [4, 5, 6, 7, 8, 9, 10, 11, 12, 13],
        ["IFCDOORPANELPROPERTIES"] = [4],
        ["IFCWINDOWLININGPROPERTIES"] = [4, 5, 6, 7],
        ["IFCWINDOWPANELPROPERTIES"] = [6, 7],
        ["IFCQUANTITYLENGTH"] = [3],
    };

    /// <summary>Attributes that hold a list of lengths (point coordinates).</summary>
    private static readonly Dictionary<string, int[]> Lists = new()
    {
        ["IFCCARTESIANPOINT"] = [0],
    };

    internal static string ToMillimetres(string type, string args, bool exactQuantities = false)
    {
        bool scalar = Scalars.TryGetValue(type, out var scalarIndices);
        bool list = Lists.TryGetValue(type, out var listIndices);
        if (!scalar && !list) return args;

        var parts = SplitTopLevel(args);
        for (int i = 0; i < parts.Count; i++)
        {
            // (the -0. offset of a building pad's layer set usage is a written negative zero, which a shift would lose)
            if (scalar && Array.IndexOf(scalarIndices!, i) >= 0)
                parts[i] = type == "IFCMATERIALLAYERSETUSAGE" && parts[i].Trim() == "-0." ? parts[i] : (exactQuantities && type == "IFCQUANTITYLENGTH" ? ShiftQuantity(parts[i]) : ShiftNumber(parts[i]));
            else if (list && Array.IndexOf(listIndices!, i) >= 0) parts[i] = ShiftList(parts[i]);
        }
        return string.Join(',', parts);
    }

    /// <summary>The conversion of the finished entity list for a unit family. Millimetre is <see cref="ToMillimetres"/>
    /// (what every file wrote before the family existed); Metre keeps the metres of the table and moves the
    /// millimetre-written attributes; the other families convert lengths, areas and volumes to their corresponding units.</summary>
    internal static Func<string, string, string> Converter(IfcUnitFamily family, bool exactQuantities = false) =>
        family == IfcUnitFamily.Millimetre ? (type, args) => ToMillimetres(type, args, exactQuantities) : (type, args) => ToFamily(type, args, family, exactQuantities);

    internal const double MetresPerFoot = 0.3048;
    internal const double SquareMetresPerSquareFoot = MetresPerFoot * MetresPerFoot;
    internal const double CubicMetresPerCubicFoot = MetresPerFoot * MetresPerFoot * MetresPerFoot;
    internal const double MetresPerInch = 0.0254;
    internal const double SquareMetresPerSquareInch = MetresPerInch * MetresPerInch;
    internal const double CubicMetresPerCubicInch = MetresPerInch * MetresPerInch * MetresPerInch;

    /// <summary>Attributes written in millimetres at their source (the overall size of a door or window, the nominal diameter
    /// and the length of a bar, the radius of a swept disk).</summary>
    private static readonly Dictionary<string, int[]> Millimetres = new()
    {
        ["IFCDOOR"] = [8, 9],
        ["IFCWINDOW"] = [8, 9],
        ["IFCREINFORCINGBAR"] = [9, 11],
        ["IFCSWEPTDISKSOLID"] = [1],
    };

    /// <summary>Attributes written in square and cubic metres at their source.</summary>
    private static readonly Dictionary<string, int[]> SquareMetres = new()
    {
        ["IFCQUANTITYAREA"] = [3],
        ["IFCREINFORCINGBAR"] = [10],
    };
    private static readonly Dictionary<string, int[]> CubicMetres = new()
    {
        ["IFCQUANTITYVOLUME"] = [3],
    };

    private enum SourceUnit { Metre, Millimetre, SquareMetre, CubicMetre }

    /// <summary>What a value written in the source unit is divided by to give the unit of the family.</summary>
    private static double Divisor(SourceUnit source, IfcUnitFamily family) => (source, family) switch
    {
        (SourceUnit.Metre, IfcUnitFamily.Foot) => MetresPerFoot,
        (SourceUnit.Millimetre, IfcUnitFamily.Foot) => 304.8,
        (SourceUnit.Millimetre, IfcUnitFamily.Metre) => 1000,
        (SourceUnit.SquareMetre, IfcUnitFamily.Foot) => SquareMetresPerSquareFoot,
        (SourceUnit.CubicMetre, IfcUnitFamily.Foot) => CubicMetresPerCubicFoot,
        // centimetre: powers of ten, applied exactly (see Quotient and DecimalDivisor)
        (SourceUnit.Metre, IfcUnitFamily.Centimetre) => 0.01,
        (SourceUnit.Millimetre, IfcUnitFamily.Centimetre) => 10,
        (SourceUnit.SquareMetre, IfcUnitFamily.Centimetre) => 0.0001,
        (SourceUnit.CubicMetre, IfcUnitFamily.Centimetre) => 0.000001,
        (SourceUnit.Metre, IfcUnitFamily.Inch) => MetresPerInch,
        (SourceUnit.Millimetre, IfcUnitFamily.Inch) => 25.4,
        (SourceUnit.SquareMetre, IfcUnitFamily.Inch) => SquareMetresPerSquareInch,
        (SourceUnit.CubicMetre, IfcUnitFamily.Inch) => CubicMetresPerCubicInch,
        _ => 1,
    };

    // The measure types of a property value that carry a length, an area or a volume, and the unit the exporter writes them in.
    private static readonly (string Measure, SourceUnit Source)[] Measures =
    [
        ("IFCPOSITIVELENGTHMEASURE(", SourceUnit.Millimetre), ("IFCLENGTHMEASURE(", SourceUnit.Millimetre),
        ("IFCAREAMEASURE(", SourceUnit.SquareMetre), ("IFCVOLUMEMEASURE(", SourceUnit.CubicMetre),
    ];

    internal static string ToFamily(string type, string args, IfcUnitFamily family, bool exact = false)
    {
        if (family == IfcUnitFamily.Millimetre) return ToMillimetres(type, args, exact);
        bool metres = Scalars.ContainsKey(type) || Lists.ContainsKey(type);
        bool millimetres = Millimetres.ContainsKey(type), squares = SquareMetres.ContainsKey(type), cubes = CubicMetres.ContainsKey(type);
        bool property = type == "IFCPROPERTYSINGLEVALUE";
        if (!metres && !millimetres && !squares && !cubes && !property) return args;

        var parts = SplitTopLevel(args);
        for (int i = 0; i < parts.Count; i++)
        {
            if (metres && Scalars.TryGetValue(type, out var scalarIndices) && Array.IndexOf(scalarIndices, i) >= 0)
                parts[i] = type == "IFCMATERIALLAYERSETUSAGE" && parts[i].Trim() == "-0." ? parts[i] : Scale(parts[i], Divisor(SourceUnit.Metre, family), exact && type.StartsWith("IFCQUANTITY", StringComparison.Ordinal));
            else if (metres && Lists.TryGetValue(type, out var listIndices) && Array.IndexOf(listIndices, i) >= 0)
                parts[i] = ScaleList(parts[i], Divisor(SourceUnit.Metre, family));
            else if (millimetres && Array.IndexOf(Millimetres[type], i) >= 0) parts[i] = Scale(parts[i], Divisor(SourceUnit.Millimetre, family));
            else if (squares && Array.IndexOf(SquareMetres[type], i) >= 0) parts[i] = Scale(parts[i], Divisor(SourceUnit.SquareMetre, family), exact && type == "IFCQUANTITYAREA");
            else if (cubes && Array.IndexOf(CubicMetres[type], i) >= 0) parts[i] = Scale(parts[i], Divisor(SourceUnit.CubicMetre, family), exact);
            else if (property && i == 2) parts[i] = ScaleMeasure(parts[i], family, exact);
        }
        return string.Join(',', parts);
    }

    /// <summary>The nominal value of a property, IFCLENGTHMEASURE(x) and the like, scaled when its measure type is a length,
    /// an area or a volume.</summary>
    private static string ScaleMeasure(string value, IfcUnitFamily family, bool exact)
    {
        string text = value.Trim();
        foreach (var (measure, source) in Measures)
        {
            if (!text.StartsWith(measure, StringComparison.Ordinal) || !text.EndsWith(')')) continue;
            return measure + Scale(text[measure.Length..^1], Divisor(source, family), exact) + ")";
        }
        return value;
    }

    private static string ScaleList(string list, double divisor)
    {
        string trimmed = list.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '(' || trimmed[^1] != ')') return list;
        var items = SplitTopLevel(trimmed[1..^1]);
        for (int i = 0; i < items.Count; i++) items[i] = Scale(items[i], divisor);
        return "(" + string.Join(',', items) + ")";
    }

    /// <summary>A real literal divided by a divisor, printed with the digits that identify the double; anything that is not a plain
    /// number ($, *, a reference) is returned unchanged. A divisor of 1 or an exact zero keeps the literal.</summary>
    private static string Scale(string token, double divisor, bool exactReal = false)
    {
        string text = token.Trim();
        if (divisor == 1 || text.Length == 0 || !(char.IsDigit(text[0]) || text[0] is '-' or '+' or '.')) return token;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return token;
        if (value == 0) return token;
        if (exactReal) return StepWriter.RealExact(Quotient(value, divisor));
        // a power-of-ten divisor moves the decimal point of the literal, as metres to millimetres does
        string result = DecimalDivisor(divisor) is decimal shift && ShiftLiteral(text, shift) is string shifted
            ? shifted
            : Quotient(value, divisor).ToString("R", CultureInfo.InvariantCulture);
        int exponent = result.IndexOfAny(['E', 'e']);
        if (exponent >= 0)
        {
            string mantissa = result[..exponent];
            return (mantissa.Contains('.') ? mantissa : mantissa + ".0") + result[exponent..];
        }
        return result.Contains('.') ? result : result + ".";
    }

    /// <summary>The divisors of the table that are powers of ten, as decimals. 0.01, 0.0001 and 0.000001 (the centimetre family) are
    /// not doubles, so dividing by them leaves low-digit noise (0.29 / 0.01 is 28.999999999999996).</summary>
    private static decimal? DecimalDivisor(double divisor) => divisor switch
    {
        1000 => 1000m,
        10 => 10m,
        0.01 => 0.01m,
        0.0001 => 0.0001m,
        0.000001 => 0.000001m,
        _ => null,
    };

    /// <summary>The literal divided by an exact decimal divisor, or null when it does not fit a decimal. A divisor below 1 multiplies, so
    /// the literals from 1e22 up, which are not geometry, are left to the double instead of overflowing.</summary>
    private static string? ShiftLiteral(string text, decimal divisor)
    {
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal exact)) return null;
        if (divisor < 1 && Math.Abs(exact) >= 1e22m) return null;
        return (exact / divisor).ToString("0.############################", CultureInfo.InvariantCulture);
    }

    /// <summary>The value divided by the divisor; a decimal divisor below 1 is applied as its exact reciprocal (a multiplication by
    /// 100, 10000 or 1000000), which rounds once.</summary>
    private static double Quotient(double value, double divisor) =>
        DecimalDivisor(divisor) is decimal d && d < 1 ? value * (double)(1 / d) : value / divisor;

    /// <summary>The attribute text split at the commas outside strings and parentheses.</summary>
    internal static List<string> SplitTopLevel(string args)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        bool inString = false;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c == '\'') inString = !inString;
            else if (inString) continue;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(args[start..i]);
                start = i + 1;
            }
        }
        parts.Add(args[start..]);
        return parts;
    }

    private static string ShiftList(string list)
    {
        string trimmed = list.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '(' || trimmed[^1] != ')') return list;
        var items = SplitTopLevel(trimmed[1..^1]);
        for (int i = 0; i < items.Count; i++) items[i] = ShiftNumber(items[i]);
        return "(" + string.Join(',', items) + ")";
    }

    /// <summary>A quantity real times 1000 for the current profile, whose quantity literals are the exact text of a double (see
    /// <see cref="StepWriter.RealExact"/>): moving the decimal point of such a text gives digits of no double, so the double is scaled
    /// and printed again.</summary>
    private static string ShiftQuantity(string token)
    {
        string text = token.Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && text.Length > 0 && !char.IsLetter(text[0])
            ? StepWriter.RealExact(value * 1000) : token;
    }

    /// <summary>A real literal times 1000; anything that is not a plain number ($, *, a reference) is returned unchanged.</summary>
    internal static string ShiftNumber(string token)
    {
        string text = token.Trim();
        if (text.Length == 0 || !(char.IsDigit(text[0]) || text[0] is '-' or '+' or '.')) return token;
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real)) return token;
            return StepWriter.Real(real * 1000);
        }
        string shifted = (value * 1000m).ToString("0.############################", CultureInfo.InvariantCulture);
        return shifted.Contains('.') ? shifted : shifted + ".";
    }
}
