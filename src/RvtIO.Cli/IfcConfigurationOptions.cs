using System.Globalization;
using RvtIO.Ifc;

namespace RvtIO.Cli;

internal sealed record IfcCliOptions(
    IfcSchema? Schema,
    IfcModelView? ModelView,
    IfcExportPresetKind? Preset,
    string? ConfigPath,
    string? SaveConfigPath,
    bool NoSpaces,
    bool ShowCapabilities);

internal static class IfcConfigurationOptions
{
    public const string SchemaOption = "--ifc-schema";
    public const string ModelViewOption = "--ifc-mvd";
    public const string ModelViewAliasOption = "--ifc-model-view";
    public const string PresetOption = "--ifc-preset";
    public const string ConfigOption = "--ifc-config";
    public const string SaveConfigOption = "--ifc-save-config";
    public const string NoSpacesOption = "--ifc-no-spaces";
    public const string CapabilitiesOption = "--ifc-capabilities";

    public static bool TryParse(string[] args, out IfcCliOptions options, out string? error)
    {
        options = new IfcCliOptions(null, null, null, null, null, false, false);
        error = null;

        IfcSchema? schema = null;
        IfcModelView? modelView = null;
        IfcExportPresetKind? preset = null;
        string? configPath = null;
        string? saveConfigPath = null;
        bool noSpaces = false;
        bool showCapabilities = false;

        string? ReadSingleValue(string flag, ref int index, ref bool seen)
        {
            if (seen || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }
            seen = true;
            return args[++index];
        }

        bool seenSchema = false, seenView = false, seenPreset = false, seenConfig = false, seenSaveConfig = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case SchemaOption:
                {
                    string? raw = ReadSingleValue(SchemaOption, ref i, ref seenSchema);
                    if (raw is null || !IfcExportOptionParser.TryParseSchema(raw, out var parsed))
                    {
                        error = $"{SchemaOption} requires a supported schema: ifc2x3, ifc4.";
                        return false;
                    }
                    schema = parsed;
                    break;
                }
                case ModelViewOption:
                case ModelViewAliasOption:
                {
                    string? raw = ReadSingleValue(args[i], ref i, ref seenView);
                    if (raw is null || !IfcExportOptionParser.TryParseModelView(raw, out var parsed))
                    {
                        error = $"{args[i]} requires a supported model view: default, coordination-view, coordination-view-2.0, reference-view-1.2, design-transfer-view-1.0.";
                        return false;
                    }
                    modelView = parsed;
                    break;
                }
                case PresetOption:
                {
                    string? raw = ReadSingleValue(PresetOption, ref i, ref seenPreset);
                    if (raw is null || !IfcExportOptionParser.TryParsePreset(raw, out var parsed))
                    {
                        error = $"{PresetOption} requires a supported preset: default, ifc2x3-coordination-view-2.0, ifc2x3-coordination-view-legacy, ifc4-reference-view-1.2, ifc4-design-transfer-view-1.0.";
                        return false;
                    }
                    preset = parsed;
                    break;
                }
                case ConfigOption:
                {
                    string? raw = ReadSingleValue(ConfigOption, ref i, ref seenConfig);
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        error = $"{ConfigOption} requires one path to a JSON IFC export configuration file.";
                        return false;
                    }
                    configPath = raw;
                    break;
                }
                case SaveConfigOption:
                {
                    string? raw = ReadSingleValue(SaveConfigOption, ref i, ref seenSaveConfig);
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        error = $"{SaveConfigOption} requires one output path for the effective JSON IFC configuration.";
                        return false;
                    }
                    saveConfigPath = raw;
                    break;
                }
                case NoSpacesOption:
                    noSpaces = true;
                    break;
                case CapabilitiesOption:
                    showCapabilities = true;
                    break;
            }
        }

        options = new IfcCliOptions(schema, modelView, preset, configPath, saveConfigPath, noSpaces, showCapabilities);
        return true;
    }

    public static bool TryBuildConfiguration(
        IfcCliOptions cliOptions,
        IfcProfile profile,
        bool profileWasExplicit,
        bool revitParameters,
        bool revitParameterGroups,
        bool baseQuantities,
        bool baseQuantitiesWasExplicit,
        bool openingQuantitiesSwapped,
        bool openingQuantitiesWasExplicit,
        int? exportPhaseId,
        bool exportAllPhases,
        bool exportPhaseWasExplicit,
        int? roomExportPhaseId,
        bool exportRoomsOfAllPhases,
        bool roomPhaseWasExplicit,
        IfcUnitFamily? lengthUnit,
        DateTimeOffset? timestamp,
        out IfcExportConfiguration merged,
        out ResolvedIfcExportConfiguration resolved,
        out string? error)
    {
        merged = new IfcExportConfiguration();
        resolved = null!;
        error = null;

        IfcExportConfiguration baseConfig = new();
        if (cliOptions.Preset is { } presetKind)
            baseConfig = IfcExportConfiguration.FromPreset(presetKind);

        if (cliOptions.ConfigPath is { } cfgPath)
        {
            if (!File.Exists(cfgPath))
            {
                error = $"IFC configuration file not found: {cfgPath}";
                return false;
            }
            try
            {
                var loaded = IfcExportConfiguration.FromJson(File.ReadAllText(cfgPath));
                baseConfig = baseConfig.Merge(loaded);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                error = $"Failed to load IFC configuration '{cfgPath}': {ex.Message}";
                return false;
            }
        }

        IfcPhaseScope? phaseScope = exportPhaseWasExplicit
            ? (exportAllPhases ? IfcPhaseScope.AllPhases : exportPhaseId.HasValue ? IfcPhaseScope.SpecificPhase : IfcPhaseScope.LastPhase)
            : null;
        IfcRoomPhaseScope? roomScope = roomPhaseWasExplicit
            ? (exportRoomsOfAllPhases ? IfcRoomPhaseScope.AllPhases : roomExportPhaseId.HasValue ? IfcRoomPhaseScope.SpecificPhase : IfcRoomPhaseScope.LastPhase)
            : null;

        var cliOverrides = new IfcExportConfiguration
        {
            Schema = new IfcSchemaConfiguration
            {
                Schema = cliOptions.Schema,
                ModelView = cliOptions.ModelView,
                Profile = profileWasExplicit ? profile : null,
            },
            Phases = new IfcPhaseConfiguration
            {
                Scope = phaseScope,
                PhaseId = exportPhaseId,
                RoomScope = roomScope,
                RoomPhaseId = roomExportPhaseId,
            },
            UnitsAndCoordinates = new IfcCoordinateAndUnitConfiguration
            {
                LengthUnit = lengthUnit,
                HeaderTimestamp = timestamp,
            },
            Spatial = new IfcSpatialAndRoomConfiguration
            {
                ExportRoomsAsSpaces = cliOptions.NoSpaces ? false : null,
            },
            Quantities = new IfcQuantityConfiguration
            {
                ExportBaseQuantities = baseQuantitiesWasExplicit ? baseQuantities : null,
                OpeningQuantityFrame = openingQuantitiesWasExplicit
                    ? (openingQuantitiesSwapped ? IfcOpeningQuantityFrame.LegacyOpeningAxes : IfcOpeningQuantityFrame.HostAxes)
                    : null,
            },
            PropertySets = new IfcPropertySetConfiguration
            {
                ExportRevitPropertySets = revitParameters || revitParameterGroups ? true : null,
                GroupRevitParametersByGroup = revitParameterGroups ? true : null,
            },
        };

        merged = baseConfig.Merge(cliOverrides);
        try
        {
            resolved = merged.Resolve();
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
        return true;
    }

    public static void WriteCapabilities(TextWriter output)
    {
        output.WriteLine("IFC export configuration capabilities:");
        foreach (var cap in IfcExportCapabilities.All)
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  [{cap.Status}] {cap.SettingPath} ({cap.Group}): default={cap.DefaultValue}; {cap.Notes}"));
    }
}
