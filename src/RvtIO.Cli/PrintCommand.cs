using System.Globalization;
using RvtIO;
using RvtIO.Pdf;
using RvtIO.Print;

namespace RvtIO.Cli;

/// <summary>
/// <c>rvtio print &lt;file.rvt&gt; &lt;out.pdf&gt; [--level &lt;id|name&gt;] [--scale &lt;n&gt;] [--paper A4|A3|A2|A1|A0]</c>:
/// renders the plan of one level to a one-page PDF (see <see cref="PlanPrinter"/>). <c>--list-levels</c> prints the
/// levels that can be printed instead. Exit codes: 0 written, 1 usage error or an output path that cannot be written,
/// 2 the file cannot be read, 3 a reader limit cut a stream, 4 the level has nothing to draw.
/// </summary>
internal static class PrintCommand
{
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? file = null, outputPath = null, levelText = null;
        int scale = 100;
        PdfPageSize? paper = null;
        bool listLevels = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--list-levels":
                    listLevels = true;
                    break;
                case "--level" or "--scale" or "--paper":
                    if (i + 1 >= args.Length) return Usage(error, arg + " requires a value.");
                    string value = args[++i];
                    if (arg == "--level") levelText = value;
                    else if (arg == "--scale")
                    {
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out scale) || scale <= 0)
                            return Usage(error, "--scale requires a positive whole number.");
                    }
                    else if (Paper(value) is { } size) paper = size;
                    else return Usage(error, "--paper requires one of A4, A3, A2, A1, A0.");
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal)) return Usage(error, "Unknown option for print: " + arg + ".");
                    if (file == null) file = arg;
                    else if (outputPath == null) outputPath = arg;
                    else return Usage(error, "print takes one file and one output path.");
                    break;
            }
        }
        if (file == null) return Usage(error, "print requires the file.");
        if (!listLevels && outputPath == null) return Usage(error, "print requires the output PDF path.");
        if (outputPath != null && SamePath(file, outputPath))
        {
            error.WriteLine("The output path is the input file. Nothing was read or written.");
            return RvtExitCodes.Usage;
        }

        RvtDocument document;
        try
        {
            document = RvtDocument.Open(file);
        }
        catch (RvtInputException exception)
        {
            error.WriteLine($"Cannot read {Path.GetFileName(file)}: {exception.Message}");
            return RvtExitCodes.InputError;
        }
        catch (RvtLimitException exception)
        {
            error.WriteLine($"Cannot read {Path.GetFileName(file)}: a reader limit cut the stream ({exception.Hit.Limit}).");
            return RvtExitCodes.LimitExceeded;
        }

        IReadOnlyList<PlanLevel> levels = PlanPrinter.Levels(document);
        if (listLevels)
        {
            foreach (PlanLevel level in levels)
            {
                string elevation = level.ElevationFeet is double feet
                    ? (feet * 304.8).ToString("0", CultureInfo.InvariantCulture) + " mm" : "elevation not decoded";
                output.WriteLine($"{level.LevelId}\t{level.Name ?? "(unnamed)"}\t{elevation}");
            }
            if (levels.Count == 0) error.WriteLine("No level with a wall or column was found.");
            return RvtExitCodes.Success;
        }

        if (levels.Count == 0)
        {
            error.WriteLine("No level with a wall or column was found; nothing to print.");
            return RvtExitCodes.DecodeError;
        }

        int? levelId = null;
        if (levelText != null)
        {
            foreach (PlanLevel level in levels)
            {
                if (int.TryParse(levelText, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id == level.LevelId)
                    levelId = level.LevelId;
                else if (string.Equals(level.Name, levelText, StringComparison.OrdinalIgnoreCase))
                    levelId = level.LevelId;
                if (levelId != null) break;
            }
            if (levelId == null)
            {
                error.WriteLine($"No printable level is named or numbered '{levelText}'. --list-levels shows them.");
                return RvtExitCodes.Usage;
            }
        }

        PdfDocument pdf;
        try
        {
            pdf = PlanPrinter.Render(document, levelId, scale, new PlanPrintOptions { PaperSize = paper });
        }
        catch (InvalidOperationException exception)
        {
            error.WriteLine(exception.Message);
            return RvtExitCodes.DecodeError;
        }

        try
        {
            pdf.Save(outputPath!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Cannot write {outputPath}: {exception.Message}");
            return RvtExitCodes.Usage;
        }

        PdfPageSize page = pdf.Pages[0].Size;
        error.WriteLine($"[print] plan 1:{scale}, {page.WidthMillimetres:0} x {page.HeightMillimetres:0} mm -> {outputPath}");
        return RvtExitCodes.Success;
    }

    private static PdfPageSize? Paper(string name) => name.ToUpperInvariant() switch
    {
        "A4" => PdfPageSize.A4,
        "A3" => PdfPageSize.A3,
        "A2" => PdfPageSize.A2,
        "A1" => PdfPageSize.A1,
        "A0" => PdfPageSize.A0,
        _ => null,
    };

    private static bool SamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static int Usage(TextWriter error, string problem)
    {
        error.WriteLine(problem);
        error.WriteLine("Usage: rvtio print <file.rvt> <out.pdf> [--level <id|name>] [--scale <n>] [--paper A4|A3|A2|A1|A0] [--list-levels]");
        return RvtExitCodes.Usage;
    }
}
