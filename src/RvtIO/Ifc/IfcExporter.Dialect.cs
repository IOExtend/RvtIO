using System.Globalization;
using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>Attribute spelling of the current profile that holds for whole families of entities, applied once to the
/// finished writer so the many entity writers keep their historical (the legacy profile) text.</summary>
public partial class IfcExporter
{
    internal static string CompoundPlaneAngle(double radians)
    {
        double part = radians * (180.0 / Math.PI);
        int degrees = (int)part; part -= degrees; part *= 60;
        int minutes = (int)part; part -= minutes; part *= 60;
        int seconds = (int)part; part -= seconds; part *= 1000000;
        int millionths = (int)part;
        return StepWriter.List(
            degrees.ToString(CultureInfo.InvariantCulture), minutes.ToString(CultureInfo.InvariantCulture),
            seconds.ToString(CultureInfo.InvariantCulture), millionths.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The current profile never leaves a string unset where its exporter passes an empty one: IfcRoot Name and Description,
    /// the Name and Description of a product representation, the Name of a colour and of a styled item are written <c>''</c>,
    /// not <c>$</c>.
    /// </summary>
    internal static int ApplyCurrentEmptyStrings(StepWriter w)
    {
        int rewritten = 0;
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (_, type, args) = w.EntityAt(i);
            string? replacement = type switch
            {
                "IFCPRODUCTDEFINITIONSHAPE" or "IFCMATERIALDEFINITIONREPRESENTATION" => EmptyLeadingStrings(args, 0, 2),
                "IFCCOLOURRGB" => EmptyLeadingStrings(args, 0, 1),
                "IFCSTYLEDITEM" => EmptyLeadingStrings(args, 2, 1),
                "IFCORGANIZATION" => EmptyOrganizationStrings(args),
                "IFCPROPERTYSET" => EmptyLeadingStrings(args, 3, 1),
                "IFCRELDEFINESBYPROPERTIES" => EmptyLeadingStrings(args, 2, 2),
                "IFCPROPERTYSINGLEVALUE" => EmptyLeadingStrings(args, 1, 1),
                "IFCELEMENTQUANTITY" => null,
                _ => EmptyRootStrings(args, type is "IFCPROJECT" or "IFCSITE" or "IFCBUILDING"),
            };
            if (replacement == null) continue;
            w.ReplaceArgs(i, replacement);
            rewritten++;
        }
        return rewritten;
    }

    /// <summary>IfcOrganization's Id and Description (attributes 0 and 2) are <c>''</c> when unset.</summary>
    private static string? EmptyOrganizationStrings(string args)
    {
        string withId = EmptyLeadingStrings(args, 0, 1) ?? args;
        string withDescription = EmptyLeadingStrings(withId, 2, 1) ?? withId;
        return ReferenceEquals(withDescription, args) ? null : withDescription;
    }

    /// <summary>
    /// Replaces the attributes <paramref name="first"/>.. <paramref name="first"/> + <paramref name="count"/> - 1 that are
    /// unset by <c>''</c>.
    /// </summary>
    private static string? EmptyLeadingStrings(string args, int first, int count)
    {
        string text = args;
        int position = 0;
        for (int index = 0; index < first + count && position <= text.Length; index++)
        {
            int end = TokenEnd(text, position);
            if (index >= first && end - position == 1 && text[position] == '$')
            {
                text = string.Concat(text.AsSpan(0, position), "''", text.AsSpan(end));
                end = position + 2;
            }
            position = end + 1;
        }
        return ReferenceEquals(text, args) ? null : text;
    }

    /// <summary>
    /// The Name and Description of an IfcRoot entity (attributes 2 and 3, after the 22-character GlobalId and the owner
    /// history) when they are unset; with <paramref name="objectType"/> also the ObjectType that follows the Description.
    /// </summary>
    private static string? EmptyRootStrings(string args, bool objectType)
    {
        if (args.Length < 30 || args[0] != '\'' || args[23] != '\'' || args[24] != ',' || args[25] != '#')
            return null;
        int nameStart = TokenEnd(args, 25) + 1;
        if (nameStart >= args.Length) return null;
        int descriptionStart = TokenEnd(args, nameStart) + 1;
        if (descriptionStart >= args.Length) return null;
        int descriptionEnd = TokenEnd(args, descriptionStart);
        bool withObjectType = objectType && descriptionEnd < args.Length;
        int objectTypeStart = descriptionEnd + 1;
        int objectTypeEnd = withObjectType ? TokenEnd(args, objectTypeStart) : descriptionEnd;
        string name = args[nameStart..(descriptionStart - 1)];
        string description = args[descriptionStart..descriptionEnd];
        bool unsetObjectType = withObjectType && objectTypeEnd - objectTypeStart == 1 && args[objectTypeStart] == '$';
        if (name != "$" && description != "$" && !unsetObjectType) return null;
        string tail = withObjectType
            ? "," + (unsetObjectType ? "''" : args[objectTypeStart..objectTypeEnd]) + args[objectTypeEnd..]
            : args[descriptionEnd..];
        return string.Concat(args[..nameStart], name == "$" ? "''" : name, ",",
            description == "$" ? "''" : description, tail);
    }

    /// <summary>
    /// The current profile opening elements: the ObjectType is "Opening", or "Recess" for a cut shorter than its host, and the
    /// Tag of the element the opening was created for.
    /// </summary>
    internal static int ApplyCurrentOpeningAttributes(StepWriter w, IReadOnlySet<int>? recessOpenings = null,
        IReadOnlySet<string>? doorWindowOpeningGlobalIds = null)
    {
        var indexById = new Dictionary<int, int>(w.EntityCount);
        var hostByOpening = new Dictionary<int, int>();
        var fillerByOpening = new Dictionary<int, int>();
        var openings = new List<int>();
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (id, type, args) = w.EntityAt(i);
            indexById[id] = i;
            if (type == "IFCOPENINGELEMENT") openings.Add(i);
            else if (type is "IFCRELVOIDSELEMENT" or "IFCRELFILLSELEMENT")
            {
                var arguments = SplitStepArguments(args);
                if (arguments.Length != 6 || !TryEntityId(arguments[4], out int relating)
                    || !TryEntityId(arguments[5], out int related))
                    continue;
                if (type == "IFCRELVOIDSELEMENT") hostByOpening[related] = relating;
                else fillerByOpening[relating] = related;
            }
        }

        int rewritten = 0;
        var contained = new List<(int Opening, int Host)>();
        foreach (int index in openings)
        {
            var (id, _, args) = w.EntityAt(index);
            string[] arguments = SplitStepArguments(args);
            if (arguments.Length != 8) continue;
            string tag = arguments[7];
            if (hostByOpening.TryGetValue(id, out int host) && indexById.TryGetValue(host, out int hostIndex)
                && SplitStepArguments(w.EntityAt(hostIndex).Args) is { Length: > 7 } hostArguments
                // A filled opening not named after its host is the filler's own extrusion: keep its Tag.
                && (!fillerByOpening.ContainsKey(id) || hostArguments[2] == arguments[2]))
            {
                tag = hostArguments[7];
                if (!fillerByOpening.ContainsKey(id) && doorWindowOpeningGlobalIds?.Contains(arguments[0].Trim('\'')) != true)
                    contained.Add((id, host));
            }
            string objectType = recessOpenings?.Contains(id) == true ? "'Recess'" : "'Opening'";
            if (arguments[4] == objectType && tag == arguments[7]) continue;
            arguments[4] = objectType;
            arguments[7] = tag;
            w.ReplaceArgs(index, string.Join(",", arguments));
            rewritten++;
        }
        ContainOpenings(w, contained);
        return rewritten;
    }

