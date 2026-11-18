using System.Text.Json;
using RvtIO.Parsing;

namespace RvtIO.Compact;

/// <summary>Text, placement and spacing of one unit symbol, as the document stores them in
/// its persisted Forge schemas ("text", "placement", "space").</summary>
internal readonly record struct CompactSymbolLabel(string Text, bool IsPrefix, bool HasSpace);

/// <summary>The unit settings of a document that the display-value formatter needs: the
/// FormatOptions per spec with the decimal and digit grouping symbols, the numeric unit
/// conversions and the symbol labels. Everything comes from the document's own
/// Global/Latest UnitsElem and persisted schemas.</summary>
internal sealed class CompactFormatSettings
{
    private const string SymbolPrefix = "autodesk.unit.symbol:";
    private const string DegreeSymbol = SymbolPrefix + "degree-1.0.1";
    private const string SlopeSymbol = SymbolPrefix + "slopeDegree-1.0.0";

    internal TfBlockDeserializer.UnitOptions Options { get; }
    internal UnitSchemaEvaluator.UnitConversions Conversions { get; }
    internal IReadOnlyDictionary<string, CompactSymbolLabel> Symbols { get; }

    // spec name without the version -> its option; null when two options share the name
    private readonly Dictionary<string, TfBlockDeserializer.UnitFormatOption?> _byName = new(StringComparer.Ordinal);

    internal CompactFormatSettings(TfBlockDeserializer.UnitOptions options,
        UnitSchemaEvaluator.UnitConversions conversions, IReadOnlyDictionary<string, CompactSymbolLabel> symbols)
    {
        Options = options; Conversions = conversions; Symbols = symbols;
        foreach (var option in options.Options)
        {
            string name = UnitSchemaEvaluator.DisplayNameKey(option.Spec);
            _byName[name] = _byName.ContainsKey(name) ? null : option;
        }
    }

    /// <summary>Null when the document has no complete unit options, numeric conversions or schemas.</summary>
    internal static CompactFormatSettings? Create(TfBlockDeserializer.UnitsElemOptions? options,
        UnitSchemaEvaluator.UnitConversions? conversions,
        (TfBlockDeserializer.DocumentSchemaMap Units, TfBlockDeserializer.DocumentSchemaMap Parameters)? schemas)
    {
        if (options == null || conversions == null || schemas == null) return null;
        var symbols = new Dictionary<string, CompactSymbolLabel>(StringComparer.Ordinal);
        foreach (var entry in schemas.Value.Units.Entries)
        {
            if (!entry.Key.StartsWith(SymbolPrefix, StringComparison.Ordinal)) continue;
            if (TryReadSymbol(entry.Value, out var label)) symbols.Add(entry.Key, label);
        }
        // The slope symbol is not persisted: the slope options name it, and it prints exactly like the degree sign.
        if (!symbols.ContainsKey(SlopeSymbol) && symbols.TryGetValue(DegreeSymbol, out var degree))
            symbols.Add(SlopeSymbol, degree);
        return new(options.Value, conversions, symbols);
    }

    /// <summary>The options of a spec. A spec is found by its name without the version
    /// ("autodesk.spec.aec:length-2.0.1" finds the entry stored under "...length-1.0.0").
    /// Null when the spec is absent or the name is ambiguous.</summary>
    internal TfBlockDeserializer.UnitFormatOption? Find(string spec)
    {
        int dash = spec.IndexOf('-');
        var name = dash < 0 ? spec : spec.Substring(0, dash);
        return _byName.TryGetValue(name, out var option) ? option : null;
    }

    private static bool TryReadSymbol(string json, out CompactSymbolLabel label)
    {
        label = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            string? text = null, placement = null; bool? space = null;
            foreach (var constant in document.RootElement.GetProperty("constants").EnumerateArray())
            {
                switch (constant.GetProperty("id").GetString())
                {
                    case "text": text = constant.GetProperty("value").GetString(); break;
                    case "placement": placement = constant.GetProperty("value").GetString(); break;
                    case "space": space = constant.GetProperty("value").GetBoolean(); break;
                }
            }
            if (text == null || space == null || placement is not ("Prefix" or "Suffix")) return false;
            label = new(text, placement == "Prefix", space.Value);
            return true;
        }
        catch (JsonException) { return false; }
        catch (KeyNotFoundException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
