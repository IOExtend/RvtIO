using System.Diagnostics;
using System.Globalization;
using System.Text;
using RvtIO.Diff;
using RvtIO.Internal;

namespace RvtIO.Cli;

/// <summary>
/// <c>rvtio diff &lt;left.rvt&gt; &lt;right.rvt&gt; [output.json] [--json [path]] [--output &lt;path&gt;]
/// [--category &lt;name|id&gt;] [--element &lt;uniqueId|id&gt;] [--linear-tol &lt;feet&gt;] [--angle-tol &lt;rad&gt;]
/// [--no-geometry] [--no-params] [--no-relations] [--no-links] [--include-display-units]
/// [--allow-id-fallback] [--redact] [--limit &lt;n&gt;]</c>
/// Exit codes: 0 compared cleanly, 1 usage or output write error, 2 input error, 3 reader limit exceeded.
/// </summary>
internal static class DiffCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? leftPath = null, rightPath = null, outputPath = null;
        bool jsonMode = false;
        var categories = new List<string>();
        var elements = new List<string>();
        var options = new RvtDiffOptions();
        int limit = 20;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--json":
                    jsonMode = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal)
                        && leftPath != null && rightPath != null && outputPath == null)
                    {
                        outputPath = args[++i];
                    }
                    break;
                case "--no-geometry":
                    options.IncludeGeometry = false;
                    break;
                case "--no-params":
                    options.IncludeParameters = false;
                    break;
                case "--no-relations":
                    options.IncludeRelationships = false;
                    break;
                case "--no-links":
                    options.IncludeTransmissionLinks = false;
                    break;
                case "--include-display-units":
                    options.IncludeDisplayFormattingDifferences = true;
                    break;
                case "--allow-id-fallback":
                    options.AllowElementIdFallbackWhenRelated = true;
                    break;
                case "--redact":
                    options.RedactPersonalData = true;
                    break;
                case "--output" or "-o" or "--category" or "--element" or "--linear-tol" or "--angle-tol" or "--limit":
                    if (i + 1 >= args.Length) return Usage(error, $"{arg} requires a value.");
                    string val = args[++i];
                    if (arg is "--output" or "-o")
                    {
                        if (string.IsNullOrWhiteSpace(val)) return Usage(error, $"{arg} requires a non-empty path.");
                        outputPath = val;
                        jsonMode = true;
                    }
                    else if (arg == "--category")
                    {
                        categories.AddRange(val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }
                    else if (arg == "--element")
                    {
                        elements.AddRange(val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }
                    else if (arg == "--linear-tol")
                    {
                        if (!double.TryParse(val, NumberStyles.Float, Inv, out double tol) || tol < 0)
                            return Usage(error, "--linear-tol requires a non-negative number (feet).");
                        options.LinearToleranceFeet = tol;
                    }
                    else if (arg == "--angle-tol")
                    {
                        if (!double.TryParse(val, NumberStyles.Float, Inv, out double tol) || tol < 0)
                            return Usage(error, "--angle-tol requires a non-negative number (radians).");
                        options.AngleToleranceRadians = tol;
                    }
                    else if (arg == "--limit")
                    {
                        if (!int.TryParse(val, NumberStyles.None, Inv, out limit))
                            return Usage(error, "--limit requires a non-negative whole number.");
                    }
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        return Usage(error, $"Unknown option for diff: {arg}.");
                    if (leftPath == null) leftPath = arg;
                    else if (rightPath == null) rightPath = arg;
                    else if (outputPath == null) { outputPath = arg; jsonMode = true; }
                    else return Usage(error, "diff takes two Revit files and at most one output path.");
                    break;
            }
        }

        if (leftPath == null || rightPath == null)
            return Usage(error, "diff requires two Revit file paths: <left.rvt> <right.rvt>.");

        if (outputPath != null && (FileIdentity.SameFile(outputPath, leftPath) || FileIdentity.SameFile(outputPath, rightPath)))
        {
            error.WriteLine("The output path is an input file. Nothing was read or written.");
            return RvtExitCodes.Usage;
        }

        if (categories.Count > 0) options.Categories = categories;
        if (elements.Count > 0) options.Elements = elements;

        var sw = Stopwatch.StartNew();
        RvtRevisionDiffResult result;
        try
        {
            result = RvtRevisionDiffer.Compare(leftPath, rightPath, options);
        }
        catch (RvtInputException ex)
        {
            error.WriteLine($"Cannot read Revit file: {ex.Message}");
            return RvtExitCodes.InputError;
        }
        catch (RvtLimitException ex)
        {
            error.WriteLine($"Cannot read Revit file: reader limit exceeded ({ex.Hit.Limit} = {ex.Hit.Value}).");
            return RvtExitCodes.LimitExceeded;
        }
        sw.Stop();
        long peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);

        if (jsonMode)
        {
            string json = result.ToJson(indented: true);
            if (outputPath == null)
            {
                output.WriteLine(json);
            }
            else
            {
                string tmp = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    string? parent = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    File.WriteAllText(tmp, json + Environment.NewLine, new UTF8Encoding(false));
                    File.Move(tmp, outputPath, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    error.WriteLine($"Cannot write {outputPath}: {ex.Message}");
                    return RvtExitCodes.Usage;
                }
            }
            error.WriteLine($"[diff] {result.Left.Label} -> {result.Right.Label}: +{result.Added.Count} -{result.Removed.Count} ~{result.Modified.Count} (={result.UnchangedCount}) [{sw.ElapsedMilliseconds} ms, peak {peakMb} MiB]");
            return RvtExitCodes.Success;
        }

        WriteSummary(output, result, limit, sw.ElapsedMilliseconds, peakMb);
        return RvtExitCodes.Success;
    }

    private static void WriteSummary(TextWriter output, RvtRevisionDiffResult diff, int limit, long elapsedMs, long peakMb)
    {
        output.WriteLine($"=== diff: {diff.Left.Label} -> {diff.Right.Label} ({diff.Relation}, {diff.RelationReason}) ===");
        output.WriteLine($"  Left:  v={diff.Left.RevitVersion ?? "?"} elements={diff.Left.FilteredElementCount}/{diff.Left.ElementCount} partial={diff.Left.IsPartialRead}");
        output.WriteLine($"  Right: v={diff.Right.RevitVersion ?? "?"} elements={diff.Right.FilteredElementCount}/{diff.Right.ElementCount} partial={diff.Right.IsPartialRead}");
        output.WriteLine($"  Summary: added={diff.Added.Count}, removed={diff.Removed.Count}, modified={diff.Modified.Count}, unchanged={diff.UnchangedCount}, typeImpacts={diff.TypeImpacts.Count}, ambiguous={diff.AmbiguousMatches.Count}, unavailable={diff.Unavailable.Count}");
        output.WriteLine($"  Performance: {elapsedMs} ms, peak working set {peakMb} MiB");

        foreach (string w in diff.Warnings)
            output.WriteLine($"  warning: {w}");

        void WriteList(string heading, IReadOnlyList<RvtDiffElementRef> items)
        {
            if (items.Count == 0) return;
            output.WriteLine($"\n{heading} ({items.Count}):");
            foreach (var item in items.Take(limit))
                output.WriteLine($"  {item.IdentityKey} [{item.Category ?? "?"}] {item.Name ?? "(unnamed)"}");
            if (items.Count > limit)
                output.WriteLine($"  ... +{items.Count - limit} more");
        }

        WriteList("Added", diff.Added);
        WriteList("Removed", diff.Removed);

        if (diff.Modified.Count > 0)
        {
            output.WriteLine($"\nModified ({diff.Modified.Count}):");
            foreach (var mod in diff.Modified.Take(limit))
            {
                var tags = new List<string>();
                if (mod.TypeChange != null) tags.Add("type");
                if (mod.PlacementChange != null) tags.Add("placement");
                if (mod.GeometryChange != null) tags.Add($"geometry({mod.GeometryChange.Availability})");
                if (mod.RelationshipChanges.Count > 0) tags.Add($"relations({mod.RelationshipChanges.Count})");
                if (mod.ParameterChanges.Count > 0) tags.Add($"params({mod.ParameterChanges.Count})");
                if (mod.AffectedByTypeIdentityKeys.Count > 0) tags.Add($"type-impact({mod.AffectedByTypeIdentityKeys.Count})");
                if (mod.UnavailableAspects.Count > 0) tags.Add($"unavailable:{string.Join('+', mod.UnavailableAspects)}");
                output.WriteLine($"  {mod.IdentityKey} [{mod.Element.Category ?? "?"}] {mod.Element.Name ?? "(unnamed)"}: {string.Join(", ", tags)}");
            }
            if (diff.Modified.Count > limit)
                output.WriteLine($"  ... +{diff.Modified.Count - limit} more");
        }

        if (diff.TypeImpacts.Count > 0)
        {
            output.WriteLine($"\nType impacts ({diff.TypeImpacts.Count}):");
            foreach (var impact in diff.TypeImpacts.Take(limit))
                output.WriteLine($"  {impact.TypeElement.IdentityKey} {impact.TypeElement.Name ?? "?"}: [{string.Join(", ", impact.ChangeKinds)}] -> {impact.AffectedInstances.Count} affected instance(s)");
        }
    }

    private static int Usage(TextWriter error, string problem)
    {
        error.WriteLine(problem);
        error.WriteLine("Usage: rvtio diff <left.rvt> <right.rvt> [output.json] [--json [path]] [--output <path>] [--category <name|id>] [--element <uniqueId|id>] [--linear-tol <feet>] [--angle-tol <rad>] [--no-geometry] [--no-params] [--no-relations] [--redact]");
        return RvtExitCodes.Usage;
    }
}
