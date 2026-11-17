using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RvtIO.Writing;

namespace RvtIO.Cli;

/// <summary>
/// <c>rvtio downgrade &lt;source&gt; --target-profile &lt;file&gt; (--check | --output &lt;file&gt;) [--guid-rule exact|minimal] [--report report.json]</c>: plans the edit of the
/// class table of a newer project so that an older release may load it (--check), and writes a downgraded copy
/// (--output) with the class table lowered and BasicFileInfo pointed at the target release. Exit codes:
/// 0 done, 1 usage error (also an unusable path), 2 a file cannot be read or a partition stream is damaged, 5 refused (the releases are out of scope or the edit does not apply).
/// </summary>
internal static class DowngradeCommand
{
    internal const int Refused = 5;
    internal const string ReportSchema = "rvtio-downgrade-check/1";
    internal const string WriteReportSchema = "rvtio-downgrade/1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? source = null, profile = null, reportPath = null, outputPath = null;
        var rule = GuidRule.Exact;
        bool check = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--check": check = true; break;
                case "--target-profile" or "--report" or "--guid-rule" or "--output":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return Usage(error, $"{arg} requires one value.");
                    string value = args[++i];
                    if (arg == "--target-profile") { if (profile != null) return Usage(error, "--target-profile is given twice."); profile = value; }
                    else if (arg == "--report") { if (reportPath != null) return Usage(error, "--report is given twice."); reportPath = value; }
                    else if (arg == "--output") { if (outputPath != null) return Usage(error, "--output is given twice."); outputPath = value; }
                    else if (value == "exact") rule = GuidRule.Exact;
                    else if (value == "minimal") rule = GuidRule.Minimal;
                    else return Usage(error, "--guid-rule requires one value: exact or minimal.");
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal)) return Usage(error, $"Unknown option for downgrade: {arg}.");
                    if (source != null) return Usage(error, "downgrade takes one source file.");
                    source = arg;
                    break;
            }
        }
        if (source == null) return Usage(error, "downgrade requires the source file.");
        if (profile == null) return Usage(error, "downgrade requires --target-profile <file>, a project or family saved by the target release.");
        if (!check && outputPath == null) return Usage(error, "downgrade requires --check to plan or --output <file> to write a downgraded copy.");
        if (PathProblem(source, profile, reportPath, outputPath) is { } problem) return Usage(error, problem + " Nothing was read or written.");

        string sourceName = Path.GetFileName(source), profileName = Path.GetFileName(profile);
        DowngradePlanner.Result result;
        try { result = DowngradePlanner.Plan(source, profile, rule); }
        catch (RvtInputException exception)
        {
            return End(error, reportPath, RvtExitCodes.InputError, exception.Kind, exception.Message, sourceName, profileName, rule);
        }
        catch (DowngradeRefusedException exception)
        {
            return End(error, reportPath, Refused, exception.Kind, exception.Message, sourceName, profileName, rule);
        }
        catch (RvtLimitException exception)
        {
            return End(error, reportPath, RvtExitCodes.InputError, "limit-exceeded", exception.Message, sourceName, profileName, rule);
        }

        var plan = result.Plan;
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine($"Source: {sourceName} (release {result.SourceRelease}, {plan.SourceClasses.ToString(inv)} classes)");
        output.WriteLine($"Profile: {profileName} (release {result.TargetRelease}, {plan.TargetClasses.ToString(inv)} classes)");
        output.WriteLine($"Upgrade-identifier rule: {RuleName(rule)}");
        output.WriteLine($"Classes in both: {plan.CommonClasses.ToString(inv)}; class versions to lower: {plan.HigherVersions.Count.ToString(inv)}; upgrade-identifier lists to change: {plan.GuidLists.Count.ToString(inv)}");
        string elements = plan.ElementsInSourceOnlyClasses is int n ? $"; elements in them: {n.ToString(inv)}" : "; elements not counted";
        output.WriteLine($"Classes only in the source: {plan.ClassesOnlyInSource.Count.ToString(inv)}{elements}");
        output.WriteLine($"Classes only in the profile: {plan.ClassesOnlyInTarget.Count.ToString(inv)}; classes with different fields: {plan.FieldDifferences.Count.ToString(inv)}");
        int emptied = plan.GuidLists.Count(g => g.Result == 0);
        if (emptied > 0) output.WriteLine($"Upgrade-identifier lists that become empty: {emptied.ToString(inv)} (the rule yields no identifier for them)");
        if (result.CountFailure != null) error.WriteLine($"warning: the elements were not counted: {result.CountFailure}.");

        if (outputPath != null)
        {
            try
            {
                var written = DowngradeWriter.Write(source, profile, outputPath, rule);
                output.WriteLine($"Downgraded: {Path.GetFileName(written.OutputPath)} (release {written.SourceRelease} to {written.TargetRelease}, {written.EditedClasses.ToString(inv)} classes edited, {written.ReplacedStreams.ToString(inv)} streams replaced)");
            }
            catch (DowngradeRefusedException exception)
            {
                return End(error, reportPath, Refused, exception.Kind, exception.Message, sourceName, profileName, rule, outputPath);
            }
            catch (RvtInputException exception)
            {
                return End(error, reportPath, RvtExitCodes.InputError, exception.Kind, exception.Message, sourceName, profileName, rule, outputPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                error.WriteLine($"error: the downgraded file cannot be written: {exception.Message} Nothing was written (exit code {RvtExitCodes.InputError.ToString(inv)}).");
                var failed = new
                {
                    schema = WriteReportSchema,
                    status = "unreadable",
                    kind = "write-failed",
                    message = exception.Message,
                    guidRule = RuleName(rule),
                    source = new { file = sourceName },
                    profile = new { file = profileName },
                    output = new { file = Path.GetFileName(outputPath) },
                };
                return WriteReport(error, reportPath, failed, RvtExitCodes.InputError);
            }
            if (!check)
                return WriteReport(error, reportPath, WriteReportBody(result, sourceName, profileName, outputPath), RvtExitCodes.Success);
            output.WriteLine("Check and write done" + (reportPath == null ? "." : $" except the report {reportPath}."));
            return WriteReport(error, reportPath, WriteReportBody(result, sourceName, profileName, outputPath), RvtExitCodes.Success);
        }

        output.WriteLine("Check only: no file was written" + (reportPath == null ? "." : $" except the report {reportPath}."));
        return WriteReport(error, reportPath, Report(result, sourceName, profileName), RvtExitCodes.Success);
    }

    // Normalising a path can throw; every such failure, an empty path and a directory as report are usage errors found before any file is read.
    private static string? PathProblem(string source, string profile, string? report, string? output)
    {
        foreach (var (option, path) in new[] { ("downgrade source", source), ("--target-profile", profile), ("--report", report), ("--output", output) })
            if (path != null && string.IsNullOrWhiteSpace(path)) return $"The path of {option} is empty.";
        try
        {
            Path.GetFullPath(source);
            Path.GetFullPath(profile);
            if (output != null)
            {
                Path.GetFullPath(output);
                if (Directory.Exists(output)) return "--output names a directory, not a file.";
                if (ExportReport.SameFile(output, source) || ExportReport.SameFile(output, profile)) return "--output must not name the source file or the profile file.";
            }
            if (report == null) return null;
            if (Directory.Exists(report)) return "--report names a directory, not a file.";
            if (ExportReport.SameFile(report, source) || ExportReport.SameFile(report, profile)) return "--report must not name the source file or the profile file.";
            if (output != null && ExportReport.SameFile(report, output)) return "--report must not name the output file.";
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return $"A path is not usable ({exception.Message}).";
        }
    }

    private static string RuleName(GuidRule rule) => rule == GuidRule.Exact ? "exact" : "minimal";

    private static int Usage(TextWriter error, string message)
    {
        error.WriteLine($"{message} Usage: rvtio downgrade <source.rvt> --target-profile <file> (--check | --output <file>) [--guid-rule exact|minimal] [--report <report.json>]");
        return RvtExitCodes.Usage;
    }

    private static int End(TextWriter error, string? reportPath, int exitCode, string kind, string message, string sourceName, string profileName, GuidRule rule, string? outputPath = null)
    {
        error.WriteLine($"error: {message} Nothing was written (exit code {exitCode.ToString(CultureInfo.InvariantCulture)}).");
        var report = new
        {
            schema = outputPath == null ? ReportSchema : WriteReportSchema,
            status = exitCode == Refused ? "refused" : "unreadable",
            kind,
            message,
            guidRule = RuleName(rule),
            source = new { file = sourceName },
            profile = new { file = profileName },
            output = outputPath == null ? null : new { file = Path.GetFileName(outputPath) },
        };
        return WriteReport(error, reportPath, report, exitCode);
    }

    private static object WriteReportBody(DowngradePlanner.Result result, string sourceName, string profileName, string outputPath)
    {
        var plan = result.Plan;
        return new
        {
            schema = WriteReportSchema,
            status = "ok",
            guidRule = RuleName(plan.Rule),
            source = new { file = sourceName, release = result.SourceRelease, classes = plan.SourceClasses },
            profile = new { file = profileName, release = result.TargetRelease, classes = plan.TargetClasses },
            output = new { file = Path.GetFileName(outputPath), release = result.TargetRelease, editedClasses = plan.Edits.Count },
            commonClasses = plan.CommonClasses,
            classesOnlyInSource = plan.ClassesOnlyInSource.Select(c => new { name = c.Name, elements = c.Elements }),
            elementsInClassesOnlyInSource = plan.ElementsInSourceOnlyClasses,
            elementCountFailure = result.CountFailure,
            classesOnlyInProfile = plan.ClassesOnlyInTarget,
            classesWithHigherVersion = plan.HigherVersions.Select(v => new { name = v.Name, source = v.Source, profile = v.Target }),
            classesWithLowerVersion = plan.LowerVersions,
            fieldDifferences = plan.FieldDifferences.Select(f => new { name = f.Name, onlyInSource = f.OnlyInSource, onlyInProfile = f.OnlyInTarget }),
            upgradeIdentifierLists = plan.GuidLists.Select(g => new { name = g.Name, source = g.Source, profile = g.Target, result = g.Result, added = g.Added, removed = g.Removed }),
            edits = new { classVersions = plan.HigherVersions.Count, upgradeIdentifierLists = plan.GuidLists.Count, classTableBytesBefore = result.SourceStreamBytes, classTableBytesAfter = result.EditedStreamBytes },
        };
    }

    private static object Report(DowngradePlanner.Result result, string sourceName, string profileName)
    {
        var plan = result.Plan;
        return new
        {
            schema = ReportSchema,
            status = "ok",
            guidRule = RuleName(plan.Rule),
            source = new { file = sourceName, release = result.SourceRelease, classes = plan.SourceClasses },
            profile = new { file = profileName, release = result.TargetRelease, classes = plan.TargetClasses },
            commonClasses = plan.CommonClasses,
            classesOnlyInSource = plan.ClassesOnlyInSource.Select(c => new { name = c.Name, elements = c.Elements }),
            elementsInClassesOnlyInSource = plan.ElementsInSourceOnlyClasses,
            elementCountFailure = result.CountFailure,
            classesOnlyInProfile = plan.ClassesOnlyInTarget,
            classesWithHigherVersion = plan.HigherVersions.Select(v => new { name = v.Name, source = v.Source, profile = v.Target }),
            classesWithLowerVersion = plan.LowerVersions,
            fieldDifferences = plan.FieldDifferences.Select(f => new { name = f.Name, onlyInSource = f.OnlyInSource, onlyInProfile = f.OnlyInTarget }),
            upgradeIdentifierLists = plan.GuidLists.Select(g => new { name = g.Name, source = g.Source, profile = g.Target, result = g.Result, added = g.Added, removed = g.Removed }),
            edits = new { classVersions = plan.HigherVersions.Count, upgradeIdentifierLists = plan.GuidLists.Count, classTableBytesBefore = result.SourceStreamBytes, classTableBytesAfter = result.EditedStreamBytes },
        };
    }

    // The report is written beside its target and moved over it, so a failed write leaves no half file and a link keeps its content.
    internal static int WriteReport(TextWriter error, string? reportPath, object report, int exitCode)
    {
        if (reportPath == null) return exitCode;
        string temporary = reportPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, reportPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            error.WriteLine($"error: the report cannot be written to '{reportPath}': {exception.Message}");
            return exitCode != RvtExitCodes.Success ? exitCode : RvtExitCodes.Usage;
        }
        error.WriteLine($"Report written to {reportPath}");
        return exitCode;
    }
}
