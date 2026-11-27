using System.Globalization;
using System.Text.RegularExpressions;
using RvtIO.Cli;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class IfcExporterRevitParameterFormattingTests
{
    private static string TempIfcPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"rvtio-paramfmt-{tag}-{Guid.NewGuid():N}.ifc");

    private static RevitFileInfo SampleModelWithParameters()
    {
        var info = new RevitFileInfo();
        var phase = new Element { Id = 3, CategoryId = -2000112, Category = "OST_Phases", Name = "New Construction" };
        var level = new Element
        {
            Id = 10,
            CategoryId = -2000240,
            Category = "OST_Levels",
            Name = "Basement",
            IsNativeLevel = true,
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 },
                new Parameter
                {
                    Id = -1007102,
                    Definition = new Definition { Id = -1007102, BuiltInParameter = "LEVEL_ELEV", Name = "LEVEL_ELEV" },
                    StorageType = StorageType.Double,
                    Value = -5.0 / 0.3048, // -5 m = -500 cm
                },
                new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
            ],
        };

        double[][] points =
        [
            [-0.2, 0, 3.7], [0.2, 0, 3.7], [0.2, 0, 4.3], [-0.2, 0, 4.3],
            [-0.2, 10, 3.7], [0.2, 10, 3.7], [0.2, 10, 4.3], [-0.2, 10, 4.3],
        ];
        int[][] faceIndices =
        [
            [0, 1, 2, 3], [4, 7, 6, 5], [0, 4, 5, 1],
            [1, 5, 6, 2], [2, 6, 7, 3], [3, 7, 4, 0],
        ];
        double[] bounds = [-0.2, 0, 3.7, 0.2, 10, 4.3];
        var beamGeo = new GeometryData
        {
            BoundingBox = bounds,
            Faces = faceIndices.Select(idx => new GeoFace { OuterVertices = idx.Select(i => points[i]).ToList() }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };

        var wall = new Element
        {
            Id = 20,
            TypeId = 21,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "Beam_Test",
            References = [3],
            BoundingBox = bounds,
            Geometry = beamGeo,
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 },
                new Parameter
                {
                    Id = -1007102,
                    Definition = new Definition { Id = -1007102, BuiltInParameter = "LEVEL_ELEV", Name = "LEVEL_ELEV" },
                    StorageType = StorageType.Double,
                    Value = -5.0 / 0.3048, // -5 m = -500 cm
                },
                new Parameter
                {
                    Id = -1001108,
                    Definition = new Definition { Id = -1001108, BuiltInParameter = "WALL_BASE_OFFSET", Name = "WALL_BASE_OFFSET" },
                    StorageType = StorageType.Double,
                    Value = -3.0 / 0.3048, // -300 cm
                },
                new Parameter
                {
                    Id = -1001951,
                    Definition = new Definition { Id = -1001951, BuiltInParameter = "FLOOR_HEIGHTABOVELEVEL_PARAM", Name = "FLOOR_HEIGHTABOVELEVEL_PARAM" },
                    StorageType = StorageType.Double,
                    Value = 0.75 / 0.3048, // 75 cm
                },
                new Parameter
                {
                    Id = -1140414,
                    Definition = new Definition { Id = -1140414, BuiltInParameter = "PHY_MATERIAL_PARAM_BENDING", Name = "PHY_MATERIAL_PARAM_BENDING" },
                    StorageType = StorageType.Double,
                    Value = 304800.0, // internal kg/(ft s^2) -> 1,000,000 Pa
                },
                new Parameter
                {
                    Id = -1001133,
                    Definition = new Definition { Id = -1001133, BuiltInParameter = "HOST_AREA_COMPUTED", Name = "HOST_AREA_COMPUTED" },
                    StorageType = StorageType.Double,
                    Value = 1.0 / (0.3048 * 0.3048), // 1 m^2 = 10000 cm^2
                },
                new Parameter
                {
                    Id = -1001129,
                    Definition = new Definition { Id = -1001129, BuiltInParameter = "HOST_VOLUME_COMPUTED", Name = "HOST_VOLUME_COMPUTED" },
                    StorageType = StorageType.Double,
                    Value = 1.0 / (0.3048 * 0.3048 * 0.3048), // 1 m^3 = 1000000 cm^3
                },
                new Parameter
                {
                    Id = -1155217,
                    Definition = new Definition { Id = -1155217, BuiltInParameter = "REBAR_ELEM_HOOK_ROTATION_AT_END", Name = "REBAR_ELEM_HOOK_ROTATION_AT_END" },
                    StorageType = StorageType.Double,
                    Value = Math.PI / 2, // 90 deg
                },
                new Parameter
                {
                    Id = -1152308,
                    Definition = new Definition { Id = -1152308, BuiltInParameter = "PHY_MATERIAL_PARAM_THERMAL_CONDUCTIVITY", Name = "PHY_MATERIAL_PARAM_THERMAL_CONDUCTIVITY" },
                    StorageType = StorageType.Double,
                    Value = 5.0, // 5 * 0.3048 = 1.524 W/(m K)
                },
                new Parameter
                {
                    Id = -1005430,
                    Definition = new Definition { Id = -1005430, BuiltInParameter = "ANALYTICAL_HEAT_TRANSFER_COEFFICIENT", Name = "ANALYTICAL_HEAT_TRANSFER_COEFFICIENT" },
                    StorageType = StorageType.Double,
                    Value = 2.25, // 2.25 W/(m^2 K)
                },
                new Parameter
                {
                    Id = -1152313,
                    Definition = new Definition { Id = -1152313, BuiltInParameter = "PHY_MATERIAL_PARAM_STRUCTURAL_DENSITY", Name = "PHY_MATERIAL_PARAM_STRUCTURAL_DENSITY" },
                    StorageType = StorageType.Double,
                    Value = 2400.0 * (0.3048 * 0.3048 * 0.3048), // 2400 kg/m^3
                },
                new Parameter
                {
                    Id = 900001,
                    Definition = new Definition { Id = 900001, Name = "900001" },
                    StorageType = StorageType.String,
                    Value = "Z-1",
                },
                new Parameter
                {
                    Id = 900002,
                    StorageType = StorageType.Double,
                    Value = 2.0, // 2 ft = 60.96 cm
                },
            ],
        };

        var state = new ParsingState();
        state.ElemClassIdx[900001] = 2873;
        var np1 = new TfBlockDeserializer.NativeParams();
        np1.Fields[TfClassNames.Key("ParamDef", "m_paramElemId")] = 900001;
        np1.Fields["2870:m_pParamDef.class"] = 606;
        np1.Fields[TfClassNames.Key("ParamDef", "m_groupElemId")] = -5000100;
        np1.StringFields[TfClassNames.Key("ParamDef", "m_caption")] = "Test Zone";
        state.NativeParams[900001] = np1;

        state.ElemClassIdx[900002] = 2873;
        var np2 = new TfBlockDeserializer.NativeParams();
        np2.Fields[TfClassNames.Key("ParamDef", "m_paramElemId")] = 900002;
        np2.Fields["2870:m_pParamDef.class"] = 612;
        np2.Fields[TfClassNames.Key("ParamDef", "m_groupElemId")] = -5000101;
        np2.StringFields[TfClassNames.Key("ParamDef", "m_caption")] = "Test Clearance";
        np2.StringFields[TfClassNames.Key("ParamDefValue", "m_specTypeId")] = "autodesk.spec.aec:length-2.0.0";
        state.NativeParams[900002] = np2;
        info.UserParameters = UserParameterCatalog.Build(state);
        info.Elements.Add(phase);
        info.Elements.Add(level);
        info.Elements.Add(wall);
        return info;
    }

    [Fact]
    public void RevitParameters_DefaultMode_PreservesInternalUnitsAndBuiltInNames()
    {
        var info = SampleModelWithParameters();
        string path = TempIfcPath("default");
        try
        {
            new IfcExporter(includeRevitPropertySets: true)
            {
                LengthUnit = IfcUnitFamily.Centimetre,
            }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Contains("IFCPROPERTYSINGLEVALUE('WALL_BASE_OFFSET','',IFCREAL(", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('PHY_MATERIAL_PARAM_BENDING','',IFCREAL(304800.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('900001','',IFCLABEL('Z-1'),$)", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RevitParameters_WithIfcUnitsAndDisplayNames_ConvertsUnitsAndResolvesNamesAndDescriptions()
    {
        var info = SampleModelWithParameters();
        string path = TempIfcPath("ifc-display");
        try
        {
            new IfcExporter(includeRevitPropertySets: true)
            {
                LengthUnit = IfcUnitFamily.Centimetre,
                RevitParameterUnits = IfcRevitParameterUnits.Ifc,
                RevitParameterNames = IfcRevitParameterNames.Display,
            }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Contains("IFCPROPERTYSINGLEVALUE('Elevation','LEVEL_ELEV',IFCLENGTHMEASURE(-500.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Base Offset','WALL_BASE_OFFSET',IFCLENGTHMEASURE(-300.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Height Offset From Level','FLOOR_HEIGHTABOVELEVEL_PARAM',IFCLENGTHMEASURE(75.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Bending','PHY_MATERIAL_PARAM_BENDING',IFCPRESSUREMEASURE(1000000.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Area','HOST_AREA_COMPUTED',IFCAREAMEASURE(10000.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Volume','HOST_VOLUME_COMPUTED',IFCVOLUMEMEASURE(1000000.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Hook Rotation At End','REBAR_ELEM_HOOK_ROTATION_AT_END',IFCPLANEANGLEMEASURE(90.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Thermal Conductivity','PHY_MATERIAL_PARAM_THERMAL_CONDUCTIVITY',IFCTHERMALCONDUCTIVITYMEASURE(1.524),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Heat Transfer Coefficient (U)','ANALYTICAL_HEAT_TRANSFER_COEFFICIENT',IFCTHERMALTRANSMITTANCEMEASURE(2.25),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Density','PHY_MATERIAL_PARAM_STRUCTURAL_DENSITY',IFCMASSDENSITYMEASURE(2400.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Test Zone','',IFCLABEL('Z-1'),$)", ifc);
            Assert.Matches(@"IFCPROPERTYSINGLEVALUE\('Test Clearance','',IFCLENGTHMEASURE\(60\.96\d*\),\$\)", ifc);
            Assert.Contains(".THERMALTRANSMITTANCEUNIT.", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RevitParameters_DisplayNames_DoesNotMergeDifferentBuiltInsInPropertyValueCache()
    {
        double[][] points =
        [
            [-0.2, 0, 3.7], [0.2, 0, 3.7], [0.2, 0, 4.3], [-0.2, 0, 4.3],
            [-0.2, 10, 3.7], [0.2, 10, 3.7], [0.2, 10, 4.3], [-0.2, 10, 4.3],
        ];
        int[][] faceIndices =
        [
            [0, 1, 2, 3], [4, 7, 6, 5], [0, 4, 5, 1],
            [1, 5, 6, 2], [2, 6, 7, 3], [3, 7, 4, 0],
        ];
        double[] bounds = [-0.2, 0, 3.7, 0.2, 10, 4.3];
        var wall1 = new Element
        {
            Id = 20,
            TypeId = 21,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "Beam 1",
            References = [3],
            BoundingBox = bounds,
            Geometry = new GeometryData
            {
                BoundingBox = bounds,
                Faces = faceIndices.Select(idx => new GeoFace { OuterVertices = idx.Select(i => points[i]).ToList() }).ToList(),
                BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
            },
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 },
                new Parameter { Id = -1001108, StorageType = StorageType.Double, Value = 0.0 }, // WALL_BASE_OFFSET ("Base Offset")
                new Parameter { Id = -1001357, StorageType = StorageType.Double, Value = 1.0 }, // FAMILY_BASE_LEVEL_OFFSET_PARAM ("Base Offset")
            ],
        };
        // carries one of the two only, with the same value: nothing to suffix, and nothing to share
        var wall2 = new Element
        {
            Id = 22,
            TypeId = 21,
            IsDiscovered = true,
            CategoryId = wall1.CategoryId,
            Category = wall1.Category,
            Name = "Beam 2",
            References = [3],
            BoundingBox = bounds,
            Geometry = wall1.Geometry,
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 },
                new Parameter { Id = -1001357, StorageType = StorageType.Double, Value = 0.0 },
            ],
        };
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 3, CategoryId = -2000112, Category = "OST_Phases", Name = "New Construction" });
        info.Elements.Add(wall1);
        info.Elements.Add(wall2);

        string path = TempIfcPath("cache-desc");
        try
        {
            new IfcExporter(includeRevitPropertySets: true)
            {
                RevitParameterUnits = IfcRevitParameterUnits.Ifc,
                RevitParameterNames = IfcRevitParameterNames.Display,
            }.Export(info, path);
            string ifc = File.ReadAllText(path);

            // IfcPropertySet requires unique property names: both values survive, each under its parameter id
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Base Offset [-1001108]','WALL_BASE_OFFSET',IFCLENGTHMEASURE(0.),$)", ifc);
            Assert.Matches(@"IFCPROPERTYSINGLEVALUE\('Base Offset \[-1001357\]','FAMILY_BASE_LEVEL_OFFSET_PARAM',IFCLENGTHMEASURE\(304\.8\d*\),\$\)", ifc);
            // without a clash the name stays plain, and the cache does not hand out another parameter's description
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Base Offset','FAMILY_BASE_LEVEL_OFFSET_PARAM',IFCLENGTHMEASURE(0.),$)", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RevitParameters_DisplayNames_ResolvesCompactOnlyBuiltInDisplayName()
    {
        double[][] points =
        [
            [-0.2, 0, 3.7], [0.2, 0, 3.7], [0.2, 0, 4.3], [-0.2, 0, 4.3],
            [-0.2, 10, 3.7], [0.2, 10, 3.7], [0.2, 10, 4.3], [-0.2, 10, 4.3],
        ];
        int[][] faceIndices =
        [
            [0, 1, 2, 3], [4, 7, 6, 5], [0, 4, 5, 1],
            [1, 5, 6, 2], [2, 6, 7, 3], [3, 7, 4, 0],
        ];
        double[] bounds = [-0.2, 0, 3.7, 0.2, 10, 4.3];
        var wall = new Element
        {
            Id = 20,
            TypeId = 21,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "Beam 1",
            References = [3],
            BoundingBox = bounds,
            Geometry = new GeometryData
            {
                BoundingBox = bounds,
                Faces = faceIndices.Select(idx => new GeoFace { OuterVertices = idx.Select(i => points[i]).ToList() }).ToList(),
                BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
            },
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 },
                new Parameter
                {
                    Id = -1155224, // present in compact_builtin_parameters.tsv, absent from api_parameter_definitions_2022.json
                    Definition = new Definition { Id = -1155224, BuiltInParameter = "REBAR_INSTANCE_BAR_MODEL_DIAMETER", Name = "REBAR_INSTANCE_BAR_MODEL_DIAMETER" },
                    StorageType = StorageType.Double,
                    Value = 0.25,
                },
            ],
        };
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 3, CategoryId = -2000112, Category = "OST_Phases", Name = "New Construction" });
        info.Elements.Add(wall);

        string path = TempIfcPath("compact-only");
        try
        {
            new IfcExporter(includeRevitPropertySets: true)
            {
                RevitParameterNames = IfcRevitParameterNames.Display,
            }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Contains("IFCPROPERTYSINGLEVALUE('Model Bar Diameter','REBAR_INSTANCE_BAR_MODEL_DIAMETER',IFCREAL(0.25),$)", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("autodesk.spec.aec.electrical:powerDensity", "IFCREAL", 1.0)] // W/m^2 = kg/s^3
    [InlineData("autodesk.spec.aec.structural:forceScale", "IFCREAL", 1.0)] // m/kN = s^2/kg
    [InlineData("autodesk.spec.aec.structural:areaForceScale", "IFCREAL", 0.3048 * 0.3048)] // m^3/kN
    [InlineData("autodesk.spec.aec.structural:momentScale", "IFCREAL", 1 / 0.3048)] // 1/kN
    [InlineData("autodesk.spec.aec.structural:linearForceScale", "IFCREAL", 0.3048)] // m^2/kN
    [InlineData("autodesk.spec.aec.structural:sectionProperty", "IFCLENGTHMEASURE", 304.8)] // a length, in mm before the file unit
    public void ConvertDoubleToIfcOrSi_UsesTheLengthExponentOfTheSpec(string spec, string measure, double expected)
    {
        string? converted = IfcExporter.ConvertDoubleToIfcOrSi(1.0, spec, currentProfile: true);

        Assert.NotNull(converted);
        Assert.StartsWith(measure + "(", converted);
        double actual = double.Parse(converted[(measure.Length + 1)..^1], CultureInfo.InvariantCulture);
        Assert.Equal(expected, actual, expected * 1e-12);
    }

    [Fact]
    public void RevitParameters_WithIfcUnits_DoNotGuessASpecFromTheParameterName()
    {
        var info = SingleElementModel(
        [
            new Parameter
            {
                Id = -999999001,
                Definition = new Definition { Id = -999999001, BuiltInParameter = "TEST_ONLY_SLOPE_AREA", Name = "TEST_ONLY_SLOPE_AREA" },
                StorageType = StorageType.Double,
                Value = 0.5,
            },
        ]);

        string ifc = ExportText(info, new IfcExporter(includeRevitPropertySets: true) { RevitParameterUnits = IfcRevitParameterUnits.Ifc });

        Assert.Contains("IFCPROPERTYSINGLEVALUE('TEST_ONLY_SLOPE_AREA','',IFCREAL(0.5),$)", ifc);
    }

    // spec, and the project unit that gives its measure a meaning: a named SI unit, or the factors of a derived one
    private static readonly (string Spec, string Unit)[] MeasureUnits =
    [
        ("autodesk.spec.aec.structural:stress", "PRESSUREUNIT=PASCAL"),
        ("autodesk.spec.aec.structural:areaForce", "PLANARFORCEUNIT=KILOGRAM^1 METRE^-1 SECOND^-2"),
        ("autodesk.spec.aec.hvac:viscosity", "DYNAMICVISCOSITYUNIT=KILOGRAM^1 METRE^-1 SECOND^-1"),
        ("autodesk.spec.aec.energy:permeability", "VAPORPERMEABILITYUNIT=METRE^-1 SECOND^1"),
        ("autodesk.spec.aec.structural:massPerUnitLength", "MASSPERLENGTHUNIT=KILOGRAM^1 METRE^-1"),
        ("autodesk.spec.aec.structural:force", "FORCEUNIT=NEWTON"),
        ("autodesk.spec.aec.hvac:velocity", "LINEARVELOCITYUNIT=METRE^1 SECOND^-1"),
        ("autodesk.spec.aec.energy:thermalConductivity", "THERMALCONDUCTANCEUNIT=KELVIN^-1 KILOGRAM^1 METRE^1 SECOND^-3"),
        ("autodesk.spec.aec.structural:linearMoment", "LINEARMOMENTUNIT=KILOGRAM^1 METRE^1 SECOND^-2"),
        ("autodesk.spec.aec.electrical:power", "POWERUNIT=WATT"),
        ("autodesk.spec.aec.electrical:potential", "ELECTRICVOLTAGEUNIT=VOLT"),
        ("autodesk.spec.aec.energy:specificHeat", "SPECIFICHEATCAPACITYUNIT=KELVIN^-1 METRE^2 SECOND^-2"),
        ("autodesk.spec.aec.energy:specificHeatOfVaporization", "HEATINGVALUEUNIT=METRE^2 SECOND^-2"),
        ("autodesk.spec.aec.structural:moment", "TORQUEUNIT=KILOGRAM^1 METRE^2 SECOND^-2"),
        ("autodesk.spec.aec:energy", "ENERGYUNIT=JOULE"),
        ("autodesk.spec.aec.electrical:illuminance", "ILLUMINANCEUNIT=LUX"),
        ("autodesk.spec.aec.hvac:airFlow", "VOLUMETRICFLOWRATEUNIT=METRE^3 SECOND^-1"),
        ("autodesk.spec.aec.structural:sectionModulus", "SECTIONMODULUSUNIT=METRE^3"),
        ("autodesk.spec.aec:massDensity", "MASSDENSITYUNIT=KILOGRAM^1 METRE^-3"),
        ("autodesk.spec.aec.structural:momentOfInertia", "MOMENTOFINERTIAUNIT=METRE^4"),
        ("autodesk.spec.aec.structural:warpingConstant", "WARPINGCONSTANTUNIT=METRE^6"),
        ("autodesk.spec.aec.energy:heatTransferCoefficient", "THERMALTRANSMITTANCEUNIT=KELVIN^-1 KILOGRAM^1 SECOND^-3"),
        ("autodesk.spec.aec.energy:thermalResistance", "THERMALRESISTANCEUNIT=KELVIN^1 KILOGRAM^-1 SECOND^3"),
        ("autodesk.spec.aec.electrical:current", "ELECTRICCURRENTUNIT=AMPERE"),
        ("autodesk.spec.aec.electrical:luminousFlux", "LUMINOUSFLUXUNIT=LUMEN"),
        ("autodesk.spec.aec.electrical:luminousIntensity", "LUMINOUSINTENSITYUNIT=CANDELA"),
        ("autodesk.spec.aec.hvac:temperature", "THERMODYNAMICTEMPERATUREUNIT=KELVIN"),
        ("autodesk.spec.aec.structural:mass", "MASSUNIT=KILOGRAM"),
        ("autodesk.spec.aec.structural:linearForce", "LINEARFORCEUNIT=KILOGRAM^1 SECOND^-2"),
        ("autodesk.spec.aec.structural:thermalExpansionCoefficient", "THERMALEXPANSIONCOEFFICIENTUNIT=KELVIN^-1"),
        ("autodesk.spec.aec:time", "TIMEUNIT=SECOND"),
        ("autodesk.spec.aec:frequency", "FREQUENCYUNIT=HERTZ"),
    ];

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void RevitParameters_WithIfcUnits_DeclareTheUnitOfEveryMeasureOnce(IfcProfile profile)
    {
        var info = SingleElementModel(MeasureUnits.Select((entry, i) => new Parameter
        {
            Id = 900100 + i,
            Definition = new Definition { Id = 900100 + i, Name = $"Test {i}" },
            SpecType = entry.Spec + "-2.0.0",
            StorageType = StorageType.Double,
            Value = 1.0,
        }).ToList());

        var declared = ProjectUnits(ExportText(info, new IfcExporter(includeRevitPropertySets: true)
        {
            Profile = profile,
            RevitParameterUnits = IfcRevitParameterUnits.Ifc,
        }));

        Assert.Equal(declared.Count, declared.Select(unit => unit.Split('=')[0]).Distinct().Count());
        Assert.All(MeasureUnits, entry => Assert.Contains(entry.Unit, declared));
    }

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void RevitParameters_WithoutIfcUnits_DeclareNoAdditionalUnits(IfcProfile profile)
    {
        var info = SingleElementModel(
        [
            new Parameter { Id = 900100, SpecType = "autodesk.spec.aec.structural:stress-2.0.0", StorageType = StorageType.Double, Value = 1.0 },
        ]);

        var declared = ProjectUnits(ExportText(info, new IfcExporter(includeRevitPropertySets: true) { Profile = profile }));

        Assert.DoesNotContain(declared, unit => unit.StartsWith("PRESSUREUNIT=", StringComparison.Ordinal));
    }

    private static RevitFileInfo SingleElementModel(List<Parameter> parameters)
    {
        double[][] points =
        [
            [-0.2, 0, 3.7], [0.2, 0, 3.7], [0.2, 0, 4.3], [-0.2, 0, 4.3],
            [-0.2, 10, 3.7], [0.2, 10, 3.7], [0.2, 10, 4.3], [-0.2, 10, 4.3],
        ];
        int[][] faceIndices =
        [
            [0, 1, 2, 3], [4, 7, 6, 5], [0, 4, 5, 1],
            [1, 5, 6, 2], [2, 6, 7, 3], [3, 7, 4, 0],
        ];
        double[] bounds = [-0.2, 0, 3.7, 0.2, 10, 4.3];
        parameters.Insert(0, new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 });
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 3, CategoryId = -2000112, Category = "OST_Phases", Name = "New Construction" });
        info.Elements.Add(new Element
        {
            Id = 20,
            TypeId = 21,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "Beam 1",
            References = [3],
            BoundingBox = bounds,
            Geometry = new GeometryData
            {
                BoundingBox = bounds,
                Faces = faceIndices.Select(idx => new GeoFace { OuterVertices = idx.Select(i => points[i]).ToList() }).ToList(),
                BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
            },
            Parameters = parameters,
        });
        return info;
    }

    private static string ExportText(RevitFileInfo info, IfcExporter exporter)
    {
        var text = new StringWriter();
        exporter.Export(info, text);
        return text.ToString();
    }

    /// <summary>The assigned project units as <c>TYPE=NAME</c>, or <c>TYPE=FACTOR^exponent ...</c> with the factors by name.</summary>
    private static List<string> ProjectUnits(string ifc)
    {
        var entities = Regex.Matches(ifc, @"^#(\d+)\s*=\s*([A-Z0-9]+)\((.*)\);\r?$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => (Type: m.Groups[2].Value, Args: m.Groups[3].Value));
        static IEnumerable<string> References(string args) => Regex.Matches(args, @"#(\d+)").Select(m => m.Groups[1].Value);
        static string SiName(string args)
        {
            string[] parts = args.Split(',');
            return (parts[2] == ".KILO." ? "KILO" : "") + parts[3].Trim('.');
        }

        var assignment = Assert.Single(entities.Values, e => e.Type == "IFCUNITASSIGNMENT");
        return References(assignment.Args).Select(id =>
        {
            var (type, args) = entities[id];
            string unitType = Regex.Match(args, @"\.([A-Z]+UNIT)\.").Groups[1].Value;
            if (type == "IFCSIUNIT") return $"{unitType}={SiName(args)}";
            if (type != "IFCDERIVEDUNIT") return $"{unitType}={type}";
            var factors = References(args).Select(element =>
            {
                string[] parts = entities[element].Args.Split(',');
                return $"{SiName(entities[parts[0].TrimStart('#')].Args)}^{parts[1]}";
            });
            return $"{unitType}={string.Join(' ', factors.Order(StringComparer.Ordinal))}";
        }).ToList();
    }

    [Fact]
    public void Configuration_RoundTripsRevitParameterUnitsAndNamesThroughJson()
    {
        var config = new IfcExportConfiguration
        {
            PropertySets = new IfcPropertySetConfiguration
            {
                ExportRevitPropertySets = true,
                RevitParameterUnits = IfcRevitParameterUnits.Ifc,
                RevitParameterNames = IfcRevitParameterNames.Display,
            },
        };

        string json = config.ToJson();
        Assert.Contains("\"revitParameterUnits\": \"ifc\"", json);
        Assert.Contains("\"revitParameterNames\": \"display\"", json);

        var reloaded = IfcExportConfiguration.FromJson(json);
        var resolved = reloaded.Resolve();
        Assert.True(resolved.ExportRevitPropertySets);
        Assert.Equal(IfcRevitParameterUnits.Ifc, resolved.RevitParameterUnits);
        Assert.Equal(IfcRevitParameterNames.Display, resolved.RevitParameterNames);
    }

    [Fact]
    public void CliOptions_ParsesRevitParameterUnitsAndNamesAndRejectsInvalidValues()
    {
        Assert.True(IfcConfigurationOptions.TryParse(
            ["--ifc-revit-parameter-units", "ifc", "--ifc-revit-parameter-names", "display"],
            out var parsed, out string? error));
        Assert.Null(error);
        Assert.Equal(IfcRevitParameterUnits.Ifc, parsed.RevitParameterUnits);
        Assert.Equal(IfcRevitParameterNames.Display, parsed.RevitParameterNames);

        Assert.True(IfcConfigurationOptions.TryBuildConfiguration(
            parsed, IfcProfile.Current, false,
            revitParameters: false, revitParameterGroups: false,
            baseQuantities: true, false, false, false,
            null, false, false, null, false, false, null, null,
            out _, out var resolved, out string? buildError));
        Assert.Null(buildError);
        Assert.True(resolved.ExportRevitPropertySets);
        Assert.Equal(IfcRevitParameterUnits.Ifc, resolved.RevitParameterUnits);
        Assert.Equal(IfcRevitParameterNames.Display, resolved.RevitParameterNames);

        Assert.False(IfcConfigurationOptions.TryParse(["--ifc-revit-parameter-units", "metric"], out _, out _));
        Assert.False(IfcConfigurationOptions.TryParse(["--ifc-revit-parameter-names", "localized"], out _, out _));
    }

    [Fact]
    public void Cli_CapabilitiesListsRevitParameterUnitsAndNames()
    {
        using var sw = new StringWriter();
        IfcConfigurationOptions.WriteCapabilities(sw);
        string text = sw.ToString();
        Assert.Contains("propertySets.revitParameterUnits", text);
        Assert.Contains("propertySets.revitParameterNames", text);
    }

    [Fact]
    public void Cli_RejectsInvalidRevitParameterFormattingFlagsAndRequiresIfcOutput()
    {
        Assert.NotEqual(0, Program.Main(["model.rvt", "out.ifc", "--ifc-revit-parameter-units", "bogus"]));
        Assert.NotEqual(0, Program.Main(["model.rvt", "out.ifc", "--ifc-revit-parameter-names", "bogus"]));
        Assert.NotEqual(0, Program.Main(["model.rvt", "--ifc-revit-parameter-units", "ifc"]));
        Assert.NotEqual(0, Program.Main(["model.rvt", "--ifc-revit-parameter-names", "display"]));
    }
}
