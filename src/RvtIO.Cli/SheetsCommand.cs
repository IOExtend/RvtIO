using System.Text.Json;
using System.Text.Json.Serialization;
using RvtIO;
using RvtIO.Internal;
using RvtIO.Sheets;

namespace RvtIO.Cli;

/// <summary>
/// <c>rvtio sheets &lt;file.rvt|file.rfa&gt; [output.json] [--r22-layout] [--where FILTER] [--params] [--csv] [--json]</c>:
/// reads a file and writes the sheet catalog of <see cref="SheetCatalog"/> (the sheets, their viewports and what this
/// release does not decode) as JSON to the output path, or to standard output when no path is given.
/// <c>--where NAME=VALUE</c> (also <c>!=</c> and <c>~</c> for contains, <c>*</c> and <c>?</c> wildcards, repeatable)
/// keeps only the sheets whose parameters match, as in rvt-prt's <c>list --where</c>.
/// <c>--params</c> lists each kept sheet's parameters on standard error, <c>--csv</c> writes them as CSV instead of JSON.
/// Diagnostics go to standard error.
/// Exit codes: 0 written, 1 usage error or an output path that cannot be written, 2 the file cannot be read, 3 a reader
/// limit cut a stream.
/// </summary>
internal static class SheetsCommand
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? file = null, outputPath = null;
        bool r22Layout = false, showParams = false, csv = false;
        var whereTexts = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--r22-layout")
            {
                r22Layout = true;
                continue;
            }
            if (arg == "--params")
            {
                showParams = true;
                continue;
            }
            if (arg == "--csv")
            {
                csv = true;
                continue;
            }
            if (arg == "--json")
            {
                // The default output; accepted for rvt-prt compatibility.
                continue;
            }
            if (arg == "--where")
            {
                if (i + 1 >= args.Length)
                {
                    error.WriteLine("sheets --where needs a filter: NAME=VALUE, NAME!=VALUE or NAME~TEXT. " + Usage);
                    return RvtExitCodes.Usage;
                }
                whereTexts.Add(args[++i]);
                continue;
            }
            if (arg.StartsWith("--where=", StringComparison.Ordinal))
            {
                whereTexts.Add(arg["--where=".Length..]);
                continue;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine("Unknown option for sheets: " + arg + ". " + Usage);
                return RvtExitCodes.Usage;
            }
            if (file == null) file = arg;
            else if (outputPath == null) outputPath = arg;
            else
            {
                error.WriteLine("sheets takes one file and one output path. " + Usage);
                return RvtExitCodes.Usage;
            }
        }
        if (file == null)
        {
            error.WriteLine(Usage);
            return RvtExitCodes.Usage;
        }
        if (outputPath != null && FileIdentity.SameFile(file, outputPath))
        {
            error.WriteLine("The output path is the input file. Nothing was read or written.");
            return RvtExitCodes.Usage;
        }

        var filters = new List<SheetFilter>();
        foreach (string text in whereTexts)
        {
            var parsed = SheetFilter.TryParse(text, out string? parseError);
            if (parsed == null)
            {
                error.WriteLine(parseError + " " + Usage);
                return RvtExitCodes.Usage;
            }
            filters.Add(parsed);
        }

        SheetCatalog catalog;
        try
        {
            catalog = SheetCatalog.Extract(RvtDocument.Open(file, new RvtReaderOptions { R22Layout = r22Layout }));
        }
        catch (RvtInputException exception)
        {
            error.WriteLine($"Cannot read {Path.GetFileName(file)}: {exception.Message}");
            return RvtExitCodes.InputError;
        }
        catch (RvtLimitException exception)
        {
            error.WriteLine($"Cannot read {Path.GetFileName(file)}: a reader limit cut the stream "
                + $"({exception.Hit.Limit} = {exception.Hit.Value}).");
            return RvtExitCodes.LimitExceeded;
        }

        if (filters.Count > 0)
        {
            // A misspelt name stops with the names there are, as in rvt-prt.
            var available = SheetFilter.AvailableNames(catalog);
            foreach (var filter in filters)
            {
                if (!available.Contains(filter.Name))
                {
                    error.WriteLine($"Unknown sheet parameter '{filter.Name}'. Available: {string.Join(", ", available)}.");
                    return RvtExitCodes.Usage;
                }
            }
            var kept = catalog.Sheets.Where(sheet => filters.All(filter => filter.Matches(sheet))).ToList();
            if (kept.Count == 0)
            {
                error.WriteLine("[sheets] no sheet matches the --where filters; nothing was written.");
                return RvtExitCodes.Usage;
            }
            catalog = SheetCatalog.FromFiltered(kept, catalog.Viewports.Where(v => kept.Any(s => s.ElementId == v.SheetId)).ToList());
        }

        if (showParams)
        {
            foreach (var sheet in catalog.Sheets)
            {
                error.WriteLine($"{sheet.Number ?? "?"}   {sheet.Name ?? ""}");
                foreach (var (key, value) in sheet.Parameters)
                    error.WriteLine($"       {key}: {value}");
            }
        }

        string payload = csv ? ToCsv(catalog) : JsonSerializer.Serialize(catalog, Options);
        if (outputPath == null)
        {
            output.WriteLine(payload);
        }
        else
        {
            string temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, payload + Environment.NewLine, new System.Text.UTF8Encoding(false));
                File.Move(temporary, outputPath, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                error.WriteLine($"Cannot write {outputPath}: {exception.Message}");
                return RvtExitCodes.Usage;
            }
        }

        if (catalog.Sheets.Count == 0)
            error.WriteLine("[sheets] no element carries the sheet parameters: a file the reader reads by its own class "
                + "schema may not expose the sheet class (see docs/sheets.md).");

        string target = outputPath == null ? "standard output" : outputPath;
        error.WriteLine($"[sheets] {catalog.Sheets.Count} sheets, {catalog.Viewports.Count} viewports, "
            + $"{catalog.Issues.Count} limits not decoded -> {target}");
        return RvtExitCodes.Success;
    }

    private static string ToCsv(SheetCatalog catalog)
    {
        var extra = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in catalog.Sheets)
            foreach (var key in sheet.Parameters.Keys)
                extra.Add(key);
        var lines = new List<string>
        {
            string.Join(",", new[] { "ElementId", "Number", "Name", "IssueDate", "Scale", "DrawnBy",
                "DesignedBy", "CheckedBy", "ApprovedBy" }.Concat(extra).Select(EscapeCsv)),
        };
        foreach (var sheet in catalog.Sheets)
        {
            var fields = new List<string?>
            {
                sheet.ElementId.ToString(), sheet.Number, sheet.Name, sheet.IssueDate,
                sheet.Scale?.ToString(), sheet.DrawnBy, sheet.DesignedBy, sheet.CheckedBy, sheet.ApprovedBy,
            };
            foreach (string key in extra)
                fields.Add(sheet.Parameters.TryGetValue(key, out var value) ? value : null);
            lines.Add(string.Join(",", fields.Select(EscapeCsv)));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string EscapeCsv(string? field)
    {
        field ??= string.Empty;
        if (field.Contains('"') || field.Contains(',') || field.Contains('\n') || field.Contains('\r'))
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }

    private const string Usage = "Usage: rvtio sheets <file.rvt|file.rfa> [output.json] [--r22-layout] [--where FILTER] [--params] [--csv] [--json]";
}
