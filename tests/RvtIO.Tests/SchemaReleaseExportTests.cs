using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RvtIO.Compact;
using RvtIO.Ifc;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Full reads and exports of public Autodesk samples with only the BasicFileInfo Format year changed.
/// Download URLs and hashes are recorded in Fixtures/README.md; the repository contains no Revit files.</summary>
public class SchemaReleaseExportTests
{
    [SkippableTheory]
    [InlineData("RVTIO_SCHEMA_SAMPLE_2022", 2022, 2025, false)]
    [InlineData("RVTIO_SCHEMA_SAMPLE_2024", 2024, 2022, false)]
    [InlineData("RVTIO_SCHEMA_SAMPLE_2024", 2024, 2022, true)]
    public void ChangingTheSavingYearPreservesTheFullReadAndExports(string variable, int schemaYear, int savingYear, bool r22Layout)
    {
        string original = TestFiles.FileFromEnvironment(variable);
        string copy = SchemaReleaseTests.CopyWithSavedRelease(original, savingYear);
        try
        {
            var options = new RvtReaderOptions { R22Layout = r22Layout, SystemFamilyNameLocale = "ENU" };
            var originalReader = new RvtFileReader(options);
            var expected = originalReader.Read(original);
            var copyReader = new RvtFileReader(options);
            var actual = copyReader.Read(copy);
            Assert.Equal(schemaYear.ToString(), expected.Metadata.RevitVersion);
            Assert.Equal(savingYear.ToString(), actual.Metadata.RevitVersion);
            Assert.True(expected.ElemTableIsExact);
            Assert.True(actual.ElemTableIsExact);
            Assert.NotEmpty(expected.Elements);
            Assert.Equal(expected.Elements.Select(e => e.Id), actual.Elements.Select(e => e.Id));
            Assert.Equal(expected.GeometryUndecoded, actual.GeometryUndecoded);
            Assert.Equal(expected.WideLayoutNotRewritten, actual.WideLayoutNotRewritten);

            // The changed saving year is observable metadata (also used in the IFC application header).
            // Normalize that one field; all other metadata, element data and export content must match.
            actual.Metadata.RevitVersion = expected.Metadata.RevitVersion;
            var json = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
            Assert.Equal(Hash(s => JsonSerializer.Serialize(s, expected, json)), Hash(s => JsonSerializer.Serialize(s, actual, json)));

            var timestamp = DateTimeOffset.FromUnixTimeSeconds(0);
            var originalIfc = new IfcExporter { Timestamp = timestamp };
            var copyIfc = new IfcExporter { Timestamp = timestamp };
            Assert.Equal(Hash(s =>
            {
                using var writer = new StreamWriter(s, leaveOpen: true);
                originalIfc.Export(expected, writer);
            }), Hash(s =>
            {
                using var writer = new StreamWriter(s, leaveOpen: true);
                copyIfc.Export(actual, writer);
            }));
            Assert.True(originalIfc.ExportedProducts > 0);
            Assert.Equal(originalIfc.ExportedProducts, copyIfc.ExportedProducts);

            RvtIO.Export.ValidatedElementNames.Apply(expected, originalReader.LastParsingState!);
            RvtIO.Export.ValidatedElementNames.Apply(actual, copyReader.LastParsingState!);
            var expectedIdentity = new CompactIdentity(CompactUniverse.Select(expected));
            var actualIdentity = new CompactIdentity(CompactUniverse.Select(actual));
            var expectedSource = CompactPropertySources.Create(expected, originalReader.LastParsingState, true, identity: expectedIdentity);
            var actualSource = CompactPropertySources.Create(actual, copyReader.LastParsingState, true, identity: actualIdentity);
            Assert.Equal(Hash(s => CompactJsonExporter.Export(expected, s, expectedSource, identity: expectedIdentity)),
                Hash(s => CompactJsonExporter.Export(actual, s, actualSource, identity: actualIdentity)));
        }
        finally { File.Delete(copy); }
    }

    private static string Hash(Action<Stream> write)
    {
        using var hash = SHA256.Create();
        using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write)) write(stream);
        return Convert.ToHexString(hash.Hash!);
    }
}
