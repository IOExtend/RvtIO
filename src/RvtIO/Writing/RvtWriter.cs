using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using ICSharpCode.SharpZipLib.Checksum;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Parsing;
using RvtIO.Writing;

namespace RvtIO;

/// <summary>Kind of Revit compound file container.</summary>
public enum RvtFileKind
{
    /// <summary>Neither family nor project markers were found and the file extension is not <c>.rvt</c> or <c>.rfa</c>.</summary>
    Unknown = 0,
    /// <summary>Revit project or project template (<c>.rvt</c>, <c>.rte</c>).</summary>
    Project = 1,
    /// <summary>Revit family or family template (<c>.rfa</c>, <c>.rft</c>).</summary>
    Family = 2,
}

/// <summary>Write operations classified by the <see cref="RvtWriter"/> capability matrix.</summary>
public enum RvtWriteOperation
{
    /// <summary>Lossless container round-trip preserving the directory tree, sector size, and every stream's stored bytes.</summary>
    SaveAs = 0,
    /// <summary>Rewriting the uncompressed payload of <c>Formats/Latest</c> or a <c>Global/*</c> single-member paged stream.</summary>
    PagedStreamRewrite = 1,
    /// <summary>Rewriting targeted records inside <c>Partitions/&lt;n&gt;</c> while preserving untouched blocks, channels, and payloads.</summary>
    PartitionRecordRewrite = 2,
}

/// <summary>Support status of one <see cref="RvtWriteOperation"/> on an inspected file.</summary>
/// <param name="Operation">The evaluated write operation.</param>
/// <param name="IsSupported">True when the operation is supported for this file.</param>
/// <param name="RefusalCode">Stable refusal code when <paramref name="IsSupported"/> is false; otherwise null.</param>
/// <param name="Reason">Human-readable refusal reason when <paramref name="IsSupported"/> is false; otherwise null.</param>
public sealed record RvtOperationSupport(
    RvtWriteOperation Operation,
    bool IsSupported,
    string? RefusalCode = null,
    string? Reason = null);

/// <summary>Write-capability inspection result for a Revit container across file kind, release, and operation.</summary>
public sealed class RvtWriteCapabilities
{
    /// <summary>Detected container kind (<see cref="RvtFileKind.Project"/> or <see cref="RvtFileKind.Family"/>).</summary>
    public required RvtFileKind FileKind { get; init; }

    /// <summary>Release year recorded in <c>BasicFileInfo</c>, or null when absent.</summary>
    public required int? SavedRelease { get; init; }

    /// <summary>Release year identified from the embedded <c>Formats/Latest</c> class schema, or null when unrecognised.</summary>
    public required int? SchemaRelease { get; init; }

    /// <summary>Effective layout release year (<c>2020</c> to <c>2027</c> when supported), or null when unknown.</summary>
    public required int? EffectiveRelease { get; init; }

    /// <summary>Byte width of stored element identifiers (<c>4</c> up to Revit 2023, <c>8</c> from Revit 2024), or null when unknown.</summary>
    public required int? ElementIdWidth { get; init; }

    /// <summary>True when <c>BasicFileInfo</c> reports worksharing enabled, false when explicitly disabled, or null when unavailable or unrecognised.</summary>
    public required bool? IsWorkshared { get; init; }

    /// <summary>True when <c>BasicFileInfo</c> marks a cloud model or cloud central path, false when explicitly non-cloud, or null when unavailable or unrecognised.</summary>
    public required bool? IsCloudModel { get; init; }

    /// <summary>Total number of streams in the compound file container.</summary>
    public required int StreamCount { get; init; }

    /// <summary>Number of numbered partition streams under <c>Partitions/</c>.</summary>
    public required int PartitionStreamCount { get; init; }

    /// <summary>Per-operation support decisions keyed by <see cref="RvtWriteOperation"/>.</summary>
    public required IReadOnlyDictionary<RvtWriteOperation, RvtOperationSupport> Operations { get; init; }

    /// <summary>Returns true when <paramref name="operation"/> is supported on this file.</summary>
    public bool Supports(RvtWriteOperation operation) =>
        Operations.TryGetValue(operation, out var support) && support.IsSupported;
}

/// <summary>Thrown when a write or save-as operation is refused because the source file state or edit request is unsupported.</summary>
public sealed class RvtWriteRefusedException : Exception
{
    /// <summary>Stable machine-readable refusal code.</summary>
    public string Kind { get; }

    /// <summary>Creates the exception with its stable <paramref name="kind"/> code and diagnostic message.</summary>
    public RvtWriteRefusedException(string kind, string message, Exception? inner = null) : base(message, inner) =>
        Kind = kind;
}

/// <summary>Replacement of the uncompressed payload of a single-member paged stream (<c>Formats/Latest</c> or <c>Global/&lt;Name&gt;</c>).</summary>
/// <param name="StreamPath">Container path of the target paged stream, using <c>/</c> between storage and stream name.</param>
/// <param name="UncompressedPayload">Replacement uncompressed bytes to compress and page-encode.</param>
public sealed record RvtPagedStreamEdit(string StreamPath, ReadOnlyMemory<byte> UncompressedPayload);

/// <summary>Replacement of one framed record body inside <c>Partitions/&lt;PartitionNumber&gt;</c>.</summary>
/// <param name="PartitionNumber">Non-negative number of the target <c>Partitions/&lt;n&gt;</c> stream.</param>
/// <param name="Channel">Target channel (<c>101</c>, <c>102</c>, or <c>103</c>).</param>
/// <param name="OwnerId">Owner element identifier of the record inside the block.</param>
/// <param name="Body">Replacement record body starting with its 2-byte class tag.</param>
/// <param name="Block">Content block index (<c>0</c> = project/family own elements; <c>&gt; 0</c> = loaded-family keyed block).</param>
public sealed record RvtPartitionRecordEdit(
    int PartitionNumber,
    int Channel,
    ulong OwnerId,
    ReadOnlyMemory<byte> Body,
    int Block = 0);

/// <summary>An RVT parameter assignment. A null value clears a supported shared parameter with HideWhenNoValue.</summary>
public sealed record RvtParameterEdit(long ElementId, long ParameterId, string? Value);

/// <summary>An RFA parameter assignment. A null value resets supported text to empty or material to By Category.</summary>
public sealed record RvtFamilyParameterEdit(string FamilyTypeName, long ParameterId, string? Value);

/// <summary>Set of stream and partition record edits to persist atomically into a new Revit container.</summary>
public sealed class RvtWriteRequest
{
    /// <summary>Edits targeting single-member paged streams (<c>Formats/Latest</c> or <c>Global/*</c>).</summary>
    public IReadOnlyList<RvtPagedStreamEdit> PagedStreamEdits { get; init; } = [];

