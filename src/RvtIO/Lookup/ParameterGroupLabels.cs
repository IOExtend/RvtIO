using System.Globalization;

namespace RvtIO.Lookup;

internal static class ParameterGroupLabels
{
    internal const string InvalidGroup = "Invalid group";

    /// <summary>BuiltInParameterGroup.INVALID.</summary>
    internal const int InvalidGroupId = -1;

    private static readonly Lazy<IReadOnlyDictionary<int, string>> Groups = new(LoadGroups);

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

    private static IReadOnlyDictionary<int, string> LoadGroups()
    {
        var table = new Dictionary<int, string>();
        foreach (string[] row in Rows("compact_parameter_groups.tsv", 2))
            table.Add(ParseInt(row[0], "compact_parameter_groups.tsv"), row[1]);
        return table;
    }

    internal static int ParseInt(string text, string resource) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidDataException($"Malformed number '{text}' in {resource}.");

    internal static IEnumerable<string[]> Rows(string resource, int columns)
    {
        using var stream = typeof(ParameterGroupLabels).Assembly.GetManifestResourceStream("RvtIO.Resources." + resource)
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
