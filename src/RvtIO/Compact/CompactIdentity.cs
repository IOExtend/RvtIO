using System.Globalization;
using RvtIO.Lookup;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Compact;

/// <summary>A text that is known (it may be empty) or unknown; an unknown text names what the reader lacks.</summary>
internal readonly record struct CompactText(string? Value, string? Unknown)
{
    public bool IsKnown => Value is not null;

    public static CompactText Known(string value) => new(value, null);

    public static CompactText Missing(string reason) => new(null, reason);
}

/// <summary>The type element of an element (getTypeID); <see cref="Type"/> is null for "no type" when <see cref="IsKnown"/>.</summary>
internal readonly record struct CompactType(bool IsKnown, Element? Type, string? Unknown);

/// <summary>What the two pseudo objects of a node are: <see cref="IsKnown"/> false when the reader cannot decide whether they are
/// written; otherwise both texts are set (two objects) or both are null (none).</summary>
internal readonly record struct CompactPseudo(bool IsKnown, string? CategoryLabel, string? FamilyName);

/// <summary>
/// The identity of an element as the current profile computes it for CompactJsonExport: the category label and the family name of the two
/// pseudo objects in front of a node, and the values of the "Invalid group" parameters (Category, Family Name, and Family, Type,
/// Type Id, Family and Type of an instance). Every answer is exact or unknown; nothing is estimated.
/// <para>
/// Pseudo objects. They are written when the label of the header category and <c>getFamilyName()</c> are both non-empty
/// (CompactJsonExport, parseParameterList). The label is a function of the header category id (<see cref="EnglishCategoryNames"/>);
/// the header category is <see cref="CompactHeaderCategory.Of"/>.
/// </para>
/// <para>
/// getFamilyName(). Taken per class from the vtables of the current profile modules (<see cref="CompactIdentityRules"/>): the default
/// implementation asks the element's type (getTypeID) and, when that is a Symbol, the type's own getFamilyName; the other classes
/// return a constant (a system family name), nothing (the Symbol base), or decide on a stored field.
/// </para>
/// <para>
/// getTypeID() is the null id for most classes; the others return a stored id. The reader's type id stands for it only
/// on classes where it is equal on every validated element ("R" in <see cref="CompactIdentityRules"/>).
/// </para>
/// </summary>
internal sealed class CompactIdentity
{
    internal const string InvalidGroup = "Invalid group";

    private readonly record struct ClassRule(
        char Family, string? Text, string? Special, char Type, string? TypeField, bool IsSymbol, char Category, bool ViewType);

    private static readonly Dictionary<string, ClassRule> Rules = ParseRules();

    /// <summary>Header categories whose enum name is not printed (the enum-to-text table has no entry although the category
    /// has a label): OST_PropertySet prints the empty text.</summary>
    private static readonly HashSet<int> CategoriesWithoutEnumName = [-2000924];

    private const int StructuralFoundationCategory = -2001300;
    private const int DetailGroupsCategory = -2000096;
    private const int AttachedDetailGroupsCategory = -2000097;

    private readonly Dictionary<int, Element> _rows = [];
    private readonly Func<Element, int?> _headerCategory;
    private readonly Func<Element, string?> _elementName;
    internal bool UsesDefaultResolvers { get; }

