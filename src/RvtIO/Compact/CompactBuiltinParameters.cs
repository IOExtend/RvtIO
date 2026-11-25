using System.Collections.Frozen;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Compact;

/// <summary>
/// The built-in parameter entries of an element: the listed ids (<see cref="CompactParameterLists"/>), their label and group
/// (<see cref="CompactLabels"/>) and the value text (<see cref="CompactValueRules"/>) from what the reader holds. An entry is written
/// only when all three are known; the reason an entry is left out is counted in <see cref="Skipped"/>.
/// <para>
/// Storage and spec of a parameter come from the Revit 2022 API capture (<see cref="ApiParameterDefinitions"/>); an id that capture
/// does not list is left out. <see cref="CompactValueGate"/> decides which value source is trusted per parameter; a value the reader
/// marks as approximate is never written.
/// </para>
/// <para>Not thread safe (it keeps a scratch table per element).</para>
/// </summary>
internal sealed class CompactBuiltinParameters
{
    /// <summary>The built-in entries of an element (ascending id) and the (group, label) pairs left without a validated value.</summary>
    internal readonly record struct CompactBuiltinResult(
        IReadOnlyList<(int Id, CompactProperty Property)> Entries, IReadOnlySet<(string Group, string Label)> Unvalidated);
    private static readonly IReadOnlySet<(string Group, string Label)> EmptyUnvalidated =
        FrozenSet<(string Group, string Label)>.Empty;

    /// <summary>ELEM_DESIGN_OPTION_ID ("Design Option"). Without design option sets no element is in an option, so the
    /// parameter is the null id and prints "None".</summary>
    internal const int DesignOptionParameter = -1013201;
    private const int DesignOptionSetNameParameter = -1133600, PrimaryOptionIdParameter = -1133601, DesignOptionNameParameter = -1133602,
        DesignOptionSetIdParameter = -1133603;

    private static string? NotProven(string text, out string? reason)
    {
        reason = text;
        return null;
    }

