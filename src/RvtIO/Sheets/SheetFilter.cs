using RvtIO.BuiltIns;
using RvtIO.Model;

namespace RvtIO.Sheets;

/// <summary>
/// A <c>--where</c> filter over sheets, ported from rvt-prt's sheet-parameter filters:
/// <c>NAME=VALUE</c> (<c>*</c> and <c>?</c> wildcards), <c>NAME!=VALUE</c>, <c>NAME~TEXT</c> (contains).
/// Names and values compare case-insensitively; on a Yes/No parameter (<c>Appears In Sheet List</c> or
/// one of <see cref="SheetRecord.YesNoParameters"/>) the value reads as <c>Yes</c>/<c>No</c> and the
/// <c>true</c>/<c>false</c>/<c>1</c>/<c>0</c> spellings match it; every other field compares literally.
/// A sheet without the parameter has an empty value.
/// </summary>
public sealed class SheetFilter
{
    private SheetFilter(string name, Operator op, string value)
    {
        Name = name;
        Op = op;
        Value = value;
    }

    internal enum Operator { Equals, NotEquals, Contains }

    internal string Name { get; }
    internal Operator Op { get; }
    internal string Value { get; }

    /// <summary>Parse one <c>--where</c> filter. Returns null with an error when the text is not a filter.</summary>
    public static SheetFilter? TryParse(string text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = $"Bad filter '{text}': expected NAME=VALUE, NAME!=VALUE or NAME~TEXT.";
            return null;
        }
        // Order matters: != before =.
        int notEquals = text.IndexOf("!=", StringComparison.Ordinal);
        int contains = text.IndexOf('~');
        int equals = text.IndexOf('=');
        string name, value;
        Operator op;
        if (notEquals >= 0 && (contains < 0 || notEquals < contains) && (equals < 0 || notEquals < equals))
        {
            name = text[..notEquals];
            value = text[(notEquals + 2)..];
            op = Operator.NotEquals;
        }
        else if (contains >= 0 && (equals < 0 || contains < equals))
        {
            name = text[..contains];
            value = text[(contains + 1)..];
            op = Operator.Contains;
        }
        else if (equals >= 0)
        {
            name = text[..equals];
            value = text[(equals + 1)..];
            op = Operator.Equals;
        }
        else
        {
            error = $"Bad filter '{text}': expected NAME=VALUE, NAME!=VALUE or NAME~TEXT.";
            return null;
        }
        name = name.Trim();
        value = value.Trim();
        if (name.Length == 0)
        {
            error = $"Bad filter '{text}': the parameter name is empty.";
            return null;
        }
        return new SheetFilter(name, op, value);
    }

    /// <summary>True when the sheet satisfies this filter.</summary>
    public bool Matches(SheetRecord sheet)
    {
        string actual = Lookup(sheet, Name) ?? string.Empty;
        return Op switch
        {
            Operator.Contains => actual.Contains(Value, StringComparison.OrdinalIgnoreCase),
            Operator.Equals => IsYesNoParameter(sheet, Name)
                ? WildcardEquals(actual, Value)
                : WildcardLiteralEquals(actual, Value),
            Operator.NotEquals => IsYesNoParameter(sheet, Name)
                ? !WildcardEquals(actual, Value)
                : !WildcardLiteralEquals(actual, Value),
            _ => false,
        };
    }

    /// <summary>True when the named field is a Yes/No parameter: <c>Appears In Sheet List</c> or one of
    /// <see cref="SheetRecord.YesNoParameters"/>. Only for these do the <c>true</c>/<c>false</c>/<c>1</c>/<c>0</c>
    /// spellings match <c>Yes</c>/<c>No</c>; every other field compares literally. The name compares
    /// case-insensitively even when the record was built with a case-sensitive collection.</summary>
    internal static bool IsYesNoParameter(SheetRecord sheet, string name) =>
        string.Equals(name, DisplayName(BuiltInParameter.SHEET_SCHEDULED), StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, nameof(BuiltInParameter.SHEET_SCHEDULED), StringComparison.OrdinalIgnoreCase)
        || sheet.YesNoParameters.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The value of a named sheet field or parameter, or null when the sheet has no such name.</summary>
    internal static string? Lookup(SheetRecord sheet, string name)
    {
        // Core fields by Revit display name and by BIP enum name.
        foreach (var (display, bip, get) in CoreFields(sheet))
        {
            if (string.Equals(name, display, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, bip, StringComparison.OrdinalIgnoreCase))
                return get();
        }
        foreach (var (key, value) in sheet.Parameters)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    /// <summary>True when every filter names a field the sheet exposes.</summary>
    public static bool TryValidateNames(IEnumerable<SheetFilter> filters, SheetRecord sheet, out string? unknown)
    {
        foreach (var filter in filters)
        {
            if (Lookup(sheet, filter.Name) == null)
            {
                unknown = filter.Name;
                return false;
            }
        }
        unknown = null;
        return true;
    }

    private static IEnumerable<(string Display, string Bip, Func<string?> Get)> CoreFields(SheetRecord sheet)
    {
        yield return ("Sheet Number", nameof(BuiltInParameter.SHEET_NUMBER), () => sheet.Number);
        yield return ("Sheet Name", nameof(BuiltInParameter.SHEET_NAME), () => sheet.Name);
        yield return ("Sheet Issue Date", nameof(BuiltInParameter.SHEET_ISSUE_DATE), () => sheet.IssueDate);
        yield return ("View Scale", nameof(BuiltInParameter.VIEW_SCALE), () => sheet.Scale?.ToString());
        yield return ("Drawn By", nameof(BuiltInParameter.SHEET_DRAWN_BY), () => sheet.DrawnBy);
        yield return ("Designed By", nameof(BuiltInParameter.SHEET_DESIGNED_BY), () => sheet.DesignedBy);
        yield return ("Checked By", nameof(BuiltInParameter.SHEET_CHECKED_BY), () => sheet.CheckedBy);
        yield return ("Approved By", nameof(BuiltInParameter.SHEET_APPROVED_BY), () => sheet.ApprovedBy);
        if (sheet.Parameters.TryGetValue(DisplayName(BuiltInParameter.SHEET_SCHEDULED), out var scheduled))
            yield return ("Appears In Sheet List", nameof(BuiltInParameter.SHEET_SCHEDULED), () => scheduled);
    }

    internal static string DisplayName(BuiltInParameter bip) => bip switch
    {
        BuiltInParameter.SHEET_NUMBER => "Sheet Number",
        BuiltInParameter.SHEET_NAME => "Sheet Name",
        BuiltInParameter.SHEET_ISSUE_DATE => "Sheet Issue Date",
        BuiltInParameter.VIEW_SCALE => "View Scale",
        BuiltInParameter.SHEET_DRAWN_BY => "Drawn By",
        BuiltInParameter.SHEET_DESIGNED_BY => "Designed By",
        BuiltInParameter.SHEET_CHECKED_BY => "Checked By",
        BuiltInParameter.SHEET_APPROVED_BY => "Approved By",
        BuiltInParameter.SHEET_SCHEDULED => "Appears In Sheet List",
        _ => bip.ToString(),
    };

    /// <summary>Normalize a Yes/No spelling to <c>Yes</c>/<c>No</c>; null when the text is neither.</summary>
    internal static string? NormalizeYesNo(string text) => text.ToUpperInvariant() switch
    {
        "YES" or "TRUE" or "1" => "Yes",
        "NO" or "FALSE" or "0" => "No",
        _ => null,
    };

    internal static bool WildcardEquals(string actual, string pattern)
    {
        string? normalizedPattern = NormalizeYesNo(pattern);
        string? normalizedActual = NormalizeYesNo(actual);
        if (normalizedPattern != null && normalizedActual != null)
            return string.Equals(normalizedActual, normalizedPattern, StringComparison.OrdinalIgnoreCase);
        return WildcardLiteralEquals(actual, pattern);
    }

    /// <summary>Wildcard comparison without Yes/No normalization: an empty pattern matches only an
    /// empty value (a sheet without the parameter).</summary>
    internal static bool WildcardLiteralEquals(string actual, string pattern)
    {
        if (pattern.Length == 0)
            return actual.Length == 0;
        return WildcardMatch(actual, pattern);
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        int ti = 0, pi = 0, star = -1, mark = 0;
        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' ||
                char.ToUpperInvariant(pattern[pi]) == char.ToUpperInvariant(text[ti])))
            {
                ti++;
                pi++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                star = pi++;
                mark = ti;
            }
            else if (star >= 0)
            {
                pi = star + 1;
                ti = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }
}
