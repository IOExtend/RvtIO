using System.Globalization;
using IOScene;
using RvtIO.BuiltIns;
using RvtIO.Meshes;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The scene of <see cref="RvtSceneReader"/> on synthetic meshes and elements: instancing, mirrored placements, local
/// origins, feet to metres, element facts, IFC GlobalIds, property sets and their display text, materials, the diagnostics of
/// incomplete geometry and those of a damaged read.</summary>
public class RvtSceneReaderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SceneNameCandidatesKeepElementAndParameterNamePriorities(bool keepExportProvenance)
    {
        var state = new RvtIO.Internal.ParsingState { KeepExportProvenance = keepExportProvenance };
        state.ElementNameTable[1] = "Table name";
        state.NativeElementNames[1] = "Native name";
        state.NativeElementNames[2] = "Native name";
        state.WideNames[2] = "Wide name";
        state.WideNames[3] = "Wide name";
        state.WideNames[4] = " ";
        state.ElemStrings[1] = ["Block name", "&Block caption"];
        state.ElemStrings[4] = ["1234", "First block name", "Later block name"];
        state.ElemStrings[5] = ["1234", "autodesk.parameter.key"];
        state.ElemStrings[6] = ["&First caption", "&Later caption"];
        state.ElemScanStrings[4] = ["Scan name"];
        state.ElemScanStrings[5] = ["1234", "First scan name", "Later scan name"];
        state.ElemScanStrings[7] = ["&Scan caption"];
        state.ForgeParamDefs[1] = ("autodesk.parameter.key", "Forge caption");
        state.ForgeParamDefs[6] = ("autodesk.parameter.other", null);

        ElementBuilder.CompactSceneNameCandidates(state);

        Assert.Equal("Table name", ElementBuilder.ResolveElementName(1, state));
        Assert.Equal("Native name", ElementBuilder.ResolveElementName(2, state));
        Assert.Equal("Wide name", ElementBuilder.ResolveElementName(3, state));
        Assert.Equal("First block name", ElementBuilder.ResolveElementName(4, state));
        Assert.Equal("First scan name", ElementBuilder.ResolveElementName(5, state));
        Assert.Null(ElementBuilder.ResolveElementName(8, state));
        var captions = ElementBuilder.CollectParameterDefinitionNames(state);
        Assert.Equal(2, captions.Count);
        Assert.Equal("Forge caption", captions[1]);
        Assert.Equal("First caption", captions[6]);
        Assert.Equal(keepExportProvenance ? 4 : 0, state.ElemStrings.Count);
        Assert.Equal(keepExportProvenance ? 3 : 0, state.ElemScanStrings.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeFactsAreOptionalWithoutChangingSceneParameterValues(bool keepExportProvenance)
    {
        const int parameterId = -1001105;
        var state = new RvtIO.Internal.ParsingState { KeepExportProvenance = keepExportProvenance };
        var native = new RvtIO.Parsing.TfBlockDeserializer.NativeParams { RootParameterStoresComplete = true };
        native.Doubles.Add((parameterId, 17.25));
        state.NativeParams[1] = native;
        var element = new Element { Id = 1 };
        var data = new RevitFileInfo { Elements = [element] };

        RvtIO.Parsing.ElementBuilder.ApplyNativeParams(data, state);

        var value = Assert.Single(element.Parameters, parameter => parameter.Id == parameterId);
        Assert.Equal(17.25, value.Value);
        Assert.Equal(keepExportProvenance, element.NativeStoredParameterIds?.Contains(parameterId) == true);
        Assert.Equal(keepExportProvenance, element.NativeParameterStoreIds?.Contains(parameterId) == true);
    }

    private const double Foot = 0.3048;
    private const int Wall = 100, Door = 101, Empty = 102, Broken = 103, Type = 200, Symbol = 300, Concrete = 500;

    private static Parameter Bip(BuiltInParameter bip, object value, StorageType? storage = null) => new()
    {
        Id = (int)bip, Value = value, StorageType = storage ?? StorageOf(value),
        Definition = new Definition { Id = (int)bip, BuiltInParameter = bip.ToString(), Name = bip.ToString() },
    };

    private static Parameter User(int id, string name, object value, StorageType? storage = null) => new()
    {
        Id = id, Value = value, StorageType = storage ?? StorageOf(value), Definition = new Definition { Id = id, Name = name },
    };

    private static StorageType StorageOf(object value) => value switch
    {
        double => StorageType.Double, string => StorageType.String, _ => StorageType.Integer,
    };

    /// <summary>A user length in feet, displayed in millimetres as the reader resolves a parameter with a length spec.</summary>
    private static Parameter Length(int id, string name, double feet)
    {
        var parameter = User(id, name, feet);
        parameter.SpecType = "autodesk.spec.aec:length-2.0.0";
        parameter.UnitSymbol = "mm";
        parameter.ConversionFactor = 304.8;
        return parameter;
    }

    /// <summary>A built-in double as the reader resolves it: no spec, the unit guessed from the name (mm for a name with WIDTH,
    /// HEIGHT, OFFSET, ...).</summary>
    private static Parameter GuessedLength(BuiltInParameter bip, double value)
    {
        var parameter = Bip(bip, value);
        parameter.UnitSymbol = "mm";
        parameter.ConversionFactor = 304.8;
        return parameter;
    }

    private static Element Instance(int id, int categoryId, string category, params Parameter[] parameters) => new()
    {
        Id = id, UniqueId = $"doc-{id:x8}", CategoryId = categoryId, Category = category,
        Parameters = [Bip(BuiltInParameter.ELEM_TYPE_PARAM, Type, StorageType.ElementId), .. parameters],
    };

    private static RvtDocument Document() => RvtDocument.FromInfo(new RevitFileInfo
    {
        Elements =
        [
            Instance(Wall, (int)BuiltInCategory.OST_Walls, "OST_Walls", Length(910, "Clear height", 10.5),
                User(900, "Fire rating", "EI 60"), Bip(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Outer leaf")),
            Instance(Door, (int)BuiltInCategory.OST_Doors, "OST_Doors"),
            Instance(Empty, (int)BuiltInCategory.OST_Walls, "OST_Walls"),
            Instance(Broken, (int)BuiltInCategory.OST_Walls, "OST_Walls"),
            new Element { Id = Type, Name = "Generic 200", Parameters = [User(901, "Count", 3)] },
            new Element
            {
                Id = Concrete, Name = "Concrete", CategoryId = (int)BuiltInCategory.OST_Materials, Category = "OST_Materials",
                Parameters = [Bip(BuiltInParameter.MATERIAL_PARAM_COLOR, 0x30_20_10), Bip(BuiltInParameter.MATERIAL_PARAM_TRANSPARENCY, 20)],
            },
        ],
    });

    /// <summary>One triangle at (x, y, z) + (0..1, 0..2, 0), normal +Z.</summary>
    private static TriangleMesh Triangle(double x, double y, double z, int material = 0) => new()
    {
        Positions = [x, y, z, x + 1, y, z, x, y + 2, z],
        Indices = [0, 1, 2],
        Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1],
        MaterialId = material,
    };

    private static readonly MeshTransform Mirrored = new(new(10, 20, 0), new(-1, 0, 0), new(0, 1, 0), new(0, 0, 1));
    private static readonly MeshTransform Moved = new(new(0, 4, 0), new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1));

    private static MeshSet Meshes() => new()
    {
        Symbols = new Dictionary<int, IReadOnlyList<TriangleMesh>> { [Symbol] = [Triangle(0, 0, 0, Concrete)] },
        Elements =
        [
            new ElementMesh { ElementId = Wall, Meshes = [Triangle(1000, 2000, 30), Triangle(1001, 2000, 31, Concrete)], Coverage = MeshCoverage.Complete },
            new ElementMesh { ElementId = Door, Placements = [new(Symbol, Mirrored), new(Symbol, Moved)], Coverage = MeshCoverage.Complete },
            new ElementMesh { ElementId = Empty, Coverage = MeshCoverage.NoGeometry },
            new ElementMesh { ElementId = Broken, Meshes = [Triangle(0, 0, 0)], Coverage = MeshCoverage.Partial, UntriangulatedFaces = 2 },
        ],
    };

    private static RvtSceneResult Map() => RvtSceneReader.Read(Document(), Meshes(), new RvtSceneOptions { ModelKey = "model.rvt" });

    [Fact]
    public void PagedReferencesPreserveOrderAndRestoreTheEditableList()
    {
        using var stream = new MemoryStream();
        var scratch = new SceneMeshScratch(stream);
        var element = new Element();
        var source = Enumerable.Range(0, 17_000).Select(i => i % 1037).ToArray();
        element.SetReferences(source, editable: false);
        scratch.ArchiveGeometry(element);

        var stored = element.ReadReferences;
        Assert.NotNull(stored);
        Assert.IsNotType<int[]>(stored);
        Assert.Equal(source, stored);
        Assert.Equal(source[16_384], stored[16_384]);
        var editable = element.References;
        Assert.NotNull(editable);
        editable[16_384] = 42;
        editable.RemoveAt(0);
        Assert.Same(editable, element.References);
        Assert.Equal(16_999, element.ReadReferences!.Count);
        Assert.Equal(42, element.ReadReferences[16_383]);
        Assert.Equal(source[16_384], stored[16_384]);
    }

    [Fact]
    public void ScratchScenePreservesGeometryPropertiesPlacementsAndDiagnostics()
    {
        using var stream = new MemoryStream();
        var scratch = new SceneMeshScratch(stream);
        var meshes = Meshes();
        var stored = new MeshSet
        {
            Symbols = meshes.Symbols.ToDictionary(pair => pair.Key, pair => scratch.Store(pair.Value)),
            Elements = meshes.Elements.Select(element => new ElementMesh
            {
                ElementId = element.ElementId,
                Meshes = scratch.Store(element.Meshes),
                Placements = element.Placements,
                Coverage = element.Coverage,
                UntriangulatedFaces = element.UntriangulatedFaces,
            }).ToArray(),
        };

        var expected = Map();
        var actual = RvtSceneReader.Map(Document(), stored, "model.rvt", scratch: scratch);

        Assert.True(stream.Length > 0);
        Assert.Equal(SceneJson.Write(expected.Document), SceneJson.Write(actual.Document));
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    [Fact]
    public void ScratchSceneArrayReadsAcrossPageBoundariesAndWithInterleavedWrites()
    {
        using var stream = new MemoryStream();
        var scratch = new SceneMeshScratch(stream);
        var values = Enumerable.Range(0, 8000).Select(i => new Vector3d(i, -i, i * .5)).ToArray();
        var stored = scratch.Store(values);
        var later = scratch.Store(new[] { 5, 7 });

        Assert.Equal(values, stored);
        Assert.Equal(values, stored.ReadAll());
        Assert.Equal(values[2730], stored[2730]);
        Assert.Equal(7, later[1]);
        Assert.Equal(values[^1], stored[^1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => stored[values.Length]);
    }

    [Fact]
    public void ResolvedParameterPagesPreserveOrderFlagsAndPropertyValues()
    {
        using var stream = new MemoryStream();
        var scratch = new SceneMeshScratch(stream);
        var reference = new Parameter
        {
            Id = -1012100, Definition = new Definition { Id = -1012100, Name = "Phase", BuiltInParameter = "PHASE_CREATED" },
            Value = 0, StorageType = StorageType.ElementId, NativeElementId = 4294967311, HasResolvedZeroElementId = true,
            FromNativeFields = true, IsReadOnly = true,
        };
        var text = new Parameter
        {
            Id = 1200, Value = "\ud800", StorageType = StorageType.String, Guid = Guid.Empty,
            Definition = new Definition { Id = 1200, Name = "Exact text" },
        };
        var distance = new Parameter
        {
            Id = 1201, Value = 2.25, StorageType = StorageType.Double, ForgeId = "length",
            SpecType = "autodesk.spec.aec:length-2.0.0", UnitSymbol = "mm", ConversionFactor = 304.8,
            IsApproximate = true,
        };
        var missing = new Parameter { Id = 1202, Value = false, StorageType = StorageType.Integer, HasNoRevitValue = true };
        var first = new Element { Parameters = [reference, text, distance, missing], SerializedCreatedPhaseId = 0 };
        var second = new Element { Parameters = [new Parameter { Id = -1002050, Value = 7 }] };
        var original = first.Parameters;
        scratch.ArchiveParameters(first);
        scratch.ArchiveParameters(second);

        var archived = first.ReadParameters;
        Assert.NotSame(original, archived);
        Assert.Equal(original.Select(p => p.Id), archived.Select(p => p.Id));
        Assert.Equal(7, second.ReadParameters[0].Value);
        Assert.Equal(reference.SourceElementId, archived[0].SourceElementId);
        Assert.True(archived[0].HasRevitValue);
        Assert.True(archived[0].FromNativeFields);
        Assert.True(archived[0].IsReadOnly);
        Assert.Equal(reference.Definition.BuiltInParameter, archived[0].Definition.BuiltInParameter);
        Assert.Equal(text.Value, first.LookupParameter("Exact text")!.Value);
        Assert.Equal(Guid.Empty, archived[1].Guid);
        Assert.Equal(distance.AsValueString(), archived[2].AsValueString());
        Assert.Equal(distance.SpecType, archived[2].SpecType);
        Assert.True(archived[2].IsApproximate);
        Assert.False(archived[3].HasRevitValue);
        Assert.Equal(false, archived[3].Value);
        Assert.Contains(7, ProductRules.TypeElementIds([first, second]));
        Assert.True(ProductRules.HasProjectPhaseReference(first));
        Assert.Same(archived, first.ReadParameters);

        long persisted = stream.Length;
        Assert.Equal(4, first.Parameters.Count);
        scratch.ArchiveParameters(first);
        Assert.Equal(persisted, stream.Length);
        Assert.Same(archived, first.ReadParameters);

        first.Parameters.RemoveAt(3);
        Assert.Same(first.Parameters, first.ReadParameters);
        first.Parameters[1].Value = "Resolved later";
        scratch.ArchiveParameters(first);
        Assert.Equal(persisted, stream.Length);
        Assert.Equal(3, first.ReadParameters.Count);
        Assert.Equal("Resolved later", first.ReadParameters[1].Value);
        Assert.Equal(7, second.ReadParameters[0].Value);

        first.Parameters[1].Value = new string('a', 1024);
        scratch.ArchiveParameters(first);
        long grown = stream.Length;
        Assert.True(grown > persisted);
        string replacement = new('b', 1024);
        first.Parameters[1].Value = replacement;
        scratch.ArchiveParameters(first);
        Assert.Equal(grown, stream.Length);
        Assert.Equal(replacement, first.ReadParameters[1].Value);
        Assert.Equal(7, second.ReadParameters[0].Value);
    }

    [Fact]
    public void ParameterPagesRestoreIndependentDefinitions()
    {
        using var stream = new MemoryStream();
        var scratch = new SceneMeshScratch(stream);
        string inlineName = new('x', 513);
        var first = new Element { Parameters = [WithName("Caption"), WithName(inlineName), new() { Definition = null! }] };
        var second = new Element { Parameters = [WithName("Caption")] };
        scratch.ArchiveParameters(first);
        scratch.ArchiveParameters(second);

        var definition = first.Parameters[0].Definition;
        definition.Name = "Edited";
        scratch.ArchiveParameters(first);
        Assert.Equal("Edited", first.ReadParameters[0].DefinitionName);
        Assert.Equal("Caption", second.ReadParameters[0].DefinitionName);
        Assert.Equal("BUILT_IN", second.ReadParameters[0].Definition.BuiltInParameter);
        Assert.Equal(inlineName, first.ReadParameters[1].DefinitionName);
        Assert.Null(first.ReadParameters[2].Definition);

        static Parameter WithName(string name) =>
            new() { Id = -7, Definition = new Definition { Id = -7, Name = name, BuiltInParameter = "BUILT_IN" } };
    }

    [Fact]
    public void FirstModelResolutionPassKeepsTypesAndElevationsOnParameterPages()
    {
        using var stream = new MemoryStream();
        var scratch = new SceneMeshScratch(stream);
        var type = new Element { Id = 5001, Name = "Type", CategoryId = -2000011, Category = "OST_Walls" };
        var instance = new Element
        {
            Id = 5002, CategoryId = -2000011, Category = "OST_Walls", BoundingBox = [0, 0, 12, 1, 1, 13],
            Parameters =
            [
                new() { Id = -1002050, StorageType = StorageType.ElementId, Value = type.Id },
                new() { Id = -1007102, StorageType = StorageType.Double, Value = 10.0 },
            ],
        };
        scratch.ArchiveParameters(instance);
        var info = new RevitFileInfo { Elements = [type, instance] };
        int passes = 0;

        ElementBuilder.PostProcess(info, new RvtIO.Internal.ParsingState
        {
            ArchiveSceneParameters = scratch.ArchiveParameters,
        }, passCompleted: () =>
        {
            if (++passes != 1) return;
            Assert.Equal(type.Id, instance.TypeId);
            Assert.IsNotType<List<Parameter>>(instance.ReadParameters);
            Assert.Equal(2.0, instance.ReadParameters.First(p => p.Id == -1001360).Value);
        });

        Assert.True(passes > 0);
    }

    [Fact]
    public void WorksharingOwnersUseTheEmittedCategoryWithoutRetainingParameterLists()
    {
        using var stream = new MemoryStream();
        var scratch = new SceneMeshScratch(stream);
        var info = new RevitFileInfo
        {
            Elements =
            [
                new() { CategoryId = -2000260, Parameters = [Category(-2000011)] },
                new() { Parameters = [Category(-2000260)] },
                new() { Parameters = [Category(7)] },
                new() { Parameters = [Category(-2000011), new() { Id = -1002067, Value = "Stored owner" }] },
            ],
        };
        foreach (var element in info.Elements) scratch.ArchiveParameters(element);

        ElementBuilder.ApplyEditedByCategoryRules(info, new RvtIO.Internal.ParsingState
        {
            LocalUsername = "Local owner",
            ArchiveSceneParameters = scratch.ArchiveParameters,
        });

        Assert.Equal("Local owner", Owner(info.Elements[0]));
        Assert.Equal("", Owner(info.Elements[1]));
        Assert.Null(Owner(info.Elements[2]));
        Assert.Equal("Stored owner", Owner(info.Elements[3]));
        Assert.All(info.Elements, element => Assert.IsNotType<List<Parameter>>(element.ReadParameters));

        static Parameter Category(int value) => new() { Id = -1140362, Value = value };
        static object? Owner(Element element) => element.ReadParameters.FirstOrDefault(p => p.Id == -1002067)?.Value;
    }

    [Fact]
    public void StoredPhasesAreUpdatedAndRearchivedOneElementAtATime()
    {
        using var stream = new MemoryStream();
        var scratch = new SceneMeshScratch(stream);
        var info = new RevitFileInfo
        {
            Elements =
            [
                new() { Id = 1, SerializedCreatedPhaseId = 7, Parameters = [ExistingPhase(-1)] },
                new() { Id = 2, SerializedCreatedPhaseId = 8, Parameters = [ExistingPhase(3)] },
            ],
        };
        foreach (var element in info.Elements) scratch.ArchiveParameters(element);

        var edited = new List<int>();
        ElementBuilder.ApplyStoredPhases(info, new Dictionary<uint, ushort> { [7] = 3065 },
            archiveParameters: element =>
            {
                scratch.ArchiveParameters(element);
                edited.Add(element.Id);
            });

        Assert.Equal([1, 2], edited);
        Assert.Equal(7, info.Elements[0].ReadParameters[0].Value);
        Assert.Equal(3, info.Elements[1].ReadParameters[0].Value);
        Assert.All(info.Elements, element => Assert.IsNotType<List<Parameter>>(element.ReadParameters));

        static Parameter ExistingPhase(int value) =>
            new() { Id = -1012100, StorageType = StorageType.ElementId, Value = value };
    }

    [Fact]
    public void ExpandedFacePagesPreserveFaceOrderAndRestoreMutableLists()
    {
        using var stream = new MemoryStream();
        var scratch = new SceneMeshScratch(stream);
        var faces = Enumerable.Range(0, 1024).Select(i => new GeoFace
        {
            Origin = [i, 0, 0],
            OuterVertices = [[i, 0, 0], [i + 1, 0, 0], [i, 1, 0]],
            InnerVertices = i == 1 ? [[[1.1, .1, 0], [1.2, .1, 0], [1.1, .2, 0]]] : null,
            Normal = [0, 0, 1],
            GeometryNode = i % 2 == 0 ? 4 : -7,
            RenderStyleId = i,
            MeshArcs = i == 1 ? [new([1, 2, 3], [1, 0, 0], [0, 1, 0], .5, .25, .75, 6)] : null,
            GInfoFlags = i == 0 ? 0x200u : null,
            EdgeIndices = i == 0 ? [0, 2, 1] : null,
        }).ToList();
        var geometry = new GeometryData { Faces = faces };
        scratch.ArchiveGeometry(new Element { Geometry = geometry });

        Assert.True(stream.Length > 64 * 1024);
        Assert.NotSame(faces, geometry.ReadFaces);
        Assert.Equal(faces.Select(face => face.RenderStyleId), geometry.ReadFaces.Select(face => face.RenderStyleId));
        Assert.Equal(faces[1000].OuterVertices![2], geometry.ReadFaces[1000].OuterVertices![2]);
        Assert.Equal(faces[1].InnerVertices![0][1], geometry.ReadFaces[1].InnerVertices![0][1]);
        Assert.Equal(6u, Assert.Single(geometry.ReadFaces[1].MeshArcs!).Edge);
        Assert.Equal(0x200u, geometry.ReadFaces[0].GInfoFlags);
        Assert.Equal(faces[0].EdgeIndices, geometry.ReadFaces[0].EdgeIndices);

        var restored = geometry.Faces;
        restored[0].GeometryNode = 42;
        restored.RemoveAt(restored.Count - 1);
        Assert.Same(restored, geometry.Faces);
        Assert.Equal(1023, geometry.ReadFaces.Count);
        Assert.Equal(42, geometry.ReadFaces[0].GeometryNode);
    }

    [Fact]
    public void SceneReaderRequiresSeparateTriangleScratch()
    {
        using var stream = new MemoryStream();
        var options = new RvtSceneOptions { ModelKey = "model.rvt" };
        Assert.Throws<ArgumentException>(() => RvtSceneReader.ReadStream(stream, options, stream));
        using var nonempty = new MemoryStream([7]);
        Assert.Throws<ArgumentException>(() => RvtSceneReader.ReadStream(stream, options, nonempty));
        Assert.Empty(stream.ToArray());
    }

    /// <summary>The scene of one drawable element.</summary>
    private static SceneDocument MapOne(Element element) => RvtSceneReader.Read(RvtDocument.FromInfo(new RevitFileInfo { Elements = [element] }),
        new MeshSet { Elements = [new ElementMesh { ElementId = element.Id, Meshes = [Triangle(0, 0, 0)] }] }, new RvtSceneOptions { ModelKey = "m" }).Document;

    /// <summary>The text of each property of the scene, by property name.</summary>
    private static Dictionary<string, string> Texts(SceneDocument document) =>
        document.PropertySets.SelectMany(set => set.Properties).ToDictionary(property => property.Name, property => property.Value.Text);

    private static void AssertClose(Vector3d expected, Vector3d actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
        Assert.Equal(expected.Z, actual.Z, 9);
    }

    [Fact]
    public void ScenePassesValidation()
    {
        var result = Map();
        Assert.Empty(result.Document.Grids);
        Assert.Empty(result.Document.Levels);
        Assert.All(result.Document.Elements, element => Assert.Null(element.LevelId));
        Assert.Empty(result.Document.Validate());
        Assert.Empty(SceneJson.Read(SceneJson.Write(result.Document)).Validate());
    }

    [Fact]
    public void ModelCarriesKeyFormatUnitAndProducer()
    {
        var model = Assert.Single(Map().Document.Models);
        Assert.Equal("model.rvt", model.Key);
        Assert.Equal("rvt", model.SourceFormat);
        Assert.Equal("ft", model.SourceLengthUnit);
        Assert.NotNull(model.Producer);
        Assert.StartsWith("RvtIO.Scene ", model.Producer);
        Assert.DoesNotContain('+', model.Producer);
    }

    [Fact]
    public void SceneExportsDecodedStraightGridAxesWithoutMeshes()
    {
        int category = (int)BuiltInCategory.OST_Grids;
        var b = Axis(22, "Б", [4, -2, 3], [4, 8, 3]);
        var a = Axis(11, "A", [-5, 1, 2], [5, 1, 2]);
        var arc = Axis(30, "Arc", [1, 0, 0], [0, 1, 0]);
        arc.Location!.CurveType = "Arc";
        var wall = Axis(40, "Wall", [0, 0, 0], [5, 0, 0]);
        wall.CategoryId = (int)BuiltInCategory.OST_Walls;
        var info = new RevitFileInfo
        {
            Elements =
            [
                b, a, arc, wall,
                new Element { Id = 50, CategoryId = category },
                Axis(60, "Invalid", [0, 0, 0], [double.NaN, 1, 2]),
                Axis(70, "Short point", [0, 0], [4, 0, 0]),
                Axis(80, "Collapsed", [2, 2, 2], [2, 2, 2]),
            ],
        };
        var result = RvtSceneReader.Read(RvtDocument.FromInfo(info), new MeshSet(), new RvtSceneOptions { ModelKey = "model.rvt" });
        var scene = SceneJson.Read(SceneJson.Write(result.Document));

        Assert.Empty(scene.Validate());
        Assert.Empty(scene.Elements);
        var grid = Assert.Single(scene.Grids);
        Assert.Equal(Assert.Single(scene.Models).Id, grid.ModelId);
        Assert.Equal("Grids", grid.Name);
        Assert.Collection(grid.Lines,
            line => CheckAxis(line, "A", new(-5, 1, 2), new(5, 1, 2)),
            line => CheckAxis(line, "Б", new(4, -2, 3), new(4, 8, 3)));

        Element Axis(int id, string name, double[] start, double[] end) => new()
        {
            Id = id, Name = name, CategoryId = category,
            Location = new LocationCurve { StartPoint = start, EndPoint = end },
        };

        static void CheckAxis(SceneGridLine line, string name, Vector3d start, Vector3d end)
        {
            Assert.Equal(name, line.Name);
            AssertClose(start * Foot, line.Start);
            AssertClose(end * Foot, line.End);
            Assert.Equal(start.Z * Foot, line.Elevation, 9);
        }
    }

    [Fact]
    public void SceneExportsProjectStoreysAndAssignsElementsWithTheCurrentIfcRules()
    {
        var ground = Level(10, "Ground", 0);
        var upper = Level(20, "Upper", 12);
        var landing = Level(15, "Landing", 6, story: false);
        var below = Level(5, "Below the building", -3, story: false);
        var family = Level(30, "Family reference", 12);
        family.SerializedFamilyId = 900;

        var wall = Instance(Wall, (int)BuiltInCategory.OST_Walls, "OST_Walls",
            Bip(BuiltInParameter.WALL_BASE_CONSTRAINT, upper.Id, StorageType.ElementId),
            Bip(BuiltInParameter.LEVEL_PARAM, ground.Id, StorageType.ElementId));
        var onLanding = Instance(Door, (int)BuiltInCategory.OST_Doors, "OST_Doors",
            Bip(BuiltInParameter.LEVEL_PARAM, landing.Id, StorageType.ElementId));
        var withoutLevel = Instance(Empty, (int)BuiltInCategory.OST_Doors, "OST_Doors");
        withoutLevel.BoundingBox = [0, 0, 11.9, 2, 2, 18];
        var onBelow = Instance(Broken, (int)BuiltInCategory.OST_Doors, "OST_Doors",
            Bip(BuiltInParameter.LEVEL_PARAM, below.Id, StorageType.ElementId));

        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [wall, onLanding, withoutLevel, onBelow, upper, family, below, ground, landing],
        });
        var catalog = document.GetBuildingStoreyCatalog();
        Assert.Equal(new[] { ground.Id, upper.Id }, catalog.Storeys.Select(level => level.Id));
        Assert.Null(catalog.GetStoreyId(999));

        var result = RvtSceneReader.Read(document, Meshes(), new RvtSceneOptions { ModelKey = "model.rvt" });
        var scene = SceneJson.Read(SceneJson.Write(result.Document));
        Assert.Empty(scene.Validate());
        int modelId = Assert.Single(scene.Models).Id;
        Assert.Collection(scene.Levels,
            level => CheckLevel(level, ground.Id, "Ground", 0, modelId),
            level => CheckLevel(level, upper.Id, "Upper", 12 * Foot, modelId));
        var elements = scene.Elements.ToDictionary(element => element.Id);
        Assert.Equal(upper.Id, elements[Wall].LevelId);
        Assert.Equal(ground.Id, elements[Door].LevelId);
        Assert.Equal(upper.Id, elements[Empty].LevelId);
        Assert.Null(elements[Broken].LevelId);

        static Element Level(int id, string name, double elevation, bool story = true) => new()
        {
            Id = id, Name = name, CategoryId = (int)BuiltInCategory.OST_Levels,
            IsNativeLevel = true, SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
            Parameters = [Bip(BuiltInParameter.LEVEL_ELEV, elevation), Bip(BuiltInParameter.LEVEL_IS_BUILDING_STORY, story ? 1 : 0)],
        };

        static void CheckLevel(SceneLevel level, int id, string name, double elevation, int modelId)
        {
            Assert.Equal(id, level.Id);
            Assert.Equal(modelId, level.ModelId);
            Assert.Equal(name, level.Name);
            Assert.Equal(elevation, level.Elevation!.Value, 9);
        }
    }

    [Fact]
    public void SymbolIsOneGeometryPlacedOncePerPlacement()
    {
        var result = Map();
        var door = result.Document.Instances.Where(instance => instance.ElementId == Door).ToList();
        Assert.Equal(2, door.Count);
        Assert.Single(door.Select(instance => instance.GeometryId).Distinct());
        Assert.Equal(2, result.PlacementCount);
        // the symbol (1) and the three own meshes (two of the wall, one of the broken element)
        Assert.Equal(4, result.GeometryCount);
        Assert.Equal(5, result.InstanceCount);
        Assert.All(door, instance => Assert.Equal(Concrete, instance.MaterialId));
    }

    [Fact]
    public void MirroredPlacementKeepsItsTransformAndTheWinding()
    {
        var document = Map().Document;
        var mirrored = document.Instances.First(instance => instance.ElementId == Door);
        Assert.Equal(new Transform3d(new(-1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(10 * Foot, 20 * Foot, 0)), mirrored.Transform);
        var symbol = document.Geometries.Single(geometry => geometry.Id == mirrored.GeometryId);
        Assert.Equal([0, 1, 2], symbol.Indices);
        Assert.Equal([new(0, 0, 0), new(Foot, 0, 0), new(0, 2 * Foot, 0)], symbol.Vertices);
        Assert.Equal([Vector3d.UnitZ, Vector3d.UnitZ, Vector3d.UnitZ], symbol.Normals!);
    }

    [Fact]
    public void PlacementTransformsSymbolPointsInMetres()
    {
        var document = Map().Document;
        var moved = document.Instances.Where(instance => instance.ElementId == Door).ElementAt(1);
        var symbol = document.Geometries.Single(geometry => geometry.Id == moved.GeometryId);
        // (1, 0, 0) ft of the family maps to the origin (0, 4, 0) + BasisX (0, 1, 0) = (0, 5, 0) ft
        AssertClose(new(0, 5 * Foot, 0), moved.Transform!.Value.Apply(symbol.Vertices[1]));
    }

    [Fact]
    public void OwnMeshesAreLocalToTheirLowestCornerInMetres()
    {
        var document = Map().Document;
        var wall = document.Instances.Where(instance => instance.ElementId == Wall).ToList();
        Assert.Equal(2, wall.Count);
        Assert.All(wall, instance => Assert.Equal(Transform3d.Translate(new(1000 * Foot, 2000 * Foot, 30 * Foot)), instance.Transform));
        var first = document.Geometries.Single(geometry => geometry.Id == wall[0].GeometryId);
        Assert.Equal([new(0, 0, 0), new(Foot, 0, 0), new(0, 2 * Foot, 0)], first.Vertices);
        var second = document.Geometries.Single(geometry => geometry.Id == wall[1].GeometryId);
        AssertClose(new(1002 * Foot, 2000 * Foot, 31 * Foot), wall[1].Transform!.Value.Apply(second.Vertices[1]));
        Assert.Null(wall[0].MaterialId);
        Assert.Equal(Concrete, wall[1].MaterialId);
    }

    [Fact]
    public void ElementsCarryIdentityNamesAndCategories()
    {
        var elements = Map().Document.Elements;
        Assert.Equal([Wall, Door, Empty, Broken], elements.Select(element => element.Id));
        var wall = elements[0];
        Assert.Equal("doc-00000064", wall.SourceId);
        Assert.Equal(1, wall.ModelId);
        Assert.Equal("Walls", wall.Category);
        Assert.Equal("Doors", elements[1].Category);
        Assert.Equal("Generic 200", wall.TypeName);
        Assert.Null(wall.LevelId);
        Assert.Null(wall.IfcGuid);
    }

    [Fact]
    public void ParametersAreTextPropertySetsWithRevitKeys()
    {
        var document = Map().Document;
        var wall = document.Elements[0];
        var door = document.Elements[1];
        Assert.Equal(2, wall.PropertySetIds.Count);
        var instance = document.PropertySets.Single(set => set.Id == wall.PropertySetIds[0]);
        Assert.Equal("revit:instance", instance.Key);
        var comments = instance.Properties.Single(property => property.Name == "ALL_MODEL_INSTANCE_COMMENTS");
        Assert.Equal("revit:bip:-1010106", comments.Key);
        Assert.Equal(SceneValue.FromText("Outer leaf"), comments.Value);
        var height = instance.Properties.Single(property => property.Name == "Clear height");
        Assert.Null(height.Key);
        Assert.Equal(SceneValue.FromText("3200.4 mm"), height.Value);
        Assert.Equal("EI 60", instance.Properties.Single(property => property.Name == "Fire rating").Value.Text);
        // one type set, shared by every instance of the type; the door's only instance parameter is its type, an element id
        Assert.Equal(wall.PropertySetIds[1], Assert.Single(door.PropertySetIds));
        var type = document.PropertySets.Single(set => set.Id == wall.PropertySetIds[1]);
        Assert.Equal("revit:type", type.Key);
        Assert.Equal("3", type.Properties.Single(property => property.Name == "Count").Value.Text);
        Assert.Single(document.PropertySets, set => set.Key == "revit:type");
    }

    [Fact]
    public void ApproximateParametersAreLeftOut()
    {
        var estimate = Bip(BuiltInParameter.ANALYTICAL_THERMAL_MASS, 453330.0);
        estimate.IsApproximate = true;
        var info = new RevitFileInfo
        {
            Elements = [new Element { Id = Wall, UniqueId = "w", Parameters = [Length(910, "Clear height", 10.5), estimate] }],
        };
        var meshes = new MeshSet { Elements = [new ElementMesh { ElementId = Wall, Meshes = [Triangle(0, 0, 0)] }] };
        var document = RvtSceneReader.Read(RvtDocument.FromInfo(info), meshes, new RvtSceneOptions { ModelKey = "m" }).Document;
        var set = Assert.Single(document.PropertySets);
        Assert.Equal("Clear height", Assert.Single(set.Properties).Name);
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void LengthShowsTheDisplayStringOfThePublicApiWithItsUnit()
    {
        var height = Length(910, "Clear height", 10.5);
        // A double without a resolved unit is left out.
        var plain = User(902, "Factor", 0.5);
        var document = MapOne(new Element { Id = Wall, UniqueId = "w", Parameters = [height, plain] });

        var texts = Texts(document);
        Assert.Equal("3200.4 mm", Assert.Single(texts).Value);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(height.AsValueString(), texts["Clear height"]);
            // The text does not follow the culture of the machine.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            Assert.Equal("3200.4 mm", Texts(MapOne(new Element { Id = Wall, UniqueId = "w", Parameters = [height] }))["Clear height"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void BuiltInDoubleWhoseUnitIsGuessedFromItsNameIsLeftOut()
    {
        // The reader gives every built-in double a unit by its name; for the text width factor 1.0 that is "304.8 mm".
        var widthFactor = GuessedLength(BuiltInParameter.TEXT_WIDTH_SCALE, 1.0);
        var baseOffset = GuessedLength(BuiltInParameter.WALL_BASE_OFFSET, 1.25);
        var document = MapOne(new Element
        {
            Id = Wall, UniqueId = "w", Parameters = [widthFactor, baseOffset, User(900, "Fire rating", "EI 60")],
        });
        Assert.Equal(["Fire rating"], Texts(document).Keys);
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void UnitWhoseFactorIsNotRevitsConversionIsLeftOut()
    {
        var angle = User(907, "Slope angle", Math.PI / 4);
        angle.SpecType = "autodesk.spec.aec:angle-2.0.0";
        angle.UnitSymbol = "\u00B0";
        angle.ConversionFactor = 180 / Math.PI;
        // Revit stores power in kg ft^2/s^3, not in watts: a factor of 1 does not give the watts Revit shows.
        var power = User(908, "Load", 120.0);
        power.SpecType = "autodesk.spec.aec.electrical:power-1.0.0";
        power.UnitSymbol = "W";
        power.ConversionFactor = 1;
        var perLength = User(909, "Surface per length", 2.0);
        perLength.UnitSymbol = "m\u00B2";
        perLength.ConversionFactor = 0.09290304;
        perLength.SpecType = "autodesk.spec.aec:surfaceAreaPerUnitLength-1.0.0";
        var document = MapOne(new Element { Id = Wall, UniqueId = "w", Parameters = [angle, power, perLength] });
        Assert.Equal("45 \u00B0", Assert.Single(Texts(document)).Value);
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void ElementIdValueIsLeftOut()
    {
        var level = Bip(BuiltInParameter.WALL_BASE_CONSTRAINT, 311, StorageType.ElementId);
        var material = User(903, "Finish", Concrete, StorageType.ElementId);
        var document = MapOne(new Element { Id = Wall, UniqueId = "w", Parameters = [level, material, User(900, "Fire rating", "EI 60")] });
        Assert.Equal(["Fire rating"], Texts(document).Keys);
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void YesNoOfAUserParameterIsTheNumberTheApiShows()
    {
        var external = User(904, "Is external", 1);
        // A built-in integer may be an enumeration, which the public API cannot tell from a number or a Yes/No: left out.
        var roomBounding = Bip(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING, 1);
        var document = MapOne(new Element { Id = Wall, UniqueId = "w", Parameters = [external, roomBounding, User(905, "Count", -12)] });
        var texts = Texts(document);
        Assert.Equal(external.AsValueString(), texts["Is external"]);
        Assert.Equal("1", texts["Is external"]);
        Assert.Equal("-12", texts["Count"]);
        Assert.DoesNotContain("WALL_ATTR_ROOM_BOUNDING", texts.Keys);
        Assert.Empty(document.Validate());
    }

    [Theory]
    [InlineData(263584, "d45b1816-cbcc-4dc6-b0bb-007467f3c600-000405a0", "3KMnWMoynDnh2x07HdzyEW")]
    [InlineData(4295967297L, "95db5b0a-a7bf-4747-af03-b1f5fe59fca1-1000f4241", "2LsriAfxz7Hwy3iVJ_LhxW")]
    public void IfcGuidIsTheExportIdOfTheUniqueId(long id, string uniqueId, string expectedGuid)
    {
        var element = MapOne(new Element { Id = 1, NativeId = id, UniqueId = uniqueId }).Elements[0];
        Assert.Equal(uniqueId, element.SourceId);
        Assert.Equal(expectedGuid, element.IfcGuid);
    }

    [Fact]
    public void MissingOrMalformedUniqueIdHasNoIfcGuid()
    {
        Assert.Null(MapOne(new Element { Id = 1, UniqueId = "d45b1816-cbcc-4dc6-b0bb-007467f3c600-0405a0" }).Elements[0].IfcGuid);
        Assert.Null(MapOne(new Element { Id = 1 }).Elements[0].IfcGuid);
    }

    [Fact]
    public void ValidIfcGuidParameterOverridesTheExportId()
    {
        const string UniqueId = "d45b1816-cbcc-4dc6-b0bb-007467f3c600-000405a0";
        var builtIn = Bip(BuiltInParameter.IFC_GUID, "2abcdefghijklmnopqrs$_");
        // Only the built-in parameter overrides, as in RvtIO's IFC export; a user parameter of the same name does not.
        var shared = User(906, "IfcGUID", "0123456789ABCDEFGHIJKL");
        Assert.Equal("2abcdefghijklmnopqrs$_", MapOne(new Element { Id = 1, UniqueId = UniqueId, Parameters = [shared, builtIn] }).Elements[0].IfcGuid);
        Assert.Equal("3KMnWMoynDnh2x07HdzyEW", MapOne(new Element { Id = 1, UniqueId = UniqueId, Parameters = [shared] }).Elements[0].IfcGuid);
        // Not a GlobalId: empty, too short, a character outside the alphabet, a first character above 3.
        foreach (string invalid in new[] { "", "2abcdefghijklmnopqrs$", "2abcdefghijklmnopqrs-_", "4abcdefghijklmnopqrs$_" })
            Assert.Equal("3KMnWMoynDnh2x07HdzyEW",
                MapOne(new Element { Id = 1, UniqueId = UniqueId, Parameters = [Bip(BuiltInParameter.IFC_GUID, invalid)] }).Elements[0].IfcGuid);
    }

    [Fact]
    public void MaterialHasItsNameAndColour()
    {
        var material = Assert.Single(Map().Document.Materials);
        Assert.Equal(Concrete, material.Id);
        Assert.Equal("Concrete", material.Name);
        Assert.Equal(new RgbaColor(0x10, 0x20, 0x30, 204), material.Color);
    }

    [Fact]
    public void MaterialWithoutColourIsGrey()
    {
        var info = new RevitFileInfo { Elements = [new Element { Id = Wall, UniqueId = "w" }, new Element { Id = Concrete, Name = "Plain" }] };
        var meshes = new MeshSet { Elements = [new ElementMesh { ElementId = Wall, Meshes = [Triangle(0, 0, 0, Concrete)] }] };
        var document = RvtSceneReader.Read(RvtDocument.FromInfo(info), meshes, new RvtSceneOptions { ModelKey = "m" }).Document;
        var material = Assert.Single(document.Materials);
        Assert.Equal(RgbaColor.Gray, material.Color);
        Assert.Equal("Plain", material.Name);
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void IncompleteGeometryIsReported()
    {
        var result = Map();
        Assert.DoesNotContain(result.Document.Instances, instance => instance.ElementId == Empty);
        Assert.Contains(result.Document.Elements, element => element.Id == Empty);
        var empty = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "no-geometry");
        Assert.Equal(SceneSeverity.Warning, empty.Severity);
        Assert.Equal("doc-00000066", empty.SourceId);
        Assert.Equal("doc-00000067", Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "partial-geometry").SourceId);
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.Equal(4, result.ElementCount);
        Assert.Equal(3, result.ElementsWithGeometry);
        Assert.Equal(new Dictionary<MeshCoverage, int> { [MeshCoverage.Complete] = 2, [MeshCoverage.NoGeometry] = 1, [MeshCoverage.Partial] = 1 }, result.Coverage);
    }

    [Fact]
    public void DocumentWithUntriangulatedInstanceFaceAndPlacedSymbol_ReportsPartialGeometry()
    {
        // An element that places a family symbol but whose own geometry record has an untriangulated face
        var symbol = new Element
        {
            Id = Symbol,
            Geometry = new GeometryData
            {
                Faces = [new GeoFace { OuterVertices = [[0, 0, 0], [1, 0, 0], [0, 1, 0]], Normal = [0, 0, 1], GeometryNode = 1 }],
            },
        };
        var instance = new Element
        {
            Id = Wall,
            UniqueId = "doc-00000064",
            CategoryId = -2000011,
            Category = "OST_Walls",
            Geometry = new GeometryData
            {
                ExactMappedInstances = [new GeoInstance { SymbolId = Symbol, Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0] }],
                UntriangulatedFaces = 1,
            },
            Parameters = [new Parameter { Id = -1012100, Value = 1 }],
            SerializedCreatedPhaseId = 1,
        };
        var doc = RvtDocument.FromInfo(new RevitFileInfo { Elements = [symbol, instance] });
        var result = RvtSceneReader.Read(doc, new RvtSceneOptions { ModelKey = "model.rvt" });

        var diag = Assert.Single(result.Diagnostics, d => d.Code == "partial-geometry");
        Assert.Equal(SceneSeverity.Warning, diag.Severity);
        Assert.Equal("doc-00000064", diag.SourceId);
        Assert.Contains("1 faces not triangulated", diag.Message);
        Assert.Equal(1, result.Coverage[MeshCoverage.Partial]);
    }

    [Fact]
    public void UndecodedDocumentKeepsItsElementsAndReportsAnError()
    {
        var meshes = new MeshSet
        {
            GeometryUndecoded = true,
            Elements = [new ElementMesh { ElementId = Wall, Coverage = MeshCoverage.Undecoded }, new ElementMesh { ElementId = Door, Coverage = MeshCoverage.Undecoded }],
        };
        var result = RvtSceneReader.Read(Document(), meshes, new RvtSceneOptions { ModelKey = "old.rvt" });
        var error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == SceneSeverity.Error);
        Assert.Equal("geometry-undecoded", error.Code);
        Assert.Contains("R22Layout", error.Message);
        Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Code == "undecoded"));
        Assert.Equal(2, result.Document.Elements.Count);
        Assert.Empty(result.Document.Instances);
        Assert.Empty(result.Document.Geometries);
        Assert.Equal(0, result.ElementsWithGeometry);
        Assert.Empty(result.Document.Validate());
    }

    [Fact]
    public void ElementWithoutUniqueIdIsReportedOnce()
    {
        var info = new RevitFileInfo { Elements = [new Element { Id = 1 }, new Element { Id = 2 }] };
        var meshes = new MeshSet { Elements = [new ElementMesh { ElementId = 1, Meshes = [Triangle(0, 0, 0)] }, new ElementMesh { ElementId = 2, Meshes = [Triangle(0, 0, 0)] }] };
        var result = RvtSceneReader.Read(RvtDocument.FromInfo(info), meshes, new RvtSceneOptions { ModelKey = "m" });
        Assert.All(result.Document.Elements, element => Assert.Null(element.SourceId));
        Assert.Contains("2 elements", Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "no-source-id").Message);
        Assert.Empty(result.Document.Validate());
    }

    private static readonly string[] MemberCodes = ["failed-members", "recovered-members", "unverified-members"];

    /// <summary>The scene of a synthetic project file read from disk; <paramref name="extraPartition"/> is a second partition stream.</summary>
    private static RvtSceneResult ReadSyntheticFile(byte[]? extraPartition = null, RvtSceneOptions? options = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "rvtio-scene-" + Guid.NewGuid().ToString("N") + ".rvt");
        try
        {
            CliExitCodeTests.WriteSyntheticRvt(path, extraPartition);
            return RvtSceneReader.Read(path, options);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PathReaderForwardsProgressAndCountsWithoutChangingScene()
    {
        byte[] extraPartition = [1, 2, 3];
        var progress = new ProgressLog<RvtReadProgress>();
        var baseline = ReadSyntheticFile(extraPartition, new RvtSceneOptions { ModelKey = "model" });
        var result = ReadSyntheticFile(extraPartition, new RvtSceneOptions { ModelKey = "model", ReadProgress = progress });

        Assert.Equal("metadata", progress.Updates[0].Phase);
        Assert.Equal(new RvtReadProgress("complete", 1, 1), progress.Updates[^1]);
        Assert.Equal([0L, 1L, 2L], progress.Updates.Where(update => update.Phase == "partitions").Select(update => update.Completed));
        Assert.All(progress.Updates.Where(update => update.Phase == "partitions"), update => Assert.Equal(2, update.Total));
        var blocks = progress.Updates.Where(update => update.Phase == "element-blocks" && update.GeometryBlocks == null).ToArray();
        Assert.Equal(Enumerable.Range(0, blocks.Length).Select(index => (long)index), blocks.Select(update => update.Completed));
        Assert.Equal(blocks[^1].Total, blocks[^1].Completed);
        ReadProgressTests.AssertWellFormed(progress.Updates, ReadProgressTests.ProjectPhases);
        Assert.Equal(baseline.Diagnostics, result.Diagnostics);
        // The synthetic file has no drawable element, so its scene is empty; ReadProgressTests compares the model and a real scene.
        Assert.Equal(SceneJson.Write(baseline.Document), SceneJson.Write(result.Document));
    }

    [Fact]
    public void StreamReadPreservesSceneDiagnosticsAndLeavesInputOpen()
    {
        string path = Path.Combine(Path.GetTempPath(), "rvtio-stream-" + Guid.NewGuid().ToString("N") + ".rvt");
        try
        {
            CliExitCodeTests.WriteSyntheticRvt(path, [1, 2, 3]);
            var options = new RvtSceneOptions { ModelKey = "model" };
            var fromPath = RvtSceneReader.Read(path, options);

            using var stream = File.OpenRead(path);
            stream.Position = stream.Length;
            var fromStream = RvtSceneReader.ReadStream(stream, options);

            Assert.Equal(SceneJson.Write(fromPath.Document), SceneJson.Write(fromStream.Document));
            Assert.Equal(fromPath.Diagnostics, fromStream.Diagnostics);
            stream.Position = 0;
            Assert.Equal(0xD0, stream.ReadByte());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StreamReadForwardsProgressInTheOrderOfAPathRead()
    {
        string path = Path.Combine(Path.GetTempPath(), "rvtio-stream-progress-" + Guid.NewGuid().ToString("N") + ".rvt");
        try
        {
            CliExitCodeTests.WriteSyntheticRvt(path, [1, 2, 3]);
            var fromPath = new ProgressLog<RvtReadProgress>();
            RvtSceneReader.Read(path, new RvtSceneOptions { ModelKey = "model", ReadProgress = fromPath });

            var fromStream = new ProgressLog<RvtReadProgress>();
            using var stream = File.OpenRead(path);
            RvtSceneReader.ReadStream(stream, new RvtSceneOptions { ModelKey = "model", ReadProgress = fromStream });

            Assert.Equal(fromPath.Updates, fromStream.Updates);
            Assert.Equal("metadata", fromStream.Updates[0].Phase);
            ReadProgressTests.AssertWellFormed(fromStream.Updates, ReadProgressTests.ProjectPhases);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ScratchReadKeepsTheSceneAndRecoveryDiagnosticsAndClosesScratch()
    {
        var baseline = ReadSyntheticFile([1, 2, 3], new RvtSceneOptions { ModelKey = "model" });
        using var scratch = new MemoryStream();
        var result = ReadSyntheticFile([1, 2, 3], new RvtSceneOptions
        {
            ModelKey = "model",
            CreatePartitionScratch = () => scratch,
        });

        Assert.Equal(SceneJson.Write(baseline.Document), SceneJson.Write(result.Document));
        Assert.Equal(baseline.Diagnostics, result.Diagnostics);
        Assert.False(scratch.CanRead);
    }

    [SkippableTheory]
    [InlineData("Test_2022.rvt", true)]
    [InlineData("Test_2025.rvt", false)]
    [InlineData("Test_2025.rvt", true)]
    public void ScratchReadMatchesSceneAndDiagnosticsOfARealModel(string fileName, bool archiveScene)
    {
        string path = TestFiles.Find(fileName);
        var baseline = RvtSceneReader.Read(path);
        string temporary = Path.Combine(Path.GetTempPath(), "rvtio-scratch-" + Guid.NewGuid().ToString("N"));
        using var input = File.OpenRead(path);
        var meshProgress = new ProgressLog<(int Completed, int Total)>();
        var options = new RvtSceneOptions
        {
            ModelKey = Path.GetFileName(path),
            MeshProgress = meshProgress,
            CreatePartitionScratch = () => new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.DeleteOnClose),
        };
        using var sceneScratch = archiveScene
            ? new FileStream(temporary + ".scene", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.DeleteOnClose)
            : null;
        var result = sceneScratch == null
            ? RvtSceneReader.ReadStream(input, options)
            : RvtSceneReader.ReadStream(input, options, sceneScratch);

        Assert.NotEmpty(baseline.Document.Instances);
        Assert.Equal(baseline.Diagnostics, result.Diagnostics);
        Assert.Equal(SceneJson.Write(baseline.Document), SceneJson.Write(result.Document));
        int total = meshProgress.Updates[0].Total;
        Assert.True(total > 0);
        Assert.Equal((0, total), meshProgress.Updates[0]);
        Assert.Equal((total, total), meshProgress.Updates[^1]);
        Assert.All(meshProgress.Updates, update => Assert.Equal(total, update.Total));
        Assert.False(File.Exists(temporary));
        Assert.True(input.CanRead);
    }

    [Fact]
    public void InvalidCompoundStreamKeepsItsTypedErrorAndRemainsOpen()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var exception = Assert.Throws<RvtInputException>(() =>
            RvtSceneReader.ReadStream(stream, new RvtSceneOptions { ModelKey = "model" }));

        Assert.Equal("not-a-compound-file", exception.Kind);
        stream.Position = 0;
        Assert.Equal(1, stream.ReadByte());
    }

    [Fact]
    public void EmptySceneReportsZeroDrawableElements()
    {
        var meshProgress = new ProgressLog<(int Completed, int Total)>();
        RvtSceneReader.Read(RvtDocument.FromInfo(new RevitFileInfo()), new RvtSceneOptions
        {
            ModelKey = "empty",
            MeshProgress = meshProgress,
        });

        Assert.Equal((0, 0), Assert.Single(meshProgress.Updates));
    }

    private sealed class ProgressLog<T> : IProgress<T>
    {
        public List<T> Updates { get; } = [];
        public void Report(T value) => Updates.Add(value);
    }

    [Fact]
    public void MembersThatFailedToDecodeAreAnErrorOfTheirStream()
    {
        // A partition stream without a gzip header: one member, and it does not decode.
        byte[] damaged = new byte[512];
        new Random(11).NextBytes(damaged);
        damaged[0] = 0; damaged[1] = 1;
        var result = ReadSyntheticFile(damaged);
        var error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == SceneSeverity.Error);
        Assert.Equal("failed-members", error.Code);
        Assert.Equal("The stream Partitions/2: 1 of 1 member(s) failed to decode. Elements may be missing.", error.Message);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code is "recovered-members" or "unverified-members");
    }

    [Fact]
    public void IntactFileHasNoMemberDiagnostics() =>
        Assert.DoesNotContain(ReadSyntheticFile().Diagnostics, diagnostic => MemberCodes.Contains(diagnostic.Code));

    [Fact]
    public void ReadDiagnosticsFollowTheIssuesOfTheExportReport()
    {
        var read = new RvtReadDiagnostics();
        read.LimitHits.Add(new ReaderLimitHit("Partitions/7", "partition-bytes", 1000, true, "the stream is longer than the limit"));
        read.Streams.Add(new StreamRecord("Global/ElemTable", 100, 1, 1, 0, 0, 0));
        // A rebuilt member is also unverified: it is reported as rebuilt only.
        read.Streams.Add(new StreamRecord("Partitions/0", 1000, 4, 3, 1, 1, 1));
        read.Streams.Add(new StreamRecord("Partitions/1", 1000, 5, 5, 0, 1, 3));

        var diagnostics = RvtSceneReader.ReadDiagnostics(read).ToList();

        Assert.Equal(
            [
                (SceneSeverity.Error, "read-cut"), (SceneSeverity.Error, "failed-members"), (SceneSeverity.Warning, "recovered-members"),
                (SceneSeverity.Warning, "recovered-members"), (SceneSeverity.Warning, "unverified-members"),
            ],
            diagnostics.Select(diagnostic => (diagnostic.Severity, diagnostic.Code)));
        Assert.Equal("The stream Partitions/0: 1 of 4 member(s) failed to decode. Elements may be missing.", diagnostics[1].Message);
        Assert.Equal("The stream Partitions/0: 1 of 4 member(s) were rebuilt by structural recovery. Elements may be incomplete or wrong.", diagnostics[2].Message);
        Assert.Contains("Partitions/1: 1 of 5 member(s) were rebuilt", diagnostics[3].Message);
        Assert.Equal("The stream Partitions/1: 2 of 5 member(s) decoded without a matching checksum. Elements may be incomplete or wrong.", diagnostics[4].Message);
    }

    [Fact]
    public void DocumentNeedsAModelKey()
    {
        var document = Document();
        Assert.Throws<ArgumentException>(() => RvtSceneReader.Read(document, null));
        Assert.Throws<ArgumentException>(() => RvtSceneReader.Read(document, new RvtSceneOptions { ModelKey = "" }));
        Assert.Throws<ArgumentNullException>(() => RvtSceneReader.Read((RvtDocument)null!, new RvtSceneOptions { ModelKey = "k" }));
        Assert.Throws<ArgumentException>(() => RvtSceneReader.Read("model.rvt", new RvtSceneOptions { ModelKey = "" }));
        Assert.Throws<ArgumentException>(() => RvtSceneReader.Read(document, Meshes(), null));
        Assert.Throws<ArgumentNullException>(() => RvtSceneReader.Read(document, null!, new RvtSceneOptions { ModelKey = "k" }));
        Assert.Throws<ArgumentNullException>(() => RvtSceneReader.Read(null!, Meshes(), new RvtSceneOptions { ModelKey = "k" }));
    }

    [Fact]
    public void DocumentIsReadWithTheGivenKey()
    {
        // None of these elements is drawable, so the scene has the model only.
        var result = RvtSceneReader.Read(Document(), new RvtSceneOptions { ModelKey = "site/A.rvt" });
        Assert.Equal("site/A.rvt", Assert.Single(result.Document.Models).Key);
        Assert.Empty(result.Document.Validate());
    }
}
