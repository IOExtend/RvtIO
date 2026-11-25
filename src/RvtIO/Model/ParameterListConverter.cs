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
        private readonly Dictionary<int, JsonEncodedText>? _keys;

        /// <summary>Writes values using the caller's serializer options and converters.</summary>
        public ParameterListConverter() { }

        // The CLI's full dump uses fixed JSON options and knows every parameter key before writing.
        internal ParameterListConverter(IEnumerable<int> definitionIds, System.Text.Encodings.Web.JavaScriptEncoder encoder)
        {
            _keys = definitionIds.ToDictionary(id => id, id => JsonEncodedText.Encode(
                id.ToString(System.Globalization.CultureInfo.InvariantCulture), encoder));
        }

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
                if (_keys != null && _keys.TryGetValue(p.Id, out var key))
                    writer.WritePropertyName(key);
                else writer.WritePropertyName(p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (p.SourceElementId is string sourceId)
                    writer.WriteStringValue(sourceId);
                else if (_keys != null && p.Value is int integer) writer.WriteNumberValue(integer);
                else if (_keys != null && p.Value is string text) writer.WriteStringValue(text);
                else JsonSerializer.Serialize(writer, p.Value, options);
            }
            writer.WriteEndObject();
        }
    }
}
