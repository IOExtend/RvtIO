namespace RvtIO.Parsing;

internal static class ProjectLengthUnits
{
    internal const string LengthSpecName = "autodesk.spec.aec:length";

    /// <summary>The unit id of the single Length option, null when it is missing, ambiguous or the library default.
    /// <paramref name="issue"/> says why the option is not usable (null when it is).</summary>
    internal static string? StoredLengthUnit(TfBlockDeserializer.UnitOptions? options, out string? issue)
    {
        issue = "the document unit options were not read";
        if (options == null) return null;
        var matches = options.Options.Where(option =>
            UnitSchemaEvaluator.DisplayNameKey(option.Spec) == LengthSpecName).ToArray();
        if (matches.Length != 1) issue = matches.Length == 0 ? "no Length format option" : "several Length format options";
        else if (matches[0].UseDefault != 0) issue = "the Length format option follows the library default";
        else if (string.IsNullOrEmpty(matches[0].Unit)) issue = "the Length format option has no unit";
        else
        {
            issue = null;
            return matches[0].Unit;
        }
        return null;
    }
}
