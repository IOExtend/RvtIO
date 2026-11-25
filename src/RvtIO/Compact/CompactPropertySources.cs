using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Compact;

internal sealed class CompactPropertySources : ICompactPropertySource
{
    private static readonly Func<int, bool> IsIfcId = CompactIfcParameters.OwnedIds.Contains;
    private static readonly Comparison<(int Id, int Index, CompactProperty Property)> CompareEntryIds = static (a, b) =>
        a.Id == b.Id ? a.Index.CompareTo(b.Index) : a.Id.CompareTo(b.Id);

    private readonly CompactBuiltinParameters _builtin;
    private readonly CompactIfcParameters _ifc;
    private readonly CompactUserParameters _user;
    private readonly CompactIdentityProperties? _identity;
    private readonly CompactIdentity? _identityRules;
    private readonly bool _ownsSources;
    private readonly List<(int Id, int Index, CompactProperty Property)> _orderedEntries = [];

    internal CompactPropertySources(CompactBuiltinParameters builtin, CompactIfcParameters ifc, CompactUserParameters user,
        CompactIdentity? identity = null, bool ownsSources = false)
    {
        _builtin = builtin;
        _ifc = ifc;
        _user = user;
        _identityRules = identity;
        _ownsSources = ownsSources;
        // the type entries of the identity block are listed by the same id list as the built-in entries
        _identity = identity == null ? null : new CompactIdentityProperties(identity, _ => _builtin.LastIds);
    }

    internal Action<Element, CompactProperty, CompactProperty>? Collision { get; set; }
    internal Action<Element, RvtIO.Parsing.UserParameterEntry>? UserTrace { set => _user.Trace = value; }
    internal CompactBuiltinParameters Builtin => _builtin;

    internal bool CanFork => _ownsSources && _builtin.CanFork && Collision == null
        && _user.Trace == null && _identityRules?.UsesDefaultResolvers != false;

    internal CompactPropertySources Fork() => new(_builtin.Fork(), _ifc, _user, _identityRules);

    /// <summary>The composite of a file.</summary>
    internal static CompactPropertySources Create(RevitFileInfo info, JsonReadFacts? state, bool? isProjectDocument,
        Func<Element, int?>? headerCategory = null, bool gated = true, CompactIdentity? identity = null,
        CompactSolidQuantities? quantities = null, CompactElementNames? names = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        names ??= new CompactElementNames(info, state);
        var file = new CompactListFileFacts(info.SerializedIsWorkshared, isProjectDocument,
            info.Elements.Any(element => CompactIfcParameters.ClassName(element) == "DesignOptionSet"), DesignOptions.Of(info.Elements));
        double? surveyOffset = info.SharedTransformIsActiveLocation && info.SharedTransform is { Length: 12 } transform ? transform[11] : null;
        var builtin = new CompactBuiltinParameters(names, info.SerializedFormatSettings, file, headerCategory, gated ? CompactValueGate.ForLayout(info.GeometryUndecoded) : null,
            state, surveyOffset, info.SerializedLookupNoneText, quantities);
        var settings = info.SerializedFormatSettings;
        var user = new CompactUserParameters(info,
            settings == null ? null : (spec, raw) => CompactValueFormatter.Format(raw, spec, settings),
            names.NameOf, names.Find);
        return new CompactPropertySources(builtin, new CompactIfcParameters(info), user, identity, ownsSources: true);
    }

    public IReadOnlyList<CompactProperty> GetProperties(Element element)
    {
        var builtin = _builtin.GetEntries(element, IsIfcId);
        _orderedEntries.Clear();
        AppendEntries(builtin.Entries);
        AppendEntries(_ifc.GetEntries(element));
        if (_identity != null) AppendEntries(_identity.GetEntries(element));
        _orderedEntries.Sort(CompareEntryIds);
        var result = new List<CompactProperty>(_orderedEntries.Count + 8);
        foreach (var entry in _orderedEntries) result.Add(entry.Property);
        _orderedEntries.Clear();

        int builtinCount = result.Count;
        foreach (var (user, family) in _user.GetPropertiesWithKind(element))
        {
            if (!family && builtin.Unvalidated.Contains((user.Group, user.Label))) continue;
            int earlier = 0;
            while (earlier < result.Count && (result[earlier].Group != user.Group || result[earlier].Label != user.Label))
                earlier++;
            if (earlier < result.Count)
            {
                Collision?.Invoke(element, result[earlier], user);
                // a parameter the family defines comes before the built-in parameters
                if (family && earlier < builtinCount) { result.RemoveAt(earlier); builtinCount--; }
            }
            result.Add(user);
        }
        return result;
    }

    private void AppendEntries(IReadOnlyList<(int Id, CompactProperty Property)> entries)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var (id, property) = entries[i];
            _orderedEntries.Add((id, _orderedEntries.Count, property));
        }
    }
}
