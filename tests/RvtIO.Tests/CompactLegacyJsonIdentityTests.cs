using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The legacy JSON dump (the CLI with --json-full) must not change when the compact JSON is added. The CLI serialises
/// RevitFileInfo with the options below after BuildParameterDefinitionTable; this pins that output for a small synthetic file.</summary>
public class CompactLegacyJsonIdentityTests
{
    private const string ExpectedSha256 = "f8a0fa06658dc9d5aabc9fa8fb8f8974168c9b9563f289dd6a6826f669aa1ac8";

    private static RevitFileInfo Synthetic()
    {
        var info = new RevitFileInfo { ElemTableIsExact = true };
        info.Elements.Add(new Element
        {
            Id = 243274,
            Name = "Roof \"SG\" Крыша",
            Category = "OST_Roofs",
            CategoryId = -2000035,
            UniqueId = "e3e052f9-0156-11d5-9301-0000863f27ad-0003b64a",
            FamilyName = "Basic Roof",
            BoundingBox = [0, 1.5, 2, 10, 11.25, 12],
            Parameters =
            [
                new Parameter { Id = -1001000, StorageType = StorageType.Double, Value = 3.5, Definition = new Definition { Name = "ROOF_BASE_LEVEL_PARAM", BuiltInParameter = "ROOF_BASE_LEVEL_PARAM" } },
                new Parameter { Id = -1002000, StorageType = StorageType.String, Value = "text" },
                new Parameter { Id = 262801, StorageType = StorageType.Integer, Value = 4 },
            ],
        });
        info.Elements.Add(new Element { Id = 7, Name = "Discovered", IsDiscovered = true });
        return info;
    }

    private static string Serialize(RevitFileInfo info, bool cacheKeys)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
        };
        info.BuildParameterDefinitionTable();
        options.Converters.Add(cacheKeys ? new ParameterListConverter(info.ParameterDefinitions!.Keys, options.Encoder) : new ParameterListConverter());
        return JsonSerializer.Serialize(info, options).Replace("\r\n", "\n");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyDumpOfASyntheticFile_IsUnchanged(bool cacheKeys)
    {
        string json = Serialize(Synthetic(), cacheKeys);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        Assert.True(hash == ExpectedSha256, $"legacy JSON changed (SHA-256 {hash}):\n{json}");
    }
}
