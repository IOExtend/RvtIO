using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RvtIO.Compact;

/// <summary>
/// Streams the compact JSON properties collection: {"data":{"type":"properties","collection":[ nodes ]}}, minified UTF-8
/// without a BOM and without a trailing newline.
/// </summary>
internal sealed class CompactJsonWriter : IDisposable
{
    /// <summary>The writer hands its pending bytes to the stream once this many are buffered.</summary>
    internal const int FlushThreshold = 32 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonEncodedText ObjectIdKey = JsonEncodedText.Encode("objectId");
    private static readonly JsonEncodedText NameKey = JsonEncodedText.Encode("name");
    private static readonly JsonEncodedText ExternalIdKey = JsonEncodedText.Encode("externalId");
    private static readonly JsonEncodedText ObjectKey = JsonEncodedText.Encode("object");
    private static readonly JsonEncodedText PropertiesKey = JsonEncodedText.Encode("properties");

    private static readonly IReadOnlyList<CompactProperty> NoProperties = [];

    private readonly Utf8JsonWriter _writer;
    private readonly string _classNamePrefix;
    private readonly Comparison<int> _compareProperties;
    private IReadOnlyList<CompactProperty> _sorting = NoProperties;
    private int[] _order = new int[64];
    private bool _completed;

    /// <summary>Writes the opening of the document to <paramref name="output"/>, which stays open. <paramref name="classNamePrefix"/>
    /// is put in front of every non-empty class name written to "name"; an empty class name stays empty.</summary>
    public CompactJsonWriter(Stream output, string classNamePrefix = "")
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(classNamePrefix);
        _classNamePrefix = classNamePrefix;
        _compareProperties = CompareProperties;
        _writer = new Utf8JsonWriter(output, WriterOptions);
        _writer.WriteStartObject();
        _writer.WriteStartObject("data"u8);
        _writer.WriteString("type"u8, "properties"u8);
        _writer.WriteStartArray("collection"u8);
    }

    /// <summary>Number of bytes produced so far, whether or not they have reached the stream yet.</summary>
    public long BytesWritten => _writer.BytesCommitted + _writer.BytesPending;

    public void WriteNode(in CompactNode node)
    {
        if (_completed) throw new InvalidOperationException("The document is complete.");
        if (node.ClassName is null || node.UniqueId is null || node.Properties is null)
            throw new ArgumentException($"Node {node.ElementId} has a null class name, unique id or property list.", nameof(node));
        // Everything that can be refused is checked before the first byte of the node is written.
        int count = SortProperties(node.ElementId, node.Properties);

        if (!string.IsNullOrEmpty(node.CategoryLabel) && !string.IsNullOrEmpty(node.FamilyName))
        {
            WritePseudoObject(node.CategoryLabel);
            WritePseudoObject(node.FamilyName);
        }

        _writer.WriteStartObject();
        Span<char> id = stackalloc char[20];
        node.ElementId.TryFormat(id, out int idLength, default, CultureInfo.InvariantCulture);
        _writer.WriteString(ObjectKey, id[..idLength]);
        _writer.WriteString(NameKey, node.ClassName.Length == 0 ? node.ClassName : _classNamePrefix + node.ClassName);
        _writer.WriteString(ExternalIdKey, node.UniqueId);
        _writer.WritePropertyName(PropertiesKey);
        _writer.WriteStartArray();
        WriteSortedProperties(node.Properties, count);
        _writer.WriteEndArray();
        _writer.WriteEndObject();

        if (_writer.BytesPending >= FlushThreshold) _writer.Flush();
    }

    /// <summary>Closes the collection and the document and flushes everything to the stream.</summary>
    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        _writer.WriteEndArray();
        _writer.WriteEndObject();
        _writer.WriteEndObject();
        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();

    private void WritePseudoObject(string name)
    {
        _writer.WriteStartObject();
        _writer.WriteString(ObjectIdKey, ""u8);
        _writer.WriteString(NameKey, name);
        _writer.WriteString(ExternalIdKey, ""u8);
        _writer.WriteEndObject();
    }

    /// <summary>Puts the indexes of <paramref name="properties"/> into <c>_order</c> sorted by group, label and insertion index.</summary>
    private int SortProperties(long elementId, IReadOnlyList<CompactProperty> properties)
    {
        int count = properties.Count;
        if (count == 0) return 0;
        if (_order.Length < count) _order = new int[Math.Max(count, _order.Length * 2)];
        for (int i = 0; i < count; i++)
        {
            var p = properties[i];
            if (p.Group is null || p.Label is null || p.Value is null)
                throw new InvalidDataException($"Property {i} of element {elementId} has a null group, label or value.");
            _order[i] = i;
        }
        _sorting = properties;
        try { _order.AsSpan(0, count).Sort(_compareProperties); }
        finally { _sorting = NoProperties; }
        return count;
    }

    private void WriteSortedProperties(IReadOnlyList<CompactProperty> properties, int count)
    {
        bool groupOpen = false;
        string? group = null, label = null;
        for (int k = 0; k < count; k++)
        {
            var p = properties[_order[k]];
            if (!groupOpen || !string.Equals(p.Group, group, StringComparison.Ordinal))
            {
                if (groupOpen) _writer.WriteEndObject();
                _writer.WriteStringValue(p.Group);
                _writer.WriteStartObject();
                groupOpen = true;
                group = p.Group;
                label = null;
            }
            // Same (group, label): the entry inserted first sorts first, so the later ones are dropped.
            else if (string.Equals(p.Label, label, StringComparison.Ordinal)) continue;
            _writer.WriteString(p.Label, p.Value);
            label = p.Label;
        }
        if (groupOpen) _writer.WriteEndObject();
    }

    /// <summary>Group, then label, both by UTF-16 code units; the insertion index breaks ties, which makes the sort stable.</summary>
    private int CompareProperties(int a, int b)
    {
        var x = _sorting[a];
        var y = _sorting[b];
        int c = string.CompareOrdinal(x.Group, y.Group);
        if (c != 0) return c;
        c = string.CompareOrdinal(x.Label, y.Label);
        return c != 0 ? c : a.CompareTo(b);
    }
}
