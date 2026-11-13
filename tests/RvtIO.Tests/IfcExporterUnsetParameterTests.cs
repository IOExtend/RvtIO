using System.Text.Json;
using System.Text.RegularExpressions;
using IOScene;
using RvtIO.BuiltIns;
using RvtIO.Ifc;
using RvtIO.Meshes;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

public class IfcExporterUnsetParameterTests
{
    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void RevitParameters_LeavesOutUnsetParametersAndKeepsStoredZeroAndEmptyText(IfcProfile profile)
    {
        var beam = CreateBeam(id: 300, typeId: 301, name: "BeamType_200x300");
        beam.Parameters.Clear();

        // Unset sentinels and synthesized placeholders across all StorageTypes -> omitted.
        beam.Parameters.Add(new Parameter
        {
            Id = 9001,
            Definition = new Definition { Id = 9001, Name = "UnsetDoubleSentinel" },
            StorageType = StorageType.Double,
            Value = 1e30,
        });
        beam.Parameters.Add(new Parameter
        {
            Id = 9002,
            Definition = new Definition { Id = 9002, Name = "UnsetDoubleNaN" },
            StorageType = StorageType.Double,
            Value = double.NaN,
        });
        beam.Parameters.Add(BipHelpers.MakeBipParam(-1001205, "ALL_MODEL_COST", StorageType.Double, 0.0, hasNoRevitValue: true));
        var approximateThermal = BipHelpers.MakeBipParam(-1005434, "ANALYTICAL_THERMAL_MASS", StorageType.Double, 453330.0);
        approximateThermal.IsApproximate = true;
        beam.Parameters.Add(approximateThermal);

        beam.Parameters.Add(BipHelpers.MakeBipParam(-1012101, "PHASE_DEMOLISHED", StorageType.ElementId, -1));
        beam.Parameters.Add(new Parameter
        {
            Id = 9003,
            Definition = new Definition { Id = 9003, Name = "ZeroElementId" },
            StorageType = StorageType.ElementId,
            Value = 0,
        });
        beam.Parameters.Add(new Parameter
        {
            Id = 9004,
            Definition = new Definition { Id = 9004, Name = "HostSentinelElementId" },
            StorageType = StorageType.ElementId,
            Value = -4,
        });

        beam.Parameters.Add(BipHelpers.MakeBipParam(-1002111, "WRAPPING_AT_ENDS_PARAM", StorageType.Integer, 0, hasNoRevitValue: true));
        beam.Parameters.Add(BipHelpers.MakeBipParam(-1019015, "IFC_EXPORT_ELEMENT_AS", StorageType.Integer, -1));
        beam.Parameters.Add(BipHelpers.MakeBipParam(-1114240, "RBS_CALCULATED_SIZE", StorageType.Integer, -1));
        beam.Parameters.Add(BipHelpers.MakeBipParam(-1010103, "ALL_MODEL_DESCRIPTION", StorageType.String, "", hasNoRevitValue: true));
        beam.Parameters.Add(BipHelpers.MakeBipParam(-1002103, "WALL_STRUCTURE_ID_PARAM", StorageType.None, -1, hasNoRevitValue: true));

        // Stored values including 0.0, 0, "", BuiltInCategory id, positive ElementId, and reassigned placeholder -> kept.
        beam.Parameters.Add(new Parameter
        {
            Id = 9010,
            Definition = new Definition { Id = 9010, Name = "StoredZeroReal" },
            StorageType = StorageType.Double,
            Value = 0.0,
        });
        beam.Parameters.Add(new Parameter
        {
            Id = 9011,
            Definition = new Definition { Id = 9011, Name = "StoredZeroInt" },
            StorageType = StorageType.Integer,
            Value = 0,
        });
        beam.Parameters.Add(new Parameter
        {
            Id = 9012,
            Definition = new Definition { Id = 9012, Name = "StoredEmptyText" },
            StorageType = StorageType.String,
            Value = "",
        });
        var reassigned = BipHelpers.MakeBipParam(-1001203, "ALL_MODEL_MARK", StorageType.String, "", hasNoRevitValue: true);
        reassigned.Value = "B-01";
        beam.Parameters.Add(reassigned);
        beam.Parameters.Add(BipHelpers.MakeBipParam(-1140362, "ELEM_CATEGORY_PARAM", StorageType.ElementId, -2001320));
        beam.Parameters.Add(BipHelpers.MakeBipParam(-1012100, "PHASE_CREATED", StorageType.ElementId, 3));

        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 3, CategoryId = -2003000, Name = "New Construction" });
        info.Elements.Add(beam);

        string path = TempIfcPath("unset-revit-params");
        try
        {
            new IfcExporter(includeRevitPropertySets: true) { Profile = profile }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("'UnsetDoubleSentinel'", ifc);
            Assert.DoesNotContain("'UnsetDoubleNaN'", ifc);
            Assert.DoesNotContain("'ALL_MODEL_COST'", ifc);
            Assert.DoesNotContain("'ANALYTICAL_THERMAL_MASS'", ifc);
            Assert.DoesNotContain("'PHASE_DEMOLISHED'", ifc);
            Assert.DoesNotContain("'ZeroElementId'", ifc);
            Assert.DoesNotContain("'HostSentinelElementId'", ifc);
            Assert.DoesNotContain("'WRAPPING_AT_ENDS_PARAM'", ifc);
            Assert.DoesNotContain("'IFC_EXPORT_ELEMENT_AS'", ifc);
            Assert.DoesNotContain("'RBS_CALCULATED_SIZE'", ifc);
            Assert.DoesNotContain("'ALL_MODEL_DESCRIPTION'", ifc);
            Assert.DoesNotContain("'WALL_STRUCTURE_ID_PARAM'", ifc);

            Assert.Matches(@"IFCPROPERTYSINGLEVALUE\('StoredZeroReal',(?:\$|''),IFCREAL\(0\.\),\$\)", ifc);
            Assert.Matches(@"IFCPROPERTYSINGLEVALUE\('StoredZeroInt',(?:\$|''),IFCINTEGER\(0\),\$\)", ifc);
            Assert.Matches(@"IFCPROPERTYSINGLEVALUE\('StoredEmptyText',(?:\$|''),IFCLABEL\(''\),\$\)", ifc);
            Assert.Matches(@"IFCPROPERTYSINGLEVALUE\('ALL_MODEL_MARK',(?:\$|''),IFCLABEL\('B-01'\),\$\)", ifc);
            Assert.Matches(@"IFCPROPERTYSINGLEVALUE\('ELEM_CATEGORY_PARAM',(?:\$|''),IFCINTEGER\(-2001320\),\$\)", ifc);
            Assert.Matches(@"IFCPROPERTYSINGLEVALUE\('PHASE_CREATED',(?:\$|''),IFCINTEGER\(3\),\$\)", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RevitParameterGroups_LeavesOutUnsetParametersAndKeepsFalseZeroAndCategory()
    {
        var beam = CreateBeam(id: 320, typeId: 321, name: "BeamType_200x300");
        beam.Parameters.Clear();

        // Unset parameters in API groups -> omitted.
        beam.Parameters.Add(BipHelpers.MakeBipParam(-1010103, "ALL_MODEL_DESCRIPTION", StorageType.String, "", hasNoRevitValue: true));
        beam.Parameters.Add(BipHelpers.MakeBipParam(-1001205, "ALL_MODEL_COST", StorageType.Double, 0.0, hasNoRevitValue: true));
        beam.Parameters.Add(BipHelpers.MakeBipParam(-1012101, "PHASE_DEMOLISHED", StorageType.ElementId, -1));
        beam.Parameters.Add(new Parameter { Id = -1001133, StorageType = StorageType.Double, Value = 1e30 }); // Area unset

        // Real stored 0 / false / "" / category -> kept.
        beam.Parameters.Add(new Parameter { Id = -1001007, StorageType = StorageType.Integer, Value = 0 }); // Room Bounding = false
        beam.Parameters.Add(new Parameter { Id = -1001361, StorageType = StorageType.Double, Value = 0.0 }); // Sill Height = 0
        beam.Parameters.Add(new Parameter { Id = -1010106, StorageType = StorageType.String, Value = "" }); // Comments = stored ""
        beam.Parameters.Add(new Parameter { Id = -1140362, StorageType = StorageType.ElementId, Value = -2001320 }); // Category
        beam.Parameters.Add(new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }); // Phase Created

        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 3, CategoryId = -2003000, Name = "New Construction" });
        info.Elements.Add(beam);

        string path = TempIfcPath("unset-revit-groups");
        try
        {
            new IfcExporter(includeRevitPropertySets: true)
            {
                RevitParameterGroups = true,
                Profile = IfcProfile.Current,
            }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("'Description'", ifc);
            Assert.DoesNotContain("'Cost'", ifc);
            Assert.DoesNotContain("'Phase Demolished'", ifc);
            Assert.DoesNotContain("'Area'", ifc);

            Assert.Contains("IFCPROPERTYSINGLEVALUE('Room Bounding','',IFCBOOLEAN(.F.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Sill Height','',IFCLENGTHMEASURE(0.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Comments','',IFCTEXT(''),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Category','',IFCLABEL('OST_StructuralFraming'),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Phase Created','',IFCLABEL('New Construction'),$)", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void NamedPropertyResolvers_TreatUnsetPlaceholdersAsAbsentNotBlocked()
    {
        var element = new Element
        {
            Id = 340,
            Parameters =
            [
                new Parameter
                {
                    Id = 9101,
                    Definition = new Definition { Id = 9101, Name = "Reference" },
                    StorageType = StorageType.ElementId,
                    Value = -1,
                },
                new Parameter
                {
                    Id = 9102,
                    Definition = new Definition { Id = 9102, Name = "Reference" },
                    StorageType = StorageType.Double,
                    Value = 1e30,
                },
                BipHelpers.MakeBipParam(9103, "Reference", StorageType.String, "", hasNoRevitValue: true),
            ],
        };
        var type = new Element
        {
            Id = 341,
            Parameters =
            [
                new Parameter
                {
                    Id = 9104,
                    Definition = new Definition { Id = 9104, Name = "Reference" },
                    StorageType = StorageType.String,
                    Value = "TypeRef-01",
                },
            ],
        };

        var resolved = ExactIfcStringParameterResolver.ResolveReference(element, type);
        Assert.Equal(ExactIfcStringParameterState.Value, resolved.State);
        Assert.Equal("TypeRef-01", resolved.Value);
    }

    [Fact]
    public void RvtSceneReader_LeavesOutUnsetPlaceholdersAndKeepsStoredZeroAndEmptyText()
    {
        var unsetComment = BipHelpers.MakeBipParam((int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "ALL_MODEL_INSTANCE_COMMENTS", StorageType.String, "", hasNoRevitValue: true);
        var unsetUserLength = new Parameter
        {
            Id = 9201,
            Definition = new Definition { Id = 9201, Name = "UnsetOffset" },
            StorageType = StorageType.Double,
            Value = 0.0,
            HasNoRevitValue = true,
            SpecType = "autodesk.spec.aec:length-2.0.0",
            UnitSymbol = "mm",
            ConversionFactor = 304.8,
        };
        var unsetSentinelLength = new Parameter
        {
            Id = 9202,
            Definition = new Definition { Id = 9202, Name = "SentinelLength" },
            StorageType = StorageType.Double,
            Value = 1e30,
            SpecType = "autodesk.spec.aec:length-2.0.0",
            UnitSymbol = "mm",
            ConversionFactor = 304.8,
        };
        var unsetUserInt = new Parameter
        {
            Id = 9203,
            Definition = new Definition { Id = 9203, Name = "UnsetFlag" },
            StorageType = StorageType.Integer,
            Value = 0,
            HasNoRevitValue = true,
        };

        var storedEmptyString = new Parameter
        {
            Id = 9210,
            Definition = new Definition { Id = 9210, Name = "StoredEmptyNote" },
            StorageType = StorageType.String,
            Value = "",
        };
        var storedZeroLength = new Parameter
        {
            Id = 9211,
            Definition = new Definition { Id = 9211, Name = "StoredZeroOffset" },
            StorageType = StorageType.Double,
            Value = 0.0,
            SpecType = "autodesk.spec.aec:length-2.0.0",
            UnitSymbol = "mm",
            ConversionFactor = 304.8,
        };
        var storedZeroInt = new Parameter
        {
            Id = 9212,
            Definition = new Definition { Id = 9212, Name = "StoredZeroFlag" },
            StorageType = StorageType.Integer,
            Value = 0,
        };

        var wall = new Element
        {
            Id = 10,
            UniqueId = "w-10",
            Parameters =
            [
                unsetComment, unsetUserLength, unsetSentinelLength, unsetUserInt,
                storedEmptyString, storedZeroLength, storedZeroInt,
            ],
        };
        var info = new RevitFileInfo { Elements = [wall] };
        var meshes = new MeshSet
        {
            Elements =
            [
                new ElementMesh
                {
                    ElementId = 10,
                    Meshes =
                    [
                        new TriangleMesh
                        {
                            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
                            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
                            Indices = [0, 1, 2],
                        },
                    ],
                },
            ],
        };

        SceneDocument scene = RvtSceneReader.Read(RvtDocument.FromInfo(info), meshes, new RvtSceneOptions { ModelKey = "m" }).Document;
        ScenePropertySet set = Assert.Single(scene.PropertySets);
        var byName = set.Properties.ToDictionary(p => p.Name, p => p.Value.Text);

        Assert.DoesNotContain("ALL_MODEL_INSTANCE_COMMENTS", byName.Keys);
        Assert.DoesNotContain("UnsetOffset", byName.Keys);
        Assert.DoesNotContain("SentinelLength", byName.Keys);
        Assert.DoesNotContain("UnsetFlag", byName.Keys);

        Assert.Equal("", byName["StoredEmptyNote"]);
        Assert.Equal("0 mm", byName["StoredZeroOffset"]);
        Assert.Equal("0", byName["StoredZeroFlag"]);
        Assert.Empty(scene.Validate());
    }

    [Fact]
    public void FullJsonAndPublicModel_PreserveRawParameterEntries()
    {
        var placeholder = BipHelpers.MakeBipParam(-1002002, "ALL_MODEL_FAMILY_NAME", StorageType.String, "", hasNoRevitValue: true);
        Assert.True(placeholder.HasValue);
        Assert.False(placeholder.CarriesRevitValue());

        var info = new RevitFileInfo
        {
            Elements = [new Element { Id = 50, Parameters = [placeholder] }],
        };
        string json = JsonSerializer.Serialize(info);
        Assert.DoesNotContain("HasNoRevitValue", json);
        Assert.Contains("-1002002", json);
    }

    private static string TempIfcPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"rvtio-{tag}-{Guid.NewGuid():N}.ifc");

    private static Element CreateBeam(int id, int typeId, string name)
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
        return new Element
        {
            Id = id,
            TypeId = typeId,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = name,
            References = [3],
            BoundingBox = bounds,
            Geometry = new GeometryData
            {
                BoundingBox = bounds,
                Faces = faceIndices.Select(idx => new GeoFace
                {
                    OuterVertices = idx.Select(i => points[i]).ToList(),
                }).ToList(),
                BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
            },
        };
    }
}
