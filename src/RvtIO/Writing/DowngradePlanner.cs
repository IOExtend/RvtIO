using OpenMcdf;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>How the upgrade-identifier list of a class that both schemas define is planned.</summary>
internal enum GuidRule
{
    /// <summary>The list becomes the list of the same class in the target (the default).</summary>
    Exact,
    /// <summary>Only the identifiers the target list of the same class does not hold are removed; the target identifiers the file lacks are not added.</summary>
    Minimal,
}

/// <summary>The files cannot be planned for a reason of scope (release, class table layout, an edit that does not apply). <see cref="Kind"/> is a stable code.</summary>
internal sealed class DowngradeRefusedException(string kind, string message) : Exception(message)
{
    internal string Kind { get; } = kind;
}

internal sealed record VersionChange(string Name, int Source, int Target);

/// <param name="Source">Entries of the list in the source.</param>
/// <param name="Target">Entries of the list in the target.</param>
/// <param name="Result">Entries after the edit.</param>
/// <param name="Added">Entries of the result that the source list lacks.</param>
/// <param name="Removed">Entries of the source list that the result lacks.</param>
internal sealed record GuidListChange(string Name, int Source, int Target, int Result, int Added, int Removed);

/// <summary>Field names of a class that exist in one schema only (stored fields, matched by name).</summary>
internal sealed record FieldDifference(string Name, IReadOnlyList<string> OnlyInSource, IReadOnlyList<string> OnlyInTarget);

/// <param name="Elements">Elements of this class in the source file, or null when they were not counted.</param>
internal sealed record SourceOnlyClass(string Name, int? Elements);

/// <summary>What changing the class table of the source to the target would do, computed from the two schemas alone.</summary>
internal sealed class DowngradePlan
{
    internal required GuidRule Rule { get; init; }
    internal required int SourceClasses { get; init; }
    internal required int TargetClasses { get; init; }
    internal required int CommonClasses { get; init; }
    internal required IReadOnlyList<SourceOnlyClass> ClassesOnlyInSource { get; init; }
    internal required IReadOnlyList<string> ClassesOnlyInTarget { get; init; }
    internal required IReadOnlyList<VersionChange> HigherVersions { get; init; }
    /// <summary>Classes whose source version is below the target version; nothing is changed for them.</summary>
    internal required int LowerVersions { get; init; }
    internal required IReadOnlyList<FieldDifference> FieldDifferences { get; init; }
    internal required IReadOnlyList<GuidListChange> GuidLists { get; init; }
    /// <summary>The edit list for <see cref="SchemaStreamEditor"/>: one entry per class whose version or list changes.</summary>
    internal required IReadOnlyList<SchemaEdit> Edits { get; init; }
    /// <summary>Elements the source file holds in classes the target lacks; null when they were not counted.</summary>
    internal int? ElementsInSourceOnlyClasses => ClassesOnlyInSource.Any(c => c.Elements is null) ? null : ClassesOnlyInSource.Sum(c => c.Elements!.Value);
}

/// <summary>
/// Plans the edit of the class table (Formats/Latest) of a newer file so that an older release may load it: every class version above the
/// version of the target release is lowered to it, and the upgrade-identifier list of every class that both tables define is made to fit
/// the target. Classes are matched by name. Nothing is written; the plan lists the edits and what differs between the two tables.
///
/// The plan keeps these properties: a version is only ever lowered, a class that only one table defines is never edited, and no list is
/// emptied as a blanket step: a list becomes empty only for a class whose list in the target is empty (exact rule) or holds none of the
/// source identifiers (minimal rule).
/// </summary>
internal static class DowngradePlanner
{
    /// <summary>The newest release tested as a source.</summary>
    internal const int NewestSourceRelease = 2027;

    /// <summary>The plan from two parsed schemas. <paramref name="elementCounts"/> maps class names to the elements the source file holds
    /// (null: not counted).</summary>
    internal static DowngradePlan Compute(SchemaDocument source, SchemaDocument target, GuidRule rule, IReadOnlyDictionary<string, int>? elementCounts = null)
    {
        if (!source.GuidLists) throw new DowngradeRefusedException("source-layout", "The class table of the source has no upgrade-identifier lists.");
        if (!target.GuidLists) throw new DowngradeRefusedException("profile-layout", "The class table of the profile has no upgrade-identifier lists.");
        var sourceByName = ByName(source, "source");
        var targetByName = ByName(target, "profile");

        var onlySource = new List<SourceOnlyClass>();
        var higher = new List<VersionChange>();
        var fields = new List<FieldDifference>();
        var lists = new List<GuidListChange>();
        var edits = new List<SchemaEdit>();
        int lower = 0, common = 0;
        foreach (var s in source.Classes)
        {
            if (!targetByName.TryGetValue(s.Name, out var t))
            {
                onlySource.Add(new SourceOnlyClass(s.Name, elementCounts is null ? null : elementCounts.GetValueOrDefault(s.Name)));
                continue;
            }
            common++;
            int? newVersion = null;
            if (s.Version > t.Version) { higher.Add(new VersionChange(s.Name, s.Version, t.Version)); newVersion = t.Version; }
            else if (s.Version < t.Version) lower++;

            var sourceFields = s.Fields.Where(f => f.Stored).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            var targetFields = t.Fields.Where(f => f.Stored).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            var onlyInSource = sourceFields.Where(n => !targetFields.Contains(n)).ToList();
            var onlyInTarget = targetFields.Where(n => !sourceFields.Contains(n)).ToList();
            if (onlyInSource.Count > 0 || onlyInTarget.Count > 0) fields.Add(new FieldDifference(s.Name, onlyInSource, onlyInTarget));

            var planned = PlanList(s.Guids, t.Guids, rule);
            string[]? newList = null;
            if (!planned.SequenceEqual(s.Guids, StringComparer.OrdinalIgnoreCase))
            {
                newList = [.. planned];
                var sourceSet = s.Guids.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var plannedSet = planned.ToHashSet(StringComparer.OrdinalIgnoreCase);
                lists.Add(new GuidListChange(s.Name, s.Guids.Count, t.Guids.Count, planned.Count,
                    plannedSet.Count(g => !sourceSet.Contains(g)), sourceSet.Count(g => !plannedSet.Contains(g))));
            }
            if (newVersion is not null || newList is not null) edits.Add(new SchemaEdit(s.Name, newVersion, newList));
        }
        return new DowngradePlan
        {
            Rule = rule,
            SourceClasses = source.Classes.Count,
            TargetClasses = target.Classes.Count,
            CommonClasses = common,
            ClassesOnlyInSource = onlySource,
            ClassesOnlyInTarget = [.. target.Classes.Where(c => !sourceByName.ContainsKey(c.Name)).Select(c => c.Name)],
            HigherVersions = higher,
            LowerVersions = lower,
            FieldDifferences = fields,
            GuidLists = lists,
            Edits = edits,
        };
    }

