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
            copy._definition = Definition;
            copy._metadata = _metadata?.Copy();
            if (!Equals(copy._value, value))
                copy.HasResolvedZeroElementId = false;
            copy._value = value;
            return copy;
        }

        private object? _value;
        private object? _definition;
        private Metadata? _metadata;

        internal bool HasSupplementalMetadata => _metadata != null;

        private sealed class Metadata
        {
            internal Guid? Guid;
            internal string? ForgeId;
            internal string? SpecType;
            internal string? UnitSymbol;
            internal double? ConversionFactor;
            internal long? NativeElementId;

            internal Metadata Copy() => (Metadata)MemberwiseClone();
        }

        internal sealed record DefinitionTemplate(int Id, string? Name, string? BuiltIn)
        {
            internal DefinitionTemplate(int id, string name) : this(id, name, name) { }
        }

        /// <summary>Creates an empty parameter with its own mutable definition.</summary>
        public Parameter() => _definition = new Definition();

        internal Parameter(DefinitionTemplate? definition) => _definition = definition;

        internal Parameter(Definition definition) => _definition = definition;

        /// <summary>GUID of shared/external parameters (parsed from ForgeId). Null for built-in params.
        /// Invariant per definition id - serialized once in the document's ParameterDefinitions table.</summary>
        [JsonIgnore]
        public Guid? Guid
        {
            get => _metadata?.Guid;
            set
            {
                if (value != null || _metadata != null) (_metadata ??= new()).Guid = value;
            }
        }

        /// <summary>
        /// For built-in params: the BIP enum int value (same as Definition.Id, negative).
        /// For user-defined params: the param definition element ID (positive).
        /// This is the key into the document's ParameterDefinitions table.
        /// </summary>
        public int Id { get; set; }

        /// <summary>Definition (name/BIP). Invariant per <see cref="Id"/>; hoisted into the
        /// document-level ParameterDefinitions table for JSON, so it is not serialized inline.</summary>
        [JsonIgnore]
        public Definition Definition
        {
            get => _definition is DefinitionTemplate template
                ? (Definition)(_definition = new Definition { Id = template.Id, Name = template.Name, BuiltInParameter = template.BuiltIn })
                : (Definition)_definition!;
            set => _definition = value;
        }

        internal string? DefinitionName => _definition is DefinitionTemplate template ? template.Name : ((Definition?)_definition)?.Name;

        internal static bool IsHostQuantityPlaceholder(Parameter parameter) => parameter.Id is -1012805 or -1012806 or -1012807
            && ((parameter.Value is double number && number <= 0) || (parameter.Value is int integer && integer <= 0));

        internal (int Id, string? Name, string? BuiltIn)? DefinitionFields => _definition switch
        {
            DefinitionTemplate template => (template.Id, template.Name, template.BuiltIn),
            Definition definition => (definition.Id, definition.Name, definition.BuiltInParameter),
            _ => null,
        };

        /// <summary>Storage type. Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public StorageType StorageType { get; set; }

        /// <summary>
        /// Typed value: double, int, string, or int (ElementId).
        /// Null when the value could not be resolved from the binary data.
        /// This is the only per-element-instance field of a parameter.
        /// </summary>
        public object? Value
        {
            get => _value;
            set
            {
                _value = value;
                HasNoRevitValue = false;
                HasResolvedZeroElementId = false;
            }
        }

        /// <summary>True if the parameter is read-only (computed, system-generated, etc.).
        /// Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public bool IsReadOnly { get; set; }

        /// <summary>Forge TypeId string for user-defined/shared params (e.g. "autodesk.parameter.aec.revit.external.-1:GUID-1.0.0").
        /// Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public string? ForgeId
        {
            get => _metadata?.ForgeId;
            set
            {
                if (value != null || _metadata != null) (_metadata ??= new()).ForgeId = value;
            }
        }

        /// <summary>Measurement spec type (e.g. "autodesk.spec.aec:length-2.0.0").
        /// Resolved from ForgeId via 0x0B3A blocks, or from BIP name heuristics.</summary>
        [JsonIgnore]
        public string? SpecType
        {
            get => _metadata?.SpecType;
            set
            {
                if (value != null || _metadata != null) (_metadata ??= new()).SpecType = value;
            }
        }

        /// <summary>Display unit symbol (e.g. "mm", "m^2", "degC").
        /// Resolved from SpecType via 0x0FBD ForgeTypeId spec registry.
        /// Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public string? UnitSymbol
        {
            get => _metadata?.UnitSymbol;
            set
            {
                if (value != null || _metadata != null) (_metadata ??= new()).UnitSymbol = value;
            }
        }

        /// <summary>Conversion factor from Revit internal units to display units.
        /// E.g. for length: feet -> mm = 304.8.</summary>
        [JsonIgnore]
        public double? ConversionFactor
        {
            get => _metadata?.ConversionFactor;
            set
            {
                if (value != null || _metadata != null) (_metadata ??= new()).ConversionFactor = value;
            }
        }

        /// <summary>True when this parameter's value came from an approximate fallback
        /// (e.g. dominant bounding-box dimension) rather than an exact decoded source (e.g.
        /// the element's serialized location curve). Per-instance, like <see cref="Value"/> - not invariant
        /// per definition id. Defaults to false (exact).</summary>
        [JsonIgnore]
        public bool IsApproximate { get; set; }

        /// <summary>The value was set by <c>ApplyNativeParams</c>: a parameter store of the element or a rule over decoded fields.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal bool FromNativeFields { get; set; }

        /// <summary>True when the parameter slot exists on the element with a synthesized default or
        /// unset sentinel, rather than a value stored or computed in Revit.</summary>
        [JsonIgnore]
        internal bool HasNoRevitValue { get; set; }

        /// <summary>True when a zero <see cref="StorageType.ElementId"/> value resolves to an actual
        /// target element with id <c>0</c> in this parameter's reference domain (such as project phase <c>0</c>).</summary>
        [JsonIgnore]
        internal bool HasResolvedZeroElementId { get; set; }

        /// <summary>True when <paramref name="parameterId"/> stores a reference to a <c>ProjectPhase</c> element.</summary>
        internal static bool IsPhaseElementIdParameter(int parameterId) =>
            parameterId is -1012100 or -1012101 or -1012102 or -1012112 or -1012113
                or -1114324 or -1114325 or -1114326 or -1114822;

        /// <summary>True when <paramref name="parameterId"/> stores an element-type or family reference.</summary>
        internal static bool IsTypeElementIdParameter(int parameterId) =>
            parameterId is -1002050 or -1002000 or -1002051 or -1002052;

        /// <summary>True when the model contains a real <c>ProjectPhase</c> element with id <c>0</c>.</summary>
        internal static bool HasZeroProjectPhase(RevitFileInfo info, RvtIO.Internal.ParsingState? state = null)
        {
            if (state == null && info.SerializedHasZeroProjectPhase)
                return true;

            if (info.SerializedProjectPhaseIds is { } serializedPhases && Array.IndexOf(serializedPhases, 0) >= 0)
                return true;

            foreach (var element in info.Elements)
            {
                if ((element.Id == 0 && IsProjectPhaseElement(element)) || element.SerializedRoomPhaseId == 0)
                    return true;
            }

            if (state != null)
            {
                if (state.ElemClassIdx.TryGetValue(0, out var classId) && classId == 3065)
                    return true;
                if (state.NativeRootClassNames.TryGetValue(0, out var rootClass)
                    && string.Equals(rootClass, "ProjectPhase", StringComparison.Ordinal))
                    return true;
                if (state.ProjectPhaseOrderSource is { BlockCount: 1, PhaseIds: { } phaseOrder }
                    && Array.IndexOf(phaseOrder, 0) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>True when the model contains a real <c>DimensionStyle</c> element with id <c>0</c>.</summary>
        internal static bool HasZeroDimensionStyle(RevitFileInfo info, RvtIO.Internal.ParsingState? state = null)
        {
            if (state == null && info.SerializedHasZeroDimensionStyle)
                return true;

            foreach (var element in info.Elements)
            {
                if (element.Id == 0 && (element.RootClassIs("DimensionStyle") || element.NativeRecordClass == 174))
                    return true;
            }

            if (state != null)
            {
                if (state.ElemClassIdx.TryGetValue(0, out var classId) && classId == 174)
                    return true;
                if (state.NativeRootClassNames.TryGetValue(0, out var rootClass)
                    && string.Equals(rootClass, "DimensionStyle", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>True when a <c>0</c> <see cref="StorageType.ElementId"/> value on <paramref name="owner"/>
        /// for <paramref name="parameterId"/> identifies a resolved id-<c>0</c> target element in that domain.</summary>
        internal static bool IsValidZeroElementIdTarget(
            Element owner, int parameterId, bool hasZeroProjectPhase, bool hasZeroDimensionStyle)
        {
            if (hasZeroProjectPhase && IsPhaseElementIdParameter(parameterId))
                return true;

            if (hasZeroDimensionStyle && IsTypeElementIdParameter(parameterId))
                return owner.Dimension != null
                    || owner.CategoryId is -2000260 or -2000261 or -2000262 or -2000263
                    || owner.RootClassIs("Dimension")
                    || owner.RootClassIs("SpotDimension");

            return false;
        }

        [Flags]
        internal enum ZeroReferenceKinds : byte
        {
            None = 0,
            Resolved = 1,
            Phase = 2,
            DimensionStyle = 4,
        }

        internal ZeroReferenceKinds ClassifyZeroReference()
        {
            var kinds = HasResolvedZeroElementId ? ZeroReferenceKinds.Resolved : ZeroReferenceKinds.None;
            if (StorageType != StorageType.ElementId || Value is not int id || id != 0) return kinds;
            if (IsPhaseElementIdParameter(Id)) kinds |= ZeroReferenceKinds.Phase;
            if (IsTypeElementIdParameter(Id)) kinds |= ZeroReferenceKinds.DimensionStyle;
            return kinds;
        }

        /// <summary>Marks <see cref="HasResolvedZeroElementId"/> on each parameter whose <c>0</c> element-id
        /// value resolves to a real target element with id <c>0</c> in its domain.</summary>
        internal static void ResolveZeroElementIds(RevitFileInfo info, RvtIO.Internal.ParsingState? state = null,
            Action<Element>? archiveParameters = null)
        {
            archiveParameters ??= state?.ArchiveSceneParameters;
            bool hasZeroPhase = HasZeroProjectPhase(info, state);
            bool hasZeroDimStyle = HasZeroDimensionStyle(info, state);
            if (state != null)
            {
                info.SerializedHasZeroProjectPhase = hasZeroPhase;
                info.SerializedHasZeroDimensionStyle = hasZeroDimStyle;
            }
            var relevant = ZeroReferenceKinds.Resolved;
            if (hasZeroPhase) relevant |= ZeroReferenceKinds.Phase;
            if (hasZeroDimStyle) relevant |= ZeroReferenceKinds.DimensionStyle;
            foreach (var element in info.Elements)
            {
                if (element.ArchivedZeroReferences is { } kinds && (kinds & relevant) == 0) continue;
                int index = -1;
                foreach (var parameter in element.ReadParameters)
                {
                    index++;
                    bool resolves = parameter.StorageType == StorageType.ElementId && parameter.Value is int eid && eid == 0
                        && IsValidZeroElementIdTarget(element, parameter.Id, hasZeroPhase, hasZeroDimStyle);
                    if (parameter.HasResolvedZeroElementId != resolves)
                        element.Parameters[index].HasResolvedZeroElementId = resolves;
                }
                archiveParameters?.Invoke(element);
            }
        }

        private static bool IsProjectPhaseElement(Element element) =>
            element.CategoryId == -2000112
            || element.RootClassIs("ProjectPhase")
            || element.NativeRecordClass == 3065
            || string.Equals(element.Category, "Phases", StringComparison.OrdinalIgnoreCase)
            || string.Equals(element.Category, "OST_Phases", StringComparison.OrdinalIgnoreCase);

        /// <summary>True when <paramref name="elementId"/> is a resolved reference or valid option value rather than
        /// an invalid (<c>-1</c>), uninitialized (<c>0</c> without a resolved id-<c>0</c> target), or unhosted host-pointer sentinel.</summary>
        internal static bool IsSetElementId(int parameterId, int elementId, bool hasResolvedZeroTarget = false) =>
            parameterId is -1002108 or -1010303
                ? elementId > 0
                : elementId != -1 && (elementId != 0 || hasResolvedZeroTarget);

        /// <summary>
        /// Whether this parameter carries a value stored or computed in Revit rather than an unset sentinel,
        /// synthesized reader placeholder, or approximate fallback (<see cref="IsApproximate"/>).
        /// Unlike <see cref="HasValue"/> (which checks <c>Value != null</c> on the raw decoded slot), this is
        /// <c>false</c> for <see cref="StorageType.None"/>, non-finite or <c>|v| &gt;= 1e29</c> doubles,
        /// unresolved element ids (<c>-1</c>, uninitialized <c>0</c> without a resolved id-<c>0</c> target such as
        /// project phase <c>0</c>, or non-positive pointers on host-id parameters),
        /// <c>-1</c> placeholders on non-integer built-in parameters, and unstored default slots synthesized by
        /// the reader, while remaining <c>true</c> for stored <c>0</c>, <c>0.0</c>, <c>false</c>, <c>""</c>,
        /// valid resolved id-<c>0</c> targets, and valid negative <see cref="StorageType.ElementId"/> option or category values.
        /// </summary>
        [JsonIgnore]
        public bool HasRevitValue
        {
            get
            {
                if (HasNoRevitValue || IsApproximate || _value == null || StorageType == StorageType.None)
                    return false;
                return StorageType switch
                {
                    StorageType.Double => _value is double d && double.IsFinite(d) && Math.Abs(d) < 1e29,
                    StorageType.ElementId => _value is int eid && IsSetElementId(Id, eid, HasResolvedZeroElementId),
                    StorageType.Integer => _value is int iv
                        ? !(iv == -1 && (Id is >= -1019017 and <= -1019012
                            || (Id < 0 && Parsing.ApiParameterDefinitions.TryGetStorage(Id, out var apiSt) && apiSt != StorageType.Integer)))
                        : _value is bool,
                    StorageType.String => _value is string,
                    _ => false,
                };
            }
        }

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

        /// <summary>Returns the legacy document-local element key. Use <see cref="AsElementId64"/> for the source ID.</summary>
        public int AsElementId()
        {
            if (Value is int i) return i;
            return -1;
        }

        /// <summary>The exact element reference, or -1 if the parameter holds no element ID.</summary>
        public long AsElementId64() =>
            StorageType == StorageType.ElementId && Value is int key ? NativeElementId ?? key : -1;

        /// <summary>The source reference as decimal text when it differs from the local key.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SourceElementId => NativeElementId?.ToString(CultureInfo.InvariantCulture);

        internal long? NativeElementId
        {
            get => _metadata?.NativeElementId;
            set
            {
                if (value != null || _metadata != null) (_metadata ??= new()).NativeElementId = value;
            }
        }

        /// <summary>
        /// Get a formatted display string for the value.
        /// Uses ForgeTypeId spec registry for precise unit conversion when available,
        /// falls back to parameter name heuristics.
        /// </summary>
        public string? AsValueString() => AsValueString(elementResolver: null);

        /// <summary>
        /// Get a formatted display string for the value, resolving referenced element names for ElementId parameters.
        /// </summary>
        public string? AsValueString(RvtDocument? doc) => AsValueString(doc != null ? doc.GetElement : null);

        /// <summary>
        /// Get a formatted display string for the value, resolving referenced element names for ElementId parameters.
        /// </summary>
        public string? AsValueString(Func<int, Element?>? elementResolver)
        {
            if (Value == null) return null;
            if (StorageType == StorageType.ElementId && Value is int elemId)
            {
                if (elementResolver != null && elementResolver(elemId) is { } target)
                {
                    if (!string.IsNullOrEmpty(target.Name))
                        return target.Name;
                }
                if (elemId < 0 && RvtIO.Lookup.EnglishCategoryNames.Get(elemId) is { } catName)
                {
                    return catName;
                }
                return AsElementId64().ToString(CultureInfo.InvariantCulture);
            }
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
                    return d.ToString("G", CultureInfo.InvariantCulture);
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

        /// <summary>
        /// Formats an ElementId parameter as a Navisworks NamedConstant string, matching Autodesk Navisworks Exporter:
        /// <c>{Type/Category} "{Name}", #{Id}</c> (e.g. <c>WallType "Generic - 200mm", #123456</c>).
        /// </summary>
        public string? AsNavisworksValueString(Func<int, Element?>? elementResolver = null)
        {
            if (Value == null) return null;
            if (StorageType == StorageType.ElementId && Value is int elemId)
            {
                if (elementResolver != null && elementResolver(elemId) is { } target)
                {
                    string typeName = target.Category ?? target.FamilyName ?? "Element";
                    string name = target.Name ?? string.Empty;
                    return !string.IsNullOrEmpty(name)
                        ? $"{typeName} \"{name}\", #{elemId}"
                        : $"{typeName}, #{elemId}";
                }
                if (elemId < 0 && RvtIO.Lookup.EnglishCategoryNames.Get(elemId) is { } catName)
                {
                    return $"{catName}, #{elemId}";
                }
                return $"#{elemId}";
            }
            return AsValueString(elementResolver);
        }

        private static readonly HashSet<string> LengthSpecCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "length",
            "barDiameter",
            "cableTraySize",
            "conduitSize",
            "crackWidth",
            "ductInsulationThickness",
            "ductLiningThickness",
            "ductSize",
            "pipeInsulationThickness",
            "pipeSize",
            "pipeDimension",
            "reinforcementCover",
            "reinforcementLength",
            "reinforcementSpacing",
            "wireDiameter",
            "displacement",
            "sectionDimension",
            "decimalSheetLength",
            "sheetLength",
        };

        private static readonly HashSet<string> AreaSpecCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "area",
            "crossSection",
        };

        private static readonly HashSet<string> VolumeSpecCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "volume",
            "reinforcementVolume",
        };

        private static readonly HashSet<string> AngleSpecCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "angle",
        };

        private static readonly HashSet<string> ScalarSpecCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "number",
            "integer",
            "factor",
            "ratio",
            "unitless",
            "fraction",
            "slope",
            "demandFactor",
        };

        private static (string? symbol, double factor)? ResolveSpecDisplay(string specType)
        {
            string? cat = GetSpecCategory(specType);
            if (cat == null) return null;

            if (LengthSpecCategories.Contains(cat))
            {
                return ("mm", 304.8);
            }

            if (AreaSpecCategories.Contains(cat))
            {
                return ("m\u00B2", 0.09290304);
            }

            if (VolumeSpecCategories.Contains(cat))
            {
                return ("m\u00B3", 0.0283168466);
            }

            if (AngleSpecCategories.Contains(cat))
            {
                return ("\u00B0", 180.0 / Math.PI);
            }

            if (ScalarSpecCategories.Contains(cat))
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
