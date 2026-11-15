using System.Text.Json;
using System.Text.Json.Serialization;
using RvtIO;
using RvtIO.Sheets;

namespace RvtIO.Cli;

/// <summary>
/// <c>rvtio sheets &lt;file.rvt|file.rfa&gt; [output.json] [--r22-layout]</c>: reads a file and writes the sheet catalog of
/// <see cref="SheetCatalog"/> (the sheets, their viewports and what this release does not decode) as JSON to the output
/// path, or to standard output when no path is given. Diagnostics go to standard error.
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
        bool r22Layout = false;
        foreach (string arg in args)
        {
            if (arg == "--r22-layout")
            {
                r22Layout = true;
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
        if (outputPath != null && ExportReport.SameFile(file, outputPath))
        {
            error.WriteLine("The output path is the input file. Nothing was read or written.");
            return RvtExitCodes.Usage;
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

        string json = JsonSerializer.Serialize(catalog, Options);
        if (outputPath == null)
        {
            output.WriteLine(json);
        }
        else
        {
            string temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json + Environment.NewLine, new System.Text.UTF8Encoding(false));
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

    private const string Usage = "Usage: rvtio sheets <file.rvt|file.rfa> [output.json] [--r22-layout]";
}
