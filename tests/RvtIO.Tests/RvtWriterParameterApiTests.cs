using RvtIO.BuiltIns;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The argument and file-kind checks of the public parameter-editing API, which run before any source is opened.</summary>
public sealed class RvtWriterParameterApiTests : IDisposable
{
    private const long Mark = (long)BuiltInParameter.ALL_MODEL_MARK, FamilyComments = (long)BuiltInParameter.ALL_MODEL_TYPE_COMMENTS;
    private readonly string _folder = Directory.CreateTempSubdirectory("rvtio-api-").FullName;
    private string Missing(string name) => Path.Combine(_folder, name);
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static RvtParameterEdit[] Valid => [new(1, Mark, "x")];
    private static RvtFamilyParameterEdit[] ValidFamily => [new("A", FamilyComments, "x")];

    private static void AssertKind(string kind, Action action) =>
        Assert.Equal(kind, Assert.Throws<RvtWriteRefusedException>(action).Kind);

    [Fact]
    public void ProjectBatchRejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetParameters(null!, Missing("o.rvt"), Valid));
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetParameters(Missing("s.rvt"), null!, Valid));
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetParameters(Missing("s.rvt"), Missing("o.rvt"), null!));
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetParameter(Missing("s.rvt"), Missing("o.rvt"), 1, 5, null!));
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetParameter(Missing("s.rvt"), Missing("o.rvt"), 1, BuiltInParameter.ALL_MODEL_MARK, null!));
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetParameters(Missing("s.rvt"), Missing("o.rvt"), [null!]));
    }

    [Theory]
    [InlineData("s.rfa", "o.rvt")]
    [InlineData("s.rvt", "o.rfa")]
    [InlineData("s", "o.rvt")]
    [InlineData("s.rvt", "o")]
    [InlineData("s.rte", "o.rvt")]
    public void ProjectBatchRefusesAnyOtherFileExtension(string source, string output) =>
        AssertKind("unsupported-parameter-file", () => RvtWriter.SetParameters(Missing(source), Missing(output), Valid));

    [Fact]
    public void ProjectBatchAcceptsTheExtensionInAnyCase()
    {
        var error = Record.Exception(() => RvtWriter.SetParameters(Missing("S.RVT"), Missing("O.Rvt"), Valid));
        Assert.IsNotType<RvtWriteRefusedException>(error);
        Assert.IsNotType<ArgumentException>(error);
    }

    [Fact]
    public void ProjectBatchRejectsAnEmptyBatchAndInvalidIds()
    {
        string source = Missing("s.rvt"), output = Missing("o.rvt");
        Assert.Equal(typeof(ArgumentException), Assert.ThrowsAny<ArgumentException>(() => RvtWriter.SetParameters(source, output, [])).GetType());
        Assert.Throws<ArgumentOutOfRangeException>(() => RvtWriter.SetParameters(source, output, [new(0, Mark, "x")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RvtWriter.SetParameters(source, output, [new(-3, Mark, "x")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RvtWriter.SetParameters(source, output, [new(1, Mark, "x"), new(0, Mark, "y")]));
    }

    [Theory]
    [InlineData((long)int.MinValue - 1, "x", "unsupported-parameter")]
    [InlineData(-1L, "x", "unsupported-parameter")]
    [InlineData((long)BuiltInParameter.HOST_AREA_COMPUTED, "1", "read-only-parameter")]
    [InlineData(Mark, null, "unsupported-parameter-clear")]
    [InlineData(Mark, "bad\u0001text", "invalid-parameter-value")]
    [InlineData(71L, "bad\u0001text", "invalid-parameter-value")]
    public void ProjectBatchValidatesTheParameterAndItsValue(long parameterId, string? value, string kind) =>
        AssertKind(kind, () => RvtWriter.SetParameters(Missing("s.rvt"), Missing("o.rvt"), [new(1, parameterId, value)]));

    [Fact]
    public void FamilyBatchRejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetFamilyParameters(null!, Missing("o.rfa"), ValidFamily));
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetFamilyParameters(Missing("s.rfa"), null!, ValidFamily));
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetFamilyParameters(Missing("s.rfa"), Missing("o.rfa"), null!));
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetFamilyParameter(Missing("s.rfa"), Missing("o.rfa"), "A", 71, null!));
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetFamilyParameter(Missing("s.rfa"), Missing("o.rfa"), "A", 71L, null!));
        Assert.Throws<ArgumentNullException>(() => RvtWriter.SetFamilyParameters(Missing("s.rfa"), Missing("o.rfa"), [null!]));
    }

    [Theory]
    [InlineData("s.rvt", "o.rfa")]
    [InlineData("s.rfa", "o.rvt")]
    [InlineData("s", "o.rfa")]
    [InlineData("s.rfa", "o")]
    [InlineData("s.rft", "o.rfa")]
    public void FamilyBatchRefusesAnyOtherFileExtension(string source, string output) =>
        AssertKind("unsupported-parameter-file", () => RvtWriter.SetFamilyParameters(Missing(source), Missing(output), ValidFamily));

    [Fact]
    public void FamilyBatchAcceptsTheExtensionInAnyCase()
    {
        var error = Record.Exception(() => RvtWriter.SetFamilyParameters(Missing("S.RFA"), Missing("O.Rfa"), ValidFamily));
        Assert.IsNotType<RvtWriteRefusedException>(error);
        Assert.IsNotType<ArgumentException>(error);
    }

    [Fact]
    public void FamilyBatchRejectsAnEmptyBatchBlankTypeNamesAndAZeroId()
    {
        string source = Missing("s.rfa"), output = Missing("o.rfa");
        Assert.Equal(typeof(ArgumentException), Assert.ThrowsAny<ArgumentException>(() => RvtWriter.SetFamilyParameters(source, output, [])).GetType());
        foreach (string? name in new string?[] { null, "", " ", "\t" })
            Assert.ThrowsAny<ArgumentException>(() => RvtWriter.SetFamilyParameters(source, output, [new(name!, FamilyComments, "x")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RvtWriter.SetFamilyParameters(source, output, [new("A", 0, "x")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RvtWriter.SetFamilyParameters(source, output, [new("A", FamilyComments, "x"), new("A", 0, "y")]));
    }

    [Theory]
    [InlineData(-1L, "x", "unsupported-parameter")]
    [InlineData((long)int.MinValue - 1, "x", "unsupported-parameter")]
    [InlineData(FamilyComments, null, "unsupported-parameter-clear")]
    [InlineData(FamilyComments, "bad\u0001text", "invalid-parameter-value")]
    [InlineData(71L, "bad\u0001text", "invalid-parameter-value")]
    public void FamilyBatchValidatesTheParameterAndItsValue(long parameterId, string? value, string kind) =>
        AssertKind(kind, () => RvtWriter.SetFamilyParameters(Missing("s.rfa"), Missing("o.rfa"), [new("A", parameterId, value)]));
}