    /// <summary>Edits targeting framed records inside <c>Partitions/&lt;n&gt;</c> streams.</summary>
    public IReadOnlyList<RvtPartitionRecordEdit> PartitionRecordEdits { get; init; } = [];
}

/// <summary>Summary of a completed <see cref="RvtWriter.SaveAs"/> or <see cref="RvtWriter.Rewrite"/> operation.</summary>
public sealed class RvtWriteResult
{
    /// <summary>Full path of the written output file.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Container kind of the written file.</summary>
    public required RvtFileKind FileKind { get; init; }

    /// <summary>Effective Revit release year of the written file.</summary>
    public required int EffectiveRelease { get; init; }

    /// <summary>Total number of streams verified in the written container.</summary>
    public required int StreamCount { get; init; }

    /// <summary>Number of container streams replaced during the write (<c>0</c> for an unchanged <see cref="RvtWriter.SaveAs"/>).</summary>
    public required int ReplacedStreamCount { get; init; }

    /// <summary>Number of partition records replaced across all <c>Partitions/&lt;n&gt;</c> streams.</summary>
    public required int RewrittenRecordCount { get; init; }
}

/// <summary>
/// Lossless save-as and targeted stream/record writer for local, non-workshared Revit projects (<c>.rvt</c>) and families (<c>.rfa</c>)
/// from releases 2020 through 2027.
///
/// Every write validates the source file against the capability matrix first, builds and self-verifies any rewritten paged or partition
/// streams in memory, writes to a temporary file next to the destination, reads the temporary file back to verify every stream's length
/// and SHA-256, and moves it into place without ever overwriting an existing file or mutating the source file.
/// </summary>
public static class RvtWriter
{
    /// <summary>Oldest Revit release supported by the writer capability matrix.</summary>
    public const int OldestSupportedRelease = 2020;

    /// <summary>Newest Revit release supported by the writer capability matrix.</summary>
    public const int NewestSupportedRelease = 2027;

    private static readonly IReadOnlyDictionary<string, byte[]> NoStreamReplacements = new Dictionary<string, byte[]>();

    /// <summary>Inspects <paramref name="sourcePath"/> and returns its write-capability matrix across <see cref="RvtWriteOperation"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/> is null.</exception>
    /// <exception cref="RvtInputException">The file is missing, empty, not a compound file, or truncated.</exception>
    /// <exception cref="RvtLimitException">A stream exceeds the maximum single-array byte length.</exception>
    public static RvtWriteCapabilities InspectCapabilities(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        return InspectContainer(sourcePath);
    }

    /// <summary>Writes a verified lossless copy of <paramref name="sourcePath"/> to <paramref name="outputPath"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/> or <paramref name="outputPath"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="outputPath"/> is empty or whitespace.</exception>
    /// <exception cref="IOException"><paramref name="outputPath"/> already exists, its directory is missing, or the source is locked for writing.</exception>
    /// <exception cref="RvtInputException">The source file is missing, empty, not a compound file, or truncated.</exception>
    /// <exception cref="RvtLimitException">A stream exceeds the maximum single-array byte length.</exception>
    /// <exception cref="RvtWriteRefusedException">The source file is outside the supported write-capability matrix.</exception>
    public static RvtWriteResult SaveAs(string sourcePath, string outputPath) =>
        Rewrite(sourcePath, outputPath, new RvtWriteRequest());

    /// <summary>Writes a new local RVT with one verified writable built-in parameter set.
    /// The source and any existing output are preserved. See the writing guide for supported parameters and releases.</summary>
    /// <exception cref="RvtWriteRefusedException">The parameter, element or source layout is outside the verified scope.</exception>
    public static RvtWriteResult SetParameter(string sourcePath, string outputPath, long elementId, BuiltInParameter parameter, string value)
        => SetParameter(sourcePath, outputPath, elementId, (int)parameter, value);

    /// <summary>Writes a new local RVT with one verified writable parameter replaced. A positive
    /// <paramref name="parameterId"/> identifies a parameter definition in the source project. Numeric values use invariant format.</summary>
    /// <exception cref="RvtWriteRefusedException">The parameter, element, value or source layout is outside the verified scope.</exception>
    public static RvtWriteResult SetParameter(string sourcePath, string outputPath, long elementId, int parameterId, string value)
        => SetParameter(sourcePath, outputPath, elementId, (long)parameterId, value);

    /// <inheritdoc cref="SetParameter(string,string,long,int,string)"/>
    public static RvtWriteResult SetParameter(string sourcePath, string outputPath, long elementId, long parameterId, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetParameters(sourcePath, outputPath, [new(elementId, parameterId, value)]);
    }

    /// <summary>Applies an ordered batch of RVT parameter edits. The output is published only if every edit succeeds.</summary>
    public static RvtWriteResult SetParameters(string sourcePath, string outputPath, IReadOnlyList<RvtParameterEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(edits);
        if (!string.Equals(Path.GetExtension(sourcePath), ".rvt", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(outputPath), ".rvt", StringComparison.OrdinalIgnoreCase))
            throw new RvtWriteRefusedException("unsupported-parameter-file", "Parameter edits require a source and output with the .rvt extension.");
        if (edits.Count == 0)
            throw new ArgumentException("At least one parameter edit is required.", nameof(edits));

        var batch = edits.Select(edit =>
        {
            ArgumentNullException.ThrowIfNull(edit);
            ParameterWriter.Validate(edit.ParameterId, edit.Value);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(edit.ElementId);
            return new ParameterWriter.Edit(edit.ElementId, edit.ParameterId, edit.Value);
        }).ToArray();
        return RewriteCore(sourcePath, outputPath, new RvtWriteRequest(), parameterEdits: batch);
    }

    /// <summary>Writes a new local RFA with a verified writable parameter set on the named family type.
    /// Use the source family's parameter definition ID or a supported built-in ID. Numeric values use invariant format.</summary>
    /// <exception cref="RvtWriteRefusedException">The family, type, parameter or value is outside the verified scope.</exception>
    public static RvtWriteResult SetFamilyParameter(string sourcePath, string outputPath, string familyTypeName, int parameterId, string value)
        => SetFamilyParameter(sourcePath, outputPath, familyTypeName, (long)parameterId, value);

    /// <inheritdoc cref="SetFamilyParameter(string,string,string,int,string)"/>
    public static RvtWriteResult SetFamilyParameter(string sourcePath, string outputPath, string familyTypeName, long parameterId, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetFamilyParameters(sourcePath, outputPath, [new(familyTypeName, parameterId, value)]);
    }

