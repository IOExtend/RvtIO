using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RvtIO.BuiltIns;
using RvtIO.Gltf;
using RvtIO.Internal;
using RvtIO.Meshes;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class GlbExporterTests
{
    private const double MetresPerFoot = 0.3048;
    private const int Phase = 7;
    private const int WallId = 100;
    private const int DoorAId = 101;
    private const int DoorBId = 102;
    private const int MultiPlacementId = 103;
    private const int NoGeomId = 104;
    private const int TypeId = 200;
    private const int SymbolId = 300;
    private const int ConcreteMatId = 500;
    private const int GlassMatId = 501;

    private static Parameter Bip(BuiltInParameter bip, object value, StorageType? storage = null) => new()
    {
        Id = (int)bip,
        Value = value,
        StorageType = storage ?? (value switch { double => StorageType.Double, string => StorageType.String, _ => StorageType.Integer }),
        Definition = new Definition { Id = (int)bip, BuiltInParameter = bip.ToString(), Name = bip.ToString() },
    };

    private static Element MakeElement(int id, int categoryId, string category, string name, params Parameter[] extra) => new()
    {
        Id = id,
        UniqueId = $"d45b1816-cbcc-4dc6-b0bb-007467f3c600-{id:x8}",
        CategoryId = categoryId,
        Category = category,
        Name = name,
        TypeId = TypeId,
        SerializedCreatedPhaseId = Phase,
        Parameters = [Bip(BuiltInParameter.ELEM_TYPE_PARAM, TypeId, StorageType.ElementId), .. extra],
    };

    private static RvtDocument SampleDocument() => RvtDocument.FromInfo(new RevitFileInfo
    {
        Elements =
        [
            MakeElement(WallId, (int)BuiltInCategory.OST_Walls, "OST_Walls", "Exterior Wall",
                Bip(BuiltInParameter.IFC_GUID, "3KMnWMoynDnh2x07HdzyEW")),
            MakeElement(DoorAId, (int)BuiltInCategory.OST_Doors, "OST_Doors", "Single Door Normal"),
            MakeElement(DoorBId, (int)BuiltInCategory.OST_Doors, "OST_Doors", "Single Door Mirrored"),
            MakeElement(MultiPlacementId, (int)BuiltInCategory.OST_Furniture, "OST_Furniture", "Pair Assembly"),
            MakeElement(NoGeomId, (int)BuiltInCategory.OST_GenericModel, "OST_GenericModel", "No Geometry Marker"),
            new Element { Id = TypeId, Name = "Standard 200" },
            new Element { Id = SymbolId, Name = "Flush Panel Door" },
            new Element
            {
                Id = ConcreteMatId,
                UniqueId = $"d45b1816-cbcc-4dc6-b0bb-007467f3c600-{ConcreteMatId:x8}",
                CategoryId = (int)BuiltInCategory.OST_Materials,
                Category = "OST_Materials",
                Name = "Cast-in-Place Concrete",
                Parameters = [Bip(BuiltInParameter.MATERIAL_PARAM_COLOR, 0x004080C0)], // R=0xC0, G=0x80, B=0x40
            },
            new Element
            {
                Id = GlassMatId,
                UniqueId = $"d45b1816-cbcc-4dc6-b0bb-007467f3c600-{GlassMatId:x8}",
                CategoryId = (int)BuiltInCategory.OST_Materials,
                Category = "OST_Materials",
                Name = "Clear Glazing",
                Parameters =
                [
                    Bip(BuiltInParameter.MATERIAL_PARAM_COLOR, 0x00FFA060), // R=0x60, G=0xA0, B=0xFF
                    Bip(BuiltInParameter.MATERIAL_PARAM_TRANSPARENCY, 65),
                ],
            },
        ],
    });

    private static TriangleMesh RightTriangle(double x, double y, double z, int materialId = 0) => new()
    {
        Positions = [x, y, z, x + 2, y, z, x, y + 3, z],
        Indices = [0, 1, 2],
        Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1],
        MaterialId = materialId,
    };

    private static (JsonDocument Json, byte[] Bin) ParseGlb(byte[] glb)
    {
        Assert.True(glb.Length >= 20, "GLB smaller than header + JSON chunk header.");
        Assert.Equal("glTF"u8.ToArray(), glb[0..4]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4, 4)));
        uint totalLen = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8, 4));
        Assert.Equal((uint)glb.Length, totalLen);

        uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        Assert.Equal(0u, jsonLen % 4);
        Assert.Equal("JSON"u8.ToArray(), glb[16..20]);

        int binStart = 20 + (int)jsonLen;
        byte[] jsonBytes = glb[20..binStart];
        Assert.True(binStart <= glb.Length);

        byte[] bin = [];
        if (binStart < glb.Length)
        {
            Assert.True(binStart + 8 <= glb.Length);
            uint binLen = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binStart, 4));
            Assert.Equal(0u, binLen % 4);
            Assert.Equal("BIN\0"u8.ToArray(), glb[(binStart + 4)..(binStart + 8)]);
            Assert.Equal(binStart + 8 + (int)binLen, glb.Length);
            bin = glb[(binStart + 8)..];
        }

        var json = JsonDocument.Parse(jsonBytes);
        return (json, bin);
    }

    private static float[] ReadFloatAccessor(JsonElement root, byte[] bin, int accessorIndex)
    {
        var acc = root.GetProperty("accessors")[accessorIndex];
        var bv = root.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
        int byteOffset = bv.GetProperty("byteOffset").GetInt32() + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        int count = acc.GetProperty("count").GetInt32();
        string type = acc.GetProperty("type").GetString()!;
        int components = type switch { "VEC3" => 3, "VEC2" => 2, "SCALAR" => 1, _ => throw new NotSupportedException(type) };
        var result = new float[count * components];
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(byteOffset + i * 4, 4));
        return result;
    }

    private static float[] MultiplyPoint(float[] mat4, float x, float y, float z) =>
    [
        mat4[0] * x + mat4[4] * y + mat4[8] * z + mat4[12],
        mat4[1] * x + mat4[5] * y + mat4[9] * z + mat4[13],
        mat4[2] * x + mat4[6] * y + mat4[10] * z + mat4[14],
    ];

    private static float Det3x3(float[] mat4) =>
        mat4[0] * (mat4[5] * mat4[10] - mat4[9] * mat4[6])
        - mat4[4] * (mat4[1] * mat4[10] - mat4[9] * mat4[2])
        + mat4[8] * (mat4[1] * mat4[6] - mat4[5] * mat4[2]);

    [Fact]
    public void ConvertsZUpFeetToYUpMetres_WithExactOrientationAndPosition()
    {
        var doc = SampleDocument();
        // Rotate 90 deg around +Z in Revit and translate to (10, 20, 30) ft.
        var rotated = new MeshTransform(new(10, 20, 30), new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1));
        var meshes = new MeshSet
        {
            Symbols = new Dictionary<int, IReadOnlyList<TriangleMesh>>
            {
                [SymbolId] = [RightTriangle(0, 0, 0, ConcreteMatId)],
            },
            Elements =
            [
                // Own mesh translated far from origin: LowestCorner should rebase vertices to Y-up metres.
                new ElementMesh { ElementId = WallId, Meshes = [RightTriangle(100, 200, 50)], Coverage = MeshCoverage.Complete },
                new ElementMesh { ElementId = DoorAId, Placements = [new(SymbolId, rotated)], Coverage = MeshCoverage.Complete },
            ],
        };

        byte[] glb = GlbExporter.ExportToBytes(doc, out var result, meshes);
        Assert.Equal(2, result.ElementCount);
        Assert.Equal(2, result.ElementsWithGeometry);

        var (json, bin) = ParseGlb(glb);
        using (json)
        {
            var root = json.RootElement;
            Assert.Equal("2.0", root.GetProperty("asset").GetProperty("version").GetString());

            var nodes = root.GetProperty("nodes");
            var glMeshes = root.GetProperty("meshes");

            // 1. Own mesh node: translation = (100, 50, -200) * 0.3048, vertices local relative to LowestCorner.
            var wallNode = nodes[0];
            var wallTrans = wallNode.GetProperty("translation").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            Assert.Equal((float)(100 * MetresPerFoot), wallTrans[0], 4);
            Assert.Equal((float)(50 * MetresPerFoot), wallTrans[1], 4);
            Assert.Equal((float)(-200 * MetresPerFoot), wallTrans[2], 4);

            var wallPrim = glMeshes[wallNode.GetProperty("mesh").GetInt32()].GetProperty("primitives")[0];
            var wallPos = ReadFloatAccessor(root, bin, wallPrim.GetProperty("attributes").GetProperty("POSITION").GetInt32());
            // Revit (2, 3, 0) ft local -> Y-up metres (2*S, 0, -3*S)
            Assert.Equal([0f, 0f, 0f, (float)(2 * MetresPerFoot), 0f, 0f, 0f, 0f, (float)(-3 * MetresPerFoot)],
                wallPos, (a, b) => Math.Abs(a - b) < 1e-5f);

            // Normal (0, 0, +1) in Z-up -> (0, +1, 0) in Y-up.
            var wallNormals = ReadFloatAccessor(root, bin, wallPrim.GetProperty("attributes").GetProperty("NORMAL").GetInt32());
            Assert.Equal([0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f], wallNormals, (a, b) => Math.Abs(a - b) < 1e-5f);

            // 2. Rotated placement node: Revit family vertex (2, 0, 0) ft -> rotated to (10, 22, 30) ft -> Y-up metres (10*S, 30*S, -22*S).
            var doorNode = nodes[1];
            var mat = doorNode.GetProperty("matrix").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            Assert.Equal(1f, Det3x3(mat), 5);
            var doorPrim = glMeshes[doorNode.GetProperty("mesh").GetInt32()].GetProperty("primitives")[0];
            var doorPos = ReadFloatAccessor(root, bin, doorPrim.GetProperty("attributes").GetProperty("POSITION").GetInt32());
            float[] worldV1 = MultiplyPoint(mat, doorPos[3], doorPos[4], doorPos[5]);
            Assert.Equal((float)(10 * MetresPerFoot), worldV1[0], 4);
            Assert.Equal((float)(30 * MetresPerFoot), worldV1[1], 4);
            Assert.Equal((float)(-22 * MetresPerFoot), worldV1[2], 4);
        }
    }

    [Fact]
    public void SharesSymbolMeshAcrossNormalMirroredAndMultiPlacementInstances()
    {
        var doc = SampleDocument();
        var normal = new MeshTransform(new(1, 2, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1));
        var mirrored = new MeshTransform(new(10, 20, 0), new(-1, 0, 0), new(0, 1, 0), new(0, 0, 1));
        var rotated = new MeshTransform(new(0, 5, 1), new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1));

        var meshes = new MeshSet
        {
            Symbols = new Dictionary<int, IReadOnlyList<TriangleMesh>>
            {
                [SymbolId] = [RightTriangle(0, 0, 0, ConcreteMatId)],
            },
            Elements =
            [
                new ElementMesh { ElementId = DoorAId, Placements = [new(SymbolId, normal)], Coverage = MeshCoverage.Complete },
                new ElementMesh { ElementId = DoorBId, Placements = [new(SymbolId, mirrored)], Coverage = MeshCoverage.Complete },
                new ElementMesh { ElementId = MultiPlacementId, Placements = [new(SymbolId, normal), new(SymbolId, rotated)], Coverage = MeshCoverage.Complete },
            ],
        };

        byte[] glb = GlbExporter.ExportToBytes(doc, out var result, meshes);
        Assert.Equal(3, result.ElementCount);
        Assert.Equal(1, result.MeshCount);
        Assert.Equal(1, result.SharedSymbolMeshCount);
        Assert.Equal(4, result.PlacementCount);
        Assert.Equal(1, result.MaterialCount);

        var (json, bin) = ParseGlb(glb);
        using (json)
        {
            var root = json.RootElement;
            Assert.Single(root.GetProperty("meshes").EnumerateArray());
            var nodes = root.GetProperty("nodes");

            // Normal placement node & mirrored placement node share mesh index 0.
            Assert.Equal(0, nodes[0].GetProperty("mesh").GetInt32());
            Assert.Equal(0, nodes[1].GetProperty("mesh").GetInt32());

            // Mirrored transform keeps negative determinant (-1) so glTF 2.0 reverses winding automatically.
            var mirrorMat = nodes[1].GetProperty("matrix").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            Assert.Equal(-1f, Det3x3(mirrorMat), 5);
            // Vertex (2, 0, 0) ft in family -> (10 - 2, 20, 0) ft -> (8*S, 0, -20*S) in glTF.
            var pos = ReadFloatAccessor(root, bin, root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32());
            float[] mirroredWorldV1 = MultiplyPoint(mirrorMat, pos[3], pos[4], pos[5]);
            Assert.Equal((float)(8 * MetresPerFoot), mirroredWorldV1[0], 4);
            Assert.Equal(0f, mirroredWorldV1[1], 4);
            Assert.Equal((float)(-20 * MetresPerFoot), mirroredWorldV1[2], 4);

            // Multi-placement parent node has 2 child nodes, both referencing shared mesh 0.
            var child0 = nodes[nodes[4].GetProperty("children")[0].GetInt32()];
            var child1 = nodes[nodes[4].GetProperty("children")[1].GetInt32()];
            Assert.Equal(0, child0.GetProperty("mesh").GetInt32());
            Assert.Equal(0, child1.GetProperty("mesh").GetInt32());
            Assert.Equal(MultiPlacementId, child0.GetProperty("extras").GetProperty("elementId").GetInt32());
            Assert.Equal(MultiPlacementId, nodes[4].GetProperty("extras").GetProperty("elementId").GetInt32());
        }
    }

    [Fact]
    public void NonRigidClippedPlacement_ViaElementMeshes_IsNotSharedWhenClippedOrScaled()
    {
        var state = new ParsingState();
        state.GrepBlocks[SymbolId] = new GRepData
        {
            BoundingBox = [0, 0, 0, 2, 2, 2],
            Faces =
            [
                new GRepFace
                {
                    Vertices = [[0, 0, 0], [2, 0, 0], [0, 2, 0]],
                    Origin = [0, 0, 0],
                    XVector = [0, 0, 1],
                    GeometryNode = 1,
                },
            ],
        };
        // Non-uniform scale transform (basisX scaled by 2) forces ElementMeshes to fall back to own triangles.
        state.GrepBlocks[1] = new GRepData
        {
            BoundingBox = [0, 0, 0, 4, 2, 2],
            Instances = [new GRepInstance { SymbolElemId = SymbolId, Transform = [2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0] }],
        };
        var info = new RevitFileInfo();
        ElementBuilder.BuildBlockOnlyElement(SymbolId, state, info);
        ElementBuilder.BuildBlockOnlyElement(1, state, info);
        var inst = info.Elements.Single(e => e.Id == 1);
        inst.IsBlockOnly = false;
        inst.CategoryId = -2000151;
        inst.Category = "OST_GenericModel";
        inst.SerializedCreatedPhaseId = Phase;
        inst.Parameters.Add(new Parameter { Id = -1012100, Value = Phase });

        var doc = RvtDocument.FromInfo(info);
        byte[] glb = GlbExporter.ExportToBytes(doc, out var result);
        Assert.Equal(1, result.ElementCount);
        Assert.Equal(1, result.MeshCount);
        Assert.Equal(0, result.SharedSymbolMeshCount);
        Assert.Equal(0, result.PlacementCount);
        using var parsed = ParseGlb(glb).Json;
        Assert.Single(parsed.RootElement.GetProperty("nodes").EnumerateArray());
    }

    [Fact]
    public void EmitsLinearMaterialsTransparencyAndElementMaterialExtras_IncludingNoGeometryNodes()
    {
        var doc = SampleDocument();
        var meshes = new MeshSet
        {
            Elements =
            [
                new ElementMesh
                {
                    ElementId = WallId,
                    Meshes = [RightTriangle(0, 0, 0, ConcreteMatId), RightTriangle(1, 0, 0, GlassMatId), RightTriangle(2, 0, 0, 0)],
                    Coverage = MeshCoverage.Partial,
                    UntriangulatedFaces = 1,
                },
                new ElementMesh { ElementId = NoGeomId, Coverage = MeshCoverage.NoGeometry },
            ],
        };

        byte[] glb = GlbExporter.ExportToBytes(doc, out var result, meshes);
        Assert.Equal(2, result.ElementCount);
        Assert.Equal(1, result.ElementsWithGeometry);
        Assert.Equal(3, result.MaterialCount);

        var (json, _) = ParseGlb(glb);
        using (json)
        {
            var root = json.RootElement;
            var materials = root.GetProperty("materials");

            // Concrete (opaque, file color, no alphaMode)
            var concrete = materials[0];
            Assert.Equal("Cast-in-Place Concrete", concrete.GetProperty("name").GetString());
            Assert.False(concrete.TryGetProperty("alphaMode", out _));
            var cBase = concrete.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            Assert.Equal(1f, cBase[3], 5);
            Assert.Equal(ConcreteMatId, concrete.GetProperty("extras").GetProperty("materialId").GetInt32());
            Assert.Equal("file", concrete.GetProperty("extras").GetProperty("colorSource").GetString());
            Assert.Equal("default", concrete.GetProperty("extras").GetProperty("alphaSource").GetString());

            // Glass (65% transparent -> alpha 0.35, BLEND, doubleSided)
            var glass = materials[1];
            Assert.Equal("BLEND", glass.GetProperty("alphaMode").GetString());
            Assert.True(glass.GetProperty("doubleSided").GetBoolean());
            var gBase = glass.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            Assert.Equal(0.35f, gBase[3], 4);
            Assert.Equal(GlassMatId, glass.GetProperty("extras").GetProperty("materialId").GetInt32());
            Assert.Equal("file", glass.GetProperty("extras").GetProperty("alphaSource").GetString());
            Assert.Equal(65, glass.GetProperty("extras").GetProperty("transparencyPercent").GetInt32());

            // Node extras for Wall (identity + IFC_GUID + materialIds + type metadata)
            var nodes = root.GetProperty("nodes");
            var wallExtras = nodes[0].GetProperty("extras");
            Assert.Equal(WallId, wallExtras.GetProperty("elementId").GetInt32());
            Assert.Equal($"d45b1816-cbcc-4dc6-b0bb-007467f3c600-{WallId:x8}", wallExtras.GetProperty("uniqueId").GetString());
            Assert.Equal("3KMnWMoynDnh2x07HdzyEW", wallExtras.GetProperty("ifcGuid").GetString());
            Assert.Equal("Walls", wallExtras.GetProperty("category").GetString());
            Assert.Equal(TypeId, wallExtras.GetProperty("typeId").GetInt32());
            Assert.Equal("Standard 200", wallExtras.GetProperty("typeName").GetString());
            Assert.Equal([ConcreteMatId, GlassMatId], wallExtras.GetProperty("materialIds").EnumerateArray().Select(x => x.GetInt32()).ToArray());

            // Geometry-less node preserved without mesh property
            var emptyNode = nodes[1];
            Assert.False(emptyNode.TryGetProperty("mesh", out _));
            Assert.Equal(NoGeomId, emptyNode.GetProperty("extras").GetProperty("elementId").GetInt32());
            Assert.Equal("NoGeometry", emptyNode.GetProperty("extras").GetProperty("coverage").GetString());
            Assert.False(string.IsNullOrEmpty(emptyNode.GetProperty("extras").GetProperty("ifcGuid").GetString()));
        }

        // With IncludeElementsWithoutGeometry = false, geometry-less node is dropped cleanly.
        byte[] geomOnlyGlb = GlbExporter.ExportToBytes(doc, out var geomOnlyResult, meshes, new GlbExportOptions
        {
            IncludeElementsWithoutGeometry = false,
        });
        Assert.Equal(1, geomOnlyResult.ElementCount);
        using var geomOnlyJson = ParseGlb(geomOnlyGlb).Json;
        Assert.Single(geomOnlyJson.RootElement.GetProperty("nodes").EnumerateArray());

        // When no elements have any geometry and all are empty, BIN chunk and empty arrays are omitted cleanly.
        byte[] emptyOnly = GlbExporter.ExportToBytes(doc, new MeshSet
        {
            Elements = [new ElementMesh { ElementId = NoGeomId, Coverage = MeshCoverage.NoGeometry }],
        });
        var (emptyJson, emptyBin) = ParseGlb(emptyOnly);
        using (emptyJson)
        {
            Assert.Empty(emptyBin);
            Assert.False(emptyJson.RootElement.TryGetProperty("buffers", out _));
            Assert.False(emptyJson.RootElement.TryGetProperty("meshes", out _));
            Assert.Single(emptyJson.RootElement.GetProperty("nodes").EnumerateArray());
        }
    }

    [Fact]
    public void LargeVertexMesh_UsesUnsignedIntIndices()
    {
        // >= 65535 vertices forces UNSIGNED_INT (5125) to avoid the 0xFFFF primitive restart value.
        const int vertexCount = 65535;
        double[] positions = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            positions[3 * i] = i * 0.01;
            positions[3 * i + 1] = i % 3;
        }
        var meshes = new MeshSet
        {
            Elements =
            [
                new ElementMesh
                {
                    ElementId = WallId,
                    Meshes = [new TriangleMesh { Positions = positions, Indices = [0, 1, 65534], MaterialId = 0 }],
                    Coverage = MeshCoverage.Complete,
                },
            ],
        };

        byte[] glb = GlbExporter.ExportToBytes(SampleDocument(), meshes);
        var (json, _) = ParseGlb(glb);
        using (json)
        {
            int idxAcc = json.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("indices").GetInt32();
            Assert.Equal(5125, json.RootElement.GetProperty("accessors")[idxAcc].GetProperty("componentType").GetInt32());
        }
    }

    [Fact]
    public void CliExportsGlbAndCombinedFormats_WithSingleFileRead()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rvtio-glb-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string rvt = Path.Combine(dir, "input.rvt");
            CliExitCodeTests.WriteSyntheticRvt(rvt);

            string glb = Path.Combine(dir, "out.glb");
            string ifc = Path.Combine(dir, "out.ifc");
            string scene = Path.Combine(dir, "out.scene.json");
            string compact = Path.Combine(dir, "out.compact.json");
            string report = Path.Combine(dir, "report.json");

            // Combined export: model must be read exactly once.
            var (exit, stderr) = RunCli(rvt, "--glb", glb, "--ifc", ifc, "--scene", scene, "--compact-json", compact, "--report", report);
            Assert.Equal(RvtExitCodes.Success, exit);
            Assert.True(File.Exists(glb));
            Assert.True(File.Exists(ifc));
            Assert.True(File.Exists(scene));
            Assert.True(File.Exists(compact));
            Assert.True(File.Exists(report));

            int readTimings = stderr.Split('\n').Count(l => l.Contains("Timing: read ", StringComparison.Ordinal));
            Assert.Equal(1, readTimings);

            using (var parsed = ParseGlb(File.ReadAllBytes(glb)).Json)
                Assert.Equal("2.0", parsed.RootElement.GetProperty("asset").GetProperty("version").GetString());

            // Positional .glb path exports GLB cleanly too.
            string posGlb = Path.Combine(dir, "positional.glb");
            var (posExit, posErr) = RunCli(rvt, posGlb);
            Assert.Equal(RvtExitCodes.Success, posExit);
            Assert.Equal(File.ReadAllBytes(glb), File.ReadAllBytes(posGlb));
            Assert.Contains("Timing: read ", posErr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public void SampleRvtFilesFromTestRoots_ExportValidGlbAndSingleReadMultiFormat()
    {
        string? firstRvt = TestFiles.Roots()
            .SelectMany(root => Directory.EnumerateFiles(root, "*.rvt", SearchOption.AllDirectories))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (firstRvt is null)
            throw new SkipException("No .rvt fixture files found under RVTIO_TEST_FILES.");

        string dir = Path.Combine(Path.GetTempPath(), "rvtio-glb-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string glb = Path.Combine(dir, "sample.glb");
            string ifc = Path.Combine(dir, "sample.ifc");
            var (exit, stderr) = RunCli(firstRvt, "--glb", glb, "--ifc", ifc);
            Assert.Equal(RvtExitCodes.Success, exit);
            Assert.Equal(1, stderr.Split('\n').Count(l => l.Contains("Timing: read ", StringComparison.Ordinal)));

            var result = GlbExporter.Export(firstRvt, Path.Combine(dir, "direct.glb"));
            Assert.True(result.ElementCount > 0);
            Assert.True(result.MeshCount > 0);
            Assert.Equal(File.ReadAllBytes(glb), File.ReadAllBytes(Path.Combine(dir, "direct.glb")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static (int ExitCode, string StdErr) RunCli(params string[] arguments)
    {
        var start = new ProcessStartInfo(CliExitCodeTests.DotnetExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RvtIO.Cli.dll"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "rvtio CLI timed out.");
        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stderr.Result);
    }
}
