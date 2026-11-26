using RvtIO.Model;

namespace RvtIO;

/// <summary>Specific Revit container classification across projects, families, and templates.</summary>
public enum RvtDocumentKind
{
    /// <summary>Neither project nor family markers nor a recognised Revit extension were found.</summary>
    Unknown = 0,
    /// <summary>Revit project (<c>.rvt</c>).</summary>
    Project = 1,
    /// <summary>Revit standalone family (<c>.rfa</c>).</summary>
    Family = 2,
    /// <summary>Revit project template (<c>.rte</c>).</summary>
    ProjectTemplate = 3,
    /// <summary>Revit family template (<c>.rft</c>).</summary>
    FamilyTemplate = 4,
}

/// <summary>Options controlling lightweight metadata extraction (<see cref="RvtFileReader.ReadMetadata"/>).</summary>
public sealed class RvtMetadataOptions
{
    /// <summary>Scrubs usernames, user-profile folders, UNC hosts, emails, and author identifiers across every field.</summary>
    public bool RedactPersonalData { get; set; }

    /// <summary>Replacement marker inserted by <see cref="RedactPersonalData"/>. Defaults to <c>&lt;redacted&gt;</c>.</summary>
    public string RedactionToken { get; set; } = "<redacted>";

    /// <summary>When true, includes the base64 preview image payload from <c>RevitPreview4.0</c>; default is false to keep inventory rows compact.</summary>
    public bool IncludePreviewImageBytes { get; set; }

    /// <summary>Optional path hint used when reading from a stream for extension, template, and backup detection.</summary>
    public string? SourcePath { get; set; }
}

/// <summary>
/// Lightweight container metadata summary read from the Revit compound file's header streams without reading
/// <c>Partitions/*</c> or decoding model geometry. Missing fields in the source file stay <c>null</c>.
/// </summary>
public sealed class RvtFileMetadataSummary
{
    /// <summary>Input file path when known (redacted when requested).</summary>
    public string? FilePath { get; init; }

    /// <summary>File name component when known (redacted when requested).</summary>
    public string? FileName { get; init; }

    /// <summary>Lowercase file extension including leading dot (<c>.rvt</c>, <c>.rfa</c>, <c>.rte</c>, <c>.rft</c>), or null when unknown.</summary>
    public string? Extension { get; init; }

    /// <summary>Input size in bytes when known.</summary>
    public long? FileSizeBytes { get; init; }

    /// <summary>Coarse container kind (<see cref="RvtFileKind.Project"/>, <see cref="RvtFileKind.Family"/>, or <see cref="RvtFileKind.Unknown"/>).</summary>
    public RvtFileKind FileKind { get; init; }

    /// <summary>Detailed document classification distinguishing templates (<see cref="RvtDocumentKind"/>).</summary>
    public RvtDocumentKind DocumentKind { get; init; }

    /// <summary>Short lowercase file type token (<c>rvt</c>, <c>rfa</c>, <c>rte</c>, <c>rft</c>, or <c>unknown</c>).</summary>
    public string FileType { get; init; } = "unknown";

    /// <summary>True when classified as a project or family template (<c>.rte</c> / <c>.rft</c>).</summary>
    public bool IsTemplate { get; init; }

    /// <summary>True when the file name carries a Revit incremental backup suffix (<c>.NNNN.rvt|rfa|rte|rft</c>).</summary>
    public bool IsBackup { get; init; }

    /// <summary>Numeric backup sequence parsed from the file name when <see cref="IsBackup"/> is true; otherwise null.</summary>
    public int? BackupNumber { get; init; }

    /// <summary>True when the container carries a <c>PartAtom</c> stream (matching <see cref="RevitFileInfo.IsStandaloneFamily"/>).</summary>
    public bool IsStandaloneFamily { get; init; }

    /// <summary>Revit release year that saved the file (from <c>BasicFileInfo</c>).</summary>
    public string? RevitVersion { get; init; }

    /// <summary>Revit build string from <c>BasicFileInfo</c>.</summary>
    public string? Build { get; init; }

    /// <summary>Full unparsed build line from <c>BasicFileInfo</c> when present.</summary>
    public string? FullBuild { get; init; }

    /// <summary>Client application name recorded in <c>BasicFileInfo</c>, when present.</summary>
    public string? ClientAppName { get; init; }

    /// <summary>Header stream format version integer from <c>BasicFileInfo</c>.</summary>
    public string? StreamVersion { get; init; }

    /// <summary>Raw <c>Worksharing</c> string from <c>BasicFileInfo</c> (<c>Not enabled</c>, <c>Local</c>, <c>Central</c>, etc.), or null when omitted.</summary>
    public string? Worksharing { get; init; }

    /// <summary>Normalized worksharing state matching <see cref="RevitFileInfo.SerializedIsWorkshared"/>; null when unknown.</summary>
    public bool? IsWorkshared { get; init; }

    /// <summary>True for cloud models or cloud central paths, false when explicitly local, or null when unknown.</summary>
    public bool? IsCloudModel { get; init; }