    /// <summary>Applies an ordered batch of RFA parameter edits. The output is published only if every edit succeeds.</summary>
    public static RvtWriteResult SetFamilyParameters(string sourcePath, string outputPath, IReadOnlyList<RvtFamilyParameterEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(edits);
        if (!Path.GetExtension(sourcePath).Equals(".rfa", StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(outputPath).Equals(".rfa", StringComparison.OrdinalIgnoreCase))
            throw new RvtWriteRefusedException("unsupported-parameter-file", "Family parameter edits require a source and output with the .rfa extension.");
        if (edits.Count == 0)
            throw new ArgumentException("At least one parameter edit is required.", nameof(edits));

        var batch = edits.Select(edit =>
        {
            ArgumentNullException.ThrowIfNull(edit);
            ArgumentException.ThrowIfNullOrWhiteSpace(edit.FamilyTypeName);
            ArgumentOutOfRangeException.ThrowIfZero(edit.ParameterId);
            StandaloneFamilyParameterWriter.Validate(edit.ParameterId, edit.Value);
            return new StandaloneFamilyParameterWriter.Edit(edit.FamilyTypeName, edit.ParameterId, edit.Value);
        }).ToArray();
        return RewriteCore(sourcePath, outputPath, new RvtWriteRequest(), familyEdits: batch);
    }

    /// <summary>Writes <paramref name="outputPath"/> from <paramref name="sourcePath"/> with the requested paged-stream and partition-record
    /// edits applied, preserving every other stream, block, channel, and record byte-for-byte.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/>, <paramref name="outputPath"/>, or <paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="outputPath"/> is empty or whitespace.</exception>
    /// <exception cref="IOException"><paramref name="outputPath"/> already exists, its directory is missing, or the source is locked for writing.</exception>
    /// <exception cref="RvtInputException">The source file is missing, empty, not a compound file, or truncated.</exception>
    /// <exception cref="RvtLimitException">A stream exceeds the maximum single-array byte length.</exception>
    /// <exception cref="RvtWriteRefusedException">The source file state or edit request is outside the supported scope.</exception>
    public static RvtWriteResult Rewrite(string sourcePath, string outputPath, RvtWriteRequest request)
        => RewriteCore(sourcePath, outputPath, request);

    private static RvtWriteResult RewriteCore(string sourcePath, string outputPath, RvtWriteRequest request,
        ParameterWriter.Edit[]? parameterEdits = null, StandaloneFamilyParameterWriter.Edit[]? familyEdits = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(request);
        if (request.PagedStreamEdits is null)
            throw new ArgumentException("PagedStreamEdits cannot be null.", nameof(request));
        if (request.PartitionRecordEdits is null)
            throw new ArgumentException("PartitionRecordEdits cannot be null.", nameof(request));

        string fullOutput = ValidateOutputPath(outputPath);
        RvtContainerCheck.Verify(sourcePath);
        string fullSource = Path.GetFullPath(sourcePath);

        Dictionary<string, byte[]>? replacements = null;
        RvtWriteCapabilities capabilities;
        using var sourceFile = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read);
        using (var root = RootStorage.Open(sourceFile, StorageModeFlags.LeaveOpen))
        {
            var inspection = InspectRoot(root, sourcePath, inspectImageLinks:
                !SameDirectory(Path.GetDirectoryName(fullSource)!, Path.GetDirectoryName(fullOutput)!));
            capabilities = inspection.Capabilities;

            RequireSupported(capabilities, RvtWriteOperation.SaveAs);
            bool editParameters = parameterEdits is not null || familyEdits is not null;
            if (editParameters)
            {
                RequireSupported(capabilities, RvtWriteOperation.PartitionRecordRewrite);
                var requiredKind = familyEdits is null ? RvtFileKind.Project : RvtFileKind.Family;
                if (capabilities.FileKind != requiredKind)
                    throw new RvtWriteRefusedException("unsupported-parameter-file", $"This edit requires a local Revit {requiredKind.ToString().ToLowerInvariant()}.");
            }
            if (request.PagedStreamEdits.Count > 0)
                RequireSupported(capabilities, RvtWriteOperation.PagedStreamRewrite);
            if (request.PartitionRecordEdits.Count > 0)
                RequireSupported(capabilities, RvtWriteOperation.PartitionRecordRewrite);

            ValidateExternalReferenceRelocation(root, inspection, fullSource, fullOutput);

            var pending = new ParameterWriter.Pending();
            if (editParameters)
            {
                var tags = inspection.FramingTags!.Value;
                int idWidth = capabilities.ElementIdWidth!.Value;
                foreach (var edit in parameterEdits ?? [])
                    pending.Apply(ParameterWriter.Plan(root, tags, idWidth, edit, pending));
                foreach (var edit in familyEdits ?? [])
                    pending.Apply(StandaloneFamilyParameterWriter.Plan(root, tags, idWidth, edit, pending));
                request = new RvtWriteRequest { PartitionRecordEdits = pending.Records.Values.ToArray() };
            }
            if (request.PagedStreamEdits.Count > 0 || request.PartitionRecordEdits.Count > 0)
                replacements = BuildReplacements(root, inspection, request);
            foreach (var (path, bytes) in pending.Metadata)
                (replacements ??= new(StringComparer.OrdinalIgnoreCase)).Add(inspection.CanonicalStreamPaths[path], bytes);
        }

        string outputKindHint = KindFromPath(fullOutput) is not RvtFileKind.Unknown ? fullOutput : sourcePath;
        Action<string>? verifyEditedOutput = replacements is null
            ? null
            : temporaryPath => VerifyOutputEligibility(temporaryPath, outputKindHint, capabilities, request);

        var containerResult = ContainerRewriter.Rewrite(
            fullSource,
            fullOutput,
            replacements ?? NoStreamReplacements,
            verifyEditedOutput);

        return new RvtWriteResult
        {
            OutputPath = fullOutput,
            FileKind = capabilities.FileKind,
            EffectiveRelease = capabilities.EffectiveRelease!.Value,
            StreamCount = containerResult.Entries.Values.Count(e => e.IsStream),
            ReplacedStreamCount = containerResult.Replaced,
            RewrittenRecordCount = request.PartitionRecordEdits.Count,
        };
    }

    private sealed record Inspection(
        RvtWriteCapabilities Capabilities,
        PartitionFraming.Tags? FramingTags,
        IReadOnlyDictionary<string, string> CanonicalStreamPaths,
        IReadOnlySet<ulong> PartitionRevitLinkIds,
        bool HasPartitionPointClouds,
        IReadOnlyList<(ulong Id, LinkedImageReference.Location? Location)> ImageLinks);

