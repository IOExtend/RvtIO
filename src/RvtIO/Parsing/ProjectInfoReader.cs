using System.IO.Compression;
using OpenMcdf;

namespace RvtIO.Parsing;

internal static class ProjectInfoReader
{
    public static Dictionary<string, string> Read(RootStorage root)
    {
        try
        {
            using var stream = root.OpenStream("ProjectInformation");
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return Parse(data);
        }
        // Unreadable or malformed project information: keep what was parsed.
        catch (Exception ex) when (MalformedInput.Is(ex)) { return new Dictionary<string, string>(); }
    }

    internal static Dictionary<string, string> Parse(byte[] data)
    {
        var result = new Dictionary<string, string>();
        try
        {
            int zipStart = -1;
            for (int i = 0; i < data.Length - 3; i++)
                if (data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 0x03 && data[i + 3] == 0x04)
                { zipStart = i; break; }
            if (zipStart < 0) return result;

            using var zipStream = new MemoryStream(data, zipStart, data.Length - zipStart);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (!entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                using var sr = new StreamReader(entry.Open());
                ExtractAtomFields(sr.ReadToEnd(), result);
            }
        }
        // Malformed ZIP or XML: keep what was parsed.
        catch (Exception ex) when (MalformedInput.Is(ex)) { }
        return result;
    }

    internal static void ExtractAtomFields(string xml, Dictionary<string, string> result)
    {
        // Russian element names -> English keys
        var nameMap = new Dictionary<string, string>
        {
            ["Наименование_проекта"] = "Name",
            ["Номер_проекта"] = "Number",
            ["Заказчик"] = "Client",
            ["Адрес_проекта"] = "Address",
            ["Статус_проекта"] = "Status",
            ["Дата_утверждения_проекта"] = "IssueDate",
            ["Разработал"] = "Author",
            ["Наименование_здания"] = "BuildingName",
            ["Название_организации"] = "OrganizationName"
        };

        foreach (var (ruName, enName) in nameMap)
        {
            int tagStart = xml.IndexOf($"<{ruName}", StringComparison.Ordinal);
            if (tagStart < 0) continue;
            int valStart = xml.IndexOf('>', tagStart) + 1;
            int valEnd = xml.IndexOf($"</{ruName}>", valStart, StringComparison.Ordinal);
            if (valStart > 0 && valEnd > valStart)
            {
                string val = System.Net.WebUtility.HtmlDecode(xml[valStart..valEnd].Trim());
                if (val.Length > 0) result[enName] = val;
            }
        }

        // Atom metadata (<title>, <A:product-version>, <updated>)
        ExtractTag(xml, "title", "Title", result, overwrite: false);
        ExtractTag(xml, "A:product-version", "RevitVersion", result, overwrite: true);
        ExtractTag(xml, "updated", "LastSavedUtc", result, overwrite: false);
        ExtractTag(xml, "A:updated", "LastSavedUtc", result, overwrite: false);
    }

    private static void ExtractTag(string xml, string tag, string key, Dictionary<string, string> result, bool overwrite)
    {
        int idx = xml.IndexOf($"<{tag}>", StringComparison.Ordinal);
        if (idx < 0) return;
        int vs = idx + tag.Length + 2;
        int ve = xml.IndexOf($"</{tag}>", vs, StringComparison.Ordinal);
        if (ve <= vs) return;
        string val = System.Net.WebUtility.HtmlDecode(xml[vs..ve].Trim());
        if (val.Length == 0) return;
        if (overwrite) result[key] = val;
        else result.TryAdd(key, val);
    }
}
