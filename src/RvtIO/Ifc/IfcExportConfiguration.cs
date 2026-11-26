using System.Text.Json;
using System.Text.Json.Serialization;

namespace RvtIO.Ifc;

/// <summary>Target IFC schema release written to the STEP header and applied to entity and attribute mapping.</summary>
public enum IfcSchema
{
    /// <summary>IFC2x3 schema (<c>IFC2X3</c>). Default schema preserving existing Coordination View behavior.</summary>
    Ifc2x3,

    /// <summary>IFC4 schema (<c>IFC4</c>, ISO 16739-1:2018 / IFC4 ADD2 TC1).</summary>
    Ifc4,
}

/// <summary>Target IFC Model View Definition (MVD) exchange scenario declared in <c>FILE_DESCRIPTION</c> and enforced during export.</summary>
public enum IfcModelView
{
    /// <summary>Default model view for the selected <see cref="IfcSchema"/> and <see cref="IfcProfile"/>.</summary>
    Default,

    /// <summary>IFC2x3 Coordination View (<c>CoordinationView</c>, legacy profile default).</summary>
    CoordinationView,

    /// <summary>IFC2x3 Coordination View 2.0 (<c>CoordinationView_V2.0</c>, current profile default).</summary>
    CoordinationView20,

    /// <summary>IFC4 Reference View 1.2 (<c>ReferenceView_V1.2</c>, default IFC4 view).</summary>
    ReferenceView12,

    /// <summary>IFC4 Design Transfer View 1.0 (<c>DesignTransferView_V1.0</c>).</summary>
    DesignTransferView10,
}

/// <summary>Named IFC export setup presets aligned across library API and CLI.</summary>
public enum IfcExportPresetKind
{
    /// <summary>Default exporter setup (<c>IFC2X3</c>, <c>CoordinationView_V2.0</c>, <c>Current</c> profile, base quantities on).</summary>
    Default,

    /// <summary>IFC2x3 Coordination View 2.0 (<c>Current</c> profile).</summary>
    Ifc2x3CoordinationView20,

    /// <summary>IFC2x3 Coordination View (<c>Legacy</c> compatibility profile).</summary>
    Ifc2x3CoordinationViewLegacy,

    /// <summary>IFC4 Reference View 1.2 (<c>ReferenceView_V1.2</c>, tessellated complex geometry, <c>IfcWall</c>, <c>Qto_*BaseQuantities</c>).</summary>
    Ifc4ReferenceView12,

    /// <summary>IFC4 Design Transfer View 1.0 (<c>DesignTransferView_V1.0</c>, BRep/extrusion fidelity, <c>Qto_*BaseQuantities</c>).</summary>
    Ifc4DesignTransferView10,
}

/// <summary>How building phase filtering selects model elements.</summary>
public enum IfcPhaseScope
{
    /// <summary>Profile default (last phase for <see cref="IfcProfile.Current"/>; all phases for <see cref="IfcProfile.Legacy"/>).</summary>
    Default,

    /// <summary>Export elements present in the document's last phase.</summary>
    LastPhase,

    /// <summary>Export elements from all phases.</summary>
    AllPhases,

    /// <summary>Export elements present in <see cref="IfcPhaseConfiguration.PhaseId"/>.</summary>
    SpecificPhase,
}

/// <summary>How rooms and spaces are filtered by phase.</summary>
public enum IfcRoomPhaseScope
{
    /// <summary>Follow the element phase scope.</summary>
    FollowPhaseScope,

    /// <summary>Export rooms from the document's last phase.</summary>
    LastPhase,

    /// <summary>Export rooms from all phases.</summary>
    AllPhases,

    /// <summary>Export rooms belonging to <see cref="IfcPhaseConfiguration.RoomPhaseId"/>.</summary>
    SpecificPhase,
}

/// <summary>Coordinate frame used for opening <c>Width</c> and <c>Height</c> base quantities.</summary>
public enum IfcOpeningQuantityFrame
{
    /// <summary>Use the host opening plane (width along wall, vertical height). Default.</summary>
    HostAxes,

    /// <summary>Use legacy swapped opening axes.</summary>
    LegacyOpeningAxes,
}

/// <summary>Geometry tessellation level of detail requested for faceted elements.</summary>
public enum IfcLevelOfDetail
{
    /// <summary>Extra-low tessellation detail (not currently differentiated; fails validation if requested).</summary>
    ExtraLow,

    /// <summary>Low tessellation detail (not currently differentiated; fails validation if requested).</summary>
    Low,

    /// <summary>Medium tessellation detail.</summary>
    Medium,

    /// <summary>High / exact decoded geometry detail (default exporter behavior).</summary>
    High,
}

/// <summary>Project coordinate origin convention for site and building placement.</summary>
public enum IfcCoordinateBase
{
    /// <summary>Current exporter default: shared survey transform on <c>IfcSite</c> with model internal coordinates.</summary>
    SharedCoordinates,

    /// <summary>Project Base Point only (not yet separately configurable; reports unsupported if selected).</summary>
    ProjectBasePoint,

    /// <summary>Survey Point only (not yet separately configurable; reports unsupported if selected).</summary>
    SurveyPoint,

    /// <summary>Internal origin without shared transform on site (not yet separately configurable; reports unsupported if selected).</summary>
    InternalOrigin,
}

/// <summary>Severity of a configuration validation finding.</summary>
public enum IfcConfigurationDiagnosticSeverity
{
    /// <summary>Advisory notice or downgraded capability note.</summary>
    Warning,

    /// <summary>Unsupported or invalid option combination that blocks export.</summary>
    Error,
}

/// <summary>Implementation level of an export configuration setting in the capability matrix.</summary>
public enum IfcCapabilitySupportStatus
{
    /// <summary>Fully implemented across supported schemas.</summary>
    Supported,

    /// <summary>Implemented for specific schemas, model views, or profiles.</summary>
    SchemaOrViewScoped,

    /// <summary>Declared in the configuration schema for Revit parity but not yet implemented; explicitly rejected if set away from default.</summary>
    Unsupported,
}

/// <summary>A single validation issue or warning reported for an <see cref="IfcExportConfiguration"/>.</summary>
/// <param name="Code">Stable machine-readable issue code (for example <c>incompatible-schema-model-view</c>).</param>
/// <param name="Severity">Error or warning.</param>
/// <param name="SettingPath">Dotted configuration setting path (for example <c>schema.modelView</c>).</param>
/// <param name="Message">Human-readable description of the conflict or unsupported setting.</param>
public sealed record IfcConfigurationDiagnostic(
    string Code,
    IfcConfigurationDiagnosticSeverity Severity,
    string SettingPath,
    string Message);

/// <summary>Entry in the published capability matrix describing what each configuration setting supports.</summary>
/// <param name="SettingPath">Dotted JSON setting path.</param>
/// <param name="Group">Logical configuration section.</param>
/// <param name="Name">Human-readable setting name.</param>
/// <param name="Status">Implementation status.</param>
/// <param name="SupportedValues">Accepted values or active behavior scope.</param>
/// <param name="DefaultValue">Default value when omitted.</param>
/// <param name="Notes">Behavioral notes and schema/MVD caveats.</param>
public sealed record IfcExportCapability(
    string SettingPath,
    string Group,
    string Name,
    IfcCapabilitySupportStatus Status,
    string SupportedValues,
    string DefaultValue,
    string Notes);

/// <summary>Schema, model view, and compatibility profile settings.</summary>
public sealed record IfcSchemaConfiguration
{
    /// <summary>Target IFC schema release (<see cref="IfcSchema.Ifc2x3"/> or <see cref="IfcSchema.Ifc4"/>).</summary>
    public IfcSchema? Schema { get; init; }

    /// <summary>Target Model View Definition; <see cref="IfcModelView.Default"/> selects the default view for the schema and profile.</summary>
    public IfcModelView? ModelView { get; init; }