    private static RvtWriteCapabilities InspectContainer(string containerPath, string? pathHint = null)
    {
        RvtContainerCheck.Verify(containerPath);
        using var root = RootStorage.OpenRead(containerPath);
        return InspectRoot(root, pathHint ?? containerPath).Capabilities;
    }

    private static void VerifyOutputEligibility(
        string temporaryPath,
        string pathHint,
        RvtWriteCapabilities sourceCapabilities,
        RvtWriteRequest request)
    {
        var outputCapabilities = InspectContainer(temporaryPath, pathHint);
        RequireSupported(outputCapabilities, RvtWriteOperation.SaveAs);
        if (request.PagedStreamEdits.Count > 0 || sourceCapabilities.Supports(RvtWriteOperation.PagedStreamRewrite))
            RequireSupported(outputCapabilities, RvtWriteOperation.PagedStreamRewrite);
        if (request.PartitionRecordEdits.Count > 0 || sourceCapabilities.Supports(RvtWriteOperation.PartitionRecordRewrite))
            RequireSupported(outputCapabilities, RvtWriteOperation.PartitionRecordRewrite);
    }

    private static string ValidateOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output file path cannot be empty.", nameof(outputPath));

        string fullOutput;
        try { fullOutput = Path.GetFullPath(outputPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new IOException($"'{outputPath}' is not a usable output file path.", ex); }

        string? directory = Path.GetDirectoryName(fullOutput);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The output directory for '{fullOutput}' does not exist.");

        if (File.Exists(fullOutput) || Directory.Exists(fullOutput))
            throw new IOException($"'{Path.GetFileName(fullOutput)}' already exists; the output is never replaced.");

        return fullOutput;
    }

    private static void RequireSupported(RvtWriteCapabilities capabilities, RvtWriteOperation operation)
    {
        var support = capabilities.Operations[operation];
        if (!support.IsSupported)
            throw new RvtWriteRefusedException(support.RefusalCode!, support.Reason!);
    }

    private static Inspection InspectRoot(RootStorage root, string sourcePath, bool inspectImageLinks = false)
    {
        bool hasBasicFileInfo = false;
        Dictionary<string, string> bfi;
        if (root.TryOpenStream("BasicFileInfo", out var bfiStream))
        {
            hasBasicFileInfo = true;
            using (bfiStream)
            {
                byte[] bfiBytes = new byte[checked((int)bfiStream.Length)];
                bfiStream.ReadExactly(bfiBytes);
                bfi = BasicFileInfoReader.Parse(bfiBytes);
            }
        }
        else
        {
            bfi = new Dictionary<string, string>();
        }

        int? savedRelease = SchemaRelease.Saved(bfi.GetValueOrDefault("RevitVersion"));
        bool? isWorkshared = ParseWorksharingState(bfi.GetValueOrDefault("Worksharing"));
        bool? isCloudModel = ParseCloudModelState(
            bfi.GetValueOrDefault("IsSingleUserCloudModel"),
            bfi.GetValueOrDefault("Central Model Path"));

        var streamEntries = new List<(string Path, Storage Parent, string Name, long Length)>();
        CollectStreams(root, "", streamEntries);
        var canonicalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in streamEntries) canonicalPaths.TryAdd(entry.Path, entry.Path);

        RvtFileKind fileKind = DetectFileKind(canonicalPaths, bfi.GetValueOrDefault("LastSavePath"), sourcePath);

        var partitionEntries = streamEntries
            .Where(e => PartitionNumber(e.Path) is not null)
            .OrderBy(e => PartitionNumber(e.Path)!.Value)
            .ToList();

        (string Code, string Reason)? parityFailure = null;
        (string Code, string Reason)? globalDecodeFailure = null;
        foreach (var entry in streamEntries)
        {
            bool isFormats = entry.Path.StartsWith("Formats/", StringComparison.OrdinalIgnoreCase);
            bool isGlobal = entry.Path.StartsWith("Global/", StringComparison.OrdinalIgnoreCase);
            bool isPartition = PartitionNumber(entry.Path) is not null;
            if (!isFormats && !isGlobal && !isPartition) continue;

            byte[] raw = ReadStreamBytes(entry.Parent, entry.Name, entry.Length);
            var check = PageCodec.Verify(raw);
            if (check.Status is PageCheckStatus.BadPage or PageCheckStatus.BadTail)
            {
                parityFailure ??= ("corrupt-page-parity", $"Stream '{entry.Path}' fails page parity at page {check.Page} ({check.Status}).");
                continue;
            }

            if (isGlobal && globalDecodeFailure is null)
            {
                try { _ = PagedStreamCodec.ReadPrefix(raw); }
                catch (InvalidDataException ex)
                {
                    globalDecodeFailure = ("unreadable-paged-stream", $"Paged stream '{entry.Path}' cannot be decoded cleanly: {ex.Message}");
                }
            }
        }

