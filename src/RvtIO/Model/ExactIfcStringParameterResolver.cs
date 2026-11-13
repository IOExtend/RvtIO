namespace RvtIO.Model;

/// <summary>The three outcomes needed to reproduce an IFC named-string lookup
/// without silently substituting a lower-priority fallback when the decoded RVT data is
/// insufficient to reproduce Revit's displayed value.</summary>
internal enum ExactIfcStringParameterState
{
    Absent,
    Value,
    Blocked,
}

internal readonly record struct ExactIfcStringParameterResult(
    ExactIfcStringParameterState State,
    string? Value = null)
{
    internal static ExactIfcStringParameterResult Absent =>
        new(ExactIfcStringParameterState.Absent);

    internal static ExactIfcStringParameterResult Blocked =>
        new(ExactIfcStringParameterState.Blocked);

    internal static ExactIfcStringParameterResult FromValue(string value) =>
        new(ExactIfcStringParameterState.Value, value);
}

/// <summary>Reproduces the deterministic part of the named string
/// parameter lookup of the Revit IFC exporter. Parameter names are compared after removing ASCII spaces with
/// invariant-culture case folding. Because this reader does not yet expose Revit parameter
/// groups or document display formatting, an ambiguous duplicate or a value whose
/// AsValueString result cannot be validated is reported as <see
/// cref="ExactIfcStringParameterState.Blocked"/> instead of allowing a wrong fallback.</summary>
internal static class ExactIfcStringParameterResolver
{
    internal static ExactIfcStringParameterResult Resolve(
        Element element, string propertyName)
    {
        string cleanPropertyName = RemoveAsciiSpaces(propertyName);
        var candidates = element.Parameters.Where(parameter =>
            parameter.Definition?.Name is string name
            && string.Equals(
                RemoveAsciiSpaces(name), cleanPropertyName,
                StringComparison.InvariantCultureIgnoreCase)).ToList();

        if (candidates.Count == 0)
            return ExactIfcStringParameterResult.Absent;

        var exactValues = new HashSet<string>(StringComparer.Ordinal);
        bool hasEmptyOrUnsetCandidate = false;
        foreach (Parameter parameter in candidates)
        {
            if (!parameter.CarriesRevitValue())
            {
                hasEmptyOrUnsetCandidate = true;
                continue;
            }

            // Revit calls AsValueString for every storage type. Its numeric and ElementId
            // formatting depends on document units/localization and referenced element
            // state that are not represented here, so accepting Value.ToString would be a
            // guess. A String value is byte-for-byte reproducible unless it is an IFC
            // parameter expression, which Revit evaluates against live document state.
            if (parameter.StorageType != StorageType.String
                || parameter.Value is not string stringValue)
                return ExactIfcStringParameterResult.Blocked;

            if (stringValue.Length == 0)
            {
                hasEmptyOrUnsetCandidate = true;
                continue;
            }

            if (IsParameterExpression(stringValue))
                return ExactIfcStringParameterResult.Blocked;

            exactValues.Add(stringValue);
        }

        if (exactValues.Count == 0)
            return ExactIfcStringParameterResult.Absent;

        // Revit selects by PG_IFC first, then parameter group, then parameter id. Those
        // groups are not decoded yet. Equal non-empty values are safe under every possible
        // ordering; differing values, or a mix of empty and valued candidates, are not.
        if (exactValues.Count != 1 || hasEmptyOrUnsetCandidate)
            return ExactIfcStringParameterResult.Blocked;

        return ExactIfcStringParameterResult.FromValue(exactValues.Single());
    }

    /// <summary>Reference precedence: instance Reference, type Reference,
    /// then type Reference[Type]. The caller owns the final type-name fallback.</summary>
    internal static ExactIfcStringParameterResult ResolveReference(
        Element element, Element? elementType)
    {
        ExactIfcStringParameterResult result = Resolve(element, "Reference");
        if (result.State != ExactIfcStringParameterState.Absent)
            return result;

        if (elementType == null)
            return ExactIfcStringParameterResult.Absent;

        result = Resolve(elementType, "Reference");
        return result.State != ExactIfcStringParameterState.Absent
            ? result
            : Resolve(elementType, "Reference[Type]");
    }

    private static string RemoveAsciiSpaces(string value) => value.Replace(" ", null);

    private static bool IsParameterExpression(string value)
    {
        string trimmed = value.Trim();
        return ((trimmed.Length > 1 && trimmed[0] == '{')
                || (trimmed.Length > 2 && trimmed[1] == '{'))
            && trimmed[^1] == '}';
    }
}
