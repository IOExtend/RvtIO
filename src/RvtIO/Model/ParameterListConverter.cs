using System.Text.Json;
using System.Text.Json.Serialization;

namespace RvtIO.Model
{
    /// <summary>
    /// Serializes an element's parameter list as a compact id -> value map
    /// (<c>{ "-1006401": 0.0049, "262801": "Type A" }</c>) instead of an array of
    /// <c>{ "Id", "Value" }</c> objects. Every other parameter field is invariant per
    /// definition id and lives in <see cref="RevitFileInfo.ParameterDefinitions"/>, so the
    /// only per-instance data is the value - making the map a lossless, much smaller encoding.
    /// Write-only; the in-memory model keeps the full <see cref="List{Parameter}"/>.
    /// </summary>
    public class ParameterListConverter : JsonConverter<List<Parameter>>
    {
        /// <inheritdoc />
        public override List<Parameter> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException("ParameterListConverter is write-only.");

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, List<Parameter> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            var seen = new HashSet<int>();
            foreach (var p in value)
            {
                // First occurrence wins (matches Element.LookupParameter semantics) so a
                // rare duplicate id does not produce an invalid repeated JSON property.
                if (!seen.Add(p.Id)) continue;
                writer.WritePropertyName(p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (p.SourceElementId is string sourceId)
                    writer.WriteStringValue(sourceId);
                else
                    JsonSerializer.Serialize(writer, p.Value, options);
            }
            writer.WriteEndObject();
        }
    }
}
