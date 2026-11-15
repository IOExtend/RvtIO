using System.Globalization;
using RvtIO;
using RvtIO.Pdf;
using RvtIO.Print;

namespace RvtIO.Cli;

/// <summary>
/// <c>rvtio print &lt;file.rvt&gt; &lt;out.pdf&gt; [--level &lt;id|name&gt;] [--view &lt;id|name&gt;] [--scale &lt;n&gt;]
/// [--paper A4|A3|A2|A1|A0]</c>: renders the plan of one level, or one or more plan views at their own scales, to a PDF
/// (see <see cref="PlanPrinter"/>). <c>--list-levels</c> and <c>--list-views</c> print what can be printed instead.
/// Exit codes: 0 written, 1 usage error or an output path that cannot be written (also a level or view that is not
/// printable), 2 the file cannot be read, 3 a reader limit cut a stream, 4 nothing to draw.
/// </summary>
internal static class PrintCommand
{
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? file = null, outputPath = null, levelText = null;
        var viewTexts = new List<string>();
        var sheetTexts = new List<string>();
        int scale = 100;
        PdfPageSize? paper = null;
        bool listLevels = false, listViews = false, listSheets = false, allViews = false, allSheets = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--list-levels":
                    listLevels = true;
                    break;
                case "--list-views":
                    listViews = true;
                    break;
                case "--list-sheets":
                    listSheets = true;
                    break;
                case "--all-views":
                    allViews = true;
                    break;
                case "--all-sheets":
                    allSheets = true;
                    break;
                case "--level" or "--view" or "--sheet" or "--scale" or "--paper":
                    if (i + 1 >= args.Length) return Usage(error, arg + " requires a value.");
                    string value = args[++i];
                    if (arg == "--level") levelText = value;
                    else if (arg == "--view") viewTexts.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    else if (arg == "--sheet") sheetTexts.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
        bool listing = listLevels || listViews || listSheets;
        if (!listing && outputPath == null) return Usage(error, "print requires the output PDF path.");
        // The comparison is the file's identity, not the spelling of the paths: an output path that is a hard link,
        // symbolic link or junction to the input PDF would truncate the model. ExportReport.SameFile guards the other
        // writing commands the same way.
        if (outputPath != null && ExportReport.SameFile(outputPath, file))
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
        IReadOnlyList<PlanView> views = PlanPrinter.Views(document);
        IReadOnlyList<ElevationView> elevations = ElevationPrinter.Views(document);
        IReadOnlyList<PrintableSheet> sheets = SheetPrinter.Sheets(document);
        IReadOnlyList<ComposedSheet> composed = SheetComposer.Sheets(document);
        if (listSheets)
        {
            if (composed.Count > 0)
            {
                foreach (ComposedSheet sheet in composed)
                    output.WriteLine($"{sheet.SheetId}\t{sheet.Number}\t{sheet.Name ?? ""}\t"
                        + $"{sheet.Page.WidthMillimetres:0}x{sheet.Page.HeightMillimetres:0} mm\t"
                        + $"{sheet.ViewportCount} views\t{sheet.TextCount} texts");
            }
            else
            {
                foreach (PrintableSheet sheet in sheets)
                    output.WriteLine($"{sheet.SheetId}\t{sheet.Number ?? ""}\t{sheet.Name ?? ""}\t"
                        + $"{sheet.WidthMillimetres:0}x{sheet.HeightMillimetres:0} mm\t{sheet.CurveCount} curves\t{sheet.TextCount} texts");
                if (sheets.Count == 0) error.WriteLine("No sheet with decoded linework was found.");
            }
            return RvtExitCodes.Success;
        }
        if (listLevels)
        {
            foreach (PlanLevel level in levels)
            {
                string elevation = level.ElevationFeet is double feet
                    ? (feet * 304.8).ToString("0", CultureInfo.InvariantCulture) + " mm" : "elevation not decoded";
                output.WriteLine($"{level.LevelId}\t{level.Name ?? "(unnamed)"}\t{elevation}");
            }
            if (!listViews) return RvtExitCodes.Success;
        }
        if (listViews)
        {
            foreach (PlanView view in views)
                output.WriteLine($"{view.ViewId}\t{view.Name}\t1:{view.Scale}\tlevel {view.LevelId}\tplan");
            foreach (ElevationView view in elevations)
                output.WriteLine($"{view.ViewId}\t{view.Name}\t1:{view.Scale}\t{view.Direction}\televation");
            foreach (RvtIO.Model.Element other in ViewPrinter.OtherViews(document))
            {
                string kind = other.RootClassName == "DBViewRendering" ? "rendering" : "3d";
                int otherScale = other.LookupParameter(RvtIO.BuiltIns.BuiltInParameter.VIEW_SCALE)?.Value is int value
                    ? value : 0;
                output.WriteLine($"{other.Id}\t{other.Name}\t1:{otherScale}\t-\t{kind}");
            }
            if (views.Count == 0 && elevations.Count == 0 && ViewPrinter.OtherViews(document).Count == 0)
                error.WriteLine("No printable view was found.");
            return RvtExitCodes.Success;
        }

