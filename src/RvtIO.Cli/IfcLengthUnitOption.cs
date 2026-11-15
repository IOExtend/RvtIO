namespace RvtIO.Cli;

/// <summary>The <c>--ifc-length-unit &lt;mm|m|ft&gt;</c> option: overrides the length unit family declared in the IFC file and scales all
/// written lengths, areas and volumes to match (<see cref="RvtIO.Ifc.IfcExporter.LengthUnit"/>).</summary>
internal static class IfcLengthUnitOption
{
    public const string Name = "--ifc-length-unit";

    /// <summary>Reads <c>--ifc-length-unit</c> from <paramref name="args"/>. Without the option <paramref name="unit"/> is null.
    /// Accepts <c>mm</c>/<c>millimetre</c>/<c>millimeter</c>, <c>m</c>/<c>metre</c>/<c>meter</c>, and <c>ft</c>/<c>foot</c>/<c>feet</c>
    /// (case-insensitive, singular or plural). Returns false with <paramref name="error"/> set when the option has no value, is given
    /// twice or names an unsupported unit.</summary>
    public static bool TryParse(string[] args, out RvtIO.Ifc.IfcUnitFamily? unit, out string? error)
    {
        unit = null;
        error = null;
        bool seen = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != Name) continue;
            if (seen || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{Name} requires one supported IFC length unit: mm (millimetre), m (metre) or ft (foot).";
                unit = null;
                return false;
            }
            seen = true;
            if (!TryParseValue(args[++i], out var parsed))
            {
                error = $"{Name} requires one supported IFC length unit: mm (millimetre), m (metre) or ft (foot).";
                unit = null;
                return false;
            }
            unit = parsed;
        }
        return true;
    }

    private static bool TryParseValue(string raw, out RvtIO.Ifc.IfcUnitFamily unit)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "mm" or "millimetre" or "millimeter" or "millimetres" or "millimeters":
                unit = RvtIO.Ifc.IfcUnitFamily.Millimetre;
                return true;
            case "m" or "metre" or "meter" or "metres" or "meters":
                unit = RvtIO.Ifc.IfcUnitFamily.Metre;
                return true;
            case "ft" or "foot" or "feet":
                unit = RvtIO.Ifc.IfcUnitFamily.Foot;
                return true;
            default:
                unit = default;
                return false;
        }
    }
}
