using System.Globalization;
using System.Text;

namespace RvtIO.Parsing;

/// <summary>
/// Reads the plain text of an RTF string as Revit stores it for a text note (\rtf1\ansi\ansicp1251...).
/// Destinations (font and color tables, generator, pictures) are skipped; \par and \line become newlines, \tab a tan,
/// and the text bytes are decoded first as UTF-8 and, when that fails, with Windows-1251 (the code page a Russian file
/// declares) or Windows-1252.
/// </summary>
internal static class RtfText
{
    /// <summary>Parses an RTF string.</summary>
    /// <param name="data">The bytes that hold the RTF.</param>
    /// <param name="start">The offset of the opening brace.</param>
    /// <param name="end">The offset one past the last byte to read.</param>
    /// <returns>The plain text, the font size in points when the RTF declares one, or null when the bytes are not an
    /// RTF string.</returns>
    public static (string Text, double? FontSizePoints)? Parse(byte[] data, int start, int end)
    {
        if (start < 0 || end > data.Length || start + 6 > end) return null;
        if (data[start] != (byte)'{' || data[start + 1] != (byte)'\\') return null;
        if (data[start + 2] != (byte)'r' || data[start + 3] != (byte)'t' || data[start + 4] != (byte)'f'
            || data[start + 5] != (byte)'1') return null;

        var text = new List<byte>();
        double? fontSize = null;
        int codePage = 1251;
        int depth = 0;
        int skipFrom = -1;
        int i = start;
        while (i < end)
        {
            byte b = data[i];
            if (b == (byte)'{')
            {
                depth++;
                int word = i + 1;
                if (word < end && data[word] == (byte)'\\' && word + 1 < end && data[word + 1] == (byte)'*')
                {
                    if (skipFrom < 0) skipFrom = depth;
                    i += 2;
                    continue;
                }
                if (word < end && data[word] == (byte)'\\')
                {
                    if (IsDestination(ControlWordName(data, word + 1, end)) && skipFrom < 0) skipFrom = depth;
                }
                i++;
                continue;
            }
            if (b == (byte)'}')
            {
                if (skipFrom >= 0 && skipFrom == depth) skipFrom = -1;
                depth = Math.Max(0, depth - 1);
                i++;
                if (depth == 0) break;
                continue;
            }
            if (b == (byte)'\\')
            {
                if (i + 1 >= end) { i++; continue; }
                byte next = data[i + 1];
                if (next is (byte)'*' or (byte)'{' or (byte)'}' or (byte)'\\')
                {
                    if (skipFrom < 0 && next != (byte)'*') text.Add(next);
                    i += 2;
                    continue;
                }
                if (next == (byte)'\'')
                {
                    if (i + 3 < end && IsHex(data[i + 2]) && IsHex(data[i + 3]))
                    {
                        if (skipFrom < 0)
                            text.Add((byte)Convert.ToInt32(((char)data[i + 2]).ToString()
                                + (char)data[i + 3], 16));
                        i += 4;
                        continue;
                    }
                    i += 2;
                    continue;
                }
                if (next == (byte)'u' && i + 2 < end && (data[i + 2] == (byte)'-' || IsDigit(data[i + 2])))
                {
                    int at = i + 2;
                    int sign = 1;
                    if (data[at] == (byte)'-') { sign = -1; at++; }
                    int code = 0;
                    while (at < end && IsDigit(data[at])) { code = code * 10 + (data[at] - '0'); at++; }
                    code *= sign;
                    if (code < 0) code += 65536;
                    if (skipFrom < 0 && code is > 0 and <= 0xFFFF)
                        text.AddRange(Encoding.UTF8.GetBytes(((char)code).ToString()));
                    if (at < end && data[at] == (byte)' ') at++;
                    if (at < end && data[at] != (byte)'\\' && data[at] != (byte)'}') at++;
                    i = at;
                    continue;
                }
                int wordStart = i + 1;
                int wordEnd = wordStart;
                while (wordEnd < end && char.IsAsciiLetter((char)data[wordEnd])) wordEnd++;
                string word2 = Encoding.ASCII.GetString(data, wordStart, wordEnd - wordStart);
                int number = int.MinValue;
                int numberEnd = wordEnd;
                if (numberEnd < end && data[numberEnd] == (byte)'-') numberEnd++;
                while (numberEnd < end && IsDigit(data[numberEnd])) numberEnd++;
                if (numberEnd > wordEnd) int.TryParse(Encoding.ASCII.GetString(data, wordEnd, numberEnd - wordEnd),
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
                if (numberEnd < end && data[numberEnd] == (byte)' ') numberEnd++;
                if (skipFrom < 0)
                {
                    switch (word2)
                    {
                        case "par" or "line": text.Add((byte)'\n'); break;
                        case "tab": text.Add((byte)'\t'); break;
                        case "ansicp" when number > 0: codePage = number; break;
                        case "fs" when number > 0: fontSize = number / 2.0; break;
                    }
                }
                i = numberEnd;
                continue;
            }
            if (skipFrom < 0 && (b >= 0x20 || b == (byte)'\t')) text.Add(b);
            i++;
        }

        string plain = Decode(text, codePage).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        return (plain, fontSize);
    }

    /// <summary>Decodes the text bytes: strict UTF-8 first (Revit writes UTF-8 text inside the RTF of many files), else
    /// the declared code page.</summary>
    private static string Decode(List<byte> bytes, int codePage)
    {
        var array = bytes.ToArray();
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(array);
        }
        catch (DecoderFallbackException)
        {
            var builder = new StringBuilder(array.Length);
            foreach (byte b in array) builder.Append(DecodeByte(b, codePage));
            return builder.ToString();
        }
    }

