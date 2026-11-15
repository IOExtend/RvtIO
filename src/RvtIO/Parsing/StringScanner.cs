namespace RvtIO.Parsing;

internal static class StringScanner
{
    public static char DecodeUtf16Char(byte lo, byte hi)
    {
        if (hi == 0x04) return (char)(0x0400 | lo);
        return lo == 0x9B ? '0' : (char)lo;
    }

    public static string? NormalizeParamName(string? name) =>
        name == null ? null : (name.StartsWith('&') ? name[1..] : name);

    public static bool IsValidElementName(string s) =>
        IsValidElementName(s, allowDecimalPrefixedName: false);

    /// <summary>Validates a name read from an authoritative host-object type block.
    /// Unlike generic scans, an owned type block may legitimately contain authored names
    /// beginning with a dotted numeric type code (for example, "10.1_...").</summary>
    public static bool IsValidHostObjectTypeName(string s) =>
        IsValidElementName(s, allowDecimalPrefixedName: true);

    private static bool IsValidElementName(
        string s, bool allowDecimalPrefixedName)
    {
        // Element names can be longer than param names (e.g. complex wall type names of about 100 characters),
        // so we inline the checks from IsValidParamName with a higher length limit.
        if (s.Length < 4 || s.Length > 300) return false;
        if (s.StartsWith("autodesk.")) return false;
        if (s.Contains('\\') || s.Contains('/')) return false;
        if (s.StartsWith('(') && s.Contains(',')) return false;
        if (s.All(char.IsDigit)) return false;
        if (!allowDecimalPrefixedName
            && s.Length >= 10
            && char.IsDigit(s[0])
            && char.IsDigit(s[1])
            && s[2] == '.')
            return false;
        if (s.Length >= 36 && s.Count(c => c == '-') >= 4 && s.All(c => Uri.IsHexDigit(c) || c == '-')) return false;
        if (s.Length >= 10 && s.All(c => Uri.IsHexDigit(c) || c == '-')) return false;
        if (!s.Any(char.IsLetter)) return false;
        if (s.Length <= 6 && s.All(c => char.IsLower(c) || c == '_' || char.IsDigit(c))) return false;
        return true;
    }

    public static bool IsValidParamName(string s)
    {
        if (s.Length < 3 || s.Length > 100) return false;
        if (s.StartsWith("autodesk.")) return false;
        if (s.Contains('\\') || s.Contains('/')) return false;
        if (s.StartsWith('(') && s.Contains(',')) return false;
        if (s.All(char.IsDigit)) return false;
        if (s.Length >= 10 && char.IsDigit(s[0]) && char.IsDigit(s[1]) && s[2] == '.') return false;
        if (s.Length >= 36 && s.Count(c => c == '-') >= 4 && s.All(c => Uri.IsHexDigit(c) || c == '-')) return false;
        if (s.Length >= 10 && s.All(c => Uri.IsHexDigit(c) || c == '-')) return false;
        return s.Any(char.IsLetter);
    }

    /// <summary>Scan a chunk for element name strings near element IDs.</summary>
    public static void ScanChunk(byte[] d, HashSet<uint> elemSet, Dictionary<uint, List<string>> elemStrings)
    {
        // Each string is found once in a linear pass and attributed to the known ids at i in (j-600, j+100],
        // the positions whose window [i-100, i+600) contains the string at j. Scanning a window per id would
        // revalidate the same bytes many times, because id arrays pack known ids back to back.
        var seenPerEid = new Dictionary<uint, HashSet<string>>();
        for (int j = 0; j + 6 <= d.Length; j++)
        {
            uint sLen = BitConverter.ToUInt32(d, j);
            if (sLen < 3 || sLen > 200 || j + 4 + sLen * 2 > d.Length) continue;

            bool valid = true;
            int printable = 0;
            for (int k = 0; k < (int)sLen && valid; k++)
            {
                byte lo = d[j + 4 + k * 2], hi = d[j + 4 + k * 2 + 1];
                if (hi == 0x00 || hi == 0x04 || hi == 0xF7 || hi == 0x1B)
                {
                    if (hi == 0x00 && lo >= 0x20 && lo <= 0x7E) printable++;
                    else if (hi == 0x04) printable++;
                }
                else valid = false;
            }
            if (!valid || printable < (int)(sLen * 0.6)) continue;

            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < (int)sLen; k++)
            {
                char c = DecodeUtf16Char(d[j + 4 + k * 2], d[j + 4 + k * 2 + 1]);
                if (c < 0x20) { sb.Clear(); break; }
                sb.Append(c);
            }
            if (sb.Length < 3) continue;
            string s = sb.ToString();
            if (!IsValidParamName(s)) continue;

            int lo2 = Math.Max(0, j - 599);
            int hi2 = Math.Min(d.Length - 4, j + 100);
            for (int i = lo2; i <= hi2; i++)
            {
                uint eid = BitConverter.ToUInt32(d, i);
                if (eid == 0 || !elemSet.Contains(eid)) continue;
                if (!elemStrings.TryGetValue(eid, out var list))
                    elemStrings[eid] = list = new List<string>();
                if (!seenPerEid.TryGetValue(eid, out var seen))
                    seenPerEid[eid] = seen = new HashSet<string>();
                if (seen.Add(s)) list.Add(s);
            }
        }
    }
}
