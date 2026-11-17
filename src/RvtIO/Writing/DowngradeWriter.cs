using OpenMcdf;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>
/// Writes a downgraded copy of a Revit file: the class table (<c>Formats/Latest</c>)
/// lowered to the target profile and <c>BasicFileInfo</c> pointed at the target release.
/// Within one element-id width family the other streams are copied byte-for-byte, since
/// the framing, tags and record bodies stay consistent with the edited schema, whose
/// class ids and counts are unchanged. Across widths (8-byte ids to 4-byte, Revit 2024+
/// to 2020-2023) the schema is transplanted from the profile and every
/// <c>Partitions/&lt;n&gt;</c> stream is reframed and transcoded by class and field name
/// (tags renumbered, owners narrowed with range check, fields cut/inserted, checksums
/// recomputed); records of classes the target lacks keep an unknown tag.
/// </summary>
internal static class DowngradeWriter
{
    internal sealed record DowngradeWriteResult(
        string OutputPath,
        int SourceRelease,
        int TargetRelease,
        int EditedClasses,
        int ReplacedStreams);

    internal static DowngradeWriteResult Write(
        string sourcePath, string profilePath, string outputPath, GuidRule rule)
    {
        var planResult = DowngradePlanner.Plan(sourcePath, profilePath, rule);
        var plan = planResult.Plan;

        int? sourceWidth = SchemaRelease.IdWidth(planResult.SourceRelease);
        int? targetWidth = SchemaRelease.IdWidth(planResult.TargetRelease);
        if (sourceWidth is null || targetWidth is null)            throw new DowngradeRefusedException("unknown-id-width", "The element identifier width of the source or the target release is not known.");

        if (sourceWidth == targetWidth)
            return WriteSameWidth(sourcePath, profilePath, outputPath, planResult);

        if (sourceWidth != 8 || targetWidth != 4)
            throw new DowngradeRefusedException("cross-width-downgrade",
                $"The source (release {planResult.SourceRelease}, {sourceWidth}-byte ids) and the target (release {planResult.TargetRelease}, {targetWidth}-byte ids) use an unsupported width combination; only 8-byte to 4-byte downgrades are written.");

        return WriteCrossWidth(sourcePath, profilePath, outputPath, planResult);
    }

    private static DowngradeWriteResult WriteSameWidth(
        string sourcePath, string profilePath, string outputPath, DowngradePlanner.Result planResult)
    {
        var plan = planResult.Plan;

        byte[] sourceStored;
        byte[] sourceDecompressed;
        byte[] sourceBfi;
        byte[]? sourceContents;
        using (var root = RootStorage.OpenRead(sourcePath))
        {
            sourceStored = ReadRaw(root, "Formats", "Latest");
            sourceDecompressed = PagedGzipStream.Decompress(sourceStored)
                ?? throw new RvtInputException("truncated-or-damaged", $"The class table (Formats/Latest) of the source file '{Path.GetFileName(sourcePath)}' cannot be decompressed.");
            sourceBfi = ReadRawStream(root, "BasicFileInfo");
            sourceContents = TryReadOptionalStream(root, "Contents");
        }

        byte[] editedDecompressed;
        try { editedDecompressed = SchemaStreamEditor.Apply(sourceDecompressed, plan.Edits); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or FormatException)
        {
            throw new DowngradeRefusedException("edit-not-applicable", $"The planned edit does not apply to the class table of the source: {exception.Message}");
        }

        byte[] editedStored;
        try { editedStored = PagedStreamCodec.Reencode(sourceStored, editedDecompressed); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            throw new DowngradeRefusedException("edit-not-applicable", $"The edited class table cannot be re-encoded: {exception.Message}");
        }

        byte[] profileBfi;
        byte[]? profileContents;
        using (var profileRoot = RootStorage.OpenRead(profilePath))
        {
            profileBfi = ReadRawStream(profileRoot, "BasicFileInfo");
            profileContents = TryReadOptionalStream(profileRoot, "Contents");
        }

        byte[] patchedBfi;
        try { patchedBfi = BasicFileInfoPatcher.Patch(sourceBfi, planResult.SourceRelease, planResult.TargetRelease, profileBfi); }
        catch (InvalidDataException exception)
        {
            throw new DowngradeRefusedException("missing-metadata", exception.Message);
        }

        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Formats/Latest"] = editedStored,
            ["BasicFileInfo"] = patchedBfi,
        };
        if (ArchiveContents.ForTarget(sourceContents, profileContents) is { } contents && contents != sourceContents)
            replacements["Contents"] = contents;