        SchemaDocument? schemaDoc = TfSchemaReader.Read(root);
        FormatsSchema? formatsSchema = schemaDoc is null ? null : new FormatsSchema(schemaDoc);
        int? schemaRelease = formatsSchema is null ? null : SchemaRelease.FromSchema(formatsSchema);
        int? effectiveRelease = SchemaRelease.Effective(savedRelease, schemaRelease);
        int? idWidth = SchemaRelease.IdWidth(effectiveRelease);
        PartitionFraming.Tags? framingTags = formatsSchema is null ? null : PartitionFraming.TagsOf(formatsSchema);
        ushort? rvtLinkClassTag = FindClassTag(schemaDoc, "RvtLinkSymbol");
        ushort? pointCloudClassTag = FindClassTag(schemaDoc, "PointCloudType");
        var imageLinks = new List<(ulong Id, LinkedImageReference.Location? Location)>();
        Action<PartitionFraming.Record>? inspectImage = null;
        if (inspectImageLinks && schemaDoc is not null && FindClassTag(schemaDoc, "ImageSymbol") is ushort imageTag)
        {
            var walker = new WideBlockWalker(schemaDoc, (declaring, field) =>
                ImportedImageReference.CaptureKey(declaring, field) ?? LinkedImageReference.CaptureKey(declaring, field), []);
            inspectImage = record =>
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span) != imageTag)
                    return;
                byte[] body = record.Body.ToArray();
                var image = walker.Run(body, 0, body.Length, imageTag, rootOnly: true);
                if (image.RootRead && UserParameterWriter.Integer(image, "ImageSymbol.m_flags") == 0)
                    return;
                image = walker.Run(body, 0, body.Length, imageTag);
                imageLinks.Add((record.Owner, LinkedImageReference.ReadLocation(walker, image)));
            };
        }

        (string Code, string Reason)? baseFailure = EvaluateBaseEligibility(
            fileKind,
            hasBasicFileInfo,
            isCloudModel,
            isWorkshared,
            parityFailure,
            schemaDoc,
            savedRelease,
            schemaRelease);

        RvtOperationSupport saveAsSupport;
        RvtOperationSupport pagedSupport;
        RvtOperationSupport partitionSupport;
        IReadOnlySet<ulong> partitionRevitLinkIds = new HashSet<ulong>();
        bool hasPartitionPointClouds = false;

        if (baseFailure is { } refusal)
        {
            saveAsSupport = new RvtOperationSupport(RvtWriteOperation.SaveAs, false, refusal.Code, refusal.Reason);
            pagedSupport = new RvtOperationSupport(RvtWriteOperation.PagedStreamRewrite, false, refusal.Code, refusal.Reason);
            partitionSupport = new RvtOperationSupport(RvtWriteOperation.PartitionRecordRewrite, false, refusal.Code, refusal.Reason);
        }
        else
        {
            saveAsSupport = new RvtOperationSupport(RvtWriteOperation.SaveAs, true);
            pagedSupport = globalDecodeFailure is { } gFail
                ? new RvtOperationSupport(RvtWriteOperation.PagedStreamRewrite, false, gFail.Code, gFail.Reason)
                : new RvtOperationSupport(RvtWriteOperation.PagedStreamRewrite, true);
            partitionSupport = EvaluatePartitionEligibility(
                partitionEntries,
                framingTags,
                idWidth,
                rvtLinkClassTag,
                pointCloudClassTag,
                inspectImage,
                out partitionRevitLinkIds,
                out hasPartitionPointClouds);
        }

        var operations = new Dictionary<RvtWriteOperation, RvtOperationSupport>
        {
            [RvtWriteOperation.SaveAs] = saveAsSupport,
            [RvtWriteOperation.PagedStreamRewrite] = pagedSupport,
            [RvtWriteOperation.PartitionRecordRewrite] = partitionSupport,
        };

        var capabilities = new RvtWriteCapabilities
        {
            FileKind = fileKind,
            SavedRelease = savedRelease,
            SchemaRelease = schemaRelease,
            EffectiveRelease = effectiveRelease,
            ElementIdWidth = idWidth,
            IsWorkshared = isWorkshared,
            IsCloudModel = isCloudModel,
            StreamCount = streamEntries.Count,
            PartitionStreamCount = partitionEntries.Count,
            Operations = operations,
        };

        return new Inspection(
            capabilities,
            framingTags,
            canonicalPaths,
            partitionRevitLinkIds,
            hasPartitionPointClouds,
            imageLinks);
    }

    private static ushort? FindClassTag(SchemaDocument? schemaDoc, string className)
    {
        if (schemaDoc is null) return null;
        foreach (var cls in schemaDoc.Classes)
        {
            if (string.Equals(cls.Name, className, StringComparison.Ordinal)
                && cls.Id is >= 0 and <= ushort.MaxValue)
                return (ushort)cls.Id;
        }
        return null;
    }

    private static bool? ParseWorksharingState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        if (trimmed.Equals("Not enabled", StringComparison.OrdinalIgnoreCase))
            return false;
        if (trimmed.Equals("Local", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Central", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            return true;
        return null;
    }

    private static bool? ParseCloudModelState(string? singleUserCloudValue, string? centralPath)
    {
        if (IsCloudCentralPath(centralPath)) return true;
        if (string.IsNullOrWhiteSpace(singleUserCloudValue)) return null;
        string trimmed = singleUserCloudValue.Trim();
        if (trimmed.Equals("True", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("False", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static (string Code, string Reason)? EvaluateBaseEligibility(
        RvtFileKind fileKind,
        bool hasBasicFileInfo,
        bool? isCloudModel,
        bool? isWorkshared,
        (string Code, string Reason)? parityFailure,
        SchemaDocument? schemaDoc,
        int? savedRelease,
        int? schemaRelease)
    {
        if (fileKind == RvtFileKind.Unknown)
            return ("unknown-file-kind", "The file kind (.rvt project or .rfa family) could not be determined from container streams or file extension.");
        if (isCloudModel == true)
            return ("cloud-model", "Cloud-hosted Revit models are outside the supported local-file write scope.");
        if (isWorkshared == true)
            return ("workshared-model", "Workshared Revit models are outside the supported non-workshared local-file write scope.");
        if (!hasBasicFileInfo)
            return ("missing-metadata", "BasicFileInfo is missing from the container, so sharing, hosting, and saving-release state cannot be established.");
        if (isWorkshared is null)
            return ("missing-metadata", "BasicFileInfo lacks a recognised Worksharing field, so non-workshared state cannot be established.");
        if (isCloudModel is null)
            return ("missing-metadata", "BasicFileInfo lacks a recognised IsSingleUserCloudModel field, so local non-cloud hosting cannot be established.");
        if (savedRelease is null)
            return ("missing-metadata", "BasicFileInfo lacks a recognised saving release, so compatibility with Formats/Latest cannot be established.");
        if (parityFailure is not null)
            return parityFailure;
        if (schemaDoc is null)
            return ("missing-schema", "The embedded class schema (Formats/Latest) is missing or unreadable.");
        if (schemaRelease is null)
            return ("unrecognised-release", "The Revit release of Formats/Latest is not recognised.");
        if (savedRelease.Value != schemaRelease.Value)
            return ("release-mismatch", $"The file was saved by Revit {savedRelease.Value} while Formats/Latest carries the schema of Revit {schemaRelease.Value}.");
        if (schemaRelease.Value is < OldestSupportedRelease or > NewestSupportedRelease)
            return ("unsupported-release", $"Revit release {schemaRelease.Value} is outside the supported write range ({OldestSupportedRelease}-{NewestSupportedRelease}).");
        return null;
    }

    private static RvtOperationSupport EvaluatePartitionEligibility(
        List<(string Path, Storage Parent, string Name, long Length)> partitionEntries,
        PartitionFraming.Tags? framingTags,
        int? idWidth,
        ushort? rvtLinkClassTag,
        ushort? pointCloudClassTag,
        Action<PartitionFraming.Record>? inspectImage,
        out IReadOnlySet<ulong> partitionRevitLinkIds,
        out bool hasPartitionPointClouds)
    {
        var revitLinkIds = new HashSet<ulong>();
        partitionRevitLinkIds = revitLinkIds;
        hasPartitionPointClouds = false;

        if (framingTags is null)
            return new RvtOperationSupport(RvtWriteOperation.PartitionRecordRewrite, false, "missing-framing-markers", "Formats/Latest lacks the partition framing marker classes.");
        if (idWidth is not (4 or 8))
            return new RvtOperationSupport(RvtWriteOperation.PartitionRecordRewrite, false, "unknown-id-width", "The element identifier byte width is unknown.");
        if (partitionEntries.Count == 0)
            return new RvtOperationSupport(RvtWriteOperation.PartitionRecordRewrite, false, "missing-partitions", "The file contains no numbered partition streams under Partitions/.");

        var adler = new Adler32();
        ulong terminator = idWidth.Value == 8 ? ulong.MaxValue : uint.MaxValue;
        bool sawPointCloud = false;
        foreach (var entry in partitionEntries)
        {
            byte[] raw = ReadStreamBytes(entry.Parent, entry.Name, entry.Length);
            bool checksumMismatch = false;
            ulong badOwner = 0;
            int badChannel = 0;
            bool ok = PartitionFraming.Walk(raw, framingTags.Value, idWidth.Value, record =>
            {
                if (checksumMismatch) return;
                if (record.Owner == terminator && record.Body.Length == 0) return;
                if (record.Block == 0 && record.Channel == 102 && record.Body.Length >= 2)
                {
                    ushort classTag = BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span);
                    if (rvtLinkClassTag == classTag) revitLinkIds.Add(record.Owner);
                    if (pointCloudClassTag == classTag) sawPointCloud = true;
                    inspectImage?.Invoke(record);
                }
                if (record.Channel == 101) return;
                adler.Reset();
                adler.Update(record.Body.ToArray());
                if ((uint)adler.Value != record.Extra)
                {
                    checksumMismatch = true;
                    badOwner = record.Owner;
                    badChannel = record.Channel;
                }
            }, out string? reason, out _);

            hasPartitionPointClouds = sawPointCloud;

            if (!ok)
                return new RvtOperationSupport(
                    RvtWriteOperation.PartitionRecordRewrite,
                    false,
                    "partition-framing-failed",
                    $"Partition stream '{entry.Path}' fails framing verification ({reason}).");

            if (checksumMismatch)
                return new RvtOperationSupport(
                    RvtWriteOperation.PartitionRecordRewrite,
                    false,
                    "partition-checksum-failed",
                    $"Partition stream '{entry.Path}' has a mismatched Adler-32 checksum on channel {badChannel}, owner {badOwner}.");
        }

        return new RvtOperationSupport(RvtWriteOperation.PartitionRecordRewrite, true);
    }

    private sealed record TransmissionRefEntry(
        string? ElementId,
        string? LastSavedPath,
        string? LastSavedPathType,
        string? LastSavedLoadState,
        string? DesiredPath,
        string? DesiredPathType,
        string? DesiredLoadState);

    private static void ValidateExternalReferenceRelocation(
        RootStorage root,
        Inspection inspection,
        string fullSource,
        string fullOutput)
    {
        string sourceDir = Path.GetDirectoryName(fullSource) ?? "";
        string outputDir = Path.GetDirectoryName(fullOutput) ?? "";
        bool sameDirectory = SameDirectory(sourceDir, outputDir);

        if (inspection.HasPartitionPointClouds && !sameDirectory)
        {
            throw new RvtWriteRefusedException(
                "relative-reference-relocation",
                "Saving to a different directory cannot verify external file paths of PointCloudType records, which are not listed in TransmissionData.");
        }

        foreach (var (id, location) in inspection.ImageLinks)
        {
            if (location is null)
                throw new RvtWriteRefusedException("relative-reference-relocation", $"The external path of image {id} could not be verified.");
            CheckRelativeReferenceTarget(location.Path, location.PathType, null, id.ToString(CultureInfo.InvariantCulture),
                sourceDir, outputDir, sameDirectory);
        }

        if (!inspection.CanonicalStreamPaths.TryGetValue("TransmissionData", out string? tdPath))
        {
            if (inspection.PartitionRevitLinkIds.Count > 0 && !sameDirectory)
            {
                throw new RvtWriteRefusedException(
                    "missing-metadata",
                    "TransmissionData is missing while RvtLinkSymbol records exist in Partitions/, so external reference preservation across directories cannot be verified.");
            }
            return;
        }

        byte[] tdBytes = ReadStreamByPath(root, tdPath);
        if (!TryParseTransmissionReferences(tdBytes, out var references))
        {
            if (!sameDirectory)
            {
                throw new RvtWriteRefusedException(
                    "unreadable-transmission-data",
                    "TransmissionData cannot be parsed cleanly, so relative external reference targets cannot be verified when changing directories.");
            }
            return;
        }

        if (!sameDirectory)
        {
            // every RvtLinkSymbol needs its own reference entry with a saved path and path type: without them the target of the link
            // cannot be checked (Revit writes both for a link)
            var usableIds = new HashSet<ulong>();
            var unusableIds = new HashSet<ulong>();
            foreach (var entry in references)
            {
                if (!ulong.TryParse(entry.ElementId?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)) continue;
                bool usable = !string.IsNullOrWhiteSpace(entry.LastSavedPath) && !string.IsNullOrWhiteSpace(entry.LastSavedPathType);
                (usable ? usableIds : unusableIds).Add(id);
            }
            ulong[] unverified = inspection.PartitionRevitLinkIds
                .Where(id => !usableIds.Contains(id) || unusableIds.Contains(id)).Order().ToArray();
            if (unverified.Length > 0)
            {
                throw new RvtWriteRefusedException(
                    "missing-metadata",
                    $"TransmissionData lists no external reference with a saved path and path type for RvtLinkSymbol element(s) {string.Join(", ", unverified)} in Partitions/, so their link targets cannot be verified across directories.");
            }
        }

        foreach (var entry in references)
        {
            CheckRelativeReferenceTarget(
                entry.LastSavedPath,
                entry.LastSavedPathType,
                entry.LastSavedLoadState,
                entry.ElementId,
                sourceDir,
                outputDir,
                sameDirectory);

            if (entry.DesiredPath is not null || entry.DesiredPathType is not null)
            {
                CheckRelativeReferenceTarget(
                    entry.DesiredPath,
                    entry.DesiredPathType,
                    entry.DesiredLoadState,
                    entry.ElementId,
                    sourceDir,
                    outputDir,
                    sameDirectory);
            }
        }
    }

    private static void CheckRelativeReferenceTarget(
        string? rawPath,
        string? pathType,
        string? loadState,
        string? elementId,
        string sourceDir,
        string outputDir,
        bool sameDirectory)
    {
        if (string.Equals(loadState?.Trim(), "Imported", StringComparison.OrdinalIgnoreCase))
            return;

        string? trimmedType = pathType?.Trim();
        string elementSuffix = string.IsNullOrWhiteSpace(elementId) ? "" : $" (element {elementId.Trim()})";
        // Revit 2022 writes "Relative to Central Model" for a link whose Revit API path type is Relative
        bool isExplicitRelative = string.Equals(trimmedType, "Relative", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmedType, "Relative to Central Model", StringComparison.OrdinalIgnoreCase);
        bool isExplicitNonRelative = string.Equals(trimmedType, "Absolute", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmedType, "Server", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmedType, "Content", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmedType, "Library", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(trimmedType) && !isExplicitRelative && !isExplicitNonRelative)
        {
            if (sameDirectory) return;
            throw new RvtWriteRefusedException(
                "relative-reference-relocation",
                $"Saving to a different directory cannot verify external reference{elementSuffix} with the unrecognised path type '{trimmedType}'.");
        }

        bool isImplicitRelative = string.IsNullOrEmpty(trimmedType)
            && !string.IsNullOrWhiteSpace(rawPath)
            && !IsRootedOrUriLikePath(rawPath);

        if (!isExplicitRelative && !isImplicitRelative)
            return;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            if (!sameDirectory)
            {
                throw new RvtWriteRefusedException(
                    "relative-reference-relocation",
                    $"Saving to a different directory cannot preserve relative external reference{elementSuffix} because its relative path is empty.");
            }
            return;
        }

        string normalizedRel = rawPath.Trim()
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        string sourceTarget;
        string outputTarget;
        try
        {
            sourceTarget = Path.GetFullPath(Path.Combine(sourceDir, normalizedRel));
            outputTarget = Path.GetFullPath(Path.Combine(outputDir, normalizedRel));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            if (sameDirectory) return;
            throw new RvtWriteRefusedException(
                "relative-reference-relocation",
                $"Saving to a different directory cannot resolve relative external reference '{rawPath.Trim()}'{elementSuffix}.",
                ex);
        }

        if (!FileIdentity.SameFile(sourceTarget, outputTarget))
        {
            throw new RvtWriteRefusedException(
                "relative-reference-relocation",
                $"Saving to a different directory changes the resolved target of relative external reference '{rawPath.Trim()}'{elementSuffix}; relative external references cannot be rewritten losslessly.");
        }
    }

    private static bool TryParseTransmissionReferences(byte[] data, out List<TransmissionRefEntry> references)
    {
        references = [];
        if (data.Length < 4) return false;

        int charCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
        if (charCount <= 0 || 4L + (long)charCount * 2L > data.Length)
            return false;

        string xml = Encoding.Unicode.GetString(data, 4, charCount * 2);
        XmlDocument doc;
        try
        {
            doc = new XmlDocument { XmlResolver = null };
            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(
                stringReader,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc.Load(xmlReader);
        }
        catch (XmlException)
        {
            return false;
        }

        var rootElement = doc.DocumentElement;
        if (rootElement is null || !string.Equals(rootElement.Name, "TransmissionData", StringComparison.Ordinal))
            return false;

        foreach (XmlNode node in rootElement.ChildNodes)
        {
            if (node.NodeType != XmlNodeType.Element
                || !string.Equals(node.Name, "ExternalFileReference", StringComparison.Ordinal))
                continue;

            string? elementId = null;
            string? lastPath = null;
            string? lastPathType = null;
            string? lastLoadState = null;
            string? desiredPath = null;
            string? desiredPathType = null;
            string? desiredLoadState = null;

            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                switch (child.Name)
                {
                    case "ElementId": elementId = child.InnerText; break;
                    case "LastSavedPath": lastPath = child.InnerText; break;
                    case "LastSavedPathType": lastPathType = child.InnerText; break;
                    case "LastSavedLoadState": lastLoadState = child.InnerText; break;
                    case "DesiredPath": desiredPath = child.InnerText; break;
                    case "DesiredPathType": desiredPathType = child.InnerText; break;
                    case "DesiredLoadState": desiredLoadState = child.InnerText; break;
                }
            }

            references.Add(new TransmissionRefEntry(
                elementId,
                lastPath,
                lastPathType,
                lastLoadState,
                desiredPath,
                desiredPathType,
                desiredLoadState));
        }

        return true;
    }

    private static bool SameDirectory(string sourceDir, string outputDir)
    {
        if (string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(outputDir))
            return false;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string trimmedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDir));
        string trimmedOutput = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir));
        return string.Equals(trimmedSource, trimmedOutput, comparison)
            && FileIdentity.SameFile(trimmedSource, trimmedOutput);
    }

    private static bool IsRootedOrUriLikePath(string rawPath)
    {
        string trimmed = rawPath.Trim();
        if (Path.IsPathRooted(trimmed)) return true;
        if (trimmed.Length >= 3
            && char.IsAsciiLetter(trimmed[0])
            && trimmed[1] == ':'
            && (trimmed[2] == '\\' || trimmed[2] == '/'))
            return true;
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.Contains("://", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static Dictionary<string, byte[]> BuildReplacements(
        RootStorage root,
        Inspection inspection,
        RvtWriteRequest request)
    {
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var pagedEdit in request.PagedStreamEdits)
        {
            if (pagedEdit is null)
                throw new RvtWriteRefusedException("invalid-stream-target", "A paged stream edit entry is null.");
            string trimmed = pagedEdit.StreamPath?.Trim() ?? "";
            if (!IsSupportedPagedStreamTarget(trimmed))
                throw new RvtWriteRefusedException(
                    "invalid-stream-target",
                    $"'{pagedEdit.StreamPath}' is not a supported single-member paged stream; only Formats/Latest and Global/<Name> are allowed.");
            if (!inspection.CanonicalStreamPaths.TryGetValue(trimmed, out string? canonical))
                throw new RvtWriteRefusedException("invalid-stream-target", $"Stream '{trimmed}' does not exist in the source container.");
            if (replacements.ContainsKey(canonical))
                throw new RvtWriteRefusedException("duplicate-edit", $"Stream '{canonical}' is targeted more than once.");

            if (string.Equals(canonical, "Formats/Latest", StringComparison.OrdinalIgnoreCase))
                ValidateReplacementSchema(pagedEdit.UncompressedPayload.ToArray(), inspection);

            byte[] originalStored = ReadStreamByPath(root, canonical);
            replacements[canonical] = PagedStreamCodec.Reencode(originalStored, pagedEdit.UncompressedPayload.Span);
        }

        if (request.PartitionRecordEdits.Count > 0)
        {
            foreach (var recEdit in request.PartitionRecordEdits)
            {
                if (recEdit is null)
                    throw new RvtWriteRefusedException("invalid-record-target", "A partition record edit entry is null.");
                if (recEdit.PartitionNumber < 0)
                    throw new RvtWriteRefusedException("invalid-record-target", $"Partition number {recEdit.PartitionNumber} cannot be negative.");
            }

            foreach (var group in request.PartitionRecordEdits.GroupBy(e => e.PartitionNumber))
            {
                string partKey = "Partitions/" + group.Key.ToString(CultureInfo.InvariantCulture);
                if (!inspection.CanonicalStreamPaths.TryGetValue(partKey, out string? canonical))
                    throw new RvtWriteRefusedException("missing-partition", $"Partition stream '{partKey}' does not exist in the source container.");
                if (replacements.ContainsKey(canonical))
                    throw new RvtWriteRefusedException("duplicate-edit", $"Stream '{canonical}' is targeted more than once.");

                byte[] originalStored = ReadStreamByPath(root, canonical);
                var internalEdits = group
                    .Select(e => new PartitionRecordEdit(e.Block, e.Channel, e.OwnerId, e.Body))
                    .ToList();

                try
                {
                    replacements[canonical] = PartitionStreamEditor.Apply(
                        originalStored,
                        inspection.FramingTags!.Value,
                        inspection.Capabilities.ElementIdWidth!.Value,
                        internalEdits);
                }
                catch (ArgumentException ex)
                {
                    string code = ClassifyRecordEditArgumentError(ex.Message);
                    throw new RvtWriteRefusedException(code, $"In '{canonical}': {ex.Message}", ex);
                }
            }
        }

        return replacements;
    }

    private static void ValidateReplacementSchema(byte[] schemaBytes, Inspection inspection)
    {
        var parsed = TfSchemaReader.TryParse(schemaBytes);
        if (parsed is null)
            throw new RvtWriteRefusedException(
                "invalid-schema-payload",
                "Replacement payload for Formats/Latest does not parse as a valid class schema.");

        var replacementFormats = new FormatsSchema(parsed);
        int? replacementRelease = SchemaRelease.FromSchema(replacementFormats);
        if (replacementRelease is null)
            throw new RvtWriteRefusedException(
                "unrecognised-release",
                "Replacement Formats/Latest schema does not match a recognised Revit release.");

        if (inspection.Capabilities.SavedRelease is int saved && replacementRelease.Value != saved)
            throw new RvtWriteRefusedException(
                "release-mismatch",
                $"Replacement Formats/Latest carries the schema of Revit {replacementRelease.Value} while the container was saved by Revit {saved}.");

        if (replacementRelease.Value is < OldestSupportedRelease or > NewestSupportedRelease)
            throw new RvtWriteRefusedException(
                "unsupported-release",
                $"Replacement Formats/Latest release {replacementRelease.Value} is outside the supported write range ({OldestSupportedRelease}-{NewestSupportedRelease}).");

        int? replacementIdWidth = SchemaRelease.IdWidth(replacementRelease);
        if (replacementIdWidth != inspection.Capabilities.ElementIdWidth)
            throw new RvtWriteRefusedException(
                "incompatible-schema-payload",
                $"Replacement Formats/Latest changes element identifier byte width from {inspection.Capabilities.ElementIdWidth} to {replacementIdWidth}.");

        var replacementTags = PartitionFraming.TagsOf(replacementFormats);
        if (replacementTags is null && inspection.Capabilities.PartitionStreamCount > 0)
            throw new RvtWriteRefusedException(
                "missing-framing-markers",
                "Replacement Formats/Latest lacks the partition framing marker classes required by existing partition streams.");

        if (inspection.Capabilities.PartitionStreamCount > 0 && replacementTags != inspection.FramingTags)
            throw new RvtWriteRefusedException(
                "incompatible-schema-payload",
                "Replacement Formats/Latest changes partition framing marker class identifiers required by existing partition streams.");
    }

    private static string ClassifyRecordEditArgumentError(string message)
    {
        if (message.Contains("edited twice", StringComparison.Ordinal)) return "duplicate-edit";
        if (message.Contains("does not exist", StringComparison.Ordinal)) return "missing-record";
        return "invalid-record-target";
    }

    private static bool IsSupportedPagedStreamTarget(string path)
    {
        if (string.Equals(path, "Formats/Latest", StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.StartsWith("Global/", StringComparison.OrdinalIgnoreCase)) return false;
        ReadOnlySpan<char> rest = path.AsSpan("Global/".Length);
        return rest.Length > 0 && !rest.Contains('/');
    }

    private static RvtFileKind DetectFileKind(
        Dictionary<string, string> canonicalPaths,
        string? lastSavePath,
        string sourcePath)
    {
        if (canonicalPaths.ContainsKey("PartAtom")) return RvtFileKind.Family;
        if (canonicalPaths.ContainsKey("ProjectInformation")) return RvtFileKind.Project;
        return KindFromPath(lastSavePath) is not RvtFileKind.Unknown and var fromMeta
            ? fromMeta
            : KindFromPath(sourcePath);
    }

    private static RvtFileKind KindFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return RvtFileKind.Unknown;
        string ext = Path.GetExtension(path);
        if (ext.Equals(".rfa", StringComparison.OrdinalIgnoreCase) || ext.Equals(".rft", StringComparison.OrdinalIgnoreCase))
            return RvtFileKind.Family;
        if (ext.Equals(".rvt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".rte", StringComparison.OrdinalIgnoreCase))
            return RvtFileKind.Project;
        return RvtFileKind.Unknown;
    }

    private static bool IsCloudCentralPath(string? centralPath) =>
        !string.IsNullOrWhiteSpace(centralPath)
        && (centralPath.StartsWith("Autodesk Docs://", StringComparison.OrdinalIgnoreCase)
            || centralPath.StartsWith("BIM 360://", StringComparison.OrdinalIgnoreCase)
            || centralPath.StartsWith("CLD://", StringComparison.OrdinalIgnoreCase));

    private static void CollectStreams(
        Storage storage,
        string prefix,
        List<(string Path, Storage Parent, string Name, long Length)> entries)
    {
        foreach (var entry in storage.EnumerateEntries())
        {
            if (entry.Type == EntryType.Storage)
                CollectStreams(storage.OpenStorage(entry.Name), prefix + entry.Name + "/", entries);
            else if (entry.Type == EntryType.Stream)
                entries.Add((prefix + entry.Name, storage, entry.Name, entry.Length));
        }
    }

    private static int? PartitionNumber(string path) =>
        path.StartsWith("Partitions/", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(path.AsSpan("Partitions/".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number
            : null;

    private static byte[] ReadStreamBytes(Storage parent, string name, long length)
    {
        var bytes = new byte[checked((int)length)];
        using var stream = parent.OpenStream(name);
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] ReadStreamByPath(RootStorage root, string streamPath)
    {
        string[] parts = streamPath.Split('/');
        Storage current = root;
        for (int i = 0; i < parts.Length - 1; i++)
            current = current.OpenStorage(parts[i]);
        using var stream = current.OpenStream(parts[^1]);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
