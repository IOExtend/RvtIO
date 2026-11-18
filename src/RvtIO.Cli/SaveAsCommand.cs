using System.Globalization;
using RvtIO.Internal;

namespace RvtIO.Cli;

/// <summary>
/// <c>rvtio save-as &lt;source&gt; [&lt;output&gt;] [--check] [--report report.json]</c>: inspects a local Revit project (<c>.rvt</c>) or family
/// (<c>.rfa</c>) against the write-capability matrix (<c>--check</c>) or writes a verified lossless copy to <c>&lt;output&gt;</c>.
/// Exit codes: 0 done, 1 usage error, 2 input or I/O failure, 5 refused by the write-capability matrix.
/// </summary>
internal static class SaveAsCommand
{
    internal const int Refused = 5;
    internal const string ReportSchema = "rvtio-save-as/1";

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? source = null, destination = null, reportPath = null;
        bool check = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--check":
                    check = true;
                    break;
                case "--report":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return Usage(error, "--report requires one value.");
                    if (reportPath is not null)
                        return Usage(error, "--report is given twice.");
                    reportPath = args[++i];
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        return Usage(error, $"Unknown option for save-as: {arg}.");
                    if (source is null) source = arg;
                    else if (destination is null) destination = arg;
                    else return Usage(error, "save-as takes at most two file paths (<source> and <output>).");
                    break;
            }
        }

        if (source is null)
            return Usage(error, "save-as requires <source>.");
        if (!check && destination is null)
            return Usage(error, "save-as requires <output>, or --check to inspect capabilities without writing.");
        if (check && destination is not null)
            return Usage(error, "save-as --check does not take an <output> file path.");
        if (PathProblem(source, destination, reportPath) is { } problem)
            return Usage(error, problem + " Nothing was read or written.");

        string sourceName = Path.GetFileName(source);
        try
        {
            var capabilities = RvtWriter.InspectCapabilities(source);
            if (check)
            {
                PrintCapabilities(output, sourceName, capabilities);
                bool ok = capabilities.Supports(RvtWriteOperation.SaveAs);
                int exit = ok ? RvtExitCodes.Success : Refused;
                return DowngradeCommand.WriteReport(error, reportPath, BuildCheckReport(sourceName, capabilities), exit);
            }

            var result = RvtWriter.SaveAs(source, destination!);
            var inv = CultureInfo.InvariantCulture;
            output.WriteLine($"Saved: {Path.GetFileName(result.OutputPath)} ({KindName(result.FileKind)}, release {result.EffectiveRelease.ToString(inv)}, {result.StreamCount.ToString(inv)} streams verified)");
            return DowngradeCommand.WriteReport(error, reportPath, BuildWriteReport(sourceName, capabilities, result), RvtExitCodes.Success);
        }
        catch (RvtWriteRefusedException ex)
        {
            return EndError(error, reportPath, Refused, "refused", ex.Kind, ex.Message, sourceName);
        }
        catch (RvtInputException ex)
        {
            return EndError(error, reportPath, RvtExitCodes.InputError, "unreadable", ex.Kind, ex.Message, sourceName);
        }
        catch (RvtLimitException ex)
        {
            return EndError(error, reportPath, RvtExitCodes.InputError, "unreadable", "limit-exceeded", ex.Message, sourceName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EndError(error, reportPath, RvtExitCodes.InputError, "io-error", "io-error", ex.Message, sourceName);
        }
    }

    private static void PrintCapabilities(TextWriter output, string sourceName, RvtWriteCapabilities capabilities)
    {
        var inv = CultureInfo.InvariantCulture;
        string rel = capabilities.EffectiveRelease?.ToString(inv) ?? "unknown";
        string width = capabilities.ElementIdWidth is int w ? $"{w.ToString(inv)}-byte ids" : "unknown id width";
        output.WriteLine($"Source: {sourceName} ({KindName(capabilities.FileKind)}, release {rel}, {width}, {capabilities.StreamCount.ToString(inv)} streams, {capabilities.PartitionStreamCount.ToString(inv)} partitions)");
        foreach (RvtWriteOperation op in Enum.GetValues<RvtWriteOperation>())
        {
            var support = capabilities.Operations[op];
            string state = support.IsSupported ? "supported" : $"refused ({support.RefusalCode}: {support.Reason})";
            output.WriteLine($"  {OperationName(op)}: {state}");
        }
    }

    private static string? PathProblem(string source, string? destination, string? report)
    {
        foreach (var (label, path) in new[] { ("source", source), ("output", destination), ("--report", report) })
            if (path is not null && string.IsNullOrWhiteSpace(path))
                return $"The path of {label} is empty.";
        try
        {
            Path.GetFullPath(source);
            if (destination is not null) Path.GetFullPath(destination);
            if (report is null) return null;
            Path.GetFullPath(report);
            if (Directory.Exists(report)) return "--report names a directory, not a file.";
            if (FileIdentity.SameFile(report, source) || (destination is not null && FileIdentity.SameFile(report, destination)))
                return "--report must not name the source or output file.";
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return $"A path is not usable ({ex.Message}).";
        }
    }

    private static int Usage(TextWriter error, string message)
    {
        error.WriteLine($"{message} Usage: rvtio save-as <source.rvt|source.rfa> [<output.rvt|output.rfa>] [--check] [--report <report.json>]");
        return RvtExitCodes.Usage;
    }

    private static int EndError(TextWriter error, string? reportPath, int exitCode, string status, string kind, string message, string sourceName)
    {
        error.WriteLine($"error: {message} Nothing was written (exit code {exitCode.ToString(CultureInfo.InvariantCulture)}).");
        var report = new
        {
            schema = ReportSchema,
            status,
            kind,
            message,
            source = new { file = sourceName },
        };
        return DowngradeCommand.WriteReport(error, reportPath, report, exitCode);
    }

    private static object BuildCheckReport(string sourceName, RvtWriteCapabilities capabilities) => new
    {
        schema = ReportSchema,
        status = capabilities.Supports(RvtWriteOperation.SaveAs) ? "ok" : "refused",
        mode = "check",
        source = DescribeCapabilities(sourceName, capabilities),
    };

    private static object BuildWriteReport(string sourceName, RvtWriteCapabilities capabilities, RvtWriteResult result) => new
    {
        schema = ReportSchema,
        status = "ok",
        mode = "write",
        source = DescribeCapabilities(sourceName, capabilities),
        output = new
        {
            file = Path.GetFileName(result.OutputPath),
            streams = result.StreamCount,
            replacedStreams = result.ReplacedStreamCount,
            rewrittenRecords = result.RewrittenRecordCount,
        },
    };

    private static object DescribeCapabilities(string sourceName, RvtWriteCapabilities capabilities) => new
    {
        file = sourceName,
        fileKind = KindName(capabilities.FileKind),
        savedRelease = capabilities.SavedRelease,
        schemaRelease = capabilities.SchemaRelease,
        effectiveRelease = capabilities.EffectiveRelease,
        elementIdWidth = capabilities.ElementIdWidth,
        workshared = capabilities.IsWorkshared,
        cloudModel = capabilities.IsCloudModel,
        streams = capabilities.StreamCount,
        partitions = capabilities.PartitionStreamCount,
        operations = Enum.GetValues<RvtWriteOperation>().Select(op =>
        {
            var s = capabilities.Operations[op];
            return new { operation = OperationName(op), supported = s.IsSupported, refusalCode = s.RefusalCode, reason = s.Reason };
        }),
    };

    private static string KindName(RvtFileKind kind) => kind switch
    {
        RvtFileKind.Project => "project",
        RvtFileKind.Family => "family",
        _ => "unknown",
    };

    private static string OperationName(RvtWriteOperation operation) => operation switch
    {
        RvtWriteOperation.SaveAs => "save-as",
        RvtWriteOperation.PagedStreamRewrite => "paged-stream-rewrite",
        RvtWriteOperation.PartitionRecordRewrite => "partition-record-rewrite",
        _ => operation.ToString(),
    };
}