        var rewrite = ContainerRewriter.Rewrite(sourcePath, outputPath, replacements);

        Verify(outputPath, profilePath, plan.Rule, plan.Edits.Count);

        return new DowngradeWriteResult(outputPath, planResult.SourceRelease, planResult.TargetRelease, plan.Edits.Count, rewrite.Replaced);
    }

    private static DowngradeWriteResult WriteCrossWidth(
        string sourcePath, string profilePath, string outputPath, DowngradePlanner.Result planResult)
    {
        var plan = planResult.Plan;
        const int sourceWidth = 8;
        const int targetWidth = 4;

        SchemaDocument sourceDoc, profileDoc;
        byte[] profileStored;
        byte[] sourceBfi;
        byte[]? sourceContents;
        List<(string Path, byte[] Stored)> sourcePartitions = [];
        List<(string Name, byte[] Stored)> sourceGlobalStreams = [];
        byte[]? sourceElemTableStored = null;
        byte[]? sourceHistoryStored = null;
        byte[] sourceBfiProfile;
        byte[]? profileContents;
        using (var sourceRoot = RootStorage.OpenRead(sourcePath))
        {
            byte[] sourceSchemaBytes = PagedGzipStream.Read(sourceRoot, "Formats", "Latest")
                ?? throw new RvtInputException("truncated-or-damaged", $"The class table (Formats/Latest) of the source file '{Path.GetFileName(sourcePath)}' is missing or cannot be read.");
            sourceDoc = TfSchemaReader.TryParse(sourceSchemaBytes)
                ?? throw new RvtInputException("truncated-or-damaged", $"The class table (Formats/Latest) of the source file '{Path.GetFileName(sourcePath)}' does not follow the expected layout.");
            sourceBfi = ReadRawStream(sourceRoot, "BasicFileInfo");
            sourceContents = TryReadOptionalStream(sourceRoot, "Contents");
            if (!sourceRoot.TryOpenStorage("Partitions", out var partStorage))
                throw new DowngradeRefusedException("missing-partitions", "The source file has no Partitions storage to transcode.");
            foreach (var entry in partStorage.EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream) continue;
                if (!int.TryParse(entry.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)) continue;
                using var s = partStorage.OpenStream(entry.Name);
                var raw = new byte[checked((int)s.Length)];
                s.ReadExactly(raw);
                sourcePartitions.Add(("Partitions/" + entry.Name, raw));
            }
            if (sourceRoot.TryOpenStorage("Global", out var globalStorage))
            {
                foreach (var entry in globalStorage.EnumerateEntries())
                {
                    if (entry.Type != EntryType.Stream) continue;
                    using var s = globalStorage.OpenStream(entry.Name);
                    var raw = new byte[checked((int)s.Length)];
                    s.ReadExactly(raw);
                    sourceGlobalStreams.Add((entry.Name, raw));
                    if (string.Equals(entry.Name, "ElemTable", StringComparison.OrdinalIgnoreCase)) sourceElemTableStored = raw;
                    if (string.Equals(entry.Name, "History", StringComparison.OrdinalIgnoreCase)) sourceHistoryStored = raw;
                }
            }
        }
        using (var profileRoot = RootStorage.OpenRead(profilePath))
        {
            byte[] profileSchemaBytes = PagedGzipStream.Read(profileRoot, "Formats", "Latest")
                ?? throw new RvtInputException("truncated-or-damaged", $"The class table (Formats/Latest) of the profile file '{Path.GetFileName(profilePath)}' is missing or cannot be read.");
            profileDoc = TfSchemaReader.TryParse(profileSchemaBytes)
                ?? throw new RvtInputException("truncated-or-damaged", $"The class table (Formats/Latest) of the profile file '{Path.GetFileName(profilePath)}' does not follow the expected layout.");
            var profileFormats = profileRoot.OpenStorage("Formats");
            using var ps = profileFormats.OpenStream("Latest");
            profileStored = new byte[checked((int)ps.Length)];
            ps.ReadExactly(profileStored);
            sourceBfiProfile = ReadRawStream(profileRoot, "BasicFileInfo");
            profileContents = TryReadOptionalStream(profileRoot, "Contents");
        }
        if (sourcePartitions.Count == 0)
            throw new DowngradeRefusedException("missing-partitions", "The source file has no numbered partition streams to transcode.");

        var sourceTags = PartitionFraming.TagsOf(new FormatsSchema(sourceDoc))
            ?? throw new DowngradeRefusedException("missing-framing-markers", "The source class table lacks the partition framing marker classes.");
        var targetTags = PartitionFraming.TagsOf(new FormatsSchema(profileDoc))
            ?? throw new DowngradeRefusedException("missing-framing-markers", "The profile class table lacks the partition framing marker classes.");

        // Extensible storage: the entity values are read by the schema each entity names; the
        // schemas live in the document's Latest.
        var latestStored = sourceGlobalStreams
            .FirstOrDefault(e => string.Equals(e.Name, "Latest", StringComparison.OrdinalIgnoreCase)).Stored;
        var entitySchemas = latestStored is null
            ? null
            : EntitySchemaReader.Read(PagedGzipStream.Decompress(latestStored) ?? [], sourceDoc);
        var transcoder = new R22Transcoder(sourceDoc, profileDoc, sourceWidth, entitySchemas);
        var tagFixer = TagCellRewriter.Create(sourceDoc, profileDoc, entitySchemas);

        // One pass over the stored streams gives the element ids the written file will hold and
        // the ones the rewrite leaves out; a reference inside that id range which is not held is
        // written as invalid instead of pointing at an element that is gone.
        var knownOwners = new HashSet<ulong>();
        var knownDropped = new HashSet<ulong>();
        foreach (var (_, raw) in sourcePartitions)
        {
            var state = DowngradePartitionRewriter.Inspect(raw, sourceTags, sourceWidth, transcoder);
            knownOwners.UnionWith(state.Owners);
            knownDropped.UnionWith(state.Dropped);
        }
        var presentElements = knownOwners
            .Where(id => id <= uint.MaxValue && !knownDropped.Contains(id))
            .Select(id => (uint)id)
            .ToHashSet();
        transcoder.SetElementUniverse(presentElements, presentElements.Count == 0 ? 0u : presentElements.Max());

        byte[] patchedBfi;
        try { patchedBfi = BasicFileInfoPatcher.Patch(sourceBfi, planResult.SourceRelease, planResult.TargetRelease, sourceBfiProfile); }
        catch (InvalidDataException exception)
        {
            throw new DowngradeRefusedException("missing-metadata", exception.Message);
        }

        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Formats/Latest"] = profileStored,
            ["BasicFileInfo"] = patchedBfi,
        };
        if (ArchiveContents.ForTarget(sourceContents, profileContents) is { } contents && contents != sourceContents)
            replacements["Contents"] = contents;
        // Single-object Global streams: transcode the class object by class and field name.
        // A stream that does not open with a class of the source schema holds no class object
        // (a synthetic file, or opaque data) and is copied unchanged.
        foreach (string name in new[] { "Latest", "PartitionTable", "DocumentIncrementTable" })
        {
            var entry = sourceGlobalStreams.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry.Stored is null) continue;
            byte[] decompressed = PagedGzipStream.Decompress(entry.Stored)
                ?? throw new RvtInputException("truncated-or-damaged", $"Global/{name} of the source file '{Path.GetFileName(sourcePath)}' cannot be decompressed.");
            byte[] rewritten;
            try { rewritten = DowngradeGlobalRewriter.Rewrite(name, decompressed, sourceDoc, transcoder); }
            catch (DowngradeRefusedException) { throw; }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
            {
                throw new DowngradeRefusedException("global-transcode-failed",
                    $"Global/{name} cannot be transcoded to release {planResult.TargetRelease}: {exception.Message}");
            }
            if (ReferenceEquals(rewritten, decompressed)) continue;
            replacements["Global/" + entry.Name] = PagedStreamCodec.Encode(PagedStreamCodec.ReadPrefix(entry.Stored), rewritten);
        }
        // Content documents: a block stream whose blocks open with the ContentMarker class tag.
        {
            var entry = sourceGlobalStreams.FirstOrDefault(e => string.Equals(e.Name, "ContentDocuments", StringComparison.OrdinalIgnoreCase));
            if (entry.Stored is not null)
            {
                byte[] decompressed = PagedGzipStream.Decompress(entry.Stored)
                    ?? throw new RvtInputException("truncated-or-damaged", $"Global/ContentDocuments of the source file '{Path.GetFileName(sourcePath)}' cannot be decompressed.");
                byte[] rewritten;
                try { rewritten = DowngradeContentDocumentsRewriter.Rewrite(decompressed, sourceTags, targetTags, transcoder); }
                catch (DowngradeRefusedException) { throw; }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
                {
                    throw new DowngradeRefusedException("contentdocument-transcode-failed",
                        $"Global/ContentDocuments cannot be transcoded to release {planResult.TargetRelease}: {exception.Message}");
                }
                replacements["Global/" + entry.Name] = PagedStreamCodec.Encode(PagedStreamCodec.ReadPrefix(entry.Stored), rewritten);
            }
        }
        var droppedOwners = knownDropped;
        foreach (var (path, raw) in sourcePartitions)
        {
            byte[] rewritten;
            try
            {
                rewritten = DowngradePartitionRewriter.Rewrite(raw, sourceTags, sourceWidth, targetTags, targetWidth, transcoder, knownDropped, tagFixer, out _);
            }
            catch (DowngradeRefusedException) { throw; }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
            {
                throw new DowngradeRefusedException("partition-transcode-failed",
                    $"Partition stream '{path}' cannot be transcoded to release {planResult.TargetRelease}: {exception.Message}");
            }
            replacements[path] = rewritten;
        }
        if (sourceElemTableStored is not null)
        {
            byte[] sourceElemTableDecompressed = PagedGzipStream.Decompress(sourceElemTableStored)
                ?? throw new RvtInputException("truncated-or-damaged", $"The element table (Global/ElemTable) of the source file '{Path.GetFileName(sourcePath)}' cannot be decompressed.");
            byte[] targetElemTableDecompressed;
            try
            {
                targetElemTableDecompressed = DowngradeElemTableRewriter.Rewrite(sourceElemTableDecompressed, sourceDoc, profileDoc, droppedOwners);
            }
            catch (DowngradeRefusedException) { throw; }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
            {
                throw new DowngradeRefusedException("elemtable-transcode-failed",
                    $"The element table cannot be transcoded to release {planResult.TargetRelease}: {exception.Message}");
            }
            byte[] prefix;
            try { prefix = PagedStreamCodec.ReadPrefix(sourceElemTableStored); }
            catch (InvalidDataException exception)
            {
                throw new DowngradeRefusedException("elemtable-transcode-failed",
                    $"The element table cannot be transcoded to release {planResult.TargetRelease}: {exception.Message}");
            }
            replacements["Global/ElemTable"] = PagedStreamCodec.Encode(prefix, targetElemTableDecompressed);
        }
        if (sourceHistoryStored is not null)
        {
            byte[] sourceHistoryDecompressed = PagedGzipStream.Decompress(sourceHistoryStored)
                ?? throw new RvtInputException("truncated-or-damaged", $"The episode table (Global/History) of the source file '{Path.GetFileName(sourcePath)}' cannot be decompressed.");
            byte[] targetHistoryDecompressed;
            try
            {
                targetHistoryDecompressed = DowngradeHistoryRewriter.Rewrite(sourceHistoryDecompressed, sourceDoc, profileDoc);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
            {
                throw new DowngradeRefusedException("history-transcode-failed",
                    $"The episode table cannot be transcoded to release {planResult.TargetRelease}: {exception.Message}");
            }
            byte[] historyPrefix;
            try { historyPrefix = PagedStreamCodec.ReadPrefix(sourceHistoryStored); }
            catch (InvalidDataException exception)
            {
                throw new DowngradeRefusedException("history-transcode-failed",
                    $"The episode table cannot be transcoded to release {planResult.TargetRelease}: {exception.Message}");
            }
            replacements["Global/History"] = PagedStreamCodec.Encode(historyPrefix, targetHistoryDecompressed);
        }

        var rewrite = ContainerRewriter.Rewrite(sourcePath, outputPath, replacements);

        Verify(outputPath, profilePath, plan.Rule, plan.Edits.Count);

        return new DowngradeWriteResult(outputPath, planResult.SourceRelease, planResult.TargetRelease, plan.Edits.Count, rewrite.Replaced);
    }

    private static void Verify(string outputPath, string profilePath, GuidRule rule, int appliedEdits)
    {
        // The downgraded file keeps the source class ids and counts (only versions and
        // upgrade-identifier lists are lowered), so it stays in the intentional
        // saved-vs-schema mismatch state that an older Revit is expected to tolerate
        // (see the manual 2026->2025 trial in the README). Re-planning via
        // DowngradePlanner.Plan would refuse that mismatch, so compare the schemas directly.
        byte[] outputBytes, profileBytes;
        using (var outRoot = RootStorage.OpenRead(outputPath))
        {
            outputBytes = PagedGzipStream.Read(outRoot, "Formats", "Latest")
                ?? throw new InvalidDataException("The written file has no readable class table.");
        }
        using (var profRoot = RootStorage.OpenRead(profilePath))
        {
            profileBytes = PagedGzipStream.Read(profRoot, "Formats", "Latest")
                ?? throw new InvalidDataException("The profile file has no readable class table.");
        }
        var outputSchema = TfSchemaReader.TryParse(outputBytes)
            ?? throw new InvalidDataException("The written class table does not parse.");
        var profileSchema = TfSchemaReader.TryParse(profileBytes)
            ?? throw new InvalidDataException("The profile class table does not parse.");
        var replan = DowngradePlanner.Compute(outputSchema, profileSchema, rule);
        if (replan.Edits.Count != 0)
            throw new InvalidDataException(
                $"The written file still needs {replan.Edits.Count} class-table edits against the profile (applied {appliedEdits}).");

        // When the written file carries an element table, it must decode by the schema
        // it claims: the profile schema after a cross-width transplant, or the written
        // schema itself after a same-width edit (versions and identifier lists only).
        // A carried episode table must open with the profile DocumentHistory class tag.
        using (var outRoot = RootStorage.OpenRead(outputPath))
        {
            if (!outRoot.TryOpenStorage("Global", out var global)) return;
            bool hasElemTable = false, hasHistory = false;
            foreach (var entry in global.EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream) continue;
                if (string.Equals(entry.Name, "ElemTable", StringComparison.OrdinalIgnoreCase)) hasElemTable = true;
                if (string.Equals(entry.Name, "History", StringComparison.OrdinalIgnoreCase)) hasHistory = true;
            }
            if (hasElemTable)
            {
                byte[] elemTable = PagedGzipStream.Read(outRoot, "Global", "ElemTable")
                    ?? throw new InvalidDataException("The written element table cannot be decompressed.");
                bool transplanted = SchemaRelease.FromSchema(new FormatsSchema(outputSchema))
                    == SchemaRelease.FromSchema(new FormatsSchema(profileSchema));
                var layoutSchema = transplanted ? new FormatsSchema(profileSchema) : new FormatsSchema(outputSchema);
                if (ElemTableLayout.TryParse(elemTable, layoutSchema) is null)
                    throw new InvalidDataException("The written element table does not decode by its class schema.");
            }
            if (hasHistory)
            {
                byte[] history = PagedGzipStream.Read(outRoot, "Global", "History")
                    ?? throw new InvalidDataException("The written episode table cannot be decompressed.");
                // The tag must be the DocumentHistory id of the schema the file carries:
                // the profile schema after a cross-width transplant, the edited source schema otherwise.
                int? historyTag = new FormatsSchema(outputSchema).IdOf("DocumentHistory");
                if (historyTag is not null && (history.Length < 2
                    || System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(history) != historyTag.Value))
                    throw new InvalidDataException("The written episode table does not open with the DocumentHistory class tag of its class schema.");
            }
        }
    }

    private static byte[] ReadRaw(RootStorage root, string storageName, string streamName)
    {
        var storage = root.OpenStorage(storageName);
        using var stream = storage.OpenStream(streamName);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] ReadRawStream(RootStorage root, string streamName)
    {
        using var stream = root.OpenStream(streamName);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[]? TryReadOptionalStream(RootStorage root, string streamName)
    {
        if (!root.TryOpenStream(streamName, out var stream)) return null;
        using (stream)
        {
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }
}