    /// <summary>Whether the current profile writes no opening beside this wall.</summary>
    private bool WallWritesNoBodyOpeningsCurrent(
        IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> products, int wallId)
    {
        if (!CurrentProfile || !products.TryGetValue(wallId, out var product)) return false;
        if (product.ifcType == "IFCWALL") return true;
        if (product.ifcType != "IFCWALLSTANDARDCASE" || _elementsByIdCurrent is not { } elements
            || !elements.TryGetValue(wallId, out var wall))
            return false;
        if (!_axisExtrusionBodyCurrent.TryGetValue(wallId, out bool? passes))
            _axisExtrusionBodyCurrent[wallId] = passes = PassesSimpleRectangularWallCurrent(wall);
        return passes == false;
    }

    private IReadOnlyDictionary<int, Element>? _elementsByIdCurrent;
    private readonly Dictionary<int, bool?> _axisExtrusionBodyCurrent = new();

    internal static bool IsDoorWindowCutoutExtrusionCurrent(
        Element cutter, int cutterProductEntityId, int hostWallId, IReadOnlyDictionary<int, Element> elementsById) =>
        cutterProductEntityId > 0 && cutter.CategoryId is -2000023 or -2000014 && cutter.SerializedHostId == hostWallId
        && cutter.FamilyTypeId > 0 && elementsById.TryGetValue(cutter.FamilyTypeId, out Element? symbol)
        && symbol.SerializedCutLoopCount is > 0;

