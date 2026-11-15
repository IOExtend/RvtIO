using System.Globalization;
using System.Text.Json.Serialization;

namespace RvtIO.Model
{
    /// <summary>
    /// An element parameter - mirrors the Revit API Parameter class.
    /// Value is typed according to StorageType: double, int, string, or int (ElementId).
    /// </summary>
    public class Parameter
    {
        /// <summary>A copy of this parameter with another value.</summary>
        internal Parameter ShallowCopy(object? value)
        {
            var copy = (Parameter)MemberwiseClone();
            copy.Value = value;
            return copy;
        }

        /// <summary>GUID of shared/external parameters (parsed from ForgeId). Null for built-in params.
        /// Invariant per definition id - serialized once in the document's ParameterDefinitions table.</summary>
        [JsonIgnore]
        public Guid? Guid { get; set; }

        /// <summary>
        /// For built-in params: the BIP enum int value (same as Definition.Id, negative).
        /// For user-defined params: the param definition element ID (positive).
        /// This is the key into the document's ParameterDefinitions table.
        /// </summary>
        public int Id { get; set; }

        /// <summary>Definition (name/BIP). Invariant per <see cref="Id"/>; hoisted into the
        /// document-level ParameterDefinitions table for JSON, so it is not serialized inline.</summary>
        [JsonIgnore]
        public Definition Definition { get; set; } = new();

        /// <summary>Storage type. Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public StorageType StorageType { get; set; }

        /// <summary>
        /// Typed value: double, int, string, or int (ElementId).
        /// Null when the value could not be resolved from the binary data.
        /// This is the only per-element-instance field of a parameter.
        /// </summary>
        public object? Value { get; set; }

        /// <summary>True if the parameter is read-only (computed, system-generated, etc.).
        /// Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public bool IsReadOnly { get; set; }

        /// <summary>Forge TypeId string for user-defined/shared params (e.g. "autodesk.parameter.aec.revit.external.-1:GUID-1.0.0").
        /// Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public string? ForgeId { get; set; }

        /// <summary>Measurement spec type (e.g. "autodesk.spec.aec:length-2.0.0").
        /// Resolved from ForgeId via 0x0B3A blocks, or from BIP name heuristics.</summary>
        [JsonIgnore]
        public string? SpecType { get; set; }

        /// <summary>Display unit symbol (e.g. "mm", "m^2", "degC").
        /// Resolved from SpecType via 0x0FBD ForgeTypeId spec registry.
        /// Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public string? UnitSymbol { get; set; }

        /// <summary>Conversion factor from Revit internal units to display units.
        /// E.g. for length: feet -> mm = 304.8.</summary>
        [JsonIgnore]
        public double? ConversionFactor { get; set; }

        /// <summary>True when this parameter's value came from an approximate fallback
        /// (e.g. dominant bounding-box dimension) rather than an exact decoded source (e.g.
        /// the element's serialized location curve). Per-instance, like <see cref="Value"/> - not invariant
        /// per definition id. Defaults to false (exact).</summary>
        [JsonIgnore]
        public bool IsApproximate { get; set; }

        /// <summary>The value was set by <c>ApplyNativeParams</c>: a parameter store of the element or a rule over decoded fields.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal bool FromNativeFields { get; set; }

        /// <summary>Whether this parameter has a non-null value.</summary>
        [JsonIgnore]
        public bool HasValue => Value != null;

        /// <summary>Get the value as double. Returns 0 if not a Double parameter or value is null.</summary>
        public double AsDouble()
        {
            if (Value is double d) return d;
            if (Value is int i) return i;
            return 0;
        }

        /// <summary>Get the value as integer. Returns 0 if not an Integer parameter or value is null.</summary>
        public int AsInteger()
        {
            if (Value is int i) return i;
            if (Value is double d) return (int)d;
            return 0;
        }

        /// <summary>Get the value as string. Returns null if not a String parameter.</summary>
        public string? AsString()
        {
            if (Value is string s) return s;
            return Value?.ToString();
        }

        /// <summary>Get the value as ElementId (int). Returns -1 (InvalidElementId) if not an ElementId parameter.</summary>
        public int AsElementId()
        {
            if (Value is int i) return i;
            return -1;
        }