    /// <param name="rows">The elements of the element table (<see cref="CompactUniverse"/>); a type is looked up among them.</param>
    /// <param name="headerCategory">The header category of an element, null when unknown (default <see cref="CompactHeaderCategory.Of"/>).</param>
    /// <param name="elementName">The element name (getElementName) of an element, null when the reader cannot confirm it
    /// (default <see cref="Element.Name"/>, which holds a validated name after <c>ValidatedElementNames.Apply</c>).</param>
    public CompactIdentity(IEnumerable<Element> rows, Func<Element, int?>? headerCategory = null, Func<Element, string?>? elementName = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var element in rows) _rows.TryAdd(element.Id, element);
        UsesDefaultResolvers = headerCategory == null && elementName == null;
        _headerCategory = headerCategory ?? CompactHeaderCategory.Of;
        _elementName = elementName ?? (element => element.Name);
    }

    public int? HeaderCategory(Element element) => _headerCategory(element);

    /// <summary>The class name of the element (the schema name of its root class), null when the reader cannot name it.</summary>
    public static string? RecordClassName(Element element) => CompactNodeBuilder.ResolveClassName(element, out _);

    /// <summary>Whether a rule table covers the class.</summary>
    internal static bool IsKnownClass(string recordClass) => Rules.ContainsKey(recordClass);

    /// <summary>The label printed for the header category: empty for no category, a category element and a built-in category
    /// without a label.</summary>
    public CompactText CategoryLabel(Element element)
    {
        if (_headerCategory(element) is not int category) return CompactText.Missing("header category not known");
        return CompactText.Known(category < 0 ? EnglishCategoryNames.Get(category) ?? "" : "");
    }

    public CompactPseudo Pseudo(Element element)
    {
        CompactText label = CategoryLabel(element);
        CompactText family = FamilyName(element);
        if (label.Value is { Length: 0 } || family.Value is { Length: 0 }) return new CompactPseudo(true, null, null);
        if (!label.IsKnown || !family.IsKnown) return new CompactPseudo(false, null, null);
        return new CompactPseudo(true, label.Value, family.Value);
    }

    /// <summary>Family name of the element.</summary>
    public CompactText FamilyName(Element element) => FamilyName(element, 0);

    private CompactText FamilyName(Element element, int depth)
    {
        if (!TryRule(element, out string cls, out ClassRule rule, out string? unknown)) return CompactText.Missing(unknown!);
        switch (rule.Family)
        {
            case 'E': return CompactText.Known("");
            case 'K': return rule.Text is null ? CompactText.Missing("constant family name of " + cls + " not known") : CompactText.Known(rule.Text);
            case 'X': return Special(rule.Special!, element);
        }
        // the default implementation: the family name of the type when the type is a Symbol
        CompactType type = TypeOf(element);
        if (!type.IsKnown) return CompactText.Missing(type.Unknown!);
        if (type.Type is null) return CompactText.Known("");
        if (!TryRule(type.Type, out _, out ClassRule typeRule, out unknown)) return CompactText.Missing(unknown!);
        if (!typeRule.IsSymbol) return CompactText.Known("");
        // a Symbol class never uses the default implementation, so this recursion ends after one step
        return depth > 2 ? CompactText.Missing("type chain too long") : FamilyName(type.Type, depth + 1);
    }

    private CompactText Special(string tag, Element element)
    {
        switch (tag)
        {
            case "FamilySymbol": return FamilyOfSymbol(element);
            case "DimensionStyle": return DimensionStyleFamily(element);
            case "FloorAttributes":
                // the structural foundation category selects the foundation slab family
                return _headerCategory(element) is int category
                    ? CompactText.Known(category == StructuralFoundationCategory ? "Foundation Slab" : "Floor")
                    : CompactText.Missing("header category not known");
            case "DBViewProject": return CompactText.Known("Project");
            case "DBViewSection":
                return Field(element, TfClassNames.Key("DBViewSection", "m_sectionViewType")) is int section
                    ? CompactText.Known(section == 0 ? "Building Section" : section == 3 ? "Section" : "Elevation")
                    : NotDecoded(tag);
            case "DBViewPlan": return PlanViewFamily(element);
            case "ConceptualConstructionType":
                return Field(element, TfClassNames.Key("ConceptualConstructionType", "m_massSurfaceSubCategoryId")) is int mass
                    ? CompactText.Known(mass == -2003421 ? "Mass Walls" : mass == -2003414 ? "Mass Roof" : "")
                    : NotDecoded(tag);
            case "CompoundCeilingType":
                // a ceiling type with a compound structure is a compound ceiling
                return Field(element, TfClassNames.Key("HostObjAttr", "m_pCompoundStructure")) is int structure
                    ? CompactText.Known(structure == 0 ? "Basic Ceiling" : "Compound Ceiling")
                    : NotDecoded(tag);
            case "StairsType":
                return Field(element, TfClassNames.Key("StairsType", "m_constructionMethod")) is int method
                    ? CompactText.Known(method == 1 ? "Cast-In-Place Stair" : method == 2 ? "Precast Stair" : "Assembled Stair")
                    : NotDecoded(tag);
            case "StairsRunType":
                return Field(element, TfClassNames.Key("StairsRunType", "m_hasMonolithicSupport")) is int run
                    ? CompactText.Known(run != 0 ? "Monolithic Run" : "Non-Monolithic Run")
                    : NotDecoded(tag);
            case "StairsLandingType":
                return Field(element, TfClassNames.Key("StairsLandingType", "m_hasMonolithicSupport")) is int landing
                    ? CompactText.Known(landing != 0 ? "Monolithic Landing" : "Non-Monolithic Landing")
                    : NotDecoded(tag);
            case "StairsSupportType":
                return Field(element, TfClassNames.Key("StairsSupportType", "m_topsideSurfaceType")) is int surface
                    ? CompactText.Known(surface == 0 ? "Stringer" : surface == 1 ? "Carriage" : "")
                    : NotDecoded(tag);
            case "StairsPathType":
                return Field(element, TfClassNames.Key("StairsPathType", "m_stairsPathDirection")) is int direction
                    ? CompactText.Known(direction == 1 ? "Automatic Up/Down Direction" : "Fixed Up Direction")
                    : NotDecoded(tag);
            case "RbsCableTrayType":
                return Field(element, TfClassNames.Key("RbsCableTrayType", "m_bWithFitting")) is int tray
                    ? CompactText.Known(tray != 0 ? "Cable Tray with Fittings" : "Cable Tray without Fittings")
                    : NotDecoded(tag);
            case "RbsConduitType":
                return Field(element, TfClassNames.Key("RbsConduitType", "m_bWithFitting")) is int conduit
                    ? CompactText.Known(conduit != 0 ? "Conduit with Fittings" : "Conduit without Fittings")
                    : NotDecoded(tag);
            case "AbsDuctType": return DuctFamily(element, "Rectangular Duct", "Round Duct", "Oval Duct");
            case "AbsFlexDuctType": return DuctFamily(element, "Flex Duct Rectangular", "Flex Duct Round", null);
            case "BrowserOrganization":
                return Field(element, TfClassNames.Key("BrowserOrganization", "m_type")) is int organization
                    ? CompactText.Known(organization == 0 ? "Browser - Views" : organization == 1 ? "Browser - Sheets" : "")
                    : NotDecoded(tag);
            case "ElementGroupType": return GroupTypeFamily(element);
            default: return CompactText.Missing("family name rule " + tag + " not modelled");
        }
    }

    private static int? Field(Element element, string key)
        => element.NativeListFields is { } fields && fields.TryGetValue(key, out int value) ? value : null;

    private static CompactText NotDecoded(string tag) => CompactText.Missing("the field behind the family name of " + tag + " is not decoded");

    /// <summary>Duct types take their family from the class of the owned profile: 124 round, 126 oval, 127 rectangular.</summary>
    private static CompactText DuctFamily(Element type, string rectangular, string round, string? oval)
    {
        if (Field(type, TfClassNames.Key("AbsCurveType", "m_Profile")) is not int profile) return NotDecoded("duct type");
        string? name = profile switch { 127 => rectangular, 124 => round, 126 => oval, _ => null };
        return name is null ? CompactText.Missing("duct profile " + profile.ToString(CultureInfo.InvariantCulture) + " has no known text") : CompactText.Known(name);
    }

    /// <summary>A plan view: the ceiling plan type is a ceiling plan; the floor plan type reports "Floor Plan" through its view
    /// type, nothing without one. A view type of the area plan class reports "Area Plan" when its area measure is set, which
    /// the reader does not decode.</summary>
    private CompactText PlanViewFamily(Element view)
    {
        if (Field(view, TfClassNames.Key("DBViewPlan", "m_planViewType")) is not int planType) return NotDecoded("plan view");
        if (planType == 2) return CompactText.Known("Ceiling Plan");
        if (planType != 1) return CompactText.Known("");
        if (Field(view, TfClassNames.Key("DBView", "m_dbViewTypeId")) is not int typeId) return NotDecoded("plan view");
        if (typeId <= 0) return CompactText.Known("");
        if (!_rows.TryGetValue(typeId, out Element? viewType)) return CompactText.Missing("view type of the plan view not found");
        return RecordClassName(viewType) == "DBViewTypeAreaPlan" ? CompactText.Missing("area measure of the area plan not decoded") : CompactText.Known("Floor Plan");
    }

    /// <summary>A group type without a placement helper is a detail group in the detail group category, a model group otherwise
    /// (the attached detail group category asks another virtual function; a helper gives its own name).</summary>
    private CompactText GroupTypeFamily(Element groupType)
    {
        if (Field(groupType, TfClassNames.Key("ElementGroupType", "m_placementHelper")) is not int helper) return NotDecoded("group type");
        if (helper != 0) return CompactText.Missing("placement helper of the group type not resolved");
        if (_headerCategory(groupType) is not int category) return CompactText.Missing("header category not known");
        return category == AttachedDetailGroupsCategory ? CompactText.Missing("attached detail group type not modelled")
            : CompactText.Known(category == DetailGroupsCategory ? "Detail Group" : "Model Group");
    }

    /// <summary>A family symbol takes the name of its family element.</summary>
    private CompactText FamilyOfSymbol(Element symbol)
    {
        if (symbol.SymbolFamilyId is not int id || id <= 0 || !_rows.TryGetValue(id, out Element? family))
            return CompactText.Missing("family element of the symbol not found");
        if (RecordClassName(family) != "Family") return CompactText.Missing("family element of the symbol is not a family");
        return _elementName(family) is { Length: > 0 } name ? CompactText.Known(name) : CompactText.Missing("family name not validated");
    }

    /// <summary>The family of a dimension type follows its stored dimension style type: 0 linear, 1 angular, 2 radial, 9 diameter.</summary>
    private static CompactText DimensionStyleFamily(Element style)
    {
        if (Field(style, TfClassNames.Key("DimensionStyle", "m_dimensionStyleType")) is not int kind) return NotDecoded("dimension style");
        return kind switch
        {
            0 => CompactText.Known("Linear Dimension Style"),
            1 => CompactText.Known("Angular Dimension Style"),
            2 => CompactText.Known("Radial Dimension Style"),
            9 => CompactText.Known("Diameter Dimension Style"),
            _ => CompactText.Missing("dimension style type " + kind.ToString(CultureInfo.InvariantCulture) + " has no known text"),
        };
    }

    /// <summary>The type element that getTypeID names.</summary>
    public CompactType TypeOf(Element element)
    {
        if (!TryRule(element, out string cls, out ClassRule rule, out string? unknown)) return new CompactType(false, null, unknown);
        switch (rule.Type)
        {
            case 'N': return new CompactType(true, null, null);
            case 'R': return ResolveType(element, element.TypeId, cls);
            case 'F':
                // the type id is a stored field of the element itself; -1 is the null id
                if (Field(element, rule.TypeField!) is not int stored) return new CompactType(false, null, NotDecoded("type of " + cls).Unknown);
                // a tag always has a type: a tag that stores no symbol id (multi-reference tags) gets its type from another field
                // that is not decoded
                if (stored == -1 && cls == "IndependentTag") return new CompactType(false, null, "type of a tag without a symbol id not decoded");
                return stored == -1 ? new CompactType(true, null, null) : ResolveType(element, stored, cls);
            default: return new CompactType(false, null, "getTypeID of " + cls + " not modelled");
        }
    }

    private CompactType ResolveType(Element element, int id, string cls)
        => id > 0 && id != element.Id && _rows.TryGetValue(id, out Element? type)
            ? new CompactType(true, type, null)
            : new CompactType(false, null, "type element of " + cls + " not found");

    /// <summary>Category (ids -1140362 and -1140363): the enum name of the header category, "None" without one. The parameter can
    /// also be stored with the element (views and viewers keep the Views category) and the reader cannot tell which elements do,
    /// so only the classes whose value is the header category on every validated element answer.</summary>
    public CompactText CategoryValue(Element element)
    {
        if (!TryRule(element, out _, out ClassRule rule, out string? unknown)) return CompactText.Missing(unknown!);
        if (rule.Category != 'D') return CompactText.Missing("category parameter of this class may be stored");
        if (_headerCategory(element) is not int category) return CompactText.Missing("header category not known");
        if (category == CompactHeaderCategory.NullCategory) return CompactText.Known("None");
        if (category > 0) return CompactText.Known(category.ToString(CultureInfo.InvariantCulture));
        if (CategoriesWithoutEnumName.Contains(category)) return CompactText.Known("");
        return BuiltInCategoryNames.Lookup.TryGetValue(category, out string? name) ? CompactText.Known(name) : CompactText.Missing("category enum name not known");
    }

    /// <summary>Family Name (id -1002002): the family name of a type, empty for everything else.</summary>
    public CompactText FamilyNameParameter(Element element)
    {
        if (!TryRule(element, out string cls, out ClassRule rule, out string? unknown)) return CompactText.Missing(unknown!);
        // segment center lines have a family name ("Round Duct") although the class is no symbol
        if (!rule.IsSymbol) return cls.EndsWith("CenterLine", StringComparison.Ordinal) ? CompactText.Missing("a center line has a family name the expected export derives from its owner") : CompactText.Known("");
        return rule.ViewType ? CompactText.Missing("Family Name of a view type") : FamilyName(element);
    }

    /// <summary>Family, Type, Type Id and Family and Type (ids -1002051, -1002050, -1002000, -1002052): all four print the element
    /// name of the type element, "None" when there is none.</summary>
    public CompactText TypeValue(Element element)
    {
        // a viewport lists the Family, Type and Family and Type of its view under the same labels with smaller ids
        if (RecordClassName(element) == "Viewport") return CompactText.Missing("a viewport reports the Family and Type of its view");
        CompactType type = TypeOf(element);
        if (!type.IsKnown) return CompactText.Missing(type.Unknown!);
        if (type.Type is null) return CompactText.Known("None");
        return _elementName(type.Type) is { Length: > 0 } name ? CompactText.Known(name) : CompactText.Missing("name of the type element not validated");
    }

    /// <summary>True when the category parameter of the class equals the header category on every validated element (rule "D").</summary>
    internal static bool CategoryFollowsHeader(string recordClass) => Rules.TryGetValue(recordClass, out ClassRule rule) && rule.Category == 'D';

    private static bool TryRule(Element element, out string cls, out ClassRule rule, out string? unknown)
    {
        rule = default;
        unknown = null;
        cls = RecordClassName(element) ?? "";
        if (cls.Length == 0) { unknown = "The expected export class not known"; return false; }
        if (Rules.TryGetValue(cls, out rule)) return true;
        unknown = "class " + cls + " not covered";
        return false;
    }

    private static Dictionary<string, ClassRule> ParseRules()
    {
        var rules = new Dictionary<string, ClassRule>(StringComparer.Ordinal);
        foreach (string line in CompactIdentityRules.Classes)
        {
            string[] part = line.Split('|');
            if (part.Length != 6) throw new InvalidDataException("Malformed CompactIdentityRules line: " + line);
            string family = part[1];
            char kind = family[0];
            string? text = kind == 'K' && family.StartsWith("K=", StringComparison.Ordinal) ? family[2..] : null;
            string? special = kind == 'X' ? family[2..] : null;
            string? typeField = part[2][0] == 'F' ? part[2][2..] : null;
            rules.Add(part[0], new ClassRule(kind, text, special, part[2][0], typeField, part[3] == "S", part[4][0], part[5] == "V"));
        }
        return rules;
    }
}
