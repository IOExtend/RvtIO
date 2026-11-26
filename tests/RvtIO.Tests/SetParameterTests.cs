using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Cli;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Back = RvtIO.Tests.SchemaStreamEditorTests.Back;
using Fld = RvtIO.Tests.SchemaStreamEditorTests.Fld;
using NoRef = RvtIO.Tests.SchemaStreamEditorTests.NoRef;

namespace RvtIO.Tests;

public sealed class SetParameterTests : IDisposable
{
    private const int ProjectId = 70;
    private const int DefinitionId = 71;
    private const int BindingId = 72;
    private static PartitionFraming.Tags Tags(int release) => release switch
    {
        2020 => new(779, 778, 780, 781, 782),
        2021 => new(801, 800, 802, 803, 804),
        _ => new(817, 816, 818, 819, 820),
    };
    private readonly string _folder = Directory.CreateTempSubdirectory("rvtio-parameter-").FullName;
    private string Output => Path.Combine(_folder, "output.rvt");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("A different & longer name \U0001F642\r\nSecond line")]
    [InlineData("")]
    [InlineData("Owner")]
    public void WritesIdenticalTextToTheRecordAndMetadata(string value)
    {
        string source = CreateSource();
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(source));
        RvtWriteResult result = RvtWriter.SetParameter(source, Output, ProjectId, BuiltInParameter.CLIENT_NAME, value);
        Assert.Equal(value == "Owner" ? 0 : 2, result.ReplacedStreamCount);
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(source)));

        using var root = RootStorage.OpenRead(Output);
        using var info = root.OpenStream("ProjectInformation");
        using var archive = new ZipArchive(info);
        using var xml = Assert.Single(archive.Entries).Open();
        var fields = XDocument.Load(xml).Descendants().ToArray();
        Assert.Equal(value, Assert.Single(fields, f => f.Name.LocalName == "Client_Name").Value);
        Assert.Equal("Owner", Assert.Single(fields, f => f.Name.LocalName == "Project_Name").Value);

        Assert.Equal(Body(value), ReadBody(root));
    }

    [Fact]
    public void BatchAppliesRepeatedEditsToTheSameRecordAndMetadataInOrder()
    {
        string source = CreateSource();
        var result = RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.CLIENT_NAME, "First"),
             new(ProjectId, (int)BuiltInParameter.CLIENT_NAME, "Last")]);
        Assert.Equal(1, result.RewrittenRecordCount);
        Assert.Equal(2, result.ReplacedStreamCount);
        using var root = RootStorage.OpenRead(Output);
        using var info = root.OpenStream("ProjectInformation");
        using var archive = new ZipArchive(info);
        using var xml = Assert.Single(archive.Entries).Open();
        Assert.Equal("Last", Assert.Single(XDocument.Load(xml).Descendants(), e => e.Name.LocalName == "Client_Name").Value);
        Assert.Equal(Body("Last"), ReadBody(root));
    }

    [Theory]
    [InlineData(Kind.Text, "Первое значение", "", false)]
    [InlineData(Kind.Integer, "17", "-9", false)]
    [InlineData(Kind.YesNo, "1", "0", false)]
    [InlineData(Kind.Number, "7.625", "11.125", false)]
    [InlineData(Kind.Number, "1234.56789", "-58.625", true)]
    public void AssignsAndReplacesAnIndependentGlobalParameter(Kind kind, string first, string last, bool currency)
    {
        var parameter = new GlobalFixture(kind, Currency: currency);
        string source = CreateSource(globalParameter: parameter);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var result = RvtWriter.SetParameters(source, Output, [new(DefinitionId, DefinitionId, first), new(DefinitionId, DefinitionId, last)]);
        Assert.Equal(1, result.RewrittenRecordCount);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(GlobalBody(parameter, last), ReadBody(root, DefinitionId));
    }

    [Fact]
    public void AssignsAnIndependentGlobalForceUsingKilonewtons()
    {
        var parameter = new GlobalFixture(Kind.Number, SpecId: "autodesk.spec.aec.structural:force-2.0.0");
        string source = CreateSource(globalParameter: parameter);
        RvtWriter.SetParameter(source, Output, DefinitionId, DefinitionId, "13.5 kN");
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(GlobalBody(parameter, "44291.33858267716"), ReadBody(root, DefinitionId));
    }

    [Theory]
    [InlineData("NaN", "invalid-parameter-value")]
    [InlineData("1e309", "invalid-parameter-value")]
    [InlineData("42 $", "invalid-parameter-value")]
    [InlineData("17.25000000001", "unverified-parameter-storage")]
    [InlineData(null, "unsupported-parameter-clear")]
    public void RefusesInvalidIndependentCurrencyValuesAfterStagingAnAssignment(string? value, string code)
    {
        string source = CreateSource(globalParameter: new(Kind.Number, Currency: true));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(DefinitionId, DefinitionId, "17.25"), new(DefinitionId, DefinitionId, value)]));
        Assert.Equal(code, error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData("1e-300", "0")]
    [InlineData("1.7976931348623157e308", "-1.7976931348623157e308")]
    public void RefusesIndependentCurrencyChangesThatRevitMaySuppress(string initial, string value)
    {
        string source = CreateSource(globalParameter: new(Kind.Number, InitialValue: initial, Currency: true));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.CLIENT_NAME, "Staged"), new(DefinitionId, DefinitionId, value)]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData("formula", "1", "read-only-parameter")]
    [InlineData("reporting", "1", "read-only-parameter")]
    [InlineData("consumer", "1", "unverified-parameter-storage")]
    [InlineData("", null, "unsupported-parameter-clear")]
    [InlineData("", "2", "invalid-parameter-value")]
    public void RefusesTheWholeBatchForAnInvalidGlobalAssignment(string control, string? value, string code)
    {
        var parameter = new GlobalFixture(Kind.YesNo, Formula: control == "formula", Reporting: control == "reporting",
            Consumer: control == "consumer");
        string source = CreateSource(globalParameter: parameter);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.CLIENT_NAME, "Staged"), new(DefinitionId, DefinitionId, value)]));
        Assert.Equal(code, error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void RecalculatesGlobalFormulaChainsUsingTheCurrentBatchValues(bool currencyInput, bool currencyResult)
    {
        var number = new GlobalFixture(Kind.Number);
        var input = number with { Currency = currencyInput };
        var graph = new GlobalFormulaFixture(CurrencyResult: currencyResult);
        string source = CreateSource(globalParameter: input, globalFormulas: graph);
        var write = RvtWriter.SetParameters(source, Output, [new(DefinitionId, DefinitionId, "3"), new(DefinitionId, DefinitionId, "5")]);
        Assert.Equal(3, write.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(GlobalBody(input, "5"), ReadBody(root, DefinitionId));
        Assert.Equal(GlobalBody(number with { Currency = currencyResult }, "10", BindingId, new(3, DefinitionId, 2)),
            ReadBody(root, BindingId));
        Assert.Equal(GlobalBody(number, "11", GlobalFormulaResultId, new(1, BindingId, 1)), ReadBody(root, GlobalFormulaResultId));
    }

    [Theory]
    [InlineData("autodesk.spec.aec.structural:mass-2.0.0", "autodesk.spec.aec.structural:mass-2.0.0", "5 kg", "5", "7", "8")]
    [InlineData("autodesk.spec.aec:slope-2.0.0", "autodesk.spec.aec:number-2.0.0", "5 %", "0.05", "2.05", "3.05")]
    public void RecalculatesAFormulaChainWithMeasuredValues(string spec, string constantSpec, string requested, string input,
        string result, string downstream)
    {
        var value = new GlobalFixture(Kind.Number, SpecId: spec);
        string source = CreateSource(globalParameter: value,
            globalFormulas: new(Operation: 1, SpecId: spec, ConstantSpecId: constantSpec));
        var write = RvtWriter.SetParameter(source, Output, DefinitionId, DefinitionId, requested);
        Assert.Equal(3, write.RewrittenRecordCount);

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(GlobalBody(value, input), ReadBody(root, DefinitionId));
        Assert.Equal(GlobalBody(value, result, BindingId, new(1, DefinitionId, 2, SpecId: constantSpec)),
            ReadBody(root, BindingId));
        Assert.Equal(GlobalBody(value, downstream, GlobalFormulaResultId, new(1, BindingId, 1, SpecId: constantSpec)),
            ReadBody(root, GlobalFormulaResultId));
    }

    [Theory]
    [InlineData(3, "volume", "massDensity", "mass", "4 ft3", false)]
    [InlineData(3, "volume", "massDensity", "mass", "4 ft3", true)]
    [InlineData(3, "time", "speed", "length", "4 s", false)]
    [InlineData(3, "time", "speed", "length", "4 s", true)]
    [InlineData(4, "mass", "volume", "massDensity", "4 kg", false)]
    [InlineData(4, "mass", "massDensity", "volume", "4 kg", false)]
    [InlineData(4, "length", "time", "speed", "4 ft", false)]
    [InlineData(4, "length", "speed", "time", "4 ft", false)]
    public void RecalculatesGlobalProductsAndQuotientsWithDifferentUnits(int operation, string inputSpec,
        string factorSpec, string resultSpec, string requested, bool constantFirst)
    {
        var input = new GlobalFixture(Kind.Number, SpecId: PhysicalSpec(inputSpec));
        var result = new GlobalFixture(Kind.Number, SpecId: PhysicalSpec(resultSpec));
        var expression = new GlobalBinary(operation, DefinitionId, 2, constantFirst, PhysicalSpec(factorSpec));
        string source = CreateSource(globalParameter: input, globalFormulas: new(SpecId: result.SpecId,
            ConstantSpecId: result.SpecId, Binary: expression, Cached: operation == 3 ? 4 : 1));
        var write = RvtWriter.SetParameter(source, Output, DefinitionId, DefinitionId, requested);
        Assert.Equal(3, write.RewrittenRecordCount);

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(GlobalBody(input, "4"), ReadBody(root, DefinitionId));
        Assert.Equal(GlobalBody(result, operation == 3 ? "8" : "2", BindingId, expression), ReadBody(root, BindingId));
        Assert.Equal(GlobalBody(result, operation == 3 ? "9" : "3", GlobalFormulaResultId,
            new(1, BindingId, 1, SpecId: result.SpecId)), ReadBody(root, GlobalFormulaResultId));
    }

    [Fact]
    public void RefusesAnUnverifiedCombinationOfMeasuredUnitsAtomically()
    {
        var input = new GlobalFixture(Kind.Number, SpecId: PhysicalSpec("mass"));
        string outputSpec = PhysicalSpec("length");
        var mixed = new GlobalBinary(3, DefinitionId, 2, SpecId: PhysicalSpec("time"));
        string source = CreateSource(globalParameter: input, globalFormulas: new(SpecId: outputSpec,
            ConstantSpecId: outputSpec, Binary: mixed));
        byte[] original = File.ReadAllBytes(source);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.CLIENT_NAME, "Staged"), new(DefinitionId, DefinitionId, "4 kg")]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    private static string PhysicalSpec(string name) => name == "mass" ? "autodesk.spec.aec.structural:mass-2.0.0"
        : "autodesk.spec.aec:" + name + "-2.0.0";

    [Theory]
    [InlineData(false, "read-only-parameter")]
    [InlineData(true, "unverified-parameter-storage")]
    public void RejectsBatchesWithAWritableCurrencyInputAndAnInvalidFormulaEdit(bool stale, string code)
    {
        string source = CreateSource(globalParameter: new(Kind.Number, Currency: true),
            globalFormulas: new(Stale: stale, CurrencyResult: true));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(DefinitionId, DefinitionId, "5"), new(BindingId, BindingId, "7")]));
        Assert.Equal(code, error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void GlobalFormulaChainUsesTheRoundedIntegerResult()
    {
        var number = new GlobalFixture(Kind.Number);
        var integer = new GlobalFixture(Kind.Integer);
        string source = CreateSource(globalParameter: number, globalFormulas: new(IntegerResult: true));
        var write = RvtWriter.SetParameter(source, Output, DefinitionId, DefinitionId, "-2");
        Assert.Equal(3, write.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(GlobalBody(integer, "-2", BindingId, new(3, DefinitionId, 1.25)), ReadBody(root, BindingId));
        Assert.Equal(GlobalBody(number, "-1", GlobalFormulaResultId, new(1, BindingId, 1)), ReadBody(root, GlobalFormulaResultId));
    }

    [Fact]
    public void GlobalIfFormulaUsesTheChangedYesNoValue()
    {
        var integer = new GlobalFixture(Kind.Integer);
        string source = CreateSource(globalParameter: new(Kind.YesNo), globalFormulas: new(Conditional: true));
        var write = RvtWriter.SetParameter(source, Output, DefinitionId, DefinitionId, "0");
        Assert.Equal(3, write.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(GlobalBody(integer, "3", BindingId, conditional: true), ReadBody(root, BindingId));
        Assert.Equal(GlobalBody(new(Kind.Number), "4", GlobalFormulaResultId, new(1, BindingId, 1)), ReadBody(root, GlobalFormulaResultId));
    }

    [Fact]
    public void RecalculatesASquareRootAndItsDependent()
    {
        var number = new GlobalFixture(Kind.Number);
        string source = CreateSource(globalParameter: number, globalFormulas: new(Function: 9));
        var write = RvtWriter.SetParameter(source, Output, DefinitionId, DefinitionId, "9");
        Assert.Equal(3, write.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(GlobalBody(number, "3", BindingId, function: 9), ReadBody(root, BindingId));
        Assert.Equal(GlobalBody(number, "4", GlobalFormulaResultId, new(1, BindingId, 1)), ReadBody(root, GlobalFormulaResultId));
    }

    [Theory]
    [InlineData(3, 9, "-1", "9", 3)]
    [InlineData(3, 7, "70", "0", 1)]
    [InlineData(5, 0, "1e16", "3", 9)]
    [InlineData(4, 0, "0", "3", 4)]
    [InlineData(4, 0, "1e-9", "3", 4)]
    public void PreservesTheStoredFormulaValueAndResumesCalculation(int operation, int function, string value,
        string recoveredInput, double recoveredResult)
    {
        var number = new GlobalFixture(Kind.Number);
        string source = CreateSource(globalParameter: number, globalFormulas: new(Operation: operation, Function: function));
        byte[] cached;
        byte[] dependent;
        using (var original = RootStorage.OpenRead(source))
        {
            cached = ReadBody(original, BindingId);
            dependent = ReadBody(original, GlobalFormulaResultId);
        }

        var retained = RvtWriter.SetParameter(source, Output, DefinitionId, DefinitionId, value);
        Assert.Equal(1, retained.RewrittenRecordCount);
        using (var written = RootStorage.OpenRead(Output))
        {
            Assert.Equal(GlobalBody(number, value), ReadBody(written, DefinitionId));
            Assert.Equal(cached, ReadBody(written, BindingId));
            Assert.Equal(dependent, ReadBody(written, GlobalFormulaResultId));
        }

        string recoveredPath = Path.Combine(_folder, "recovered.rvt");
        var recovered = RvtWriter.SetParameter(Output, recoveredPath, DefinitionId, DefinitionId, recoveredInput);
        Assert.Equal(3, recovered.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(recoveredPath);
        GlobalBinary? binary = function == 0
            ? new(operation, DefinitionId, operation == 4 ? 12 : 2, ConstantFirst: operation == 4) : null;
        Assert.Equal(GlobalBody(number, recoveredResult.ToString(CultureInfo.InvariantCulture), BindingId, binary, function: function),
            ReadBody(root, BindingId));
        Assert.Equal(GlobalBody(number, (recoveredResult + 1).ToString(CultureInfo.InvariantCulture), GlobalFormulaResultId,
            new(1, BindingId, 1)), ReadBody(root, GlobalFormulaResultId));
    }

    [Fact]
    public void RejectsABatchThatTriesToEditARetainedFormulaResult()
    {
        string source = CreateSource(globalParameter: new(Kind.Number), globalFormulas: new(Function: 9));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(DefinitionId, DefinitionId, "-1"), new(BindingId, BindingId, "5")]));
        Assert.Equal("read-only-parameter", error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(9, false, false, "3", "unverified-parameter-storage")]
    [InlineData(3, true, false, "3", "unverified-parameter-storage")]
    [InlineData(3, false, true, "3", "unverified-parameter-storage")]
    [InlineData(3, false, false, "2.000000001", "unverified-parameter-storage")]
    [InlineData(5, false, false, "1e-12", "unverified-parameter-storage")]
    public void RefusesTheWholeBatchWhenAGlobalFormulaCannotBeRecalculated(int operation, bool stale, bool consumer,
        string value, string code)
    {
        var graph = new GlobalFormulaFixture(operation, stale, consumer);
        string source = CreateSource(globalParameter: new(Kind.Number), globalFormulas: graph);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.CLIENT_NAME, "Staged"), new(DefinitionId, DefinitionId, value)]));
        Assert.Equal(code, error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(Kind.Text, "Restored text")]
    [InlineData(Kind.Integer, "42")]
    [InlineData(Kind.YesNo, "0")]
    [InlineData(Kind.Number, "8.625")]
    [InlineData(Kind.Number, "-8.625", true)]
    [InlineData(Kind.Material, "81")]
    public void BatchClearsSharedValueAndCanAssignItAgain(Kind kind, string newValue, bool currency = false)
    {
        var parameter = new UserParameter(kind, true, HideWhenNoValue: true, Currency: currency);
        string source = CreateSource(wall: true, user: parameter);
        var result = RvtWriter.SetParameters(source, Output,
            [new(ProjectId, DefinitionId, null), new(ProjectId, DefinitionId, null)]);
        Assert.Equal(1, result.RewrittenRecordCount);
        using (var cleared = RootStorage.OpenRead(Output))
            Assert.Equal(UserBody(parameter, cleared: true), ReadBody(cleared));

        string restored = Path.Combine(_folder, "restored.rvt");
        RvtWriter.SetParameters(source, restored,
            [new(ProjectId, DefinitionId, null), new(ProjectId, DefinitionId, newValue)]);
        using var root = RootStorage.OpenRead(restored);
        Assert.Equal(UserBody(parameter, newValue), ReadBody(root));
    }

    [Theory]
    [InlineData(false, false, "unsupported-parameter-clear")]
    [InlineData(true, false, "unsupported-parameter-clear")]
    [InlineData(true, true, "missing-element")]
    public void BatchRefusalLeavesNoOutputAndPreservesTheInput(bool shared, bool hide, string code)
    {
        string source = CreateSource(wall: true, user: new(Kind.Text, shared, HideWhenNoValue: hide));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, DefinitionId, "Staged"), new(hide ? 999 : ProjectId, DefinitionId, null)]));
        Assert.Equal(code, error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    private static byte[] ReadBody(RootStorage root, ulong elementId = ProjectId, int release = 2022)
    {
        using var stream = root.OpenStorage("Partitions").OpenStream("0");
        byte[] raw = new byte[stream.Length];
        stream.ReadExactly(raw);
        var records = new List<byte[]>();
        Assert.True(PartitionFraming.Walk(raw, Tags(release), 4, record =>
        {
            if (record.Block != 0 || record.Channel != 102 || record.Owner != elementId)
                return;
            records.Add(record.Body.ToArray());
        }, out var failure, out _), failure);
        return Assert.Single(records);
    }

    [Theory]
    [InlineData(BuiltInParameter.HOST_AREA_COMPUTED, "read-only-parameter")]
    [InlineData(BuiltInParameter.ALL_MODEL_TYPE_NAME, "unsupported-parameter")]
    public void CliRefusesUnverifiedParameters(BuiltInParameter parameter, string errorCode)
    {
        using var error = new StringWriter();
        int exit = SetParameterCommand.Run([CreateSource(), Output, "70", parameter.ToString(), "123"], TextWriter.Null, error);
        Assert.Equal(SaveAsCommand.Refused, exit);
        Assert.Contains(errorCode, error.ToString());
        Assert.False(File.Exists(Output));
    }

    private static (int Exit, string Output, string Error) RunCli(Func<TextWriter, TextWriter, int> command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = command(output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private string Manifest(string json)
    {
        string path = Path.Combine(_folder, "edits.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string Id(BuiltInParameter parameter) => ((int)parameter).ToString(CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("a.rvt|b.rvt|70|71")]
    [InlineData("a.rvt|b.rvt|70|71|v|extra")]
    [InlineData(" |b.rvt|70|71|v")]
    [InlineData("a.rvt|\t|70|71|v")]
    [InlineData("a.rvt|b.rvt| |71|v")]
    [InlineData("a.rvt|b.rvt|abc|71|v")]
    [InlineData("a.rvt|b.rvt|0|71|v")]
    [InlineData("a.rvt|b.rvt|-5|71|v")]
    [InlineData("a.rvt|b.rvt|70.5|71|v")]
    [InlineData("a.rvt|b.rvt|70|0|v")]
    [InlineData("a.rvt|b.rvt|70|NOT_A_PARAMETER|v")]
    [InlineData("a.rvt|b.rvt|70|client_name|v")]
    [InlineData("a.rvt|b.rvt|70|ALL_MODEL_MARK,ALL_MODEL_COST|v")]
    public void SetParameterReportsUsageForMalformedArguments(string joined)
    {
        var (exit, output, error) = RunCli((o, e) => SetParameterCommand.Run(joined.Split('|'), o, e));
        Assert.Equal(RvtExitCodes.Usage, exit);
        Assert.Contains("Usage: rvtio set-parameter <source.rvt> <output.rvt> <element-id>", error);
        Assert.Equal("", output);
    }

    [Theory]
    [InlineData("a.rfa|b.rfa|Type|71")]
    [InlineData("a.rfa|b.rfa|Type|71|v|extra")]
    [InlineData("a.rfa|b.rfa| |71|v")]
    [InlineData(" |b.rfa|Type|71|v")]
    [InlineData("a.rfa|b.rfa|Type|0|v")]
    [InlineData("a.rfa|b.rfa|Type|NOT_A_PARAMETER|v")]
    [InlineData("a.rfa|b.rfa|Type|ALL_MODEL_MARK,ALL_MODEL_COST|v")]
    public void SetFamilyParameterReportsUsageForMalformedArguments(string joined)
    {
        var (exit, output, error) = RunCli((o, e) => SetParameterCommand.Run(joined.Split('|'), o, e, familyFile: true));
        Assert.Equal(RvtExitCodes.Usage, exit);
        Assert.Contains("Usage: rvtio set-family-parameter <source.rfa> <output.rfa> <type-name>", error);
        Assert.Equal("", output);
    }

    [Theory]
    [InlineData("a.rvt|b.rvt")]
    [InlineData("a.rvt|b.rvt|edits.json|extra")]
    [InlineData(" |b.rvt|edits.json")]
    [InlineData("a.rvt| |edits.json")]
    [InlineData("a.rvt|b.rvt| ")]
    public void EditParametersReportsUsageForMalformedArguments(string joined)
    {
        var (exit, output, error) = RunCli((o, e) => SetParameterCommand.RunBatch(joined.Split('|'), o, e));
        Assert.Equal(RvtExitCodes.Usage, exit);
        Assert.Contains("Usage: rvtio edit-parameters <source.rvt|source.rfa> <output.rvt|output.rfa> <edits.json>", error);
        Assert.Equal("", output);
    }

    [Theory]
    [InlineData("""{"elementId":70,"parameterId":71,"value":"x"}""", "Expected a non-empty array")]
    [InlineData("[]", "Expected a non-empty array")]
    [InlineData("[1]", "Edit 1 must have")]
    [InlineData("""[{"elementId":70,"parameterId":71}]""", "Edit 1 must have")]
    [InlineData("""[{"elementId":70,"parameterId":71,"value":"x","extra":1}]""", "Edit 1 must have")]
    [InlineData("""[{"elementId":70,"parameterId":71,"value":5}]""", "Edit 1 must have")]
    [InlineData("""[{"elementId":70,"parameterId":"71","value":"x"}]""", "Edit 1 must have")]
    [InlineData("""[{"elementId":70,"parameterId":0,"value":"x"}]""", "Edit 1 must have")]
    [InlineData("""[{"elementId":70,"parameterId":71.5,"value":"x"}]""", "Edit 1 must have")]
    [InlineData("""[{"elementId":0,"parameterId":71,"value":"x"}]""", "Edit 1 requires a positive integer elementId")]
    [InlineData("""[{"elementId":-3,"parameterId":71,"value":"x"}]""", "Edit 1 requires a positive integer elementId")]
    [InlineData("""[{"elementId":"70","parameterId":71,"value":"x"}]""", "Edit 1 requires a positive integer elementId")]
    [InlineData("""[{"elementId":70.5,"parameterId":71,"value":"x"}]""", "Edit 1 requires a positive integer elementId")]
    [InlineData("""[{"typeName":"A","parameterId":71,"value":"x"}]""", "Edit 1 requires a positive integer elementId")]
    [InlineData("""[{"elementId":70,"parameterId":71,"value":"x"},{"elementId":0,"parameterId":71,"value":"x"}]""",
        "Edit 2 requires a positive integer elementId")]
    [InlineData("[{", "")]
    [InlineData("", "")]
    public void EditParametersRefusesAMalformedProjectManifestBeforeWriting(string json, string message)
    {
        string source = CreateSource();
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var (exit, output, error) = RunCli((o, e) => SetParameterCommand.RunBatch([source, Output, Manifest(json)], o, e));
        Assert.Equal(RvtExitCodes.Usage, exit);
        Assert.StartsWith("error: ", error);
        Assert.Contains(message, error);
        Assert.Equal("", output);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData("""{"typeName":"A","parameterId":71,"value":"x"}""", "Expected a non-empty array")]
    [InlineData("[]", "Expected a non-empty array")]
    [InlineData("""[{"typeName":"A","parameterId":71}]""", "Edit 1 must have")]
    [InlineData("""[{"typeName":"A","parameterId":71,"value":"x","extra":1}]""", "Edit 1 must have")]
    [InlineData("""[{"typeName":"A","parameterId":0,"value":"x"}]""", "Edit 1 must have")]
    [InlineData("""[{"typeName":"A","parameterId":71,"value":5}]""", "Edit 1 must have")]
    [InlineData("""[{"typeName":" ","parameterId":71,"value":"x"}]""", "Edit 1 requires a family typeName")]
    [InlineData("""[{"typeName":5,"parameterId":71,"value":"x"}]""", "Edit 1 requires a family typeName")]
    [InlineData("""[{"elementId":70,"parameterId":71,"value":"x"}]""", "Edit 1 requires a family typeName")]
    [InlineData("""[{"typeName":"A","parameterId":71,"value":"x"},{"typeName":"","parameterId":71,"value":"x"}]""",
        "Edit 2 requires a family typeName")]
    public void EditParametersRefusesAMalformedFamilyManifestBeforeWriting(string json, string message)
    {
        string project = CreateSource();
        string source = Path.ChangeExtension(project, ".rfa");
        File.Move(project, source);
        string output = Path.Combine(_folder, "output.rfa");
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var (exit, stdout, error) = RunCli((o, e) => SetParameterCommand.RunBatch([source, output, Manifest(json)], o, e));
        Assert.Equal(RvtExitCodes.Usage, exit);
        Assert.StartsWith("error: ", error);
        Assert.Contains(message, error);
        Assert.Equal("", stdout);
        Assert.False(File.Exists(output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    // The manifest is read by the source extension; the writer then refuses an output of the other kind.
    [Theory]
    [InlineData("source.rfa", "output.rvt", """[{"typeName":"A","parameterId":71,"value":"x"}]""")]
    [InlineData("source.rvt", "output.rfa", """[{"elementId":70,"parameterId":71,"value":"x"}]""")]
    public void EditParametersReadsTheManifestByTheSourceExtension(string source, string output, string json)
    {
        var (exit, _, error) = RunCli((o, e) => SetParameterCommand.RunBatch(
            [Path.Combine(_folder, source), Path.Combine(_folder, output), Manifest(json)], o, e));
        Assert.Equal(SaveAsCommand.Refused, exit);
        Assert.StartsWith("error [unsupported-parameter-file]: ", error);
        Assert.False(File.Exists(Path.Combine(_folder, output)));
    }

    [Fact]
    public void EditParametersReportsAMissingManifestAsAnInputError()
    {
        var (exit, _, error) = RunCli((o, e) => SetParameterCommand.RunBatch([CreateSource(), Output, Path.Combine(_folder, "none.json")], o, e));
        Assert.Equal(RvtExitCodes.InputError, exit);
        Assert.StartsWith("error: ", error);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void EditParametersAppliesAProjectManifestIncludingANullClear()
    {
        var parameter = new UserParameter(Kind.Text, true, HideWhenNoValue: true);
        string source = CreateSource(wall: true, user: parameter);
        var (exit, output, error) = RunCli((o, e) => SetParameterCommand.RunBatch([source, Output, Manifest("""
            [
              {"elementId":70,"parameterId":71,"value":null},
              {"elementId":70,"parameterId":71,"value":"Restored"}
            ]
            """)], o, e));
        Assert.True(exit == RvtExitCodes.Success, error);
        Assert.Equal("Saved: output.rvt" + Environment.NewLine, output);
        using (var root = RootStorage.OpenRead(Output))
            Assert.Equal(UserBody(parameter, "Restored"), ReadBody(root));

        string cleared = Path.Combine(_folder, "cleared.rvt");
        var clear = RunCli((o, e) => SetParameterCommand.RunBatch([source, cleared, Manifest("""[{"elementId":70,"parameterId":71,"value":null}]""")], o, e));
        Assert.True(clear.Exit == RvtExitCodes.Success, clear.Error);
        using var clearedRoot = RootStorage.OpenRead(cleared);
        Assert.Equal(UserBody(parameter, cleared: true), ReadBody(clearedRoot));
    }

    [Fact]
    public void EditParametersRefusesTheWholeProjectBatchWhenALaterEditIsRefused()
    {
        string source = CreateSource();
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        string manifest = Manifest("[{\"elementId\":70,\"parameterId\":" + Id(BuiltInParameter.CLIENT_NAME) + ",\"value\":\"Staged\"},"
            + "{\"elementId\":70,\"parameterId\":" + Id(BuiltInParameter.HOST_AREA_COMPUTED) + ",\"value\":\"1\"}]");
        var (exit, output, error) = RunCli((o, e) => SetParameterCommand.RunBatch([source, Output, manifest], o, e));
        Assert.Equal(SaveAsCommand.Refused, exit);
        Assert.StartsWith("error [read-only-parameter]: ", error);
        Assert.Equal("", output);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData("Owner", "Saved: output.rvt (value already matched)")]
    [InlineData("Changed", "Saved: output.rvt")]
    public void SetParameterReportsWhetherTheValueAlreadyMatched(string value, string expected)
    {
        string source = CreateSource();
        var (exit, output, error) = RunCli((o, e) => SetParameterCommand.Run([source, Output, "70", "CLIENT_NAME", value], o, e));
        Assert.True(exit == RvtExitCodes.Success, error);
        Assert.Equal(expected + Environment.NewLine, output);
    }

    [Fact]
    public void SetParameterReportsInputProblemsAsInputErrors()
    {
        string source = CreateSource();
        File.WriteAllText(Output, "occupied");
        var existing = RunCli((o, e) => SetParameterCommand.Run([source, Output, "70", "CLIENT_NAME", "New"], o, e));
        Assert.Equal(RvtExitCodes.InputError, existing.Exit);
        Assert.StartsWith("error: ", existing.Error);
        Assert.Equal("occupied", File.ReadAllText(Output));

        var missing = RunCli((o, e) => SetParameterCommand.Run([Path.Combine(_folder, "none.rvt"), Path.Combine(_folder, "o2.rvt"), "70", "CLIENT_NAME", "New"], o, e));
        Assert.Equal(RvtExitCodes.InputError, missing.Exit);
        Assert.StartsWith("error: ", missing.Error);
    }

    [Fact]
    public void AFamilyBatchRefusesAProjectContainer()
    {
        string project = CreateSource();
        string source = Path.ChangeExtension(project, ".rfa");
        File.Move(project, source);
        string output = Path.Combine(_folder, "output.rfa");
        string manifest = Manifest("[{\"typeName\":\"A\",\"parameterId\":" + Id(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS) + ",\"value\":\"x\"}]");
        var (exit, _, error) = RunCli((o, e) => SetParameterCommand.RunBatch([source, output, manifest], o, e));
        Assert.Equal(SaveAsCommand.Refused, exit);
        Assert.StartsWith("error [unsupported-parameter-file]: ", error);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void SaveAsRefusesAnotherDirectoryWhenAnImageLinkIsRelative()
    {
        string source = CreateSource(image: new(OwnerKind.Wall, LinkedPath: "picture.png"));
        string output = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "moved")).FullName, "output.rvt");
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(source, output));
        Assert.Equal("relative-reference-relocation", refused.Kind);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void SaveAsRefusesAnotherDirectoryWhenAnImageLinkCannotBeVerified()
    {
        string source = CreateSource(image: new(OwnerKind.Wall, LinkedPath: "picture.png", PathType: "Server"));
        string output = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "moved")).FullName, "output.rvt");
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(source, output));
        Assert.Equal("relative-reference-relocation", refused.Kind);
        Assert.Contains("could not be verified", refused.Message);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("Relative to Central Model", false)]
    [InlineData("Server", false)]
    [InlineData("Absolute", true)]
    public void SaveAsInTheSameDirectoryNeverInspectsImageLinks(string pathType, bool absolute)
    {
        string path = absolute ? Path.Combine(_folder, "picture.png") : "picture.png";
        string source = CreateSource(image: new(OwnerKind.Wall, LinkedPath: path, PathType: pathType));
        RvtWriter.SaveAs(source, Output);
        Assert.True(File.Exists(Output));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveAsAcceptsAnotherDirectoryForAnAbsoluteLinkAndAnImportedImage(bool linked)
    {
        var image = linked ? new ProjectImage(OwnerKind.Wall, LinkedPath: Path.Combine(_folder, "picture.png"), PathType: "Absolute")
            : new ProjectImage(OwnerKind.Wall);
        string source = CreateSource(image: image);
        string output = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "moved")).FullName, "output.rvt");
        RvtWriter.SaveAs(source, output);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void RefusesANegativeIdOutsideTheBuiltInRange()
    {
        long id = (int)BuiltInParameter.CLIENT_NAME - (1L << 32);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(CreateSource(), Output, ProjectId, id, "New"));
        Assert.Equal("unsupported-parameter", error.Kind);
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(71, "Owner", false, "missing-element")]
    [InlineData(ProjectId, "stale", false, "unverified-parameter-storage")]
    [InlineData(ProjectId, "Owner", true, "unverified-parameter-storage")]
    public void RefusesAmbiguousStorage(int id, string xmlValue, bool duplicate, string errorCode)
    {
        var exception = Assert.Throws<RvtWriteRefusedException>(() =>
            RvtWriter.SetParameter(CreateSource(xmlValue, duplicate), Output, id, BuiltInParameter.CLIENT_NAME, "New"));
        Assert.Equal(errorCode, exception.Kind);
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, "group membership")]
    [InlineData(false, true, "global parameter")]
    public void WritesWallCommentsOnlyWithoutUnverifiedDependencies(bool grouped, bool global, string? reason)
    {
        string source = CreateSource(wall: true, grouped: grouped, global: global);
        if (reason is null)
        {
            var result = RvtWriter.SetParameter(source, Output, ProjectId, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "New");
            Assert.Equal(1, result.ReplacedStreamCount);
            Assert.Equal(1, result.RewrittenRecordCount);
            return;
        }

        var refused = Assert.Throws<RvtWriteRefusedException>(() =>
            RvtWriter.SetParameter(source, Output, ProjectId, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "New"));
        Assert.Equal("unverified-parameter-storage", refused.Kind);
        Assert.Contains(reason, refused.Message);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void WritesAnUnassociatedParameterOnAWallWithAGlobalAssociation()
    {
        var association = new ProjectAssociation(BuiltInParameter.WALL_BASE_OFFSET);
        string source = CreateSource(wall: true, globalParameter: new(Kind.Number), association: association);
        var result = RvtWriter.SetParameter(source, Output, ProjectId, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "New");

        Assert.Equal(1, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        using var original = RootStorage.OpenRead(source);
        Assert.Equal(Body("New", wall: true, association: association), ReadBody(root));
        Assert.Equal(ReadBody(original, DefinitionId), ReadBody(root, DefinitionId));
    }

    [Fact]
    public void RefusesTheEntireBatchWhenOneRequestedParameterIsGloballyDriven()
    {
        const BuiltInParameter property = BuiltInParameter.ALL_MODEL_MARK;
        var association = new ProjectAssociation(property);
        string source = CreateSource(wall: true, globalParameter: new(Kind.Text), association: association);
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Staged"), new(ProjectId, (int)property, "Rejected")]));

        Assert.Equal("read-only-parameter", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void RefusesAnOrdinaryWriteWhenTheElementHasAnUnverifiedAssociationLayout()
    {
        var association = new ProjectAssociation(BuiltInParameter.WALL_BASE_OFFSET, Malformed: true);
        string source = CreateSource(wall: true, globalParameter: new(Kind.Number), association: association);
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() =>
            RvtWriter.SetParameter(source, Output, ProjectId, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Rejected"));

        Assert.Equal("unverified-parameter-storage", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData("Replacement")]
    [InlineData("")]
    public void WritesAGlobalValueAndItsControlledProjectPropertyInOrder(string finalValue)
    {
        var association = new ProjectAssociation((BuiltInParameter)DefinitionId, GlobalId: PropertyGlobalId);
        var user = new UserParameter(Kind.Text, Shared: true);
        var global = new GlobalFixture(Kind.Text, InitialValue: "Old");
        string source = CreateSource(wall: true, user: user, globalParameter: global, association: association);
        var result = RvtWriter.SetParameters(source, Output,
            [new(PropertyGlobalId, PropertyGlobalId, "Intermediate"), new(PropertyGlobalId, PropertyGlobalId, finalValue)]);

        Assert.Equal(2, result.RewrittenRecordCount);
        using var output = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(user, finalValue, association: association), ReadBody(output));
        Assert.Equal(GlobalBody(global, finalValue, PropertyGlobalId), ReadBody(output, PropertyGlobalId));
    }

    [Theory]
    [InlineData("", "First")]
    [InlineData("Old", "Replacement")]
    [InlineData("Old", "")]
    public void WritesAGlobalTextValueAndItsControlledWallComments(string initial, string value)
    {
        var association = new ProjectAssociation(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        var global = new GlobalFixture(Kind.Text, InitialValue: initial);
        string source = CreateSource(xmlValue: initial, wall: true, globalParameter: global, association: association);
        var result = RvtWriter.SetParameters(source, Output,
            [new(DefinitionId, DefinitionId, "Intermediate"), new(DefinitionId, DefinitionId, value)]);

        Assert.Equal(2, result.RewrittenRecordCount);
        using var output = RootStorage.OpenRead(Output);
        Assert.Equal(Body(value, wall: true, association: association), ReadBody(output));
        Assert.Equal(GlobalBody(global, value), ReadBody(output, DefinitionId));
    }

    [Theory]
    [InlineData(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, Kind.Text, "Different", "unverified-parameter-storage")]
    [InlineData(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, Kind.Number, "0", "unverified-parameter-storage")]
    [InlineData(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, Kind.Text, "Owner", "unsupported-parameter-owner")]
    [InlineData(BuiltInParameter.ALL_MODEL_MARK, Kind.Text, "Owner", "unverified-parameter-storage")]
    [InlineData(BuiltInParameter.ALL_MODEL_URL, Kind.Text, "Owner", "unverified-parameter-storage")]
    public void RefusesAnUnverifiedBuiltInAssociation(BuiltInParameter parameter, Kind kind, string initial, string error)
    {
        var association = new ProjectAssociation(parameter);
        string source = CreateSource(wall: true, globalParameter: new(kind, InitialValue: initial), association: association);
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output,
            DefinitionId, DefinitionId, kind == Kind.Text ? "New" : "2"));

        Assert.Equal(error, refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(2020)]
    [InlineData(2021)]
    public void RefusesAGlobalAssociationToInstanceCommentsInOlderFormats(int release)
    {
        var association = new ProjectAssociation(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        string source = CreateSource(wall: true, release: release, globalParameter: new(Kind.Text, InitialValue: "Owner"), association: association);
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, DefinitionId, DefinitionId, "New"));

        Assert.Equal("unverified-parameter-storage", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_GenericModel)]
    [InlineData(BuiltInCategory.OST_Doors)]
    [InlineData(BuiltInCategory.OST_Windows)]
    [InlineData(BuiltInCategory.OST_Furniture)]
    public void WritesAGlobalCurrencyValueAndItsControlledFamilyCost(BuiltInCategory category)
    {
        var global = new GlobalFixture(Kind.Number, Currency: true, InitialValue: "17.25");
        var association = new ProjectAssociation(BuiltInParameter.ALL_MODEL_COST, GlobalId: PropertyGlobalId);
        var family = new FamilyFixture(Category: category, OnlyPrimary: true, TypedCost: 17.25);
        string source = CreateSource(family: family, globalParameter: global, association: association);
        var result = RvtWriter.SetParameters(source, Output,
            [new(PropertyGlobalId, PropertyGlobalId, "0"), new(PropertyGlobalId, PropertyGlobalId, "58.625")]);

        Assert.Equal(2, result.RewrittenRecordCount);
        using var output = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", association: association, associationSlot: true, typedCost: 58.625), ReadBody(output));
        Assert.Equal(GlobalBody(global, "58.625", PropertyGlobalId), ReadBody(output, PropertyGlobalId));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Doors, BuiltInParameter.FIRE_RATING, "60 min", "90 min")]
    [InlineData(BuiltInCategory.OST_Windows, BuiltInParameter.ALL_MODEL_MANUFACTURER, "", "New manufacturer")]
    [InlineData(BuiltInCategory.OST_Furniture, BuiltInParameter.ALL_MODEL_MODEL, "Old model", "")]
    [InlineData(BuiltInCategory.OST_Furniture, BuiltInParameter.ALL_MODEL_COST, "17.25", "58.625")]
    public void WritesAnIdentityValueInTheNamedFamilyStoreThroughAGlobal(
        BuiltInCategory category, BuiltInParameter parameter, string initial, string replacement)
    {
        bool cost = parameter == BuiltInParameter.ALL_MODEL_COST;
        var global = new GlobalFixture(cost ? Kind.Number : Kind.Text, InitialValue: initial, Currency: cost);
        var association = new ProjectAssociation(parameter, GlobalId: PropertyGlobalId);
        var identity = new StoredIdentity(parameter, initial);
        var family = new FamilyFixture(Category: category, OnlyPrimary: true, Identity: identity);
        string source = CreateSource(family: family, globalParameter: global, association: association);
        var result = RvtWriter.SetParameter(source, Output, PropertyGlobalId, PropertyGlobalId, replacement);

        Assert.Equal(2, result.RewrittenRecordCount);
        using var output = RootStorage.OpenRead(Output);
        using var original = RootStorage.OpenRead(source);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", identity: identity with { Value = replacement },
            association: association, associationSlot: true), ReadBody(output));
        Assert.Equal(ReadBody(original, FamilyId), ReadBody(output, FamilyId));
        Assert.Equal(GlobalBody(global, replacement, PropertyGlobalId), ReadBody(output, PropertyGlobalId));
    }

    [Theory]
    [InlineData(false, "Different")]
    [InlineData(true, "Stored")]
    public void RefusesAStaleOrAmbiguousIdentityValueControlledByAGlobal(bool competingTypedCopy, string initial)
    {
        const BuiltInParameter parameter = BuiltInParameter.ALL_MODEL_TYPE_COMMENTS;
        var identity = new StoredIdentity(parameter, "Stored", competingTypedCopy);
        var family = new FamilyFixture(Category: BuiltInCategory.OST_Furniture, OnlyPrimary: true, Identity: identity);
        var association = new ProjectAssociation(parameter, GlobalId: PropertyGlobalId);
        string source = CreateSource(family: family, globalParameter: new(Kind.Text, InitialValue: initial), association: association);
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() =>
            RvtWriter.SetParameter(source, Output, PropertyGlobalId, PropertyGlobalId, "Replacement"));

        Assert.Equal("unverified-parameter-storage", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_GenericModel, false, false)]
    [InlineData(BuiltInCategory.OST_GenericModel, true, false)]
    [InlineData(BuiltInCategory.OST_GenericModel, false, true)]
    [InlineData(BuiltInCategory.OST_Doors, false, false)]
    [InlineData(BuiltInCategory.OST_Windows, true, false)]
    [InlineData(BuiltInCategory.OST_Furniture, false, true)]
    public void AGlobalIdentityAssignmentChangesOnlyTheAssociatedSymbols(BuiltInCategory category, bool derived, bool both)
    {
        const BuiltInParameter parameter = BuiltInParameter.ALL_MODEL_TYPE_COMMENTS;
        var identity = new StoredIdentity(parameter, "Stored");
        var association = new ProjectAssociation(parameter, GlobalId: PropertyGlobalId, Derived: derived, PrimaryAndDerived: both);
        var family = new FamilyFixture(Category: category, Identity: identity, DerivedType: true);
        string source = CreateSource(family: family, globalParameter: new(Kind.Text, InitialValue: identity.Value), association: association);
        var result = RvtWriter.SetParameter(source, Output, PropertyGlobalId, PropertyGlobalId, "New");

        Assert.Equal(both ? 3 : 2, result.RewrittenRecordCount);
        using var output = RootStorage.OpenRead(Output);
        using var original = RootStorage.OpenRead(source);
        int unchanged = derived ? ProjectId : DerivedId;
        int target = derived ? DerivedId : ProjectId;
        if (both)
            Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", identity: identity with { Value = "New" },
                association: association, associationSlot: true), ReadBody(output, DerivedId));
        else
            Assert.Equal(ReadBody(original, (ulong)unchanged), ReadBody(output, (ulong)unchanged));
        Assert.Equal(SymbolBody(target, derived ? ProjectId : -1, "Old", identity: identity with { Value = "New" },
            association: association, associationSlot: true), ReadBody(output, (ulong)target));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Doors, false, false)]
    [InlineData(BuiltInCategory.OST_Windows, true, false)]
    [InlineData(BuiltInCategory.OST_Furniture, false, true)]
    public void AGlobalCurrencyAssignmentChangesOnlyTheAssociatedSymbolCosts(BuiltInCategory category, bool derived, bool both)
    {
        const double before = 17.25;
        const double after = 58.625;
        var association = new ProjectAssociation(BuiltInParameter.ALL_MODEL_COST, GlobalId: PropertyGlobalId, Derived: derived, PrimaryAndDerived: both);
        var global = new GlobalFixture(Kind.Number, Currency: true, InitialValue: "17.25");
        string source = CreateSource(family: new FamilyFixture(Category: category, DerivedType: true, TypedCost: before),
            globalParameter: global, association: association);
        var result = RvtWriter.SetParameters(source, Output,
            [new(PropertyGlobalId, PropertyGlobalId, "0"), new(PropertyGlobalId, PropertyGlobalId, "58.625")]);

        Assert.Equal(both ? 3 : 2, result.RewrittenRecordCount);
        using var output = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", association: derived ? null : association, associationSlot: true,
            typedCost: derived ? before : after), ReadBody(output));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", association: derived || both ? association : null, associationSlot: true,
            typedCost: derived || both ? after : before), ReadBody(output, DerivedId));
        Assert.Equal(GlobalBody(global, "58.625", PropertyGlobalId), ReadBody(output, PropertyGlobalId));
    }

    [Theory]
    [InlineData(false, "unverified-parameter-storage")]
    [InlineData(true, "read-only-parameter")]
    public void RefusesUnsafeGlobalCostBatches(bool currency, string error)
    {
        var global = new GlobalFixture(Kind.Number, Currency: currency, InitialValue: "17.25");
        var association = new ProjectAssociation(BuiltInParameter.ALL_MODEL_COST, GlobalId: PropertyGlobalId);
        var family = new FamilyFixture(Category: BuiltInCategory.OST_GenericModel, OnlyPrimary: true, TypedCost: 17.25);
        string source = CreateSource(family: family, globalParameter: global, association: association);
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(PropertyGlobalId, PropertyGlobalId, "58.625"), new(ProjectId, (int)BuiltInParameter.ALL_MODEL_COST, "12")]));

        Assert.Equal(error, refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void RefusesAGlobalWriteWhenTheControlledPropertyDisagreesWithTheSavedValue()
    {
        var association = new ProjectAssociation((BuiltInParameter)DefinitionId, GlobalId: PropertyGlobalId);
        var global = new GlobalFixture(Kind.Text, InitialValue: "Different");
        string source = CreateSource(wall: true, user: new(Kind.Text, Shared: true), globalParameter: global, association: association);
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, PropertyGlobalId, PropertyGlobalId, "New"));

        Assert.Equal("unverified-parameter-storage", refused.Kind);
        Assert.Contains("controlled property disagrees", refused.Message);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(OwnerKind.Wall, 1, true)]
    [InlineData(OwnerKind.WallType, 2, true)]
    [InlineData(OwnerKind.Room, 1, true)]
    [InlineData(OwnerKind.Wall, 1, false)]
    [InlineData(OwnerKind.WallType, 2, false)]
    [InlineData(OwnerKind.Room, 1, false)]
    public void AGlobalCurrencyValueUpdatesAVerifiedProjectParameter(OwnerKind owner, int binding, bool shared)
    {
        var parameter = new UserParameter(Kind.Number, shared, Binding: binding, Owner: owner, Currency: true);
        var association = new ProjectAssociation((BuiltInParameter)DefinitionId, GlobalId: PropertyGlobalId);
        var global = new GlobalFixture(Kind.Number, InitialValue: "1.5", Currency: true);
        string source = CreateSource(wall: true, user: parameter, globalParameter: global, association: association);
        var result = RvtWriter.SetParameters(source, Output,
            [new(PropertyGlobalId, PropertyGlobalId, "-82.625"), new(PropertyGlobalId, PropertyGlobalId, "14.25")]);

        Assert.Equal(2, result.RewrittenRecordCount);
        using var output = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, "14.25", association: association), ReadBody(output));
        Assert.Equal(GlobalBody(global, "14.25", PropertyGlobalId), ReadBody(output, PropertyGlobalId));
    }

    [Fact]
    public void RefusesACurrencyAssociationToANumberDefinition()
    {
        var association = new ProjectAssociation((BuiltInParameter)DefinitionId, GlobalId: PropertyGlobalId);
        string source = CreateSource(wall: true, user: new(Kind.Number, true), association: association,
            globalParameter: new(Kind.Number, InitialValue: "1.5", Currency: true));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output,
            PropertyGlobalId, PropertyGlobalId, "14.25"));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(2022, true, true)]
    [InlineData(2022, true, false)]
    [InlineData(2022, false, true)]
    [InlineData(2021, false, true)]
    [InlineData(2020, false, true)]
    public void SlopeAndNumberAssociationsUseTheStoredRatio(int release, bool slopeDriver, bool slopeProperty)
    {
        const string slope = "autodesk.spec.aec:slope-2.0.0";
        var parameter = new UserParameter(Kind.Number, true,
            DefinitionSpec: slopeProperty ? (UnitType.UT_Slope, slope) : null);
        var association = new ProjectAssociation((BuiltInParameter)DefinitionId, GlobalId: PropertyGlobalId);
        var global = new GlobalFixture(Kind.Number, InitialValue: "1.5", SpecId: slopeDriver ? slope : null);
        string source = CreateSource(wall: true, user: parameter, association: association, globalParameter: global, release: release);

        RvtWriter.SetParameters(source, Output,
            [new(PropertyGlobalId, PropertyGlobalId, slopeDriver ? "-62.5 %" : "-0.625"),
             new(PropertyGlobalId, PropertyGlobalId, slopeDriver ? "25 %" : "0.25")]);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, "0.25", association: association), ReadBody(root, release: release));
        Assert.Equal(GlobalBody(global, "0.25", PropertyGlobalId, release: release), ReadBody(root, PropertyGlobalId, release));
    }

    [Theory]
    [InlineData(2020)]
    [InlineData(2021)]
    public void LegacyProjectBindingsRefuseSlopeGlobalsOnNumberParameters(int release)
    {
        var association = new ProjectAssociation((BuiltInParameter)DefinitionId, GlobalId: PropertyGlobalId);
        string source = CreateSource(wall: true, user: new(Kind.Number, true), association: association, release: release,
            globalParameter: new(Kind.Number, InitialValue: "1.5", SpecId: "autodesk.spec.aec:slope-2.0.0"));
        byte[] before = File.ReadAllBytes(source);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Staged"), new(PropertyGlobalId, PropertyGlobalId, "25 %")]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Contains("different types", error.Message);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void AForceAssociationToSlopeRefusesTheEntireBatch()
    {
        var parameter = new UserParameter(Kind.Number, true, DefinitionSpec: (UnitType.UT_Slope, "autodesk.spec.aec:slope-2.0.0"));
        var association = new ProjectAssociation((BuiltInParameter)DefinitionId, GlobalId: PropertyGlobalId);
        string source = CreateSource(wall: true, user: parameter, association: association,
            globalParameter: new(Kind.Number, InitialValue: "1.5", SpecId: "autodesk.spec.aec.structural:force-2.0.0"));
        byte[] before = File.ReadAllBytes(source);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Staged"), new(PropertyGlobalId, PropertyGlobalId, "25 kN")]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Contains("different types", error.Message);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void AGlobalFamilyInstanceInputRecalculatesItsLocalResult()
    {
        var association = new ProjectAssociation((BuiltInParameter)DefinitionId, GlobalId: PropertyGlobalId);
        var global = new GlobalFixture(Kind.Text, InitialValue: "Old");
        string source = CreateSource(family: new FamilyFixture(Instance: true, Formula: true), globalParameter: global, association: association);
        var result = RvtWriter.SetParameter(source, Output, PropertyGlobalId, PropertyGlobalId, "New");

        Assert.Equal(2, result.RewrittenRecordCount);
        using var output = RootStorage.OpenRead(Output);
        using var original = RootStorage.OpenRead(source);
        Assert.Equal(InstanceBody("New", formulaResult: "New", association: association), ReadBody(output));
        Assert.Equal(ReadBody(original, MasterId), ReadBody(output, MasterId));
        Assert.Equal(ReadBody(original, DerivedId), ReadBody(output, DerivedId));
    }

    [Fact]
    public void AGlobalFamilyTypeInputRecalculatesThePrimaryTypeWithoutRewritingDerivedCopies()
    {
        var association = new ProjectAssociation((BuiltInParameter)DefinitionId, GlobalId: PropertyGlobalId);
        var global = new GlobalFixture(Kind.Text, InitialValue: "Old");
        string source = CreateSource(family: new FamilyFixture(Formula: true), globalParameter: global, association: association);
        var result = RvtWriter.SetParameter(source, Output, PropertyGlobalId, PropertyGlobalId, "New");

        Assert.Equal(2, result.RewrittenRecordCount);
        using var output = RootStorage.OpenRead(Output);
        using var original = RootStorage.OpenRead(source);
        Assert.Equal(SymbolBody(ProjectId, -1, "New", formula: true, association: association, associationSlot: true), ReadBody(output));
        Assert.Equal(ReadBody(original, DerivedId), ReadBody(output, DerivedId));
    }

    [Fact]
    public void RefusesAGlobalWriteWhenTheControlledFamilyCacheIsStale()
    {
        var association = new ProjectAssociation((BuiltInParameter)DefinitionId, GlobalId: PropertyGlobalId);
        string source = CreateSource(family: new FamilyFixture(Instance: true, Formula: true),
            globalParameter: new(Kind.Text, InitialValue: "Stale"), association: association);
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() =>
            RvtWriter.SetParameter(source, Output, PropertyGlobalId, PropertyGlobalId, "New"));

        Assert.Equal("unverified-parameter-storage", refused.Kind);
        Assert.Contains("controlled family property disagrees", refused.Message);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(BuiltInParameter.ALL_MODEL_MARK, null, "First")]
    [InlineData(BuiltInParameter.ALL_MODEL_MARK, "Old", "")]
    [InlineData(BuiltInParameter.ALL_MODEL_TYPE_MARK, null, "")]
    [InlineData(BuiltInParameter.ALL_MODEL_TYPE_MARK, "Old", "Replacement")]
    [InlineData(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, null, "")]
    [InlineData(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, null, "")]
    [InlineData(BuiltInParameter.ALL_MODEL_DESCRIPTION, "Existing", "Description")]
    [InlineData(BuiltInParameter.ALL_MODEL_MANUFACTURER, null, "Manufacturer")]
    [InlineData(BuiltInParameter.ALL_MODEL_MODEL, "Existing", "Model")]
    [InlineData(BuiltInParameter.ALL_MODEL_URL, null, "https://example.invalid/")]
    [InlineData(BuiltInParameter.FIRE_RATING, "1 h", "2 h")]
    [InlineData(BuiltInParameter.ALL_MODEL_COST, null, "58.625")]
    [InlineData(BuiltInParameter.ALL_MODEL_COST, "17.25", "58.625")]
    public void WritesBuiltInIdentityParameters(BuiltInParameter parameter, string? before, string value)
    {
        string source = CreateSource(builtIn: parameter, builtInValue: before);
        var result = RvtWriter.SetParameters(source, Output, [new(ProjectId, (int)parameter, value)]);
        Assert.Equal(1, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(BuiltInBody(parameter, value), ReadBody(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusesACostChangeThatRevitMaySuppress(bool loadedFamily)
    {
        const string suppressed = "17.25000000001";
        var identity = new StoredIdentity(BuiltInParameter.ALL_MODEL_COST, "17.25");
        string source = loadedFamily
            ? CreateSource(family: new FamilyFixture(Category: BuiltInCategory.OST_GenericModel, OnlyPrimary: true, Identity: identity))
            : CreateSource(builtIn: BuiltInParameter.ALL_MODEL_COST, builtInValue: "17.25");
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_COST, suppressed)]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Contains("comparison tolerance", error.Message);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));

        // A visible change is still accepted.
        RvtWriter.SetParameters(source, Output, [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_COST, "17.26")]);
        Assert.True(File.Exists(Output));
    }

    [Fact]
    public void RejectsAWholeMarkBatchForAnInapplicableOwner()
    {
        string source = CreateSource(builtIn: BuiltInParameter.ALL_MODEL_MARK);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_MARK, "First"),
             new(ProjectId, (int)BuiltInParameter.ALL_MODEL_TYPE_MARK, "Wrong owner")]));
        Assert.Equal("unsupported-parameter-owner", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(BuiltInParameter.WALL_USER_HEIGHT_PARAM, "12.75 ft", "unverified-parameter-storage")]
    [InlineData(BuiltInParameter.WALL_BASE_OFFSET, "-2 ft", "unverified-parameter-storage")]
    [InlineData(BuiltInParameter.WALL_TOP_OFFSET, "2 ft", "unverified-parameter-storage")]
    [InlineData(BuiltInParameter.WALL_USER_HEIGHT_PARAM, null, "unsupported-parameter-clear")]
    [InlineData(BuiltInParameter.WALL_BASE_OFFSET, null, "unsupported-parameter-clear")]
    [InlineData(BuiltInParameter.WALL_TOP_OFFSET, null, "unsupported-parameter-clear")]
    public void RefusesAWholeBatchWhenAWallDimensionCannotBeWritten(BuiltInParameter parameter, string? value, string errorCode)
    {
        string source = CreateSource(builtIn: BuiltInParameter.ALL_MODEL_MARK);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_MARK, "New mark"),
             new(ProjectId, (int)parameter, value)]));
        Assert.Equal(errorCode, refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("1,250")]
    [InlineData("100 USD")]
    public void RejectsAWholeBatchForInvalidCost(string cost)
    {
        string source = CreateSource(builtIn: BuiltInParameter.ALL_MODEL_COST);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, "Staged"),
             new(ProjectId, (int)BuiltInParameter.ALL_MODEL_COST, cost)]));
        Assert.Equal("invalid-parameter-value", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(true, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)]
    [InlineData(false, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)]
    public void RefusesBuiltInIdentityOnAnUnverifiedFamilyCategory(bool instance, BuiltInParameter parameter)
    {
        string source = CreateSource(family: new(Instance: instance, Category: BuiltInCategory.OST_PlumbingFixtures));
        var refused = Assert.Throws<RvtWriteRefusedException>(() =>
            RvtWriter.SetParameter(source, Output, ProjectId, parameter, "Comment"));
        Assert.Equal("unverified-parameter-storage", refused.Kind);
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Doors, null, "Первый комментарий")]
    [InlineData(BuiltInCategory.OST_Windows, "Existing", "Replacement")]
    [InlineData(BuiltInCategory.OST_Furniture, null, "")]
    public void WritesInstanceCommentsOnVerifiedFamilyCategories(BuiltInCategory category, string? before, string value)
    {
        string source = CreateSource(family: new(Instance: true, Category: category, InstanceComment: before));
        var result = RvtWriter.SetParameter(source, Output, ProjectId, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, value);
        Assert.Equal(1, result.RewrittenRecordCount);

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(InstanceBody("Old", BindStore.Value, value, (int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), ReadBody(root));
    }

    [Theory]
    [InlineData(Kind.Text, "Text with new length & unicode ✓", false)]
    [InlineData(Kind.Text, "Old", true)]
    [InlineData(Kind.Integer, "-17", true)]
    [InlineData(Kind.YesNo, "0", true)]
    [InlineData(Kind.Number, "-2.25", true)]
    [InlineData(Kind.Number, "-2.25", true, 2020)]
    [InlineData(Kind.Number, "-2.25", true, 2021)]
    public void WritesUserParameterByDefinitionId(Kind kind, string value, bool shared, int release = 2022)
    {
        var parameter = new UserParameter(kind, shared);
        string source = CreateSource(wall: true, user: parameter, release: release);
        using var error = new StringWriter();
        int exit = SetParameterCommand.Run([source, Output, "70", "71", value], TextWriter.Null, error);
        Assert.True(exit == RvtExitCodes.Success, error.ToString());
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, value), ReadBody(root, release: release));
    }

    [Theory]
    [InlineData(2020)]
    [InlineData(2021)]
    public void RequiresAMatchingUnitForMeasurements(int release)
    {
        string source = CreateSource(wall: true, user: new(Kind.Number, true, Measured: true), release: release);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, "2 m2"));
        Assert.Equal("invalid-parameter-value", error.Kind);
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(2020)]
    [InlineData(2021)]
    [InlineData(2022)]
    public void ConvertsAnExplicitLengthToInternalFeet(int release)
    {
        var parameter = new UserParameter(Kind.Number, true, Measured: true);
        string source = CreateSource(wall: true, user: parameter, release: release);
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, "609.6 mm");
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, "2"), ReadBody(root, release: release));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetsAMaterialInTheRootReferenceStore(bool first)
    {
        var parameter = new UserParameter(Kind.Material, true, Store: first ? StoreState.NullPointer : StoreState.Value);
        string source = CreateSource(wall: true, user: parameter);
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, TargetMaterialId.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, TargetMaterialId.ToString(CultureInfo.InvariantCulture)), ReadBody(root));
    }

    [Theory]
    [InlineData("70")]
    [InlineData("999")]
    [InlineData("-1")]
    public void RefusesReferencesToAnythingOtherThanAnExistingMaterial(string target)
    {
        string source = CreateSource(wall: true, user: new(Kind.Material, true));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, target));
        Assert.Equal("invalid-parameter-value", error.Kind);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void WritesTheRightNumberWhenTheSchemaStoresTheIdFirst()
    {
        var parameter = new UserParameter(Kind.Number, true, IdFirst: true);
        string source = CreateSource(wall: true, user: parameter);
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, "8.625");
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, "8.625"), ReadBody(root));
    }

    [Theory]
    [InlineData(Kind.Text, "First text ✓", false, false)]
    [InlineData(Kind.Integer, "-17", false, false)]
    [InlineData(Kind.YesNo, "1", true, false)]
    [InlineData(Kind.Number, "8.625", true, false)]
    [InlineData(Kind.Number, "8.625", true, true)]
    public void AssignsFirstValueWithOrWithoutAnExistingStore(Kind kind, string value, bool populated, bool idFirst)
    {
        var parameter = new UserParameter(kind, true, IdFirst: idFirst, Store: populated ? StoreState.Neighbour : StoreState.NullPointer);
        string source = CreateSource(wall: true, user: parameter);
        var result = RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value);
        Assert.Equal(1, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, value), ReadBody(root));
    }

    [Theory]
    [InlineData(2020, OwnerKind.Wall, 1, true, "0")]
    [InlineData(2022, OwnerKind.WallType, 2, true, "1e-300")]
    [InlineData(2022, OwnerKind.Room, 1, true, "0")]
    [InlineData(2020, OwnerKind.Wall, 1, false, "0")]
    [InlineData(2022, OwnerKind.WallType, 2, false, "1e-300")]
    [InlineData(2022, OwnerKind.Room, 1, false, "0")]
    public void AssignsAnExplicitFirstCurrencyValue(int release, OwnerKind owner, int binding, bool shared, string value)
    {
        var parameter = new UserParameter(Kind.Number, shared, Binding: binding, Store: StoreState.NullPointer,
            Owner: owner, Currency: true);
        string source = CreateSource(wall: true, user: parameter, release: release);
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value);
        using var output = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, value), ReadBody(output, release: release));
    }

    [Theory]
    [InlineData(2020, UnitType.UT_Force, "autodesk.spec.aec.structural:force-2.0.0", "13.5 kN", "44291.33858267716")]
    [InlineData(2022, UnitType.UT_Mass, "autodesk.spec.aec.structural:mass-2.0.0", "12.5 kg", "12.5")]
    [InlineData(2022, UnitType.UT_MassDensity, "autodesk.spec.aec:massDensity-2.0.0", "14.5 kg/m³", "0.4105942755840001")]
    [InlineData(2022, UnitType.UT_Speed, "autodesk.spec.aec:speed-2.0.0", "15.5 m/s", "50.85301837270341")]
    [InlineData(2020, UnitType.UT_Slope, "autodesk.spec.aec:slope-2.0.0", "16.5 %", "0.165")]
    [InlineData(2020, UnitType.UT_TimeInterval, "autodesk.spec.aec:time-2.0.0", "17.5 s", "17.5")]
    public void ConvertsAndReplacesAnEngineeringMeasurement(int release, UnitType legacy, string forge, string input, string internalValue)
    {
        var parameter = new UserParameter(Kind.Number, true, Store: StoreState.NullPointer, DefinitionSpec: (legacy, forge));
        string source = CreateSource(wall: true, user: parameter, release: release);
        RvtWriter.SetParameters(source, Output, [new(ProjectId, DefinitionId, input), new(ProjectId, DefinitionId, "-" + input)]);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, "-" + internalValue), ReadBody(root, release: release));
    }

    [Theory]
    [InlineData("12.5 kN", "invalid-parameter-value")]
    [InlineData("12.50000000001 kg", "unverified-parameter-storage")]
    public void RefusesTheBatchWhenAnEngineeringMeasurementIsInvalidOrAmbiguous(string value, string kind)
    {
        var parameter = new UserParameter(Kind.Number, true,
            DefinitionSpec: (UnitType.UT_Mass, "autodesk.spec.aec.structural:mass-2.0.0"));
        string source = CreateSource(wall: true, user: parameter);
        byte[] before = File.ReadAllBytes(source);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, DefinitionId, "12.5 kg"), new(ProjectId, DefinitionId, value)]));
        Assert.Equal(kind, error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(OwnerKind.Wall, true, "17.25000000001", "unverified-parameter-storage")]
    [InlineData(OwnerKind.Wall, true, "42 $", "invalid-parameter-value")]
    [InlineData(OwnerKind.Wall, true, null, "unsupported-parameter-clear")]
    [InlineData(OwnerKind.Room, true, "17.25000000001", "unverified-parameter-storage")]
    [InlineData(OwnerKind.Room, true, "42 $", "invalid-parameter-value")]
    [InlineData(OwnerKind.Room, true, null, "unsupported-parameter-clear")]
    [InlineData(OwnerKind.Wall, false, "17.25000000001", "unverified-parameter-storage")]
    [InlineData(OwnerKind.Wall, false, "42 $", "invalid-parameter-value")]
    [InlineData(OwnerKind.Wall, false, null, "unsupported-parameter-clear")]
    public void RefusesInvalidCurrencyBatchesWithoutChangingTheProject(OwnerKind owner, bool shared, string? value, string kind)
    {
        string source = CreateSource(wall: true, user: new(Kind.Number, shared, Owner: owner, Currency: true));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, DefinitionId, "17.25"), new(ProjectId, DefinitionId, value)]));
        Assert.Equal(kind, error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(OwnerKind.WallType, 2, true)]
    [InlineData(OwnerKind.Room, 1, true)]
    [InlineData(OwnerKind.WallType, 1, false)]
    [InlineData(OwnerKind.Room, 2, false)]
    public void FirstValueRequiresTheRightBindingForTheOwner(OwnerKind owner, int binding, bool writable)
    {
        var parameter = new UserParameter(Kind.Text, false, Binding: binding, Store: StoreState.NullPointer, Owner: owner);
        string source = CreateSource(wall: true, user: parameter);
        if (!writable)
        {
            var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, "First"));
            Assert.Contains("different instance or type", error.Message);
            Assert.False(File.Exists(Output));
            return;
        }

        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, "First");
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, "First"), ReadBody(root));
    }

    [Theory]
    [InlineData(false, "", false, "invalid-parameter-value")]
    [InlineData(true, "New", false, "read-only-parameter")]
    [InlineData(false, "New", true, "unverified-parameter-storage")]
    public void RefusesUnsafeFirstAssignments(bool readOnly, string value, bool unsorted, string code)
    {
        var parameter = new UserParameter(Kind.Text, true, ReadOnly: readOnly,
            Store: unsorted ? StoreState.Unsorted : StoreState.NullPointer);
        string source = CreateSource(wall: true, user: parameter);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value));
        Assert.Equal(code, error.Kind);
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(Kind.Text, "New", true, 1, false, false, "read-only-parameter")]
    [InlineData(Kind.Text, "New", false, 2, false, false, "unverified-parameter-storage")]
    [InlineData(Kind.Text, "New", false, 1, true, false, "unverified-parameter-storage")]
    [InlineData(Kind.Text, "New", false, 1, false, true, "unverified-parameter-storage")]
    [InlineData(Kind.Integer, "101", false, 1, false, false, "invalid-parameter-value")]
    [InlineData(Kind.YesNo, "2", false, 1, false, false, "invalid-parameter-value")]
    [InlineData(Kind.Number, "NaN", false, 1, false, false, "invalid-parameter-value")]
    public void RefusesUnsafeUserParameterEdits(Kind kind, string value, bool readOnly, int binding, bool grouped, bool global, string code)
    {
        string source = CreateSource(wall: true, grouped: grouped, global: global, user: new UserParameter(kind, true, readOnly, binding));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value));
        Assert.Equal(code, refused.Kind);
        Assert.False(File.Exists(Output));
    }

    public enum Kind { Text, Integer, YesNo, Number, Material, FamilyType }
    public enum OwnerKind : ushort { Wall = 22, WallType = 50, Room = 51 }
    private enum StoreState { Value, NullPointer, Neighbour, Unsorted }
    private sealed record UserParameter(Kind Kind, bool Shared, bool ReadOnly = false, int Binding = 1, bool Measured = false,
        bool IdFirst = false, StoreState Store = StoreState.Value, OwnerKind Owner = OwnerKind.Wall, bool HideWhenNoValue = false,
        bool Currency = false, (UnitType Legacy, string Forge)? DefinitionSpec = null);
    private sealed record ProjectImage(OwnerKind Owner, int? Stored = null, bool BrokenHolder = false, int DesignOption = -1,
        string? LinkedPath = null, string PathType = "Relative to Central Model");
    private sealed record GlobalFixture(Kind Kind, bool Formula = false, bool Reporting = false, bool Consumer = false, string? InitialValue = null,
        bool Currency = false, string? SpecId = null);
    private sealed record ProjectAssociation(BuiltInParameter Property, bool Malformed = false, int GlobalId = DefinitionId,
        bool Derived = false, bool PrimaryAndDerived = false);
    private sealed record GlobalFormulaFixture(int Operation = 3, bool Stale = false, bool Consumer = false,
        bool IntegerResult = false, bool Conditional = false, int Function = 0, bool CurrencyResult = false,
        string? SpecId = null, string? ConstantSpecId = null, GlobalBinary? Binary = null, double? Cached = null);
    private sealed record GlobalBinary(int Operation, int Parameter, double Constant, bool ConstantFirst = false, string? SpecId = null);
    private const int GlobalFormulaResultId = 73;
    private const int GlobalFormulaConsumerId = 74;
    private const int PropertyGlobalId = 79;
    private sealed record FamilyFixture(bool Instance = false, bool Inherited = false, bool Trivial = true, bool Diverged = false,
        bool Reporting = false, bool ProjectBound = false, bool SameDefinition = false, bool DerivedType = false, bool DerivedBoundCopy = false,
        bool BoundReadOnly = false, bool HideWhenNoValue = true, bool BoundValue = true, BuiltInCategory Category = BuiltInCategory.OST_Doors,
        bool OnlyPrimary = false, StoredIdentity? Identity = null, string? InstanceComment = null,
        bool Formula = false, bool FormulaTrivial = true, string? InstanceFormulaCache = null, NumericFamilyFormula? NumericFormula = null,
        double? TypedCost = null, bool SharedBinding = true, NestedReference? Reference = null, OrdinaryTarget? ProjectTarget = null,
        bool BoundFamilyType = false);
    private sealed record NestedReference(int Value = -1, string TargetFamily = "Nested", BuiltInCategory TargetCategory = BuiltInCategory.OST_GenericModel,
        bool ReadOnly = false);
    private sealed record OrdinaryTarget(int MappedId = TargetMaterialId, int SavedReference = -1,
        BuiltInCategory Category = BuiltInCategory.OST_GenericModel, bool Active = true, bool DependentSymbol = false,
        int? LinkedFamilyMap = null, bool ContainsOwner = false, bool OwnerHasProxy = true);
    private sealed record NumericFamilyFormula(int Input = 8, int Operation = 3, double? CurrencyInput = null);
    private sealed record StoredIdentity(BuiltInParameter Parameter, string Value, bool CompetingTypedCopy = false);
    private const int FamilyId = 73, MasterId = 74, DerivedId = 75;
    private const int BoundParameterId = 76;
    private const int FamilyFormulaResultId = 77;
    private const int OriginalMaterialId = 80, TargetMaterialId = 81;
    private const int NestedTypeId = 82;
    private const int ProjectTargetFamilyId = 83, ProjectTargetSymbolId = 84, DependentTargetSymbolId = 85;
    private const int LinkedFamilyId = 86, LinkedSymbolId = 87;
    private const int TargetLevelId = 88;
    private const int ImageId = 89, RasterHolderId = 90;

    [Theory]
    [InlineData(OwnerKind.Wall, null, ImageId)]
    [InlineData(OwnerKind.WallType, -1, ImageId)]
    [InlineData(OwnerKind.Room, ImageId, -1)]
    [InlineData(OwnerKind.Room, null, -1)]
    public void WritesTheBuiltInProjectImage(OwnerKind owner, int? stored, int value)
    {
        var image = new ProjectImage(owner, stored);
        string source = CreateSource(image: image);
        RvtWriter.SetParameter(source, Output, ProjectId, ImageParameter(owner), value.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(ProjectImageBody(image with { Stored = value }), ReadBody(root));
    }

    [Fact]
    public void WritesALevelIdInTheBuiltInProjectImage()
    {
        var image = new ProjectImage(OwnerKind.Room, Stored: ImageId);
        string source = CreateSource(targetLevelDesignOption: -1, image: image);
        RvtWriter.SetParameter(source, Output, ProjectId, ImageParameter(image.Owner), TargetLevelId.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(ProjectImageBody(image with { Stored = TargetLevelId }), ReadBody(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritesALinkedImageWhenItsPathKeepsTheSameTarget(bool absolute)
    {
        string imagePath = absolute ? Path.Combine(_folder, "picture.png") : "picture.png";
        var image = new ProjectImage(OwnerKind.Wall, LinkedPath: imagePath, PathType: absolute ? "Absolute" : "Relative to Central Model");
        string source = CreateSource(image: image);
        string output = absolute ? Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "moved")).FullName, "output.rvt") : Output;
        RvtWriter.SetParameter(source, output, ProjectId, ImageParameter(image.Owner), ImageId.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(output);
        Assert.Equal(ProjectImageBody(image with { Stored = ImageId }), ReadBody(root));
    }

    [Fact]
    public void RefusesToRelocateAProjectWhenAnImagePathWouldResolveDifferently()
    {
        string source = CreateSource(image: new(OwnerKind.Wall, LinkedPath: "picture.png"));
        string output = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "moved")).FullName, "output.rvt");
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_MARK, "Staged"), new(ProjectId, (int)BuiltInParameter.ALL_MODEL_IMAGE, "-1")]));
        Assert.Equal("relative-reference-relocation", refused.Kind);
        Assert.False(File.Exists(output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData("89", true, -1, "invalid-parameter-value")]
    [InlineData("89", false, 91, "invalid-parameter-value")]
    [InlineData(null, false, -1, "unsupported-parameter-clear")]
    public void AnInvalidProjectImageRejectsTheEntireBatch(string? value, bool brokenHolder, int designOption, string expectedKind)
    {
        string source = CreateSource(image: new(OwnerKind.Wall, BrokenHolder: brokenHolder, DesignOption: designOption));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_MARK, "Staged"), new(ProjectId, (int)BuiltInParameter.ALL_MODEL_IMAGE, value)]));
        Assert.Equal(expectedKind, refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void RefusesToWriteTypeImageOnALoadedFamilySymbol()
    {
        string source = CreateSource(family: new(OnlyPrimary: true, Category: BuiltInCategory.OST_GenericModel));
        var refused = Assert.Throws<RvtWriteRefusedException>(() =>
            RvtWriter.SetParameter(source, Output, ProjectId, BuiltInParameter.ALL_MODEL_TYPE_IMAGE, "-1"));
        Assert.Equal("unsupported-parameter-owner", refused.Kind);
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(OwnerKind.Wall, 1, false, NestedTypeId)]
    [InlineData(OwnerKind.WallType, 2, true, TargetMaterialId)]
    [InlineData(OwnerKind.Room, 1, true, -1)]
    public void WritesAProjectBoundFamilyTypeReference(OwnerKind owner, int binding, bool hasValue, int target)
    {
        var parameter = new UserParameter(Kind.FamilyType, Shared: true, Binding: binding,
            Store: hasValue ? StoreState.Value : StoreState.NullPointer, Owner: owner);
        string source = CreateSource(user: parameter);
        string value = target.ToString(CultureInfo.InvariantCulture);
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, value), ReadBody(root));
    }

    [Theory]
    [InlineData(OwnerKind.Wall, 1, false, BuiltInCategory.OST_GenericModel)]
    [InlineData(OwnerKind.WallType, 2, true, BuiltInCategory.OST_Doors)]
    [InlineData(OwnerKind.Room, 1, true, BuiltInCategory.OST_Doors)]
    public void WritesAnOrdinaryFamilyTypeReferenceInASharedProjectBinding(OwnerKind owner, int binding, bool hasValue, BuiltInCategory target)
    {
        var parameter = new UserParameter(Kind.FamilyType, Shared: true, Binding: binding,
            Store: hasValue ? StoreState.Value : StoreState.NullPointer, Owner: owner);
        string source = CreateSource(user: parameter, projectTarget: new(MappedId: NestedTypeId, Category: target));
        string value = ProjectTargetSymbolId.ToString(CultureInfo.InvariantCulture);
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, value), ReadBody(root));
    }

    [Theory]
    [InlineData(false, false, ProjectId)]
    [InlineData(false, true, ProjectTargetSymbolId)]
    [InlineData(true, false, MasterId)]
    [InlineData(true, true, ProjectTargetSymbolId)]
    public void WritesSharedProjectFamilyTypeReferencesOnLoadedFamilies(bool instance, bool existing, int target)
    {
        var family = new FamilyFixture(Instance: instance, ProjectBound: true, OnlyPrimary: true, BoundValue: existing,
            Reference: new(), ProjectTarget: new(ContainsOwner: true), BoundFamilyType: true);
        string source = CreateSource(family: family);
        RvtWriter.SetParameter(source, Output, ProjectId, BoundParameterId, target.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(instance ? InstanceBody("Old", reference: -1, primary: true, referenceStore: true, boundReference: target)
            : SymbolBody(ProjectId, -1, "Old", reference: -1, active: true, referenceStore: true, boundReference: target), ReadBody(root));
    }

    [Theory]
    [InlineData(OwnerKind.Wall, 1, false)]
    [InlineData(OwnerKind.WallType, 2, true)]
    [InlineData(OwnerKind.Room, 1, true)]
    public void WritesALocalLevelInASharedProjectFamilyTypeBinding(OwnerKind owner, int binding, bool existing)
    {
        var parameter = new UserParameter(Kind.FamilyType, Shared: true, Binding: binding,
            Store: existing ? StoreState.Value : StoreState.NullPointer, Owner: owner);
        string source = CreateSource(user: parameter, targetLevelDesignOption: -1);
        string value = TargetLevelId.ToString(CultureInfo.InvariantCulture);
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, value), ReadBody(root));
    }

    [Theory]
    [InlineData(true, 601)]
    [InlineData(false, -1)]
    public void RefusesAnUnverifiedLevelTargetForTheEntireBatch(bool shared, int targetLevelDesignOption)
    {
        string source = CreateSource(user: new UserParameter(Kind.FamilyType, Shared: shared),
            targetLevelDesignOption: targetLevelDesignOption);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_MARK, "Staged"),
             new(ProjectId, DefinitionId, TargetLevelId.ToString(CultureInfo.InvariantCulture))]));
        Assert.Equal("invalid-parameter-value", error.Kind);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void RefusesAnInactiveTargetForASharedProjectFamilyTypeBindingAtomically()
    {
        string source = CreateSource(user: new UserParameter(Kind.FamilyType, Shared: true), projectTarget: new(Active: false));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_MARK, "Staged"),
             new(ProjectId, DefinitionId, ProjectTargetSymbolId.ToString(CultureInfo.InvariantCulture))]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Contains("activating and regenerating", error.Message);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void AProjectBindingWithoutASharedDefinitionStillChecksFamilyDependencies()
    {
        string source = CreateSource(user: new UserParameter(Kind.FamilyType, Shared: false), projectTarget: new(MappedId: NestedTypeId));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId,
            ProjectTargetSymbolId.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void ClearsAProjectBoundFamilyTypeReferenceWithHideWhenNoValue()
    {
        var parameter = new UserParameter(Kind.FamilyType, Shared: true, HideWhenNoValue: true);
        string source = CreateSource(user: parameter);
        RvtWriter.SetParameters(source, Output, [new(ProjectId, DefinitionId, null)]);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(UserBody(parameter, cleared: true), ReadBody(root));
    }

    [Theory]
    [InlineData("9999999", "invalid-parameter-value")]
    [InlineData(null, "unsupported-parameter-clear")]
    public void RefusesTheEntireBatchWhenAProjectBoundFamilyTypeEditIsInvalid(string? value, string kind)
    {
        string source = CreateSource(user: new UserParameter(Kind.FamilyType, Shared: true));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_MARK, "Staged"), new(ProjectId, DefinitionId, value)]));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(-1, NestedTypeId)]
    [InlineData(NestedTypeId, -1)]
    [InlineData(NestedTypeId, TargetMaterialId)]
    public void WritesAReferenceToThePrimaryFamilyType(int oldValue, int value)
    {
        string source = CreateSource(family: new FamilyFixture(OnlyPrimary: true, Reference: new(oldValue)));
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", reference: value), ReadBody(root));
    }

    [Fact]
    public void WritesTheFirstLocalFamilyTypeReferenceOnAnInstance()
    {
        string source = CreateSource(family: new FamilyFixture(Instance: true, Inherited: true, OnlyPrimary: true, Reference: new()));
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, NestedTypeId.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(InstanceBody("", reference: NestedTypeId, primary: true), ReadBody(root));
        Assert.Equal(SymbolBody(MasterId, -1, "Old", instanceDefault: true, reference: -1), ReadBody(root, MasterId));
    }

    [Fact]
    public void UpdatesTheFamilyTypeReferenceOnThePrimaryAndDerivedSymbols()
    {
        string source = CreateSource(family: new FamilyFixture(Reference: new()));
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, NestedTypeId.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", reference: NestedTypeId), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", reference: NestedTypeId), ReadBody(root, DerivedId));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(NestedTypeId)]
    public void WritesAnOrdinaryProjectFamilyTypeReference(int original)
    {
        string source = CreateSource(family: new FamilyFixture(Reference: new(original), ProjectTarget: new()));
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, ProjectTargetSymbolId.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", reference: ProjectTargetSymbolId, active: true), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", reference: ProjectTargetSymbolId, active: true), ReadBody(root, DerivedId));
    }

    [Theory]
    [InlineData(LinkedFamilyId, -1)]
    [InlineData(LinkedSymbolId, -1)]
    [InlineData(TargetMaterialId, LinkedSymbolId)]
    public void WritesAProjectFamilyTypeReferenceWithAnUnrelatedDependency(int mappedId, int savedReference)
    {
        var target = new OrdinaryTarget(mappedId, savedReference, LinkedFamilyMap: TargetMaterialId);
        string source = CreateSource(family: new FamilyFixture(Reference: new(), ProjectTarget: target));
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, ProjectTargetSymbolId.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", reference: ProjectTargetSymbolId, active: true), ReadBody(root));
    }

    [Fact]
    public void ReadsFamilyDependenciesFromRecordsAlreadyStagedInABatch()
    {
        var target = new OrdinaryTarget(LinkedFamilyMap: TargetMaterialId);
        string source = CreateSource(family: new FamilyFixture(Reference: new(), ProjectTarget: target));
        using var root = RootStorage.OpenRead(source);
        var schema = TfSchemaReader.Read(root)!;
        var formats = new FormatsSchema(schema);
        var table = PagedGzipStream.Read(root, "Global", "ElemTable")!;
        var ids = ElemTableLayout.TryParse(table, formats)!.Select(row => row.Id).ToHashSet();
        var pending = new ParameterWriter.Pending();
        Assert.Empty(ProjectFamilyReference.Read(root, Tags(2022), sizeof(int), schema, ids,
            ProjectTargetSymbolId, ProjectTargetFamilyId, pending).Dependencies);

        byte[] staged = SymbolBody(ProjectTargetSymbolId, -1, "Target", reference: LinkedSymbolId, familyId: ProjectTargetFamilyId, active: true);
        pending.Apply(new ParameterWriter.PlanResult([new(0, 102, ProjectTargetSymbolId, staged)], null));
        Assert.Equal(LinkedFamilyId, Assert.Single(ProjectFamilyReference.Read(root, Tags(2022), sizeof(int), schema, ids,
            ProjectTargetSymbolId, ProjectTargetFamilyId, pending).Dependencies));
    }

    [Theory]
    [InlineData(FamilyId, "dependency on the family being edited")]
    [InlineData(9999999, "dependencies could not be verified")]
    public void RefusesTheBatchWhenAnIndirectFamilyDependencyIsUnverified(int mappedId, string message)
    {
        var target = new OrdinaryTarget(LinkedFamilyId, LinkedFamilyMap: mappedId);
        string source = CreateSource(family: new FamilyFixture(Instance: true, ProjectBound: true, Reference: new(), ProjectTarget: target));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, BoundParameterId, "Staged"), new(ProjectId, DefinitionId, ProjectTargetSymbolId.ToString(CultureInfo.InvariantCulture))]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Contains(message, error.Message);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(TargetMaterialId)]
    [InlineData(NestedTypeId)]
    public void WritesAnIndependentTypeReferenceToATypeContainingTheSharedOwner(int original)
    {
        string source = CreateSource(family: new FamilyFixture(Reference: new(original), ProjectTarget: new(ContainsOwner: true)));
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, ProjectTargetSymbolId.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", reference: ProjectTargetSymbolId, active: true), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", reference: ProjectTargetSymbolId, active: true), ReadBody(root, DerivedId));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void RefusesAnEntireBatchForACircularReferenceOutsideTheVerifiedTypeScope(bool instance, bool ownerHasProxy, bool trivial)
    {
        var target = new OrdinaryTarget(ContainsOwner: true, OwnerHasProxy: ownerHasProxy);
        string source = CreateSource(family: new FamilyFixture(Instance: instance, ProjectBound: true, Trivial: trivial,
            Reference: new(), ProjectTarget: target));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, BoundParameterId, "Staged"), new(ProjectId, DefinitionId, ProjectTargetSymbolId.ToString(CultureInfo.InvariantCulture))]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(FamilyId, -1, BuiltInCategory.OST_GenericModel, "unverified-parameter-storage")]
    [InlineData(9999999, -1, BuiltInCategory.OST_GenericModel, "unverified-parameter-storage")]
    [InlineData(TargetMaterialId, ProjectId, BuiltInCategory.OST_GenericModel, "unverified-parameter-storage")]
    [InlineData(TargetMaterialId, -1, BuiltInCategory.OST_Furniture, "invalid-parameter-value")]
    public void RefusesTheBatchForAnUnverifiedProjectFamilyTypeTarget(int mappedId, int savedReference, BuiltInCategory category, string kind)
    {
        string source = CreateSource(family: new FamilyFixture(Reference: new(), ProjectTarget: new(mappedId, savedReference, category)));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, DefinitionId, NestedTypeId.ToString(CultureInfo.InvariantCulture)),
             new(ProjectId, DefinitionId, ProjectTargetSymbolId.ToString(CultureInfo.InvariantCulture))]));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void RejectsAProjectFamilyTypeTargetThatNeedsActivation()
    {
        string source = CreateSource(family: new FamilyFixture(Reference: new(), ProjectTarget: new(Active: false)));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId,
            ProjectTargetSymbolId.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Contains("activating and regenerating", error.Message);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void RejectsAProjectFamilyTypeTargetWhenAnotherTypeOfItsFamilyReferencesTheOwner()
    {
        string source = CreateSource(family: new FamilyFixture(Reference: new(), ProjectTarget: new(DependentSymbol: true)));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId,
            ProjectTargetSymbolId.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void WritesTheFirstLocalReferenceForAnInstanceWithADerivedSymbol()
    {
        string source = CreateSource(family: new FamilyFixture(Instance: true, Inherited: true, Reference: new()));
        RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, NestedTypeId.ToString(CultureInfo.InvariantCulture));
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(InstanceBody("", reference: NestedTypeId), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, MasterId, "Old", instanceDefault: true, reference: -1), ReadBody(root, DerivedId));
    }

    [Fact]
    public void RefusesTheEntireFamilyTypeBatchForAnIndependentDerivedEdit()
    {
        string source = CreateSource(family: new FamilyFixture(DerivedType: true, Reference: new()));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        string value = NestedTypeId.ToString(CultureInfo.InvariantCulture);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, DefinitionId, value), new(DerivedId, DefinitionId, value)]));
        Assert.Equal("unsupported-parameter", error.Kind);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void RefusesToOverwriteAnUnverifiedDerivedFamilyTypeReference()
    {
        string source = CreateSource(family: new FamilyFixture(Diverged: true, Reference: new()));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        string value = NestedTypeId.ToString(CultureInfo.InvariantCulture);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData("9999999", "Nested", BuiltInCategory.OST_GenericModel, false, "invalid-parameter-value")]
    [InlineData("82", "Host", BuiltInCategory.OST_GenericModel, false, "invalid-parameter-value")]
    [InlineData("82", "Nested", BuiltInCategory.OST_Furniture, false, "invalid-parameter-value")]
    [InlineData("82", "Nested", BuiltInCategory.OST_GenericModel, true, "read-only-parameter")]
    [InlineData(null, "Nested", BuiltInCategory.OST_GenericModel, false, "unsupported-parameter-clear")]
    public void RefusesTheEntireBatchForAnInvalidFamilyTypeReference(string? value, string nestedName,
        BuiltInCategory nestedCategory, bool readOnly, string kind)
    {
        var comments = new StoredIdentity(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, "Original");
        string source = CreateSource(family: new FamilyFixture(OnlyPrimary: true, Identity: comments,
            Reference: new(TargetFamily: nestedName, TargetCategory: nestedCategory, ReadOnly: readOnly)));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)comments.Parameter, "Staged"), new(ProjectId, DefinitionId, value)]));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Doors, BuiltInParameter.FIRE_RATING, "90 min")]
    [InlineData(BuiltInCategory.OST_Windows, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, "")]
    [InlineData(BuiltInCategory.OST_GenericModel, BuiltInParameter.ALL_MODEL_COST, "14.125")]
    public void WritesTheLocalNamedValueOfALoadedPrimaryType(BuiltInCategory category, BuiltInParameter parameter, string value)
    {
        var initial = new StoredIdentity(parameter, parameter == BuiltInParameter.ALL_MODEL_COST ? "17.25" : "Template");
        string source = CreateSource(family: new FamilyFixture(Category: category, OnlyPrimary: true, Identity: initial));
        var result = RvtWriter.SetParameter(source, Output, ProjectId, parameter, value);
        Assert.Equal(1, result.RewrittenRecordCount);

        using var root = RootStorage.OpenRead(Output);
        using var original = RootStorage.OpenRead(source);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", identity: initial with { Value = value }), ReadBody(root));
        Assert.Equal(ReadBody(original, FamilyId), ReadBody(root, FamilyId));
    }

    [Fact]
    public void RefusesAnEntireLoadedTypeBatchWhenTheIdentityHasTwoStores()
    {
        var identity = new StoredIdentity(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, "Template", CompetingTypedCopy: true);
        string source = CreateSource(family: new FamilyFixture(OnlyPrimary: true, Identity: identity));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)BuiltInParameter.ALL_MODEL_MODEL, "Staged"), new(ProjectId, (int)identity.Parameter, "Rejected")]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Contains("competing typed store", error.Message);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_GenericModel, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, "Template", "Updated")]
    [InlineData(BuiltInCategory.OST_Doors, BuiltInParameter.FIRE_RATING, "60 min", "90 min")]
    [InlineData(BuiltInCategory.OST_Windows, BuiltInParameter.ALL_MODEL_DESCRIPTION, "Template", "")]
    [InlineData(BuiltInCategory.OST_Furniture, BuiltInParameter.ALL_MODEL_COST, "17.25", "29.125")]
    public void UpdatesAnImportedIdentityAndMirrorsTheVerifiedDerivedSymbol(BuiltInCategory category,
        BuiltInParameter parameter, string before, string after)
    {
        var identity = new StoredIdentity(parameter, before);
        string source = CreateSource(family: new FamilyFixture(Category: category, DerivedType: true, Identity: identity));
        var result = RvtWriter.SetParameter(source, Output, ProjectId, identity.Parameter, after);
        Assert.Equal(2, result.RewrittenRecordCount);

        using var root = RootStorage.OpenRead(Output);
        var expected = identity with { Value = after };
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", identity: expected), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", identity: expected), ReadBody(root, DerivedId));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_GenericModel)]
    [InlineData(BuiltInCategory.OST_Doors)]
    [InlineData(BuiltInCategory.OST_Windows)]
    [InlineData(BuiltInCategory.OST_Furniture)]
    public void RejectsADirectNamedIdentityEditOnADerivedSymbol(BuiltInCategory category)
    {
        var identity = new StoredIdentity(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, "Template");
        string source = CreateSource(family: new FamilyFixture(Category: category, DerivedType: true, Identity: identity));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, (int)identity.Parameter, "Staged"), new(DerivedId, (int)identity.Parameter, "Rejected")]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Doors)]
    [InlineData(BuiltInCategory.OST_Windows)]
    [InlineData(BuiltInCategory.OST_Furniture)]
    public void ProjectIdentityAssignmentsKeepSeparatePrimaryAndDerivedValues(BuiltInCategory category)
    {
        string source = CreateSource(family: new FamilyFixture(Category: category, DerivedType: true));
        int cost = (int)BuiltInParameter.ALL_MODEL_COST;
        var result = RvtWriter.SetParameters(source, Output,
            [new(ProjectId, cost, "17.25"), new(DerivedId, cost, "21.125"), new(ProjectId, cost, "32.125")]);
        Assert.Equal(2, result.RewrittenRecordCount);

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", typedCost: 32.125), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", typedCost: 21.125), ReadBody(root, DerivedId));
    }

    [Theory]
    [InlineData(false, false, 2)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    public void WritesLoadedFamilyValuesAndMirrorsOnlyTheType(bool instance, bool inherited, int count)
    {
        string source = CreateSource(family: new FamilyFixture(instance, inherited));
        const string value = "Family text & value";
        var result = RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value);
        Assert.Equal(count, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(instance ? InstanceBody(value) : SymbolBody(ProjectId, -1, value), ReadBody(root));
        Assert.Equal(instance ? SymbolBody(DerivedId, MasterId, "Old", true) : SymbolBody(DerivedId, ProjectId, value),
            ReadBody(root, DerivedId));
    }

    [Theory]
    [InlineData(false, false, false, "independent of geometry")]
    [InlineData(true, true, false, "disagrees with its master")]
    [InlineData(true, false, true, "is reporting")]
    public void RefusesLoadedFamilyValuesWithUnverifiedDependencies(bool trivial, bool diverged, bool reporting, string reason)
    {
        string source = CreateSource(family: new FamilyFixture(Trivial: trivial, Diverged: diverged, Reporting: reporting));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, "New"));
        Assert.Contains(reason, refused.Message);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void RecalculatesAFormulaOnThePrimaryAndItsDerivedSymbol()
    {
        string source = CreateSource(family: new FamilyFixture(Formula: true));
        const string value = "Changed formula input";
        var result = RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value);
        Assert.Equal(2, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, value, formula: true), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, value, formula: true), ReadBody(root, DerivedId));
        using var original = RootStorage.OpenRead(source);
        Assert.Equal(ReadBody(original, FamilyId), ReadBody(root, FamilyId));
    }

    [Theory]
    [InlineData("99", 50)]
    [InlineData("-99", -49)]
    public void ResolvesAFormulaResultToItsWritableInputAndMirrorsTheType(string requested, int input)
    {
        string source = CreateSource(family: new FamilyFixture(Formula: true, NumericFormula: new()));
        var result = RvtWriter.SetParameter(source, Output, ProjectId, FamilyFormulaResultId, requested);

        Assert.Equal(2, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        using var original = RootStorage.OpenRead(source);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", formula: true, numericFormula: new(input)), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", formula: true, numericFormula: new(input)), ReadBody(root, DerivedId));
        Assert.Equal(ReadBody(original, FamilyId), ReadBody(root, FamilyId));
    }

    [Theory]
    [InlineData("43.5", 21.75)]
    [InlineData("-7.25", -3.625)]
    public void ResolvesAnExactCurrencyFormulaInverse(string requested, double input)
    {
        var formula = new NumericFamilyFormula(CurrencyInput: 7.25);
        string source = CreateSource(family: new FamilyFixture(Formula: true, NumericFormula: formula));
        var result = RvtWriter.SetParameter(source, Output, ProjectId, FamilyFormulaResultId, requested);

        Assert.Equal(2, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        var expected = formula with { CurrencyInput = input };
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", formula: true, numericFormula: expected), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", formula: true, numericFormula: expected), ReadBody(root, DerivedId));
    }

    [Fact]
    public void RefusesAnAmbiguousReplacementOfAnIndependentLoadedCurrencyValue()
    {
        var formula = new NumericFamilyFormula(CurrencyInput: 7.25);
        string source = CreateSource(family: new FamilyFixture(NumericFormula: formula));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, DefinitionId, "17.25"), new(ProjectId, DefinitionId, "17.25000000001")]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(46.87500009, true)]
    [InlineData(46.87499991, true)]
    [InlineData(46.87500009375, false)]
    [InlineData(46.87499990625, false)]
    public void ClassifiesVerifiedRetainedCurrencyFormulaValues(double calculated, bool retained)
    {
        Assert.Equal(retained, ParameterFormulaEvaluator.IsWithinVerifiedRelativeTolerance(46.875, calculated));
    }

    [Fact]
    public void AGlobalCurrencyInputRecalculatesTheLoadedTypeFormula()
    {
        var formula = new NumericFamilyFormula(CurrencyInput: 7.25);
        var association = new ProjectAssociation((BuiltInParameter)DefinitionId, GlobalId: PropertyGlobalId);
        var global = new GlobalFixture(Kind.Number, InitialValue: "7.25", Currency: true);
        string source = CreateSource(family: new FamilyFixture(Formula: true, NumericFormula: formula),
            globalParameter: global, association: association);
        RvtWriter.SetParameter(source, Output, PropertyGlobalId, PropertyGlobalId, "17.25");

        using var root = RootStorage.OpenRead(Output);
        var expected = formula with { CurrencyInput = 17.25 };
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", formula: true, numericFormula: expected,
            association: association, associationSlot: true), ReadBody(root));
        Assert.Equal(GlobalBody(global, "17.25", PropertyGlobalId), ReadBody(root, PropertyGlobalId));
    }

    [Fact]
    public void KeepsTheCurrentFormulaInputWhenTheRequestedResultAlreadyMatches()
    {
        string source = CreateSource(family: new FamilyFixture(Formula: true, NumericFormula: new(7, Operation: 4)));
        var result = RvtWriter.SetParameter(source, Output, ProjectId, FamilyFormulaResultId, "4");

        Assert.Equal(0, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        using var original = RootStorage.OpenRead(source);
        Assert.Equal(ReadBody(original), ReadBody(root));
        Assert.Equal(ReadBody(original, DerivedId), ReadBody(root, DerivedId));
    }

    [Fact]
    public void RefusesAnEntireBatchWhenAFormulaInverseProducesAnOutOfRangeResult()
    {
        string source = CreateSource(family: new FamilyFixture(Formula: true, NumericFormula: new()));
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, FamilyFormulaResultId, "99"), new(ProjectId, FamilyFormulaResultId, int.MaxValue.ToString())]));

        Assert.Equal("invalid-parameter-value", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void RecalculatesAnInstanceFormulaWithoutChangingItsType()
    {
        string source = CreateSource(family: new FamilyFixture(Instance: true, Formula: true));
        const string value = "New local formula input";
        var result = RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, value);

        Assert.Equal(1, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        using var original = RootStorage.OpenRead(source);
        Assert.Equal(InstanceBody(value, formulaResult: value), ReadBody(root));
        foreach (ulong id in new ulong[] { FamilyId, MasterId, DerivedId })
            Assert.Equal(ReadBody(original, id), ReadBody(root, id));
    }

    [Fact]
    public void RefusesAnEntireBatchWhenAnInstanceFormulaResultIsEditedDirectly()
    {
        string source = CreateSource(family: new FamilyFixture(Instance: true, Formula: true));
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, DefinitionId, "Staged local input"), new(ProjectId, FamilyFormulaResultId, "Rejected")]));

        Assert.Contains("saved expression", refused.Message);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void RefusesAnInstanceFormulaWhenItsStoredResultDisagreesWithTheFormula()
    {
        string source = CreateSource(family: new FamilyFixture(Instance: true, Formula: true, InstanceFormulaCache: "Stale"));
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameter(source, Output, ProjectId, DefinitionId, "New"));

        Assert.Contains("does not reproduce its stored value", refused.Message);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void RejectsABatchWhenTheLoadedFormulaResultCanDriveGeometry()
    {
        string source = CreateSource(family: new FamilyFixture(Formula: true, FormulaTrivial: false));
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, DefinitionId, "Changed"), new(ProjectId, DefinitionId, "Never published")]));
        Assert.Equal("unverified-parameter-storage", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClearsOnlyTheProjectBoundValueOnALoadedFamilyElement(bool instance)
    {
        string source = CreateSource(family: new FamilyFixture(Instance: instance, ProjectBound: true));
        var result = RvtWriter.SetParameters(source, Output, [new(ProjectId, BoundParameterId, null), new(ProjectId, BoundParameterId, null)]);
        Assert.Equal(1, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(instance ? InstanceBody("Old", BindStore.Empty) : SymbolBody(ProjectId, -1, "Old", bound: BindStore.Empty), ReadBody(root));
    }

    [Fact]
    public void ClearsAProjectBoundTypeValueInheritedByADerivedSymbol()
    {
        string source = CreateSource(family: new FamilyFixture(ProjectBound: true, DerivedType: true));
        var result = RvtWriter.SetParameters(source, Output, [new(ProjectId, BoundParameterId, null)]);
        Assert.Equal(1, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", bound: BindStore.Empty), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old"), ReadBody(root, DerivedId));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ClearsTheProjectBoundValuesOfOnlyTheAddressedSymbols(bool primary, bool derived)
    {
        string source = CreateSource(family: new FamilyFixture(ProjectBound: true, DerivedType: true, DerivedBoundCopy: true));
        var edits = new List<RvtParameterEdit>();
        if (primary)
            edits.Add(new(ProjectId, BoundParameterId, null));
        if (derived)
            edits.Add(new(DerivedId, BoundParameterId, null));
        var result = RvtWriter.SetParameters(source, Output, edits);
        Assert.Equal(edits.Count, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", bound: primary ? BindStore.Empty : BindStore.Value), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", bound: derived ? BindStore.Empty : BindStore.Value), ReadBody(root, DerivedId));
    }

    [Theory]
    [InlineData(true, false, "New local text")]
    [InlineData(true, true, "Replacement text")]
    [InlineData(true, false, "")]
    [InlineData(false, false, "New local text")]
    [InlineData(false, true, "Replacement text")]
    [InlineData(false, false, "")]
    public void WritesOnlyTheAddressedDerivedSymbolsProjectValue(bool hide, bool existing, string value)
    {
        string source = CreateSource(family: new FamilyFixture(ProjectBound: true, DerivedType: true, DerivedBoundCopy: existing,
            HideWhenNoValue: hide));
        var result = RvtWriter.SetParameters(source, Output, [new(DerivedId, BoundParameterId, value)]);
        Assert.Equal(1, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", bound: BindStore.Value), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", bound: BindStore.Value, boundText: value), ReadBody(root, DerivedId));
    }

    [Theory]
    [InlineData(false, false, true, "")]
    [InlineData(true, false, false, "First value")]
    [InlineData(true, true, true, "Replacement")]
    [InlineData(false, true, false, "Replacement")]
    public void WritesThePrimarySymbolsProjectValue(bool derived, bool existing, bool hide, string value)
    {
        string source = CreateSource(family: new FamilyFixture(ProjectBound: true, DerivedType: derived, DerivedBoundCopy: derived,
            HideWhenNoValue: hide, BoundValue: existing));
        var result = RvtWriter.SetParameters(source, Output, [new(ProjectId, BoundParameterId, value)]);
        Assert.Equal(1, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(SymbolBody(ProjectId, -1, "Old", bound: BindStore.Value, boundText: value), ReadBody(root));
        if (derived)
            Assert.Equal(SymbolBody(DerivedId, ProjectId, "Old", bound: BindStore.Value), ReadBody(root, DerivedId));
    }

    [Theory]
    [InlineData(false, true, "First instance value")]
    [InlineData(true, false, "Replacement")]
    [InlineData(false, false, "")]
    public void WritesAProjectBoundValueOnALoadedInstance(bool existing, bool hide, string value)
    {
        string source = CreateSource(family: new FamilyFixture(Instance: true, ProjectBound: true,
            HideWhenNoValue: hide, BoundValue: existing));
        var result = RvtWriter.SetParameters(source, Output, [new(ProjectId, BoundParameterId, value)]);
        Assert.Equal(1, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(InstanceBody("Old", BindStore.Value, value), ReadBody(root));
        Assert.Equal(SymbolBody(DerivedId, MasterId, "Old", true), ReadBody(root, DerivedId));
    }

    [Theory]
    [InlineData(true, false, "First instance text")]
    [InlineData(false, false, "")]
    [InlineData(false, true, "Replacement")]
    public void WritesNonsharedProjectTextOnALoadedFamilyElement(bool instance, bool existing, string value)
    {
        var family = new FamilyFixture(Instance: instance, ProjectBound: true, BoundValue: existing, SharedBinding: false);
        string source = CreateSource(family: family);
        var result = RvtWriter.SetParameter(source, Output, ProjectId, BoundParameterId, value);

        Assert.Equal(1, result.RewrittenRecordCount);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(instance ? InstanceBody("Old", BindStore.Value, value)
            : SymbolBody(ProjectId, -1, "Old", bound: BindStore.Value, boundText: value), ReadBody(root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RefusesTheEntireBatchWhenClearingANonsharedProjectValue(bool instance)
    {
        string source = CreateSource(family: new FamilyFixture(Instance: instance, ProjectBound: true, SharedBinding: false));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, BoundParameterId, "Staged"), new(ProjectId, BoundParameterId, null)]));

        Assert.Equal("unsupported-parameter-clear", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void RejectsTheWholeBatchWhenClearingAnOrdinaryProjectBoundInstanceValue()
    {
        string source = CreateSource(family: new FamilyFixture(Instance: true, ProjectBound: true, HideWhenNoValue: false));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, BoundParameterId, "Staged"), new(ProjectId, BoundParameterId, null)]));
        Assert.Equal("unsupported-parameter-clear", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void RefusesProjectBoundAssignmentWhenTheFamilyAlsoDeclaresTheDefinition()
    {
        string source = CreateSource(family: new FamilyFixture(SameDefinition: true));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, DefinitionId, "Ambiguous")]));
        Assert.Equal("unsupported-parameter", refused.Kind);
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void RejectsTheWholeBatchWhenAProjectBoundTypeAssignmentIsReadOnly(bool derived, bool hide)
    {
        string source = CreateSource(family: new FamilyFixture(ProjectBound: true, DerivedType: true,
            BoundReadOnly: true, HideWhenNoValue: hide));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(ProjectId, DefinitionId, "Staged"), new(derived ? DerivedId : ProjectId, BoundParameterId, "Unsupported")]));
        Assert.Equal("read-only-parameter", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void RejectsTheWholeBatchWhenClearingADerivedValueWithoutHideWhenNoValue()
    {
        string source = CreateSource(family: new FamilyFixture(ProjectBound: true, DerivedType: true, HideWhenNoValue: false));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(DerivedId, BoundParameterId, "Staged"), new(DerivedId, BoundParameterId, null)]));
        Assert.Equal("unsupported-parameter-clear", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void RejectsTheBatchWhenAClearAfterTheDerivedClearIsUnsupported()
    {
        string source = CreateSource(family: new FamilyFixture(ProjectBound: true, DerivedType: true, DerivedBoundCopy: true));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
            [new(DerivedId, BoundParameterId, null), new(ProjectId, DefinitionId, null)]));
        Assert.Equal("unsupported-parameter-clear", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusesClearingAValueDeclaredInTheFamily(bool sameDefinition)
    {
        string source = CreateSource(family: new FamilyFixture(Instance: true, ProjectBound: !sameDefinition, SameDefinition: sameDefinition));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        RvtParameterEdit[] edits = sameDefinition ? [new(ProjectId, DefinitionId, null)]
            : [new(ProjectId, BoundParameterId, null), new(ProjectId, DefinitionId, null)];
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output, edits));
        Assert.Equal("unsupported-parameter-clear", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    private string CreateSource(string xmlValue = "Owner", bool duplicate = false, bool wall = false, bool grouped = false, bool global = false,
        UserParameter? user = null, FamilyFixture? family = null, int release = 2022,
        BuiltInParameter? builtIn = null, string? builtInValue = null, GlobalFixture? globalParameter = null,
        GlobalFormulaFixture? globalFormulas = null, ProjectAssociation? association = null, OrdinaryTarget? projectTarget = null,
        int? targetLevelDesignOption = null, ProjectImage? image = null)
    {
        var tags = Tags(release);
        bool material = user?.Kind is Kind.Material or Kind.FamilyType || family?.BoundFamilyType == true || image is not null;
        projectTarget ??= family?.ProjectTarget;
        var schema = new SchemaStreamEditorTests.Writer();
        void Add(string name, params Fld[] fields) => schema.Top(new(name, new NoRef(), 1, fields, []));
        Fld Reference(string name, int id, byte mods = 0) => new(name, 14, mods, Target: new Back(id));
        Add("Identifier", new Fld("m_id", 4)); // 12
        Add("OriginalElementId", Reference("m_id", 12)); // 13
        Add("ElementId", Reference("m_id", 12)); // 14
        Add("ElemHistory", Reference("m_originalElementId", 13), new("m_creationDate", 4), new("m_lastModificationDate", 4), new("m_lastUserModificationDate", 4)); // 15
        Add("ElemRec", Reference("m_id", 14), Reference("m_history", 15), new("m_partitionId", 4), Reference("m_OwningElementId", 14)); // 16
        Add("ElemTable", Reference("m_elemArr", 16, 0x50)); // 17
        Add("ParamValueAString", new Fld("m_paramId", 4), new("m_value", 8)); // 18
        Add("ParamValueSetAString", Reference("m_paramSet", 18, 0x50)); // 19
        List<Fld> elementFields = [Reference("m_id", 14), new("m_pParamValueSetAString", 14, 1), new("m_pParamValueSetInt", 14, 1), new("m_pParamValueSetDouble", 14, 1)];
        if (material)
            elementFields.Add(new("m_pParamValueSetElementId", 14, 1));
        Add("Element", elementFields.ToArray()); // 20
        schema.Top(new("ProjectInfo", new Back(20), 1, [], [])); // 21
        Fld[] ownerFields = material || user is { Store: not StoreState.Value }
            ? [new("m_pGroup", 14, 1), new("m_pTail", 14, 1)] : [new("m_pGroup", 14, 1)];
        if (association is not null)
            ownerFields = [.. ownerFields, new("m_association", 14, 1)];
        schema.Top(new("SWall", new Back(20), 1, ownerFields, [])); // 22
        Add("GroupMembership"); // 23
        Add(globalParameter is null ? "ParamElemGlobal" : "UnrelatedRecord"); // 24
        Add("ParamDef", new Fld("m_paramElemId", 4), new("m_readOnly", 1)); // 25
        schema.Top(new("ParamDefString", new Back(25), 1, [], [])); // 26
        schema.Top(new("ParamDefInt", new Back(25), 1, [new("m_lowBound", 4), new("m_upBound", 4)], [])); // 27
        schema.Top(new("ParamDefYesNo", new Back(25), 1, [], [])); // 28
        Add("ForgeTypeId", new Fld("m_typeId", 8)); // 29
        schema.Top(new("ParamDefValue", new Back(25), 1,
            [release == 2020 ? new("m_unitType", 4) : Reference("m_specTypeId", 29), new("m_restriction", 4), new("m_boundless", 1)], [])); // 30
        schema.Top(new("ParamElem", new Back(20), 1, [new("m_pParamDef", 14, 1)], [])); // 31
        schema.Top(new("ParamElemExternal", new Back(31), 1, [new("m_bindingIds", 4, 0x50), new("m_userModifiable", 1), new("m_hideWhenNoValue", 1)], [])); // 32
        schema.Top(new("ParamElemProject", new Back(31), 1, [new("m_bindingIds", 4, 0x50)], [])); // 33
        schema.Top(new("ParamBinding", new Back(20), 1, [new("m_paramId", 4), new("m_categoryId", 4), new("m_elemOrSymbol", 4)], [])); // 34
        Add("ParamValueInt", new Fld("m_paramId", 4), new("m_value", 4)); // 35
        Add("ParamValueSetInt", Reference("m_paramSet", 35, 0x50)); // 36
        Fld doubleValue = new("m_value", 7), doubleId = new("m_paramId", 4);
        Add("ParamValueDouble", user?.IdFirst == true ? [doubleId, doubleValue] : [doubleValue, doubleId]); // 37
        Add("ParamValueSetDouble", Reference("m_paramSet", 37, 0x50)); // 38
        Add("NamedParam", new Fld("m_str", 8), new("m_oExpression", 14, 1), new("m_value", 7), new("m_elemId", 4),
            new("m_paramId", 4), new("m_int", 4), new("m_instance", 1), new("m_reporting", 1)); // 39
        Add("FamilyParams", Reference("m_params", 39, 0x50)); // 40
        Fld[] familyFields = [new("m_familyParams", 14, 2), new("m_categoryId", 4), new("m_trivialParamIds", 4, 0x50),
            new("m_inPlace", 1), new("m_name", 8)];
        if (projectTarget is not null)
            familyFields = [..familyFields, new("m_oFamDoc", 14, 2), new("m_familyIds", 14, 2), new("m_bShared", 1)];
        schema.Top(new("FamilyBase", new Back(20), 1, familyFields, [])); // 41
        schema.Top(new("Family", new Back(41), 1, [], [])); // 42
        Add("InstInfoBase", new Fld("m_symbolId", 4)); // 43
        schema.Top(new("InstanceInfo", new Back(43), 1, [], [])); // 44
        Fld[] instanceFields = [new("m_pInstParams", 14, 2), new("m_pInstanceInfo", 14, 2), new("m_masterSymbolId", 4), new("m_superInstanceId", 4)];
        Fld[] symbolFields = [new("m_pParams", 14, 2), new("m_familyId", 4), new("m_masterId", 4), new("m_ownerInstanceId", 4)];
        if (projectTarget is not null)
            symbolFields = [..symbolFields, new("m_active", 1)];
        if (association is not null && family is { Instance: true })
            instanceFields = [..instanceFields, new("m_association", 14, 1)];
        if (association is not null && family is { Instance: false })
            symbolFields = [..symbolFields, new("m_association", 14, 1)];
        schema.Top(new("FamilyInstance", new Back(20), 1, instanceFields, [])); // 45
        schema.Top(new("FamilySymbol", new Back(20), 1, symbolFields, [])); // 46
        schema.Top(new("ParamElemFamily", new Back(31), 1, [new("m_instanceParam", 1)], [])); // 47
        Add("ElementHeader", new Fld("m_regenHistoryCount", 4), Reference("m_categroryId", 14),
            Reference("m_familyId", 14), Reference("m_ownerViewId", 14), Reference("m_designOptionId", 14)); // 48
        Add("Tail", new Fld("m_value", 4)); // 49
        schema.Top(new("BasicWallType", new Back(20), 1, ownerFields, [])); // 50
        schema.Top(new("RoomElem", new Back(20), 1, ownerFields, [])); // 51
        schema.Top(new("ParamDefMaterialBrowse", new Back(25), 1,
            [new("m_unassignedString", 4), new("m_extraButtonFlag", 4), new("m_includeParams", 1)], [])); // 52
        Add("ParamValueElementId", Reference("m_paramId", 14), Reference("m_value", 14)); // 53
        Add("ParamValueSetElementId", Reference("m_paramSet", 53, 0x50)); // 54
        Add("Material", new Fld("m_name", 8)); // 55
        schema.Top(new("MaterialElem", new Back(20), 1, [new("m_pMaterial", 14, 1)], [])); // 56
        Add("AStringWrapper", new Fld("m_str", 8)); // 57
        Add("ParamStorage", Reference("m_str", 57), new("m_dbl", 7), Reference("m_elemId", 14), new("m_int", 4), new("m_storageType", 4)); // 58
        schema.Top(new(globalParameter is null ? "GlobalRecord" : "ParamElemGlobal", new Back(31), 1,
            [Reference("m_constraintId", 14), Reference("m_propertyId", 14), new("m_reporting", 1), Reference("m_value", 58), new("m_oExpression", 14, 1)], [])); // 59
        if (association is null)
            Add("ParamIdPair", Reference("m_famParamId", 14)); // 60
        else
            Add("ParamIdPair", Reference("m_famParamId", 14), Reference("m_elemPropId", 14), new("m_geomTag", 4), new("m_bIsSymbol", 1));
        schema.Top(new("AssociatedElement", new Back(20), 1, [Reference("m_parameter", 60)], [])); // 61
        Add("FormulaTokenParameter", Reference("m_paramId", 14)); // 62
        Add("BinaryOperatorExpression", new Fld("m_binaryOperator", 4), new("m_pLeftSubexpression", 14, 1), new("m_pRightSubexpression", 14, 1)); // 63
        Add("ParameterExpression", Reference("m_paramId", 14)); // 64
        Add("NumberConstantExpression", new Fld("m_value", 7), Reference("m_specTypeId", 29)); // 65
        Add("FunctionExpression", new Fld("m_function", 4), new("m_subexpressions", 14, 0x51)); // 66
        Add("ParametrizedElemParamsCellBase", Reference("m_paramDrivenData", 60, 0x50)); // 67
        schema.Top(new("ProjectParametrizedElemParamsCell", new Back(67), 1, [], [])); // 68
        schema.Top(new("ParamDefFamType", new Back(25), 1, [new("m_categoryId", 4)], [])); // 69
        schema.Top(new("NestedFamSymRefElem", new Back(20), 1,
            [new("m_famName", 8), new("m_symName", 8), new("m_categoryId", 4)], [])); // 70
        Add("ForeignElemRef", new Fld("m_id", 4)); // 71
        Add("std::pair< ElementId, ForeignElemRef >", Reference("first", 14), Reference("second", 71)); // 72
        Add("FamilyDocument", Reference("m_big2SmallMap2", 72, 0x50)); // 73
        Add("ElementIdPlusIndex", Reference("m_elementId", 14), new("m_index", 4)); // 74
        Add("ElementIdIndexPairSet", Reference("m_data", 74, 0x50)); // 75
        schema.Top(new("SpecializedFamilySymbol", new Back(46), 1, [], [])); // 76
        schema.Top(new("Level", new Back(20), 1, [], [])); // 77
        Add("SymbolInfo", new Fld("m_name", 8)); // 78
        schema.Top(new("Symbol", new Back(20), 1, [new Fld("m_symbolInfo", 14, 1)], [])); // 79
        var imageFields = new Fld[] { Reference("m_idImageHolder", 14), new("m_flags", 4), Reference("m_viewId", 14),
            Reference("m_ownerSymbolId", 14), new("m_widthInPixels", 4), new("m_heightInPixels", 4) };
        if (image?.LinkedPath is not null)
            imageFields = [..imageFields, new("m_externalResource", 14, 1)];
        schema.Top(new("ImageSymbol", new Back(79), 1, imageFields, [])); // 80
        Add("ARasterImage", new Fld("m_compressedImage", 2, 0x50), new("m_width", 4), new("m_height", 4)); // 81
        schema.Top(new("ImageHolder", new Back(20), 1,
            [new Fld("m_pRasterImage", 14, 1), Reference("m_idImageSymbol", 14), new("m_flags", 4)], [])); // 82
        Add("std::pair< AString, AString >", new Fld("first", 8), new Fld("second", 8)); // 83
        Add("ExternalResourceRef", Reference("m_referenceInformation", 83, 0x50)); // 84
        Add("ExternalResourceReferenceCell", Reference("m_externalResourceReferences", 84, 0x50)); // 85
        for (int i = 86; i < tags.ContentKey; i++)
            Add("Filler" + i);
        foreach (string marker in new[] { "ContentKey", "ContentMarker", "SegmentMarker", "SegmentCheckback", "SignatureMarker" })
            Add(marker);

        string path = Path.Combine(_folder, "source.rvt");
        using var root = RootStorage.Create(path, OpenMcdf.Version.V4);
        using (var basic = root.CreateStream("BasicFileInfo"))
            basic.Write(RvtWriterTests.BuildBasicFileInfoBytes(release));
        using (var formats = root.CreateStorage("Formats").CreateStream("Latest"))
            formats.Write(PagedStreamCodec.Encode([], schema.Build()));
        using (var elemTable = root.CreateStorage("Global").CreateStream("ElemTable"))
        {
            int[] owners = family is { Instance: true, OnlyPrimary: true } ? [ProjectId, DefinitionId, FamilyId, MasterId]
                : family is { Instance: true } ? [ProjectId, DefinitionId, FamilyId, MasterId, DerivedId]
                : family is { OnlyPrimary: true } or { ProjectBound: true, DerivedType: false } ? [ProjectId, DefinitionId, FamilyId]
                : family is not null ? [ProjectId, DefinitionId, FamilyId, DerivedId]
                : material ? [ProjectId, DefinitionId, BindingId, OriginalMaterialId, TargetMaterialId]
                : user is not null ? [ProjectId, DefinitionId, BindingId] : [ProjectId];
            if (family is { ProjectBound: true })
                owners = [..owners, BoundParameterId, BindingId];
            if (family is { SameDefinition: true })
                owners = [..owners, BindingId];
            if (family is { Formula: true })
                owners = [..owners, FamilyFormulaResultId];
            if (family?.Reference is not null)
                owners = [..owners, NestedTypeId, TargetMaterialId];
            if (projectTarget is not null)
                owners = [..owners, ProjectTargetFamilyId, ProjectTargetSymbolId];
            if (projectTarget is { DependentSymbol: true })
                owners = [..owners, DependentTargetSymbolId];
            if (projectTarget?.LinkedFamilyMap is not null)
                owners = [..owners, LinkedFamilyId, LinkedSymbolId];
            if (user?.Kind == Kind.FamilyType)
                owners = [..owners, NestedTypeId];
            if (targetLevelDesignOption is not null)
                owners = [..owners, TargetLevelId];
            if (image is not null)
                owners = [..owners, ImageId, RasterHolderId];
            if (globalParameter is not null)
            {
                int id = association?.GlobalId ?? DefinitionId;
                owners = globalParameter.Consumer ? [..owners, id, BindingId] : [..owners, id];
            }
            if (globalFormulas is not null)
                owners = globalFormulas.Consumer ? [..owners, BindingId, GlobalFormulaResultId, GlobalFormulaConsumerId]
                    : [..owners, BindingId, GlobalFormulaResultId];
            Array.Sort(owners);
            byte[] table = new byte[6 + owners.Length * 28];
            table[0] = 17;
            table[2] = (byte)owners.Length;
            for (int i = 0; i < owners.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(6 + 28 * i), owners[i]);
            elemTable.Write(PagedStreamCodec.Encode(new byte[8], table));
        }
        using (var partition = root.CreateStorage("Partitions").CreateStream("0"))
        {
            var category = (user?.Owner ?? image?.Owner) == OwnerKind.Room ? BuiltInCategory.OST_Rooms : BuiltInCategory.OST_Walls;
            ushort ownerTag = user is not null ? (ushort)user.Owner : wall ? (ushort)22 : (ushort)21;
            var records = new List<(ulong, ushort, byte[])>
                { (ProjectId, ownerTag, user is null ? Body(wall ? xmlValue : "Owner", duplicate, wall, grouped, association)[2..] : UserBody(user, grouped: grouped, association: association)[2..]) };
            if (builtIn is BuiltInParameter builtInParameter)
            {
                byte[] body = BuiltInBody(builtInParameter, builtInValue);
                records[0] = (ProjectId, BinaryPrimitives.ReadUInt16LittleEndian(body), body[2..]);
            }
            if (family is not null)
                records = FamilyRecords(family, association);
            if (projectTarget is not null)
                AddOrdinaryTarget(records, projectTarget, material, family is { Instance: true } ? MasterId : ProjectId);
            if (user is not null)
            {
                records.Add((DefinitionId, user.Shared ? (ushort)32 : (ushort)33, DefinitionBody(user, release)));
                records.Add((BindingId, 34, Pack(writer =>
                {
                    WriteEmptyElement(writer, BindingId, material);
                    writer.Write(DefinitionId);
                    writer.Write((int)category);
                    writer.Write(user.Binding);
                })));
            }
            if (user?.Kind is Kind.Material or Kind.FamilyType)
            {
                foreach (int id in new[] { OriginalMaterialId, TargetMaterialId })
                    records.Add(((ulong)id, 56, Pack(writer =>
                    {
                        WriteEmptyElement(writer, id, material: true);
                        WritePointer(writer, 55);
                        WriteText(writer, $"Material {id}");
                    })));
            }
            if (user?.Kind == Kind.FamilyType)
                records.Add((NestedTypeId, 70, Pack(writer =>
                {
                    WriteEmptyElement(writer, NestedTypeId, material: true);
                    WriteText(writer, "Nested");
                    WriteText(writer, "Standard");
                    writer.Write((int)BuiltInCategory.OST_GenericModel);
                })));
            if (targetLevelDesignOption is not null)
                records.Add((TargetLevelId, 77, Pack(writer => WriteEmptyElement(writer, TargetLevelId, material))));
            if (image is { } imageFixture)
            {
                bool linked = imageFixture.LinkedPath is not null;
                records[0] = (ProjectId, (ushort)imageFixture.Owner, ProjectImageBody(imageFixture)[2..]);
                records.Add((ImageId, 80, Pack(writer =>
                {
                    WriteEmptyElement(writer, ImageId, material: true);
                    WritePointer(writer, 78);
                    writer.Write(RasterHolderId);
                    writer.Write(linked ? 4 : 0);
                    writer.Write(-1);
                    writer.Write(-1);
                    writer.Write(2);
                    writer.Write(2);
                    if (linked)
                        WritePointer(writer, 85);
                    WriteText(writer, "Picture.png");
                    if (linked)
                    {
                        writer.Write(1);
                        writer.Write(2);
                        WriteText(writer, "Path");
                        WriteText(writer, imageFixture.LinkedPath!);
                        WriteText(writer, "PathType");
                        WriteText(writer, imageFixture.PathType);
                    }
                })));
                records.Add((RasterHolderId, 82, Pack(writer =>
                {
                    WriteEmptyElement(writer, RasterHolderId, material: true);
                    WritePointer(writer, linked ? 0 : 81);
                    writer.Write(imageFixture.BrokenHolder ? ProjectId : ImageId);
                    writer.Write(linked ? 1 : 0);
                    if (!linked)
                    {
                        writer.Write(4);
                        writer.Write(new byte[] { 1, 2, 3, 4 });
                        writer.Write(2);
                        writer.Write(2);
                    }
                })));
            }
            if (global)
                records.Add((ProjectId + 3, 24, []));
            if (globalParameter is not null)
            {
                string initial = globalParameter.InitialValue ?? (globalFormulas is null ? "0" : globalFormulas.Conditional ? "1" : "2");
                int id = association?.GlobalId ?? DefinitionId;
                records.Add(((ulong)id, 59, GlobalBody(globalParameter, initial, id, release: release)[2..]));
                if (globalParameter.Consumer)
                    records.Add((BindingId, 61, Pack(writer =>
                    {
                        WriteEmptyElement(writer, BindingId);
                        writer.Write(DefinitionId);
                    })));
            }
            if (globalFormulas is not null)
            {
                var number = new GlobalFixture(Kind.Number);
                bool division = globalFormulas.Operation == 4;
                bool conditional = globalFormulas.Conditional;
                var first = globalFormulas.Binary ?? new GlobalBinary(globalFormulas.Operation, DefinitionId, division ? 12 : globalFormulas.IntegerResult ? 1.25 : 2,
                    ConstantFirst: division, SpecId: globalFormulas.ConstantSpecId);
                int function = globalFormulas.Function;
                double cached = globalFormulas.Cached ?? (function switch
                {
                    7 => Math.Exp(2),
                    9 => Math.Sqrt(2),
                    _ => conditional ? 2 : globalFormulas.Stale ? 13 : division ? 6 : globalFormulas.IntegerResult ? 3 : 4,
                });
                var resultKind = conditional || globalFormulas.IntegerResult ? Kind.Integer : Kind.Number;
                records.Add((BindingId, 59, GlobalBody(new(resultKind, Currency: globalFormulas.CurrencyResult, SpecId: globalFormulas.SpecId),
                    cached.ToString(CultureInfo.InvariantCulture), BindingId,
                    conditional || function != 0 ? null : first, conditional, function)[2..]));
                double following = globalFormulas.Stale ? 5 : cached + 1;
                records.Add((GlobalFormulaResultId, 59, GlobalBody(number with { SpecId = globalFormulas.SpecId },
                    following.ToString(CultureInfo.InvariantCulture), GlobalFormulaResultId,
                    new(1, BindingId, 1, SpecId: globalFormulas.ConstantSpecId))[2..]));
                if (globalFormulas.Consumer)
                    records.Add((GlobalFormulaConsumerId, 61, Pack(writer =>
                    {
                        WriteEmptyElement(writer, GlobalFormulaConsumerId);
                        writer.Write(GlobalFormulaResultId);
                    })));
            }
            var block = PartitionStreamEditorTests.MakeStandardBlock(4, keyed: false, records.ToArray());
            if (user is not null || family is not null || builtIn is not null || association is not null || image is not null)
            {
                byte[] header = new byte[22];
                BinaryPrimitives.WriteUInt16LittleEndian(header, 48);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(6), (int)(family?.Category ?? category));
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), -1);
                block.Chains[0].Records[0] = new(ProjectId, header);
                if (family is { DerivedType: true })
                {
                    var headers = block.Chains[0].Records;
                    int index = Array.FindIndex(headers, entry => entry.Owner == DerivedId);
                    headers[index] = new(DerivedId, header);
                }
                if (family is { Instance: true, BoundFamilyType: true })
                {
                    var headers = block.Chains[0].Records;
                    int index = Array.FindIndex(headers, entry => entry.Owner == MasterId);
                    headers[index] = new(MasterId, header);
                }
                if (projectTarget is not null)
                {
                    byte[] targetHeader = (byte[])header.Clone();
                    BinaryPrimitives.WriteInt32LittleEndian(targetHeader.AsSpan(6), (int)projectTarget.Category);
                    var headers = block.Chains[0].Records;
                    int index = Array.FindIndex(headers, entry => entry.Owner == ProjectTargetSymbolId);
                    headers[index] = new(ProjectTargetSymbolId, targetHeader);
                }
                if (targetLevelDesignOption is int designOption)
                {
                    byte[] levelHeader = (byte[])header.Clone();
                    BinaryPrimitives.WriteInt32LittleEndian(levelHeader.AsSpan(6), (int)BuiltInCategory.OST_Levels);
                    BinaryPrimitives.WriteInt32LittleEndian(levelHeader.AsSpan(18), designOption);
                    var headers = block.Chains[0].Records;
                    int index = Array.FindIndex(headers, entry => entry.Owner == TargetLevelId);
                    headers[index] = new(TargetLevelId, levelHeader);
                }
                if (image is not null)
                {
                    byte[] imageHeader = (byte[])header.Clone();
                    BinaryPrimitives.WriteInt32LittleEndian(imageHeader.AsSpan(6), (int)BuiltInCategory.OST_RasterImages);
                    BinaryPrimitives.WriteInt32LittleEndian(imageHeader.AsSpan(18), image.DesignOption);
                    var headers = block.Chains[0].Records;
                    int index = Array.FindIndex(headers, entry => entry.Owner == ImageId);
                    headers[index] = new(ImageId, imageHeader);
                }
            }
            partition.Write(PartitionStreamEditorTests.BuildPartitionStream(4, tags, block));
        }
        using var info = root.CreateStream("ProjectInformation");
        using var zip = new ZipArchive(info, ZipArchiveMode.Create);
        using var output = zip.CreateEntry("sample.project.xml").Open();
        using var writer = new StreamWriter(output);
        writer.Write($"""<entry xmlns="http://www.w3.org/2005/Atom" xmlns:A="urn:schemas-autodesk-com:partatom"><A:features><A:feature><A:group><Client_Name type="system">{xmlValue}</Client_Name><Project_Name type="system">Owner</Project_Name></A:group></A:feature></A:features></entry>""");
        return path;
    }

    private static List<(ulong, ushort, byte[])> FamilyRecords(FamilyFixture options, ProjectAssociation? association)
    {
        int master = options.Instance ? MasterId : ProjectId;
        int numericDefinition = options.NumericFormula?.CurrencyInput is not null ? 30 : 27;
        var bound = options.ProjectBound && options.BoundValue && !options.BoundFamilyType ? BindStore.Value : BindStore.None;
        int? boundReference = options.BoundFamilyType && options.BoundValue ? -1 : null;
        var records = new List<(ulong, ushort, byte[])>
        {
            (ProjectId, options.Instance ? (ushort)45 : (ushort)46,
                options.Instance ? (options.InstanceComment is { } comment
                    ? InstanceBody(options.Inherited ? null : "Old", BindStore.Value, comment, (int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                    : InstanceBody(options.Inherited ? null : "Old", bound,
                        formulaResult: options.Formula ? options.InstanceFormulaCache ?? "Old" : null, association: association,
                        reference: options.Reference?.Value, primary: options.OnlyPrimary,
                        referenceStore: options.BoundFamilyType, boundReference: boundReference))[2..]
                    : SymbolBody(ProjectId, -1, "Old", bound: bound, identity: options.Identity,
                        formula: options.Formula, numericFormula: options.NumericFormula,
                        association: association is { Derived: false } ? association : null,
                        associationSlot: association is not null, typedCost: options.TypedCost, reference: options.Reference?.Value,
                        active: options.ProjectTarget is null ? null : true, referenceStore: options.BoundFamilyType,
                        boundReference: boundReference)[2..]),
            (DefinitionId, options.SameDefinition ? (ushort)32 : (ushort)47, Pack(writer =>
            {
                WriteEmptyElement(writer, DefinitionId, material: options.BoundFamilyType);
                WritePointer(writer, options.Reference is not null ? 69 : options.NumericFormula is null ? 26 : numericDefinition);
                if (options.SameDefinition)
                    WriteBoundDefinition(writer, DefinitionId);
                else
                {
                    writer.Write(options.Instance);
                    writer.Write(DefinitionId);
                    writer.Write(options.Reference?.ReadOnly ?? false);
                    if (options.Reference is not null)
                        writer.Write((int)BuiltInCategory.OST_GenericModel);
                    if (options.NumericFormula is not null)
                        WriteNumericDefinition(writer);
                }
            })),
            (FamilyId, 42, Pack(writer =>
            {
                WriteEmptyElement(writer, FamilyId, material: options.BoundFamilyType);
                WritePointer(writer, 40);
                writer.Write((int)options.Category);
                writer.Write((options.Trivial ? 1 : 0) + (options.Formula && options.FormulaTrivial ? 1 : 0));
                if (options.Trivial)
                    writer.Write(DefinitionId);
                if (options.Formula && options.FormulaTrivial)
                    writer.Write(FamilyFormulaResultId);
                writer.Write(false);
                WriteText(writer, "Host");
                if (options.ProjectTarget is { } target)
                {
                    int map = target.LinkedFamilyMap == FamilyId || target.ContainsOwner ? 73 : 0;
                    WritePointer(writer, map);
                    WritePointer(writer, map == 0 ? 0 : 75);
                    writer.Write(target.ContainsOwner);
                }
                WriteNamedStore(writer, "Old", options.Instance, options.Reporting, options.Identity, options.Formula,
                    numericFormula: options.NumericFormula, reference: options.Reference?.Value);
                if (options.Formula)
                    WriteFamilyFormula(writer, options.NumericFormula);
                if (options.ProjectTarget is { ContainsOwner: true } owner)
                    WriteFamilyMaps(writer, [NestedTypeId], owner.OwnerHasProxy ? [NestedTypeId] : []);
                else if (options.ProjectTarget?.LinkedFamilyMap == FamilyId)
                    WriteFamilyMaps(writer, [TargetMaterialId], []);
            })),
        };
        if (options.Reference is { } reference)
        {
            records.Add((NestedTypeId, 70, Pack(writer =>
            {
                WriteEmptyElement(writer, NestedTypeId, material: options.BoundFamilyType);
                WriteText(writer, reference.TargetFamily);
                WriteText(writer, "Standard");
                writer.Write((int)reference.TargetCategory);
            })));
            records.Add((TargetMaterialId, 56, Pack(writer =>
            {
                WriteEmptyElement(writer, TargetMaterialId, material: options.BoundFamilyType);
                WritePointer(writer, 55);
                WriteText(writer, "Finish");
            })));
        }
        if (!options.OnlyPrimary && (options.Instance || !options.ProjectBound || options.DerivedType))
            records.Add((DerivedId, 46, SymbolBody(DerivedId, master, options.Diverged ? "Different" : "Old", options.Instance,
                options.DerivedBoundCopy ? BindStore.Value : BindStore.None, identity: options.Identity,
                formula: options.Formula, numericFormula: options.NumericFormula,
                association: association is { Derived: true } or { PrimaryAndDerived: true } ? association : null,
                associationSlot: association is not null && !options.Instance,
                typedCost: options.TypedCost,
                reference: options.Reference is { } nested ? options.Diverged ? OriginalMaterialId : nested.Value : null,
                active: options.ProjectTarget is null ? null : true, referenceStore: options.BoundFamilyType)[2..]));
        if (options.Formula)
            records.Add((FamilyFormulaResultId, 47, Pack(writer =>
            {
                WriteEmptyElement(writer, FamilyFormulaResultId);
                WritePointer(writer, options.NumericFormula is null ? 26 : numericDefinition);
                writer.Write(options.Instance);
                writer.Write(FamilyFormulaResultId);
                writer.Write(false);
                if (options.NumericFormula is not null)
                    WriteNumericDefinition(writer);
            })));
        if (options.Instance)
            records.Add((MasterId, 46, SymbolBody(MasterId, -1, "Old", true, formula: options.Formula,
                reference: options.Reference?.Value, active: options.ProjectTarget is null ? null : true,
                referenceStore: options.BoundFamilyType)[2..]));
        if (options.ProjectBound)
            records.Add((BoundParameterId, options.SharedBinding ? (ushort)32 : (ushort)33, Pack(writer =>
            {
                WriteEmptyElement(writer, BoundParameterId, material: options.BoundFamilyType);
                WritePointer(writer, options.BoundFamilyType ? 69 : 26);
                WriteBoundDefinition(writer, BoundParameterId, options.HideWhenNoValue, options.BoundReadOnly, options.SharedBinding);
                if (options.BoundFamilyType)
                    writer.Write((int)BuiltInCategory.OST_GenericModel);
            })));
        if (options.ProjectBound || options.SameDefinition)
            records.Add((BindingId, 34, Pack(writer =>
            {
                WriteEmptyElement(writer, BindingId, material: options.BoundFamilyType);
                writer.Write(options.SameDefinition ? DefinitionId : BoundParameterId);
                writer.Write((int)options.Category);
                writer.Write(options.Instance ? 1 : 2);
            })));
        return records;

        void WriteNumericDefinition(BinaryWriter writer)
        {
            if (options.NumericFormula?.CurrencyInput is not null)
            {
                WriteText(writer, "autodesk.spec.measurable:currency-2.0.0");
                writer.Write(1);
                writer.Write(false);
            }
            else
            {
                writer.Write(int.MinValue);
                writer.Write(int.MaxValue);
            }
        }
    }

    private static void AddOrdinaryTarget(List<(ulong, ushort, byte[])> records, OrdinaryTarget target, bool referenceStore,
        int referringType)
    {
        AddFamily(ProjectTargetFamilyId, "Target", target.ContainsOwner ? [FamilyId, referringType, NestedTypeId] : [target.MappedId]);
        records.Add((ProjectTargetSymbolId, 46, SymbolBody(ProjectTargetSymbolId, -1, "Target",
            reference: target.SavedReference, familyId: ProjectTargetFamilyId, active: target.Active, referenceStore: referenceStore)[2..]));
        if (target.DependentSymbol)
            records.Add((DependentTargetSymbolId, 76, SymbolBody(DependentTargetSymbolId, -1, "Other type",
                reference: ProjectId, familyId: ProjectTargetFamilyId, active: true, referenceStore: referenceStore)[2..]));
        if (target.LinkedFamilyMap is int linked)
        {
            AddFamily(LinkedFamilyId, "Linked", [linked]);
            records.Add((LinkedSymbolId, 46, SymbolBody(LinkedSymbolId, -1, "Linked type", familyId: LinkedFamilyId,
                active: true, referenceStore: referenceStore)[2..]));
        }

        void AddFamily(int id, string name, int[] external)
        {
            records.Add(((ulong)id, 42, Pack(writer =>
            {
                WriteEmptyElement(writer, id, material: referenceStore);
                WritePointer(writer, 40);
                writer.Write((int)target.Category);
                writer.Write(0);
                writer.Write(false);
                WriteText(writer, name);
                WritePointer(writer, 73);
                WritePointer(writer, 75);
                writer.Write(false);
                writer.Write(0);
                WriteFamilyMaps(writer, external, []);
            })));
        }
    }

    private static void WriteFamilyMaps(BinaryWriter writer, int[] external, int[] owned)
    {
        writer.Write(external.Length);
        foreach (int id in external)
        {
            writer.Write(id);
            writer.Write(1);
        }
        writer.Write(owned.Length);
        for (int index = 0; index < owned.Length; index++)
        {
            writer.Write(owned[index]);
            writer.Write(index);
        }
    }

    private static void WriteBoundDefinition(BinaryWriter writer, int id, bool hideWhenNoValue = true, bool readOnly = false, bool shared = true)
    {
        writer.Write(1);
        writer.Write(BindingId);
        if (shared)
        {
            writer.Write(true);
            writer.Write(hideWhenNoValue);
        }
        writer.Write(id);
        writer.Write(readOnly);
    }

    private enum BindStore { None, Value, Empty }

    private static byte[] SymbolBody(int id, int master, string value, bool instanceDefault = false, BindStore bound = BindStore.None,
        string boundText = "Bound value", StoredIdentity? identity = null, bool formula = false,
        NumericFamilyFormula? numericFormula = null, ProjectAssociation? association = null, bool associationSlot = false,
        double? typedCost = null, int? reference = null, int familyId = FamilyId, bool? active = null, bool referenceStore = false,
        int? boundReference = null) => Pack(writer =>
    {
        writer.Write((ushort)46);
        WriteEmptyElement(writer, id, material: referenceStore,
            textStore: bound != BindStore.None || identity?.CompetingTypedCopy == true, doubleStore: typedCost.HasValue,
            elementIdStore: boundReference.HasValue);
        WritePointer(writer, 40);
        writer.Write(familyId);
        writer.Write(master);
        writer.Write(-1);
        if (active.HasValue)
            writer.Write(active.Value);
        if (associationSlot)
            WritePointer(writer, association is null ? 0 : 68);
        WriteBoundValue(writer, bound, boundText);
        if (identity is { CompetingTypedCopy: true })
        {
            writer.Write(1);
            writer.Write((int)identity.Parameter);
            WriteText(writer, "Typed override");
        }
        if (typedCost is double cost)
        {
            writer.Write(1);
            writer.Write(cost);
            writer.Write((int)BuiltInParameter.ALL_MODEL_COST);
        }
        WriteBoundReference(writer, boundReference);
        WriteNamedStore(writer, value, instanceDefault, identity: identity, formula: formula, numericFormula: numericFormula,
            reference: reference);
        if (association is not null)
            WriteProjectAssociation(writer, association);
        if (formula)
            WriteFamilyFormula(writer, numericFormula);
    });

    private static byte[] InstanceBody(string? value, BindStore bound = BindStore.None, string boundText = "Bound value",
        int parameterId = BoundParameterId, string? formulaResult = null, ProjectAssociation? association = null,
        int? reference = null, bool primary = false, bool referenceStore = false, int? boundReference = null) => Pack(writer =>
    {
        writer.Write((ushort)45);
        WriteEmptyElement(writer, ProjectId, material: referenceStore, textStore: bound != BindStore.None,
            elementIdStore: boundReference.HasValue);
        WritePointer(writer, 40);
        WritePointer(writer, 44);
        writer.Write(MasterId);
        writer.Write(-1);
        if (association is not null)
            WritePointer(writer, 68);
        WriteBoundValue(writer, bound, boundText, parameterId);
        WriteBoundReference(writer, boundReference);
        WriteNamedStore(writer, value, formulaResult: formulaResult, reference: reference);
        writer.Write(primary ? MasterId : DerivedId);
        if (association is not null)
            WriteProjectAssociation(writer, association);
    });

    private static void WriteBoundReference(BinaryWriter writer, int? reference)
    {
        if (reference is not int id)
            return;
        writer.Write(1);
        writer.Write(BoundParameterId);
        writer.Write(id);
    }

    private static void WriteBoundValue(BinaryWriter writer, BindStore state, string value = "Bound value", int parameterId = BoundParameterId)
    {
        if (state == BindStore.None)
            return;
        writer.Write(state == BindStore.Value ? 1 : 0);
        if (state == BindStore.Value)
        {
            writer.Write(parameterId);
            WriteText(writer, value);
        }
    }

    private static void WriteNamedStore(BinaryWriter writer, string? value, bool instance = false, bool reporting = false,
        StoredIdentity? identity = null, bool formula = false, string? formulaResult = null, NumericFamilyFormula? numericFormula = null,
        int? reference = null)
    {
        writer.Write((value is null ? 0 : 1) + (identity is null ? 0 : 1) + (formula || formulaResult is not null ? 1 : 0));
        if (value is not null)
            WriteRow(DefinitionId, numericFormula is null && reference is null ? value : "", numericFormula?.CurrencyInput ?? 0,
                instance, reporting, integer: numericFormula?.CurrencyInput is null ? numericFormula?.Input ?? 0 : 0, elementId: reference ?? -1);
        if (formula && numericFormula is { CurrencyInput: double amount } currency)
            WriteRow(FamilyFormulaResultId, "", currency.Operation == 3 ? 2 * amount : amount / 2d, instance, false, expression: 63);
        else if (formula && numericFormula is { } numerical)
            WriteRow(FamilyFormulaResultId, "", 0, instance, false, expression: 63,
                integer: numerical.Operation == 3 ? 2 * numerical.Input : (int)Math.Floor(numerical.Input / 2d + 0.5));
        else if (formula)
            WriteRow(FamilyFormulaResultId, value!, 0, instance, false, expression: 64);
        else if (formulaResult is not null)
            WriteRow(FamilyFormulaResultId, formulaResult, 0, false, false);
        if (identity is not null)
        {
            bool cost = identity.Parameter == BuiltInParameter.ALL_MODEL_COST;
            WriteRow((int)identity.Parameter, cost ? "" : identity.Value,
                cost ? double.Parse(identity.Value, System.Globalization.CultureInfo.InvariantCulture) : 0, false, false);
        }

        void WriteRow(int id, string text, double number, bool isInstance, bool isReporting, ushort expression = 0, int integer = 0,
            int elementId = -1)
        {
            WriteText(writer, text);
            if (expression == 0)
                writer.Write(0u);
            else
                WritePointer(writer, expression);
            writer.Write(number); writer.Write(elementId);
            writer.Write(id); writer.Write(integer); writer.Write(isInstance); writer.Write(isReporting);
        }
    }

    private static void WriteFamilyFormula(BinaryWriter writer, NumericFamilyFormula? numericFormula)
    {
        if (numericFormula is not null)
        {
            writer.Write(numericFormula.Operation);
            WritePointer(writer, 64);
            WritePointer(writer, 65);
        }
        writer.Write(DefinitionId);
        if (numericFormula is not null)
        {
            writer.Write(2.0);
            WriteText(writer, "autodesk.spec.aec:number-2.0.0");
        }
    }

    private static byte[] Body(string value, bool duplicate = false, bool wall = false, bool grouped = false, ProjectAssociation? association = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(wall ? (ushort)22 : (ushort)21);
        writer.Write(ProjectId);
        writer.Write(uint.MaxValue);
        writer.Write((ushort)19);
        writer.Write(0u);
        writer.Write(0u);
        if (wall)
        {
            writer.Write(grouped ? uint.MaxValue : 0);
            if (grouped)
                writer.Write((ushort)23);
        }
        if (association is not null)
            WritePointer(writer, 68);
        writer.Write(2);
        writer.Write((int)(wall ? BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS : BuiltInParameter.CLIENT_NAME));
        writer.Write(value.Length);
        writer.Write(Encoding.Unicode.GetBytes(value));
        writer.Write((int)(duplicate ? BuiltInParameter.CLIENT_NAME : BuiltInParameter.PROJECT_NAME));
        writer.Write(5);
        writer.Write(Encoding.Unicode.GetBytes("Owner"));
        if (association is not null)
            WriteProjectAssociation(writer, association);
        return stream.ToArray();
    }

    private static byte[] BuiltInBody(BuiltInParameter parameter, string? value) => Pack(writer =>
    {
        bool instance = parameter is BuiltInParameter.ALL_MODEL_MARK or BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS;
        bool cost = parameter == BuiltInParameter.ALL_MODEL_COST;
        writer.Write((ushort)(instance ? OwnerKind.Wall : OwnerKind.WallType));
        writer.Write(ProjectId);
        WritePointer(writer, !cost && value is not null ? 19 : 0);
        WritePointer(writer, 0);
        WritePointer(writer, cost && value is not null ? 38 : 0);
        WritePointer(writer, 0);
        if (value is not null)
        {
            writer.Write(1);
            if (cost)
                writer.Write(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
            writer.Write((int)parameter);
            if (!cost)
                WriteText(writer, value);
        }
    });

    private static BuiltInParameter ImageParameter(OwnerKind owner) => owner == OwnerKind.WallType
        ? BuiltInParameter.ALL_MODEL_TYPE_IMAGE : BuiltInParameter.ALL_MODEL_IMAGE;

    private static byte[] ProjectImageBody(ProjectImage image) => Pack(writer =>
    {
        writer.Write((ushort)image.Owner);
        WriteEmptyElement(writer, ProjectId, material: true, elementIdStore: image.Stored.HasValue);
        WritePointer(writer, 0);
        WritePointer(writer, 0);
        if (image.Stored is int value)
        {
            writer.Write(1);
            writer.Write((int)ImageParameter(image.Owner));
            writer.Write(value);
        }
    });

    private static byte[] UserBody(UserParameter parameter, string? value = null, bool grouped = false, bool cleared = false,
        ProjectAssociation? association = null) => Pack(writer =>
    {
        writer.Write((ushort)parameter.Owner);
        writer.Write(ProjectId);
        bool present = !cleared && (value is not null || parameter.Store == StoreState.Value);
        bool hasTable = present || parameter.Store != StoreState.NullPointer;
        bool reference = parameter.Kind is Kind.Material or Kind.FamilyType;
        WritePointer(writer, hasTable && parameter.Kind == Kind.Text ? 19 : 0);
        WritePointer(writer, hasTable && parameter.Kind is Kind.Integer or Kind.YesNo ? 36 : 0);
        WritePointer(writer, hasTable && parameter.Kind == Kind.Number ? 38 : 0);
        if (reference)
            WritePointer(writer, hasTable ? 54 : 0);
        WritePointer(writer, grouped ? 23 : 0);
        if (reference)
            WritePointer(writer, 54);
        else if (parameter.Store != StoreState.Value)
            WritePointer(writer, 49);
        if (association is not null)
            WritePointer(writer, 68);
        if (hasTable)
        {
            int[] otherIds = parameter.Store switch
            {
                StoreState.Neighbour => [DefinitionId + 1],
                StoreState.Unsorted => [DefinitionId + 1, DefinitionId - 1],
                StoreState.Value when parameter.IdFirst => [DefinitionId - 1],
                _ => [],
            };
            writer.Write(otherIds.Length + (present ? 1 : 0));
            foreach (int id in otherIds.Where(id => id < DefinitionId && parameter.Store != StoreState.Unsorted))
                WriteRow(id, "42");
            if (present)
                WriteRow(DefinitionId, value ?? (parameter.Kind switch
                {
                    Kind.Text => "Old",
                    Kind.Number => "1.5",
                    Kind.Material => OriginalMaterialId.ToString(CultureInfo.InvariantCulture),
                    Kind.FamilyType => NestedTypeId.ToString(CultureInfo.InvariantCulture),
                    _ => "1",
                }));
            foreach (int id in otherIds.Where(id => id > DefinitionId || parameter.Store == StoreState.Unsorted))
                WriteRow(id, "42");
        }
        if (reference)
        {
            writer.Write(1); writer.Write(-123); writer.Write(OriginalMaterialId);
        }
        else if (parameter.Store != StoreState.Value)
            writer.Write(12345678);
        if (association is not null)
            WriteProjectAssociation(writer, association);

        void WriteRow(int id, string text)
        {
            bool valueFirst = parameter.Kind == Kind.Number && !parameter.IdFirst;
            if (!valueFirst)
                writer.Write(id);
            if (parameter.Kind == Kind.Number)
                writer.Write(double.Parse(text, CultureInfo.InvariantCulture));
            else if (parameter.Kind == Kind.Text)
                WriteText(writer, text);
            else
                writer.Write(int.Parse(text, CultureInfo.InvariantCulture));
            if (valueFirst)
                writer.Write(id);
        }
    });

    private static void WriteProjectAssociation(BinaryWriter writer, ProjectAssociation association)
    {
        writer.Write(1);
        writer.Write(association.GlobalId);
        writer.Write((int)association.Property);
        writer.Write(association.Malformed ? 5 : -1);
        writer.Write(false);
    }

    private static byte[] DefinitionBody(UserParameter parameter, int release) => Pack(writer =>
    {
        WriteEmptyElement(writer, DefinitionId, parameter.Kind is Kind.Material or Kind.FamilyType);
        WritePointer(writer, parameter.Kind switch { Kind.Text => 26, Kind.Integer => 27, Kind.YesNo => 28, Kind.Material => 52,
            Kind.FamilyType => 69, _ => 30 });
        writer.Write(1);
        writer.Write(BindingId);
        if (parameter.Shared)
        {
            writer.Write(false); // Read-only in the UI.
            writer.Write(parameter.HideWhenNoValue);
        }
        writer.Write(DefinitionId);
        writer.Write(parameter.ReadOnly);
        if (parameter.Kind == Kind.Material)
        {
            writer.Write(0); writer.Write(-1); writer.Write(false);
        }
        if (parameter.Kind == Kind.FamilyType)
            writer.Write((int)BuiltInCategory.OST_GenericModel);
        if (parameter.Kind == Kind.Integer)
        {
            writer.Write(-100);
            writer.Write(100);
        }
        if (parameter.Kind == Kind.Number)
        {
            if (release == 2020)
                writer.Write((int)(parameter.DefinitionSpec?.Legacy ??
                    (parameter.Currency ? UnitType.UT_Currency : parameter.Measured ? UnitType.UT_Length : UnitType.UT_Number)));
            else if (parameter.DefinitionSpec is { } spec)
                WriteText(writer, spec.Forge);
            else if (parameter.Currency)
                WriteText(writer, "autodesk.spec.measurable:currency-2.0.0");
            else if (parameter.Measured)
                WriteText(writer, "autodesk.spec.aec:length-1.0.0");
            else
                WriteText(writer, release == 2021 ? "autodesk.spec.aec:number-1.0.1" : "autodesk.spec.aec:number-2.0.0");
            writer.Write(1);
            writer.Write(false);
        }
    });

    private static void WriteEmptyElement(BinaryWriter writer, int id, bool material = false, bool textStore = false, bool doubleStore = false,
        bool elementIdStore = false)
    {
        writer.Write(id);
        WritePointer(writer, textStore ? 19 : 0);
        writer.Write(0u);
        WritePointer(writer, doubleStore ? 38 : 0);
        if (material)
            WritePointer(writer, elementIdStore ? 54 : 0);
    }

    private static byte[] GlobalBody(GlobalFixture parameter, string value = "0", int id = DefinitionId, GlobalBinary? binary = null,
        bool conditional = false, int function = 0, int release = 2022) => Pack(writer =>
    {
        writer.Write((ushort)59);
        WriteEmptyElement(writer, id);
        WritePointer(writer, parameter.Kind switch { Kind.Text => 26, Kind.Integer => 27, Kind.YesNo => 28, _ => 30 });
        writer.Write(-1);
        writer.Write(-1);
        writer.Write(parameter.Reporting);
        WriteText(writer, parameter.Kind == Kind.Text ? value : "");
        writer.Write(parameter.Kind == Kind.Number ? double.Parse(value, CultureInfo.InvariantCulture) : 0d);
        writer.Write(-1);
        writer.Write(parameter.Kind is Kind.Integer or Kind.YesNo ? int.Parse(value, CultureInfo.InvariantCulture) : 0);
        writer.Write(parameter.Kind switch { Kind.Text => 3, Kind.Number => 2, _ => 1 });
        WritePointer(writer, conditional || function != 0 ? 66 : binary is not null ? 63 : parameter.Formula ? 62 : 0);
        writer.Write(id);
        writer.Write(false);
        if (parameter.Kind == Kind.Integer)
        {
            writer.Write(-100);
            writer.Write(100);
        }
        if (parameter.Kind == Kind.Number)
        {
            if (release == 2020)
                writer.Write((int)(parameter.SpecId switch
                {
                    null => parameter.Currency ? UnitType.UT_Currency : UnitType.UT_Number,
                    "autodesk.spec.aec:slope-2.0.0" => UnitType.UT_Slope,
                    _ => throw new ArgumentException("The fixture does not supply a legacy unit for this spec.", nameof(parameter)),
                }));
            else
                WriteText(writer, parameter.SpecId ??
                    (parameter.Currency ? "autodesk.spec.measurable:currency-2.0.0" : "autodesk.spec.aec:number-2.0.0"));
            writer.Write(1);
            writer.Write(false);
        }
        if (parameter.Formula)
            writer.Write(ProjectId);
        if (binary is not null)
        {
            writer.Write(binary.Operation);
            WritePointer(writer, binary.ConstantFirst ? 65 : 64);
            WritePointer(writer, binary.ConstantFirst ? 64 : 65);
            if (!binary.ConstantFirst) writer.Write(binary.Parameter);
            writer.Write(binary.Constant);
            WriteText(writer, binary.SpecId ?? "autodesk.spec.aec:number-2.0.0");
            if (binary.ConstantFirst) writer.Write(binary.Parameter);
        }
        if (conditional)
        {
            writer.Write(10);
            writer.Write(3);
            WritePointer(writer, 64);
            WritePointer(writer, 65);
            WritePointer(writer, 65);
            writer.Write(DefinitionId);
            writer.Write(1.5);
            WriteText(writer, "autodesk.spec.aec:number-2.0.0");
            writer.Write(2.5);
            WriteText(writer, "autodesk.spec.aec:number-2.0.0");
        }
        if (function != 0)
        {
            writer.Write(function);
            writer.Write(1);
            WritePointer(writer, 64);
            writer.Write(DefinitionId);
        }
    });

    private static void WritePointer(BinaryWriter writer, int tag)
    {
        writer.Write(tag == 0 ? 0u : uint.MaxValue);
        if (tag != 0)
            writer.Write((ushort)tag);
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        writer.Write(value.Length);
        writer.Write(Encoding.Unicode.GetBytes(value));
    }

    private static byte[] Pack(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }
}