    /// <summary>Behavior profile (<see cref="IfcProfile.Current"/> or <see cref="IfcProfile.Legacy"/>), independent of schema and MVD.</summary>
    public IfcProfile? Profile { get; init; }
}

/// <summary>Element filtering and view scope settings.</summary>
public sealed record IfcElementScopeConfiguration
{
    /// <summary>Restrict export to elements visible in a specific Revit view (not implemented; must remain null/false).</summary>
    public bool? ExportElementsInCurrentViewOnly { get; init; }

    /// <summary>Include 2D plan/annotation linework beyond grid footprints (not implemented; must remain null/false).</summary>
    public bool? ExportAnnotations2D { get; init; }

    /// <summary>Export linked RVT files (not implemented; must remain null/false).</summary>
    public bool? ExportLinkedFiles { get; init; }

    /// <summary>Honor element/type <c>Export to IFC</c> parameter (default <c>true</c>).</summary>
    public bool? RespectExportToIfcParameter { get; init; }

    /// <summary>Split multi-storey walls and columns by level (not implemented; must remain null/false).</summary>
    public bool? SplitWallsAndColumnsByLevel { get; init; }
}

/// <summary>Phase selection settings for model elements and rooms.</summary>
public sealed record IfcPhaseConfiguration
{
    /// <summary>Element phase selection mode.</summary>
    public IfcPhaseScope? Scope { get; init; }

    /// <summary>Native Revit phase element id when <see cref="Scope"/> is <see cref="IfcPhaseScope.SpecificPhase"/>.</summary>
    public int? PhaseId { get; init; }

    /// <summary>Room/space phase selection mode.</summary>
    public IfcRoomPhaseScope? RoomScope { get; init; }

    /// <summary>Native Revit room phase element id when <see cref="RoomScope"/> is <see cref="IfcRoomPhaseScope.SpecificPhase"/>.</summary>
    public int? RoomPhaseId { get; init; }
}

/// <summary>Coordinate origin, length unit family, and deterministic header timestamp settings.</summary>
public sealed record IfcCoordinateAndUnitConfiguration
{
    /// <summary>Length unit family written to the IFC file (<c>Millimetre</c>, <c>Metre</c>, or <c>Foot</c>); <c>null</c> uses the profile default.</summary>
    public IfcUnitFamily? LengthUnit { get; init; }

    /// <summary>Coordinate base convention.</summary>
    public IfcCoordinateBase? CoordinateBase { get; init; }

    /// <summary>Optional fixed UTC timestamp for deterministic <c>FILE_NAME</c> header output.</summary>
    public DateTimeOffset? HeaderTimestamp { get; init; }
}

/// <summary>Geometry representation and tessellation detail settings.</summary>
public sealed record IfcGeometryConfiguration
{
    /// <summary>Requested geometry detail level (<see cref="IfcLevelOfDetail.High"/> supported).</summary>
    public IfcLevelOfDetail? DetailLevel { get; init; }

    /// <summary>When <c>true</c> under IFC4, converts faceted BRep and surface-model shapes into <c>IfcPolygonalFaceSet</c> / <c>IfcTriangulatedFaceSet</c> tessellations (required for Reference View 1.2).</summary>
    public bool? PreferTessellation { get; init; }

    /// <summary>Preserve solid BRep/extrusion geometry where decoded instead of tessellating (must align with model view).</summary>
    public bool? UseSolidBrepWhenAvailable { get; init; }
}

/// <summary>Spatial structure, room/space export, and space boundary settings.</summary>
public sealed record IfcSpatialAndRoomConfiguration
{
    /// <summary>Whether decoded Revit rooms are exported as <c>IfcSpace</c> (and HVAC zones) (default <c>true</c>).</summary>
    public bool? ExportRoomsAsSpaces { get; init; }

    /// <summary>Export 1st/2nd level <c>IfcRelSpaceBoundary</c> relations (not implemented; must remain null/false).</summary>
    public bool? ExportRoomBoundaries { get; init; }
}

/// <summary>Quantity takeoff and opening measurement settings.</summary>
public sealed record IfcQuantityConfiguration
{
    /// <summary>Export base quantities (<c>BaseQuantities</c> in IFC2x3; <c>Qto_*BaseQuantities</c> in IFC4) (default <c>true</c>).</summary>
    public bool? ExportBaseQuantities { get; init; }

    /// <summary>Axes frame for door/window opening <c>Width</c> and <c>Height</c> quantities.</summary>
    public IfcOpeningQuantityFrame? OpeningQuantityFrame { get; init; }
}

/// <summary>Property set generation and grouping settings.</summary>
public sealed record IfcPropertySetConfiguration
{
    /// <summary>Export standard <c>Pset_*Common</c> property sets (default <c>true</c>).</summary>
    public bool? ExportIfcCommonPropertySets { get; init; }

    /// <summary>Export Revit instance and type parameters as property sets (default <c>false</c>).</summary>
    public bool? ExportRevitPropertySets { get; init; }

    /// <summary>Group Revit parameters into one property set per Revit parameter group (implies <see cref="ExportRevitPropertySets"/>).</summary>
    public bool? GroupRevitParametersByGroup { get; init; }

    /// <summary>Export Revit schedules as custom property sets (not implemented; must remain null/false).</summary>
    public bool? ExportSchedulesAsPropertySets { get; init; }
}

/// <summary>Category and parameter mapping settings.</summary>
public sealed record IfcMappingConfiguration
{
    /// <summary>Honor Revit per-element/per-type <c>IfcExportAs</c> and <c>IfcExportType</c> overrides (default <c>true</c>).</summary>
    public bool? UseRevitIfcExportAsParameters { get; init; }

    /// <summary>External family/category mapping table file path (not implemented; must remain null/empty).</summary>
    public string? FamilyMappingTablePath { get; init; }

    /// <summary>External parameter mapping table file path (not implemented; must remain null/empty).</summary>
    public string? ParameterMappingTablePath { get; init; }
}

/// <summary>
/// Unified, serializable IFC export configuration shared between the library API and CLI.
/// Supports named presets, explicit overrides, validation, and capability introspection.
/// </summary>
public sealed record IfcExportConfiguration
{
    /// <summary>Default IFC export configuration (IFC2X3, Current profile, default model view and scope).</summary>
    public static IfcExportConfiguration Default { get; } = new();

    /// <summary>Optional display or setup name (for example a saved setup name).</summary>
    public string? Name { get; init; }

    /// <summary>Optional named preset used as the base layer before explicit fields and runtime overrides.</summary>
    public IfcExportPresetKind? Preset { get; init; }

    /// <summary>Schema release, MVD exchange view, and compatibility profile settings.</summary>
    public IfcSchemaConfiguration Schema { get; init; } = new();

    /// <summary>Element inclusion and view scope settings.</summary>
    public IfcElementScopeConfiguration Scope { get; init; } = new();

    /// <summary>Element and room phase filter settings.</summary>
    public IfcPhaseConfiguration Phases { get; init; } = new();

    /// <summary>Coordinate origin, length unit family, and timestamp settings.</summary>
    public IfcCoordinateAndUnitConfiguration UnitsAndCoordinates { get; init; } = new();

    /// <summary>Geometry representation and tessellation settings.</summary>
    public IfcGeometryConfiguration Geometry { get; init; } = new();

    /// <summary>Spatial structure and room/space export settings.</summary>
    public IfcSpatialAndRoomConfiguration Spatial { get; init; } = new();

    /// <summary>Base quantity export settings.</summary>
    public IfcQuantityConfiguration Quantities { get; init; } = new();

    /// <summary>Property set and Revit parameter export settings.</summary>
    public IfcPropertySetConfiguration PropertySets { get; init; } = new();