    private readonly CompactElementNames _names;
    private readonly Func<int, Element?> _findElement;
    private readonly CompactFormatSettings? _format;
    private readonly CompactListFileFacts _file;
    private readonly Func<Element, int?> _headerCategory;
    private readonly bool _usesDefaultHeaderCategory;
    private readonly CompactValueGate? _gate;
    private readonly JsonReadFacts? _state;
    private readonly CompactSolidQuantities? _quantities;
    private readonly double? _surveyOffsetFeet;
    private readonly string? _lookupNoneText;
    private readonly Dictionary<int, Parameter> _held = [];
    private readonly HashSet<int> _conflicting = [];
    private readonly HashSet<(string Group, string Label)> _seen = [];
    private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);

    /// <param name="gate">Which value sources are trusted per parameter; null writes the derived value for every parameter and
    /// reports both candidate texts to <see cref="Candidates"/>.</param>
    /// <param name="state">The parsing state: the native parameter stores and FamilyParams records of the elements (their own values).</param>
    /// <param name="lookupNoneText">The empty lookup table entry in the saved locale of the file (the English one is always recognized).</param>
    /// <param name="surveyOffsetFeet">Shared (survey) elevation minus internal elevation of the active project location, null when the
    /// location is not resolved exactly.</param>
    public CompactBuiltinParameters(CompactElementNames names, CompactFormatSettings? format, CompactListFileFacts file,
        Func<Element, int?>? headerCategory = null, CompactValueGate? gate = null, JsonReadFacts? state = null, double? surveyOffsetFeet = null,
        string? lookupNoneText = null, CompactSolidQuantities? quantities = null)
    {
        _quantities = quantities;
        _lookupNoneText = lookupNoneText;
        _state = state;
        _surveyOffsetFeet = surveyOffsetFeet;
        _names = names;
        _findElement = names.Find;
        _format = format;
        _file = file;
        _usesDefaultHeaderCategory = headerCategory == null;
        _headerCategory = headerCategory ?? CompactHeaderCategory.Of;
        _gate = gate;
    }

    /// <summary>Why entries were left out and how many: "list: ..." counts elements, the others entries.</summary>
    public IReadOnlyDictionary<string, int> Skipped => _skipped;

    /// <summary>Called for every id the rules list (reason null: written) and once per element with id 0 when its list is
    /// unknown (reason "list: ..."); for measurements.</summary>
    internal Action<Element, int, string?>? Trace { get; set; }

    /// <summary>For measurements: called with the facts of every element before its list is decided.</summary>
    internal Action<Element, ICompactListFacts>? FactsTrace { get; set; }

    /// <summary>For measurements: (element, id, stored-rule text, derived text) of every listed parameter whose storage is known.</summary>
    internal Action<Element, int, string?, string?>? Candidates { get; set; }

    /// <summary>For measurements only: replaces the rule-derived id list of an element (null result: use the rules).</summary>
    internal Func<Element, int[]?>? IdListOverride { get; set; }

    /// <summary>The ids listed for the element of the last <see cref="GetEntries"/> call, null when the list is unknown.</summary>
    internal IReadOnlyList<int>? LastIds { get; private set; }

    /// <summary>Number of entries written and elements whose id list was known.</summary>
    public int Written { get; private set; }

    public int ElementsWithList { get; private set; }

    public int Elements { get; private set; }

    internal bool CanFork => _usesDefaultHeaderCategory
        && Trace == null && FactsTrace == null && Candidates == null && IdListOverride == null;

    internal CompactBuiltinParameters Fork() =>
        new(_names, _format, _file, gate: _gate, state: _state, surveyOffsetFeet: _surveyOffsetFeet,
            lookupNoneText: _lookupNoneText, quantities: _quantities);

    internal void CollectReport(CompactBuiltinParameters worker)
    {
        if (worker.Elements == 0) return;
        Elements += worker.Elements;
        Written += worker.Written;
        ElementsWithList += worker.ElementsWithList;
        LastIds = worker.LastIds;
        foreach (var (reason, count) in worker._skipped)
            _skipped[reason] = _skipped.GetValueOrDefault(reason) + count;
        worker.Elements = worker.Written = worker.ElementsWithList = 0;
        worker._skipped.Clear();
    }

    /// <summary>The entries in ascending id order, and the (group, label) pairs that are listed but whose value is not written: a later
    /// parameter with the same pair must not be written either, because the writer keeps the first value of a repeated pair.
    /// Ids owned by another source (<paramref name="isOwned"/>) are not looked at.</summary>
    public CompactBuiltinResult GetEntries(Element element, Func<int, bool>? isOwned = null)
    {
        Elements++;
        int? header = _headerCategory(element);
        string recordClass = ElementListFacts.RecordClassName(element) ?? "";
        var facts = new ElementListFacts(element, header, _file, _findElement);
        FactsTrace?.Invoke(element, facts);
        var list = IdListOverride?.Invoke(element) is { } forced
            ? new CompactListResult(forced, null)
            : CompactParameterLists.GetBuiltinIds(recordClass, facts);
        LastIds = list.Ids;
        if (list.Ids is not { } ids)
        {
            string reason = "list: " + list.Unknown;
            Skip(reason);
            Trace?.Invoke(element, 0, reason);
            return new([], EmptyUnvalidated);
        }
        ElementsWithList++;
        Prepare(element);
        var result = new List<(int, CompactProperty)>(ids.Length);
        HashSet<(string Group, string Label)>? unvalidated = null;
        _seen.Clear();
        foreach (int id in ids)
        {
            if (isOwned?.Invoke(id) == true) continue;
            if (!CompactLabels.TryGetLabels(id, out string? group, out string? label))
            {
                Skip("label or group unknown");
                Trace?.Invoke(element, id, "label or group unknown");
                continue;
            }
            if (CompactLabelChanges.IsChanged(id))
            {
                Skip("label or group changed after Revit 2022");
                Trace?.Invoke(element, id, "label or group changed after Revit 2022");
                continue;
            }
            string? text = ValueText(element, id, header, out string? reason);
            // a repeated (group, label): the first id is the one kept, whatever the writer is given later
            bool first = _seen.Add((group, label));
            if (!first && text != null && unvalidated?.Contains((group, label)) == true)
            {
                text = null;
                reason = "label shared with an earlier parameter whose value is not written";
            }
            if (text == null)
            {
                Skip(reason!);
                Trace?.Invoke(element, id, reason);
                if (first) (unvalidated ??= new()).Add((group, label));
                continue;
            }
            Trace?.Invoke(element, id, null);
            result.Add((id, new CompactProperty(group, label, text)));
            Written++;
        }
        return new(result, unvalidated ?? EmptyUnvalidated);
    }

    private void Prepare(Element element)
    {
        _held.Clear();
        _conflicting.Clear();
        foreach (Parameter parameter in element.Parameters)
        {
            // a file with 64-bit ids: the reader's other passes hand out values from scanned strings and defaults; only the decoded
            // parameter stores and field rules count
            if (element.NativeRootClassName != null && !parameter.FromNativeFields) continue;
            if (!_held.TryAdd(parameter.Id, parameter) && !Equals(_held[parameter.Id].Value, parameter.Value))
                _conflicting.Add(parameter.Id);
        }
        // Detail Line of a curve element: a detail line exactly when a view owns it (m_ownerDBViewId set); a model or sketch line
        // has none.
        if (element.NativeRootClassName != null && !_held.ContainsKey(CurveIsDetailParameter)
            && ElementListFacts.RecordClassName(element) == CurveElemClass && element.SerializedOwnerDbViewId is int ownerView)
            _held[CurveIsDetailParameter] = BipHelpers.MakeBipParam(CurveIsDetailParameter, "CURVE_IS_DETAIL", StorageType.Integer, ownerView > 0 ? 1 : 0);
        // Length of a curve element: the curve length of the Revit 2022 reader (second read of the file, CompactSolidQuantities)
        if (_quantities != null && element.NativeRootClassName != null && !_held.ContainsKey(CurveElemLengthParameter)
            && ElementListFacts.RecordClassName(element) == CurveElemClass && _quantities.CurveLength(element.Id) is double curveLength)
            _held[CurveElemLengthParameter] = BipHelpers.MakeBipParam(CurveElemLengthParameter, "CURVE_ELEM_LENGTH", StorageType.Double, curveLength);
    }

    /// <summary>The value text of a listed id, or null with the reason it is not written.</summary>
    private string? ValueText(Element element, int id, int? header, out string? reason)
    {
        reason = null;
        if (CompactValueRules.IsCategoryParameter(id))
        {
            // a class listed from observation (CompactObservedLists) answers only where its identity rule confirms the header category
            if (ElementListFacts.RecordClassName(element) is { } listed && CompactObservedLists.IsSupported(listed) && !CompactIdentity.CategoryFollowsHeader(listed))
            {
                reason = "category: the parameter of this class may differ from the header category";
                return null;
            }
            string? category = CompactValueRules.CategoryText(header);
            if (category == null) reason = "category: header category unknown";
            return category;
        }
        if (id == DesignOptionParameter)
        {
            // property set elements have no design option at all: no value
            if (ElementListFacts.RecordClassName(element) == "PropertySetElement") return "";
            if (_file.DesignOptionSetsExist == false) return "None";
            // the header of the element names its design option: the null id prints "None". The stored Element.m_designOptionId
            // (-4 on many elements) says nothing: other elements with that stored value print the option their header names
            if (element.NativeHeaderDesignOptionId == -1) return "None";
            if (element.NativeHeaderDesignOptionId is int option && _file.DesignOptions?.NameOf(option) is { } optionName) return optionName;
            reason = element.NativeHeaderDesignOptionId is null ? "design option: the file has design option sets"
                : "design option: the name of the option is not validated";
            return null;
        }
        if (id is DesignOptionNameParameter or DesignOptionSetNameParameter)
            return element.NativeDesignOptionName ?? NotProven("design option: the record holds no name", out reason);
        if (id is DesignOptionSetIdParameter)
            return (_file.DesignOptions?.SetOf(element.Id) is int set ? _file.DesignOptions.NameOf(set) : null)
                ?? NotProven("design option: the set of the option is not decoded", out reason);
        if (id is PrimaryOptionIdParameter)
            return (_file.DesignOptions?.PrimaryOf(element.Id) is int primary ? _file.DesignOptions.NameOf(primary) : null)
                ?? NotProven("design option: the primary option of the set is not decoded", out reason);
        if (element.NativeRootClassName != null && id == HostVolumeParameter && header == MassCategory) { reason = "host volume of a mass is not decided"; return null; }
        // quantities of the solids of a family instance of a wide layout: the gate's V lines say per header category which formula
        // Volume, Area or Length follows (CompactSolidQuantities); an element without such a line goes on to the other rules
        if (_gate != null && _quantities != null && element.NativeRootClassName != null && header is int solidCategory
            && id is HostVolumeParameter or HostAreaParameter or CurveElemLengthParameter or SystemLengthParameter
            && ElementListFacts.RecordClassName(element) == FamilyInstanceClass && _quantities.SolidsOf(element.Id) is { } solids
            && _gate.SolidRule(FamilyInstanceClass, id, solidCategory, solids.Shape) is { } formula)
            return formula == "empty" ? "" : SolidText(id, CompactSolidQuantities.Value(solids, formula), out reason);
        // Level of a family instance: the stored fields give it exactly except for a work plane based instance without a stored level
        if (id == ScheduleLevelParameter && CompactFamilyInstanceFacts.Applies(element) && CompactFamilyInstanceFacts.LevelFollowsStoredFields(element) != true)
        {
            reason = "level of a work plane based instance without a stored level is not decided";
            return null;
        }
        var record = OwnRecord(element, id);
        if (CompactValueRules.ImageParameters.Contains(id))
        {
            // the element's own FamilyParams record: a stored null id prints "None", no record prints ""; a reference to an element
            // prints that element's name, which is not validated
            if (record == null) return "";
            if (record.ElemId == -1) return "None";
            reason = "image reference: the name of the referenced element is not validated";
            return null;
        }
        if (id is CompactValueRules.TypeNameParameter or CompactValueRules.FamilyNameParameter)
        {
            // a center line reports the type name of the element it belongs to ("Standard" for pipe and fitting center lines),
            // not the empty text of an element that is no type
            if (ElementListFacts.RecordClassName(element) is { } centerLine && centerLine.EndsWith("CenterLine", StringComparison.Ordinal))
            {
                reason = "type or family name of a center line is the one of its owner";
                return null;
            }
            // only a type has a type name and a family name: every other element prints ""
            switch (CompactElementKinds.IsType(element))
            {
                case false: return "";
                case null: reason = "type or not: the element has no class id"; return null;
            }
            // a type's type name is its own name; without a validated name the value the reader holds goes through the gate like any other
            if (id == CompactValueRules.TypeNameParameter && _names.NameOf(element.Id) is { } own) return own;
        }
        if (!ApiParameterDefinitions.TryGetStorage(id, out StorageType storage))
        {
            // a definition of kind "None" is a button: no value, prints ""
            if (ApiParameterDefinitions.TryGet(id, out _, out _, out _)) return "";
            reason = "no storage or spec source";
            return null;
        }
        ApiParameterDefinitions.TryGet(id, out _, out _, out string spec);
        string recordClass = ElementListFacts.RecordClassName(element) ?? "";
        if (recordClass == "FamilySymbol")
        {
            // thermal values come from the entry of the construction lookup table; a symbol with the empty entry prints "". A symbol
            // with another entry prints either the values of the entry or "": not decided, nothing is written.
            if (id == AnalyticConstructionParameter) return AnalyticConstructionText(id, record, out reason);
            if (id is ThermalCoefficientParameter or SolarHeatGainParameter or VisualLightParameter)
            {
                if (IsLookupNone(AnalyticConstructionName(element, OwnRecord(element, AnalyticConstructionParameter)))) return "";
                reason = "thermal value: the construction lookup entry of the symbol is not the empty entry";
                return null;
            }
        }
        if (id == HostAreaParameter && header == CurtainPanelsCategory && recordClass == "FamilyInstance" && IsLoadableFamilyInstance(element))
        {
            // the area of a curtain panel placed from a loadable family is the largest face of its geometry here; a minority of such
            // areas differ slightly, the system panels do not
            reason = "area of a curtain panel from a loadable family is not validated";
            return null;
        }
        string? derived = DerivedText(id, storage, spec, out string? derivedReason);
        string? stored = StoredText(element, id, derived, storage, spec, out string? storedReason);
        Candidates?.Invoke(element, id, stored, derived);
        if (_gate == null)
        {
            reason = derivedReason;
            return derived;
        }
        var source = _gate.Of(recordClass, id);
        if (source == CompactValueGate.Source.None)
        {
            reason = "value rule not verified for this parameter";
            return null;
        }
        // the element's own FamilyParams record is the value printed for a text or an element reference; the reader's own
        // parameter can be a heuristic that replaced an empty text
        if (record != null && storage == StorageType.String) return record.Str ?? "";
        if (record != null && storage == StorageType.ElementId && CompactValueRules.ElementIdText(record.ElemId, _names.NameOf) is { } referenced)
            return referenced;
        if (source == CompactValueGate.Source.Stored)
        {
            reason = storedReason;
            return stored;
        }
        // the reader holds nothing: for a pair whose stored text was right wherever that happened, the stores decide
        if (derived == null && _gate.AbsentIsStored(recordClass, id))
        {
            reason = storedReason;
            return stored;
        }
        reason = derivedReason;
        return derived;
    }

    private const int ThermalCoefficientParameter = -1005430, SolarHeatGainParameter = -1005432, VisualLightParameter = -1005433;
    private const int AnalyticConstructionParameter = -1005437, CurtainPanelsCategory = -2000170;
    private const int ScheduleLevelParameter = -1002062;
    private const int CurveIsDetailParameter = -1004011, CurveElemLengthParameter = -1004005, SystemLengthParameter = -1001375;
    private const string CurveElemClass = "CurveElem", FamilyInstanceClass = "FamilyInstance";
    private const int HostAreaParameter = -1012805, HostVolumeParameter = -1012806, MassCategory = -2003400;
    private const int SurveyBottomParameter = -1001658, SurveyTopParameter = -1001657;

    /// <summary>The text of a quantity of the solids in the units of the file.</summary>
    private string? SolidText(int id, double? value, out string? reason)
    {
        reason = null;
        if (value is not double number) { reason = "the solids do not hold the value"; return null; }
        if (_format == null) { reason = "no format settings"; return null; }
        ApiParameterDefinitions.TryGet(id, out _, out _, out string spec);
        string? text = CompactValueFormatter.Format(number, spec, _format);
        if (text == null) reason = "solid quantity not formatted (unit or spec unknown)";
        return text;
    }

    /// <summary>True when the symbol of the family instance is a loadable family symbol, not a system symbol (SysPanelFamSym, SysMullionFamSym).</summary>
    private bool IsLoadableFamilyInstance(Element element) =>
        (element.NativeMasterSymbolId ?? element.InstanceSymbolId) is int symbol && _names.Find(symbol) is { } symbolElement
        && ElementListFacts.RecordClassName(symbolElement) == "FamilySymbol";

    /// <summary>The first FamilyParams record of the element for the id (the reader keeps the first of a repeated id).</summary>
    private TfBlockDeserializer.NamedParamRecord? OwnRecord(Element element, int id)
    {
        if (_state == null) return null;
        var native = _state.StoresOf((uint)element.Id);
        foreach (var record in native.NamedParams)
            if (record.ParamId == id) return record;
        return null;
    }

    /// <summary>"Analytic Construction" of a family symbol: the stored name of an entry of the construction lookup table. The empty
    /// entry prints "&lt;None&gt;" in any locale, as does an entry unknown to the target table; a known entry prints as stored. Only the
    /// empty entry is written.</summary>
    private string? AnalyticConstructionText(int id, TfBlockDeserializer.NamedParamRecord? record, out string? reason)
    {
        reason = null;
        string? name = record != null ? record.Str : _held.TryGetValue(id, out Parameter? held) && held.Value is string text ? text : null;
        if (IsLookupNone(name)) return "<None>";
        reason = name == null ? "value not held by the reader" : "analytic construction: the name the expected export prints for this entry is not validated";
        return null;
    }

    private string? AnalyticConstructionName(Element element, TfBlockDeserializer.NamedParamRecord? record) =>
        record != null ? record.Str : element.Parameters.FirstOrDefault(p => p.Id == AnalyticConstructionParameter)?.Value as string;

    private bool IsLookupNone(string? name) => name != null && (name == "<None>" || name == _lookupNoneText);

    /// <summary>The value of a parameter that is not computed: what the element's complete parameter stores hold, "" when they
    /// do not hold the id. <paramref name="held"/> is the text of the reader's value.</summary>
    private string? StoredText(Element element, int id, string? held, StorageType storage, string spec, out string? reason)
    {
        reason = null;
        if (!element.NativeParameterStoresComplete || element.NativeParameterStoreIds is not { } stores)
        {
            reason = "the parameter stores of the element are not decoded completely";
            return null;
        }
        if (!stores.Contains(id))
        {
            // a value the element keeps in a family parameter record (symbols of Revit 2025 do) is not in the stores
            if (element.NativeRootClassName != null && OwnRecord(element, id) != null) { reason = "the value is in a family parameter record, not in the stores"; return null; }
            // a wide-layout type keeps the value in a field of its own class, not in the stores (Abbreviation of a system type:
            // RbsSystemType.m_abbreviation); the field table sets such a parameter
            if (element.NativeRootClassName != null && held != null && NativeFieldParameters.All.Any(row => row.ParamId == id)) return held;
            return "";
        }
        // a number is printed from the value of the store itself: the reader's parameter of the same id can have been replaced by a
        // derived value (wall openings: Base Offset stored as 109,01, 0 in the reader's parameter)
        if (storage == StorageType.Double && _state != null)
        {
            foreach (var row in _state.StoresOf((uint)element.Id).Doubles)
            {
                if (row.ParamId != id) continue;
                if (spec.Length == 0 || _format == null) { reason = "double without a spec or format settings"; return null; }
                string? formatted = CompactValueFormatter.Format(row.Value, spec, _format);
                if (formatted == null) reason = "stored double not formatted (unit or spec unknown)";
                return formatted;
            }
        }
        if (held == null) reason = "stored value not available as text";
        return held;
    }

    /// <summary>The text of the value the reader holds for the id; null with the reason when there is none or it cannot be
    /// written exactly.</summary>
    private string? DerivedText(int id, StorageType storage, string spec, out string? reason)
    {
        reason = null;
        if (!_held.TryGetValue(id, out Parameter? parameter) || parameter.Value == null)
        {
            reason = "value not held by the reader";
            return null;
        }
        if (_conflicting.Contains(id)) { reason = "reader holds conflicting values"; return null; }
        if (parameter.IsApproximate) { reason = "reader value is approximate"; return null; }
        switch (storage)
        {
            case StorageType.String:
                if (parameter.Value is string text) return text;
                break;
            case StorageType.Integer:
                if (parameter.Value is int integer) return CompactValueRules.IntegerText(integer, spec);
                if (parameter.Value is bool flag) return CompactValueRules.IntegerText(flag ? 1 : 0, spec);
                break;
            case StorageType.Double:
                if (parameter.Value is double or int)
                {
                    if (spec.Length == 0) { reason = "double without a spec"; return null; }
                    if (_format == null) { reason = "no format settings"; return null; }
                    double number = parameter.AsDouble();
                    if (id is SurveyBottomParameter or SurveyTopParameter)
                    {
                        // an elevation relative to the survey point: the internal elevation moved by the active project location
                        if (_surveyOffsetFeet is not double offset) { reason = "survey elevation: the project location is not resolved"; return null; }
                        number += offset;
                    }
                    string? formatted = CompactValueFormatter.Format(number, spec, _format);
                    if (formatted == null) reason = "double not formatted (unit or spec unknown)";
                    return formatted;
                }
                break;
            case StorageType.ElementId:
                if (parameter.Value is int target)
                {
                    string? name = CompactValueRules.ElementIdText(target, _names.NameOf);
                    if (name == null) reason = "referenced element name not validated";
                    return name;
                }
                break;
        }
        reason = "reader value has another storage kind";
        return null;
    }

    /// <summary>One summary line, then the reasons entries were left out, most frequent first.</summary>
    public void WriteReport(TextWriter output)
    {
        output.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Compact built-in parameters: {Written} entries written, id list known for {ElementsWithList} of {Elements} elements"));
        foreach (var (reason, count) in _skipped.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
            output.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  left out x{count}: {reason}"));
    }

    private void Skip(string reason) => _skipped[reason] = _skipped.GetValueOrDefault(reason) + 1;
}