        try
        {
            PdfDocument pdf;
            if (allSheets)
            {
                if (composed.Count == 0)
                {
                    error.WriteLine("No numbered sheet was found; nothing to compose.");
                    return RvtExitCodes.DecodeError;
                }
                pdf = SheetComposer.RenderSheets(document, composed.Select(sheet => sheet.SheetId).ToList(),
                    new SheetComposeOptions { PaperSize = paper });
            }
            else if (allViews)
            {
                var ids = new List<int>();
                foreach (PlanView view in views) ids.Add(view.ViewId);
                foreach (ElevationView view in elevations) ids.Add(view.ViewId);
                // A view set is complete with the 3D views and the renderings.
                foreach (RvtIO.Model.Element other in ViewPrinter.OtherViews(document)) ids.Add(other.Id);
                if (ids.Count == 0)
                {
                    error.WriteLine("No printable view was found; nothing to print.");
                    return RvtExitCodes.DecodeError;
                }
                pdf = ViewPrinter.Render(document, ids, new PlanPrintOptions { PaperSize = paper });
            }
            else if (sheetTexts.Count > 0)
            {
                var sheetIds = new List<int>();
                var composedIds = new List<int>();
                foreach (string text in sheetTexts)
                {
                    ComposedSheet? composedMatch = null;
                    foreach (ComposedSheet sheet in composed)
                    {
                        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id == sheet.SheetId)
                            composedMatch = sheet;
                        else if (string.Equals(sheet.Number, text, StringComparison.OrdinalIgnoreCase))
                            composedMatch = sheet;
                        if (composedMatch != null) break;
                    }
                    if (composedMatch != null)
                    {
                        composedIds.Add(composedMatch.SheetId);
                        continue;
                    }
                    PrintableSheet? match = null;
                    foreach (PrintableSheet sheet in sheets)
                    {
                        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id == sheet.SheetId)
                            match = sheet;
                        else if (string.Equals(sheet.Number, text, StringComparison.OrdinalIgnoreCase))
                            match = sheet;
                        if (match != null) break;
                    }
                    if (match == null)
                    {
                        error.WriteLine($"No printable sheet is named or numbered '{text}'. --list-sheets shows them.");
                        return RvtExitCodes.Usage;
                    }
                    sheetIds.Add(match.SheetId);
                }
                pdf = composedIds.Count == sheetTexts.Count
                    ? SheetComposer.RenderSheets(document, composedIds, new SheetComposeOptions { PaperSize = paper })
                    : SheetPrinter.RenderSheets(document, sheetIds, new SheetPrintOptions { PaperSize = paper });
            }
            else if (viewTexts.Count > 0)
            {
                var viewIds = new List<int>();
                foreach (string text in viewTexts)
                {
                    int? resolved = ResolveView(text, views, elevations);
                    if (resolved == null)
                    {
                        // A 3D view or a rendering is a printable view too.
                        foreach (RvtIO.Model.Element other in ViewPrinter.OtherViews(document))
                        {
                            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int otherId)
                                && otherId == other.Id)
                                resolved = other.Id;
                            else if (string.Equals(other.Name, text, StringComparison.OrdinalIgnoreCase))
                                resolved = other.Id;
                            if (resolved != null) break;
                        }
                    }
                    if (resolved == null)
                    {
                        error.WriteLine($"No printable view is named or numbered '{text}'. --list-views shows them.");
                        return RvtExitCodes.Usage;
                    }
                    viewIds.Add(resolved.Value);
                }
                pdf = ViewPrinter.Render(document, viewIds, new PlanPrintOptions { PaperSize = paper });
            }
            else
            {
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
                pdf = PlanPrinter.Render(document, levelId, scale, new PlanPrintOptions { PaperSize = paper });
            }

            // The PDF is written beside the target and moved over it, so a failure leaves the target as it was and the
            // output keeps its own directory (the temporary file is on the same volume).
            string temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                pdf.Save(temporary);
                File.Move(temporary, outputPath!, overwrite: true);
            }
            catch
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                throw;
            }
            PdfPageSize page = pdf.Pages[0].Size;
            error.WriteLine($"[print] {pdf.Pages.Count} page(s), first {page.WidthMillimetres:0} x {page.HeightMillimetres:0} mm -> {outputPath}");
            return RvtExitCodes.Success;
        }
        catch (InvalidOperationException exception)
        {
            error.WriteLine(exception.Message);
            return RvtExitCodes.DecodeError;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Cannot write {outputPath}: {exception.Message}");
            return RvtExitCodes.Usage;
        }
    }

    /// <summary>The id of the printable view a text names: a plan or an elevation, by id or by exact name.</summary>
    private static int? ResolveView(string text, IReadOnlyList<PlanView> views, IReadOnlyList<ElevationView> elevations)
    {
        bool byId = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int id);
        foreach (PlanView view in views)
        {
            if ((byId && id == view.ViewId) || string.Equals(view.Name, text, StringComparison.OrdinalIgnoreCase))
                return view.ViewId;
        }
        foreach (ElevationView view in elevations)
        {
            if ((byId && id == view.ViewId) || string.Equals(view.Name, text, StringComparison.OrdinalIgnoreCase))
                return view.ViewId;
        }
        return null;
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

    private static int Usage(TextWriter error, string problem)
    {
        error.WriteLine(problem);
        error.WriteLine("Usage: rvtio print <file.rvt> <out.pdf> [--level <id|name>] [--view <id|name>] [--sheet <id|number>] "
            + "[--all-views] [--all-sheets] [--scale <n>] [--paper A4|A3|A2|A1|A0] "
            + "[--list-levels] [--list-views] [--list-sheets]");
        return RvtExitCodes.Usage;
    }
}
