using System.Globalization;
using RvtIO.Lookup;

namespace RvtIO.Compact;

/// <summary>The English labels of the compact JSON export: parameter captions, parameter group names, category names.</summary>
internal static class CompactLabels
{
    internal const string InvalidGroup = "Invalid group";

    /// <summary>BuiltInParameterGroup.INVALID.</summary>
    internal const int InvalidGroupId = -1;

    private static readonly Lazy<IReadOnlyDictionary<int, (int Group, string Label)>> Parameters = new(LoadParameters);
    private static readonly Lazy<IReadOnlyDictionary<int, string>> Groups = new(LoadGroups);
    // group label and caption the current profile prints where they differ from the Revit 2022 text (compact_label_overrides.tsv)
    private static readonly Lazy<IReadOnlyDictionary<int, (string Group, string Label)>> Overrides = new(LoadOverrides);

    internal static bool HasOverride(int builtinId) => Overrides.Value.ContainsKey(builtinId);

    internal static bool TryGetLabels(int builtinId, out string group, out string label)
    {
        if (Overrides.Value.TryGetValue(builtinId, out var validated))
        {
            (group, label) = validated;
            return true;
        }
        group = "";
        return TryGetParameterLabel(builtinId, out label) && TryGetParameterGroupLabel(builtinId, out group);
    }

    /// <summary>The caption of a builtin parameter (a negative BuiltInParameter value); false when no source lists it.</summary>
    internal static bool TryGetParameterLabel(int builtinId, out string label)
    {
        bool found = Parameters.Value.TryGetValue(builtinId, out var parameter);
        label = parameter.Label ?? "";
        return found;
    }

    /// <summary>The BuiltInParameterGroup value of a builtin parameter (-1 for INVALID); false when no source lists it.</summary>
    internal static bool TryGetParameterGroupId(int builtinId, out int groupId)
    {
        bool found = Parameters.Value.TryGetValue(builtinId, out var parameter);
        groupId = parameter.Group;
        return found;
    }

    internal static bool TryGetParameterGroupLabel(int builtinId, out string label)
    {
        label = "";
        return Parameters.Value.TryGetValue(builtinId, out var parameter) && TryGetGroupLabel(parameter.Group, out label);
    }

    /// <summary>The label of a BuiltInParameterGroup value (-1 gives "Invalid group"); false for a value the Revit 2022
    /// sources do not know (the current profile has two more groups, -5000232 and -5000233).</summary>
    internal static bool TryGetGroupLabel(int groupId, out string label)
    {
        if (groupId == InvalidGroupId)
        {
            label = InvalidGroup;
            return true;
        }
        bool found = Groups.Value.TryGetValue(groupId, out string? known);
        label = known ?? "";
        return found;
    }

    internal static string GroupLabel(int groupId) => TryGetGroupLabel(groupId, out string label) ? label : InvalidGroup;

    /// <summary>The English name of a builtin category (a negative BuiltInCategory value).</summary>
    internal static bool TryGetCategoryLabel(int builtinCategoryId, out string label)
    {
        label = EnglishCategoryNames.Get(builtinCategoryId) ?? "";
        return label.Length != 0;
    }

    private static IReadOnlyDictionary<int, (int, string)> LoadParameters()
    {
        var table = new Dictionary<int, (int, string)>();
        foreach (string[] row in Rows("compact_builtin_parameters.tsv", 3))
            table.Add(ParseInt(row[0], "compact_builtin_parameters.tsv"), (ParseInt(row[1], "compact_builtin_parameters.tsv"), row[2]));
        return table;
    }

    private static IReadOnlyDictionary<int, (string, string)> LoadOverrides()
    {
        var table = new Dictionary<int, (string, string)>();
        foreach (string[] row in Rows("compact_label_overrides.tsv", 4))
            table.Add(ParseInt(row[0], "compact_label_overrides.tsv"), (row[1], row[2]));
        return table;
    }

    private static IReadOnlyDictionary<int, string> LoadGroups()
    {
        var table = new Dictionary<int, string>();
        foreach (string[] row in Rows("compact_parameter_groups.tsv", 2))
            table.Add(ParseInt(row[0], "compact_parameter_groups.tsv"), row[1]);
        return table;
    }

    private static int ParseInt(string text, string resource) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidDataException($"Malformed number '{text}' in {resource}.");

    private static IEnumerable<string[]> Rows(string resource, int columns)
    {
        using var stream = typeof(CompactLabels).Assembly.GetManifestResourceStream("RvtIO.Resources." + resource)
            ?? throw new InvalidOperationException($"Missing resource {resource}.");
        using var reader = new StreamReader(stream);
        for (string? line; (line = reader.ReadLine()) != null;)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] fields = line.Split('\t');
            if (fields.Length != columns) throw new InvalidDataException($"Malformed line in {resource}: {line}");
            yield return fields;
        }
    }
}