    /// <summary>Category and parameter mapping settings.</summary>
    public IfcMappingConfiguration Mappings { get; init; } = new();

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new FlexibleEnumJsonConverter<IfcSchema>(IfcExportOptionParser.TryParseSchema, IfcExportOptionParser.FormatSchema));
        options.Converters.Add(new FlexibleEnumJsonConverter<IfcModelView>(IfcExportOptionParser.TryParseModelView, IfcExportOptionParser.FormatModelView));
        options.Converters.Add(new FlexibleEnumJsonConverter<IfcExportPresetKind>(IfcExportOptionParser.TryParsePreset, IfcExportOptionParser.FormatPreset));
        options.Converters.Add(new FlexibleEnumJsonConverter<IfcUnitFamily>(IfcExportOptionParser.TryParseUnitFamily, IfcExportOptionParser.FormatUnitFamily));
        options.Converters.Add(new FlexibleEnumJsonConverter<IfcProfile>(IfcExportOptionParser.TryParseProfile, IfcExportOptionParser.FormatProfile));
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    /// <summary>Creates a configuration initialized from a named preset.</summary>
    public static IfcExportConfiguration FromPreset(IfcExportPresetKind preset) => preset switch
    {
        IfcExportPresetKind.Default or IfcExportPresetKind.Ifc2x3CoordinationView20 => new IfcExportConfiguration
        {
            Name = IfcExportOptionParser.FormatPreset(preset),
            Preset = preset,
            Schema = new IfcSchemaConfiguration
            {
                Schema = IfcSchema.Ifc2x3,
                ModelView = IfcModelView.CoordinationView20,
                Profile = IfcProfile.Current,
            },
            Quantities = new IfcQuantityConfiguration
            {
                ExportBaseQuantities = true,
                OpeningQuantityFrame = IfcOpeningQuantityFrame.HostAxes,
            },
            Spatial = new IfcSpatialAndRoomConfiguration { ExportRoomsAsSpaces = true },
            Geometry = new IfcGeometryConfiguration { DetailLevel = IfcLevelOfDetail.High, PreferTessellation = false },
            UnitsAndCoordinates = new IfcCoordinateAndUnitConfiguration { CoordinateBase = IfcCoordinateBase.SharedCoordinates },
            PropertySets = new IfcPropertySetConfiguration { ExportIfcCommonPropertySets = true, ExportRevitPropertySets = false, GroupRevitParametersByGroup = false },
            Scope = new IfcElementScopeConfiguration { RespectExportToIfcParameter = true },
            Mappings = new IfcMappingConfiguration { UseRevitIfcExportAsParameters = true },
        },
        IfcExportPresetKind.Ifc2x3CoordinationViewLegacy => new IfcExportConfiguration
        {
            Name = IfcExportOptionParser.FormatPreset(preset),
            Preset = preset,
            Schema = new IfcSchemaConfiguration
            {
                Schema = IfcSchema.Ifc2x3,
                ModelView = IfcModelView.CoordinationView,
                Profile = IfcProfile.Legacy,
            },
            Quantities = new IfcQuantityConfiguration
            {
                ExportBaseQuantities = true,
                OpeningQuantityFrame = IfcOpeningQuantityFrame.HostAxes,
            },
            Spatial = new IfcSpatialAndRoomConfiguration { ExportRoomsAsSpaces = true },
            Geometry = new IfcGeometryConfiguration { DetailLevel = IfcLevelOfDetail.High, PreferTessellation = false },
            UnitsAndCoordinates = new IfcCoordinateAndUnitConfiguration { CoordinateBase = IfcCoordinateBase.SharedCoordinates },
            PropertySets = new IfcPropertySetConfiguration { ExportIfcCommonPropertySets = true, ExportRevitPropertySets = false, GroupRevitParametersByGroup = false },
            Scope = new IfcElementScopeConfiguration { RespectExportToIfcParameter = true },
            Mappings = new IfcMappingConfiguration { UseRevitIfcExportAsParameters = true },
        },
        IfcExportPresetKind.Ifc4ReferenceView12 => new IfcExportConfiguration
        {
            Name = IfcExportOptionParser.FormatPreset(preset),
            Preset = preset,
            Schema = new IfcSchemaConfiguration
            {
                Schema = IfcSchema.Ifc4,
                ModelView = IfcModelView.ReferenceView12,
                Profile = IfcProfile.Current,
            },
            Quantities = new IfcQuantityConfiguration
            {
                ExportBaseQuantities = true,
                OpeningQuantityFrame = IfcOpeningQuantityFrame.HostAxes,
            },
            Spatial = new IfcSpatialAndRoomConfiguration { ExportRoomsAsSpaces = true },
            Geometry = new IfcGeometryConfiguration { DetailLevel = IfcLevelOfDetail.High, PreferTessellation = true },
            UnitsAndCoordinates = new IfcCoordinateAndUnitConfiguration { CoordinateBase = IfcCoordinateBase.SharedCoordinates },
            PropertySets = new IfcPropertySetConfiguration { ExportIfcCommonPropertySets = true, ExportRevitPropertySets = false, GroupRevitParametersByGroup = false },
            Scope = new IfcElementScopeConfiguration { RespectExportToIfcParameter = true },
            Mappings = new IfcMappingConfiguration { UseRevitIfcExportAsParameters = true },
        },
        IfcExportPresetKind.Ifc4DesignTransferView10 => new IfcExportConfiguration
        {
            Name = IfcExportOptionParser.FormatPreset(preset),
            Preset = preset,
            Schema = new IfcSchemaConfiguration
            {
                Schema = IfcSchema.Ifc4,
                ModelView = IfcModelView.DesignTransferView10,
                Profile = IfcProfile.Current,
            },
            Quantities = new IfcQuantityConfiguration
            {
                ExportBaseQuantities = true,
                OpeningQuantityFrame = IfcOpeningQuantityFrame.HostAxes,
            },
            Spatial = new IfcSpatialAndRoomConfiguration { ExportRoomsAsSpaces = true },
            Geometry = new IfcGeometryConfiguration { DetailLevel = IfcLevelOfDetail.High, PreferTessellation = false },
            UnitsAndCoordinates = new IfcCoordinateAndUnitConfiguration { CoordinateBase = IfcCoordinateBase.SharedCoordinates },
            PropertySets = new IfcPropertySetConfiguration { ExportIfcCommonPropertySets = true, ExportRevitPropertySets = false, GroupRevitParametersByGroup = false },
            Scope = new IfcElementScopeConfiguration { RespectExportToIfcParameter = true },
            Mappings = new IfcMappingConfiguration { UseRevitIfcExportAsParameters = true },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, $"Unsupported IFC export preset '{preset}'."),
    };

    /// <summary>Serializes this configuration to indented UTF-8 JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>Deserializes a configuration from JSON, rejecting unknown fields and invalid enum values.</summary>
    public static IfcExportConfiguration FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var configuration = JsonSerializer.Deserialize<IfcExportConfiguration>(json, SerializerOptions)
            ?? throw new JsonException("Configuration JSON must represent an object.");
        string? nullSection = configuration switch
        {
            { Schema: null } => "schema",
            { Scope: null } => "scope",
            { Phases: null } => "phases",
            { UnitsAndCoordinates: null } => "unitsAndCoordinates",
            { Geometry: null } => "geometry",
            { Spatial: null } => "spatial",
            { Quantities: null } => "quantities",
            { PropertySets: null } => "propertySets",
            { Mappings: null } => "mappings",
            _ => null,
        };
        if (nullSection != null)
            throw new JsonException($"IFC configuration section '{nullSection}' must be an object.");
        return configuration;
    }

    /// <summary>
    /// Layers <paramref name="overrides"/> over this configuration so every non-null field in
    /// <paramref name="overrides"/> takes precedence over the base setting.
    /// </summary>
    public IfcExportConfiguration Merge(IfcExportConfiguration? overrides)
    {
        if (overrides is null) return this;

        IfcModelView? baseModelView = Schema.ModelView;
        bool? basePreferTessellation = Geometry.PreferTessellation;
        if (Preset is { } basePresetKind && Enum.IsDefined(basePresetKind))
        {
            var presetDefaults = FromPreset(basePresetKind);
            bool schemaChanged = overrides.Schema.Schema is { } newSchema && newSchema != Schema.Schema;
            bool viewChanged = overrides.Schema.ModelView is { } newView && newView != Schema.ModelView;
            if (schemaChanged && overrides.Schema.ModelView is null && baseModelView == presetDefaults.Schema.ModelView)
                baseModelView = null;
            if ((schemaChanged || viewChanged) && overrides.Geometry.PreferTessellation is null && basePreferTessellation == presetDefaults.Geometry.PreferTessellation)
                basePreferTessellation = null;
        }

        return new IfcExportConfiguration
        {
            Name = overrides.Name ?? Name,
            Preset = overrides.Preset ?? Preset,
            Schema = new IfcSchemaConfiguration
            {
                Schema = overrides.Schema.Schema ?? Schema.Schema,
                ModelView = overrides.Schema.ModelView ?? baseModelView,
                Profile = overrides.Schema.Profile ?? Schema.Profile,
            },
            Scope = new IfcElementScopeConfiguration
            {
                ExportElementsInCurrentViewOnly = overrides.Scope.ExportElementsInCurrentViewOnly ?? Scope.ExportElementsInCurrentViewOnly,
                ExportAnnotations2D = overrides.Scope.ExportAnnotations2D ?? Scope.ExportAnnotations2D,
                ExportLinkedFiles = overrides.Scope.ExportLinkedFiles ?? Scope.ExportLinkedFiles,
                RespectExportToIfcParameter = overrides.Scope.RespectExportToIfcParameter ?? Scope.RespectExportToIfcParameter,
                SplitWallsAndColumnsByLevel = overrides.Scope.SplitWallsAndColumnsByLevel ?? Scope.SplitWallsAndColumnsByLevel,
            },
            Phases = new IfcPhaseConfiguration
            {
                Scope = overrides.Phases.Scope ?? Phases.Scope,
                PhaseId = overrides.Phases.Scope is IfcPhaseScope.LastPhase or IfcPhaseScope.AllPhases
                    ? overrides.Phases.PhaseId
                    : overrides.Phases.PhaseId ?? Phases.PhaseId,
                RoomScope = overrides.Phases.RoomScope ?? Phases.RoomScope,
                RoomPhaseId = overrides.Phases.RoomScope is IfcRoomPhaseScope.LastPhase or IfcRoomPhaseScope.AllPhases
                    ? overrides.Phases.RoomPhaseId
                    : overrides.Phases.RoomPhaseId ?? Phases.RoomPhaseId,
            },
            UnitsAndCoordinates = new IfcCoordinateAndUnitConfiguration
            {
                LengthUnit = overrides.UnitsAndCoordinates.LengthUnit ?? UnitsAndCoordinates.LengthUnit,
                CoordinateBase = overrides.UnitsAndCoordinates.CoordinateBase ?? UnitsAndCoordinates.CoordinateBase,
                HeaderTimestamp = overrides.UnitsAndCoordinates.HeaderTimestamp ?? UnitsAndCoordinates.HeaderTimestamp,
            },
            Geometry = new IfcGeometryConfiguration
            {
                DetailLevel = overrides.Geometry.DetailLevel ?? Geometry.DetailLevel,
                PreferTessellation = overrides.Geometry.PreferTessellation ?? basePreferTessellation,
                UseSolidBrepWhenAvailable = overrides.Geometry.UseSolidBrepWhenAvailable ?? Geometry.UseSolidBrepWhenAvailable,
            },
            Spatial = new IfcSpatialAndRoomConfiguration
            {
                ExportRoomsAsSpaces = overrides.Spatial.ExportRoomsAsSpaces ?? Spatial.ExportRoomsAsSpaces,
                ExportRoomBoundaries = overrides.Spatial.ExportRoomBoundaries ?? Spatial.ExportRoomBoundaries,
            },
            Quantities = new IfcQuantityConfiguration
            {
                ExportBaseQuantities = overrides.Quantities.ExportBaseQuantities ?? Quantities.ExportBaseQuantities,
                OpeningQuantityFrame = overrides.Quantities.OpeningQuantityFrame ?? Quantities.OpeningQuantityFrame,
            },
            PropertySets = new IfcPropertySetConfiguration
            {
                ExportIfcCommonPropertySets = overrides.PropertySets.ExportIfcCommonPropertySets ?? PropertySets.ExportIfcCommonPropertySets,
                ExportRevitPropertySets = overrides.PropertySets.ExportRevitPropertySets ?? PropertySets.ExportRevitPropertySets,
                GroupRevitParametersByGroup = overrides.PropertySets.GroupRevitParametersByGroup ?? PropertySets.GroupRevitParametersByGroup,
                ExportSchedulesAsPropertySets = overrides.PropertySets.ExportSchedulesAsPropertySets ?? PropertySets.ExportSchedulesAsPropertySets,
            },
            Mappings = new IfcMappingConfiguration
            {
                UseRevitIfcExportAsParameters = overrides.Mappings.UseRevitIfcExportAsParameters ?? Mappings.UseRevitIfcExportAsParameters,
                FamilyMappingTablePath = overrides.Mappings.FamilyMappingTablePath ?? Mappings.FamilyMappingTablePath,
                ParameterMappingTablePath = overrides.Mappings.ParameterMappingTablePath ?? Mappings.ParameterMappingTablePath,
            },
        };
    }

    /// <summary>Validates this configuration (after applying its <see cref="Preset"/> base layer) and returns all findings.</summary>
    public IReadOnlyList<IfcConfigurationDiagnostic> Validate() => ResolveCore(throwOnError: false).Diagnostics;

    /// <summary>Resolves defaults, preset inheritance, and explicit overrides, throwing <see cref="InvalidOperationException"/> on invalid or unsupported combinations.</summary>
    public ResolvedIfcExportConfiguration Resolve() => ResolveCore(throwOnError: true);

    private ResolvedIfcExportConfiguration ResolveCore(bool throwOnError)
    {
        var diagnostics = new List<IfcConfigurationDiagnostic>();

        IfcExportConfiguration effective;
        if (Preset is { } presetKind)
        {
            if (!Enum.IsDefined(presetKind))
            {
                diagnostics.Add(new IfcConfigurationDiagnostic(
                    "invalid-preset", IfcConfigurationDiagnosticSeverity.Error, "preset",
                    $"Unsupported IFC export preset '{presetKind}'."));
                effective = this;
            }
            else
            {
                var basePreset = FromPreset(presetKind);
                // When an explicit Schema override changes away from the preset schema, let ModelView and PreferTessellation re-resolve cleanly unless explicitly set.
                if (Schema.Schema is { } explicitSchema && basePreset.Schema.Schema != explicitSchema)
                {
                    if (Schema.ModelView is null)
                        basePreset = basePreset with { Schema = basePreset.Schema with { ModelView = IfcModelView.Default } };
                    if (Geometry.PreferTessellation is null)
                        basePreset = basePreset with { Geometry = basePreset.Geometry with { PreferTessellation = null } };
                }
                if (Schema.ModelView is IfcModelView.ReferenceView12 && Geometry.PreferTessellation is null && basePreset.Geometry.PreferTessellation is false)
                    basePreset = basePreset with { Geometry = basePreset.Geometry with { PreferTessellation = null } };
                if (Schema.ModelView is IfcModelView.DesignTransferView10 && Geometry.PreferTessellation is null && basePreset.Geometry.PreferTessellation is true)
                    basePreset = basePreset with { Geometry = basePreset.Geometry with { PreferTessellation = null } };
                effective = basePreset.Merge(this);
            }
        }
        else
        {
            effective = this;
        }

        IfcSchema schema = effective.Schema.Schema ?? IfcSchema.Ifc2x3;
        if (!Enum.IsDefined(schema))
            diagnostics.Add(new IfcConfigurationDiagnostic(
                "invalid-schema", IfcConfigurationDiagnosticSeverity.Error, "schema.schema",
                $"Unsupported IFC schema '{schema}'. Supported values: IFC2X3, IFC4."));

        IfcProfile profile = effective.Schema.Profile ?? IfcProfile.Current;
        if (!Enum.IsDefined(profile))
            diagnostics.Add(new IfcConfigurationDiagnostic(
                "invalid-profile", IfcConfigurationDiagnosticSeverity.Error, "schema.profile",
                $"Unsupported IFC export profile '{profile}'. Supported values: Current, Legacy."));

        IfcModelView requestedView = effective.Schema.ModelView ?? IfcModelView.Default;
        if (!Enum.IsDefined(requestedView))
            diagnostics.Add(new IfcConfigurationDiagnostic(
                "invalid-model-view", IfcConfigurationDiagnosticSeverity.Error, "schema.modelView",
                $"Unsupported IFC model view '{requestedView}'."));

        IfcModelView effectiveView = requestedView switch
        {
            IfcModelView.Default when schema == IfcSchema.Ifc2x3 =>
                profile == IfcProfile.Current ? IfcModelView.CoordinationView20 : IfcModelView.CoordinationView,
            IfcModelView.Default when schema == IfcSchema.Ifc4 => IfcModelView.ReferenceView12,
            _ => requestedView,
        };

        if (Enum.IsDefined(schema) && Enum.IsDefined(requestedView))
        {
            bool compatible = (schema, effectiveView) switch
            {
                (IfcSchema.Ifc2x3, IfcModelView.CoordinationView or IfcModelView.CoordinationView20) => true,
                (IfcSchema.Ifc4, IfcModelView.ReferenceView12 or IfcModelView.DesignTransferView10) => true,
                _ => false,
            };
            if (!compatible)
                diagnostics.Add(new IfcConfigurationDiagnostic(
                    "incompatible-schema-model-view", IfcConfigurationDiagnosticSeverity.Error, "schema.modelView",
                    $"Model view '{IfcExportOptionParser.FormatModelView(effectiveView)}' is not supported with schema '{IfcExportOptionParser.FormatSchema(schema)}'."));
        }

        bool preferTessellation = effective.Geometry.PreferTessellation ?? (effectiveView == IfcModelView.ReferenceView12);
        if (schema == IfcSchema.Ifc2x3 && effective.Geometry.PreferTessellation == true)
            diagnostics.Add(new IfcConfigurationDiagnostic(
                "conflicting-geometry-tessellation", IfcConfigurationDiagnosticSeverity.Error, "geometry.preferTessellation",
                "IFC4 tessellation entities (IfcPolygonalFaceSet / IfcTriangulatedFaceSet) cannot be enabled under schema IFC2X3."));
        else if (effectiveView == IfcModelView.ReferenceView12 && effective.Geometry.PreferTessellation == false)
            diagnostics.Add(new IfcConfigurationDiagnostic(
                "conflicting-geometry-tessellation", IfcConfigurationDiagnosticSeverity.Error, "geometry.preferTessellation",
                "IFC4 ReferenceView_V1.2 requires tessellation for faceted solids and meshes; set preferTessellation to true or select DesignTransferView_V1.0."));

        if (effective.Geometry.UseSolidBrepWhenAvailable == true && effectiveView == IfcModelView.ReferenceView12)
            diagnostics.Add(new IfcConfigurationDiagnostic(
                "conflicting-geometry-mode", IfcConfigurationDiagnosticSeverity.Error, "geometry.useSolidBrepWhenAvailable",
                "UseSolidBrepWhenAvailable conflicts with IFC4 ReferenceView_V1.2; select DesignTransferView_V1.0 to emit IfcFacetedBrep solids."));
        else if (effective.Geometry.UseSolidBrepWhenAvailable == false && effectiveView != IfcModelView.ReferenceView12)
            diagnostics.Add(new IfcConfigurationDiagnostic(
                "unsupported-option", IfcConfigurationDiagnosticSeverity.Error, "geometry.useSolidBrepWhenAvailable",
                "Disabling solid BRep fallback is only supported under IFC4 ReferenceView_V1.2."));

        if (effective.Geometry.DetailLevel is { } detail && detail != IfcLevelOfDetail.High)
            diagnostics.Add(new IfcConfigurationDiagnostic(
                "unsupported-detail-level", IfcConfigurationDiagnosticSeverity.Error, "geometry.detailLevel",
                $"Geometry detail level '{detail}' is not implemented; only High (decoded geometry) is supported."));

        if (effective.UnitsAndCoordinates.LengthUnit is { } unit && !Enum.IsDefined(unit))
            diagnostics.Add(new IfcConfigurationDiagnostic(
                "invalid-length-unit", IfcConfigurationDiagnosticSeverity.Error, "unitsAndCoordinates.lengthUnit",
                $"Unsupported length unit '{unit}'."));

        if (effective.UnitsAndCoordinates.CoordinateBase is { } coordBase && coordBase != IfcCoordinateBase.SharedCoordinates)
            diagnostics.Add(new IfcConfigurationDiagnostic(
                "unsupported-coordinate-base", IfcConfigurationDiagnosticSeverity.Error, "unitsAndCoordinates.coordinateBase",
                $"Coordinate base '{coordBase}' is not separately configurable; only SharedCoordinates is supported."));

        ValidatePhase(effective.Phases, diagnostics);
        ValidateUnsupportedFlags(effective, diagnostics);

        bool revitGroups = effective.PropertySets.GroupRevitParametersByGroup ?? false;
        bool revitPsets = (effective.PropertySets.ExportRevitPropertySets ?? false) || revitGroups;

        var resolved = new ResolvedIfcExportConfiguration
        {
            Name = effective.Name,
            Preset = effective.Preset,
            Schema = schema,
            RequestedModelView = requestedView,
            EffectiveModelView = effectiveView,
            Profile = profile,
            PhaseScope = effective.Phases.Scope ?? IfcPhaseScope.Default,
            ExportPhaseId = effective.Phases.PhaseId,
            RoomPhaseScope = effective.Phases.RoomScope ?? IfcRoomPhaseScope.FollowPhaseScope,
            RoomExportPhaseId = effective.Phases.RoomPhaseId,
            LengthUnit = effective.UnitsAndCoordinates.LengthUnit,
            HeaderTimestamp = effective.UnitsAndCoordinates.HeaderTimestamp,
            CoordinateBase = effective.UnitsAndCoordinates.CoordinateBase ?? IfcCoordinateBase.SharedCoordinates,
            PreferTessellation = preferTessellation,
            UseWallStandardCase = schema == IfcSchema.Ifc2x3 || effectiveView == IfcModelView.DesignTransferView10,
            ExportRoomsAsSpaces = effective.Spatial.ExportRoomsAsSpaces ?? true,
            ExportBaseQuantities = effective.Quantities.ExportBaseQuantities ?? true,
            OpeningQuantitiesInHostAxes = (effective.Quantities.OpeningQuantityFrame ?? IfcOpeningQuantityFrame.HostAxes) == IfcOpeningQuantityFrame.HostAxes,
            ExportIfcCommonPropertySets = effective.PropertySets.ExportIfcCommonPropertySets ?? true,
            ExportRevitPropertySets = revitPsets,
            RevitParameterGroups = revitGroups,
            RespectExportToIfcParameter = effective.Scope.RespectExportToIfcParameter ?? true,
            UseRevitIfcExportAsParameters = effective.Mappings.UseRevitIfcExportAsParameters ?? true,
            Diagnostics = diagnostics,
        };

        if (throwOnError)
        {
            var errors = diagnostics.Where(d => d.Severity == IfcConfigurationDiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                string summary = string.Join(" ", errors.Select(e => $"[{e.Code} at {e.SettingPath}] {e.Message}"));
                throw new InvalidOperationException($"Invalid IFC export configuration: {summary}");
            }
        }
        return resolved;
    }

    private static void ValidatePhase(IfcPhaseConfiguration phases, List<IfcConfigurationDiagnostic> diagnostics)
    {
        var scope = phases.Scope ?? IfcPhaseScope.Default;
        if (!Enum.IsDefined(scope))
        {
            diagnostics.Add(new IfcConfigurationDiagnostic("invalid-phase-scope", IfcConfigurationDiagnosticSeverity.Error,
                "phases.scope", $"Unsupported phase scope '{scope}'."));
        }
        else if (scope == IfcPhaseScope.SpecificPhase)
        {
            if (phases.PhaseId is not >= 0)
                diagnostics.Add(new IfcConfigurationDiagnostic("invalid-phase-id", IfcConfigurationDiagnosticSeverity.Error,
                    "phases.phaseId", "Phases.Scope = SpecificPhase requires a non-negative PhaseId."));
        }
        else if (phases.PhaseId is { } unexpectedPhaseId)
        {
            if (scope is IfcPhaseScope.AllPhases or IfcPhaseScope.LastPhase)
                diagnostics.Add(new IfcConfigurationDiagnostic("conflicting-phase-options", IfcConfigurationDiagnosticSeverity.Error,
                    "phases.phaseId", $"PhaseId ({unexpectedPhaseId}) cannot be combined with PhaseScope '{scope}'."));
            else if (unexpectedPhaseId < 0)
                diagnostics.Add(new IfcConfigurationDiagnostic("invalid-phase-id", IfcConfigurationDiagnosticSeverity.Error,
                    "phases.phaseId", "PhaseId must be non-negative."));
        }

        var roomScope = phases.RoomScope ?? IfcRoomPhaseScope.FollowPhaseScope;
        if (!Enum.IsDefined(roomScope))
        {
            diagnostics.Add(new IfcConfigurationDiagnostic("invalid-room-phase-scope", IfcConfigurationDiagnosticSeverity.Error,
                "phases.roomScope", $"Unsupported room phase scope '{roomScope}'."));
        }
        else if (roomScope == IfcRoomPhaseScope.SpecificPhase)
        {
            if (phases.RoomPhaseId is not >= 0)
                diagnostics.Add(new IfcConfigurationDiagnostic("invalid-room-phase-id", IfcConfigurationDiagnosticSeverity.Error,
                    "phases.roomPhaseId", "Phases.RoomScope = SpecificPhase requires a non-negative RoomPhaseId."));
        }
        else if (phases.RoomPhaseId is { } unexpectedRoomPhaseId)
        {
            if (roomScope is IfcRoomPhaseScope.AllPhases or IfcRoomPhaseScope.LastPhase)
                diagnostics.Add(new IfcConfigurationDiagnostic("conflicting-room-phase-options", IfcConfigurationDiagnosticSeverity.Error,
                    "phases.roomPhaseId", $"RoomPhaseId ({unexpectedRoomPhaseId}) cannot be combined with RoomPhaseScope '{roomScope}'."));
            else if (unexpectedRoomPhaseId < 0)
                diagnostics.Add(new IfcConfigurationDiagnostic("invalid-room-phase-id", IfcConfigurationDiagnosticSeverity.Error,
                    "phases.roomPhaseId", "RoomPhaseId must be non-negative."));
        }
    }

    private static void ValidateUnsupportedFlags(IfcExportConfiguration c, List<IfcConfigurationDiagnostic> diagnostics)
    {
        void Unsupported(bool condition, string path, string message)
        {
            if (condition)
                diagnostics.Add(new IfcConfigurationDiagnostic("unsupported-option", IfcConfigurationDiagnosticSeverity.Error, path, message));
        }

        Unsupported(c.Scope.ExportElementsInCurrentViewOnly == true, "scope.exportElementsInCurrentViewOnly",
            "View-specific element filtering is not implemented.");
        Unsupported(c.Scope.ExportAnnotations2D == true, "scope.exportAnnotations2D",
            "General 2D plan/annotation export beyond grid footprints is not implemented.");
        Unsupported(c.Scope.ExportLinkedFiles == true, "scope.exportLinkedFiles",
            "Exporting linked RVT files is not implemented.");
        Unsupported(c.Scope.SplitWallsAndColumnsByLevel == true, "scope.splitWallsAndColumnsByLevel",
            "Splitting walls and columns by level is not implemented.");
        Unsupported(c.Scope.RespectExportToIfcParameter == false, "scope.respectExportToIfcParameter",
            "Ignoring the Revit 'Export to IFC' parameter is not implemented.");
        Unsupported(c.Spatial.ExportRoomBoundaries == true, "spatial.exportRoomBoundaries",
            "Exporting IfcRelSpaceBoundary geometry is not implemented.");
        Unsupported(c.PropertySets.ExportIfcCommonPropertySets == false, "propertySets.exportIfcCommonPropertySets",
            "Suppressing standard Pset_*Common property sets is not implemented.");
        Unsupported(c.PropertySets.ExportSchedulesAsPropertySets == true, "propertySets.exportSchedulesAsPropertySets",
            "Exporting Revit schedules as IFC property sets is not implemented.");
        Unsupported(c.Mappings.UseRevitIfcExportAsParameters == false, "mappings.useRevitIfcExportAsParameters",
            "Disabling per-element IfcExportAs/IfcExportType parameter overrides is not implemented.");
        Unsupported(!string.IsNullOrWhiteSpace(c.Mappings.FamilyMappingTablePath), "mappings.familyMappingTablePath",
            "External family mapping table files are not implemented.");
        Unsupported(!string.IsNullOrWhiteSpace(c.Mappings.ParameterMappingTablePath), "mappings.parameterMappingTablePath",
            "External parameter mapping table files are not implemented.");
    }
}