    // Exact: the list of the target as it is (a class the target gives no identifiers gets an empty list). Minimal: the source list without
    // the identifiers the target list lacks. A list is empty only when the rule yields nothing for that class; there is no step that empties lists.
    private static List<string> PlanList(IReadOnlyList<string> source, IReadOnlyList<string> target, GuidRule rule)
        => rule == GuidRule.Exact
            ? [.. target]
            : [.. source.Where(g => target.Contains(g, StringComparer.OrdinalIgnoreCase))];

    private static Dictionary<string, SchemaClass> ByName(SchemaDocument document, string role)
    {
        var map = new Dictionary<string, SchemaClass>(StringComparer.Ordinal);
        foreach (var c in document.Classes)
            if (!map.TryAdd(c.Name, c))
                throw new DowngradeRefusedException("ambiguous-class", $"The class table of the {role} defines the class name {c.Name} more than once.");
        return map;
    }

    /// <summary>The outcome of planning two files.</summary>
    internal sealed record Result(DowngradePlan Plan, int SourceRelease, int TargetRelease, int SourceStreamBytes, int EditedStreamBytes, string? CountFailure);

    /// <summary>Reads the source file and the profile file and plans the edit. Nothing is written.</summary>
    /// <exception cref="RvtInputException">A file is missing, not a compound file, cut or damaged, or its Formats/Latest cannot be read.</exception>
    /// <exception cref="DowngradeRefusedException">The releases are out of scope or the class tables cannot be planned.</exception>
    internal static Result Plan(string sourcePath, string profilePath, GuidRule rule, bool countElements = true)
    {
        RvtContainerCheck.Verify(sourcePath);
        RvtContainerCheck.Verify(profilePath);
        using var sourceRoot = RootStorage.OpenRead(sourcePath);
        using var profileRoot = RootStorage.OpenRead(profilePath);
        byte[] sourceStream = ReadStream(sourceRoot, "source", sourcePath);
        byte[] profileStream = ReadStream(profileRoot, "profile", profilePath);
        var sourceSchema = ParseSchema(sourceStream, "source", sourcePath);
        var profileSchema = ParseSchema(profileStream, "profile", profilePath);

        int sourceRelease = SchemaRelease.FromSchema(new FormatsSchema(sourceSchema))
            ?? throw new DowngradeRefusedException("source-release", "The release of the source file is not recognised from its class table.");
        int targetRelease = SchemaRelease.FromSchema(new FormatsSchema(profileSchema))
            ?? throw new DowngradeRefusedException("profile-release", "The release of the profile file is not recognised from its class table.");
        if (SchemaRelease.Saved(BasicFileInfoReader.Read(sourceRoot).GetValueOrDefault("RevitVersion")) is int saved && saved != sourceRelease)
            throw new DowngradeRefusedException("source-release", $"The source file is saved by release {saved} but carries the class table of release {sourceRelease}.");
        if (sourceRelease > NewestSourceRelease)
            throw new DowngradeRefusedException("source-too-new", $"The source file is from release {sourceRelease}; the newest release tested as a source is {NewestSourceRelease}.");
        if (targetRelease >= sourceRelease)
            throw new DowngradeRefusedException("profile-not-older", $"The profile file is from release {targetRelease}, which is not older than the source (release {sourceRelease}).");

        var plan = Compute(sourceSchema, profileSchema, rule);
        string? countFailure = null;
        if (countElements)
        {
            var counts = ElementCensus.CountByClass(sourceRoot, sourceSchema, out countFailure);
            if (counts is not null) plan = Compute(sourceSchema, profileSchema, rule, counts);
        }

        byte[] edited;
        try { edited = SchemaStreamEditor.Apply(sourceStream, plan.Edits); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or FormatException)
        {
            throw new DowngradeRefusedException("edit-not-applicable", $"The planned edit does not apply to the class table of the source: {exception.Message}");
        }
        return new Result(plan, sourceRelease, targetRelease, sourceStream.Length, edited.Length, countFailure);
    }

    private static byte[] ReadStream(RootStorage root, string role, string path)
        => PagedGzipStream.Read(root, "Formats", "Latest")
            ?? throw new RvtInputException("truncated-or-damaged", $"The class table (Formats/Latest) of the {role} file '{Path.GetFileName(path)}' is missing or cannot be read.");

    private static SchemaDocument ParseSchema(byte[] stream, string role, string path)
        => TfSchemaReader.TryParse(stream)
            ?? throw new RvtInputException("truncated-or-damaged", $"The class table (Formats/Latest) of the {role} file '{Path.GetFileName(path)}' does not follow the expected layout.");
}
