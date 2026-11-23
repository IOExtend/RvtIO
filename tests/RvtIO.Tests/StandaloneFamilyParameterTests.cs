using System.Globalization;
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

public sealed class StandaloneFamilyParameterTests : IDisposable
{
    private const int OwnerId = 70, ParameterId = 71, FamilyClass = 24, OriginalParameterId = 50;
    private const int ImageId = 73, ImageHolderId = 74, TypeImage = (int)BuiltInParameter.ALL_MODEL_TYPE_IMAGE;
    private const int TypeComments = (int)BuiltInParameter.ALL_MODEL_TYPE_COMMENTS;
    private const int FormulaResultId = 75;
    private const int NestedFamilyId = 80, NestedTypeA = 81, NestedTypeB = 82;
    private sealed record FamilyShape(BuiltInCategory Category, int? DimensionDriver = null, bool HasUnnamedType = false,
        string? UnnamedComment = null, bool Formula = false, bool CurrencyInstance = false);
    private const int UnitsId = 90, CostId = (int)BuiltInParameter.ALL_MODEL_COST, ElevationId = (int)BuiltInParameter.FAMILY_FREEINST_DEFAULT_ELEVATION;
    private const double MillimetreToFeet = 0.001 / 0.3048;

    /// <summary>A numeric built-in value row stored in both types and the active copy, with the units element and the PartAtom entry
    /// that go with it; the fixture holds exactly one display option, the one of this value.</summary>
    private sealed record BuiltInNumber(int Id, string Element, string? DisplayName, string Type, string Unit, double A, double B,
        string Spec, string UnitId, double Accuracy, Func<double, string> Text)
    {
        internal static readonly BuiltInNumber Cost = new(CostId, "Cost", null, "Currency", "$", 12.5, 17.25,
            "autodesk.spec.measurable:currency-2.0.0", "autodesk.unit.unit:currency-1.0.0", 0.01,
            value => value.ToString("F2", CultureInfo.InvariantCulture));
        internal static readonly BuiltInNumber Elevation = new(ElevationId, "Default_Elevation", "Default Elevation", "Length", "mm",
            150 * MillimetreToFeet, 450 * MillimetreToFeet, "autodesk.spec.aec:length-2.0.0", "autodesk.unit.unit:millimeters-1.0.1", 1,
            value => Math.Round(value / MillimetreToFeet, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture));
    }

    private static readonly Guid SharedGuid = new("50b3ce53-fa73-47a1-ae3e-75035a1f17dd");
    private static readonly Guid CreationEpisode = new("f569346e-bacd-43d6-a834-80eaa8fa035f");
    private static PartitionFraming.Tags Tags(int release) => release switch
    {
        2020 => new(779, 778, 780, 781, 782),
        2023 => new(869, 868, 870, 871, 872),
        2024 => new(891, 890, 892, 893, 894),
        2026 => new(931, 930, 932, 933, 934),
        2027 => new(951, 950, 952, 953, 954),
        _ => new(817, 816, 818, 819, 820),
    };
    private static long Id(long id, bool wide) => wide ? (1L << 32) + id : id;

