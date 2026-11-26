using System.Globalization;
using RvtIO.Internal;
using RvtIO;
using RvtIO.Pdf;
using RvtIO.Print;
using RvtIO.Sheets;

namespace RvtIO.Cli;

/// <summary>
/// <c>rvtio print &lt;file.rvt&gt; &lt;out.pdf|out.svg&gt; [--level &lt;id|name&gt;] [--view &lt;id|name&gt;] [--sheet &lt;id|number&gt;] [--where FILTER] [--scale &lt;n&gt;]
/// [--paper A4|A3|A2|A1|A0]</c>: renders the plan of one level to PDF or SVG (<c>.svg</c>), or one or more plan views or sheets at their own scales to PDF
/// (see <see cref="PlanPrinter"/>). <c>--list-levels</c> and <c>--list-views</c> print what can be printed instead.
/// <c>--where NAME=VALUE</c> (also <c>!=</c> and <c>~</c> for contains, <c>*</c> and <c>?</c> wildcards, repeatable) keeps
/// only the sheets whose parameters match; it needs <c>--sheet</c>, <c>--all-sheets</c> or <c>--list-sheets</c>.
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
        var whereTexts = new List<string>();
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
                case "--where":
                    if (i + 1 >= args.Length) return Usage(error, "--where needs a filter: NAME=VALUE, NAME!=VALUE or NAME~TEXT.");
                    whereTexts.Add(args[++i]);
                    break;
                default:
                    if (arg.StartsWith("--where=", StringComparison.Ordinal))
                    {
                        whereTexts.Add(arg["--where=".Length..]);
                        break;
                    }
                    if (arg.StartsWith("--", StringComparison.Ordinal)) return Usage(error, "Unknown option for print: " + arg + ".");
                    if (file == null) file = arg;
                    else if (outputPath == null) outputPath = arg;
                    else return Usage(error, "print takes one file and one output path.");
                    break;
            }
        }
        if (file == null) return Usage(error, "print requires the file.");
        var whereFilters = new List<SheetFilter>();
        foreach (string text in whereTexts)
        {
            var parsed = SheetFilter.TryParse(text, out string? parseError);
            if (parsed == null) return Usage(error, parseError!);
            whereFilters.Add(parsed);
        }
        if (whereFilters.Count > 0 && sheetTexts.Count == 0 && !allSheets && !listSheets)
            return Usage(error, "--where selects sheets; it needs --sheet, --all-sheets or --list-sheets.");
        bool listing = listLevels || listViews || listSheets;
        if (!listing && outputPath == null) return Usage(error, "print requires the output PDF path (or .svg for a level plan).");
        bool svgOutput = outputPath != null && outputPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
        if (svgOutput && (allSheets || allViews || sheetTexts.Count > 0 || viewTexts.Count > 0 || whereFilters.Count > 0))
            return Usage(error, "SVG output supports level plan rendering only (--level, --scale, --paper); use a .pdf path for views and sheets.");
        // File identity also catches when a link in the output path points to the input model.
        if (outputPath != null && FileIdentity.SameFile(outputPath, file))
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
        HashSet<int>? whereSheets = null;
        if (whereFilters.Count > 0)
        {
            // A misspelt name stops with the names there are, as in rvt-prt and rvtio sheets.
            SheetCatalog catalog = SheetCatalog.Extract(document);
            var available = SheetFilter.AvailableNames(catalog);
            foreach (var filter in whereFilters)
            {
                if (!available.Contains(filter.Name))
                {
                    error.WriteLine($"Unknown sheet parameter '{filter.Name}'. Available: {string.Join(", ", available)}.");
                    return RvtExitCodes.Usage;
                }
            }
            whereSheets = catalog.Sheets.Where(sheet => whereFilters.All(filter => filter.Matches(sheet)))
                .Select(sheet => sheet.ElementId).ToHashSet();
            if (whereSheets.Count == 0)
            {
                error.WriteLine("[print] no sheet matches the --where filters; nothing was written.");
                return RvtExitCodes.Usage;
            }
        }
        IReadOnlyList<PrintableSheet> ListedSheets() =>
            whereSheets == null ? sheets : sheets.Where(sheet => whereSheets.Contains(sheet.SheetId)).ToList();
        IReadOnlyList<ComposedSheet> ListedComposed() =>
            whereSheets == null ? composed : composed.Where(sheet => whereSheets.Contains(sheet.SheetId)).ToList();
        if (listSheets)
        {
            IReadOnlyList<ComposedSheet> listedComposed = ListedComposed();
            IReadOnlyList<PrintableSheet> listedSheets = ListedSheets();
            if (listedComposed.Count > 0)
            {
                foreach (ComposedSheet sheet in listedComposed)
                    output.WriteLine($"{sheet.SheetId}\t{sheet.Number}\t{sheet.Name ?? ""}\t"
                        + $"{sheet.Page.WidthMillimetres:0}x{sheet.Page.HeightMillimetres:0} mm\t"
                        + $"{sheet.ViewportCount} views\t{sheet.TextCount} texts");
            }
            else
            {
                foreach (PrintableSheet sheet in listedSheets)
                    output.WriteLine($"{sheet.SheetId}\t{sheet.Number ?? ""}\t{sheet.Name ?? ""}\t"
                        + $"{sheet.WidthMillimetres:0}x{sheet.HeightMillimetres:0} mm\t{sheet.CurveCount} curves\t{sheet.TextCount} texts");
                if (listedSheets.Count == 0) error.WriteLine("No sheet with decoded linework was found.");
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
            if (svgOutput)
            {
                if (levels.Count == 0)
                {
                    error.WriteLine("No level with a wall or column was found; nothing to print.");
                    return RvtExitCodes.DecodeError;
                }
                int? levelId = ResolveLevelId(levelText, levels);
                if (levelText != null && levelId == null)
                {
                    error.WriteLine($"No printable level is named or numbered '{levelText}'. --list-levels shows them.");
                    return RvtExitCodes.Usage;
                }

                PdfPageSize svgPaper;
                double effectiveScale;
                string temporarySvg = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var fs = File.Create(temporarySvg))
                        PlanPrinter.WriteSvg(document, fs, levelId, scale, new PlanPrintOptions { PaperSize = paper },
                            out svgPaper, out effectiveScale);
                    File.Move(temporarySvg, outputPath!, overwrite: true);
                }
                catch
                {
                    if (File.Exists(temporarySvg)) File.Delete(temporarySvg);
                    throw;
                }
                error.WriteLine($"[print] 1 page(s), first {svgPaper.WidthMillimetres:0} x {svgPaper.HeightMillimetres:0} mm (1:{effectiveScale:0}) -> {outputPath}");
                return RvtExitCodes.Success;
            }

            PdfDocument pdf;
            if (allSheets)
            {
                IReadOnlyList<ComposedSheet> toCompose = ListedComposed();
                if (toCompose.Count == 0)
                {
                    error.WriteLine("No numbered sheet was found; nothing to compose.");
                    return RvtExitCodes.DecodeError;
                }
                pdf = SheetComposer.RenderSheets(document, toCompose.Select(sheet => sheet.SheetId).ToList(),
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
                // Explicit names resolve against the complete catalogue; the --where filters
                // then intersect. Resolving against the filtered lists first would report a
                // filtered-out sheet as nonexistent instead of skipping it.
                ExplicitSheetSelection selection = SelectExplicitSheets(sheetTexts, sheets, composed, whereSheets);
                if (selection.Missing is { } missing)
                {
                    error.WriteLine($"No printable sheet is named or numbered '{missing}'. --list-sheets shows them.");
                    return RvtExitCodes.Usage;
                }
                if (selection.ComposedIds.Count == 0 && selection.SheetIds.Count == 0)
                {
                    error.WriteLine("[print] no sheet matches the --where filters; nothing was written.");
                    return RvtExitCodes.Usage;
                }
                var sheetIds = selection.SheetIds;
                var composedIds = selection.ComposedIds;
                pdf = sheetIds.Count == 0
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
                int? levelId = ResolveLevelId(levelText, levels);
                if (levelText != null && levelId == null)
                {
                    error.WriteLine($"No printable level is named or numbered '{levelText}'. --list-levels shows them.");
                    return RvtExitCodes.Usage;
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

    /// <summary>Explicit <c>--sheet</c> texts resolved against the complete catalogue, then intersected
    /// with the <c>--where</c> filter: a sheet prints when it is one of those numbered and matches the
    /// filters. <see cref="Missing"/> names the first text no printable sheet carries; when it is null
    /// and both id lists are empty, the intersection removed everything.</summary>
    internal sealed record ExplicitSheetSelection(
        IReadOnlyList<int> SheetIds, IReadOnlyList<int> ComposedIds, string? Missing);

    /// <summary>Resolves explicit sheet names against every printable sheet, then keeps only the ids
    /// the <c>--where</c> filter matched (null keeps everything).</summary>
    internal static ExplicitSheetSelection SelectExplicitSheets(
        IEnumerable<string> sheetTexts,
        IReadOnlyList<PrintableSheet> sheets,
        IReadOnlyList<ComposedSheet> composed,
        ISet<int>? whereSheets)
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
            if (match == null) return new ExplicitSheetSelection([], [], text);
            sheetIds.Add(match.SheetId);
        }
        if (whereSheets != null)
        {
            composedIds.RemoveAll(id => !whereSheets.Contains(id));
            sheetIds.RemoveAll(id => !whereSheets.Contains(id));
        }
        return new ExplicitSheetSelection(sheetIds, composedIds, null);
    }

    private static int? ResolveLevelId(string? levelText, IReadOnlyList<PlanLevel> levels)
    {
        if (levelText == null) return null;
        bool byId = int.TryParse(levelText, NumberStyles.None, CultureInfo.InvariantCulture, out int id);
        foreach (PlanLevel level in levels)
        {
            if ((byId && id == level.LevelId) || string.Equals(level.Name, levelText, StringComparison.OrdinalIgnoreCase))
                return level.LevelId;
        }
        return null;
    }

    private static int Usage(TextWriter error, string problem)
    {
        error.WriteLine(problem);
        error.WriteLine("Usage: rvtio print <file.rvt> <out.pdf|out.svg> [--level <id|name>] [--view <id|name>] [--sheet <id|number>] "
            + "[--where FILTER] [--all-views] [--all-sheets] [--scale <n>] [--paper A4|A3|A2|A1|A0] "
            + "[--list-levels] [--list-views] [--list-sheets]");
        return RvtExitCodes.Usage;
    }
}