    /// <summary>Saved locale tag from <c>BasicFileInfo</c> (for example <c>RUS</c> or <c>ENU</c>).</summary>
    public string? SavedLocale { get; init; }

    /// <summary>Last save path recorded in <c>BasicFileInfo</c> (redacted when requested).</summary>
    public string? LastSavePath { get; init; }

    /// <summary>Central model path recorded in <c>BasicFileInfo</c> (redacted when requested).</summary>
    public string? CentralModelPath { get; init; }

    /// <summary>Worksharing username from <c>BasicFileInfo</c> (redacted when requested).</summary>
    public string? Username { get; init; }

    /// <summary>Document author from <c>BasicFileInfo</c> or <c>ProjectInfo</c> (redacted when requested).</summary>
    public string? Author { get; init; }

    /// <summary>UTC save timestamp extracted from Atom metadata in <c>ProjectInformation</c> or <c>PartAtom</c>, when present.</summary>
    public string? LastSavedUtc { get; init; }

    /// <summary>Unique document GUID recorded in <c>BasicFileInfo</c>, when present.</summary>
    public string? UniqueDocumentGuid { get; init; }

    /// <summary>Document save increment count recorded in <c>BasicFileInfo</c>, when present.</summary>
    public int? UniqueDocumentIncrements { get; init; }

    /// <summary>Central model identity GUID recorded in <c>BasicFileInfo</c>, when present.</summary>
    public string? CentralModelIdentity { get; init; }

    /// <summary>Model identity GUID recorded in <c>BasicFileInfo</c>, when present.</summary>
    public string? ModelIdentity { get; init; }

    /// <summary>Central model reload version number recorded in <c>BasicFileInfo</c>, when present.</summary>
    public int? CentralModelVersion { get; init; }

    /// <summary>Episode GUID corresponding to the last reload latest in <c>BasicFileInfo</c>, when present.</summary>
    public string? CentralModelEpisodeGuid { get; init; }

    /// <summary>Whether all local changes were saved to central (<c>All Local Changes Saved To Central</c>), when present.</summary>
    public bool? AllLocalChangesSavedToCentral { get; init; }

    /// <summary>Default workset opening mode recorded in <c>BasicFileInfo</c>, when present.</summary>
    public string? OpenWorksetDefault { get; init; }

    /// <summary>Whether <c>Project Spark File</c> is set in <c>BasicFileInfo</c>, when present.</summary>
    public bool? IsProjectSparkFile { get; init; }

    /// <summary>Structured <see cref="FileMetadata"/> aligned with full <see cref="RvtFileReader.Read"/>.</summary>
    public FileMetadata Metadata { get; init; } = new();

    /// <summary>Structured <see cref="ProjectInfo"/> read from <c>ProjectInformation</c> (or Atom title/version from <c>PartAtom</c>).</summary>
    public ProjectInfo ProjectInfo { get; init; } = new();

    /// <summary>Structured <see cref="TransmissionInfo"/> when a valid <c>TransmissionData</c> stream is present.</summary>
    public TransmissionInfo? TransmissionData { get; init; }

    /// <summary>True when <c>BasicFileInfo</c> was present in the container.</summary>
    public bool HasBasicFileInfo { get; init; }

    /// <summary>True when <c>ProjectInformation</c> was present in the container.</summary>
    public bool HasProjectInformation { get; init; }

    /// <summary>True when <c>PartAtom</c> was present in the container.</summary>
    public bool HasPartAtom { get; init; }

    /// <summary>True when <c>TransmissionData</c> was present in the container.</summary>
    public bool HasTransmissionData { get; init; }

    /// <summary>True when a preview image was detected in <c>RevitPreview4.0</c>.</summary>
    public bool HasPreviewImage { get; init; }

    /// <summary>Detected MIME type of the preview image (<c>image/png</c> or <c>image/jpeg</c>), or null when none.</summary>
    public string? PreviewImageMimeType { get; init; }

    /// <summary>Count of external file references in <see cref="TransmissionData"/> when present.</summary>
    public int? ExternalReferenceCount { get; init; }

    /// <summary>Transmission flag from <see cref="TransmissionData"/> when present.</summary>
    public bool? IsTransmitted { get; init; }

    /// <summary>Total number of streams in the container directory (counted without reading stream contents).</summary>
    public int StreamCount { get; init; }

    /// <summary>Number of partition streams under <c>Partitions/</c> (counted without opening or reading any partition stream).</summary>
    public int PartitionCount { get; init; }

    /// <summary>Key-value fields parsed from <c>BasicFileInfo</c> (values redacted when requested).</summary>
    public IReadOnlyDictionary<string, string> BasicFileInfoFields { get; init; } = new Dictionary<string, string>();

    /// <summary>Returns a copy of this summary with personal identifiers and sensitive path segments replaced by <paramref name="token"/>.</summary>
    public RvtFileMetadataSummary Redact(string token = "<redacted>") =>
        Parsing.RvtPiiRedactor.RedactSummary(this, string.IsNullOrWhiteSpace(token) ? "<redacted>" : token);
}