/// <summary>Concrete resolved settings produced by <see cref="IfcExportConfiguration.Resolve"/>.</summary>
public sealed record ResolvedIfcExportConfiguration
{
    /// <summary>Configuration display name.</summary>
    public string? Name { get; init; }
    /// <summary>Preset used as the base layer, if any.</summary>
    public IfcExportPresetKind? Preset { get; init; }
    /// <summary>Resolved IFC schema.</summary>
    public IfcSchema Schema { get; init; }
    /// <summary>Requested model view before resolving <see cref="IfcModelView.Default"/>.</summary>
    public IfcModelView RequestedModelView { get; init; }
    /// <summary>Concrete resolved model view written to <c>FILE_DESCRIPTION</c>.</summary>
    public IfcModelView EffectiveModelView { get; init; }
    /// <summary>Resolved compatibility profile (<see cref="IfcProfile.Current"/> or <see cref="IfcProfile.Legacy"/>).</summary>
    public IfcProfile Profile { get; init; }
    /// <summary>Resolved element phase scope.</summary>
    public IfcPhaseScope PhaseScope { get; init; }
    /// <summary>Explicit phase id when scoping to a single phase.</summary>
    public int? ExportPhaseId { get; init; }
    /// <summary>Resolved room phase scope.</summary>
    public IfcRoomPhaseScope RoomPhaseScope { get; init; }
    /// <summary>Explicit room phase id when scoping rooms to a single phase.</summary>
    public int? RoomExportPhaseId { get; init; }
    /// <summary>Explicit length unit override, or null to use the profile default.</summary>
    public IfcUnitFamily? LengthUnit { get; init; }
    /// <summary>Fixed UTC timestamp for the STEP header, or null to use export time.</summary>
    public DateTimeOffset? HeaderTimestamp { get; init; }
    /// <summary>Resolved coordinate base.</summary>
    public IfcCoordinateBase CoordinateBase { get; init; }
    /// <summary>True when faceted BRep/surface geometry should be written as IFC4 tessellations.</summary>
    public bool PreferTessellation { get; init; }
    /// <summary>True when standard rectangular vertical-extrusion walls emit <c>IfcWallStandardCase</c>.</summary>
    public bool UseWallStandardCase { get; init; }
    /// <summary>True when decoded rooms are written as <c>IfcSpace</c>.</summary>
    public bool ExportRoomsAsSpaces { get; init; }
    /// <summary>True when base quantity sets are written.</summary>
    public bool ExportBaseQuantities { get; init; }
    /// <summary>True when opening quantities use host wall axes.</summary>
    public bool OpeningQuantitiesInHostAxes { get; init; }
    /// <summary>True when standard <c>Pset_*Common</c> sets are written.</summary>
    public bool ExportIfcCommonPropertySets { get; init; }
    /// <summary>True when Revit parameters are written as property sets.</summary>
    public bool ExportRevitPropertySets { get; init; }
    /// <summary>True when Revit parameter property sets are grouped per Revit parameter group.</summary>
    public bool RevitParameterGroups { get; init; }
    /// <summary>True when the <c>Export to IFC</c> parameter filters elements.</summary>
    public bool RespectExportToIfcParameter { get; init; }
    /// <summary>True when <c>IfcExportAs</c>/<c>IfcExportType</c> parameters override category mappings.</summary>
    public bool UseRevitIfcExportAsParameters { get; init; }
    /// <summary>Validation diagnostics captured while resolving this configuration.</summary>
    public IReadOnlyList<IfcConfigurationDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>Published capability matrix for <see cref="IfcExportConfiguration"/>.</summary>
public static class IfcExportCapabilities
{
    /// <summary>All configuration settings paired with their implementation status and supported values.</summary>
    public static IReadOnlyList<IfcExportCapability> All { get; } =
    [
        new("schema.schema", "Schema & MVD", "IFC Schema", IfcCapabilitySupportStatus.Supported,
            "IFC2X3, IFC4", "IFC2X3", "IFC4 implements ISO 16739-1:2018 (IFC4 ADD2 TC1). Independent of Legacy/Current profile."),
        new("schema.modelView", "Schema & MVD", "Model View Definition", IfcCapabilitySupportStatus.SchemaOrViewScoped,
            "Default, CoordinationView, CoordinationView_V2.0 (IFC2X3); ReferenceView_V1.2, DesignTransferView_V1.0 (IFC4)", "Default",
            "IFC2X3 defaults to CoordinationView_V2.0 (Current) / CoordinationView (Legacy); IFC4 defaults to ReferenceView_V1.2."),
        new("schema.profile", "Schema & MVD", "Compatibility Profile", IfcCapabilitySupportStatus.Supported,
            "Current, Legacy", "Current", "Governs GlobalIds, property-set formatting, and Revit alignment independently of schema/MVD."),
        new("preset", "Presets", "Named Preset", IfcCapabilitySupportStatus.Supported,
            "default, ifc2x3-coordination-view-2.0, ifc2x3-coordination-view-legacy, ifc4-reference-view-1.2, ifc4-design-transfer-view-1.0", "default",
            "Preset values act as defaults overridden by explicit config fields and API/CLI flags."),
        new("scope.exportElementsInCurrentViewOnly", "Element Scope", "Current View Only", IfcCapabilitySupportStatus.Unsupported,
            "false (default)", "false", "View-based element visibility filtering is not implemented; true fails validation."),
        new("scope.exportAnnotations2D", "Element Scope", "2D Annotations", IfcCapabilitySupportStatus.Unsupported,
            "false (default)", "false", "General 2D view annotations beyond grid FootPrint curves are not implemented; true fails validation."),
        new("scope.exportLinkedFiles", "Element Scope", "Linked Files", IfcCapabilitySupportStatus.Unsupported,
            "false (default)", "false", "Linked RVT models are not loaded during export; true fails validation."),
        new("scope.respectExportToIfcParameter", "Element Scope", "Export to IFC Flag", IfcCapabilitySupportStatus.Supported,
            "true", "true", "Honors Revit 'Export to IFC' element/type parameter; setting false is unsupported."),
        new("scope.splitWallsAndColumnsByLevel", "Element Scope", "Split Walls/Columns by Level", IfcCapabilitySupportStatus.Unsupported,
            "false (default)", "false", "Automatic geometric storey splitting is not implemented; true fails validation."),
        new("phases.scope", "Phases", "Element Phase Scope", IfcCapabilitySupportStatus.Supported,
            "Default, LastPhase, AllPhases, SpecificPhase", "Default", "Default uses LastPhase in Current profile and AllPhases in Legacy profile."),
        new("phases.phaseId", "Phases", "Specific Element Phase Id", IfcCapabilitySupportStatus.Supported,
            "Positive Revit Phase ElementId", "null", "Exports project state at the specified phase."),
        new("phases.roomScope", "Phases", "Room Phase Scope", IfcCapabilitySupportStatus.Supported,
            "FollowPhaseScope, LastPhase, AllPhases, SpecificPhase", "FollowPhaseScope", "Controls which phase's rooms export as IfcSpace."),
        new("phases.roomPhaseId", "Phases", "Specific Room Phase Id", IfcCapabilitySupportStatus.Supported,
            "Positive Revit Phase ElementId", "null", "Exports rooms belonging to the specified phase."),
        new("unitsAndCoordinates.lengthUnit", "Units & Coordinates", "Length Unit Family", IfcCapabilitySupportStatus.Supported,
            "Millimetre (mm), Metre (m), Foot (ft)", "null (Project unit in Current; mm in Legacy)", "Scales geometric coordinates, lengths, areas, volumes, and IFC4 point lists."),
        new("unitsAndCoordinates.coordinateBase", "Units & Coordinates", "Coordinate Base", IfcCapabilitySupportStatus.SchemaOrViewScoped,
            "SharedCoordinates", "SharedCoordinates", "Exports active shared transform and geo-reference on IfcSite with internal coordinates; other modes fail validation."),
        new("unitsAndCoordinates.headerTimestamp", "Units & Coordinates", "Deterministic Timestamp", IfcCapabilitySupportStatus.Supported,
            "UTC ISO-8601 / Unix seconds / null", "null", "Fixes FILE_NAME timestamp for reproducible byte-identical exports."),
        new("geometry.detailLevel", "Geometry", "Tessellation Detail Level", IfcCapabilitySupportStatus.SchemaOrViewScoped,
            "High", "High", "Exports decoded geometry at native fidelity; coarser LOD presets are not implemented and fail validation."),
        new("geometry.preferTessellation", "Geometry", "IFC4 Tessellated Shapes", IfcCapabilitySupportStatus.SchemaOrViewScoped,
            "true in ReferenceView_V1.2, false in DesignTransferView_V1.0 and IFC2X3", "MVD-dependent",
            "Writes IfcPolygonalFaceSet / IfcTriangulatedFaceSet ('Tessellation') instead of IfcFacetedBrep / SurfaceModel in IFC4."),
        new("geometry.useSolidBrepWhenAvailable", "Geometry", "Preserve Solid BRep", IfcCapabilitySupportStatus.SchemaOrViewScoped,
            "MVD-dependent", "MVD-dependent", "Allowed in DesignTransferView_V1.0 and IFC2X3; disallowed under ReferenceView_V1.2."),
        new("spatial.exportRoomsAsSpaces", "Spatial & Rooms", "Export Rooms as IfcSpace", IfcCapabilitySupportStatus.Supported,
            "true, false", "true", "When false, omits IfcSpace, IfcZone, and room containment relations."),
        new("spatial.exportRoomBoundaries", "Spatial & Rooms", "Space Boundaries", IfcCapabilitySupportStatus.Unsupported,
            "false (default)", "false", "IfcRelSpaceBoundary generation is not implemented; true fails validation."),
        new("quantities.exportBaseQuantities", "Quantities", "Export Base Quantities", IfcCapabilitySupportStatus.Supported,
            "true, false", "true", "Writes 'BaseQuantities' (IFC2X3) or 'Qto_*BaseQuantities' (IFC4)."),
        new("quantities.openingQuantityFrame", "Quantities", "Opening Quantity Frame", IfcCapabilitySupportStatus.Supported,
            "HostAxes, LegacyOpeningAxes", "HostAxes", "Controls Width/Height axis assignment on opening quantity sets."),
        new("propertySets.exportIfcCommonPropertySets", "Property Sets", "IFC Common Psets", IfcCapabilitySupportStatus.SchemaOrViewScoped,
            "true", "true", "Standard Pset_*Common sets are always exported; disabling fails validation."),
        new("propertySets.exportRevitPropertySets", "Property Sets", "Revit Property Sets", IfcCapabilitySupportStatus.Supported,
            "true, false", "false", "Exports Revit instance and type parameters in IfcPropertySet entities."),
        new("propertySets.groupRevitParametersByGroup", "Property Sets", "Group Revit Parameters", IfcCapabilitySupportStatus.Supported,
            "true, false", "false", "Splits Revit parameters into one IfcPropertySet per parameter group (implies exportRevitPropertySets)."),
        new("propertySets.exportSchedulesAsPropertySets", "Property Sets", "Revit Schedules as Psets", IfcCapabilitySupportStatus.Unsupported,
            "false (default)", "false", "Schedule-to-Pset export is not implemented; true fails validation."),
        new("mappings.useRevitIfcExportAsParameters", "Mappings", "IfcExportAs / IfcExportType", IfcCapabilitySupportStatus.Supported,
            "true", "true", "Reads per-element and per-type IFC class overrides from Revit parameters."),
        new("mappings.familyMappingTablePath", "Mappings", "External Family Mapping Table", IfcCapabilitySupportStatus.Unsupported,
            "null (built-in table)", "null", "External family mapping file loading is not implemented; setting a path fails validation."),
        new("mappings.parameterMappingTablePath", "Mappings", "External Parameter Mapping Table", IfcCapabilitySupportStatus.Unsupported,
            "null (built-in mapping)", "null", "External parameter mapping file loading is not implemented; setting a path fails validation."),
    ];
}

internal static class IfcExportOptionParser
{
    public static bool TryParseSchema(string? text, out IfcSchema schema)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "ifc2x3":
            case "ifc2x3_cv2":
            case "2x3":
                schema = IfcSchema.Ifc2x3;
                return true;
            case "ifc4":
            case "ifc4add2":
            case "ifc4_add2":
            case "ifc4add2tc1":
            case "4":
                schema = IfcSchema.Ifc4;
                return true;
            default:
                schema = default;
                return false;
        }
    }

    public static string FormatSchema(IfcSchema schema) => schema switch
    {
        IfcSchema.Ifc2x3 => "IFC2X3",
        IfcSchema.Ifc4 => "IFC4",
        _ => schema.ToString(),
    };

    public static bool TryParseModelView(string? text, out IfcModelView view)
    {
        switch (text?.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(".", ""))
        {
            case "default":
                view = IfcModelView.Default;
                return true;
            case "coordinationview":
            case "coordination":
            case "cv":
                view = IfcModelView.CoordinationView;
                return true;
            case "coordinationviewv20":
            case "coordinationview20":
            case "coordinationview2":
            case "cv20":
            case "cv2":
                view = IfcModelView.CoordinationView20;
                return true;
            case "referenceview":
            case "referenceviewv12":
            case "referenceview12":
            case "reference":
            case "rv":
            case "rv12":
                view = IfcModelView.ReferenceView12;
                return true;
            case "designtransferview":
            case "designtransferviewv10":
            case "designtransferview10":
            case "designtransfer":
            case "dtv":
            case "dtv10":
                view = IfcModelView.DesignTransferView10;
                return true;
            default:
                view = default;
                return false;
        }
    }

    public static string FormatModelView(IfcModelView view) => view switch
    {
        IfcModelView.Default => "Default",
        IfcModelView.CoordinationView => "CoordinationView",
        IfcModelView.CoordinationView20 => "CoordinationView_V2.0",
        IfcModelView.ReferenceView12 => "ReferenceView_V1.2",
        IfcModelView.DesignTransferView10 => "DesignTransferView_V1.0",
        _ => view.ToString(),
    };

    public static bool TryParsePreset(string? text, out IfcExportPresetKind preset)
    {
        switch (text?.Trim().ToLowerInvariant().Replace("_", "-"))
        {
            case "default":
                preset = IfcExportPresetKind.Default;
                return true;
            case "ifc2x3-coordination-view-2.0":
            case "ifc2x3-coordination-view-20":
            case "ifc2x3-cv2":
            case "ifc2x3":
                preset = IfcExportPresetKind.Ifc2x3CoordinationView20;
                return true;
            case "ifc2x3-coordination-view-legacy":
            case "ifc2x3-legacy":
            case "legacy":
                preset = IfcExportPresetKind.Ifc2x3CoordinationViewLegacy;
                return true;
            case "ifc4-reference-view-1.2":
            case "ifc4-reference-view":
            case "ifc4-rv":
            case "reference-view":
                preset = IfcExportPresetKind.Ifc4ReferenceView12;
                return true;
            case "ifc4-design-transfer-view-1.0":
            case "ifc4-design-transfer-view":
            case "ifc4-dtv":
            case "design-transfer-view":
                preset = IfcExportPresetKind.Ifc4DesignTransferView10;
                return true;
            default:
                preset = default;
                return false;
        }
    }

    public static string FormatPreset(IfcExportPresetKind preset) => preset switch
    {
        IfcExportPresetKind.Default => "default",
        IfcExportPresetKind.Ifc2x3CoordinationView20 => "ifc2x3-coordination-view-2.0",
        IfcExportPresetKind.Ifc2x3CoordinationViewLegacy => "ifc2x3-coordination-view-legacy",
        IfcExportPresetKind.Ifc4ReferenceView12 => "ifc4-reference-view-1.2",
        IfcExportPresetKind.Ifc4DesignTransferView10 => "ifc4-design-transfer-view-1.0",
        _ => preset.ToString(),
    };

    public static bool TryParseProfile(string? text, out IfcProfile profile)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "current":
                profile = IfcProfile.Current;
                return true;
            case "legacy":
                profile = IfcProfile.Legacy;
                return true;
            default:
                profile = default;
                return false;
        }
    }

    public static string FormatProfile(IfcProfile profile) => profile switch
    {
        IfcProfile.Current => "Current",
        IfcProfile.Legacy => "Legacy",
        _ => profile.ToString(),
    };

    public static bool TryParseUnitFamily(string? text, out IfcUnitFamily unit)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "mm":
            case "millimetre":
            case "millimeter":
                unit = IfcUnitFamily.Millimetre;
                return true;
            case "m":
            case "metre":
            case "meter":
                unit = IfcUnitFamily.Metre;
                return true;
            case "ft":
            case "foot":
            case "feet":
                unit = IfcUnitFamily.Foot;
                return true;
            default:
                unit = default;
                return false;
        }
    }

    public static string FormatUnitFamily(IfcUnitFamily unit) => unit switch
    {
        IfcUnitFamily.Millimetre => "mm",
        IfcUnitFamily.Metre => "m",
        IfcUnitFamily.Foot => "ft",
        _ => unit.ToString(),
    };
}

internal sealed class FlexibleEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public delegate bool TryParseDelegate(string? text, out T value);

    private readonly TryParseDelegate _tryParse;
    private readonly Func<T, string> _format;

    public FlexibleEnumJsonConverter(TryParseDelegate tryParse, Func<T, string> format)
    {
        _tryParse = tryParse;
        _format = format;
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected string token for {typeof(T).Name}.");
        string? raw = reader.GetString();
        if (_tryParse(raw, out T parsed))
            return parsed;
        if (Enum.TryParse<T>(raw, ignoreCase: true, out T direct) && Enum.IsDefined(direct))
            return direct;
        throw new JsonException($"Unsupported {typeof(T).Name} value '{raw}'.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(_format(value));
}
