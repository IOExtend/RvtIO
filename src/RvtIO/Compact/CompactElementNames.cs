using System.Collections.Concurrent;
using RvtIO.Export;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Compact;

/// <summary>
/// The name printed for an element a parameter refers to (the Name of the referenced element), where this
/// reader can confirm it: <see cref="ValidatedElementNames"/> (types, levels, materials and the type name of an occurrence). Null
/// means "not validated"; the writer then leaves the entry out, because an element with an empty name prints its decimal id and the
/// reader cannot tell an empty name from an unvalidated one.
/// Without the parsing state only the name of a material is known (<see cref="Element.SerializedMaterialName"/>).
/// </summary>
internal sealed class CompactElementNames
{
    private readonly Dictionary<int, Element> _byId;
    private readonly Dictionary<int, int> _counts;
    private readonly JsonReadFacts? _state;
    private readonly ConcurrentDictionary<int, string?> _cache = new();

    public CompactElementNames(RevitFileInfo info, JsonReadFacts? state)
    {
        ArgumentNullException.ThrowIfNull(info);
        _state = state;
        _byId = new(info.Elements.Count);
        _counts = new(info.Elements.Count);
        foreach (Element element in info.Elements)
        {
            _byId.TryAdd(element.Id, element);
            _counts[element.Id] = _counts.GetValueOrDefault(element.Id) + 1;
        }
    }

    internal void ApplyValidatedNames(RevitFileInfo info, JsonReadFacts state) =>
        ValidatedElementNames.Apply(info, state, _byId, _counts);

    /// <summary>The element with the id, null when the file has none.</summary>
    public Element? Find(int id) => _byId.GetValueOrDefault(id);

    /// <summary>The validated name of the element with the id; null when there is none.</summary>
    public string? NameOf(int id) => _cache.GetOrAdd(id, static (key, names) => names.ResolveName(key), this);

    private string? ResolveName(int id)
    {
        string? name = null;
        if (_byId.TryGetValue(id, out Element? element))
            name = _state != null
                ? ValidatedElementNames.Resolve(element, _byId, _counts, _state)?.Name
                : element.RuleClassName == "MaterialElem" ? element.SerializedMaterialName : null;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
