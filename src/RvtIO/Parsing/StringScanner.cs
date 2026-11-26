using System.Runtime.InteropServices;

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

    public static bool IsValidParamName(string s) => IsValidParamName(s.AsSpan());

    private static bool IsValidParamName(ReadOnlySpan<char> s)
    {
        if (s.Length < 3 || s.Length > 100) return false;
        if (s.IndexOfAny('\\', '/') >= 0) return false;
        if (s[0] == '(' && s.Contains(',')) return false;
        if (s.Length >= 10 && char.IsDigit(s[0]) && char.IsDigit(s[1]) && s[2] == '.') return false;
        bool hasLetter = false;
        bool couldBeHexId = s.Length >= 10;
        foreach (char c in s)
        {
            hasLetter |= char.IsLetter(c);
            if (couldBeHexId) couldBeHexId = Uri.IsHexDigit(c) || c == '-';
            if (hasLetter && !couldBeHexId)
                return !s.StartsWith("autodesk.", StringComparison.CurrentCulture);
        }
        return false;
    }

    private static ReadOnlySpan<byte> ThreeZeros => [0, 0, 0];

    // After the first valid name, only these strings can affect the Forge ID/caption passes.
    internal static bool UsedForParameterCaption(string value) => value.StartsWith('&')
        || (value.Length <= 80 && value.Contains(" - ")) || value.StartsWith("autodesk.parameter.");

    /// <summary>Scan a chunk for element name strings near element IDs.</summary>
    public static void ScanChunk(byte[] d, HashSet<uint> elemSet, Dictionary<uint, List<string>> elemStrings,
        bool namesAndCaptionsOnly = false)
    {
        // Each string is found once in a linear pass and attributed to the known ids at i in (j-600, j+100],
        // the positions whose window [i-100, i+600) contains the string at j. Scanning a window per id would
        // revalidate the same bytes many times, because id arrays pack known ids back to back.
        // Note: IsValidParamName rejects sLen > 100, so only length prefixes in [3, 100] can yield a string.
        // In little-endian uint32, sLen in [3, 100] always has bytes [j+1..j+3] == 0x00, 0x00, 0x00.
        if (d.Length < 10) return;

        var seenPerEid = new Dictionary<uint, HashSet<string>>();
        var nearbyIds = new Queue<(int Offset, uint Id)>();
        int lastIdOffset = -1;
        Span<char> text = stackalloc char[100];
        ReadOnlySpan<byte> span = d;

        int searchFrom = 1;
        int searchEnd = d.Length - 6;
        while (searchFrom + 3 <= searchEnd)
        {
            int rel = span.Slice(searchFrom, searchEnd - searchFrom).IndexOf(ThreeZeros);
            if (rel < 0) break;

            int zeroPos = searchFrom + rel;
            int j = zeroPos - 1;
            searchFrom = zeroPos + 1;

            uint sLen = d[j];
            int payloadBytes = (int)sLen * 2;
            if (sLen < 3 || sLen > 100 || j + 4 + payloadBytes > d.Length) continue;

            int payloadStart = j + 4;
            int minPrintable = (int)(sLen * 0.6);
            int printable = 0;
            bool hasLetter = false;
            bool couldBeHexId = sLen >= 10;
            bool rejected = false;
            bool startsWithParen = false;

            for (int k = 0; k < (int)sLen; k++)
            {
                int offset = payloadStart + k * 2;
                byte lo = d[offset];
                byte hi = d[offset + 1];
                if (hi is not (0x00 or 0x04 or 0xF7 or 0x1B))
                {
                    rejected = true;
                    break;
                }
                if (hi == 0x04 || hi == 0x00 && lo is >= 0x20 and <= 0x7E) printable++;
                char c = DecodeUtf16Char(lo, hi);
                if (c < ' ' || c == '\\' || c == '/') { rejected = true; break; }
                if (k == 0) startsWithParen = c == '(';
                else if (startsWithParen && c == ',') { rejected = true; break; }

                text[k] = c;
                hasLetter |= char.IsLetter(c);
                if (couldBeHexId) couldBeHexId = Uri.IsHexDigit(c) || c == '-';
            }

            if (rejected || printable < minPrintable || !hasLetter || couldBeHexId) continue;
            if (sLen >= 10 && char.IsDigit(text[0]) && char.IsDigit(text[1]) && text[2] == '.') continue;
            var candidate = text[..(int)sLen];
            // Cultural collation can ignore even printable ASCII characters.
            if (candidate.StartsWith("autodesk.", StringComparison.CurrentCulture)) continue;

            int lo2 = Math.Max(0, j - 599);
            int hi2 = Math.Min(d.Length - 4, j + 100);
            // Nearby captions have overlapping ID windows. Only scan the newly entered offsets.
            while (nearbyIds.TryPeek(out var oldest) && oldest.Offset < lo2) nearbyIds.Dequeue();
            for (int i = Math.Max(lo2, lastIdOffset + 1); i <= hi2; i++)
            {
                uint eid = BitConverter.ToUInt32(d, i);
                if (eid != 0 && elemSet.Contains(eid)) nearbyIds.Enqueue((i, eid));
            }
            lastIdOffset = hi2;
            if (nearbyIds.Count == 0) continue;

            string s = new(candidate);
            bool firstOnly = namesAndCaptionsOnly && !UsedForParameterCaption(s);
            foreach (var (_, eid) in nearbyIds)
            {
                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(elemStrings, eid, out bool exists);
                if (!exists)
                {
                    list = new List<string>(4) { s };
                    continue;
                }
                if (firstOnly) continue;

                var items = list!;
                if (items.Count > 12 && seenPerEid.TryGetValue(eid, out var seen))
                {
                    if (seen.Add(s)) items.Add(s);
                    continue;
                }
                bool dup = false;
                for (int idx = 0; idx < items.Count; idx++)
                {
                    if (ReferenceEquals(items[idx], s) || string.Equals(items[idx], s, StringComparison.Ordinal))
                    {
                        dup = true;
                        break;
                    }
                }
                if (dup) continue;
                if (items.Count >= 12)
                {
                    seen = new HashSet<string>(items.Count + 4, StringComparer.Ordinal);
                    for (int idx = 0; idx < items.Count; idx++) seen.Add(items[idx]);
                    seen.Add(s);
                    seenPerEid[eid] = seen;
                }
                items.Add(s);
            }
        }
    }
}
