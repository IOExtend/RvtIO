using System.Text.Json;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class IfcExportConfigurationTests
{
    private static RevitFileInfo MinimalDocument()
    {
        const int phaseId = 7;
        double[][] pts = [[0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0], [0, 0, 1], [4, 0, 1], [4, 3, 1], [0, 3, 1]];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new()
        {
            ProjectInfo = new ProjectInfo { Name = "Synthetic Project", Number = "001" },
            SerializedProjectPhaseIds = [phaseId],
            Elements =
            [
                new Element { Id = phaseId, CategoryId = -2000112, Name = "Phase 1", NativeRootClassName = "ProjectPhase" },
                new Element
                {
                    Id = 10, Name = "Level 1", IsNativeLevel = true,
                    SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
                    Parameters =
                    [
                        new Parameter { Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = 0.0 },
                        new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
                    ],
                },
                new Element
                {
                    Id = 11, CategoryId = -2000160, Category = "OST_Rooms", Name = "Room 1", SerializedRoomPhaseId = phaseId,
                    Parameters =
                    [
                        new Parameter { Id = -1006916, StorageType = StorageType.ElementId, Value = 10 },
                        new Parameter { Id = -1006901, StorageType = StorageType.String, Value = "101" },
                    ],
                },
                new Element { Id = 20, TypeId = -1, CategoryId = -2000032, Name = "Floor Type" },
                new Element
                {
                    Id = 21, CategoryId = -2000032, Category = "OST_Floors", Name = "Floor 1", TypeId = 20,
                    References = [phaseId], SerializedCreatedPhaseId = phaseId, SerializedDemolishedPhaseId = -1,
                    Parameters =
                    [
                        new Parameter { Id = -1002050, Value = 20, StorageType = StorageType.ElementId, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" } },
                        new Parameter { Id = -1012100, Value = phaseId, StorageType = StorageType.ElementId },
                    ],
                    BoundingBox = [0, 0, 0, 4, 3, 1],
                    Geometry = new GeometryData
                    {
                        BoundingBox = [0, 0, 0, 4, 3, 1],
                        Faces = faces.Select(idx => new GeoFace { OuterVertices = idx.Select(k => pts[k]).ToList() }).ToList(),
                        BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
                    },
                },
            ],
        };
    }

    [Fact]
    public void Defaults_KeepIfc2x3AndResolveConsistentSettings()
    {
        var resolved = IfcExportConfiguration.Default.Resolve();
        Assert.Equal(IfcSchema.Ifc2x3, resolved.Schema);
        Assert.Equal(IfcModelView.CoordinationView20, resolved.EffectiveModelView);
        Assert.Equal(IfcProfile.Current, resolved.Profile);
        Assert.False(resolved.PreferTessellation);
        Assert.True(resolved.UseWallStandardCase);
        Assert.True(resolved.ExportRoomsAsSpaces);
        Assert.True(resolved.ExportBaseQuantities);
        Assert.True(resolved.OpeningQuantitiesInHostAxes);
        Assert.False(resolved.ExportRevitPropertySets);
    }

    [Theory]
    [InlineData(IfcExportPresetKind.Default, IfcSchema.Ifc2x3, IfcModelView.CoordinationView20, IfcProfile.Current, false)]
    [InlineData(IfcExportPresetKind.Ifc2x3CoordinationView20, IfcSchema.Ifc2x3, IfcModelView.CoordinationView20, IfcProfile.Current, false)]
    [InlineData(IfcExportPresetKind.Ifc2x3CoordinationViewLegacy, IfcSchema.Ifc2x3, IfcModelView.CoordinationView, IfcProfile.Legacy, false)]
    [InlineData(IfcExportPresetKind.Ifc4ReferenceView12, IfcSchema.Ifc4, IfcModelView.ReferenceView12, IfcProfile.Current, true)]
    [InlineData(IfcExportPresetKind.Ifc4DesignTransferView10, IfcSchema.Ifc4, IfcModelView.DesignTransferView10, IfcProfile.Current, false)]
    public void Presets_RoundTripThroughJsonAndResolveExpectedSettings(
        IfcExportPresetKind presetKind,
        IfcSchema expectedSchema,
        IfcModelView expectedView,
        IfcProfile expectedProfile,
        bool expectedTessellation)
    {
        var preset = IfcExportConfiguration.FromPreset(presetKind);
        string json = preset.ToJson();
        var reloaded = IfcExportConfiguration.FromJson(json);
        var resolved = reloaded.Resolve();

        Assert.Equal(presetKind, resolved.Preset);
        Assert.Equal(expectedSchema, resolved.Schema);
        Assert.Equal(expectedView, resolved.EffectiveModelView);
        Assert.Equal(expectedProfile, resolved.Profile);
        Assert.Equal(expectedTessellation, resolved.PreferTessellation);
        Assert.Empty(resolved.Diagnostics);
    }

    [Fact]
    public void Precedence_PresetOverriddenByJsonThenExplicitOverrides_ClearsIncompatiblePresetDefaults()
    {
        var basePreset = IfcExportConfiguration.FromPreset(IfcExportPresetKind.Ifc4ReferenceView12);
        var jsonSetup = IfcExportConfiguration.FromJson("""
        {
          "unitsAndCoordinates": { "lengthUnit": "Metre" },
          "spatial": { "exportRoomsAsSpaces": false }
        }
        """);
        var explicitOverride = new IfcExportConfiguration
        {
            Schema = new IfcSchemaConfiguration
            {
                Schema = IfcSchema.Ifc2x3,
                Profile = IfcProfile.Legacy,
            },
        };

        var resolved = basePreset.Merge(jsonSetup).Merge(explicitOverride).Resolve();
        Assert.Equal(IfcSchema.Ifc2x3, resolved.Schema);
        Assert.Equal(IfcModelView.CoordinationView, resolved.EffectiveModelView);
        Assert.Equal(IfcProfile.Legacy, resolved.Profile);
        Assert.Equal(IfcUnitFamily.Metre, resolved.LengthUnit);
        Assert.False(resolved.ExportRoomsAsSpaces);
        Assert.False(resolved.PreferTessellation);

        var switchedToDtv = basePreset.Merge(new IfcExportConfiguration
        {
            Schema = new IfcSchemaConfiguration { ModelView = IfcModelView.DesignTransferView10 },
        }).Resolve();
        Assert.Equal(IfcSchema.Ifc4, switchedToDtv.Schema);
        Assert.Equal(IfcModelView.DesignTransferView10, switchedToDtv.EffectiveModelView);
        Assert.False(switchedToDtv.PreferTessellation);
    }

    [Fact]
    public void FromJson_RejectsUnknownFields()
    {
        Assert.Throws<JsonException>(() => IfcExportConfiguration.FromJson("""{"unknownField": 123}"""));
    }

    [Theory]
    [InlineData(IfcSchema.Ifc2x3, IfcModelView.ReferenceView12, "incompatible-schema-model-view")]
    [InlineData(IfcSchema.Ifc2x3, IfcModelView.DesignTransferView10, "incompatible-schema-model-view")]
    [InlineData(IfcSchema.Ifc4, IfcModelView.CoordinationView, "incompatible-schema-model-view")]
    [InlineData(IfcSchema.Ifc4, IfcModelView.CoordinationView20, "incompatible-schema-model-view")]
    public void Validation_RejectsIncompatibleSchemaAndModelView(IfcSchema schema, IfcModelView view, string expectedCode)
    {
        var cfg = new IfcExportConfiguration
        {
            Schema = new IfcSchemaConfiguration { Schema = schema, ModelView = view },
        };

        var diags = cfg.Validate();
        Assert.Contains(diags, d => d.Code == expectedCode && d.Severity == IfcConfigurationDiagnosticSeverity.Error);
        Assert.Throws<InvalidOperationException>(() => cfg.Resolve());
        Assert.Throws<InvalidOperationException>(() => new IfcExporter(cfg).Export(MinimalDocument(), new StringWriter()));
    }

    [Fact]
    public void Validation_RejectsConflictingGeometryAndUnsupportedFlags()
    {
        var tessIn2x3 = new IfcExportConfiguration
        {
            Schema = new IfcSchemaConfiguration { Schema = IfcSchema.Ifc2x3 },
            Geometry = new IfcGeometryConfiguration { PreferTessellation = true },
        };
        Assert.Contains(tessIn2x3.Validate(), d => d.Code == "conflicting-geometry-tessellation");

        var noTessInRv = new IfcExportConfiguration
        {
            Schema = new IfcSchemaConfiguration { Schema = IfcSchema.Ifc4, ModelView = IfcModelView.ReferenceView12 },
            Geometry = new IfcGeometryConfiguration { PreferTessellation = false },
        };
        Assert.Contains(noTessInRv.Validate(), d => d.Code == "conflicting-geometry-tessellation");

        var brepInRv = new IfcExportConfiguration
        {
            Schema = new IfcSchemaConfiguration { Schema = IfcSchema.Ifc4, ModelView = IfcModelView.ReferenceView12 },
            Geometry = new IfcGeometryConfiguration { UseSolidBrepWhenAvailable = true },
        };
        Assert.Contains(brepInRv.Validate(), d => d.Code == "conflicting-geometry-mode");

        var unsupported = new IfcExportConfiguration
        {
            Scope = new IfcElementScopeConfiguration
            {
                ExportElementsInCurrentViewOnly = true,
                ExportAnnotations2D = true,
                ExportLinkedFiles = true,
                SplitWallsAndColumnsByLevel = true,
                RespectExportToIfcParameter = false,
            },
            Spatial = new IfcSpatialAndRoomConfiguration { ExportRoomBoundaries = true },
            PropertySets = new IfcPropertySetConfiguration { ExportIfcCommonPropertySets = false, ExportSchedulesAsPropertySets = true },
            Mappings = new IfcMappingConfiguration
            {
                UseRevitIfcExportAsParameters = false,
                FamilyMappingTablePath = "families.txt",
                ParameterMappingTablePath = "params.txt",
            },
            Geometry = new IfcGeometryConfiguration { DetailLevel = IfcLevelOfDetail.Low },
            UnitsAndCoordinates = new IfcCoordinateAndUnitConfiguration { CoordinateBase = IfcCoordinateBase.ProjectBasePoint },
        };
        var diags = unsupported.Validate();
        Assert.True(diags.Count >= 12);
        Assert.Contains(diags, d => d.Code == "unsupported-detail-level");
        Assert.Contains(diags, d => d.Code == "unsupported-coordinate-base");
        Assert.Throws<InvalidOperationException>(() => unsupported.Resolve());
    }

    [Fact]
    public void Validation_RejectsConflictingPhases_WhileAllowingZeroPhaseId()
    {
        var conflicting = new IfcExportConfiguration
        {
            Phases = new IfcPhaseConfiguration { Scope = IfcPhaseScope.AllPhases, PhaseId = 10 },
        };
        Assert.Contains(conflicting.Validate(), d => d.Code == "conflicting-phase-options");

        var zeroPhase = new IfcExportConfiguration
        {
            Phases = new IfcPhaseConfiguration { Scope = IfcPhaseScope.SpecificPhase, PhaseId = 0, RoomScope = IfcRoomPhaseScope.SpecificPhase, RoomPhaseId = 0 },
        };
        Assert.Empty(zeroPhase.Validate());
        Assert.Equal(0, zeroPhase.Resolve().ExportPhaseId);
    }

    [Fact]
    public void CapabilitiesMatrix_ListsSupportedScopedAndUnsupportedSettings()
    {
        Assert.NotEmpty(IfcExportCapabilities.All);
        Assert.Equal(IfcExportCapabilities.All.Count, IfcExportCapabilities.All.Select(c => c.SettingPath).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(IfcExportCapabilities.All, c => c.SettingPath == "schema.schema" && c.Status == IfcCapabilitySupportStatus.Supported);
        Assert.Contains(IfcExportCapabilities.All, c => c.SettingPath == "schema.modelView" && c.Status == IfcCapabilitySupportStatus.SchemaOrViewScoped);
        Assert.Contains(IfcExportCapabilities.All, c => c.SettingPath == "scope.exportLinkedFiles" && c.Status == IfcCapabilitySupportStatus.Unsupported);
    }

    [Fact]
    public void ExportRoomsAsSpacesFalse_OmitsIfcSpaceAndPreservesBuildingProducts()
    {
        var config = IfcExportConfiguration.FromPreset(IfcExportPresetKind.Ifc4ReferenceView12) with
        {
            Spatial = new IfcSpatialAndRoomConfiguration { ExportRoomsAsSpaces = false },
            UnitsAndCoordinates = new IfcCoordinateAndUnitConfiguration { HeaderTimestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z") },
        };

        var withSpaces = new StringWriter();
        new IfcExporter(config with { Spatial = new IfcSpatialAndRoomConfiguration { ExportRoomsAsSpaces = true } }).Export(MinimalDocument(), withSpaces);
        Assert.Contains("IFCSPACE(", withSpaces.ToString());

        var withoutSpaces = new StringWriter();
        new IfcExporter(config).Export(MinimalDocument(), withoutSpaces);
        string step = withoutSpaces.ToString();
        Assert.DoesNotContain("IFCSPACE(", step);
        Assert.Contains("IFCSLAB(", step);
    }

    [Fact]
    public void Cli_CapabilitiesSaveConfigAndInvalidCombinationBehavior()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"rvtio-cfg-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string configFile = Path.Combine(tempDir, "setup.json");
        try
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            try
            {
                var outSw = new StringWriter();
                var errSw = new StringWriter();
                Console.SetOut(outSw);
                Console.SetError(errSw);

                int capCode = Program.Main(["--ifc-capabilities"]);
                Assert.Equal(0, capCode);
                Assert.Contains("schema.schema", outSw.ToString());
                Assert.Contains("ISO 16739-1:2018", outSw.ToString());

                outSw.GetStringBuilder().Clear();
                errSw.GetStringBuilder().Clear();
                int saveCode = Program.Main(["--ifc-save-config", configFile, "--ifc-preset", "ifc4-reference-view-1.2", "--ifc-no-spaces"]);
                Assert.Equal(0, saveCode);
                Assert.True(File.Exists(configFile));
                var loaded = IfcExportConfiguration.FromJson(File.ReadAllText(configFile));
                var resolved = loaded.Resolve();
                Assert.Equal(IfcSchema.Ifc4, resolved.Schema);
                Assert.Equal(IfcModelView.ReferenceView12, resolved.EffectiveModelView);
                Assert.False(resolved.ExportRoomsAsSpaces);

                errSw.GetStringBuilder().Clear();
                int badCode = Program.Main(["model.rvt", "--ifc", Path.Combine(tempDir, "bad.ifc"), "--ifc-schema", "ifc2x3", "--ifc-mvd", "reference-view-1.2"]);
                Assert.Equal(1, badCode);
                Assert.Contains("incompatible-schema-model-view", errSw.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
