using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class BipParameterTests
{
    [Theory]
    [InlineData("ALL_MODEL_MARK")]
    [InlineData("Custom Mark")]
    [InlineData(null)]
    public void BuiltInParametersRetainTheirNamesAndHaveIndependentDefinitions(string? name)
    {
        const int id = (int)BuiltInParameter.ALL_MODEL_MARK;
        var first = BipHelpers.MakeBipParam(id, name, StorageType.String, "first");
        var second = BipHelpers.MakeBipParam(id, name, StorageType.String, "");

        Assert.Equal((id, name, name), first.DefinitionFields!.Value);
        Assert.Equal((id, name, name), second.DefinitionFields!.Value);
        Assert.Equal("first", first.Value);
        Assert.Equal("", second.Value);

        first.Definition.Name = "Edited";

        Assert.Equal("Edited", first.DefinitionName);
        Assert.Equal(name, second.DefinitionName);
        Assert.Equal((id, name, name),
            BipHelpers.MakeBipParam(id, name, StorageType.String, null).DefinitionFields!.Value);
    }
}
