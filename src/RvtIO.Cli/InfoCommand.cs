using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Cli;

/// <summary>
/// <c>rvtio info &lt;file-or-dir&gt; [output] [--format text|json|jsonl|csv] [--csv] [--jsonl] [--json] [--text]
/// [--output &lt;path&gt;] [--recursive|--no-recursive] [--redact]</c>:
/// reads container metadata without reading <c>Partitions/*</c> or decoding geometry, supporting single files and recursive folder inventories.
/// Exit codes: 0 ok, 1 usage or output write error, 2 input/container error (including folders where any file failed), 3 reader limit exceeded.
/// </summary>
internal static partial class InfoCommand
{
    private static readonly string[] RevitExtensions = [".rvt", ".rfa", ".rte", ".rft"];

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"^(?<stem>.+)\.(?<num>\d{4})\.(?<ext>rvt|rfa|rte|rft)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BackupNamePattern();

    private enum OutputFormat
    {
        Text,
        Json,
        Jsonl,
        Csv,
    }

    internal sealed class InventoryRow
    {
        public string? Path { get; init; }
        public string? FileName { get; init; }
        public string Status { get; init; } = "ok";
        public string? ErrorKind { get; init; }
        public string? Error { get; init; }
        public string FileType { get; init; } = "unknown";
        public string DocumentKind { get; init; } = nameof(RvtDocumentKind.Unknown);
        public bool IsTemplate { get; init; }
        public bool IsBackup { get; init; }
        public int? BackupNumber { get; init; }
        public long? FileSizeBytes { get; init; }
        public string? RevitVersion { get; init; }
        public string? Build { get; init; }
        public string? FullBuild { get; init; }
        public string? ClientAppName { get; init; }
        public string? StreamVersion { get; init; }
        public string? Worksharing { get; init; }
        public bool? IsWorkshared { get; init; }
        public bool? IsCloudModel { get; init; }
        public string? SavedLocale { get; init; }
        public string? LastSavedUtc { get; init; }
        public string? LastSavePath { get; init; }
        public string? CentralModelPath { get; init; }
        public string? Username { get; init; }
        public string? Author { get; init; }
        public string? UniqueDocumentGuid { get; init; }
        public int? UniqueDocumentIncrements { get; init; }
        public string? CentralModelIdentity { get; init; }
        public string? ModelIdentity { get; init; }
        public int? CentralModelVersion { get; init; }
        public string? CentralModelEpisodeGuid { get; init; }
        public bool? AllLocalChangesSavedToCentral { get; init; }
        public string? OpenWorksetDefault { get; init; }
        public bool? IsProjectSparkFile { get; init; }
        public string? ProjectName { get; init; }
        public string? ProjectNumber { get; init; }
        public string? Title { get; init; }
        public int? ExternalReferenceCount { get; init; }
        public bool? IsTransmitted { get; init; }
        public bool? HasPreviewImage { get; init; }
        public string? PreviewImageMimeType { get; init; }
        public int? StreamCount { get; init; }
        public int? PartitionCount { get; init; }
        public ProjectInfo? ProjectInfo { get; init; }
        public TransmissionInfo? TransmissionData { get; init; }
        public IReadOnlyDictionary<string, string>? BasicFileInfoFields { get; init; }
    }

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? targetPath = null, outputPath = null;
        OutputFormat? format = null;
        bool recursive = true;
        bool redact = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--csv":
                    format = OutputFormat.Csv;
                    break;
                case "--jsonl":
                    format = OutputFormat.Jsonl;
                    break;
                case "--json":
                    format = OutputFormat.Json;
                    break;
                case "--text":
                    format = OutputFormat.Text;
                    break;
                case "--recursive":
                    recursive = true;
                    break;
                case "--no-recursive" or "--no-recurse":
                    recursive = false;
                    break;
                case "--redact":
                    redact = true;
                    break;
                case "--format" or "--output" or "-o":
                    if (i + 1 >= args.Length) return Usage(error, $"{arg} requires a value.");
                    string value = args[++i];
                    if (arg == "--format")
                    {
                        if (!TryParseFormat(value, out var parsed))
                            return Usage(error, $"Unknown format '{value}'. Expected text, json, jsonl, or csv.");
                        format = parsed;
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(value)) return Usage(error, $"{arg} requires a non-empty path.");
                        outputPath = value;
                    }
                    break;
                default:
                    if (arg.StartsWith("--format=", StringComparison.Ordinal))
                    {
                        string valueEq = arg["--format=".Length..];
                        if (!TryParseFormat(valueEq, out var parsedEq))
                            return Usage(error, $"Unknown format '{valueEq}'. Expected text, json, jsonl, or csv.");
                        format = parsedEq;
                        break;
                    }
                    if (arg.StartsWith("--output=", StringComparison.Ordinal))
                    {
                        string outEq = arg["--output=".Length..];
                        if (string.IsNullOrWhiteSpace(outEq)) return Usage(error, "--output requires a non-empty path.");
                        outputPath = outEq;
                        break;
                    }
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                        return Usage(error, $"Unknown option for info: {arg}.");
                    if (targetPath == null) targetPath = arg;
                    else if (outputPath == null) outputPath = arg;
                    else return Usage(error, "info takes one file or folder path and at most one output path.");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(targetPath))
            return Usage(error, "info requires a Revit file or folder path.");

        OutputFormat effectiveFormat = format ?? OutputFormat.Text;
        var metadataOptions = new RvtMetadataOptions { RedactPersonalData = redact };
        var reader = new RvtFileReader();

        if (outputPath != null && File.Exists(targetPath) && FileIdentity.SameFile(targetPath, outputPath))
        {
            error.WriteLine("The output path is the input file. Nothing was read or written.");
            return RvtExitCodes.Usage;
        }

        if (Directory.Exists(targetPath))
            return RunDirectory(targetPath, outputPath, effectiveFormat, recursive, reader, metadataOptions, output, error);

        if (File.Exists(targetPath))
            return RunSingleFile(targetPath, outputPath, effectiveFormat, reader, metadataOptions, output, error);

        string missingMsg = $"'{targetPath}' does not exist.";
        if (redact) missingMsg = RvtPiiRedactor.RedactText(missingMsg, metadataOptions.RedactionToken, RvtPiiRedactor.CollectSensitiveTokens(targetPath)) ?? missingMsg;
        error.WriteLine($"Cannot read {Path.GetFileName(targetPath)}: {missingMsg}");
        return RvtExitCodes.InputError;
    }

    private static int RunSingleFile(
        string filePath,
        string? outputPath,
        OutputFormat format,
        RvtFileReader reader,
        RvtMetadataOptions options,
        TextWriter output,
        TextWriter error)
    {
        RvtFileMetadataSummary summary;
        try
        {
            summary = reader.ReadMetadata(filePath, options);
        }
        catch (RvtInputException exception)
        {
            string msg = options.RedactPersonalData
                ? RvtPiiRedactor.RedactText(exception.Message, options.RedactionToken, RvtPiiRedactor.CollectSensitiveTokens(filePath)) ?? exception.Message
                : exception.Message;
            error.WriteLine($"Cannot read {Path.GetFileName(filePath)}: {msg}");
            return RvtExitCodes.InputError;
        }
        catch (RvtLimitException exception)
        {
            string msg = options.RedactPersonalData
                ? RvtPiiRedactor.RedactText(exception.Message, options.RedactionToken, RvtPiiRedactor.CollectSensitiveTokens(filePath)) ?? exception.Message
                : exception.Message;
            error.WriteLine($"Cannot read {Path.GetFileName(filePath)}: {msg}");
            return RvtExitCodes.LimitExceeded;
        }

        var row = FromSummary(summary);
        string rendered = format switch
        {
            OutputFormat.Csv => RenderCsv([row]),
            OutputFormat.Jsonl => RenderJsonl([row]),
            OutputFormat.Json => JsonSerializer.Serialize(row, IndentedJsonOptions),
            _ => RenderSingleText(row),
        };

        return WriteOutput(rendered, outputPath, output, error, RvtExitCodes.Success);
    }

    private static int RunDirectory(
        string directoryPath,
        string? outputPath,
        OutputFormat format,
        bool recursive,
        RvtFileReader reader,
        RvtMetadataOptions options,
        TextWriter output,
        TextWriter error)
    {
        var files = new List<string>();
        var enumerationErrors = new List<string>();
        CollectRevitFiles(directoryPath, recursive, files, enumerationErrors);
        files.Sort(StringComparer.OrdinalIgnoreCase);

        var rows = new List<InventoryRow>(files.Count);
        bool hasInputError = enumerationErrors.Count > 0;
        bool hasLimitError = false;

        foreach (string enumErr in enumerationErrors)
        {
            string msg = options.RedactPersonalData
                ? RvtPiiRedactor.RedactText(enumErr, options.RedactionToken, RvtPiiRedactor.CollectSensitiveTokens(directoryPath)) ?? enumErr
                : enumErr;
            error.WriteLine(msg);
        }

        foreach (string file in files)
        {
            if (outputPath != null && FileIdentity.SameFile(file, outputPath)) continue;
            try
            {
                var summary = reader.ReadMetadata(file, options);
                rows.Add(FromSummary(summary));
            }
            catch (RvtInputException exception)
            {
                hasInputError = true;
                rows.Add(ErrorRow(file, exception.Kind, exception.Message, options));
            }
            catch (RvtLimitException exception)
            {
                hasLimitError = true;
                rows.Add(ErrorRow(file, "limit-exceeded", exception.Message, options));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                hasInputError = true;
                rows.Add(ErrorRow(file, "cannot-open", exception.Message, options));
            }
        }

        int failed = rows.Count(r => r.Status != "ok");
        int succeeded = rows.Count - failed;
        string displayDir = options.RedactPersonalData
            ? RvtPiiRedactor.RedactText(directoryPath, options.RedactionToken, RvtPiiRedactor.CollectSensitiveTokens(directoryPath)) ?? directoryPath
            : directoryPath;

        string rendered = format switch
        {
            OutputFormat.Csv => RenderCsv(rows),
            OutputFormat.Jsonl => RenderJsonl(rows),
            OutputFormat.Json => JsonSerializer.Serialize(new
            {
                directory = displayDir,
                recursive,
                total = rows.Count,
                succeeded,
                failed,
                files = rows,
            }, IndentedJsonOptions),
            _ => RenderDirectoryText(displayDir, rows, succeeded, failed),
        };

        int finalExit = hasInputError
            ? RvtExitCodes.InputError
            : hasLimitError ? RvtExitCodes.LimitExceeded : RvtExitCodes.Success;

        int writeResult = WriteOutput(rendered, outputPath, output, error, finalExit);
        if (writeResult != finalExit) return writeResult;

        if (rows.Count == 0)
            error.WriteLine($"[info] 0 Revit files (.rvt, .rfa, .rte, .rft) found in {displayDir}");
        else if (failed > 0)
            error.WriteLine($"[info] {rows.Count} file(s): {succeeded} ok, {failed} error(s)");

        return finalExit;
    }

    private static void CollectRevitFiles(string rootDir, bool recursive, List<string> results, List<string> errors)
    {
        var pending = new Stack<string>();
        pending.Push(rootDir);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFiles(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Cannot list directory '{current}': {ex.Message}");
                continue;
            }

            foreach (string file in entries)
            {
                string ext = System.IO.Path.GetExtension(file);
                if (RevitExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    results.Add(file);
            }

            if (!recursive) continue;
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(current))
                    pending.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Cannot list subdirectories of '{current}': {ex.Message}");
            }
        }
    }

    private static InventoryRow FromSummary(RvtFileMetadataSummary s) => new()
    {
        Path = s.FilePath,
        FileName = s.FileName,
        Status = "ok",
        FileType = s.FileType,
        DocumentKind = s.DocumentKind.ToString(),
        IsTemplate = s.IsTemplate,
        IsBackup = s.IsBackup,
        BackupNumber = s.BackupNumber,
        FileSizeBytes = s.FileSizeBytes,
        RevitVersion = s.RevitVersion,
        Build = s.Build,
        FullBuild = s.FullBuild,
        ClientAppName = s.ClientAppName,
        StreamVersion = s.StreamVersion,
        Worksharing = s.Worksharing,
        IsWorkshared = s.IsWorkshared,
        IsCloudModel = s.IsCloudModel,
        SavedLocale = s.SavedLocale,
        LastSavedUtc = s.LastSavedUtc,
        LastSavePath = s.LastSavePath,
        CentralModelPath = s.CentralModelPath,
        Username = s.Username,
        Author = s.Author,
        UniqueDocumentGuid = s.UniqueDocumentGuid,
        UniqueDocumentIncrements = s.UniqueDocumentIncrements,
        CentralModelIdentity = s.CentralModelIdentity,
        ModelIdentity = s.ModelIdentity,
        CentralModelVersion = s.CentralModelVersion,
        CentralModelEpisodeGuid = s.CentralModelEpisodeGuid,
        AllLocalChangesSavedToCentral = s.AllLocalChangesSavedToCentral,
        OpenWorksetDefault = s.OpenWorksetDefault,
        IsProjectSparkFile = s.IsProjectSparkFile,
        ProjectName = s.ProjectInfo.Name,
        ProjectNumber = s.ProjectInfo.Number,
        Title = s.ProjectInfo.Title,
        ExternalReferenceCount = s.ExternalReferenceCount,
        IsTransmitted = s.IsTransmitted,
        HasPreviewImage = s.HasPreviewImage,
        PreviewImageMimeType = s.PreviewImageMimeType,
        StreamCount = s.StreamCount,
        PartitionCount = s.PartitionCount,
        ProjectInfo = s.ProjectInfo,
        TransmissionData = s.TransmissionData,
        BasicFileInfoFields = s.BasicFileInfoFields,
    };

    private static InventoryRow ErrorRow(string filePath, string kind, string message, RvtMetadataOptions options)
    {
        string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        string fileType = ext.TrimStart('.');
        if (string.IsNullOrEmpty(fileType)) fileType = "unknown";
        var docKind = ext switch
        {
            ".rvt" => RvtDocumentKind.Project,
            ".rfa" => RvtDocumentKind.Family,
            ".rte" => RvtDocumentKind.ProjectTemplate,
            ".rft" => RvtDocumentKind.FamilyTemplate,
            _ => RvtDocumentKind.Unknown,
        };
        bool isTemplate = docKind is RvtDocumentKind.ProjectTemplate or RvtDocumentKind.FamilyTemplate;
        string fileName = System.IO.Path.GetFileName(filePath);
        bool isBackup = false;
        int? backupNumber = null;
        if (BackupNamePattern().Match(fileName) is { Success: true } match
            && int.TryParse(match.Groups["num"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int num))
        {
            isBackup = true;
            backupNumber = num;
        }
        long? length = null;
        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Exists) length = fi.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }

        string? outPath = filePath;
        string? outName = fileName;
        string outMessage = message;
        if (options.RedactPersonalData)
        {
            var tokens = RvtPiiRedactor.CollectSensitiveTokens(filePath, message);
            outPath = RvtPiiRedactor.RedactText(filePath, options.RedactionToken, tokens);
            outName = RvtPiiRedactor.RedactText(fileName, options.RedactionToken, tokens);
            outMessage = RvtPiiRedactor.RedactText(message, options.RedactionToken, tokens) ?? options.RedactionToken;
        }

        return new InventoryRow
        {
            Path = outPath,
            FileName = outName,
            Status = "error",
            ErrorKind = kind,
            Error = outMessage,
            FileType = fileType,
            DocumentKind = docKind.ToString(),
            IsTemplate = isTemplate,
            IsBackup = isBackup,
            BackupNumber = backupNumber,
            FileSizeBytes = length,
        };
    }

    private static string RenderCsv(IReadOnlyList<InventoryRow> rows)
    {
        var lines = new List<string>(rows.Count + 1)
        {
            string.Join(",",
                "path", "fileName", "status", "errorKind", "error",
                "fileType", "documentKind", "isTemplate", "isBackup", "backupNumber", "fileSizeBytes",
                "revitVersion", "build", "worksharing", "isWorkshared", "isCloudModel",
                "savedLocale", "streamVersion", "lastSavedUtc", "lastSavePath", "centralModelPath",
                "username", "author", "uniqueDocumentGuid", "uniqueDocumentIncrements",
                "centralModelIdentity", "modelIdentity", "centralModelVersion", "centralModelEpisodeGuid",
                "allLocalChangesSavedToCentral", "openWorksetDefault",
                "projectName", "projectNumber", "title", "externalReferenceCount", "isTransmitted",
                "hasPreviewImage", "previewImageMimeType")
        };

        foreach (var r in rows)
        {
            var fields = new string?[]
            {
                r.Path,
                r.FileName,
                r.Status,
                r.ErrorKind,
                r.Error,
                r.FileType,
                r.DocumentKind,
                FormatBool(r.Status == "ok" || r.DocumentKind != nameof(RvtDocumentKind.Unknown) ? r.IsTemplate : null),
                FormatBool(r.Status == "ok" || r.IsBackup ? r.IsBackup : null),
                r.BackupNumber?.ToString(CultureInfo.InvariantCulture),
                r.FileSizeBytes?.ToString(CultureInfo.InvariantCulture),
                r.RevitVersion,
                r.Build,
                r.Worksharing,
                FormatBool(r.IsWorkshared),
                FormatBool(r.IsCloudModel),
                r.SavedLocale,
                r.StreamVersion,
                r.LastSavedUtc,
                r.LastSavePath,
                r.CentralModelPath,
                r.Username,
                r.Author,
                r.UniqueDocumentGuid,
                r.UniqueDocumentIncrements?.ToString(CultureInfo.InvariantCulture),
                r.CentralModelIdentity,
                r.ModelIdentity,
                r.CentralModelVersion?.ToString(CultureInfo.InvariantCulture),
                r.CentralModelEpisodeGuid,
                FormatBool(r.AllLocalChangesSavedToCentral),
                r.OpenWorksetDefault,
                r.ProjectName,
                r.ProjectNumber,
                r.Title,
                r.ExternalReferenceCount?.ToString(CultureInfo.InvariantCulture),
                FormatBool(r.IsTransmitted),
                FormatBool(r.HasPreviewImage),
                r.PreviewImageMimeType,
            };
            lines.Add(string.Join(",", fields.Select(EscapeCsv)));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderJsonl(IReadOnlyList<InventoryRow> rows)
    {
        if (rows.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append(JsonSerializer.Serialize(rows[i], CompactJsonOptions));
        }
        return sb.ToString();
    }

    private static string RenderSingleText(InventoryRow r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File:         {r.Path ?? r.FileName ?? "(stream)"}");
        sb.AppendLine($"Type:         {r.DocumentKind} ({r.FileType}){(r.IsTemplate ? " [template]" : "")}{(r.IsBackup ? $" [backup #{r.BackupNumber}]" : "")}");
        sb.AppendLine($"Revit:        {r.RevitVersion ?? "unknown"}{(r.Build != null ? $" ({r.Build})" : "")}");
        sb.AppendLine($"Worksharing:  {r.Worksharing ?? "unknown"} (workshared={FormatUnknownBool(r.IsWorkshared)}, cloud={FormatUnknownBool(r.IsCloudModel)})");
        if (r.SavedLocale != null) sb.AppendLine($"Locale:       {r.SavedLocale}");
        if (r.LastSavedUtc != null) sb.AppendLine($"Saved UTC:    {r.LastSavedUtc}");
        if (r.UniqueDocumentIncrements != null || r.UniqueDocumentGuid != null)
            sb.AppendLine($"Increments:   {r.UniqueDocumentIncrements?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} (GUID: {r.UniqueDocumentGuid ?? "unknown"})");
        if (r.LastSavePath != null) sb.AppendLine($"Last Save:    {r.LastSavePath}");
        if (r.CentralModelPath != null) sb.AppendLine($"Central Path: {r.CentralModelPath}");
        if (r.Username != null) sb.AppendLine($"Username:     {r.Username}");
        if (r.Author != null) sb.AppendLine($"Author:       {r.Author}");
        if (r.ProjectName != null || r.ProjectNumber != null)
            sb.AppendLine($"Project:      {r.ProjectName ?? ""}{(r.ProjectNumber != null ? $" [{r.ProjectNumber}]" : "")}");
        if (r.ExternalReferenceCount != null)
            sb.AppendLine($"Ext Refs:     {r.ExternalReferenceCount} (transmitted={FormatUnknownBool(r.IsTransmitted)})");
        sb.AppendLine($"Streams:      {r.StreamCount} total, {r.PartitionCount} partition(s)");
        return sb.ToString().TrimEnd();
    }

    private static string RenderDirectoryText(string directory, IReadOnlyList<InventoryRow> rows, int succeeded, int failed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Inventory: {directory} ({rows.Count} file(s), {succeeded} ok, {failed} error(s))");
        foreach (var r in rows)
        {
            if (r.Status == "ok")
            {
                string ws = r.Worksharing ?? "unknown";
                string ver = r.RevitVersion ?? "unknown";
                string flags = $"{r.FileType}{(r.IsBackup ? $" backup#{r.BackupNumber}" : "")}";
                sb.AppendLine($"[ok]  {r.Path}\t{ver}\t{flags}\t{ws}\t{r.Build ?? ""}");
            }
            else
            {
                sb.AppendLine($"[err] {r.Path}\t{r.FileType}\t{r.ErrorKind}: {r.Error}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static int WriteOutput(string payload, string? outputPath, TextWriter output, TextWriter error, int exitCode)
    {
        if (outputPath == null)
        {
            if (payload.Length > 0) output.WriteLine(payload);
            return exitCode;
        }

        string temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string? parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            string text = payload.Length > 0 ? payload + Environment.NewLine : string.Empty;
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, outputPath, overwrite: true);
            return exitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            error.WriteLine($"Cannot write {outputPath}: {exception.Message}");
            return RvtExitCodes.Usage;
        }
    }

    private static bool TryParseFormat(string value, out OutputFormat format)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "text": format = OutputFormat.Text; return true;
            case "json": format = OutputFormat.Json; return true;
            case "jsonl": format = OutputFormat.Jsonl; return true;
            case "csv": format = OutputFormat.Csv; return true;
            default: format = OutputFormat.Text; return false;
        }
    }

    private static string? FormatBool(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => null,
    };

    private static string FormatUnknownBool(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => "unknown",
    };

    private static string EscapeCsv(string? field)
    {
        field ??= string.Empty;
        if (field.Contains('"') || field.Contains(',') || field.Contains('\n') || field.Contains('\r'))
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }

    private static int Usage(TextWriter error, string problem)
    {
        error.WriteLine(problem);
        error.WriteLine("Usage: rvtio info <file-or-dir> [output] [--format text|json|jsonl|csv] [--csv|--jsonl|--json] [--output <path>] [--no-recursive] [--redact]");
        return RvtExitCodes.Usage;
    }
}
