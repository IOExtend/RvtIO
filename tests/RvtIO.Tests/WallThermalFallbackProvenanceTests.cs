using System.Text.Json;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class WallThermalFallbackProvenanceTests
{
    private static readonly int[] ThermalIds = [-1005430, -1005436, -1005435, -1005434, -1005431];

    private static void Enrich(RevitFileInfo info, ParsingState? state) =>
        ElementBuilder.EnrichTypeLevelBips(info, state, info.Elements.ToDictionary(element => element.Id));

    private static (RevitFileInfo Info, Element Wall) Model()
    {
        var wall = new Element { Id = 10, CategoryId = -2000011, References = [20],
            Parameters = [new Parameter { Id = -1001000, Value = 0.375, StorageType = StorageType.Double }] };
        var material = new Element { Id = 20, CategoryId = -2000700 };
        return (new RevitFileInfo { Elements = [wall, material] }, wall);
    }

    [Fact]
    public void EnrichUsesTheFirstParameterWhenTheSameIdOccursTwice()
    {
        var (info, wall) = Model();
        var first = BipHelpers.MakeBipParam(-1005500, "STRUCTURAL_MATERIAL_PARAM", StorageType.ElementId, -1);
        var second = BipHelpers.MakeBipParam(-1005500, "STRUCTURAL_MATERIAL_PARAM", StorageType.ElementId, -2);
        wall.Parameters.AddRange([first, second]);

        Enrich(info, new ParsingState());

        Assert.Equal(20, first.Value);
        Assert.Equal(-2, second.Value);
        Assert.Equal([first, second], wall.Parameters.Where(parameter => parameter.Id == -1005500));
    }

    [Fact]
    public void CommonDefaultsRetainStoredValuesAndAppendInRuleOrder()
    {
        var (info, wall) = Model();
        var stored = BipHelpers.MakeBipParam(-1010104, "ALL_MODEL_URL", StorageType.String, "stored");
        wall.Parameters.Add(stored);

        Enrich(info, new ParsingState());

        Assert.Same(stored, Assert.Single(wall.Parameters, parameter => parameter.Id == -1010104));
        Assert.Equal("stored", stored.Value);
        int first = wall.Parameters.FindIndex(parameter => parameter.Id == -1010103);
        Assert.True(first >= 0);
        Assert.Equal(-1010105, wall.Parameters[first + 1].Id);
        Assert.Equal(-1001205, wall.Parameters[^1].Id);
        Assert.All(wall.Parameters.Skip(first), parameter => Assert.True(parameter.HasNoRevitValue));
    }

    [Fact]
    public void CommonDefaultDefinitionsCanBeEditedIndependently()
    {
        var (info, wall) = Model();
        Enrich(info, new ParsingState());

        const int descriptionId = -1010103;
        var wallDescription = Assert.Single(wall.Parameters, parameter => parameter.Id == descriptionId);
        var materialDescription = Assert.Single(info.Elements[1].Parameters, parameter => parameter.Id == descriptionId);
        wallDescription.Definition.Name = "Edited wall description";

        Assert.Equal("ALL_MODEL_DESCRIPTION", materialDescription.Definition.Name);
        Assert.Equal("", materialDescription.Value);
    }

    [Fact]
    public void DerivedOffsetCanReadAnElevationQueuedInTheSameBatch()
    {
        var level = new Element
        {
            Id = 1,
            CategoryId = -2000240,
            Parameters = [BipHelpers.MakeBipParam(-1007102, "LEVEL_ELEV", StorageType.Double, 1.25)],
        };
        var instance = new Element { Id = 2, CategoryId = -2000151, References = [1], BoundingBox = [0, 0, 3, 5, 2, 5] };

        Enrich(new RevitFileInfo { Elements = [level, instance] }, new ParsingState());

        var elevation = Assert.Single(instance.Parameters, parameter => parameter.Id == -1001360);
        var offset = Assert.Single(instance.Parameters, parameter => parameter.Id == -1001364);
        Assert.Equal(1.75, elevation.Value);
        Assert.Equal(elevation.Value, offset.Value);
        Assert.True(instance.Parameters.IndexOf(elevation) < instance.Parameters.IndexOf(offset));
    }

    [Fact]
    public void TypeReferencesKeepPriorityAndOnlyLevelSelectionExcludesSelf()
    {
        var phaseZero = new Element { Id = 0, CategoryId = -2000112, TypeId = 10 };
        var otherPhase = new Element { Id = 11, CategoryId = -2000112 };
        var level = new Element { Id = 1, CategoryId = -2000240, TypeId = 10 };
        var otherLevel = new Element { Id = 2, CategoryId = -2000240 };
        var host = new Element { Id = 3, CategoryId = -2000011 };
        var otherHost = new Element { Id = 4, CategoryId = -2000011 };
        var type = new Element { Id = 10, References = [1, 2, 0, 3] };
        var instance = new Element { Id = 20, CategoryId = -2000170, TypeId = 10, References = [11, 4] };

        Enrich(new RevitFileInfo
        {
            Elements = [phaseZero, otherPhase, level, otherLevel, host, otherHost, type, instance],
        }, new ParsingState());

        Assert.Equal(2, Value(level, -1001352));
        Assert.Equal(1, Value(instance, -1001352));
        Assert.Equal(0, Value(phaseZero, -1012100));
        Assert.Equal(0, Value(instance, -1012100));
        Assert.Equal(3, Value(instance, -1002108));

        static object? Value(Element element, int id) => Assert.Single(element.Parameters, p => p.Id == id).Value;
    }

    [Theory]
    [InlineData(-1005430, 3.4866666666666668, StorageType.Double)]
    [InlineData(-1005436, 3, StorageType.Integer)]
    [InlineData(-1005435, 0.7, StorageType.Double)]
    [InlineData(-1005434, 453330.0, StorageType.Double)]
    [InlineData(-1005431, 0.375 / 3.4866666666666668, StorageType.Double)]
    public void NewThermalFallbackRetainsLegacyValueButIsNotExact(int id, object expected, StorageType storage)
    {
        var (info, wall) = Model();
        Enrich(info, new ParsingState());
        var parameter = Assert.Single(wall.Parameters, p => p.Id == id);
        Assert.Equal(expected, parameter.Value);
        Assert.Equal(storage, parameter.StorageType);
        Assert.True(parameter.IsApproximate);
        Assert.Null(parameter.SpecType);
        Assert.Null(parameter.ConversionFactor);
        string serialized = JsonSerializer.Serialize(parameter);
        Enrich(info, new ParsingState());
        Assert.Same(parameter, Assert.Single(wall.Parameters, p => p.Id == id));
        Assert.Equal(serialized, JsonSerializer.Serialize(parameter));
        Assert.True(parameter.IsApproximate);
    }

    public static IEnumerable<object?[]> ExistingValues() => ThermalIds.SelectMany(id =>
        new object?[] { null, 0, 0.0, "{unresolved}", -1 }.SelectMany(value =>
            new[] { false, true }.Select(approximate => new object?[] { id, value, approximate })));

    [Theory]
    [MemberData(nameof(ExistingValues))]
    public void ExistingParameterIsNeverReplacedOrRemarked(int id, object? value, bool approximate)
    {
        var (info, wall) = Model();
        var parameter = new Parameter { Id = id, Value = value, IsApproximate = approximate,
            StorageType = StorageType.Double, Definition = new Definition { Id = id, Name = "own" },
            SpecType = "own-spec", UnitSymbol = "own-unit", ConversionFactor = 2.5 };
        wall.Parameters.Add(parameter);
        string before = JsonSerializer.Serialize(parameter);
        Enrich(info, new ParsingState());
        Assert.Same(parameter, Assert.Single(wall.Parameters, p => p.Id == id));
        Assert.Equal(before, JsonSerializer.Serialize(parameter));
        Assert.Equal(approximate, parameter.IsApproximate);
        Assert.Equal("own", parameter.Definition.Name);
        Assert.Equal("own-spec", parameter.SpecType);
        Assert.Equal("own-unit", parameter.UnitSymbol);
        Assert.Equal(2.5, parameter.ConversionFactor);
    }

    [Theory]
    [InlineData("null_state")]
    [InlineData("no_category")]
    [InlineData("nonwall")]
    [InlineData("no_material")]
    [InlineData("unresolved_material")]
    [InlineData("wrong_material_category")]
    public void ExistingFallbackScopeIsNotExpanded(string mutation)
    {
        var (info, wall) = Model();
        switch (mutation)
        {
            case "no_category": wall.CategoryId = null; break;
            case "nonwall": wall.CategoryId = -2000032; break;
            case "no_material": wall.References = []; break;
            case "unresolved_material": wall.References = [21]; break;
            case "wrong_material_category": info.Elements[1].CategoryId = -2000023; break;
        }
        Enrich(info, mutation == "null_state" ? null : new ParsingState());
        Assert.DoesNotContain(wall.Parameters, p => ThermalIds.Contains(p.Id));
    }

    [Theory]
    [InlineData(null, StorageType.Double)]
    [InlineData(1, StorageType.Integer)]
    public void ResistanceStillRequiresExistingDoubleWidth(object? width, StorageType storage)
    {
        var (info, wall) = Model();
        wall.Parameters[0].Value = width;
        wall.Parameters[0].StorageType = storage;
        Enrich(info, new ParsingState());
        Assert.DoesNotContain(wall.Parameters, p => p.Id == -1005431);
        Assert.All(wall.Parameters.Where(p => ThermalIds.Contains(p.Id)), p => Assert.True(p.IsApproximate));
    }
}
