using System.Globalization;
using IOScene;
using RvtIO.BuiltIns;
using RvtIO.Meshes;
using RvtIO.Model;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The scene of <see cref="RvtSceneReader"/> on synthetic meshes and elements: instancing, mirrored placements, local
/// origins, feet to metres, element facts, IFC GlobalIds, property sets and their display text, materials, the diagnostics of
/// incomplete geometry and those of a damaged read.</summary>
public class RvtSceneReaderTests
{
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
    private static RvtSceneResult ReadSyntheticFile(byte[]? extraPartition = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "rvtio-scene-" + Guid.NewGuid().ToString("N") + ".rvt");
        try
        {
            CliExitCodeTests.WriteSyntheticRvt(path, extraPartition);
            return RvtSceneReader.Read(path);
        }
        finally { File.Delete(path); }
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
