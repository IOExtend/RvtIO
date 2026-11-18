using RvtIO.Lookup;
using static RvtIO.Lookup.ParameterGroupLabels;

namespace RvtIO.Compact;

/// <summary>The English labels of the compact JSON export: parameter captions, parameter group names, category names.</summary>
internal static class CompactLabels
{
    private static readonly Lazy<IReadOnlyDictionary<int, (int Group, string Label)>> Parameters = new(LoadParameters);
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
}
