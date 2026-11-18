using System.Globalization;
using RvtIO.Internal;

namespace RvtIO.Cli;

/// <summary>
/// <c>rvtio coverage &lt;file.rvt&gt; [--report &lt;report.json&gt;] [--top &lt;n&gt;]</c>: counts how many records of the partition streams of one file
/// the file's own class schema walks to their last byte (<see cref="CoverageCensus"/>) and prints the counts. It writes nothing but the
/// report. Exit codes: 0 measured (also when a partition stream does not frame or the records cannot be counted), 1 usage error (also a
/// report that cannot be written), 2 the file cannot be read. With <c>--trace</c> it prints the walk of single records instead
/// (<see cref="CoverageTrace"/>), a developer aid.
/// </summary>
internal static class CoverageCommand
{
    internal const string ReportSchema = "rvtio-coverage/1";

    /// <summary>The most stop reasons listed for one class, on standard output and in the report.</summary>
    private const int ListedStops = 5;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? file = null, reportPath = null, trace = null, traceReason = null;
        int top = 15, traceMax = 3, traceChannel = 102, traceBytes = 256;
        bool traceFamily = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--trace-family": traceFamily = true; break;
                case "--trace" or "--trace-reason" or "--trace-max" or "--trace-channel" or "--trace-bytes":
                    if (i + 1 >= args.Length) return Usage(error, $"{arg} requires one value.");
                    string traceValue = args[++i];
                    if (arg == "--trace") trace = traceValue;
                    else if (arg == "--trace-reason") traceReason = traceValue;
                    else if (!int.TryParse(traceValue, NumberStyles.None, Inv, out int number)) return Usage(error, $"{arg} requires a whole number.");
                    else if (arg == "--trace-max") traceMax = number;
                    else if (arg == "--trace-channel") traceChannel = number;
                    else traceBytes = number;
                    break;
                case "--report" or "--top":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return Usage(error, $"{arg} requires one value.");
                    string value = args[++i];
                    if (arg == "--report") { if (reportPath != null) return Usage(error, "--report is given twice."); reportPath = value; }
                    else if (!int.TryParse(value, NumberStyles.None, Inv, out top)) return Usage(error, "--top requires a whole number, 0 or more.");
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal)) return Usage(error, $"Unknown option for coverage: {arg}.");
                    if (file != null) return Usage(error, "coverage takes one file.");
                    file = arg;
                    break;
            }
        }
        if (file == null) return Usage(error, "coverage requires the file.");
        if (trace != null && reportPath != null) return Usage(error, "--trace writes no report.");
        if (ReportProblem(file, reportPath) is { } problem) return Usage(error, problem + " Nothing was read or written.");

        string name = Path.GetFileName(file);
        if (trace != null)
        {
            // --trace <class name | owner ids>: print the walk of single records instead of counting
            var owners = trace.Split(',').Select(t => ulong.TryParse(t, NumberStyles.None, Inv, out ulong id) ? id : (ulong?)null).ToList();
            bool byOwner = owners.All(o => o != null);
            try
            {
                CoverageTrace.Write(file, new(byOwner ? null : trace, byOwner ? owners.Select(o => o!.Value).ToHashSet() : null, traceReason, traceChannel, traceFamily, traceMax, traceBytes), output);
            }
            catch (RvtInputException exception) { return Unreadable(error, null, exception.Kind, exception.Message, name); }
            catch (RvtLimitException exception) { return Unreadable(error, null, "limit-exceeded", exception.Message, name); }
            return RvtExitCodes.Success;
        }
        CoverageCensus.Result result;
        try { result = CoverageCensus.Read(file); }
        catch (RvtInputException exception) { return Unreadable(error, reportPath, exception.Kind, exception.Message, name); }
        catch (RvtLimitException exception) { return Unreadable(error, reportPath, "limit-exceeded", exception.Message, name); }

        output.WriteLine($"File: {name} (saved by release {Show(result.SavedRelease)}, schema of release {Show(result.SchemaRelease)}, " +
            $"{result.ClassCount.ToString(Inv)} classes, element id of {Show(result.IdWidth)} bytes)");
        var partitions = result.Streams.Where(s => s.Kind == "partition").ToList();
        var paged = result.Streams.Where(s => s.Kind == "paged").ToList();
        output.WriteLine($"Streams: {result.Streams.Count.ToString(Inv)}, {result.Streams.Sum(s => s.RawBytes).ToString(Inv)} raw bytes; " +
            $"partition streams framed {partitions.Count(s => s.Framing == "ok").ToString(Inv)} of {partitions.Count.ToString(Inv)}; " +
            $"paged streams decoded {paged.Count(s => s.Decoded == true).ToString(Inv)} of {paged.Count.ToString(Inv)}");
        if (result.RecordsFailure != null) output.WriteLine($"Records not counted: {result.RecordsFailure}.");
        foreach (var group in result.Groups) output.WriteLine(GroupLine(group));
        if (top > 0 && result.RecordsFailure == null)
        {
            var notExact = result.Classes.Where(c => c.Sums.Exact < c.Sums.Items).OrderByDescending(c => c.Sums.Items - c.Sums.Exact).Take(top).ToList();
            output.WriteLine(notExact.Count == 0 ? "Not exact: none" : $"Not exact, top {top.ToString(Inv)}:");
            foreach (var row in notExact) output.WriteLine("  " + ClassLine(row));
        }
        return DowngradeCommand.WriteReport(error, reportPath, Report(result, name), RvtExitCodes.Success);
    }

    internal static string GroupLine(CoverageCensus.GroupRow group)
    {
        var sums = group.Sums;
        string mismatches = sums.ChecksumMismatches == 0 ? "" : $"; {sums.ChecksumMismatches.ToString(Inv)} checksum mismatches";
        return $"{Block(group.Family)} {group.Channel.ToString(Inv)}: {sums.Items.ToString(Inv)} records, {sums.Exact.ToString(Inv)} exact ({Percent(sums.Exact, sums.Items)}); " +
            $"{sums.PayloadBytes.ToString(Inv)} payload bytes, {sums.ExactBytes.ToString(Inv)} exact ({Percent(sums.ExactBytes, sums.PayloadBytes)}){mismatches}";
    }

    private static string ClassLine(CoverageCensus.ClassRow row)
    {
        string stops = string.Join("; ", row.Stops.Take(ListedStops).Select(s => $"{s.Reason} ({s.Count.ToString(Inv)})"));
        long other = OtherStops(row);
        return $"{row.Channel.ToString(Inv)} {Block(row.Family)} {row.Class}: {(row.Sums.Items - row.Sums.Exact).ToString(Inv)} of {row.Sums.Items.ToString(Inv)}; " +
            stops + (other == 0 ? "" : $"; other ({other.ToString(Inv)})");
    }

    private static long OtherStops(CoverageCensus.ClassRow row) => row.Stops.Skip(ListedStops).Sum(s => s.Count);

    private static string Block(bool family) => family ? "family" : "project";

    private static string Show(int? value) => value?.ToString(Inv) ?? "unknown";

    // Rounded down, so that 100.00% is only printed when nothing is missing.
    private static string Percent(long part, long whole)
    {
        if (whole == 0) return "-";
        long hundredths = part * 10000 / whole;
        return $"{(hundredths / 100).ToString(Inv)}.{(hundredths % 100).ToString("00", Inv)}%";
    }

    // A path that cannot be normalised is a usage error; the report is moved over its target, so a report path that names the input would destroy it.
    private static string? ReportProblem(string file, string? report)
    {
        try
        {
            Path.GetFullPath(file);
            if (report == null) return null;
            if (Directory.Exists(report)) return "--report names a directory, not a file.";
            return FileIdentity.SameFile(report, file) ? "--report must not name the input file." : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return $"A path is not usable ({exception.Message}).";
        }
    }

    private static int Usage(TextWriter error, string message)
    {
        error.WriteLine($"{message} Usage: rvtio coverage <file.rvt> [--report <report.json>] [--top <n>], or rvtio coverage <file.rvt> " +
            "--trace <class|owner,...> [--trace-reason <text>] [--trace-max <n>] [--trace-channel <n>] [--trace-bytes <n>] [--trace-family]");
        return RvtExitCodes.Usage;
    }

    private static int Unreadable(TextWriter error, string? reportPath, string kind, string message, string name)
    {
        error.WriteLine($"error: {message} Nothing was measured (exit code {RvtExitCodes.InputError.ToString(Inv)}).");
        return DowngradeCommand.WriteReport(error, reportPath, new { schema = ReportSchema, status = "unreadable", kind, message, file = name }, RvtExitCodes.InputError);
    }

    private static object Report(CoverageCensus.Result result, string name) => new
    {
        schema = ReportSchema,
        status = "ok",
        file = name,
        savedRelease = result.SavedRelease,
        schemaRelease = result.SchemaRelease,
        idWidth = result.IdWidth,
        classCount = result.ClassCount,
        streams = result.Streams.Select(s => new
        {
            path = s.Path,
            kind = s.Kind,
            rawBytes = s.RawBytes,
            framing = s.Framing,
            decoded = s.Decoded,
            blocks = s.Blocks,
            familyBlocks = s.FamilyBlocks,
            segments = s.Segments,
            inflatedBytes = s.InflatedBytes,
            records = s.Records,
        }),
        records = result.RecordsFailure != null ? null : (object)new
        {
            totals = new
            {
                items = result.Totals.Items,
                exact = result.Totals.Exact,
                payloadBytes = result.Totals.PayloadBytes,
                exactBytes = result.Totals.ExactBytes,
                checksumMismatches = result.Totals.ChecksumMismatches,
                terminators = result.Totals.Terminators,
            },
            groups = result.Groups.Select(g => new
            {
                block = Block(g.Family),
                channel = g.Channel,
                items = g.Sums.Items,
                exact = g.Sums.Exact,
                payloadBytes = g.Sums.PayloadBytes,
                exactBytes = g.Sums.ExactBytes,
                checksumMismatches = g.Sums.ChecksumMismatches,
                terminators = g.Sums.Terminators,
            }),
            classes = result.Classes.Select(c => new
            {
                block = Block(c.Family),
                channel = c.Channel,
                @class = c.Class,
                items = c.Sums.Items,
                exact = c.Sums.Exact,
                payloadBytes = c.Sums.PayloadBytes,
                exactBytes = c.Sums.ExactBytes,
                checksumMismatches = c.Sums.ChecksumMismatches,
                stops = c.Stops.Take(ListedStops).Select(s => new { reason = s.Reason, count = s.Count, owners = s.Owners }),
                otherStops = OtherStops(c),
            }),
        },
        recordsFailure = result.RecordsFailure,
    };
}
