using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO;

public partial class RvtFileReader
{
    [GeneratedRegex(@"^(?<stem>.+)\.(?<num>\d{4})\.(?<ext>rvt|rfa|rte|rft)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BackupFilePattern();

    /// <summary>
    /// Reads only container metadata streams (<c>BasicFileInfo</c>, <c>ProjectInformation</c>, <c>PartAtom</c>,
    /// <c>TransmissionData</c>, and <c>RevitPreview4.0</c>) from <paramref name="rvtPath"/> without reading
    /// <c>Partitions/*</c> or decoding geometry.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="rvtPath"/> is null.</exception>
    /// <exception cref="RvtInputException">The file is missing, empty, not a Revit compound file, or a metadata stream is cut or damaged.</exception>
    /// <exception cref="RvtLimitException">A metadata stream exceeds the maximum single-array byte length.</exception>
    public RvtFileMetadataSummary ReadMetadata(string rvtPath, RvtMetadataOptions? metadataOptions = null)
    {
        ArgumentNullException.ThrowIfNull(rvtPath);
        FileInfo info;
        try { info = new FileInfo(rvtPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new RvtInputException("cannot-open", $"'{rvtPath}' is not a usable file path.", ex); }
        if (!info.Exists) throw new RvtInputException("cannot-open", $"'{info.Name}' does not exist.");
        if (info.Length == 0) throw new RvtInputException("empty-file", $"'{info.Name}' is empty.");

        FileStream file;
        try { file = new FileStream(rvtPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new RvtInputException("cannot-open", $"'{info.Name}' cannot be opened ({ex.GetType().Name}); the file may be in use or not readable.", ex); }

        using (file)
        {
            var effectiveOptions = metadataOptions ?? new RvtMetadataOptions();
            string pathHint = string.IsNullOrWhiteSpace(effectiveOptions.SourcePath) ? rvtPath : effectiveOptions.SourcePath!;
            return ReadMetadataCore(file, info.Name, pathHint, info.Length, effectiveOptions, surfaceStreamFailures: false);
        }
    }

    /// <summary>
    /// Reads only container metadata streams from a readable, seekable <paramref name="stream"/> without reading
    /// <c>Partitions/*</c> or decoding geometry. The stream is never modified or closed.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable or not seekable, or is closed.</exception>
    /// <exception cref="RvtInputException">The stream is empty, is not a Revit compound file, or a metadata stream is cut or damaged.</exception>
    /// <exception cref="RvtLimitException">A metadata stream exceeds the maximum single-array byte length.</exception>
    public RvtFileMetadataSummary ReadMetadataStream(Stream stream, RvtMetadataOptions? metadataOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The Revit input stream must be readable and seekable.", nameof(stream));
        var effectiveOptions = metadataOptions ?? new RvtMetadataOptions();
        string? pathHint = effectiveOptions.SourcePath;
        string displayName = string.IsNullOrWhiteSpace(pathHint) ? "Revit input stream" : Path.GetFileName(pathHint);
        long? length = null;
        try { length = stream.Length; } catch (ObjectDisposedException) { }
        return ReadMetadataCore(stream, displayName, pathHint, length, effectiveOptions, surfaceStreamFailures: true);
    }

    private static RvtFileMetadataSummary ReadMetadataCore(
        Stream stream,
        string displayName,
        string? pathHint,
        long? fileSizeBytes,
        RvtMetadataOptions options,
        bool surfaceStreamFailures)
    {
        if (stream.Length == 0)
            throw new RvtInputException("empty-file", $"'{displayName}' is empty.");
        stream.Position = 0;

        var view = new ReadOnlyStreamView(stream);
        RootStorage root;
        try
        {
            root = RootStorage.Open(view, StorageModeFlags.LeaveOpen);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (surfaceStreamFailures) view.ThrowStreamFailure();
            throw new RvtInputException(
                "not-a-compound-file",
                $"'{displayName}' is not a Revit file: its compound file header or directory cannot be read ({ex.Message}). The file may be cut or damaged.",
                ex);
        }

        using (root)
        {
            try
            {
                var (streamCount, partitionCount, topEntries) = ScanMetadataDirectory(root, displayName);
                bool hasBasicFileInfo = topEntries.Contains("BasicFileInfo");
                bool hasProjectInformation = topEntries.Contains("ProjectInformation");
                bool hasPartAtom = topEntries.Contains("PartAtom");
                bool hasTransmissionData = topEntries.Contains("TransmissionData");
                bool hasPreviewStream = topEntries.Contains("RevitPreview4.0");
                bool hasRevitSignature = hasBasicFileInfo
                    || hasProjectInformation
                    || hasPartAtom
                    || hasTransmissionData
                    || hasPreviewStream
                    || topEntries.Contains("Formats")
                    || topEntries.Contains("Global")
                    || topEntries.Contains("Partitions");

                if (!hasRevitSignature)
                {
                    throw new RvtInputException(
                        "not-a-revit-file",
                        $"'{displayName}' is a compound file, but contains no recognised Revit streams.");
                }

                Dictionary<string, string> bfi = hasBasicFileInfo
                    ? BasicFileInfoReader.Parse(ReadMetadataStreamBytes(root, "BasicFileInfo", displayName))
                    : new Dictionary<string, string>();

                var projectFields = new Dictionary<string, string>();
                if (hasProjectInformation)
                {
                    byte[] piBytes = ReadMetadataStreamBytes(root, "ProjectInformation", displayName);
                    projectFields = ProjectInfoReader.Parse(piBytes);
                }

                var partAtomFields = new Dictionary<string, string>();
                if (hasPartAtom)
                {
                    byte[] paBytes = ReadMetadataStreamBytes(root, "PartAtom", displayName);
                    string xml = Encoding.UTF8.GetString(paBytes);
                    ProjectInfoReader.ExtractAtomFields(xml, partAtomFields);
                }

                TransmissionInfo? transmissionData = null;
                if (hasTransmissionData)
                {
                    byte[] tdBytes = ReadMetadataStreamBytes(root, "TransmissionData", displayName);
                    transmissionData = TransmissionDataReader.Parse(tdBytes);
                }

                string? previewBase64 = null;
                string? previewMime = null;
                if (hasPreviewStream)
                {
                    byte[] previewBytes = ReadMetadataStreamBytes(root, "RevitPreview4.0", displayName);
                    (previewBase64, previewMime) = ParsePreviewSignature(previewBytes, options.IncludePreviewImageBytes);
                }

                if (surfaceStreamFailures) view.ThrowStreamFailure();

                string? lastSavePath = bfi.GetValueOrDefault("LastSavePath");
                string? rawExt = NormalizeExtension(Path.GetExtension(pathHint))
                    ?? NormalizeExtension(Path.GetExtension(lastSavePath));
                var (fileKind, docKind, fileType, isTemplate) = ClassifyDocument(hasPartAtom, hasProjectInformation, rawExt);

                string? fileName = string.IsNullOrEmpty(pathHint) ? null : Path.GetFileName(pathHint);
                bool isBackup = false;
                int? backupNumber = null;
                if (!string.IsNullOrEmpty(fileName) && BackupFilePattern().Match(fileName) is { Success: true } backupMatch
                    && int.TryParse(backupMatch.Groups["num"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int num))
                {
                    isBackup = true;
                    backupNumber = num;
                }

                string? worksharing = bfi.GetValueOrDefault("Worksharing");
                bool? isWorkshared = worksharing switch
                {
                    "Not enabled" => false,
                    "Local" or "Central" => true,
                    _ => ParseWorksharingLoose(worksharing),
                };

                string? centralModelPath = bfi.GetValueOrDefault("Central Model Path");
                bool? isCloudModel = ParseCloudModelState(bfi.GetValueOrDefault("IsSingleUserCloudModel"), centralModelPath);

                string? revitVersion = bfi.GetValueOrDefault("RevitVersion");
                string? build = bfi.GetValueOrDefault("Build");
                string? savedLocale = bfi.GetValueOrDefault("Locale when saved");
                string? streamVersion = bfi.GetValueOrDefault("StreamVersion");

                var fileMetadata = new FileMetadata
                {
                    StreamVersion = streamVersion,
                    RevitVersion = revitVersion,
                    Build = build,
                    LastSavePath = lastSavePath,
                    SavedLocale = savedLocale,
                    PreviewImage = previewBase64,
                    PreviewImageMimeType = previewMime,
                };

                var projectInfo = new ProjectInfo
                {
                    Name = projectFields.GetValueOrDefault("Name"),
                    Number = projectFields.GetValueOrDefault("Number"),
                    Client = projectFields.GetValueOrDefault("Client"),
                    Address = projectFields.GetValueOrDefault("Address"),
                    Status = projectFields.GetValueOrDefault("Status"),
                    IssueDate = projectFields.GetValueOrDefault("IssueDate"),
                    Title = projectFields.GetValueOrDefault("Title"),
                    RevitVersion = projectFields.GetValueOrDefault("RevitVersion"),
                    Author = projectFields.GetValueOrDefault("Author"),
                    BuildingName = projectFields.GetValueOrDefault("BuildingName"),
                    OrganizationName = projectFields.GetValueOrDefault("OrganizationName"),
                };

                string? lastSavedUtc = projectFields.GetValueOrDefault("LastSavedUtc")
                    ?? partAtomFields.GetValueOrDefault("LastSavedUtc");
                string? effectiveRevitVersion = revitVersion
                    ?? projectInfo.RevitVersion
                    ?? partAtomFields.GetValueOrDefault("RevitVersion");

                string? author = bfi.GetValueOrDefault("Author");
                if (string.IsNullOrWhiteSpace(author)) author = projectInfo.Author;

                var summary = new RvtFileMetadataSummary
                {
                    FilePath = pathHint,
                    FileName = fileName,
                    Extension = rawExt,
                    FileSizeBytes = fileSizeBytes,
                    FileKind = fileKind,
                    DocumentKind = docKind,
                    FileType = fileType,
                    IsTemplate = isTemplate,
                    IsBackup = isBackup,
                    BackupNumber = backupNumber,
                    IsStandaloneFamily = hasPartAtom,
                    RevitVersion = effectiveRevitVersion,
                    Build = build,
                    FullBuild = bfi.GetValueOrDefault("FullBuild"),
                    ClientAppName = bfi.GetValueOrDefault("ClientAppName"),
                    StreamVersion = streamVersion,
                    Worksharing = worksharing,
                    IsWorkshared = isWorkshared,
                    IsCloudModel = isCloudModel,
                    SavedLocale = savedLocale,
                    LastSavePath = lastSavePath,
                    CentralModelPath = centralModelPath,
                    Username = bfi.GetValueOrDefault("Username"),
                    Author = author,
                    LastSavedUtc = lastSavedUtc,
                    UniqueDocumentGuid = bfi.GetValueOrDefault("Unique Document GUID"),
                    UniqueDocumentIncrements = ParseOptionalInt(bfi.GetValueOrDefault("Unique Document Increments")),
                    CentralModelIdentity = bfi.GetValueOrDefault("Central Model Identity"),
                    ModelIdentity = bfi.GetValueOrDefault("Model Identity"),
                    CentralModelVersion = ParseOptionalInt(bfi.GetValueOrDefault("Central model's version number corresponding to the last reload latest")),
                    CentralModelEpisodeGuid = bfi.GetValueOrDefault("Central model's episode GUID corresponding to the last reload latest"),
                    AllLocalChangesSavedToCentral = ParseOptionalBoolBit(bfi.GetValueOrDefault("All Local Changes Saved To Central")),
                    OpenWorksetDefault = bfi.GetValueOrDefault("Open Workset Default"),
                    IsProjectSparkFile = ParseOptionalBoolBit(bfi.GetValueOrDefault("Project Spark File")),
                    Metadata = fileMetadata,
                    ProjectInfo = projectInfo,
                    TransmissionData = transmissionData,
                    HasBasicFileInfo = hasBasicFileInfo,
                    HasProjectInformation = hasProjectInformation,
                    HasPartAtom = hasPartAtom,
                    HasTransmissionData = hasTransmissionData,
                    HasPreviewImage = previewMime != null,
                    PreviewImageMimeType = previewMime,
                    ExternalReferenceCount = transmissionData?.ExternalReferences.Count,
                    IsTransmitted = transmissionData?.IsTransmitted,
                    StreamCount = streamCount,
                    PartitionCount = partitionCount,
                    BasicFileInfoFields = new Dictionary<string, string>(bfi, StringComparer.Ordinal),
                };

                return options.RedactPersonalData ? summary.Redact(options.RedactionToken) : summary;
            }
            catch (Exception ex) when (ex is not RvtInputException and not RvtLimitException and not OutOfMemoryException)
            {
                if (surfaceStreamFailures) view.ThrowStreamFailure();
                throw new RvtInputException(
                    "truncated-or-damaged",
                    $"'{displayName}' is cut or damaged: metadata cannot be read ({ex.Message}).",
                    ex);
            }
        }
    }

    // Enumerates directory metadata only: never opens Partitions/*, Global/*, or Formats/* streams.
    private static (int StreamCount, int PartitionCount, HashSet<string> TopEntries) ScanMetadataDirectory(RootStorage root, string displayName)
    {
        const int MaxDepth = 64;
        const int MaxEntries = 200_000;
        var topEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int streamCount = 0;
        int partitionCount = 0;
        int visited = 0;

        void Walk(Storage storage, string prefix, int depth)
        {
            if (depth > MaxDepth)
                throw new InvalidDataException($"storage nesting deeper than {MaxDepth} levels in '{displayName}'");
            foreach (var entry in storage.EnumerateEntries())
            {
                if (++visited > MaxEntries)
                    throw new InvalidDataException($"more than {MaxEntries} directory entries in '{displayName}'");
                if (depth == 0) topEntries.Add(entry.Name);
                if (entry.Type == EntryType.Storage)
                {
                    Walk(storage.OpenStorage(entry.Name), prefix + entry.Name + "/", depth + 1);
                }
                else if (entry.Type == EntryType.Stream)
                {
                    streamCount++;
                    if (prefix.Equals("Partitions/", StringComparison.OrdinalIgnoreCase))
                        partitionCount++;
                }
            }
        }

        Walk(root, "", 0);
        return (streamCount, partitionCount, topEntries);
    }

    private static byte[] ReadMetadataStreamBytes(RootStorage root, string streamName, string displayName)
    {
        using var stream = root.OpenStream(streamName);
        if (stream.Length > Array.MaxLength)
            throw new RvtLimitException(new ReaderLimitHit(streamName, "array-length", Array.MaxLength, Raisable: false,
                $"stream '{streamName}' in '{displayName}' exceeds maximum array length"));
        int length = checked((int)stream.Length);
        var data = new byte[length];
        try
        {
            stream.ReadExactly(data);
        }
        catch (EndOfStreamException ex)
        {
            throw new RvtInputException("truncated-or-damaged",
                $"'{displayName}' is cut or damaged: stream '{streamName}' ends early ({ex.Message}).", ex);
        }
        return data;
    }

    private static (string? base64Data, string? mimeType) ParsePreviewSignature(byte[] raw, bool includeBase64)
    {
        for (int i = 0; i < raw.Length - 8; i++)
        {
            if (raw[i] == 0x89 && raw[i + 1] == 0x50 && raw[i + 2] == 0x4E && raw[i + 3] == 0x47)
                return (includeBase64 ? Convert.ToBase64String(raw.AsSpan(i)) : null, "image/png");
        }
        for (int i = 0; i < raw.Length - 3; i++)
        {
            if (raw[i] == 0xFF && raw[i + 1] == 0xD8 && raw[i + 2] == 0xFF)
                return (includeBase64 ? Convert.ToBase64String(raw.AsSpan(i)) : null, "image/jpeg");
        }
        return (null, null);
    }

    private static string? NormalizeExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return null;
        string lower = ext.Trim().ToLowerInvariant();
        return lower is ".rvt" or ".rfa" or ".rte" or ".rft" ? lower : null;
    }

    private static (RvtFileKind FileKind, RvtDocumentKind DocKind, string FileType, bool IsTemplate) ClassifyDocument(
        bool hasPartAtom, bool hasProjectInformation, string? ext)
    {
        bool isFamilyMarker = hasPartAtom && !hasProjectInformation;
        bool isProjectMarker = hasProjectInformation && !hasPartAtom;

        if (ext == ".rft") return (RvtFileKind.Family, RvtDocumentKind.FamilyTemplate, "rft", true);
        if (ext == ".rte") return (RvtFileKind.Project, RvtDocumentKind.ProjectTemplate, "rte", true);
        if (ext == ".rfa") return (RvtFileKind.Family, RvtDocumentKind.Family, "rfa", false);
        if (ext == ".rvt") return (RvtFileKind.Project, RvtDocumentKind.Project, "rvt", false);

        if (isFamilyMarker || hasPartAtom)
            return (RvtFileKind.Family, RvtDocumentKind.Family, "rfa", false);
        if (isProjectMarker)
            return (RvtFileKind.Project, RvtDocumentKind.Project, "rvt", false);
        return (RvtFileKind.Unknown, RvtDocumentKind.Unknown, "unknown", false);
    }

    private static bool? ParseWorksharingLoose(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        if (trimmed.Equals("Not enabled", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.Equals("Local", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Central", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Created", StringComparison.OrdinalIgnoreCase))
            return true;
        return null;
    }

    private static bool? ParseCloudModelState(string? singleUserCloudValue, string? centralPath)
    {
        if (!string.IsNullOrWhiteSpace(centralPath)
            && (centralPath.StartsWith("Autodesk Docs://", StringComparison.OrdinalIgnoreCase)
                || centralPath.StartsWith("BIM 360://", StringComparison.OrdinalIgnoreCase)
                || centralPath.StartsWith("CLD://", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (string.IsNullOrWhiteSpace(singleUserCloudValue)) return null;
        string trimmed = singleUserCloudValue.Trim();
        if (trimmed.Equals("True", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("False", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static int? ParseOptionalInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static bool? ParseOptionalBoolBit(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" => true,
        "0" or "false" or "no" => false,
        _ => null,
    };
}