        /// <summary>
        /// Get a formatted display string for the value.
        /// Uses ForgeTypeId spec registry for precise unit conversion when available,
        /// falls back to parameter name heuristics.
        /// </summary>
        public string? AsValueString()
        {
            if (Value == null) return null;
            if (Value is double d)
            {
                // Use ForgeTypeId-derived unit info if available
                if (UnitSymbol != null && ConversionFactor.HasValue && ConversionFactor.Value != 0)
                {
                    double displayed = d * ConversionFactor.Value;
                    return string.IsNullOrWhiteSpace(UnitSymbol)
                        ? displayed.ToString("G6", CultureInfo.InvariantCulture)
                        : $"{displayed:G6} {UnitSymbol}";
                }

                // If the parameter has an explicit SpecType, evaluate spec category with highest priority.
                if (!string.IsNullOrWhiteSpace(SpecType))
                {
                    var specDisplay = ResolveSpecDisplay(SpecType);
                    if (specDisplay.HasValue)
                    {
                        var (symbol, factor) = specDisplay.Value;
                        if (string.IsNullOrWhiteSpace(symbol))
                        {
                            return d.ToString("G", CultureInfo.InvariantCulture);
                        }
                        double displayed = d * factor;
                        return $"{displayed:G6} {symbol}";
                    }
                }

                // Fallback: heuristic based on parameter or BIP name tokens
                string? name = Definition?.Name;
                string? bip = Definition?.BuiltInParameter;

                // Dimensionless coefficients, scales, ratios, and factors must not be inferred as dimensional lengths/areas/volumes
                if (HasWordToken(name, bip, ScalarKeywords))
                {
                    return d.ToString("G", CultureInfo.InvariantCulture);
                }

                if (HasWordToken(name, bip, LengthKeywords))
                {
                    return $"{d * 304.8:F1} mm";
                }
                if (HasWordToken(name, bip, AreaKeywords))
                    return $"{d * 0.09290304:F3} m²";
                if (HasWordToken(name, bip, VolumeKeywords))
                    return $"{d * 0.0283168466:F4} m³";
                if (HasWordToken(name, bip, AngleKeywords))
                    return $"{d * 180 / Math.PI:F2}°";
                return d.ToString("G", CultureInfo.InvariantCulture);
            }
            return Value.ToString();
        }

        private static (string? symbol, double factor)? ResolveSpecDisplay(string specType)
        {
            string? cat = GetSpecCategory(specType);
            if (cat == null) return null;

            if (cat.Equals("length", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("barDiameter", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("cableTraySize", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("conduitSize", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("crackWidth", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("ductInsulationThickness", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("ductLiningThickness", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("ductSize", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("pipeInsulationThickness", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("pipeSize", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("pipeDimension", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("reinforcementCover", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("reinforcementLength", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("reinforcementSpacing", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("wireDiameter", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("displacement", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("sectionDimension", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("decimalSheetLength", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("sheetLength", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("length", StringComparison.OrdinalIgnoreCase))
            {
                return ("mm", 304.8);
            }

            if (cat.Equals("area", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("crossSection", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("surfaceAreaPerUnitLength", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("area", StringComparison.OrdinalIgnoreCase))
            {
                return ("m\u00B2", 0.09290304);
            }

            if (cat.Equals("volume", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("reinforcementVolume", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("volume", StringComparison.OrdinalIgnoreCase))
            {
                return ("m\u00B3", 0.0283168466);
            }

            if (cat.Equals("angle", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("angle", StringComparison.OrdinalIgnoreCase))
            {
                return ("\u00B0", 180.0 / Math.PI);
            }

            if (cat.Equals("number", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("integer", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("factor", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("ratio", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("unitless", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("fraction", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("slope", StringComparison.OrdinalIgnoreCase)
                || cat.Equals("demandFactor", StringComparison.OrdinalIgnoreCase))
            {
                return (null, 1.0);
            }

            return null;
        }

        private static string? GetSpecCategory(string specType)
        {
            if (string.IsNullOrWhiteSpace(specType)) return null;
            int colon = specType.LastIndexOf(':');
            string afterColon = colon >= 0 ? specType[(colon + 1)..] : specType;
            int dash = afterColon.IndexOf('-');
            return dash > 0 ? afterColon[..dash] : afterColon;
        }

        private static readonly System.Text.RegularExpressions.Regex WordTokenRegex =
            new(@"[A-Z]+(?![a-z])|[A-Z][a-z]+|[a-z]+|\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly HashSet<string> ScalarKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "SCALE", "SCALES",
            "FACTOR", "FACTORS",
            "RATIO", "RATIOS",
            "COEFFICIENT", "COEFFICIENTS",
            "PERCENT", "PERCENTAGE", "PERCENTAGES",
            "MULTIPLIER", "MULTIPLIERS"
        };

        private static readonly HashSet<string> LengthKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "WIDTH", "HEIGHT", "LENGTH", "OFFSET", "THICKNESS", "DEPTH",
            "RADIUS", "DIAMETER", "DISTANCE", "PERIMETER", "SPAN", "ELEVATION"
        };

        private static readonly HashSet<string> AreaKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "AREA", "AREAS"
        };

        private static readonly HashSet<string> VolumeKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "VOLUME", "VOLUMES"
        };

        private static readonly HashSet<string> AngleKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "ANGLE", "ANGLES", "ROTATION", "SLOPE"
        };

        private static bool HasWordToken(string? text1, string? text2, HashSet<string> keywords)
        {
            if (!string.IsNullOrEmpty(text1))
            {
                foreach (System.Text.RegularExpressions.Match m in WordTokenRegex.Matches(text1))
                {
                    if (keywords.Contains(m.Value)) return true;
                }
            }
            if (!string.IsNullOrEmpty(text2))
            {
                foreach (System.Text.RegularExpressions.Match m in WordTokenRegex.Matches(text2))
                {
                    if (keywords.Contains(m.Value)) return true;
                }
            }
            return false;
        }
    }
}