    private static void ContainOpenings(StepWriter w, List<(int Opening, int Host)> openings)
    {
        if (openings.Count == 0) return;
        var relationByMember = new Dictionary<int, int>();
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (_, type, args) = w.EntityAt(i);
            if (type != "IFCRELCONTAINEDINSPATIALSTRUCTURE") continue;
            var relation = SplitStepArguments(args);
            if (relation.Length != 6 || relation[4].Length < 3 || relation[4][0] != '(') continue;
            foreach (string member in SplitStepArguments(relation[4][1..^1]))
                if (TryEntityId(member, out int memberId)) relationByMember.TryAdd(memberId, i);
        }
        var additions = new SortedDictionary<int, List<int>>();
        foreach (var (opening, host) in openings)
        {
            if (relationByMember.ContainsKey(opening) || !relationByMember.TryGetValue(host, out int relationIndex)) continue;
            if (!additions.TryGetValue(relationIndex, out var list)) additions[relationIndex] = list = new List<int>();
            list.Add(opening);
        }
        foreach (var (relationIndex, added) in additions)
        {
            var relation = SplitStepArguments(w.EntityAt(relationIndex).Args);
            relation[4] = relation[4][..^1] + "," + string.Join(",", added.Select(id => "#" + id)) + ")";
            w.ReplaceArgs(relationIndex, string.Join(",", relation));
        }
    }

    private static bool TryEntityId(string reference, out int id)
    {
        id = 0;
        return reference.Length > 1 && reference[0] == '#'
            && int.TryParse(reference.AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    /// <summary>
    /// The current profile type objects, found through their IfcRelDefinesByType: the Name is the type name alone, a wall type
    /// is always NOTDEFINED, and IfcFurnitureType carries the required AssemblyPlace.NOTDEFINED, also for
    /// furniture-system types.
    /// </summary>
    internal static int ApplyCurrentTypeObjects(StepWriter w, IReadOnlyDictionary<int, Element> elementsById,
        IReadOnlyDictionary<int, int> productElementIdByEntity)
    {
        var indexById = new Dictionary<int, int>(w.EntityCount);
        for (int i = 0; i < w.EntityCount; i++) indexById[w.EntityAt(i).Id] = i;

        int rewritten = 0;
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (_, relationType, relationArgs) = w.EntityAt(i);
            if (relationType != "IFCRELDEFINESBYTYPE") continue;
            var relation = SplitStepArguments(relationArgs);
            if (relation.Length != 6 || !TryEntityId(relation[5], out int typeEntity)
                || !indexById.TryGetValue(typeEntity, out int typeIndex)
                || relation[4].Length < 3 || relation[4][0] != '(')
                continue;
            var related = SplitStepArguments(relation[4][1..^1]);
            if (related.Length == 0 || !TryEntityId(related[0], out int firstProduct)
                || !productElementIdByEntity.TryGetValue(firstProduct, out int productElementId)
                || !elementsById.TryGetValue(productElementId, out Element? product)
                || !elementsById.TryGetValue(ResolveTypeObjectElementId(product, elementsById, currentProfile: true), out Element? typeElement))
                continue;

            var (type, typeArgs) = (w.EntityAt(typeIndex).Type, w.EntityAt(typeIndex).Args);
            string[] attributes = SplitStepArguments(typeArgs);
            if (attributes.Length < 9) continue;
            var (full, own) = TypeObjectNameParts(typeElement, true);
            bool changed = false;
            if (full != null && attributes[2] == S(full))
            {
                attributes[2] = S(own ?? full[(full.IndexOf(':') + 1)..]);
                changed = true;
            }
            if (type == "IFCWALLTYPE" && attributes.Length == 10 && attributes[9] != ".NOTDEFINED.")
            {
                attributes[9] = ".NOTDEFINED.";
                changed = true;
            }
            if (type == "IFCSYSTEMFURNITUREELEMENTTYPE" && attributes.Length == 9)
            {
                w.ReplaceEntity(typeIndex, "IFCFURNITURETYPE", string.Join(",", attributes) + ",.NOTDEFINED.");
                rewritten++;
                continue;
            }
            if (type == "IFCFURNITURETYPE" && attributes.Length == 10 && attributes[9] == "$")
            {
                attributes[9] = ".NOTDEFINED.";
                changed = true;
            }
            if (!changed) continue;
            w.ReplaceArgs(typeIndex, string.Join(",", attributes));
            rewritten++;
        }
        return rewritten;
    }

    internal static string CurrentShortDescriptiveName(string familyAndType)
    {
        int separator = familyAndType.IndexOf(':');
        return separator <= 0 ? familyAndType : string.Concat(familyAndType.AsSpan(0, separator), " : ",
            familyAndType.AsSpan(separator + 1));
    }

    private const int CurrentPadCategoryId = -2001263, CurrentFloorCategoryId = -2000032, CurrentFoundationCategoryId = -2001300;

    /// <summary>
    /// The PredefinedType of an IfcSlab occurrence in the current profile where it departs from the legacy profile rule
    /// <paramref name="resolved"/>: a building pad is NOTDEFINED, and a Floor element whose own type is a foundation slab type
    /// is BASESLAB.
    /// </summary>
    internal static string CurrentSlabPredefinedType(Element occurrence, Element? type, bool currentProfile, string resolved)
    {
        if (!currentProfile) return resolved;
        if (occurrence.CategoryId == CurrentPadCategoryId) return "NOTDEFINED";
        return resolved == "FLOOR" && occurrence.CategoryId == CurrentFloorCategoryId
            && type is { Id: > 0 } && type.Id != occurrence.Id && occurrence.FamilyTypeId == type.Id
            && type.CategoryId == CurrentFoundationCategoryId ? "BASESLAB" : resolved;
    }

    /// <summary>
    /// The trailing attributes of an IfcStairFlight in the current profile: NumberOfRiser, RiserHeight and NumberOfTreads of the run
    /// (its own riser count and per-run riser height), TreadLength of its stairs. An attribute whose source is not decoded stays unset.
    /// </summary>
    internal static string StairFlightAttributes(Element run, IReadOnlyDictionary<int, Element> elementsById,
        Dictionary<int, List<Element>>? stairRuns = null)
    {
        string risers = "$", treads = "$", riserHeight = "$", treadLength = "$";
        if (run.SerializedStairRunInput is { Numbers: { } numbers } input && input.OwnerId == run.Id)
        {
            elementsById.TryGetValue(input.StairsId, out var stairs);
            int runRisers = StairRunRisers(run, numbers, current: true), indexSpan = StairRunIndexSpan(numbers);
            if (runRisers > 0 && indexSpan > 0)
            {
                risers = runRisers.ToString(CultureInfo.InvariantCulture);
                if (StairRunCountsAgree(run, elementsById, numbers, current: true, stairRuns) && StairRunTreads(run, numbers, current: true) is int count && count > 0)
                    treads = count.ToString(CultureInfo.InvariantCulture);
                double runRiserMillimetres = (numbers.TopElevation - numbers.BottomElevation) / indexSpan * 304.8;
                if (double.IsFinite(runRiserMillimetres) && runRiserMillimetres > 0)
                    riserHeight = StepWriter.RealFromMillimetres(runRiserMillimetres);
            }
            else if (indexSpan <= 0 && stairs?.SerializedActualNumberOfRisers is int stairRisers && stairRisers > 0)
                risers = stairRisers.ToString(CultureInfo.InvariantCulture);
            if (riserHeight == "$" && stairs?.SerializedActualRiserHeightFeet is double riserFeet && double.IsFinite(riserFeet) && riserFeet > 0)
                riserHeight = StepWriter.RealFromMillimetres(riserFeet * 304.8);
            if (stairs?.SerializedActualTreadDepthFeet is double treadFeet && double.IsFinite(treadFeet) && treadFeet > 0)
                treadLength = StepWriter.RealFromMillimetres(treadFeet * 304.8);
        }
        return $"{risers},{treads},{riserHeight},{treadLength}";
    }

    internal static string? StoreyObjectType(Element level, Element? type) =>
        level.SerializedUiFamilyLabel is { Length: > 0 } family && type != null && type.Id != level.Id
            && type.CategoryId == level.CategoryId && type.Name is { Length: > 0 } typeName
            ? $"{family}:{typeName}" : null;

    /// <summary>The end (index of the separating comma, or the length) of the attribute that starts at
    /// <paramref name="start"/>, outside strings and nested lists.</summary>
    private static int TokenEnd(string args, int start)
    {
        int depth = 0;
        bool inString = false;
        for (int i = start; i < args.Length; i++)
        {
            char c = args[i];
            if (c == '\'') inString = !inString;
            else if (inString) continue;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) return i;
        }
        return args.Length;
    }
}
