using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;

namespace RvtIO.Parsing;

internal static partial class BasicFileInfoReader
{
    private const string CloudModelKey = "IsSingleUserCloudModel";
    public static Dictionary<string, string> Read(RootStorage root)
    {
        if (!root.TryOpenStream("BasicFileInfo", out var stream)) return new Dictionary<string, string>();
        using (stream)
        {
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return Parse(data);
        }
    }

    internal static Dictionary<string, string> Parse(byte[] data)
    {
        var result = new Dictionary<string, string>();
        if (data.Length < 12) return result;

        int version = BitConverter.ToInt32(data, 0);
        result["StreamVersion"] = version.ToString();
        for (int pos = 4; pos + 4 <= data.Length;)
        {
            int charCount = BitConverter.ToInt32(data, pos);
            if (charCount > 0 && charCount < 2000 && pos + 4 + charCount * 2 <= data.Length
                && data[pos + 5] == 0 && data[pos + 4] >= 0x20)
            {
                string val = Encoding.Unicode.GetString(data, pos + 4, charCount * 2);
                if (val.Contains(":\\") || val.Contains(":/") || val.StartsWith("\\\\", StringComparison.Ordinal))
                    result.TryAdd("LastSavePath", val);
                else if (val.Length == 4 && val.All(char.IsDigit))
                    result.TryAdd("RevitVersion", val);
                else if (val.Length > 8 && (val.Contains("(x64)") || val.Contains("(x86)")))
                    result.TryAdd("Build", val);
                pos += 4 + charCount * 2;
            }
            else
            {
                pos++;
            }
        }
        for (int parity = 0; parity < 2; parity++)
        {
            int runStart = -1;
            for (int i = parity; i < data.Length - 1; i += 2)
            {
                char ch = (char)(data[i] | (data[i + 1] << 8));
                if (char.IsHighSurrogate(ch) && i + 3 < data.Length
                    && char.IsLowSurrogate((char)(data[i + 2] | (data[i + 3] << 8))))
                {
                    if (runStart < 0) runStart = i;
                    i += 2;
                    continue;
                }
                bool isText = ch is '\t' or '\r' or '\n'
                    || (ch >= 0x20 && ch < 0xFFFE && !char.IsControl(ch) && !char.IsSurrogate(ch));
                if (isText) { if (runStart < 0) runStart = i; }
                else
                {
                    if (runStart >= 0 && i - runStart >= 8)
                        ParseTextRun(data, runStart, i, result);
                    runStart = -1;
                }
            }
            if (runStart >= 0 && data.Length - 1 - runStart >= 8)
                ParseTextRun(data, runStart, data.Length - ((data.Length - runStart) & 1), result);
        }
        ReadCloudModel(data, result);
        if (result.TryGetValue("Revit Build", out string? labelledBuild)) result.TryAdd("Build", labelledBuild);
        if (result.TryGetValue("Last Save Path", out string? labelledPath)) result.TryAdd("LastSavePath", labelledPath);
        if (result.TryGetValue("Format", out string? format)
            && format.Length == 4 && format.All(char.IsDigit))
            result["RevitVersion"] = format;
        if (!result.ContainsKey("RevitVersion") && ReleaseOfBuild(result) is { } release)
            result["RevitVersion"] = release;
        return result;
    }

    private static void ParseTextRun(byte[] data, int runStart, int runEnd, Dictionary<string, string> result)
    {
        string run = Encoding.Unicode.GetString(data, runStart, runEnd - runStart);
        bool anyKv = false;
        foreach (var lineRaw in run.Split('\r', '\n'))
        {
            string line = lineRaw.Trim();
            int ci = line.IndexOf(":\t", StringComparison.Ordinal);
            if (ci < 0) ci = line.IndexOf(": ", StringComparison.Ordinal);
            if (ci > 0 && ci < 60)
            {
                string key = line[..ci].Trim();
                string val = line[(ci + 2)..].Trim();
                if (IsValidFieldKey(key) && val.Length > 0)
                {
                    if (key == CloudModelKey && !val.Equals("True", StringComparison.Ordinal) && !val.Equals("False", StringComparison.Ordinal))
                        continue;
                    result.TryAdd(key, val);
                    anyKv = true;
                }
            }
        }
        if (!anyKv && run.Contains("Revit", StringComparison.Ordinal) && run.All(c => c < 0x80) && !result.ContainsKey("FullBuild"))
            result["FullBuild"] = run.Trim();
    }

    private static bool IsValidFieldKey(string key)
    {
        if (key.Length == 0) return false;
        bool hasLetter = false;
        foreach (char c in key)
        {
            if (c > 0x7E || c < 0x20) return false;
            if (char.IsLetter(c)) { hasLetter = true; continue; }
            if (char.IsDigit(c) || c is ' ' or '\'' or '.' or '_' or '(' or ')' or '-') continue;
            return false;
        }
        return hasLetter;
    }
    private static void ReadCloudModel(byte[] data, Dictionary<string, string> result)
    {
        var key = Encoding.Unicode.GetBytes(CloudModelKey + ": ");
        int at = data.AsSpan().IndexOf(key);
        if (at < 0) return;
        var value = data.AsSpan(at + key.Length);
        foreach (string text in new[] { "True", "False" })
            if (value.StartsWith(Encoding.ASCII.GetBytes(text + "\0")) || (value.StartsWith(Encoding.Unicode.GetBytes(text))
                && value.Length >= 2 * text.Length + 2 && value[2 * text.Length] is 0x0D or 0x0A))
                result[CloudModelKey] = text;
    }
    [GeneratedRegex(@"(?<!\d)((?:200[89]|20[1-3]\d))(?!\d)\s*\(Build:")]
    private static partial Regex YearBeforeBuild();
    private static string? ReleaseOfBuild(Dictionary<string, string> fields)
    {
        foreach (string key in new[] { "Build", "Revit Build", "FullBuild" })
        {
            if (!fields.TryGetValue(key, out string? text)) continue;
            var written = YearBeforeBuild().Match(text);
            if (written.Success) return written.Groups[1].Value;
        }
        return null;
    }
}
