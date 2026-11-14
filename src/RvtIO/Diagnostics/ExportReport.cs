using System.Text.Json;
using System.Text.Json.Serialization;
using RvtIO.Model;

namespace RvtIO;

/// <summary>Process exit codes of the command line exporter (documented in the README and in docs/export-report.md).</summary>
public static class RvtExitCodes
{
    /// <summary>The export finished. With <c>--allow-partial</c>, or without <c>--strict</c> when a stream lost or recovered a member,
    /// it may have read less than the whole file: the report says so.</summary>
    public const int Success = 0;
    /// <summary>The command line is wrong. Unchanged from earlier versions.</summary>
    public const int Usage = 1;
    /// <summary>The input cannot be read: missing, empty, not a compound file, or cut or damaged so that a stream ends early.</summary>
    public const int InputError = 2;
    /// <summary>A stream exceeded a limit of the reader and the read was cut. Not returned with <c>--allow-partial</c>,
    /// except for a stream over the platform array length, which cannot be read at all.</summary>
    public const int LimitExceeded = 3;
    /// <summary>The container is readable but a decoder failed on its content (damaged or unsupported data). The report holds the exception.</summary>
    public const int DecodeError = 4;
    /// <summary>With <c>--strict</c>, the read finished without exceeding a limit, or <c>--allow-partial</c> accepted a limit,
    /// but the read is partial (a member failed to decode, was rebuilt by structural recovery, or decoded without a matching checksum,
    /// or the element table decoded no row). Nothing is exported; the report lists the streams and the reason.</summary>
    public const int PartialRead = 6;

    /// <summary>The exit code of a finished read: <see cref="LimitExceeded"/> when a limit cut a stream and partial output
    /// was not allowed, else <see cref="Success"/>.</summary>
    public static int ForRead(RvtReadDiagnostics diagnostics, bool allowPartial) =>
        ForRead(diagnostics, allowPartial, strict: false, partialRead: false);

    /// <summary>The exit code of a finished read: <see cref="LimitExceeded"/> when a limit cut a stream and partial output
    /// was not allowed, else <see cref="PartialRead"/> when <paramref name="strict"/> is true and the read is partial,
    /// else <see cref="Success"/>.</summary>
    public static int ForRead(RvtReadDiagnostics diagnostics, bool allowPartial, bool strict, bool partialRead = false) =>
        diagnostics.LimitHits.Count > 0 && !allowPartial ? LimitExceeded
        : strict && (partialRead || diagnostics.LimitHits.Count > 0
            || diagnostics.Streams.Any(s => s.Failed > 0 || s.Recovered > 0 || s.Unverified > 0)) ? PartialRead
        : Success;
}

/// <summary>The input cannot be read at all (missing, empty, not a compound file, or a stream ends before its declared length).</summary>
public sealed class RvtInputException : Exception
{
    /// <summary>Stable code: <c>cannot-open</c>, <c>empty-file</c>, <c>not-a-compound-file</c> or <c>truncated-or-damaged</c>.</summary>
    public string Kind { get; }

