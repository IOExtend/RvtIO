using RvtIO.Model;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

public class ExactIfcStringParameterResolverTests
{
    [Fact]
    public void ResolveReference_UsesInstanceBeforeTypeAndPreservesWhitespaceValue()
    {
        var instance = ElementWithParameter("Re fer ence", StorageType.String, "   ");
        var type = ElementWithParameter("Reference", StorageType.String, "Type value");

        ExactIfcStringParameterResult result =
            ExactIfcStringParameterResolver.ResolveReference(instance, type);

        Assert.Equal(ExactIfcStringParameterState.Value, result.State);
        Assert.Equal("   ", result.Value);
    }

    [Fact]
    public void ResolveReference_EmptyInstanceFallsThroughToTypeThenTypeSuffix()
    {
        var instance = ElementWithParameter("Reference", StorageType.String, "");
        var type = ElementWithParameter("reference [type]", StorageType.String, "Type suffix");

        ExactIfcStringParameterResult result =
            ExactIfcStringParameterResolver.ResolveReference(instance, type);

        Assert.Equal(ExactIfcStringParameterState.Value, result.State);
        Assert.Equal("Type suffix", result.Value);
    }

    [Fact]
    public void ResolveReference_LeavesInstanceAndTypeParametersInScratch()
    {
        using var stream = new MemoryStream();
        var scratch = new SceneMeshScratch(stream);
        var instance = ElementWithParameter("Reference", StorageType.String, "");
        var type = ElementWithParameter("reference [type]", StorageType.String, "Type suffix");
        scratch.ArchiveParameters(instance);
        scratch.ArchiveParameters(type);
        var storedInstance = instance.ReadParameters;
        var storedType = type.ReadParameters;

        var result = ExactIfcStringParameterResolver.ResolveReference(instance, type);

        Assert.Equal(ExactIfcStringParameterState.Value, result.State);
        Assert.Equal("Type suffix", result.Value);
        Assert.Same(storedInstance, instance.ReadParameters);
        Assert.Same(storedType, type.ReadParameters);
    }

    [Theory]
    [InlineData(StorageType.Double, 12.5)]
    [InlineData(StorageType.Integer, 12)]
    [InlineData(StorageType.ElementId, 12)]
    [InlineData(StorageType.String, "{Element.Name}")]
    [InlineData(StorageType.String, "u{Element.Name}")]
    public void Resolve_PresentButUnreproducibleValueBlocksFallback(
        StorageType storageType, object value)
    {
        var element = ElementWithParameter("Reference", storageType, value);

        ExactIfcStringParameterResult result =
            ExactIfcStringParameterResolver.Resolve(element, "Reference");

        Assert.Equal(ExactIfcStringParameterState.Blocked, result.State);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Resolve_ConflictingDuplicateBlocksButIdenticalDuplicatesAreOrderIndependent()
    {
        var conflicting = ElementWithParameter("Reference", StorageType.String, "First");
        conflicting.Parameters.Add(Parameter("Reference", StorageType.String, "Second", 20));
        Assert.Equal(ExactIfcStringParameterState.Blocked,
            ExactIfcStringParameterResolver.Resolve(conflicting, "Reference").State);

        var identical = ElementWithParameter("Reference", StorageType.String, "Same");
        identical.Parameters.Add(Parameter("REF ERENCE", StorageType.String, "Same", 20));
        ExactIfcStringParameterResult identicalResult =
            ExactIfcStringParameterResolver.Resolve(identical, "Reference");
        Assert.Equal(ExactIfcStringParameterState.Value, identicalResult.State);
        Assert.Equal("Same", identicalResult.Value);
    }

    private static Element ElementWithParameter(
        string name, StorageType storageType, object? value) => new()
    {
        Parameters = [Parameter(name, storageType, value, 10)],
    };

    private static Parameter Parameter(
        string name, StorageType storageType, object? value, int id) => new()
    {
        Id = id,
        Definition = new Definition { Id = id, Name = name },
        StorageType = storageType,
        Value = value,
    };
}