    private static string ImageText(int id) => id == -1 ? "<None>" : "test-image.png";
    private readonly string _folder = Directory.CreateTempSubdirectory("rvtio-rfa-").FullName;
    private string Output => Path.Combine(_folder, "edited.rfa");
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("A", "Новое & longer", "Original B", "Новое & longer", "Original B")]
    [InlineData("B", "New active", "New active", "Original A", "New active")]
    [InlineData("A", "Original A", "Original B", "Original A", "Original B")]
    [InlineData("B", "Wide ID", "Wide ID", "Original A", "Wide ID", 2024)]
    [InlineData("B", "Wide ID", "Wide ID", "Original A", "Wide ID", 2026)]
    [InlineData("A", "New shared value", "Original B", "New shared value", "Original B", 2020, true)]
    public void WritesTheSelectedTypeItsActiveCopyAndItsPartAtom(string typeName, string input, string active, string a, string b,
        int release = 2022, bool shared = false)
    {
        bool wide = release >= 2024;
        string source = CreateSource(release: release, shared: shared);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        using var error = new StringWriter();
        int exit = SetParameterCommand.Run([source, Output, typeName, Id(ParameterId, wide).ToString(), input], TextWriter.Null, error, familyFile: true);
        Assert.True(exit == RvtExitCodes.Success, error.ToString());
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));

        using var root = RootStorage.OpenRead(Output);
        using var atom = root.OpenStream("PartAtom");
        var values = XDocument.Load(atom).Descendants().Where(e => e.Name.LocalName == "Proof").Select(e => e.Value).ToArray();
        Assert.Equal(new[] { a, b }, values);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass, FamilyBody(active, a, b, wide: wide)), ReadFamilyBody(root, release));
    }

    [Fact]
    public void RecalculatesTheFormulaAndEditsMetadataOnlyForTheSelectedTypes()
    {
        var shape = new FamilyShape(BuiltInCategory.OST_GenericModel, Formula: true);
        string source = CreateSource(shape: shape);
        RvtWriter.SetFamilyParameters(source, Output, [new("A", ParameterId, "Changed A"), new("B", ParameterId, "Changed B")]);
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass,
            FamilyBody("Changed B", "Changed A", "Changed B", shape: shape)), ReadFamilyBody(root, 2022));
        using var atom = root.OpenStream("PartAtom");
        Assert.Equal(new[] { "Changed A", "Changed B" }, XDocument.Load(atom)
            .Descendants().Where(e => e.Name.LocalName == "Calculated").Select(e => e.Value));
    }

    [Theory]
    [InlineData(2020)]
    [InlineData(2024)]
    public void WritesCurrencyDefaultsAndTheirCalculatedValuesInBatchOrder(int release)
    {
        var shape = new FamilyShape(BuiltInCategory.OST_GenericModel, Formula: true, CurrencyInstance: true);
        string source = CreateSource(release: release, shape: shape);
        bool wide = release >= 2024;
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        long input = Id(ParameterId, wide);
        RvtWriter.SetFamilyParameters(source, Output, [new("A", input, "17.125"), new("B", input, "5"), new("B", input, "-58.625")]);

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass,
            FamilyBody("-58.625", "17.125", "-58.625", wide: wide, shape: shape)), ReadFamilyBody(root, release));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(ParameterId, "42 $", "invalid-parameter-value")]
    [InlineData(ParameterId, null, "unsupported-parameter-clear")]
    [InlineData(FormulaResultId, "42", "read-only-parameter")]
    public void RefusesInvalidCurrencyBatchesWithoutChangingTheSource(int id, string? value, string kind)
    {
        var shape = new FamilyShape(BuiltInCategory.OST_GenericModel, Formula: true, CurrencyInstance: true);
        string source = CreateSource(shape: shape);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameters(source, Output,
            [new("A", ParameterId, "17.125"), new("A", id, value)]));
        Assert.Equal(kind, error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData("A", "17.25000000001")]
    [InlineData("B", "1e-300")]
    public void RefusesAmbiguousChangesToCurrencyDefaultsWithoutFormulas(string type, string value)
    {
        var shape = new FamilyShape(BuiltInCategory.OST_GenericModel, CurrencyInstance: true);
        string source = CreateSource(shape: shape);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameters(source, Output,
            [new("A", ParameterId, "17.25"), new(type, ParameterId, value)]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(2020, false, "A", -1, -1, NestedTypeA, "Nested : Small")]
    [InlineData(2024, false, "B", NestedTypeA, NestedTypeA, NestedTypeB, "Nested : Large")]
    [InlineData(2027, false, "B", -1, NestedTypeB, -1, null)]
    [InlineData(2027, true, "A", -1, NestedTypeA, NestedTypeB, null)]
    [InlineData(2020, false, "A", -1, -1, NestedTypeA, "Nested : Small", true)]
    [InlineData(2023, false, "B", NestedTypeA, NestedTypeA, NestedTypeB, "Nested : Large", true)]
    [InlineData(2027, false, "B", -1, NestedTypeB, -1, null, true)]
    [InlineData(2027, true, "A", -1, NestedTypeA, NestedTypeB, null, true)]
    public void WritesNestedTypeReferencesAndTheSelectedPartAtomEntry(int release, bool instance, string type,
        int oldA, int oldB, int target, string? display, bool shared = false)
    {
        bool wide = release >= 2024;
        string source = CreateSource(release: release, shared: shared, nested: (oldA, oldB, instance));

        RvtWriter.SetFamilyParameter(source, Output, type, Id(ParameterId, wide),
            (target == -1 ? target : Id(target, wide)).ToString(CultureInfo.InvariantCulture));

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass, FamilyBody("", "", "", wide: wide,
            nested: (type == "A" ? target : oldA, type == "B" ? target : oldB, instance))), ReadFamilyBody(root, release));
        using var atom = root.OpenStream("PartAtom");
        var entry = XDocument.Load(atom).Descendants().Single(e => e.Name.LocalName == "part"
            && e.Elements().Any(child => child.Name.LocalName == "title" && child.Value == type))
            .Elements().SingleOrDefault(e => e.Name.LocalName == "Proof");
        Assert.Equal(display, entry?.Value);
        if (display is not null)
        {
            Assert.Equal("Family type: Generic Models", (string?)entry?.Attribute("typeOfParameter"));
            Assert.Equal(shared ? "shared" : "custom", (string?)entry?.Attribute("type"));
            Assert.Equal(shared ? SharedGuid.ToString() : null, (string?)entry?.Attribute("id"));
        }
    }

    [Theory]
    [InlineData("999", false, "invalid-parameter-value")]
    [InlineData(null, false, "unsupported-parameter-clear")]
    [InlineData("82", true, "unverified-parameter-storage")]
    public void RefusesAnEntireNestedReferenceBatchWhenAnEditFails(string? value, bool staleMetadata, string kind)
    {
        string source = CreateSource(nested: (NestedTypeA, NestedTypeB, false), staleMetadata: staleMetadata);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameters(source, Output,
            [new("B", ParameterId, NestedTypeA.ToString()), new("A", ParameterId, value)]));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void RejectsASharedNestedReferenceBatchWhenThePartAtomGuidDoesNotMatch()
    {
        string source = CreateSource(shared: true, nested: (NestedTypeA, NestedTypeB, false), staleMetadataGuid: true);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameters(source, Output,
            [new("B", ParameterId, NestedTypeA.ToString()), new("A", ParameterId, NestedTypeB.ToString())]));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(false, "read-only-parameter")]
    [InlineData(true, "unverified-parameter-storage")]
    public void RejectsABatchWithAnExplicitFormulaWriteOrAFormulaDrivenAssociation(bool association, string code)
    {
        var shape = new FamilyShape(BuiltInCategory.OST_GenericModel, Formula: true);
        string source = CreateSource(association: association, shape: shape);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameters(source, Output,
            [new("A", ParameterId, "Staged"), new("A", FormulaResultId, "Refused")]));
        Assert.Equal(code, error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void BatchClearsBothTypesAndCanRefillOne()
    {
        string source = CreateSource();
        string manifest = Path.Combine(_folder, "edits.json");
        File.WriteAllText(manifest, """
            [
              {"typeName":"A","parameterId":71,"value":null},
              {"typeName":"B","parameterId":71,"value":null},
              {"typeName":"A","parameterId":71,"value":"Refilled"}
            ]
            """);
        using var error = new StringWriter();
        int exit = SetParameterCommand.RunBatch([source, Output, manifest], TextWriter.Null, error);
        Assert.True(exit == RvtExitCodes.Success, error.ToString());
        using var root = RootStorage.OpenRead(Output);
        using var atom = root.OpenStream("PartAtom");
        var parts = XDocument.Load(atom).Descendants(XName.Get("part", "urn:schemas-autodesk-com:partatom")).ToArray();
        Assert.Equal("Refilled", Assert.Single(parts[0].Elements(), e => (string?)e.Attribute("displayName") == "Proof").Value);
        Assert.Single(parts[1].Elements());
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass, FamilyBody("", "Refilled", "", wide: false)), ReadFamilyBody(root, 2022));
    }

    [Fact]
    public void MissingTypeRefusesTheWholeFamilyBatch()
    {
        string source = CreateSource();
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameters(source, Output,
            [new("B", ParameterId, null), new("Missing", ParameterId, "Never")]));
        Assert.Equal("missing-family-type", error.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void ManifestRequiresAnExplicitValue()
    {
        string source = CreateSource();
        string manifest = Path.Combine(_folder, "edits.json");
        File.WriteAllText(manifest, """[{"typeName":"B","parameterId":71}]""");
        Assert.Equal(RvtExitCodes.Usage, SetParameterCommand.RunBatch([source, Output, manifest], TextWriter.Null, TextWriter.Null));
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData("A", 2020, "Первое")]
    [InlineData("B", 2027, "")]
    public void FirstBuiltInTextCreatesDefaultRowsForTheOtherTypes(string type, int release, string value)
    {
        string source = CreateSource(release: release);
        using var errors = new StringWriter();
        int exit = SetParameterCommand.Run([source, Output, type, "ALL_MODEL_TYPE_COMMENTS", value], TextWriter.Null, errors, familyFile: true);
        Assert.True(exit == RvtExitCodes.Success, errors.ToString());

        using var root = RootStorage.OpenRead(Output);
        string a = type == "A" ? value : "", b = type == "B" ? value : "";
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass,
            FamilyBody("Original B", "Original A", "Original B", wide: release >= 2024, comments: (a, b))),
            ReadFamilyBody(root, release));
        using var atom = root.OpenStream("PartAtom");
        var nodes = XDocument.Load(atom).Descendants(XName.Get("Type_Comments", "http://www.w3.org/2005/Atom")).ToArray();
        if (value.Length == 0)
            Assert.Empty(nodes);
        else
        {
            var node = Assert.Single(nodes);
            Assert.Equal("system", (string?)node.Attribute("type"));
            Assert.Equal(value, node.Value);
        }
    }

    [Fact]
    public void BuiltInBatchReplacesOneTypeAndWritesEmptyTextOnTheActiveType()
    {
        string source = CreateSource(comments: ("Old A", "Old B"));
        string manifest = Path.Combine(_folder, "built-in-edits.json");
        File.WriteAllText(manifest, """
            [
              {"typeName":"B","parameterId":-1010105,"value":""},
              {"typeName":"A","parameterId":-1010105,"value":"New A"}
            ]
            """);
        using var errors = new StringWriter();
        int exit = SetParameterCommand.RunBatch([source, Output, manifest], TextWriter.Null, errors);
        Assert.True(exit == RvtExitCodes.Success, errors.ToString());
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass,
            FamilyBody("Original B", "Original A", "Original B", comments: ("New A", ""))), ReadFamilyBody(root, 2022));
        using var atom = root.OpenStream("PartAtom");
        Assert.Equal("New A", Assert.Single(XDocument.Load(atom).Descendants(
            XName.Get("Type_Comments", "http://www.w3.org/2005/Atom"))).Value);
    }

    [Fact]
    public void BuiltInMetadataMismatchRejectsTheWholeBatch()
    {
        string source = CreateSource(staleMetadata: true, comments: ("Old A", "Old B"));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameters(source, Output,
            [new("B", TypeComments, "Staged"), new("A", TypeComments, "Invalid")]));
        Assert.Equal("unverified-parameter-storage", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Doors, true)]
    [InlineData(BuiltInCategory.OST_Windows, true)]
    [InlineData(BuiltInCategory.OST_Furniture, false)]
    public void WritesIdentityInSupportedCategoriesAndMirrorsTheUnnamedType(BuiltInCategory category, bool unnamed)
    {
        var shape = new FamilyShape(category,
            category == BuiltInCategory.OST_Doors ? (int)BuiltInParameter.FAMILY_WIDTH_PARAM : null, unnamed);
        string source = CreateSource(shape: shape);
        RvtWriter.SetFamilyParameters(source, Output,
            [new("A", TypeComments, "Initial"), new("B", TypeComments, "Other type"), new("A", TypeComments, "Changed")]);

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass,
            FamilyBody("Original B", "Original A", "Original B", comments: ("Changed", "Other type"),
                shape: shape with { UnnamedComment = unnamed ? "Changed" : null })), ReadFamilyBody(root, 2022));
        using var atom = root.OpenStream("PartAtom");
        Assert.Equal(new[] { "Changed", "Other type" }, XDocument.Load(atom)
            .Descendants(XName.Get("Type_Comments", "http://www.w3.org/2005/Atom")).Select(entry => entry.Value));
    }

    [Fact]
    public void RefusesTheWholeBatchWhenAnIdentityParameterIsUsedByADimension()
    {
        string source = CreateSource(shape: new FamilyShape(BuiltInCategory.OST_Doors, TypeComments));
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameters(source, Output,
            [new("B", (int)BuiltInParameter.ALL_MODEL_MODEL, "Staged"), new("A", TypeComments, "Invalid")]));
        Assert.Equal("unverified-parameter-storage", refused.Kind);
        Assert.Contains("dimension association", refused.Message);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData("FIRE_RATING")]
    [InlineData("DOOR_FIRE_RATING")]
    public void AcceptsBothNamesForDoorFireRating(string parameterName)
    {
        string source = CreateSource(shape: new FamilyShape(BuiltInCategory.OST_Doors));
        using var errors = new StringWriter();
        int exit = SetParameterCommand.Run([source, Output, "A", parameterName, "90 min"], TextWriter.Null, errors, familyFile: true);
        Assert.True(exit == RvtExitCodes.Success, errors.ToString());

        using var root = RootStorage.OpenRead(Output);
        using var atom = root.OpenStream("PartAtom");
        Assert.Equal("90 min", Assert.Single(XDocument.Load(atom)
            .Descendants(XName.Get("Fire_Rating", "http://www.w3.org/2005/Atom"))).Value);
    }

    [Theory]
    [InlineData(2020, "A")]
    [InlineData(2027, "B")]
    public void WritesImportedTypeImageAndInitializesOtherTypes(int release, string type)
    {
        bool wide = release >= 2024;
        string source = CreateSource(release: release, importedImage: true);
        RvtWriter.SetFamilyParameter(source, Output, type, TypeImage, Id(ImageId, wide).ToString());

        using var root = RootStorage.OpenRead(Output);
        var a = type == "A" ? ImageId : -1;
        var b = type == "B" ? ImageId : -1;
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass,
            FamilyBody("Original B", "Original A", "Original B", wide: wide, images: (a, b))), ReadFamilyBody(root, release));
        using var atom = root.OpenStream("PartAtom");
        var titles = XDocument.Load(atom).Descendants(XName.Get("Type_Image", "http://www.w3.org/2005/Atom")).Select(e => e.Value);
        Assert.Equal(type == "A" ? ["test-image.png", "<None>"] : new[] { "<None>", "test-image.png" }, titles);
    }

    [Fact]
    public void ATypeImageWithoutAMatchingHolderRejectsTheWholeBatch()
    {
        string source = CreateSource(importedImage: true, imageHolderMismatch: true);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameters(source, Output,
            [new("B", TypeComments, "Staged"), new("A", TypeImage, ImageId.ToString())]));
        Assert.Equal("invalid-parameter-value", refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(2020, "A", -1, ImageId)]
    [InlineData(2027, "B", -1, ImageId)]
    [InlineData(2027, "B", ImageId, -1)]
    public void WritesCustomTypeImageAndItsMetadata(int release, string type, int before, int after)
    {
        bool wide = release >= 2024;
        string source = CreateSource(release: release, customImage: (before, before));
        RvtWriter.SetFamilyParameter(source, Output, type, Id(ParameterId, wide),
            (after == -1 ? -1 : Id(after, wide)).ToString(CultureInfo.InvariantCulture));

        using var root = RootStorage.OpenRead(Output);
        var a = type == "A" ? after : before;
        var b = type == "B" ? after : before;
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass,
            FamilyBody("Original B", "Original A", "Original B", wide: wide, nested: (a, b, false))), ReadFamilyBody(root, release));
        using var atom = root.OpenStream("PartAtom");
        Assert.Equal(new[] { ImageText(a), ImageText(b) }, XDocument.Load(atom)
            .Descendants(XName.Get("Proof", "http://www.w3.org/2005/Atom")).Select(entry => entry.Value));
    }

    [Theory]
    [InlineData("73", false, "unverified-parameter-storage")]
    [InlineData("71", true, "invalid-parameter-value")]
    [InlineData(null, true, "unsupported-parameter-clear")]
    public void RejectsTheWholeBatchWhenACustomTypeImageEditIsInvalid(string? value, bool validMetadata, string kind)
    {
        string source = CreateSource(customImage: (-1, -1), staleMetadata: !validMetadata);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var refused = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameters(source, Output,
            [new("B", ParameterId, ImageId.ToString()), new("A", ParameterId, value)]));
        Assert.Equal(kind, refused.Kind);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData("A", 2022, false, true)]
    [InlineData("B", 2020, true, true)]
    [InlineData("A", 2023, true, true)]
    [InlineData("B", 2027, true, true)]
    [InlineData("A", 2027, true, false)]
    public void FirstTextValueCreatesMetadataForTheSelectedType(string type, int release, bool shared, bool visible)
    {
        const string caption = "9 Русский / текст", value = "Новое & first value";
        string? forgeId = release switch
        {
            2023 => $"revit.local.shared:{CreationEpisode:N}{OriginalParameterId:x8}-1.0.0",
            2027 => $"revit.local.shared:{SharedGuid:N}-2.0.0",
            _ => null,
        };
        string source = CreateSource(release: release, shared: shared, blankType: type, caption: caption, visible: visible, forgeId: forgeId);
        var result = RvtWriter.SetFamilyParameter(source, Output, type, Id(ParameterId, release >= 2024), value);
        Assert.Equal(1, result.RewrittenRecordCount);
        Assert.Equal(visible ? 2 : 1, result.ReplacedStreamCount);

        using var root = RootStorage.OpenRead(Output);
        using var atom = root.OpenStream("PartAtom");
        var parts = XDocument.Load(atom).Descendants(XName.Get("part", "urn:schemas-autodesk-com:partatom")).ToArray();
        var selected = Assert.Single(parts, p => p.Elements().First().Value == type);
        var other = Assert.Single(parts, p => p != selected);
        Assert.Equal(type == "A" ? "Original B" : "Original A", other.Elements().Last().Value);
        if (visible)
        {
            var entry = Assert.Single(selected.Elements(), e => (string?)e.Attribute("displayName") == caption);
            Assert.Equal("_9_Русский___текст", entry.Name.LocalName);
            Assert.Equal(shared ? "shared" : "custom", (string?)entry.Attribute("type"));
            Assert.Equal(shared ? SharedGuid.ToString() : null, (string?)entry.Attribute("id"));
            Assert.Equal(value, entry.Value);
        }
        else
            Assert.Single(selected.Elements());
        string a = type == "A" ? value : "Original A", b = type == "B" ? value : "Original B";
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass, FamilyBody(b, a, b, wide: release >= 2024)), ReadFamilyBody(root, release));
    }

    private static XElement[] ReadParts(RootStorage root)
    {
        using var atom = root.OpenStream("PartAtom");
        return XDocument.Load(atom).Descendants(XName.Get("part", "urn:schemas-autodesk-com:partatom")).ToArray();
    }

    [Theory]
    [InlineData("Height", "A", "New A")]
    [InlineData("Glass", "B", "Новое значение")]
    public void ReplacesPartAtomEntriesThatRevitWritesWithoutDisplayName(string caption, string type, string value)
    {
        string source = CreateSource(caption: caption, plainName: true);
        RvtWriter.SetFamilyParameter(source, Output, type, ParameterId, value);

        using var root = RootStorage.OpenRead(Output);
        string a = type == "A" ? value : "Original A", b = type == "B" ? value : "Original B";
        foreach (var part in ReadParts(root))
        {
            var entry = Assert.Single(part.Elements(), e => e.Name.LocalName == caption);
            Assert.Null(entry.Attribute("displayName"));
            Assert.Equal(part.Elements().First().Value == "A" ? a : b, entry.Value);
        }
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass, FamilyBody(type == "B" ? value : "Original B", a, b)),
            ReadFamilyBody(root, 2022));
    }

    [Fact]
    public void ClearsAPartAtomEntryThatRevitWritesWithoutDisplayName()
    {
        string source = CreateSource(caption: "Height", plainName: true);
        RvtWriter.SetFamilyParameter(source, Output, "A", ParameterId, "");

        using var root = RootStorage.OpenRead(Output);
        var parts = ReadParts(root);
        Assert.Single(parts[0].Elements());
        Assert.Equal("Original B", Assert.Single(parts[1].Elements(), e => e.Name.LocalName == "Height").Value);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass, FamilyBody("Original B", "", "Original B")), ReadFamilyBody(root, 2022));
    }

    [Fact]
    public void FirstValueLeavesThePlainEntryOfTheOtherTypeUntouched()
    {
        string source = CreateSource(caption: "Height", plainName: true, blankType: "B");
        RvtWriter.SetFamilyParameter(source, Output, "B", ParameterId, "First");

        using var root = RootStorage.OpenRead(Output);
        var parts = ReadParts(root);
        var original = Assert.Single(parts[0].Elements(), e => e.Name.LocalName == "Height");
        Assert.Null(original.Attribute("displayName"));
        Assert.Equal("Original A", original.Value);
        Assert.Equal("First", Assert.Single(parts[1].Elements(), e => e.Name.LocalName == "Height").Value);
    }

    [Fact]
    public void ReplacesAFamilyTypeReferenceEntryThatRevitWritesWithoutDisplayName()
    {
        string source = CreateSource(caption: "Glass", plainName: true, nested: (NestedTypeA, NestedTypeA, false));
        RvtWriter.SetFamilyParameter(source, Output, "B", ParameterId, NestedTypeB.ToString());

        using var root = RootStorage.OpenRead(Output);
        var parts = ReadParts(root);
        Assert.Equal("Nested : Small", Assert.Single(parts[0].Elements(), e => e.Name.LocalName == "Glass").Value);
        var replaced = Assert.Single(parts[1].Elements(), e => e.Name.LocalName == "Glass");
        Assert.Null(replaced.Attribute("displayName"));
        Assert.Equal("Nested : Large", replaced.Value);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass, FamilyBody("", "", "",
            nested: (NestedTypeA, NestedTypeB, false))), ReadFamilyBody(root, 2022));
    }

    [Fact]
    public void RefusesAFirstSharedValueWhenTheGuidsDisagree()
    {
        string source = CreateSource(release: 2027, shared: true, blankType: "B", forgeId: $"revit.local.shared:{Guid.Empty:N}-2.0.0");
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameter(source, Output, "B", Id(ParameterId, true), "First"));
        Assert.Contains("GUID and Forge type ID disagree", error.Message);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void RefusesAForgeIdFromADifferentElementHistory()
    {
        string forgeId = $"revit.local.shared:{Guid.Empty:N}{OriginalParameterId:x8}-1.0.0";
        string source = CreateSource(release: 2023, shared: true, blankType: "B", forgeId: forgeId);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameter(source, Output, "B", ParameterId, "First"));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.False(File.Exists(Output));
    }

    private static XElement[] NumberEntries(RootStorage root, string element)
    {
        using var atom = root.OpenStream("PartAtom");
        return XDocument.Load(atom).Descendants(XName.Get(element, "http://www.w3.org/2005/Atom")).ToArray();
    }

    [Fact]
    public void WritesABuiltInCostAndItsFormattedPartAtomEntry()
    {
        var cost = BuiltInNumber.Cost;
        string source = CreateSource(number: cost);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        RvtWriter.SetFamilyParameter(source, Output, "A", CostId, "20");

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass, FamilyBody("Original B", "Original A", "Original B", number: (CostId, 20, cost.B))),
            ReadFamilyBody(root, 2022));
        var entries = NumberEntries(root, "Cost");
        Assert.Equal(["20.00", "17.25"], entries.Select(e => e.Value));
        Assert.All(entries, e => Assert.Equal("$", (string?)e.Attribute("units")));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void WritesABuiltInCostToTheActiveTypeAndItsCurrentValue()
    {
        var cost = BuiltInNumber.Cost;
        string source = CreateSource(number: cost);
        RvtWriter.SetFamilyParameter(source, Output, "B", CostId, "-3.5");

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass, FamilyBody("Original B", "Original A", "Original B", number: (CostId, cost.A, -3.5))),
            ReadFamilyBody(root, 2022));
        Assert.Equal(["12.50", "-3.50"], NumberEntries(root, "Cost").Select(e => e.Value));
    }

    [Theory]
    [InlineData("A", "12.5000000000001")]
    [InlineData("B", "17.25000000000001")]
    public void RefusesABuiltInCostChangeThatRevitMaySuppress(string type, string value)
    {
        string source = CreateSource(number: BuiltInNumber.Cost);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameter(source, Output, type, CostId, value));
        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Contains("comparison tolerance", error.Message);
        Assert.False(File.Exists(Output));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void KeepsABuiltInCostThatAlreadyHasTheRequestedValue()
    {
        string source = CreateSource(number: BuiltInNumber.Cost);
        RvtWriter.SetFamilyParameter(source, Output, "A", CostId, "12.5");

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass, FamilyBody("Original B", "Original A", "Original B",
            number: (CostId, BuiltInNumber.Cost.A, BuiltInNumber.Cost.B))), ReadFamilyBody(root, 2022));
        Assert.Equal(["12.50", "17.25"], NumberEntries(root, "Cost").Select(e => e.Value));
    }

    [Theory]
    [InlineData("A", "300 mm", 300, 450)]
    [InlineData("B", "25 mm", 150, 25)]
    public void WritesADefaultElevationAndItsFormattedPartAtomEntry(string type, string value, double a, double b)
    {
        var elevation = BuiltInNumber.Elevation;
        string source = CreateSource(number: elevation);
        RvtWriter.SetFamilyParameter(source, Output, type, ElevationId, value);

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(PartitionStreamEditorTests.TaggedBody(FamilyClass, FamilyBody("Original B", "Original A", "Original B",
            number: (ElevationId, a * MillimetreToFeet, b * MillimetreToFeet))), ReadFamilyBody(root, 2022));
        var entries = NumberEntries(root, "Default_Elevation");
        Assert.Equal([elevation.Text(a * MillimetreToFeet), elevation.Text(b * MillimetreToFeet)], entries.Select(e => e.Value));
        Assert.All(entries, e => Assert.Equal("mm", (string?)e.Attribute("units")));
        Assert.All(entries, e => Assert.Equal("Default Elevation", (string?)e.Attribute("displayName")));
    }

    [Theory]
    [InlineData("300")]
    [InlineData("300 furlongs")]
    public void RefusesADefaultElevationWithoutAKnownLengthUnit(string value)
    {
        string source = CreateSource(number: BuiltInNumber.Elevation);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameter(source, Output, "A", ElevationId, value));
        Assert.Equal("invalid-parameter-value", error.Kind);
        Assert.False(File.Exists(Output));
    }

    private static byte[] ReadFamilyBody(RootStorage root, int release)
    {
        bool wide = release >= 2024;
        using var part = root.OpenStorage("Partitions").OpenStream("0");
        var raw = new byte[part.Length];
        part.ReadExactly(raw);
        var matches = new List<byte[]>();
        Assert.True(PartitionFraming.Walk(raw, Tags(release), wide ? 8 : 4, r =>
        {
            if (r.Block == 0 && r.Channel == 102 && r.Owner == (ulong)Id(OwnerId, wide))
                matches.Add(r.Body.ToArray());
        }, out var failure, out _), failure);
        return Assert.Single(matches);
    }

    [Theory]
    [InlineData("Missing", false, false, false, false, "missing-family-type")]
    [InlineData("A", true, false, false, false, "read-only-parameter")]
    [InlineData("A", false, true, false, false, "unverified-parameter-storage")]
    [InlineData("A", false, false, true, false, "unverified-parameter-storage")]
    [InlineData("A", false, false, false, true, "unverified-parameter-storage")]
    public void RefusesAmbiguousOrDrivenEdits(string type, bool readOnly, bool reporting, bool association, bool staleMetadata, string code)
    {
        string source = CreateSource(readOnly, reporting, association, staleMetadata);
        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetFamilyParameter(source, Output, type, ParameterId, "New"));
        Assert.Equal(code, error.Kind);
        Assert.False(File.Exists(Output));
    }

    private string CreateSource(bool readOnly = false, bool reporting = false, bool association = false, bool staleMetadata = false,
        int release = 2022, bool shared = false, string? blankType = null, string caption = "Proof", bool visible = true,
        string? forgeId = null, (string A, string B)? comments = null, bool importedImage = false, bool imageHolderMismatch = false,
        FamilyShape? shape = null, (int A, int B, bool Instance)? nested = null, bool staleMetadataGuid = false,
        (int A, int B)? customImage = null, bool plainName = false, BuiltInNumber? number = null)
    {
        var references = customImage is { } values ? (values.A, values.B, false) : nested;
        importedImage |= customImage.HasValue;
        bool currencyInstance = shape?.CurrencyInstance == true;
        bool wide = release >= 2024;
        int width = wide ? 8 : 4;
        var tags = Tags(release);
        var schema = new SchemaStreamEditorTests.Writer();
        void Add(string name, params Fld[] fields) => schema.Top(new(name, new NoRef(), 1, fields, []));
        Fld Reference(string name, int id, byte mods = 0) => new(name, 14, mods, Target: new Back(id));
        Fld PointerField(string name) => new(name, 14, 2);
        Add("Identifier", new Fld(wide ? "m_id64" : "m_id", wide ? (byte)11 : (byte)4)); // 12
        Add("OriginalElementId", Reference("m_id", 12)); // 13
        Add("ElementId", Reference("m_id", 12)); // 14
        Add("ElemHistory", Reference("m_originalElementId", 13), new("m_creationDate", 4), new("m_lastModificationDate", 4), new("m_lastUserModificationDate", 4)); // 15
        Add("ElemRec", Reference("m_id", 14), Reference("m_history", 15), new("m_partitionId", 4), Reference("m_OwningElementId", 14)); // 16
        Add("ElemTable", Reference("m_elemArr", 16, 0x50)); // 17
        Add("Element", Reference("m_id", 14)); // 18
        Add("NamedParam", new Fld("m_str", 8), new("m_oExpression", 14, 1), new("m_value", 7), Reference("m_elemId", 14),
            Reference("m_paramId", 14), new("m_int", 4), new("m_instance", 1), new("m_reporting", 1)); // 19
        Add("FamilyParams", Reference("m_params", 19, 0x50)); // 20
        Add("FamilyTypePair", new Fld("name", 8), Reference("params", 20)); // 21
        Add("FamilyTypeTable", new Fld("m_idx", 4), Reference("m_pairs", 21, 0x50)); // 22
        schema.Top(new("FamilyBase", new Back(18), 1,
            [PointerField("m_familyParams"), PointerField("m_pFamilyTypes"), new("m_contentProtected", 1), new("m_bHasLabeledDimensions", 1),
                Reference("m_categoryId", 14), new("m_inPlace", 1), new("m_name", 8)], [])); // 23
        schema.Top(new("Family", new Back(23), 1, [PointerField("m_oFamDimConstrMgr")], [])); // 24
        Add("Filler25"); // 25
        List<Fld> defFields = [Reference("m_paramElemId", 14), new("m_caption", 8), new("m_readOnly", 1), new("m_userVisible", 1)];
        if (forgeId is not null)
            defFields.Add(new("m_typeId", 8));
        Add("ParamDef", defFields.ToArray()); // 26
        schema.Top(new("ParamDefString", new Back(26), 1, [], [])); // 27
        schema.Top(new("ParamElem", new Back(18), 1, [PointerField("m_pParamDef")], [])); // 28
        schema.Top(new("ParamElemFamily", new Back(28), 1, [new("m_instanceParam", 1)], [])); // 29
        List<Fld> documentFields = [Reference("m_ownerFamilyId", 14), Reference("m_ownerFamilyContainingGroupId", 14)];
        if (number is not null)
            documentFields.AddRange([PointerField("m_pSchemaStorage"), PointerField("m_pUnitsTracking")]);
        Add("ADocument", documentFields.ToArray()); // 30
        Add("ParamIdPair", Reference("m_famParamId", 14)); // 31
        schema.Top(new("ExtrusionElem", new Back(18), 1, [Reference("m_driver", 31)], [])); // 32
        Add("GUIDvalue", new Fld("m_guid", 9)); // 33
        Add("ExternalParamKey", Reference("m_guidValue", 33)); // 34
        schema.Top(new("ParamElemExternal", new Back(28), 1,
            [Reference("m_bindingIds", 14, 0x50), Reference("m_externalParamKey", 34)], [])); // 35
        Add("DocumentHistory"); // 36
        Add("SymbolInfo", new Fld("m_name", 8)); // 37
        schema.Top(new("Symbol", new Back(18), 1, [PointerField("m_symbolInfo")], [])); // 38
        schema.Top(new("ImageSymbol", new Back(38), 1,
            [Reference("m_idImageHolder", 14), new("m_flags", 4), Reference("m_viewId", 14), Reference("m_ownerSymbolId", 14),
                new("m_widthInPixels", 4), new("m_heightInPixels", 4)], [])); // 39
        Add("ARasterImage", new Fld("m_compressedImage", 2, 0x50), new("m_width", 4), new("m_height", 4)); // 40
        schema.Top(new("ImageHolder", new Back(18), 1,
            [new("m_pRasterImage", 14, 1), Reference("m_idImageSymbol", 14), new("m_flags", 4)], [])); // 41
        Add("ParamExprEntry", new Fld("m_coef", 7), Reference("m_paramId", 14)); // 42
        Add("ParamExpr", Reference("m_entries", 42, 0x50), new Fld("m_msgId", 4), Reference("m_elemId", 14)); // 43
        Add("ElementIdIntPair", Reference("m_elementId", 14), new Fld("m_int", 4)); // 44
        Add("std::pair< ElementIdIntPair, ParamExpr >", Reference("first", 44), Reference("second", 43)); // 45
        Add("FamDimConstrMgrImpl", Reference("m_paramExprs", 45, 0x50), new Fld("m_drivenDimSegs", 4, 0x50),
            Reference("m_additionalParamExprs", 45, 0x50), new("m_dimSegDataMap", 4, 0x50)); // 46
        Add("ParameterExpression", Reference("m_paramId", 14)); // 47
        schema.Top(new("ParamDefFamType", new Back(26), 1, [Reference("m_categoryId", 14)], [])); // 48
        schema.Top(new("FamilySymbol", new Back(38), 1, [Reference("m_familyId", 14),
            Reference("m_masterId", 14), Reference("m_ownerInstanceId", 14)], [])); // 49
        schema.Top(new("ParamDefImageSymbolBrowseEdit", new Back(26), 1, [], [])); // 50
        schema.Top(new("ParamDefValue", new Back(26), 1,
            [release == 2020 ? new("m_unitType", 4) : new("m_specTypeId", 8), new("m_restriction", 4), new("m_boundless", 1)], [])); // 51
        int fillers = 52;
        if (number is not null)
        {
            // The units element of the modern layout: one display option per spec, each a pair of the spec and its format options.
            Add("ESSchemaStorage"); // 52
            Add("UnitsTracking", Reference("m_unitsElemId", 14)); // 53
            Add("ForgeTypeId", new Fld("m_typeId", 8)); // 54
            Add("FormatOptions", Reference("m_symbolTypeId", 54), Reference("m_unitTypeId", 54), new("m_accuracy", 7), new("m_roundingMethod", 4),
                new("m_bSuppressLeadingZeros", 1), new("m_bSuppressSpaces", 1), new("m_bSuppressTrailingZeros", 1), new("m_bUseDefault", 1),
                new("m_bUseGrouping", 1), new("m_bUsePlusPrefix", 1)); // 55
            Add("std::pair< ForgeTypeId, FormatOptions >", Reference("first", 54), Reference("second", 55)); // 56
            Add("AUnits", Reference("m_formatOptionsMap", 56, 0x50), new("m_universalUnitSystem", 4), new("m_decimalSymbol", 4),
                new("m_digitGroupingSymbol", 4), new("m_digitGroupingAmount", 4)); // 57
            schema.Top(new("UnitsElem", new Back(18), 1, [Reference("m_units", 57)], [])); // 58
            fillers = 59;
        }
        for (int i = fillers; i < tags.ContentKey; i++)
            Add("Filler" + i);
        foreach (string marker in new[] { "ContentKey", "ContentMarker", "SegmentMarker", "SegmentCheckback", "SignatureMarker" })
            Add(marker);

        var file = Path.Combine(_folder, "source.rfa");
        using var root = RootStorage.Create(file, OpenMcdf.Version.V4);
        using (var basic = root.CreateStream("BasicFileInfo"))
            basic.Write(RvtWriterTests.BuildBasicFileInfoBytes(release, lastSavePath: file));
        using (var formats = root.CreateStorage("Formats").CreateStream("Latest"))
            formats.Write(PagedStreamCodec.Encode([], schema.Build()));
        var global = root.CreateStorage("Global");
        using (var header = global.CreateStream("Latest"))
            header.Write(PagedStreamCodec.Encode(new byte[8], Pack(w =>
            {
                w.Write((ushort)30); WriteId(w, OwnerId, wide); WriteId(w, -1, wide);
                if (number is not null)
                {
                    Pointer(w, 52); Pointer(w, 53); // the schema storage (empty) and the units tracking object that names the units element
                    WriteId(w, UnitsId, wide);
                }
            })));
        using (var table = global.CreateStream("ElemTable"))
        {
            var ids = new List<int> { OwnerId, ParameterId };
            if (association)
                ids.Add(72);
            if (importedImage)
                ids.AddRange([ImageId, ImageHolderId]);
            if (shape?.Formula == true)
                ids.Add(FormulaResultId);
            if (nested.HasValue)
                ids.AddRange([NestedFamilyId, NestedTypeA, NestedTypeB]);
            if (number is not null)
                ids.Add(UnitsId);
            byte[] bytes = Pack(w =>
            {
                w.Write((ushort)17); w.Write(ids.Count);
                foreach (int id in ids)
                {
                    WriteId(w, id, wide);
                    WriteId(w, release == 2023 && id == ParameterId ? OriginalParameterId : id, wide);
                    w.Write(new byte[16 + width]);
                }
            });
            table.Write(PagedStreamCodec.Encode(new byte[8], bytes));
        }
        if (release == 2023)
        {
            using var history = global.CreateStream("History");
            var bytes = new byte[127];
            bytes[0] = 36; bytes[2] = 1; bytes[106] = 1;
            CreationEpisode.TryWriteBytes(bytes.AsSpan(110));
            history.Write(PagedStreamCodec.Encode(new byte[8], bytes));
        }
        using (var part = root.CreateStorage("Partitions").CreateStream("0"))
        {
            string a = currencyInstance ? "0" : blankType == "A" ? "" : "Original A";
            string b = currencyInstance ? "0" : blankType == "B" ? "" : "Original B";
            var records = new List<(ulong, ushort, byte[])>
            {
                ((ulong)Id(OwnerId, wide), FamilyClass, FamilyBody(b, a, b, reporting, wide, comments, shape: shape, nested: references,
                    number: number is null ? null : (number.Id, number.A, number.B))),
                ((ulong)Id(ParameterId, wide), shared ? (ushort)35 : (ushort)29, Pack(w =>
                {
                    WriteId(w, ParameterId, wide);
                    Pointer(w, currencyInstance ? (ushort)51 : customImage.HasValue ? (ushort)50 : nested.HasValue ? (ushort)48 : (ushort)27);
                    if (shared)
                    {
                        w.Write(0); w.Write(SharedGuid.ToByteArray());
                    }
                    else
                        w.Write(currencyInstance || nested?.Instance == true);
                    WriteId(w, ParameterId, wide); Text(w, caption); w.Write(readOnly); w.Write(visible);
                    if (forgeId is not null)
                        Text(w, forgeId);
                    if (nested.HasValue)
                        WriteId(w, (int)BuiltInCategory.OST_GenericModel, wide);
                    if (currencyInstance)
                        WriteCurrencySpec(w);
                })),
            };
            if (number is not null)
                records.Add(((ulong)Id(UnitsId, wide), 58, Pack(w =>
                {
                    WriteId(w, UnitsId, wide);
                    w.Write(1); Text(w, number.Spec);                  // the one display option: its spec,
                    Text(w, ""); Text(w, number.UnitId);               // no symbol, the unit
                    w.Write(number.Accuracy); w.Write(0); w.Write(new byte[6]);
                    w.Write(1); w.Write(0); w.Write(0); w.Write(0);   // metric, decimal point, grouping (unused)
                })));
            if (nested.HasValue)
            {
                records.Add(((ulong)Id(NestedFamilyId, wide), FamilyClass, Pack(w =>
                {
                    WriteId(w, NestedFamilyId, wide); w.Write(0u); w.Write(0u); w.Write(false); w.Write(false);
                    WriteId(w, (int)BuiltInCategory.OST_GenericModel, wide); w.Write(false); Text(w, "Nested"); w.Write(0u);
                })));
                foreach (int id in new[] { NestedTypeA, NestedTypeB })
                    records.Add(((ulong)Id(id, wide), 49, Pack(w =>
                    {
                        WriteId(w, id, wide); Pointer(w, 37); WriteId(w, NestedFamilyId, wide);
                        WriteId(w, -1, wide); WriteId(w, -1, wide);
                        Text(w, id == NestedTypeA ? "Small" : "Large");
                    })));
            }
            if (association)
                records.Add(((ulong)Id(72, wide), 32,
                    Pack(w => { WriteId(w, 72, wide); WriteId(w, shape?.Formula == true ? FormulaResultId : ParameterId, wide); })));
            if (shape?.Formula == true)
                records.Add(((ulong)Id(FormulaResultId, wide), 29, Pack(w =>
                {
                    WriteId(w, FormulaResultId, wide); Pointer(w, currencyInstance ? (ushort)51 : (ushort)27); w.Write(currencyInstance);
                    WriteId(w, FormulaResultId, wide); Text(w, "Calculated"); w.Write(false); w.Write(true);
                    if (currencyInstance)
                        WriteCurrencySpec(w);
                })));
            if (importedImage)
            {
                records.Add(((ulong)Id(ImageId, wide), 39, Pack(w =>
                {
                    WriteId(w, ImageId, wide); Pointer(w, 37); WriteId(w, ImageHolderId, wide);
                    w.Write(0); WriteId(w, -1, wide); WriteId(w, -1, wide); w.Write(2); w.Write(2);
                    Text(w, "test-image.png");
                })));
                records.Add(((ulong)Id(ImageHolderId, wide), 41, Pack(w =>
                {
                    WriteId(w, ImageHolderId, wide); Pointer(w, 40);
                    WriteId(w, imageHolderMismatch ? OwnerId : ImageId, wide); w.Write(0);
                    w.Write(4); w.Write(new byte[] { 1, 2, 3, 4 }); w.Write(2); w.Write(2);
                })));
            }
            var block = PartitionStreamEditorTests.MakeStandardBlock(width, keyed: false, records.ToArray());
            part.Write(PartitionStreamEditorTests.BuildPartitionStream(width, tags, block));
        }
        using var atom = root.CreateStream("PartAtom");
        XNamespace ns = "http://www.w3.org/2005/Atom", partAtom = "urn:schemas-autodesk-com:partatom";
        new XDocument(new XElement(ns + "entry",
            new XElement(ns + "category", new XElement(ns + "term", "Generic Models"), new XElement(ns + "scheme", "adsk:revit:grouping")),
            new XElement(partAtom + "family",
            Part("A", staleMetadata ? "Stale" : "Original A"), Part("B", "Original B")))).Save(atom);
        return file;

        void WriteCurrencySpec(BinaryWriter writer)
        {
            if (release == 2020)
                writer.Write((int)UnitType.UT_Currency);
            else
                Text(writer, "autodesk.spec.measurable:currency-2.0.0");
            writer.Write(1);
            writer.Write(false);
        }

        XElement Part(string type, string value)
        {
            var node = new XElement(partAtom + "part", new XElement(ns + "title", type));
            int reference = type == "A" ? nested?.A ?? -1 : nested?.B ?? -1;
            if (!currencyInstance && type != blankType && (nested is null || !nested.Value.Instance && reference != -1))
            {
                if (nested.HasValue && !(staleMetadata && type == "A"))
                    value = reference == NestedTypeA ? "Nested : Small" : "Nested : Large";
                if (customImage is { } image && !(staleMetadata && type == "A"))
                    value = ImageText(type == "A" ? image.A : image.B);
                // Revit omits displayName when the element name already equals the caption.
                var parameter = new XElement(ns + (plainName ? caption : "Proof"), plainName ? null : new XAttribute("displayName", caption),
                    new XAttribute("type", shared ? "shared" : "custom"),
                    new XAttribute("typeOfParameter", customImage.HasValue ? "Image" : nested.HasValue ? "Family type: Generic Models" : "Text"), value);
                if (shared)
                    parameter.SetAttributeValue("id", staleMetadataGuid && type == "A" ? CreationEpisode : SharedGuid);
                node.Add(parameter);
            }
            string? comment = type == "A" ? comments?.A : comments?.B;
            if (!string.IsNullOrEmpty(comment))
                node.Add(new XElement(ns + "Type_Comments", new XAttribute("displayName", "Type Comments"),
                    new XAttribute("type", "system"), new XAttribute("typeOfParameter", "Text"),
                    staleMetadata && type == "A" ? "Stale" : comment));
            if (number is not null)
            {
                var entry = new XElement(ns + number.Element, new XAttribute("type", "system"), new XAttribute("typeOfParameter", number.Type),
                    new XAttribute("units", number.Unit), number.Text(type == "A" ? number.A : number.B));
                if (number.DisplayName is not null)
                    entry.SetAttributeValue("displayName", number.DisplayName);
                node.Add(entry);
            }
            if (shape?.Formula == true && !currencyInstance)
                node.Add(new XElement(ns + "Calculated", new XAttribute("displayName", "Calculated"),
                    new XAttribute("type", "custom"), new XAttribute("typeOfParameter", "Text"), value));
            return node;
        }
    }

    private static byte[] FamilyBody(string active, string a, string b, bool reporting = false, bool wide = false,
        (string A, string B)? comments = null, (int A, int B)? images = null, FamilyShape? shape = null,
        (int A, int B, bool Instance)? nested = null, (int Id, double A, double B)? number = null) => Pack(w =>
    {
        WriteId(w, OwnerId, wide); Pointer(w, 20); Pointer(w, 22); w.Write(false); w.Write(false);
        WriteId(w, (int)(shape?.Category ?? BuiltInCategory.OST_GenericModel), wide); w.Write(false); Text(w, "Host"); Pointer(w, 46);
        bool formula = shape?.Formula == true;
        bool currencyInstance = shape?.CurrencyInstance == true;
        Store(w, active, reporting, wide, comments?.B, images?.B, formula, nested?.B, nested?.Instance ?? false, currencyInstance, number is { } n0 ? (n0.Id, n0.B) : null);
        if (shape?.HasUnnamedType == true)
        {
            w.Write(2); w.Write(3);
            Text(w, " "); Store(w, "", reporting, wide, shape.UnnamedComment, null, formula);
        }
        else
        {
            w.Write(1); w.Write(2);
        }
        Text(w, "A"); Store(w, a, reporting, wide, comments?.A, images?.A, formula, nested?.A, nested?.Instance ?? false, currencyInstance, number is { } nA ? (nA.Id, nA.A) : null);
        Text(w, "B"); Store(w, b, reporting, wide, comments?.B, images?.B, formula, nested?.B, nested?.Instance ?? false, currencyInstance, number is { } nB ? (nB.Id, nB.B) : null);
        if (shape?.DimensionDriver is int driver)
        {
            w.Write(1); WriteId(w, -1, wide); w.Write(0);
            w.Write(1); w.Write(1d); WriteId(w, driver, wide); w.Write(0); WriteId(w, -1, wide);
        }
        else
            w.Write(0);
        for (int field = 0; field < 3; field++)
            w.Write(0);
        if (formula)
            for (int expression = 0; expression < (shape?.HasUnnamedType == true ? 4 : 3); expression++)
                WriteId(w, ParameterId, wide);
    });

    private static void Store(BinaryWriter w, string value, bool reporting, bool wide, string? comments, int? image, bool formula,
        int? nestedReference = null, bool instance = false, bool currencyInstance = false, (int Id, double Value)? builtInNumber = null)
    {
        w.Write(1 + (comments is null ? 0 : 1) + (image is null ? 0 : 1) + (formula ? 1 : 0) + (builtInNumber is null ? 0 : 1));
        double number = currencyInstance ? double.Parse(value, CultureInfo.InvariantCulture) : 0;
        Row(ParameterId, nestedReference.HasValue || currencyInstance ? "" : value, reporting, nestedReference ?? -1,
            isInstance: instance || currencyInstance, number: number);
        if (comments is not null)
            Row(TypeComments, comments, false);
        if (image is int id)
            Row(TypeImage, "", false, id);
        if (formula)
            Row(FormulaResultId, currencyInstance ? "" : value, false, expression: 47, isInstance: currencyInstance, number: number);
        if (builtInNumber is { } built)
            Row(built.Id, "", false, number: built.Value);

        void Row(int id, string text, bool isReporting, int reference = -1, ushort expression = 0, bool isInstance = false, double number = 0)
        {
            Text(w, text);
            if (expression == 0)
                w.Write(0u);
            else
                Pointer(w, expression);
            w.Write(number); WriteId(w, reference, wide);
            WriteId(w, id, wide); w.Write(0); w.Write(isInstance); w.Write(isReporting);
        }
    }

    private static void WriteId(BinaryWriter writer, int id, bool wide)
    {
        if (wide)
            writer.Write(id < 0 ? id : Id(id, wide));
        else
            writer.Write(id);
    }

    private static void Text(BinaryWriter writer, string value)
    {
        writer.Write(value.Length);
        writer.Write(Encoding.Unicode.GetBytes(value));
    }

    private static void Pointer(BinaryWriter writer, ushort tag)
    {
        writer.Write(uint.MaxValue);
        writer.Write(tag);
    }

    private static byte[] Pack(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }
}