    /// <summary>Creates the exception with its stable <paramref name="kind"/> code.</summary>
    public RvtInputException(string kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

/// <summary>A stream exceeds a limit of the reader that cannot be read around (see <see cref="RvtReaderOptions.RaiseLimits"/>).</summary>
public sealed class RvtLimitException : Exception
{
    /// <summary>The limit that was exceeded.</summary>
    public ReaderLimitHit Hit { get; }

    /// <summary>Creates the exception for a limit hit.</summary>
    public RvtLimitException(ReaderLimitHit hit) : base(hit.Detail) => Hit = hit;
}

/// <summary>A limit of the reader that cut a stream.</summary>
/// <param name="Stream">The stream, as storage/name.</param>
/// <param name="Limit">Stable limit code, see docs/export-report.md.</param>
/// <param name="Value">The value of the limit in force.</param>
/// <param name="Raisable">True when <see cref="RvtReaderOptions.RaiseLimits"/> raises this limit.</param>
public sealed record ReaderLimitHit(string Stream, string Limit, long Value, bool Raisable, string Detail);

/// <summary>How much of one partition stream decoded. Members are the gzip members the stream is cut into.</summary>
public sealed record StreamRecord(string Name, long RawBytes, int Members, int Decoded, int Failed, int Recovered, int Unverified);

/// <summary>What the reader collects while it reads a file, for the report (<see cref="RvtReaderOptions.Diagnostics"/>).</summary>
public sealed class RvtReadDiagnostics
{
    /// <summary>The partition streams read, in the order they were read.</summary>
    public List<StreamRecord> Streams { get; } = new();

    /// <summary>The streams cut by a limit of the reader.</summary>
    public List<ReaderLimitHit> LimitHits { get; } = new();
}

/// <summary>The machine-readable report of one export run (docs/export-report.md). Serialize with <see cref="ToJson"/>.</summary>
public sealed class ExportReport
{
    /// <summary>The name and version of the report format.</summary>
    public const string SchemaName = "rvtio-report/1";

    /// <summary>Always <see cref="SchemaName"/>.</summary>
    public string Schema { get; set; } = SchemaName;
    /// <summary><c>complete</c>, <c>partial</c> (a limit cut a stream, or a member of a stream did not decode) or <c>failed</c>.</summary>
    public string Status { get; set; } = "complete";
    /// <summary>The exit code of the process (see <see cref="RvtExitCodes"/>).</summary>
    public int ExitCode { get; set; }

    /// <summary>The input file.</summary>
    public ReportInput Input { get; set; } = new();

    /// <summary>Why the file could not be read; only when <see cref="Status"/> is <c>failed</c>.</summary>
    public ReportError? Error { get; set; }

    /// <summary>The options that bear on the limits and the limits that cut a stream.</summary>
    public ReportLimits Limits { get; set; } = new();

    /// <summary>The partition streams.</summary>
    public List<ReportStream> Streams { get; set; } = new();

    /// <summary>The element table against the parsed elements; absent when the file could not be read.</summary>
    public ReportElements? Elements { get; set; }

    /// <summary>Other findings that make the read partial.</summary>
    public List<ReportIssue> Issues { get; set; } = new();

    /// <summary>What the export wrote and left out; absent when nothing was exported.</summary>
    public ReportExport? Export { get; set; }

    /// <summary>The input file.</summary>
    public sealed class ReportInput
    {
        /// <summary>File name without directory.</summary>
        public string File { get; set; } = "";

        /// <summary>Size in bytes.</summary>
        public long Bytes { get; set; }
    }

    /// <summary>A failure to read the file.</summary>
    public sealed class ReportError
    {
        /// <summary>Stable code, for example <c>truncated-or-damaged</c>.</summary>
        public string Kind { get; set; } = "";

        /// <summary>One line for a person.</summary>
        public string Message { get; set; } = "";
        /// <summary>The exception text with its stack trace, for a <c>decode-failure</c>; absent otherwise.</summary>
        public string? Detail { get; set; }
    }

    /// <summary>The limits of the run.</summary>
    public sealed class ReportLimits
    {
        /// <summary>True when the run raised the limits (<c>--raise-limits</c>).</summary>
        public bool Raised { get; set; }
        /// <summary>True when the run accepted a read cut by a limit (<c>--allow-partial</c>).</summary>
        public bool PartialAllowed { get; set; }
        /// <summary>True when the run refused a partial read (<c>--strict</c>).</summary>
        public bool Strict { get; set; }
        /// <summary>Every stream a limit cut.</summary>
        public List<ReaderLimitHit> Hit { get; set; } = new();
    }

    /// <summary>One partition stream.</summary>
    public sealed class ReportStream
    {
        /// <summary>Storage and name, for example <c>Partitions/0</c>.</summary>
        public string Name { get; set; } = "";

        /// <summary>Size of the stream in the file.</summary>
        public long RawBytes { get; set; }

        /// <summary>Gzip members the stream is cut into.</summary>
        public int Members { get; set; }

        /// <summary>Members that produced output.</summary>
        public int Decoded { get; set; }

        /// <summary>Members that produced none; their data is missing.</summary>
        public int Failed { get; set; }

        /// <summary>Members rescued by a structural relock.</summary>
        public int Recovered { get; set; }

        /// <summary>Members kept although their checksum did not match.</summary>
        public int Unverified { get; set; }
        /// <summary>Decoded members / members; 1 for a stream without members.</summary>
        public double DecodedFraction { get; set; }
    }

    /// <summary>A finding about one stream.</summary>
    public sealed class ReportIssue
    {
        /// <summary>Storage and name of the stream, for example <c>Global/ElemTable</c>.</summary>
        public string Stream { get; set; } = "";

        /// <summary>Stable code: <c>no-rows</c>, <c>failed-members</c>, <c>recovered-members</c> or <c>unverified-members</c>.</summary>
        public string Code { get; set; } = "";

        /// <summary>One line for a person.</summary>
        public string Detail { get; set; } = "";
    }

    /// <summary>The element table against the parsed elements.</summary>
    public sealed class ReportElements
    {
        /// <summary>Rows of the element table.</summary>
        public int InTable { get; set; }
        /// <summary>Rows of the element table for which the reader parsed a record.</summary>
        public int Decoded { get; set; }
        /// <summary>Decoded / in table; 1 for an empty table.</summary>
        public double DecodedFraction { get; set; }
        /// <summary>Elements that did not reach the file, by class and reason, including the rows that never decoded.</summary>
        public List<ReportSkipped> Undecoded { get; set; } = new();
    }

    /// <summary>What the export wrote.</summary>
    public sealed class ReportExport
    {
        /// <summary><c>ifc</c>, <c>json</c> (the BM export) or <c>json-full</c>.</summary>
        public string Format { get; set; } = "";
        /// <summary>Parsed elements the export considered.</summary>
        public int Considered { get; set; }
        /// <summary>Elements the export wrote.</summary>
        public int Exported { get; set; }

        /// <summary>Elements the export left out.</summary>
        public int Skipped { get; set; }
        /// <summary>Exported / considered; 1 when nothing was considered.</summary>
        public double ExportedFraction { get; set; }
        /// <summary>Every skipped element class with its reason.</summary>
        public List<ReportSkipped> SkippedByClass { get; set; } = new();
    }

    /// <summary>Elements of one class left out for one reason.</summary>
    public sealed class ReportSkipped
    {
        /// <summary>The category of the elements, else their root class, else <c>unknown</c>.</summary>
        public string Class { get; set; } = "";

        /// <summary>Stable code of the rule that left them out, see docs/export-report.md.</summary>
        public string Reason { get; set; } = "";

        /// <summary>How many elements.</summary>
        public int Count { get; set; }
        /// <summary>Up to <see cref="SkipLog.ExampleLimit"/> element ids.</summary>
        public List<int> Examples { get; set; } = new();
    }

    /// <summary>True when both paths name the same file. Existing files are compared by file identity (volume and file index on Windows,
    /// device and inode on Linux and macOS), which catches hard links, file links and directory links (junctions, directory symbolic links)
    /// anywhere in the path. Otherwise, or when a file does not exist yet, the real paths are compared: every existing component is resolved
    /// through its links, so a report path below a linked directory is recognised before the file is created. Used to refuse a report path
    /// that would overwrite the input or the export output.</summary>
    internal static bool SameFile(string first, string second)
    {
        string a = Path.GetFullPath(first), b = Path.GetFullPath(second);
        if (File.Exists(a) && File.Exists(b) && FileIdentity(a) is { } ia && FileIdentity(b) is { } ib) return ia == ib;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(RealPath(a), RealPath(b), comparison);
    }

    /// <summary>The path with every existing component resolved through its symbolic links and junctions; components that do not exist are kept.</summary>
    internal static string RealPath(string fullPath, int depth = 0)
    {
        string root = Path.GetPathRoot(fullPath) ?? "";
        string current = root;
        foreach (string part in fullPath[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            // a link loop has no real path: stop resolving and compare what is left
            if (depth < 32 && info.Exists && info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                current = RealPath(target.FullName, depth + 1);
        }
        return current;
    }

    // Volume serial number and file index on Windows, or device and inode on Linux and macOS; null where the platform call is not available.
    private static (ulong Device, ulong Inode)? FileIdentity(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (OperatingSystem.IsWindows())
                return GetFileInformationByHandle(handle, out var info)
                    ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
                    : null;
            if (OperatingSystem.IsLinux())
                return LinuxFileIdentity(handle);
            if (OperatingSystem.IsMacOS())
                return DarwinFileIdentity(handle);
            return null;
        }
        // a file that cannot be opened or queried is compared by path
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static (ulong Device, ulong Inode)? LinuxFileIdentity(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        const int AtEmptyPath = 0x1000;
        const uint StatxIno = 0x00000100u;
        byte emptyPath = 0;
        byte[] buffer = new byte[256];
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (LinuxStatx((int)handle.DangerousGetHandle(), ref emptyPath, AtEmptyPath, StatxIno, ref buffer[0]) != 0)
                return null;
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
        uint mask = BitConverter.ToUInt32(buffer, 0);
        if ((mask & StatxIno) == 0) return null;
        ulong inode = BitConverter.ToUInt64(buffer, 32);
        uint devMajor = BitConverter.ToUInt32(buffer, 136);
        uint devMinor = BitConverter.ToUInt32(buffer, 140);
        return (((ulong)devMajor << 32) | devMinor, inode);
    }

    private static (ulong Device, ulong Inode)? DarwinFileIdentity(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        byte[] buffer = new byte[256];
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            int fd = (int)handle.DangerousGetHandle();
            int rc = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => DarwinFstatArm64(fd, ref buffer[0]),
                System.Runtime.InteropServices.Architecture.X64 => DarwinFstatInode64(fd, ref buffer[0]),
                _ => -1,
            };
            if (rc != 0) return null;
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
        // __DARWIN_STRUCT_STAT64: int32 st_dev at 0, uint16 st_mode at 4, uint16 st_nlink at 6, uint64 st_ino at 8.
        uint device = BitConverter.ToUInt32(buffer, 0);
        ulong inode = BitConverter.ToUInt64(buffer, 8);
        return (device, inode);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int LinuxStatx(int dirfd, ref byte pathname, int flags, uint mask, ref byte statxbuf);

    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static extern int DarwinFstatArm64(int fd, ref byte statbuf);

    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int DarwinFstatInode64(int fd, ref byte statbuf);

    // A stack trace names the source files of the build machine: keep the frames, drop the paths.
    internal static string StripSourcePaths(string trace) =>
        System.Text.RegularExpressions.Regex.Replace(trace, @" in [^\r\n]*?:line \d+", "");

    /// <summary>The report of a file that could not be read.</summary>
    public static ExportReport Failed(string file, long bytes, string kind, string message, int exitCode, RvtReadDiagnostics? diagnostics,
        bool raised, bool partialAllowed, string? detail = null) =>
        Failed(file, bytes, kind, message, exitCode, diagnostics, raised, partialAllowed, detail, strict: false);

    /// <summary>The report of a file that could not be read.</summary>
    public static ExportReport Failed(string file, long bytes, string kind, string message, int exitCode, RvtReadDiagnostics? diagnostics,
        bool raised, bool partialAllowed, string? detail, bool strict)
    {
        var report = new ExportReport { Status = "failed", ExitCode = exitCode };
        report.Input.File = file;
        report.Input.Bytes = bytes;
        report.Error = new ReportError { Kind = kind, Message = message, Detail = detail == null ? null : StripSourcePaths(detail) };
        report.Limits.Raised = raised;
        report.Limits.PartialAllowed = partialAllowed;
        report.Limits.Strict = strict;
        if (diagnostics != null) report.Fill(diagnostics);
        return report;
    }

    /// <summary>The report of a finished read. <paramref name="elementIds"/> are the rows of the element table.</summary>
    public static ExportReport ForRead(string file, long bytes, RvtReadDiagnostics diagnostics, bool raised, bool partialAllowed,
        IReadOnlyCollection<uint> elementIds, RevitFileInfo info) =>
        ForRead(file, bytes, diagnostics, raised, partialAllowed, elementIds, info, strict: false);

    /// <summary>The report of a finished read. <paramref name="elementIds"/> are the rows of the element table.</summary>
    public static ExportReport ForRead(string file, long bytes, RvtReadDiagnostics diagnostics, bool raised, bool partialAllowed,
        IReadOnlyCollection<uint> elementIds, RevitFileInfo info, bool strict)
    {
        var report = new ExportReport();
        report.Input.File = file;
        report.Input.Bytes = bytes;
        report.Limits.Raised = raised;
        report.Limits.PartialAllowed = partialAllowed;
        report.Limits.Strict = strict;
        report.Fill(diagnostics);
        // A row counts as decoded when a parsed record has its id; the discovered and block-only records have none in the table.
        var parsed = new HashSet<long>(info.Elements.Where(element => !element.IsBlockOnly).Select(element => (long)(uint)element.Id));
        int decoded = elementIds.Count(id => parsed.Contains(id));
        var undecoded = new SkipLog();
        foreach (uint id in elementIds)
            if (!parsed.Contains(id)) undecoded.Add((int)id, "unknown", "row of the element table without a parsed record");
        report.Elements = new ReportElements
        {
            InTable = elementIds.Count,
            Decoded = decoded,
            DecodedFraction = Fraction(decoded, elementIds.Count),
            Undecoded = undecoded.Groups(),
        };
        foreach (var stream in report.Streams)
        {
            if (stream.Failed > 0)
                report.Issues.Add(new ReportIssue { Stream = stream.Name, Code = "failed-members", Detail = $"{stream.Failed} of {stream.Members} member(s) failed to decode" });
            if (stream.Recovered > 0)
                report.Issues.Add(new ReportIssue { Stream = stream.Name, Code = "recovered-members", Detail = $"{stream.Recovered} of {stream.Members} member(s) rebuilt by structural recovery" });
            int unverifiedWithoutRecovery = stream.Unverified - stream.Recovered;
            if (unverifiedWithoutRecovery > 0)
                report.Issues.Add(new ReportIssue { Stream = stream.Name, Code = "unverified-members", Detail = $"{unverifiedWithoutRecovery} of {stream.Members} member(s) decoded without a matching checksum" });
        }
        // A project always has elements: a table without a row did not decode, whatever the streams say.
        if (elementIds.Count == 0)
            report.Issues.Add(new ReportIssue { Stream = "Global/ElemTable", Code = "no-rows", Detail = "the element table decoded no row" });
        bool lostMembers = report.Streams.Any(stream => stream.Failed > 0 || stream.Unverified > 0 || stream.Recovered > 0);
        bool isPartial = diagnostics.LimitHits.Count > 0 || lostMembers || report.Issues.Count > 0;
        report.Status = isPartial ? "partial" : "complete";
        report.ExitCode = RvtExitCodes.ForRead(diagnostics, partialAllowed, strict, isPartial);
        return report;
    }

    private void Fill(RvtReadDiagnostics diagnostics)
    {
        Limits.Hit = diagnostics.LimitHits.ToList();
        Streams = diagnostics.Streams.Select(stream => new ReportStream
        {
            Name = stream.Name, RawBytes = stream.RawBytes, Members = stream.Members, Decoded = stream.Decoded,
            Failed = stream.Failed, Recovered = stream.Recovered, Unverified = stream.Unverified,
            DecodedFraction = Fraction(stream.Decoded, stream.Members),
        }).ToList();
    }

    /// <summary>Adds the export part. <paramref name="skipped"/> holds the elements the export left out.</summary>
    public void SetExport(string format, int exported, SkipLog skipped)
    {
        int considered = exported + skipped.Total;
        Export = new ReportExport
        {
            Format = format, Considered = considered, Exported = exported, Skipped = skipped.Total,
            ExportedFraction = Fraction(exported, considered), SkippedByClass = skipped.Groups(),
        };
    }

    /// <summary>The export part of a JSON run: the compact BM export writes the rows of the element table
    /// (<c>CompactUniverse</c>), the full dump writes every parsed element.</summary>
    internal void SetJsonExport(RevitFileInfo info, bool full)
    {
        var skipped = new SkipLog();
        int exported = 0;
        foreach (var element in info.Elements)
        {
            if (full || Compact.CompactUniverse.IsTableRow(element, info.ElemTableIsExact)) exported++;
            else skipped.Add(element.Id, SkipLog.ClassOf(element), element.IsBlockOnly ? "block-only-record" : "record-outside-exact-element-table");
        }
        SetExport(full ? "json-full" : "json", exported, skipped);
    }

    private static double Fraction(int part, int whole) => whole == 0 ? 1.0 : Math.Round((double)part / whole, 6);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The report as indented UTF-8 JSON text.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>Elements an export left out, counted by class and reason. Not thread safe.</summary>
public sealed class SkipLog
{
    /// <summary>Element ids kept per class and reason.</summary>
    public const int ExampleLimit = 5;

    private readonly List<(int Id, string Class, string Reason)> _entries = new();

    /// <summary>Elements left out.</summary>
    public int Total => _entries.Count;

    /// <summary>Records that the element was left out.</summary>
    public void Add(int elementId, string elementClass, string reason) => _entries.Add((elementId, elementClass, reason));

    /// <summary>Takes the element back (the export wrote it after all): its latest entry. False when it had none.</summary>
    public bool Remove(int elementId)
    {
        int index = _entries.FindLastIndex(entry => entry.Id == elementId);
        if (index >= 0) _entries.RemoveAt(index);
        return index >= 0;
    }

    /// <summary>The skipped elements grouped by class and reason, largest first, then by class and reason.</summary>
    public List<ExportReport.ReportSkipped> Groups() => _entries
        .GroupBy(entry => (entry.Class, entry.Reason))
        .Select(group => new ExportReport.ReportSkipped
        {
            Class = group.Key.Class, Reason = group.Key.Reason, Count = group.Count(),
            Examples = group.Select(entry => entry.Id).Order().Take(ExampleLimit).ToList(),
        })
        .OrderByDescending(group => group.Count).ThenBy(group => group.Class, StringComparer.Ordinal).ThenBy(group => group.Reason, StringComparer.Ordinal)
        .ToList();

    /// <summary>The class name a report uses for an element: its category, else its root class, else <c>unknown</c>.</summary>
    internal static string ClassOf(Element element) => element.Category ?? element.RootClassName ?? "unknown";
}
