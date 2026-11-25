using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class FullJsonOutputTests
{
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
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new ParameterListConverter() },
        };
        using var expected = new MemoryStream();
        JsonSerializer.Serialize(expected, model, options);
        string path = TestFiles.Scratch($"full-{Guid.NewGuid():N}.json");
        try
        {
            using (var output = File.Create(path)) FullJsonOutput.Write(output, model, options);
            Assert.True(expected.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }
}
