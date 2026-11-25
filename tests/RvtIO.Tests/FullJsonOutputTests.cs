using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class FullJsonOutputTests
{
    private static JsonSerializerOptions Options(bool indented) => new()
    {
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // The worker count is fixed so the parallel path runs even on a single-CPU machine.
    private static void AssertMatchesTheStreamingSerializer(RevitFileInfo model, JsonSerializerOptions options)
    {
        using var expected = new MemoryStream();
        JsonSerializer.Serialize(expected, model, options);
        string path = TestFiles.Scratch($"full-{Guid.NewGuid():N}.json");
        try
        {
            using (var output = File.Create(path)) FullJsonOutput.Write(output, model, options, workerCount: 4);
            Assert.True(expected.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParallelAndOversizedBatchesMatchTheStreamingSerializer(bool indented)
    {
        var model = new RevitFileInfo { SitePlaceName = "after the elements" };
        for (int i = 0; i < 4201; i++)
            model.Elements.Add(new Element { Id = i, Name = "имя ]],\n\"" + i });
        // The first, an interior and the final batch must fall back to streaming.
        model.Elements[0].Name = model.Elements[517].Name = model.Elements[^1].Name = new string('ё', 1_100_000);
        var options = Options(indented);
        options.Converters.Add(new ParameterListConverter());
        AssertMatchesTheStreamingSerializer(model, options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyedParameterValuesMatchTheStreamingSerializerInParallelBatches(bool indented)
    {
        var model = new RevitFileInfo();
        for (int i = 0; i < 4201; i++)
            model.Elements.Add(new Element
            {
                Id = i,
                Name = "e" + i,
                Parameters =
                [
                    new Parameter { Id = -1001000, StorageType = StorageType.Integer, Value = i },
                    new Parameter { Id = -1002000, StorageType = StorageType.String, Value = "текст \"" + i + "\"" },
                    new Parameter { Id = 262801, StorageType = StorageType.Double, Value = i / 8.0 },
                    // A repeated id keeps its first value.
                    new Parameter { Id = -1001000, StorageType = StorageType.Integer, Value = -i },
                ],
            });
        var options = Options(indented);
        model.BuildParameterDefinitionTable();
        options.Converters.Add(new ParameterListConverter(model.ParameterDefinitions!.Keys, options.Encoder));
        AssertMatchesTheStreamingSerializer(model, options);
    }
}