    private static bool IsDestination(string name) => name is "fonttbl" or "colortbl" or "stylesheet" or "info"
        or "pict" or "object" or "header" or "footer" or "field" or "generator" or "listtable" or "listoverridetable"
        or "rsidtbl" or "xmlnstbl" or "themedata" or "colorschememapping" or "datastore" or "latentstyles"
        or "filetbl" or "revtbl" or "pn" or "fldinst" or "fldrslt" or "bkmkstart" or "bkmkend";

    private static string ControlWordName(byte[] data, int start, int end)
    {
        int at = start;
        while (at < end && char.IsAsciiLetter((char)data[at])) at++;
        return Encoding.ASCII.GetString(data, start, at - start);
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static bool IsHex(byte b) => IsDigit(b) || b is >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    /// <summary>A single byte of the fallback code page: Windows-1251 for a Russian file, Windows-1252 otherwise.</summary>
    private static char DecodeByte(int value, int codePage)
    {
        if (value < 0x80) return (char)value;
        if (codePage == 1251)
        {
            if (value == 0xA8) return '\u0401';
            if (value == 0xB8) return '\u0451';
            if (value >= 0xC0) return (char)(0x0410 + (value - 0xC0));
            return value switch
            {
                0x80 => '\u0402', 0x81 => '\u0403', 0x82 => '\u201A', 0x83 => '\u0453', 0x84 => '\u201E',
                0x85 => '\u2026', 0x86 => '\u2020', 0x87 => '\u2021', 0x88 => '\u20AC', 0x89 => '\u2030',
                0x8A => '\u0409', 0x8B => '\u2039', 0x8C => '\u040A', 0x8D => '\u040C', 0x8E => '\u040B',
                0x8F => '\u040F', 0x90 => '\u0452', 0x91 => '\u2018', 0x92 => '\u2019', 0x93 => '\u201C',
                0x94 => '\u201D', 0x95 => '\u2022', 0x96 => '\u2013', 0x97 => '\u2014', 0x99 => '\u2122',
                0x9A => '\u0459', 0x9B => '\u203A', 0x9C => '\u045A', 0x9D => '\u045C', 0x9E => '\u045B',
                0x9F => '\u045F', 0xA0 => '\u00A0', 0xA1 => '\u040E', 0xA2 => '\u045E', 0xA3 => '\u0408',
                0xA4 => '\u00A4', 0xA5 => '\u0490', 0xA6 => '\u00A6', 0xA7 => '\u00A7', 0xA9 => '\u00A9',
                0xAA => '\u0404', 0xAB => '\u00AB', 0xAC => '\u00AC', 0xAD => '\u00AD', 0xAE => '\u00AE',
                0xAF => '\u0407', 0xB0 => '\u00B0', 0xB1 => '\u00B1', 0xB2 => '\u0406', 0xB3 => '\u0456',
                0xB4 => '\u0491', 0xB5 => '\u00B5', 0xB6 => '\u00B6', 0xB7 => '\u00B7', 0xB9 => '\u2116',
                0xBA => '\u0454', 0xBB => '\u00BB', 0xBC => '\u0458', 0xBD => '\u0405', 0xBE => '\u0455',
                0xBF => '\u0457',
                _ => '\uFFFD',
            };
        }
        return value switch
        {
            0x80 => '\u20AC', 0x82 => '\u201A', 0x83 => '\u0192', 0x84 => '\u201E', 0x85 => '\u2026',
            0x86 => '\u2020', 0x87 => '\u2021', 0x88 => '\u02C6', 0x89 => '\u2030', 0x8A => '\u0160',
            0x8B => '\u2039', 0x8C => '\u0152', 0x8E => '\u017D', 0x91 => '\u2018', 0x92 => '\u2019',
            0x93 => '\u201C', 0x94 => '\u201D', 0x95 => '\u2022', 0x96 => '\u2013', 0x97 => '\u2014',
            0x98 => '\u02DC', 0x99 => '\u2122', 0x9A => '\u0161', 0x9B => '\u203A', 0x9C => '\u0153',
            0x9E => '\u017E', 0x9F => '\u0178',
            _ => (char)value,
        };
    }
}
